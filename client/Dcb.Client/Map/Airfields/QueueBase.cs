using System.Windows.Media;
using Dcb.Client.Core;

namespace Dcb.Client.Map.Airfields;

/// <summary>A base with a runway queue — an airfield (its runways' middle) or a carrier (the
/// ship, moving) — and the own aircraft measured against it.</summary>
public sealed record QueueBase(string Key, string Name, double X, double Z, double ElevM, double Vx, double Vz)
{
    public static readonly Brush ArrivalBrush = Frozen(Color.FromRgb(0xFF, 0x9A, 0x30));
    public static readonly Brush DepartureBrush = Frozen(Color.FromRgb(0x3A, 0xA8, 0xFF));

    public static Brush BrushOf(QueueKind kind) => kind == QueueKind.Arrival ? ArrivalBrush : DepartureBrush;

    public static QueueBase ForAirfield(Airfield f)
    {
        var (x, z) = f.Runways.Count > 0 ? (f.Runways.Average(r => r.X), f.Runways.Average(r => r.Z)) : (f.X, f.Z);
        var store = AppState.Store;
        var elev = store.Weather?.Airfields.FirstOrDefault(a => a.Id == f.Id)?.Elevation
            ?? (store.Terrain is { } g ? LineOfSight.HeightAt(g, x, z) : null) ?? 0;
        return new QueueBase(RunwayQueues.AirfieldKey(f.Id), f.Name.Replace('_', ' '), x, z, elev, 0, 0);
    }

    /// <summary>Null when the carrier is not in the picture.</summary>
    public static QueueBase? ForCarrier(long id)
    {
        if (CarrierState.Ship(id) is not { } ship || AppState.Store.Carriers.FirstOrDefault(c => c.Id == id) is not { } info) return null;
        // An aircraft's reference point sits ~2 m above the deck when parked.
        var deck = (CarrierGeometry.For(info.Type)?.DeckHeightM ?? 22) - 2;
        return new QueueBase(RunwayQueues.CarrierKey(id), CarrierGeometry.Label(info.Type), ship.X, ship.Z, ship.Y + deck, ship.Vx, ship.Vz);
    }

    /// <summary>The base of a queue key, or null when it is gone.</summary>
    public static QueueBase? ForKey(string key)
    {
        if (key.StartsWith("af:") && int.TryParse(key[3..], out var af))
            return AppState.Store.Airfields.FirstOrDefault(a => a.Id == af) is { } f ? ForAirfield(f) : null;
        if (key.StartsWith("cv:") && long.TryParse(key[3..], out var cv)) return ForCarrier(cv);
        return null;
    }

    /// <summary>Every own aircraft against this base (fog of war: only own units are sequenced).</summary>
    public Dictionary<long, QueueObs> Observe()
    {
        var obs = new Dictionary<long, QueueObs>();
        var store = AppState.Store;
        if (store.Theatre is not { } theatre) return obs;
        foreach (var t in store.Tracks)
        {
            var u = t.Unit;
            if (u.Detected || t.Lost || !Symbology.IsAir(Symbology.KindOf(u)) || u.Lat is not { } lat || u.Lon is not { } lon || u.Alt is not { } alt) continue;
            var (x, z) = theatre.FromLatLon(lat, lon);
            obs[u.Id] = QueueObs.Of(u.Id, x, z, alt, u.Hdg ?? 0, u.Spd ?? 0, X, Z, ElevM, Vx, Vz);
        }
        return obs;
    }

    /// <summary>"247°/12.3" — magnetic bearing from the base to the unit and range in NM.</summary>
    public string BearingRange(Unit u)
    {
        var store = AppState.Store;
        if (store.Theatre is not { } theatre || u.Lat is not { } lat || u.Lon is not { } lon) return "";
        var (blat, blon) = theatre.ToLatLon(X, Z);
        var (trueDeg, m) = Nav.BearingRange(blat, blon, lat, lon);
        var brg = theatre.MagVar is { } mv ? Nav.Magnetic(trueDeg, mv.At(blat, blon)) : trueDeg;
        return $"{Nav.Bearing3(brg)}°/{m / Groove.MetersPerNm:0.0}";
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
