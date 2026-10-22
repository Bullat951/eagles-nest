namespace Dcb.Client.Core;

/// <summary>A carrier class's landing geometry in the ship's frame (metres from the model's
/// origin: forward, starboard; "deck height" = where an aircraft's reference point sits on the
/// deck, so heights read 0 at touchdown). Nimitz values are measured from an AI F/A-18C trap on
/// CVN-74 on the test server (2026-10-22: final track −9.3° to the ship, descent reaching the
/// deck 96 m aft of the origin, 1 m starboard, at 22.0 m); the others are scaled estimates.</summary>
public sealed record CarrierGeometry(
    string Class, double LengthM, double BeamM, bool AngledDeck, double DeckAngleDeg,
    double TouchdownForward, double TouchdownStarboard, double DeckHeightM, double GlideslopeDeg)
{
    private static readonly CarrierGeometry Nimitz = new("Nimitz", 333, 77, true, -9.0, -96, 1, 21.9, 3.5);
    private static readonly CarrierGeometry Forrestal = new("Forrestal", 325, 76, true, -8.0, -94, 1, 21.9, 3.5);
    private static readonly CarrierGeometry Kuznetsov = new("Kuznetsov", 305, 72, true, -7.0, -95, -10, 17, 4.0);
    private static readonly CarrierGeometry Tarawa = new("Tarawa", 250, 32, false, 0, -60, 0, 18, 0);
    private static readonly CarrierGeometry Invincible = new("Invincible", 210, 36, false, 0, -50, 0, 13, 0);

    /// <summary>The geometry of a DCS ship type, or null when it is no carrier DCB knows.</summary>
    public static CarrierGeometry? For(string type) => type switch
    {
        "CVN_70" or "CVN_71" or "CVN_72" or "CVN_73" or "CVN_75" or "Stennis" => Nimitz,
        "Forrestal" => Forrestal,
        "CV_1143_5" or "KUZNECOW" => Kuznetsov,
        "LHA_Tarawa" => Tarawa,
        "hms_invincible" => Invincible,
        _ => null,
    };

    /// <summary>Full LSO tools: an angled deck with arresting wires behind a glideslope.</summary>
    public bool Arrested => AngledDeck && GlideslopeDeg > 0;

    /// <summary>"CVN-74 Stennis" style label from the DCS type.</summary>
    public static string Label(string type) => type switch
    {
        "Stennis" => "CVN-74 Stennis",
        "CVN_70" => "CVN-70 Vinson",
        "CVN_71" => "CVN-71 Roosevelt",
        "CVN_72" => "CVN-72 Lincoln",
        "CVN_73" => "CVN-73 Washington",
        "CVN_75" => "CVN-75 Truman",
        "Forrestal" => "CV-59 Forrestal",
        "CV_1143_5" or "KUZNECOW" => "Kuznetsov",
        "LHA_Tarawa" => "LHA-1 Tarawa",
        "hms_invincible" => "HMS Invincible",
        _ => type,
    };
}

