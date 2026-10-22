using Dcb.Client.Core;
using static Dcb.Client.Core.TaxiwayGraph;

namespace Dcb.Client.Core.Tests;

public class TaxiwayGraphTests
{
    private static Edge E(uint a, uint b, params double[] xz) => new("taxiway_23m", 23, false, a, b, xz);

    [Fact]
    public void Straight_taxiway_runs_through_a_crossing_as_one_leg()
    {
        // Parallel taxiway 1-2-3 along z, a connector 2-4 leaving at a right angle.
        var g = new TaxiwayGraph(
        [
            E(1, 2, 0, 0, 0, 500),
            E(2, 3, 0, 500, 0, 1000),
            E(2, 4, 0, 500, 300, 500),
        ]);
        var legs = g.Legs();
        Assert.Equal(2, legs.Count);
        Assert.Contains(legs, l => Math.Abs(l.Length - 1000) < 1);
        Assert.Contains(legs, l => Math.Abs(l.Length - 300) < 1);
    }

    [Fact]
    public void A_right_angle_turn_inside_an_edge_ends_the_leg()
    {
        var g = new TaxiwayGraph([E(1, 2, 0, 0, 0, 400, 400, 400)]);
        var legs = g.Legs();
        Assert.Equal(2, legs.Count);
        Assert.All(legs, l => Assert.Equal(400, l.Length, 0));
    }

    [Fact]
    public void A_gentle_curve_stays_one_leg()
    {
        // 90° spread over a quarter circle of 600 m radius: ~9.4 m steps of 0.9°.
        var pts = new List<double>();
        for (int i = 0; i <= 100; i++)
        {
            var a = i * Math.PI / 200;
            pts.Add(600 * Math.Sin(a));
            pts.Add(600 - 600 * Math.Cos(a));
        }
        var legs = new TaxiwayGraph([E(1, 2, pts.ToArray())]).Legs();
        Assert.Single(legs);
    }

    [Fact]
    public void Runway_edges_are_not_legs_and_names_go_by_vote()
    {
        var g = new TaxiwayGraph(
        [
            new Edge("runway_65m", 65, true, 1, 2, [0, 0, 0, 3000]),
            E(3, 4, 200, 0, 200, 3000),
            E(4, 5, 200, 3000, 0, 3000),
        ]);
        var legs = g.Legs();
        Assert.Equal(2, legs.Count);
        var named = Name(legs, [("A", 220, 100), ("A", 180, 2900), ("B", 230, 1500), ("C", 100, 3020), ("Z", 900, 900)]);
        Assert.Equal(2, named.Count);
        Assert.Equal("A", named.Single(n => n.Leg.Length > 2000).Name);
        Assert.Equal("C", named.Single(n => n.Leg.Length < 300).Name);
    }

    [Theory]
    [InlineData("taxiway_23m", 23)]
    [InlineData("taxiway_23md", 23)]
    [InlineData("runway_65m", 65)]
    [InlineData("apron", 20)]
    public void Width_from_type(string type, double width) => Assert.Equal(width, WidthOf(type));
}
