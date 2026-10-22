using System.Text;
using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class RoadNetworkTests
{
    private static byte[] Rn4(params (byte Kind, byte Type, (double X, double Z)[] Points)[] edges)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(2); w.Write(48); w.Write(0L); w.Write(new byte[16]);
        var name = "landscape4::lRoadNetwork"u8.ToArray();
        w.Write(name.Length); w.Write(name);
        string[] types = ["track", "secondary", "primary", "motorway", "rail_main"];
        w.Write(5); w.Write(types.Length);
        foreach (var t in types) { w.Write(t.Length); w.Write(Encoding.ASCII.GetBytes(t)); }
        w.Write(new byte[37]);  // other tables between edges (odd length on purpose)
        foreach (var (kind, type, pts) in edges)
        {
            w.Write(6); w.Write(kind); w.Write(type); w.Write(7); w.Write(8); w.Write(0); w.Write(pts.Length);
            foreach (var (x, z) in pts) { w.Write(x); w.Write(120.0); w.Write(z); }
            w.Write(pts.Length);  // the per-point direction array that follows
            foreach (var _ in pts) { w.Write(1.0); w.Write(0.0); w.Write(0.0); }
        }
        // A look-alike with an absurd elevation must not be taken.
        w.Write(6); w.Write((byte)2); w.Write((byte)0); w.Write(1); w.Write(2); w.Write(0); w.Write(2);
        w.Write(0.0); w.Write(99999.0); w.Write(0.0); w.Write(1.0); w.Write(99999.0); w.Write(1.0);
        return ms.ToArray();
    }

    [Fact]
    public void Decodes_typed_edges_and_skips_noise()
    {
        var data = Rn4(
            (2, 3, [(-35000, -270000), (-35100, -269900), (-35200, -269800)]),
            (1, 4, [(100000, 50000), (100500, 50500)]));
        var net = RoadNetwork.Decode(new MemoryStream(data));
        Assert.Equal(2, net.Edges.Count);
        Assert.Equal(RoadNetwork.Kind.Motorway, net.Edges[0].Kind);
        // The straight middle point is simplified away.
        Assert.Equal([-35000f, -270000f, -35200f, -269800f], net.Edges[0].Xz);
        Assert.Equal(RoadNetwork.Kind.Rail, net.Edges[1].Kind);
    }

    [Theory]
    [InlineData("viaduct_motorway", RoadNetwork.Kind.Motorway)]
    [InlineData("primary_slope", RoadNetwork.Kind.Primary)]
    [InlineData("road_tunnel", RoadNetwork.Kind.Primary)]
    [InlineData("viaduct_link", RoadNetwork.Kind.Secondary)]
    [InlineData("track_balloon", RoadNetwork.Kind.Track)]
    [InlineData("viaduct_rail_10", RoadNetwork.Kind.Rail)]
    public void Classifies_dcs_type_names(string type, RoadNetwork.Kind kind) => Assert.Equal(kind, RoadNetwork.Classify(type));

    [Fact]
    public void Simplify_drops_collinear_points_and_keeps_bends()
    {
        // Straight run with a 1 m wobble, then a 90° bend.
        float[] xz = [0, 0, 10, 0.5f, 20, -0.5f, 30, 0, 30, 10, 30, 20];
        var s = RoadNetwork.Simplify(xz, 2.5);
        Assert.Equal([0f, 0f, 30f, 0f, 30f, 20f], s);
    }

    [Fact]
    public void Cache_round_trips()
    {
        var net = new RoadNetwork([new RoadNetwork.Edge(RoadNetwork.Kind.Primary, [1, 2, 3, 4])]);
        var ms = new MemoryStream();
        net.Save(ms);
        ms.Position = 0;
        var back = RoadNetwork.Load(ms);
        Assert.Equal(RoadNetwork.Kind.Primary, back.Edges[0].Kind);
        Assert.Equal([1f, 2f, 3f, 4f], back.Edges[0].Xz);
    }
}
