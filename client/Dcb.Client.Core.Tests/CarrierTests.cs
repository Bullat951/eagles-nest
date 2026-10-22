using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class CarrierTests
{
    private static readonly CarrierGeometry Nimitz = CarrierGeometry.For("Stennis")!;
    // A ship at the origin steaming 090° (grid) at 13 kt.
    private static readonly LsoShip Ship = new(4289, 0, 0, 0, 90, 0, 6.7);

    /// <summary>An aircraft <paramref name="asternM"/> behind touchdown on the landing centreline,
    /// <paramref name="aboveM"/> above the glideslope, <paramref name="rightM"/> right of the line,
    /// flying down the final at 70 m/s relative to the ship.</summary>
    private static LsoAircraft OnFinal(double asternM, double aboveM = 0, double rightM = 0, double pitch = 5, long id = 1)
    {
        var (tx, tz, ty) = Groove.Touchdown(Ship, Nimitz);
        var l = Groove.FinalBearingGrid(Ship, Nimitz) * Math.PI / 180;
        double lx = Math.Cos(l), lz = Math.Sin(l), rx = -Math.Sin(l), rz = Math.Cos(l);
        var y = ty + asternM * Math.Tan(Nimitz.GlideslopeDeg * Math.PI / 180) + aboveM;
        var vy = -70 * Math.Tan(Nimitz.GlideslopeDeg * Math.PI / 180);
        return new LsoAircraft(id, tx - lx * asternM + rx * rightM, y, tz - lz * asternM + rz * rightM,
            Ship.Vx + lx * 70, vy, Ship.Vz + lz * 70, 81, pitch, 1, 1);
    }

    [Fact]
    public void Aircraft_on_the_glideslope_and_centreline_reads_zero()
    {
        var g = Groove.Of(Ship, Nimitz, OnFinal(926));
        Assert.Equal(926, g.AsternM, 1);
        Assert.Equal(0, g.GlideM, 1);
        Assert.Equal(0, g.CrossM, 1);
        Assert.Equal(70, g.ClosureMs, 1);
        Assert.Equal(81, Groove.FinalBearingGrid(Ship, Nimitz), 6);  // 090 − 9° angled deck
    }

    [Fact]
    public void High_and_left_are_signed()
    {
        var g = Groove.Of(Ship, Nimitz, OnFinal(463, aboveM: 20, rightM: -10));
        Assert.Equal(65.6, g.GlideFt, 1);
        Assert.Equal(-32.8, g.CrossFt, 1);
        Assert.True(g.LineupDeg < 0);
    }

    private static LsoMessage Frame(double t, params LsoAircraft[] a) => new("blue", t, [Ship], a);

    private static PassTracker Tracker() => new() { CarrierGeometryFor = id => id == Ship.Id ? Nimitz : null };

    [Fact]
    public void A_pass_with_a_landing_event_is_a_graded_trap()
    {
        var t = Tracker();
        double time = 0;
        for (var astern = 1800.0; astern >= 0; astern -= 70, time += 1)
            t.Update(Frame(time, OnFinal(astern, pitch: 8.1 - 3.5)), _ => ("Enfield11", "FA-18C_hornet"));
        var pass = Assert.Single(t.Log);
        Assert.Equal(["AR", "IC", "IM", "X"], pass.Samples.Keys.Order());
        t.Landings([new CarrierEvent(1, "trap", 1, Ship.Id, time, 0, 0, 0, 0)]);
        Assert.Equal("TRAP", pass.Result);
        Assert.Equal("OK", pass.Grade);
    }

    [Fact]
    public void Crossing_the_deck_without_landing_is_a_bolter_and_climbing_away_a_wave_off()
    {
        var t = Tracker();
        double time = 0;
        for (var astern = 1800.0; astern >= -400; astern -= 70, time += 1)
            t.Update(Frame(time, OnFinal(Math.Max(astern, -400), aboveM: astern < 0 ? -astern * Math.Tan(3.5 * Math.PI / 180) + 2 : 0)), _ => ("Enfield11", "FA-18C_hornet"));
        for (int i = 0; i < 6; i++, time++) t.Update(Frame(time, OnFinal(-600, aboveM: 60)), _ => ("Enfield11", "FA-18C_hornet"));
        Assert.Equal("BOLTER", t.Log[0].Result);
        Assert.Equal("B", t.Log[0].Grade);

        var w = Tracker();
        time = 0;
        for (var astern = 1800.0; astern >= 600; astern -= 70, time += 1) w.Update(Frame(time, OnFinal(astern)), _ => ("Uzi11", null));
        for (int i = 0; i < 6; i++, time++) w.Update(Frame(time, OnFinal(600, aboveM: 70 + 15 * i) with { Vy = 10 }), _ => ("Uzi11", null));
        Assert.Equal("WAVE OFF", w.Log[0].Result);
        Assert.Equal("WO", w.Log[0].Grade);
    }

    [Fact]
    public void A_brief_excursion_from_the_groove_stays_one_pass()
    {
        var t = Tracker();
        double time = 0;
        for (var astern = 1800.0; astern >= 1100; astern -= 70, time += 1) t.Update(Frame(time, OnFinal(astern)), _ => ("Enfield11", "FA-18C_hornet"));
        for (int i = 0; i < 2; i++, time++) t.Update(Frame(time, OnFinal(1050, rightM: 600)), _ => ("Enfield11", "FA-18C_hornet"));
        for (var astern = 1000.0; astern >= 0; astern -= 70, time += 1) t.Update(Frame(time, OnFinal(astern)), _ => ("Enfield11", "FA-18C_hornet"));
        t.Landings([new CarrierEvent(1, "trap", 1, Ship.Id, time, 0, 0, 0, 0)]);
        Assert.Equal("TRAP", Assert.Single(t.Log).Result);
    }

    [Fact]
    public void A_landing_event_decides_only_the_latest_pass()
    {
        var t = Tracker();
        double time = 0;
        // First pass: a bolter.
        for (var astern = 1800.0; astern >= -400; astern -= 70, time += 1)
            t.Update(Frame(time, OnFinal(astern, aboveM: astern < 0 ? -astern * Math.Tan(3.5 * Math.PI / 180) + 2 : 0)), _ => ("Enfield11", "FA-18C_hornet"));
        for (int i = 0; i < 6; i++, time++) t.Update(Frame(time, OnFinal(-600, aboveM: 60)), _ => ("Enfield11", "FA-18C_hornet"));
        // Second pass: trapped.
        for (var astern = 1800.0; astern >= 0; astern -= 70, time += 1) t.Update(Frame(time, OnFinal(astern)), _ => ("Enfield11", "FA-18C_hornet"));
        t.Landings([new CarrierEvent(1, "trap", 1, Ship.Id, time, 0, 0, 0, 0)]);
        Assert.Equal(["BOLTER", "TRAP"], t.Log.Select(p => p.Result));
    }

    [Fact]
    public void An_overhead_arrival_is_no_pass()
    {
        // 725 ft over the deck at 180 m/s, straight over the ship, as the AI arrives for the break
        // (recorded on CVN-74): with the gear reported up, and with no gear reported at all.
        foreach (double? gear in new double?[] { 0, null })
        {
            var t = Tracker();
            double time = 0;
            for (var astern = 1800.0; astern >= -600; astern -= 180, time += 1)
                t.Update(Frame(time, OnFinal(astern, aboveM: 221 - Math.Max(0, astern) * Math.Tan(3.5 * Math.PI / 180)) with { Vy = 0, Gear = gear }), _ => ("Enfield11", "FA-18C_hornet"));
            for (int i = 0; i < 6; i++, time++) t.Update(Frame(time, OnFinal(-1000, aboveM: 221) with { Gear = gear }), _ => ("Enfield11", "FA-18C_hornet"));
            Assert.Empty(t.Log);
        }
    }

    [Fact]
    public void Headwind_lowers_the_flight_path_angle_for_aoa()
    {
        // 15 m/s of air coming down the final: through the air the descent is shallower, so the
        // same pitch reads less AoA than over the ground.
        var a = OnFinal(926, pitch: 5);
        var l = Groove.FinalBearingGrid(Ship, Nimitz) * Math.PI / 180;
        var still = Groove.Of(Ship, Nimitz, a);
        var windy = Groove.Of(Ship, Nimitz, a, (-Math.Cos(l) * 15, -Math.Sin(l) * 15));
        Assert.True(windy.AoaDeg < still.AoaDeg);
    }

    [Fact]
    public void Grades_follow_deviations_in_close()
    {
        var pass = new Pass { Unit = 1, Name = "x", Type = "FA-18C_hornet", Result = "TRAP" };
        pass.Samples["IC"] = new Groove(463, -3, 30, 12, 70, 8.0, 70);     // 39 ft high: a little; 10 ft left: fine
        pass.Samples["AR"] = new Groove(111, 0, 7, 0, 70, 8.1, 70);
        var (grade, comments) = PassTracker.GradeOf(pass);
        Assert.Equal("(OK)", grade);
        Assert.Equal("(H)IC", comments);

        pass.Samples["AR"] = new Groove(111, 15, 7, -25, 70, 8.1, 70);     // 82 ft low, 49 ft right at the ramp
        Assert.Equal("C", PassTracker.GradeOf(pass).Grade);
    }

    [Fact]
    public void Recovery_case_from_weather()
    {
        WeatherMessage Wx(double visM, double baseM, double density) =>
            new("2025-07-01", 0, visM, new FogWeather(false, 0, 0), new DustWeather(false, 0), new CloudWeather(baseM, 300, density, null, 0), []);
        Assert.Equal(1, Marshal.CaseFor(Wx(80000, 600, 2)));       // scattered: no ceiling
        Assert.Equal(1, Marshal.CaseFor(Wx(80000, 1200, 8)));      // overcast at 3900 ft
        Assert.Equal(2, Marshal.CaseFor(Wx(80000, 500, 8)));       // overcast at 1640 ft
        Assert.Equal(3, Marshal.CaseFor(Wx(80000, 200, 8)));       // overcast at 660 ft
        Assert.Equal(3, Marshal.CaseFor(Wx(5000, 3000, 0)));       // 2.7 NM visibility
    }

    [Fact]
    public void Case_III_stack_assigns_angels_dme_and_push_times()
    {
        Unit U(long id) => SymbologyTests.Make(id: id);
        var slots = Marshal.Stack(3, [(U(1), 40), (U(2), 12), (U(3), 80)], missionNow: 3600 + 30);
        Assert.Equal(2, slots.Count);  // the one at 80 NM is not yet in the stack
        Assert.Equal((2L, 6, 21), (slots[0].Unit.Id, slots[0].Angels, slots[0].Dme!.Value));
        Assert.Equal(3600 + 6 * 60, slots[0].PushTime);
        Assert.Equal(3600 + 7 * 60, slots[1].PushTime);
        Assert.Equal(2, Marshal.Stack(1, [(U(1), 5)], 0)[0].Angels);
    }
}
