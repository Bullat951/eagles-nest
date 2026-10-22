namespace Dcb.Client.Core;

public enum QueueKind { Arrival, Departure }

/// <summary>An aircraft against a base (an airfield or a carrier) at one picture: range, height
/// above the base, speed relative to it (a carrier moves) and whether it is closing.</summary>
public readonly record struct QueueObs(long Id, double RangeNm, double HeightM, double RelSpeedMs, bool Closing)
{
    /// <summary>Off the ground or deck (the reference point sits ~2 m up when parked).</summary>
    public bool Airborne => HeightM > 15;

    /// <summary>An aircraft at (x, z, altitude MSL) moving at <paramref name="spd"/> m/s on
    /// heading <paramref name="hdg"/> (degrees; grid ≈ true within the convergence, a few
    /// degrees) against a base at (bx, bz), elevation <paramref name="baseElevM"/> MSL, moving at
    /// (bvx, bvz) m/s.</summary>
    public static QueueObs Of(long id, double x, double z, double altM, double hdg, double spd,
        double bx, double bz, double baseElevM, double bvx = 0, double bvz = 0)
    {
        double dx = x - bx, dz = z - bz;
        var dist = Math.Sqrt(dx * dx + dz * dz);
        var h = hdg * Math.PI / 180;
        double vx = Math.Cos(h) * spd - bvx, vz = Math.Sin(h) * spd - bvz;
        var rel = Math.Sqrt(vx * vx + vz * vz);
        // Closing: most of the motion relative to the base points at it.
        var closingMs = dist < 1 ? 0 : -(vx * dx + vz * dz) / dist;
        return new QueueObs(id, dist / Groove.MetersPerNm, altM - baseElevM, rel, rel > 20 && closingMs > 0.5 * rel);
    }
}

/// <summary>One aircraft in a runway queue.</summary>
public sealed class QueueEntry(long unit, QueueKind kind)
{
    public long Unit { get; } = unit;
    public QueueKind Kind { get; set; } = kind;
}

/// <summary>The takeoff and landing order at one base, as the controller set it (the order of
/// the strips in a tower's runway bay). Departures and arrivals share the one order.</summary>
public sealed class RunwayQueue(string key)
{
    /// <summary>Within this range a landed arrival or a departure on the ground counts as at the base.</summary>
    public const double AtBaseNm = 3;
    /// <summary>Suggest arrivals that close on the base within this range.</summary>
    public const double ArrivalsNm = 15;
    /// <summary>Arrivals spaced at least this far apart leave room for a departure between them.</summary>
    public const double DepartureGapNm = 6;

    private readonly List<QueueEntry> _entries = [];

    public string Key { get; } = key;
    public IReadOnlyList<QueueEntry> Entries => _entries;

    /// <summary>1-based place, or 0 when the unit is not in this queue.</summary>
    public int Place(long unit) => _entries.FindIndex(e => e.Unit == unit) + 1;

    public bool Contains(long unit) => Place(unit) > 0;

    /// <summary>Adds at the end (a unit already here only changes kind).</summary>
    public void Add(long unit, QueueKind kind)
    {
        if (_entries.FirstOrDefault(e => e.Unit == unit) is { } e) e.Kind = kind;
        else _entries.Add(new QueueEntry(unit, kind));
    }

    public bool Remove(long unit) => _entries.RemoveAll(e => e.Unit == unit) > 0;

    /// <summary>Moves a unit by <paramref name="delta"/> places (negative = towards the front).</summary>
    public void Move(long unit, int delta)
    {
        var i = _entries.FindIndex(e => e.Unit == unit);
        if (i < 0) return;
        var j = Math.Clamp(i + delta, 0, _entries.Count - 1);
        var e = _entries[i];
        _entries.RemoveAt(i);
        _entries.Insert(j, e);
    }

    /// <summary>Priority: to the front.</summary>
    public void Priority(long unit) => Move(unit, -_entries.Count);

    /// <summary>Drops strips that are done: units gone from the picture, arrivals landed and
    /// slowed to taxi speed, departures airborne and clear of the base. True when any went.</summary>
    public bool Prune(IReadOnlyDictionary<long, QueueObs> obs) => _entries.RemoveAll(e =>
        !obs.TryGetValue(e.Unit, out var o)
        || (e.Kind == QueueKind.Arrival && !o.Airborne && o.RangeNm < AtBaseNm && o.RelSpeedMs < 15)
        || (e.Kind == QueueKind.Departure && o.Airborne && o.RangeNm > AtBaseNm)) > 0;

