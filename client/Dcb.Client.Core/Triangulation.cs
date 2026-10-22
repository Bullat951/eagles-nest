using System.Text.Json.Serialization;

namespace Dcb.Client.Core;

/// <summary>An RWR bearing a pilot called: from where the aircraft was, along its heading plus the
/// clock position (3 o'clock = 90° right of the nose), true degrees.</summary>
public sealed record BearingLine(int Id, long TrackId, string Label, double Lat, double Lon, double BearingTrue, int Clock, DateTime Time)
{
    /// <summary>True bearing of a clock position seen from a heading (12 = the nose).</summary>
    public static double BearingOf(double headingTrue, int clock) => Nav.Normalize(headingTrue + clock % 12 * 30);
}

/// <summary>Where crossing bearing lines put the emitter: position and the 1σ error ellipse
/// (semi-axes in metres, the major axis' true bearing) from an RWR accuracy of
/// <see cref="Triangulation.BearingSigmaDeg"/>.</summary>
public sealed record BearingFix(double Lat, double Lon, double MajorM, double MinorM, double MajorTrueDeg, int Lines);

public static class Triangulation
{
    /// <summary>Assumed RWR bearing error (1σ). RWRs are coarse: ±10–15° is typical.</summary>
    public const double BearingSigmaDeg = 10;
    /// <summary>A fix further than this from a line's origin is not believed (no RWR hears that far).</summary>
    public const double MaxRangeM = 150 * Nav.MetersPerNm;
    private const double MetersPerDegLat = 110_574;

    /// <summary>The weighted least-squares crossing of the lines (each weighted by its bearing
    /// error at the fix's range), or null when they are too few, too parallel, or cross behind
    /// an aircraft or beyond <see cref="MaxRangeM"/>.</summary>
    public static BearingFix? Solve(IReadOnlyList<BearingLine> lines)
    {
        if (lines.Count < 2) return null;
        // A local flat frame (east, north metres) around the first origin: fine over a few hundred km.
        double lat0 = lines[0].Lat, lon0 = lines[0].Lon, kx = MetersPerDegLat * Math.Cos(lat0 * Math.PI / 180);
        var p = lines.Select(l => ((l.Lon - lon0) * kx, (l.Lat - lat0) * MetersPerDegLat)).ToArray();
        var d = lines.Select(l => (Math.Sin(l.BearingTrue * Math.PI / 180), Math.Cos(l.BearingTrue * Math.PI / 180))).ToArray();
        var w = Enumerable.Repeat(1.0, lines.Count).ToArray();
        double x = 0, y = 0, a11 = 0, a12 = 0, a22 = 0;
        var tanSigma = Math.Tan(BearingSigmaDeg * Math.PI / 180);
        for (int iter = 0; iter < 4; iter++)
        {
            a11 = a12 = a22 = 0;
            double b1 = 0, b2 = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                // Distance of (x, y) across line i is n·(q − p) with n the line's normal.
                double nx = -d[i].Item2, ny = d[i].Item1, np = nx * p[i].Item1 + ny * p[i].Item2;
                a11 += w[i] * nx * nx; a12 += w[i] * nx * ny; a22 += w[i] * ny * ny;
                b1 += w[i] * nx * np; b2 += w[i] * ny * np;
            }
            var det = a11 * a22 - a12 * a12;
            // Too parallel: the normal matrix is nearly singular (lines within a few degrees).
            if (det <= 1e-6 * (a11 + a22) * (a11 + a22)) return null;
            x = (a22 * b1 - a12 * b2) / det;
            y = (a11 * b2 - a12 * b1) / det;
            for (int i = 0; i < lines.Count; i++)
            {
                var range = Math.Max(1000, Math.Sqrt((x - p[i].Item1) * (x - p[i].Item1) + (y - p[i].Item2) * (y - p[i].Item2)));
                var sigma = range * tanSigma;
                w[i] = 1 / (sigma * sigma);
            }
        }
        for (int i = 0; i < lines.Count; i++)
        {
            double dx = x - p[i].Item1, dy = y - p[i].Item2;
            if (dx * d[i].Item1 + dy * d[i].Item2 <= 0) return null;  // behind the aircraft
            if (dx * dx + dy * dy > MaxRangeM * MaxRangeM) return null;
        }
        // Covariance = inverse of the weighted normal matrix; its eigenvalues are the ellipse's axes².
        var detA = a11 * a22 - a12 * a12;
        double c11 = a22 / detA, c12 = -a12 / detA, c22 = a11 / detA;
        var mean = (c11 + c22) / 2;
        var root = Math.Sqrt(Math.Max(0, (c11 - c22) * (c11 - c22) / 4 + c12 * c12));
        double major = Math.Sqrt(mean + root), minor = Math.Sqrt(Math.Max(0, mean - root));
        // Major axis direction (east, north) → true bearing.
        var angle = 0.5 * Math.Atan2(2 * c12, c11 - c22);  // from east, counter-clockwise
        var majorTrue = Nav.Normalize(90 - angle * 180 / Math.PI) % 180;
        return new BearingFix(lat0 + y / MetersPerDegLat, lon0 + x / kx, major, minor, majorTrue, lines.Count);
    }
}