/// <summary>An aircraft against a carrier's landing area at one moment.</summary>
/// <param name="AsternM">Distance behind the touchdown point along the landing centreline.</param>
/// <param name="CrossM">Sideways from the centreline, right of it (as the pilot sees it) positive.</param>
/// <param name="HeightM">Height above the flight deck.</param>
/// <param name="GlideM">Height above (+) or below (−) the glideslope.</param>
/// <param name="ClosureMs">Speed towards the touchdown point relative to the ship.</param>
/// <param name="AoaDeg">Pitch minus flight-path angle (no wind): an estimate of angle of attack.</param>
public readonly record struct Groove(double AsternM, double CrossM, double HeightM, double GlideM, double ClosureMs, double AoaDeg, double RelSpeedMs)
{
    public const double FeetPerMeter = 3.28084, MetersPerNm = 1852, KnotsPerMs = 1.943844;

    public double AsternNm => AsternM / MetersPerNm;
    public double GlideFt => GlideM * FeetPerMeter;
    public double CrossFt => CrossM * FeetPerMeter;
    public double LineupDeg => Math.Atan2(CrossM, Math.Max(1, AsternM)) * 180 / Math.PI;

    /// <summary>The landing centreline's direction from grid north (ship heading + deck angle).</summary>
    public static double FinalBearingGrid(LsoShip ship, CarrierGeometry g) => Nav.Normalize(ship.Hdg + g.DeckAngleDeg);

    /// <summary>The touchdown point in DCS map metres (x, z) and the deck's height.</summary>
    public static (double X, double Z, double Y) Touchdown(LsoShip ship, CarrierGeometry g)
    {
        var h = ship.Hdg * Math.PI / 180;
        double fx = Math.Cos(h), fz = Math.Sin(h), sx = -Math.Sin(h), sz = Math.Cos(h);
        return (ship.X + fx * g.TouchdownForward + sx * g.TouchdownStarboard,
                ship.Z + fz * g.TouchdownForward + sz * g.TouchdownStarboard,
                ship.Y + g.DeckHeightM);
    }

    /// <summary>The aircraft against the landing area. <paramref name="wind"/> is the air's
    /// motion (m/s, grid x/z, where it goes) for the angle-of-attack estimate.</summary>
    public static Groove Of(LsoShip ship, CarrierGeometry g, LsoAircraft a, (double X, double Z) wind = default)
    {
        var (tx, tz, ty) = Touchdown(ship, g);
        var l = FinalBearingGrid(ship, g) * Math.PI / 180;
        double lx = Math.Cos(l), lz = Math.Sin(l), rx = -Math.Sin(l), rz = Math.Cos(l);
        double dx = a.X - tx, dz = a.Z - tz;
        var astern = -(dx * lx + dz * lz);
        var cross = dx * rx + dz * rz;
        var height = a.Y - ty;
        var glide = height - Math.Max(0, astern) * Math.Tan(g.GlideslopeDeg * Math.PI / 180);
        double rvx = a.Vx - ship.Vx, rvz = a.Vz - ship.Vz;
        var closure = rvx * lx + rvz * lz;
        // Flight path through the air, not over the ground: the aircraft's velocity less the wind.
        double airX = a.Vx - wind.X, airZ = a.Vz - wind.Z;
        var horizontal = Math.Sqrt(airX * airX + airZ * airZ);
        var aoa = a.Pitch - Math.Atan2(a.Vy, Math.Max(1, horizontal)) * 180 / Math.PI;
        var rel = Math.Sqrt(rvx * rvx + rvz * rvz + a.Vy * a.Vy);
        return new Groove(astern, cross, height, glide, closure, aoa, rel);
    }

    /// <summary>On-speed angle of attack for types DCB knows (degrees), else null.</summary>
    public static double? OnSpeedAoa(string? type) => type switch
    {
        "FA-18C_hornet" or "FA-18E" or "FA-18F" or "EA-18G" => 8.1,
        "F-14A-135-GR" or "F-14B" => 10.0,
        "T-45" => 8.1,
        _ => null,
    };
}

/// <summary>One recovery pass as an LSO grades it.</summary>
public sealed class Pass
{
    public long Unit { get; init; }
    public long Carrier { get; init; }
    public string Name { get; set; } = "";
    public string? Type { get; set; }
    public double StartTime { get; init; }
    public double? EndTime { get; set; }
    /// <summary>Samples at X (start, 3/4 NM), IM (1/2), IC (1/4) and AR (at the ramp).</summary>
    public Dictionary<string, Groove> Samples { get; } = [];
    public double MinHeightM { get; set; } = double.MaxValue;
    public string Result { get; set; } = "";       // TRAP, BOLTER, WAVE OFF ("" while flying)
    public string Grade { get; set; } = "";        // OK, (OK), ---, C, B, WO (editable)
    public string Comments { get; set; } = "";
    public bool Final => Result.Length > 0;
}

/// <summary>Follows aircraft through the groove from the fast feed and logs each pass with a
/// result and an automatic grade. Not thread-safe: use from one thread.</summary>
public sealed class PassTracker
{
    public static readonly (string Name, double Nm)[] Marks = [("X", 0.75), ("IM", 0.5), ("IC", 0.25), ("AR", 0.06)];
    private const double GrooveNm = 1.0, GrooveCrossM = 460, GrooveHeightM = 400;
    // ≈ 200 ft above the glideslope. The AI's overhead arrival (725 ft, gear up, 350 kt) is
    // 370 ft above it at 1 NM; its real pass after the break stayed within 72 ft (CVN-74, 2026-10-22).
    private const double StartAboveM = 60;
    private const double DecideAfterS = 4;  // wait this long after a pass ends for DCS's landing event

    private readonly Dictionary<long, Pass> _active = [];
    private readonly List<Pass> _log = [];
    private readonly HashSet<long> _events = [];
    private readonly List<CarrierEvent> _landings = [];

    /// <summary>Every pass, newest last (including those still flying).</summary>
    public IReadOnlyList<Pass> Log => _log;

    public event Action? Changed;

