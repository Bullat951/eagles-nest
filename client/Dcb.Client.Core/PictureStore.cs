namespace Dcb.Client.Core;

/// <summary>A unit as the controller currently knows it. A lost radar contact keeps its last
/// position until <see cref="PictureStore.LostTimeout"/> runs out.</summary>
public sealed class Track(Unit unit, DateTime lastSeen)
{
    public Unit Unit { get; internal set; } = unit;
    public DateTime LastSeen { get; internal set; } = lastSeen;
    public bool Lost { get; internal set; }

    /// <summary>An aircraft on the ground or a ship's deck (parked, taxiing, rolling); false for
    /// everything else and when the terrain is not known yet.</summary>
    public bool OnGround { get; internal set; }

    /// <summary>1 while tracked, falling linearly to 0 as a lost contact ages out.</summary>
    public double Freshness(DateTime now) =>
        Lost ? Math.Clamp(1 - (now - LastSeen) / PictureStore.LostTimeout, 0, 1) : 1;
}

/// <summary>A missile in flight and where it has been (one point per picture, newest last).</summary>
public sealed class MissileTrack
{
    public const int TrailLength = 12;
    private readonly List<(double Lat, double Lon)> _trail = [];

    public MissileTrack(Weapon weapon) => Add(weapon);

    public Weapon Weapon { get; private set; } = null!;
    public IReadOnlyList<(double Lat, double Lon)> Trail => _trail;

    internal void Add(Weapon weapon)
    {
        Weapon = weapon;
        _trail.Add((weapon.Lat, weapon.Lon));
        if (_trail.Count > TrailLength) _trail.RemoveAt(0);
    }
}

/// <summary>The controller's picture, shared by every panel. Not thread-safe: use from the UI thread.</summary>
public sealed class PictureStore
{
    public static readonly TimeSpan LostTimeout = TimeSpan.FromSeconds(30);

    private readonly Dictionary<long, Track> _tracks = [];

    public string? Coalition { get; private set; }
    public Picture? Latest { get; private set; }
    public Theatre? Theatre { get; private set; }
    public TerrainGrid? Terrain { get; private set; }

    /// <summary>Raised when the theatre or its terrain grid arrives or is cleared.</summary>
    public event Action? TheatreChanged;

    public void SetTheatre(Theatre theatre)
    {
        Theatre = theatre;
        if (Terrain is not null && Terrain.Id != theatre.Id) Terrain = null;
        TheatreChanged?.Invoke();
    }

    public void SetTerrain(TerrainGrid terrain)
    {
        Terrain = terrain;
        TheatreChanged?.Invoke();
    }

    /// <summary>Airdromes and helipads of the theatre with runways and parking.</summary>
    public IReadOnlyList<Airfield> Airfields { get; private set; } = [];
    public event Action? AirfieldsChanged;

    public void SetAirfields(IReadOnlyList<Airfield> airfields)
    {
        Airfields = airfields;
        AirfieldsChanged?.Invoke();
    }

    /// <summary>The server's latest weather (for ATIS), once a minute.</summary>
    public WeatherMessage? Weather { get; private set; }
    public event Action? WeatherChanged;

    /// <summary>Mission time of day now (seconds since local midnight): the weather's time,
    /// carried forward by the pictures' mission clock; null before the first weather.</summary>
    public double? AbsTimeNow => Weather is { } w && _weatherPictureTime is { } at && Latest is { } p ? w.Time + (p.Time - at) : Weather?.Time;
    private double? _weatherPictureTime;

    public void SetWeather(WeatherMessage? weather)
    {
        Weather = weather;
        _weatherPictureTime = Latest?.Time;
        WeatherChanged?.Invoke();
    }

    /// <summary>Foothold campaign state, when the server runs a Foothold mission.</summary>
    public FootholdState? Foothold { get; private set; }
    public event Action? FootholdChanged;

    public void SetFoothold(FootholdState? state)
    {
        Foothold = state;
        FootholdChanged?.Invoke();
    }
    public IReadOnlyCollection<Track> Tracks => _tracks.Values;

    /// <summary>The coalition's carriers in the latest picture.</summary>
    public IReadOnlyList<CarrierInfo> Carriers => Latest?.Carriers ?? [];

    /// <summary>The latest fast LSO frame (10 a second while aircraft are near own carriers).</summary>
    public LsoMessage? Lso { get; private set; }

    /// <summary>Recent LSO frames per aircraft (≈ the last 40 s) for trails and profiles.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<(double Time, LsoShip Ship, LsoAircraft Aircraft)>> LsoHistory => _lsoHistory;
    private readonly Dictionary<long, IReadOnlyList<(double, LsoShip, LsoAircraft)>> _lsoHistory = [];