/// <summary>A surface-to-air threat type the controller can place, with its ring (approximate
/// maximum engagement range, for situational awareness).</summary>
public sealed record ThreatType(string Name, double RangeKm, string Group)
{
    public double RangeM => RangeKm * 1000;

    public static readonly IReadOnlyList<ThreatType> All =
    [
        new("SA-2", 40, "Long range"), new("SA-5", 240, "Long range"), new("SA-10", 75, "Long range"),
        new("SA-11", 35, "Medium range"), new("SA-6", 25, "Medium range"), new("SA-3", 18, "Medium range"),
        new("SA-15", 12, "Short range"), new("SA-8", 10, "Short range"), new("SA-19", 8, "Short range"),
        new("SA-13", 5, "Short range"), new("SA-9", 4, "Short range"), new("AAA", 3, "Short range"),
        new("Patriot", 100, "Western"), new("Hawk", 40, "Western"), new("NASAMS", 25, "Western"),
        new("HQ-7", 12, "Western"), new("Roland", 8, "Western"), new("Rapier", 7, "Western"),
        new("SAM", 20, "Unknown"),
    ];

    public static ThreatType? Find(string? name) => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The threat type a mark's text starts with, as players and controllers write it:
    /// "SA-6", "sa6", "SA 11 near Hama", "Patriot", "HQ7"; null when it starts with none.</summary>
    public static ThreatType? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.TrimStart();
        var sa = System.Text.RegularExpressions.Regex.Match(t, @"^SA[\s\-]?(\d{1,2})(?!\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (sa.Success) return Find("SA-" + sa.Groups[1].Value);
        var word = System.Text.RegularExpressions.Regex.Match(t, @"^[A-Za-z]+(?:[\s\-]?\d+)?").Value.Replace(" ", "").Replace("-", "");
        return All.FirstOrDefault(x => !x.Name.StartsWith("SA-") && string.Equals(x.Name.Replace("-", ""), word, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A threat the controller placed: local until sent, then also an F10 mark (MarkId).</summary>
public sealed class ThreatMarker(int id, ThreatType type, double lat, double lon, DateTime created)
{
    public int Id { get; } = id;
    public ThreatType Type { get; } = type;
    public double Lat { get; } = lat;
    public double Lon { get; } = lon;
    public DateTime Created { get; } = created;
    public long? MarkId { get; internal set; }
    /// <summary>When it was sent as an F10 mark (the marks feed may lag the answer a moment).</summary>
    public DateTime? SentAt { get; internal set; }
}

/// <summary>The "marks" message: the F10 map marks the controller's coalition can see.</summary>
public sealed record MarksMessage(
    [property: JsonPropertyName("coalition")] string Coalition,
    [property: JsonPropertyName("marks")] IReadOnlyList<F10Mark> Marks);

/// <summary>An F10 map mark: id, text, DCS metres and lat/lon, mission time, who placed it (a
/// player's name, or "mission data" for scripts), whether DCB made it and with which threat type.</summary>
public sealed record F10Mark(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("lat")] double Lat,
    [property: JsonPropertyName("lon")] double Lon,
    [property: JsonPropertyName("time")] double Time,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("dcb")] bool Dcb,
    [property: JsonPropertyName("threat")] string? Threat)
{
    public bool ByMission => Author is null or "mission data";

    /// <summary>The threat this mark stands for: DCB's recorded type, else the one its text
    /// starts with (a player's "SA-11" mark is a threat too).</summary>
    public ThreatType? ThreatType => (Dcb ? Core.ThreatType.Find(Threat) : null) ?? Core.ThreatType.Parse(Text);
}

/// <summary>A threat as the maps draw it: one placed here and not sent, or an F10 mark that names
/// a threat (sent from here — <see cref="Local"/> — by another controller, or by a player).
/// <see cref="Removable"/>: a DCB mark, which controllers may remove.</summary>
public sealed record ThreatView(ThreatType Type, double Lat, double Lon, long? MarkId, ThreatMarker? Local, bool Removable = false);

/// <summary>The controller's triangulation board, shared by every map: RWR bearing lines, placed
/// threats and the coalition's F10 marks. Not thread-safe: use from the UI thread.</summary>
public sealed class GciBoard
{
    private readonly List<BearingLine> _lines = [];
    private readonly List<ThreatMarker> _threats = [];
    private int _nextId = 1;

    public IReadOnlyList<BearingLine> Lines => _lines;
    public IReadOnlyList<ThreatMarker> Threats => _threats;
    public IReadOnlyList<F10Mark> Marks { get; private set; } = [];

    public event Action? Changed;

    public BearingLine AddLine(long trackId, string label, double lat, double lon, double headingTrue, int clock, DateTime now)
    {
        var line = new BearingLine(_nextId++, trackId, label, lat, lon, BearingLine.BearingOf(headingTrue, clock), clock, now);
        _lines.Add(line);
        Changed?.Invoke();
        return line;
    }

    public void ClearLines(long? trackId = null)
    {
        if (_lines.RemoveAll(l => trackId is null || l.TrackId == trackId) > 0) Changed?.Invoke();
    }

    /// <summary>One fix per aircraft that called at least two bearings.</summary>
    public IReadOnlyList<(long TrackId, BearingFix Fix)> Fixes() =>
        _lines.GroupBy(l => l.TrackId)
            .Select(g => (g.Key, Fix: Triangulation.Solve(g.ToList())))
            .Where(f => f.Fix is not null)
            .Select(f => (f.Key, f.Fix!))
            .ToList();

    public ThreatMarker AddThreat(ThreatType type, double lat, double lon, DateTime now)
    {
        var t = new ThreatMarker(_nextId++, type, lat, lon, now);
        _threats.Add(t);
        Changed?.Invoke();
        return t;
    }

    public void RemoveThreat(ThreatMarker threat)
    {
        if (_threats.Remove(threat)) Changed?.Invoke();
    }

    /// <summary>The threat was sent as F10 mark <paramref name="markId"/> (null: taken back).</summary>
    public void SetMarkId(ThreatMarker threat, long? markId, DateTime now)
    {
        threat.MarkId = markId;
        threat.SentAt = markId is null ? null : now;
        Changed?.Invoke();
    }

    /// <summary>How long a just-sent threat waits for its mark to show in the marks feed.</summary>
    public static readonly TimeSpan FeedGrace = TimeSpan.FromSeconds(15);

    /// <summary>The coalition's marks from the server. Once sent, a threat lives as its mark:
    /// when the mark is gone (a player deleted it in the game, or another controller removed it)
    /// the threat goes too.</summary>
    public void ApplyMarks(IReadOnlyList<F10Mark> marks, DateTime now)
    {
        Marks = marks;
        var ids = marks.Select(m => m.Id).ToHashSet();
        _threats.RemoveAll(t => t.MarkId is { } id && !ids.Contains(id) && now - t.SentAt > FeedGrace);
        Changed?.Invoke();
    }

    /// <summary>Disconnected: the marks go (the next connection resends them); threats sent from
    /// here come back as their marks.</summary>
    public void ClearMarks()
    {
        Marks = [];
        _threats.RemoveAll(t => t.MarkId is not null);
        Changed?.Invoke();
    }

    /// <summary>Unsent threats placed here, plus every F10 mark that names a threat — except
    /// mission marks that only label a zone (Foothold's "SA-5 (50)" zone is not a threat ring).</summary>
    public IReadOnlyList<ThreatView> AllThreats(Func<F10Mark, bool>? isZoneLabel = null)
    {
        var list = new List<ThreatView>();
        var shown = new HashSet<long>();
        foreach (var m in Marks)
        {
            if (m.ThreatType is not { } type || (m.ByMission && isZoneLabel?.Invoke(m) == true)) continue;
            shown.Add(m.Id);
            list.Add(new ThreatView(type, m.Lat, m.Lon, m.Id, _threats.FirstOrDefault(t => t.MarkId == m.Id), m.Dcb));
        }
        // Unsent, or sent and not in the feed yet.
        foreach (var t in _threats.Where(t => t.MarkId is not { } id || !shown.Contains(id)))
            list.Add(new ThreatView(t.Type, t.Lat, t.Lon, t.MarkId, t, t.MarkId is not null));
        return list;
    }

    /// <summary>Marks drawn as marks: not the ones naming a threat (drawn as threats), and not
    /// mission marks that only label a zone (<paramref name="isZoneLabel"/>).</summary>
    public IEnumerable<F10Mark> VisibleMarks(Func<F10Mark, bool> isZoneLabel) =>
        Marks.Where(m => !(m.ByMission && isZoneLabel(m)) && m.ThreatType is null);
}