    /// <summary>A fast-feed update: passes start, take samples and end. <paramref name="names"/>
    /// gives callsign and type for a unit id.</summary>
    public void Update(LsoMessage m, Func<long, (string Name, string? Type)> names)
    {
        var changed = false;
        foreach (var ship in m.Carriers)
        {
            if (CarrierGeometryFor(ship.Id) is not { Arrested: true } g) continue;
            var wind = WindFor(ship.Id);
            foreach (var a in m.Aircraft)
            {
                var gr = Groove.Of(ship, g, a, wind);
                var inGroove = gr.AsternNm is > 0 and < GrooveNm && Math.Abs(gr.CrossM) < GrooveCrossM && gr.HeightM < GrooveHeightM && gr.ClosureMs > 15;
                if (!_active.TryGetValue(a.Id, out var pass))
                {
                    // A pass starts out in the groove (not over the deck), near the glideslope and
                    // with the gear down (when the model reports it): an overhead arrival or a
                    // Case I break passes above it and is no pass.
                    if (!inGroove || gr.AsternNm < 0.5 || gr.GlideM > StartAboveM || a.Gear is < 0.5) continue;
                    var (name, type) = names(a.Id);
                    pass = new Pass { Unit = a.Id, Carrier = ship.Id, Name = name, Type = type, StartTime = m.Time };
                    _active[a.Id] = pass;
                    _log.Add(pass);
                    changed = true;
                }
                if (pass.Carrier != ship.Id) continue;
                // Past the wires and flying, climbing away well above the path (a wave-off), or
                // gone out of the groove.
                var leaving = gr.AsternM < -80 || (gr.GlideM > 60 && a.Vy > 2) || (!inGroove && gr.AsternNm > 0.05);
                // Back in the groove before the pass was decided (an S-turn, a brief excursion): the
                // same pass. Not while still leaving: a wave-off stays inside the groove box at first.
                if (pass.EndTime is not null && !pass.Final && inGroove && gr.AsternM > 0 && !leaving)
                {
                    pass.EndTime = null;
                    changed = true;
                }
                pass.MinHeightM = Math.Min(pass.MinHeightM, gr.HeightM);
                foreach (var (mark, nm) in Marks)
                    if (!pass.Samples.ContainsKey(mark) && gr.AsternNm <= nm)
                    {
                        pass.Samples[mark] = gr;
                        changed = true;
                    }
                // At rest on the deck: trapped (whether or not the event has arrived).
                if (gr.HeightM < 4 && gr.RelSpeedMs < 8 && gr.AsternM < 30 && pass.EndTime is null)
                {
                    End(pass, m.Time, "TRAP");
                    changed = true;
                }
                // Leaving: decide shortly.
                else if (pass.EndTime is null && leaving)
                {
                    pass.EndTime = m.Time;
                    changed = true;
                }
            }
        }
        changed |= Decide(m.Time);
        if (changed) Changed?.Invoke();
    }