    /// <summary>Recovery passes graded from the LSO feed.</summary>
    public PassTracker Passes { get; }

    /// <summary>Raised after each LSO frame.</summary>
    public event Action? LsoChanged;

    /// <summary>The latest RWR / ESM picture (every second).</summary>
    public RwrMessage? Rwr { get; private set; }

    /// <summary>Raised after each RWR picture.</summary>
    public event Action? RwrChanged;

    public void ApplyRwr(RwrMessage m)
    {
        Rwr = m;
        RwrChanged?.Invoke();
    }

    /// <summary>The coalition's NCTR requests and results (latest message).</summary>
    public NctrMessage? Nctr { get; private set; }
    public event Action? NctrChanged;

    public void ApplyNctr(NctrMessage m)
    {
        Nctr = m;
        NctrChanged?.Invoke();
    }

    /// <summary>The NCTR answers for a contact, newest request first, one per method.</summary>
    public IReadOnlyList<NctrResult> NctrFor(long unitId) =>
        (Nctr?.Requests ?? []).OrderByDescending(r => r.Id)
            .SelectMany(r => r.Results.Where(x => x.Unit == unitId && x.Status == "ok"))
            .GroupBy(x => x.Method).Select(g => g.First()).ToList();

    private Dictionary<long, string> _ids = [];

    /// <summary>The server's identification mode ("manual": contacts carry no type), from the ids channel.</summary>
    public string IdentificationMode { get; private set; } = "auto";

    /// <summary>The coalition's identities (contact id → label), shared by its controllers.</summary>
    public IReadOnlyDictionary<long, string> Identities => _ids;

    public void ApplyIds(IdsMessage m)
    {
        IdentificationMode = m.Mode;
        _ids = m.Ids.ToDictionary(i => i.Unit, i => i.Label);
        foreach (var t in _tracks.Values.Where(t => t.Unit.Detected))
        {
            var label = _ids.GetValueOrDefault(t.Unit.Id);
            if (t.Unit.Assigned != label) t.Unit = t.Unit with { Assigned = label };
        }
        Changed?.Invoke();
    }

    /// <summary>What one own unit's receiver hears in the latest RWR picture (empty when nothing).</summary>
    public IReadOnlyList<RwrEmitter> RwrFor(long unitId) =>
        Rwr?.Receivers.FirstOrDefault(r => r.Id == unitId)?.Emitters ?? [];

    public PictureStore()
    {
        Passes = new PassTracker
        {
            CarrierGeometryFor = id => Carriers.FirstOrDefault(c => c.Id == id) is { } c ? CarrierGeometry.For(c.Type) : null,
            // The wind at the deck (from, true) as air motion; grid ≈ true within the convergence (a few degrees).
            WindFor = id => Carriers.FirstOrDefault(c => c.Id == id) is { } c
                ? (Math.Cos((c.WindFrom + 180) * Math.PI / 180) * c.WindMs, Math.Sin((c.WindFrom + 180) * Math.PI / 180) * c.WindMs)
                : default,
        };
    }

    public void ApplyLso(LsoMessage m)
    {
        Lso = m;
        foreach (var a in m.Aircraft)
        {
            // Each aircraft's frames against its nearest carrier.
            var ship = m.Carriers.MinBy(c => (c.X - a.X) * (c.X - a.X) + (c.Z - a.Z) * (c.Z - a.Z));
            if (ship is null) continue;
            var list = _lsoHistory.TryGetValue(a.Id, out var old) ? old.ToList() : [];
            list.Add((m.Time, ship, a));
            list.RemoveAll(f => m.Time - f.Item1 > 40);
            _lsoHistory[a.Id] = list;
        }
        foreach (var id in _lsoHistory.Keys.Where(id => m.Aircraft.All(a => a.Id != id)).ToList()) _lsoHistory.Remove(id);
        Passes.Update(m, id => Find(id)?.Unit is { } u ? (Symbology.NameOf(u) ?? u.Callsign ?? $"#{id}", u.Type) : ($"#{id}", null));
        LsoChanged?.Invoke();
    }

    /// <summary>Missiles in flight in the latest picture, with their recent positions.</summary>
    public IReadOnlyCollection<MissileTrack> Missiles => _missiles.Values;
    private readonly Dictionary<long, MissileTrack> _missiles = [];
    public long? SelectedId { get; private set; }

    /// <summary>Raised after tracks change (new picture, expiry, clear).</summary>
    public event Action? Changed;
    public event Action? SelectionChanged;