    /// <summary>The strip's state, as a tower would mark it.</summary>
    public static string StateOf(QueueKind kind, QueueObs o) => kind switch
    {
        QueueKind.Arrival when !o.Airborne => "LANDED",
        QueueKind.Arrival => o.RangeNm <= 5 ? "FINAL" : "INBOUND",
        _ when o.Airborne => "AIRBORNE",
        _ when o.RelSpeedMs > 25 => "ROLLING",
        _ when o.RelSpeedMs > 2 => "TAXI",
        _ => "HOLDING",
    };

    /// <summary>Own aircraft worth a strip that have none here: airborne and closing within
    /// <see cref="ArrivalsNm"/> → arrival; on the ground at the base and moving → departure.</summary>
    public IEnumerable<(QueueObs Obs, QueueKind Kind)> Candidates(IEnumerable<QueueObs> obs) =>
        obs.Where(o => !Contains(o.Id))
            .Select(o => o.Airborne && o.Closing && o.RangeNm <= ArrivalsNm ? (o, QueueKind.Arrival)
                : !o.Airborne && o.RangeNm < AtBaseNm && o.RelSpeedMs > 2 ? (o, QueueKind.Departure)
                : (o, (QueueKind?)null))
            .Where(c => c.Item2 is not null)
            .Select(c => (c.o, c.Item2!.Value))
            .OrderBy(c => c.o.RangeNm);

    /// <summary>A hint for the first departure in the queue: whether the next arrival ahead of
    /// it is far enough out to let it go first; null without a departure waiting.</summary>
    public string? DepartureGap(IReadOnlyDictionary<long, QueueObs> obs)
    {
        var dep = _entries.FirstOrDefault(e => e.Kind == QueueKind.Departure && obs.TryGetValue(e.Unit, out var o) && !o.Airborne);
        if (dep is null) return null;
        var nearest = _entries.Where(e => e.Kind == QueueKind.Arrival && obs.TryGetValue(e.Unit, out var o) && o.Airborne)
            .Select(e => obs[e.Unit].RangeNm).DefaultIfEmpty(double.MaxValue).Min();
        return nearest == double.MaxValue ? "No arrivals: departures can go"
            : nearest >= DepartureGapNm ? $"Gap: next arrival {nearest:0.0} NM out, a departure can go"
            : $"No gap: next arrival {nearest:0.0} NM out (need {DepartureGapNm:0} NM)";
    }
}

/// <summary>Every base's queue (key "af:&lt;airfield id&gt;" or "cv:&lt;carrier unit id&gt;").
/// A unit is in one queue at a time. Not thread-safe: use from the UI thread.</summary>
public sealed class RunwayQueues
{
    private readonly Dictionary<string, RunwayQueue> _queues = [];

    public static string AirfieldKey(int id) => $"af:{id}";
    public static string CarrierKey(long id) => $"cv:{id}";

    public event Action? Changed;

    public IEnumerable<RunwayQueue> All => _queues.Values;

    public RunwayQueue For(string key) => _queues.TryGetValue(key, out var q) ? q : _queues[key] = new RunwayQueue(key);

    /// <summary>Adds a unit at the end of a base's queue, taking it out of any other.</summary>
    public void Add(string key, long unit, QueueKind kind)
    {
        foreach (var q in _queues.Values.Where(q => q.Key != key)) q.Remove(unit);
        For(key).Add(unit, kind);
        Changed?.Invoke();
    }

    public void Remove(string key, long unit)
    {
        if (For(key).Remove(unit)) Changed?.Invoke();
    }

    public void Move(string key, long unit, int delta)
    {
        For(key).Move(unit, delta);
        Changed?.Invoke();
    }

    public void Priority(string key, long unit)
    {
        For(key).Priority(unit);
        Changed?.Invoke();
    }

    public void Toggle(string key, long unit)
    {
        if (For(key).Entries.FirstOrDefault(e => e.Unit == unit) is not { } e) return;
        e.Kind = e.Kind == QueueKind.Arrival ? QueueKind.Departure : QueueKind.Arrival;
        Changed?.Invoke();
    }

    public void Prune(string key, IReadOnlyDictionary<long, QueueObs> obs)
    {
        if (For(key).Prune(obs)) Changed?.Invoke();
    }

    public void Clear()
    {
        _queues.Clear();
        Changed?.Invoke();
    }
}