    /// <summary>DCS's landing events (from the pictures; each id once).</summary>
    public void Landings(IEnumerable<CarrierEvent> events)
    {
        var changed = false;
        foreach (var e in events)
        {
            if (!_events.Add(e.Id)) continue;
            _landings.Add(e);
            // Only the unit's latest pass on that ship, begun before the landing.
            var pass = _log.LastOrDefault(p => p.Unit == e.Unit && p.Carrier == e.Carrier);
            if (pass is not null && pass.Result != "TRAP" && e.Time >= pass.StartTime - 5)
            {
                End(pass, pass.EndTime ?? e.Time, "TRAP");
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Lets the controller set a pass's grade.</summary>
    public void SetGrade(Pass pass, string grade)
    {
        pass.Grade = grade;
        Changed?.Invoke();
    }

    private bool Decide(double now)
    {
        var changed = false;
        foreach (var pass in _active.Values.ToList())
        {
            if (pass.EndTime is not { } end || now - end < DecideAfterS) continue;
            // No landing: over the deck low → bolter; otherwise a wave-off (or a climb-out).
            End(pass, end, pass.MinHeightM < 6 ? "BOLTER" : "WAVE OFF");
            changed = true;
        }
        return changed;
    }

    private void End(Pass pass, double time, string result)
    {
        pass.EndTime = time;
        pass.Result = result;
        (pass.Grade, pass.Comments) = GradeOf(pass);
        _active.Remove(pass.Unit);
    }

    /// <summary>Navy-style grade from the samples: deviations in close and at the ramp decide it.
    /// OK = all within limits, (OK) fair = small deviations, --- no grade = large ones, C = cut
    /// (low or far off line at the ramp). Bolter B, wave-off WO. Comments: H/LO, LUL/LUR, F/SLO
    /// with the mark (X, IM, IC, AR); "(…)" a little, plain a lot. F/SLO (fast/slow) come from the
    /// estimated AoA and are noted only, not graded.</summary>
    public static (string Grade, string Comments) GradeOf(Pass pass)
    {
        var comments = new List<string>();
        var worst = 0;  // 0 ok, 1 little, 2 a lot, 3 dangerous
        var onSpeed = Groove.OnSpeedAoa(pass.Type);
        foreach (var (mark, _) in Marks)
        {
            if (!pass.Samples.TryGetValue(mark, out var s)) continue;
            var close = mark is "IC" or "AR";
            void Note(string code, double size, double little, double alot)
            {
                var abs = Math.Abs(size);
                if (abs <= little) return;
                var much = abs > alot;
                comments.Add(much ? $"{code}{mark}" : $"({code}){mark}");
                if (close) worst = Math.Max(worst, much ? 2 : 1);
            }
            Note(s.GlideFt > 0 ? "H" : "LO", s.GlideFt, 25, 60);
            Note(s.CrossFt < 0 ? "LUL" : "LUR", s.CrossFt, 12, 30);
            // AoA is estimated (pitch less flight-path angle through the air): noted, never graded.
            if (onSpeed is { } aoa && Math.Abs(s.AoaDeg - aoa) > 1.0)
                comments.Add(Math.Abs(s.AoaDeg - aoa) > 2.0 ? $"{(s.AoaDeg < aoa ? "F" : "SLO")}{mark}" : $"({(s.AoaDeg < aoa ? "F" : "SLO")}){mark}");
            if (mark == "AR" && (s.GlideFt < -60 || Math.Abs(s.CrossFt) > 40)) worst = 3;
        }
        var grade = pass.Result switch
        {
            "BOLTER" => "B",
            "WAVE OFF" => "WO",
            _ => worst switch { 0 => "OK", 1 => "(OK)", 2 => "---", _ => "C" },
        };
        return (grade, string.Join(" ", comments));
    }

    /// <summary>The geometry of a carrier unit (set from the pictures' carrier list).</summary>
    public Func<long, CarrierGeometry?> CarrierGeometryFor { get; set; } = _ => null;

    /// <summary>The wind at a carrier (air motion, m/s, grid x/z).</summary>
    public Func<long, (double X, double Z)> WindFor { get; set; } = _ => default;
}

/// <summary>The marshal stack for a carrier: recovery case from the weather and the inbound
/// aircraft's slots — Case I overhead at angels 2, 3, 4…; Case II/III on the marshal radial
/// (final bearing + 180°) at angels 6, 7, 8… with DME = angels + 15 and push times a minute apart.</summary>
public static class Marshal
{
    public sealed record Slot(int Number, Unit Unit, int Angels, int? Dme, double? PushTime, double RangeNm);

    /// <summary>Case I: ceiling ≥ 3000 ft and visibility ≥ 5 NM; Case II: ceiling ≥ 1000 ft and
    /// visibility ≥ 5 NM; otherwise Case III. Clear skies count as no ceiling. At night (the sun
    /// below the horizon at the ship) always Case III, as DCS's Supercarrier does.</summary>
    public static int CaseFor(WeatherMessage? wx, double? sunElevationDeg = null)
    {
        if (sunElevationDeg is < 0) return 3;
        if (wx is null) return 1;
        var visNm = wx.Visibility / Groove.MetersPerNm;
        if (wx.Fog is { On: true } fog) visNm = Math.Min(visNm, fog.Visibility / Groove.MetersPerNm);
        var overcast = wx.Clouds is { } c && (c.Preset is not null || c.Density >= 5);
        var ceilingFt = overcast ? wx.Clouds!.Base * Groove.FeetPerMeter : double.MaxValue;
        if (ceilingFt >= 3000 && visNm >= 5) return 1;
        if (ceilingFt >= 1000 && visNm >= 5) return 2;
        return 3;
    }

    /// <summary>Carrier-capable own aircraft within 60 NM, airborne, nearest first, in slots.</summary>
    public static IReadOnlyList<Slot> Stack(int recoveryCase, IEnumerable<(Unit Unit, double RangeNm)> inbound, double missionNow)
    {
        var slots = new List<Slot>();
        var n = 0;
        var firstPush = Math.Ceiling((missionNow + 5 * 60) / 60) * 60;  // the next whole minute five minutes on
        foreach (var (u, range) in inbound.Where(i => i.RangeNm <= 60).OrderBy(i => i.RangeNm))
        {
            n++;
            slots.Add(recoveryCase == 1
                ? new Slot(n, u, 1 + n, null, null, range)
                : new Slot(n, u, 5 + n, 5 + n + 15, firstPush + (n - 1) * 60, range));
        }
        return slots;
    }

    /// <summary>Types that recover on carriers.</summary>
    public static bool CarrierCapable(string? type) => type is "FA-18C_hornet" or "F-14A-135-GR" or "F-14B" or "S-3B" or "S-3B Tanker"
        or "E-2C" or "C-2A" or "T-45" or "AV8BNA" or "Su-33" or "MiG-29K" or "SH-60B" or "UH-60A" or "Rafale_M";
}
