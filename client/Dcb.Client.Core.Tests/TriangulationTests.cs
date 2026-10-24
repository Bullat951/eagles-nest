using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class TriangulationTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A line from an aircraft at (lat, lon) towards the emitter, as the true bearing.</summary>
    private static BearingLine Toward(int id, double lat, double lon, double eLat, double eLon, double errorDeg = 0) =>
        new(id, 1, "test", lat, lon, Nav.Normalize(Nav.BearingRange(lat, lon, eLat, eLon).TrueDeg + errorDeg), 10, null, T0);

    [Fact]
    public void ClockPositionsAreRelativeToTheNose()
    {
        Assert.Equal(90, BearingLine.BearingOf(0, 3));
        Assert.Equal(270, BearingLine.BearingOf(0, 9));
        Assert.Equal(0, BearingLine.BearingOf(0, 12));
        Assert.Equal(30, BearingLine.BearingOf(300, 3));
    }

    [Fact]
    public void ExactLinesCrossAtTheEmitter()
    {
        const double eLat = 35.30, eLon = 36.33;
        var fix = Triangulation.Solve([Toward(1, 35.00, 36.00, eLat, eLon), Toward(2, 35.10, 36.60, eLat, eLon), Toward(3, 35.55, 36.10, eLat, eLon)]);
        Assert.NotNull(fix);
        var miss = Nav.BearingRange(fix.Lat, fix.Lon, eLat, eLon).Meters;
        Assert.True(miss < 300, $"fix {miss:0} m off");
        Assert.Equal(3, fix.Lines);
    }

    [Fact]
    public void NoisyLinesStayNearAndReportTheirError()
    {
        const double eLat = 35.30, eLon = 36.33;
        var fix = Triangulation.Solve([Toward(1, 35.00, 36.00, eLat, eLon, 4), Toward(2, 35.10, 36.60, eLat, eLon, -3)]);
        Assert.NotNull(fix);
        var miss = Nav.BearingRange(fix.Lat, fix.Lon, eLat, eLon).Meters;
        Assert.True(miss < 10_000, $"fix {miss:0} m off");
        Assert.True(fix.MajorM > fix.MinorM && fix.MinorM > 1000);
    }

    [Fact]
    public void TighterLinesPullTheFixAndShrinkTheEllipse()
    {
        const double eLat = 35.30, eLon = 36.33;
        static BearingLine With(BearingLine l, double sigma) => l with { SigmaDeg = sigma };
        var coarse = Triangulation.Solve([With(Toward(1, 35.00, 36.00, eLat, eLon, 4), 15), With(Toward(2, 35.10, 36.60, eLat, eLon), 15),
            With(Toward(3, 35.55, 36.10, eLat, eLon, -5), 15)])!;
        var fine = Triangulation.Solve([With(Toward(1, 35.00, 36.00, eLat, eLon, 4), 15), With(Toward(2, 35.10, 36.60, eLat, eLon), 3),
            With(Toward(3, 35.55, 36.10, eLat, eLon, -5), 15)])!;
        Assert.True(fine.MajorM * fine.MinorM < coarse.MajorM * coarse.MinorM);
        // The exact, tight line weighs more: the fix moves onto it.
        var exact = Toward(2, 35.10, 36.60, eLat, eLon);
        double Across(BearingFix f)
        {
            var br = Nav.BearingRange(exact.Lat, exact.Lon, f.Lat, f.Lon);
            return Math.Abs(Math.Sin((br.TrueDeg - exact.BearingTrue) * Math.PI / 180)) * br.Meters;
        }
        Assert.True(Across(fine) < Across(coarse));
    }

    [Fact]
    public void ParallelOrBehindLinesGiveNoFix()
    {
        // Same bearing from two points along it: no crossing.
        Assert.Null(Triangulation.Solve([new(1, 1, "a", 35.0, 36.0, 90, 10, null, T0), new(2, 1, "a", 35.0, 36.2, 90, 10, null, T0)]));
        // Two lines pointing away from each other cross behind both aircraft.
        Assert.Null(Triangulation.Solve([new(1, 1, "a", 35.0, 36.0, 270, 10, null, T0), new(2, 1, "a", 35.0, 36.4, 90, 10, null, T0)]));
        Assert.Null(Triangulation.Solve([new(1, 1, "a", 35.0, 36.0, 90, 10, null, T0)]));
    }

    [Fact]
    public void BoardFixesPerAircraftAndMergesServerThreats()
    {
        var board = new GciBoard();
        board.AddLine(7, "Viper", 35.00, 36.00, 45, T0);
        board.AddLine(7, "Viper", 35.40, 36.00, 135, T0, 5, "6");
        board.AddLine(8, "Hornet", 34.00, 36.00, 90, T0);  // one line only: no fix
        var fixes = board.Fixes();
        Assert.Single(fixes);
        Assert.Equal(7, fixes[0].TrackId);

        var mine = board.AddThreat(ThreatType.Find("SA-6")!, fixes[0].Fix.Lat, fixes[0].Fix.Lon, T0);
        var note = board.AddThreat(ThreatType.Find("AAA")!, 35.0, 36.0, T0);  // never sent
        board.SetMarkId(mine, 880000, T0);
        Assert.Equal(2, board.AllThreats().Count);  // sent, feed not there yet: still drawn once
        static bool ZoneLabel(F10Mark m) => m.Text.Contains("Sierra") || m.Text.Contains("(50)");
        board.ApplyMarks([
            new F10Mark(880000, "SA-6 (GCI)", 0, 0, mine.Lat, mine.Lon, 1, null, true, "SA-6"),
            new F10Mark(880001, "SA-10 (GCI)", 0, 0, 34.5, 37.0, 1, null, true, "SA-10"),
            new F10Mark(5001, " Sierra (36)", 0, 0, 35.5, 38.0, 0, "mission data", false, null),
            new F10Mark(5029, " SA-5 (50)", 0, 0, 34.8, 38.8, 0, "mission data", false, null),  // a zone's name
            new F10Mark(12, "pilot down", 0, 0, 35.1, 36.1, 99, "Viper 1-1", false, null),
            new F10Mark(13, "sa11 near the dam", 0, 0, 35.8, 38.6, 99, "Viper 1-2", false, null),
        ], T0.AddSeconds(2));
        var threats = board.AllThreats(ZoneLabel);
        Assert.Equal(4, threats.Count);
        Assert.Contains(threats, t => t.Local == mine && t.MarkId == 880000 && t.Removable);
        Assert.Contains(threats, t => t.Local == note && t.MarkId is null);
        Assert.Contains(threats, t => t.Type.Name == "SA-10" && t.Local is null && t.Removable);
        Assert.Contains(threats, t => t.Type.Name == "SA-11" && t.MarkId == 13 && !t.Removable);  // a player's
        Assert.Equal([12L], board.VisibleMarks(ZoneLabel).Select(m => m.Id).ToList());

        // A just-sent threat waits for the feed; once its mark is gone (deleted in the game) it goes too.
        board.ApplyMarks([], T0.AddSeconds(5));
        Assert.Contains(mine, board.Threats);
        board.ApplyMarks([], T0.AddSeconds(30));
        Assert.DoesNotContain(mine, board.Threats);
        Assert.Contains(note, board.Threats);
    }

    [Fact]
    public void SharedThreatsAreDrawnOnceTheTheatreConverts()
    {
        var board = new GciBoard();
        board.ApplyShared([new SharedThreat(4, 1000, 2000, "SA-11", 50), new SharedThreat(5, 0, 0, "bogus", 51)]);
        Assert.Empty(board.AllThreats());  // no theatre yet
        board.ToLatLon = (x, z) => (42 + x / 1e5, 41 + z / 1e5);
        var t = Assert.Single(board.AllThreats());  // the unknown type is left out
        Assert.Equal("SA-11", t.Type.Name);
        Assert.Equal(4, t.SharedId);
        Assert.True(t.Removable);
        Assert.Equal(42.01, t.Lat, 6);
        board.ClearMarks();  // disconnected: the next connection resends them
        Assert.Empty(board.AllThreats());
    }

    [Theory]
    [InlineData("SA-6 (GCI)", "SA-6")]
    [InlineData("sa6", "SA-6")]
    [InlineData("  SA 11 near Hama", "SA-11")]
    [InlineData("Patriot battery", "Patriot")]
    [InlineData("hq7", "HQ-7")]
    [InlineData("aaa on the ridge", "AAA")]
    [InlineData("SA-60 bogus", null)]
    [InlineData("SAR pilot down", null)]
    [InlineData("pilot down", null)]
    [InlineData("hawkeye", null)]
    public void ThreatTypeFromMarkText(string text, string? expected) =>
        Assert.Equal(expected, ThreatType.Parse(text)?.Name);
}