    public void Apply(string coalition, Picture picture, DateTime now)
    {
        Coalition = coalition;
        Latest = picture;
        var seen = new HashSet<long>();
        foreach (var raw in picture.Units)
        {
            var u = raw.Detected && _ids.TryGetValue(raw.Id, out var label) ? raw with { Assigned = label } : raw;
            seen.Add(u.Id);
            if (_tracks.TryGetValue(u.Id, out var t))
            {
                t.Unit = u;
                t.LastSeen = now;
                t.Lost = false;
            }
            else
            {
                _tracks[u.Id] = new Track(u, now);
            }
        }
        foreach (var t in _tracks.Values.Where(t => !seen.Contains(t.Unit.Id)).ToList())
        {
            // An own unit missing from the picture no longer exists; a contact may just
            // have left radar coverage, so it lingers as lost.
            if (t.Unit.Detected) t.Lost = true;
            else _tracks.Remove(t.Unit.Id);
        }
        ExpireLost(now, raise: false);
        UpdateOnGround();
        ApplyMissiles(picture.Weapons ?? []);
        if (picture.Events is { Count: > 0 } events) Passes.Landings(events);
        Changed?.Invoke();
    }

    /// <summary>Within this distance of a ship and below <see cref="DeckMaxM"/> an aircraft counts as on its deck.</summary>
    public const double DeckRadiusM = 250, DeckMaxM = 40;

    /// <summary>Height above the ground under an aircraft at which it counts as landed: a parked
    /// aircraft's reference point sits a few metres up; a hovering helicopter is not landed.</summary>
    public static bool IsOnGround(bool helicopter, double altM, double? groundM, bool overDeck) =>
        (groundM is { } g && altM - g < (helicopter ? 5 : 15)) || (overDeck && altM < DeckMaxM);

    private void UpdateOnGround()
    {
        if (Theatre is not { } theatre) return;
        var ships = _tracks.Values.Where(t => Symbology.KindOf(t.Unit) == SymbolKind.Ship && t.Unit.Lat is not null && t.Unit.Lon is not null)
            .Select(t => theatre.FromLatLon(t.Unit.Lat!.Value, t.Unit.Lon!.Value)).ToList();
        foreach (var t in _tracks.Values)
        {
            var u = t.Unit;
            var kind = Symbology.KindOf(u);
            if (!Symbology.IsAir(kind) || u.Lat is not { } lat || u.Lon is not { } lon || u.Alt is not { } alt)
            {
                t.OnGround = false;
                continue;
            }
            var (x, z) = theatre.FromLatLon(lat, lon);
            var ground = Terrain is { } grid ? LineOfSight.HeightAt(grid, x, z) : null;
            var overDeck = alt < DeckMaxM && ships.Any(s => (s.X - x) * (s.X - x) + (s.Z - z) * (s.Z - z) < DeckRadiusM * DeckRadiusM);
            t.OnGround = IsOnGround(kind == SymbolKind.Helicopter, alt, ground, overDeck);
        }
    }

    private void ApplyMissiles(IReadOnlyList<Weapon> weapons)
    {
        var seen = new HashSet<long>();
        foreach (var w in weapons)
        {
            seen.Add(w.Id);
            if (_missiles.TryGetValue(w.Id, out var m)) m.Add(w);
            else _missiles[w.Id] = new MissileTrack(w);
        }
        foreach (var id in _missiles.Keys.Where(id => !seen.Contains(id)).ToList()) _missiles.Remove(id);
    }

    /// <summary>Drops lost contacts older than <see cref="LostTimeout"/>.</summary>
    public void Expire(DateTime now)
    {
        if (ExpireLost(now, raise: true)) return;
        if (_tracks.Values.Any(t => t.Lost)) Changed?.Invoke();  // fading still needs a redraw
    }

    public void Clear()
    {
        _tracks.Clear();
        _missiles.Clear();
        _lsoHistory.Clear();
        Lso = null;
        Latest = null;
        Coalition = null;
        Select(null);
        Changed?.Invoke();
        // Theatre and terrain stay: they rarely change and the next connection resends them.
    }

    public void Select(long? id)
    {
        if (SelectedId == id) return;
        SelectedId = id;
        SelectionChanged?.Invoke();
    }

    public Track? Find(long id) => _tracks.GetValueOrDefault(id);

    private bool ExpireLost(DateTime now, bool raise)
    {
        var expired = _tracks.Values.Where(t => t.Lost && now - t.LastSeen >= LostTimeout).Select(t => t.Unit.Id).ToList();
        foreach (var id in expired) _tracks.Remove(id);
        if (expired.Contains(SelectedId ?? -1)) Select(null);
        if (expired.Count > 0 && raise) Changed?.Invoke();
        return expired.Count > 0;
    }
}
