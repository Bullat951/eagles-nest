using System.Text.Json;
using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class TheatreTests
{
    // 3×3 grid over x 0..2000, z 0..2000: lat rises with x, lon with z.
    private static Theatre Make() => new("Test", 0, 0, 2000, 2000,
        new GeoGrid(3, [40, 40, 40, 41, 41, 41, 42, 42, 42], [30, 31, 32, 30, 31, 32, 30, 31, 32]),
        [[JsonDocument.Parse("\"Town\"").RootElement, JsonDocument.Parse("41.5").RootElement, JsonDocument.Parse("31.5").RootElement]]);

    [Fact]
    public void Grid_nodes_map_exactly()
    {
        var (lat, lon) = Make().ToLatLon(1000, 2000);
        Assert.Equal(41, lat, 9);
        Assert.Equal(32, lon, 9);
    }

    [Fact]
    public void Between_nodes_interpolates()
    {
        var (lat, lon) = Make().ToLatLon(500, 1500);
        Assert.Equal(40.5, lat, 9);
        Assert.Equal(31.5, lon, 9);
    }

    [Fact]
    public void Outside_bounds_clamps_to_edge()
    {
        var (lat, _) = Make().ToLatLon(-5000, 0);
        Assert.Equal(40, lat, 9);
    }

    [Theory]
    [InlineData(500, 1500)]
    [InlineData(1234, 77)]
    [InlineData(1999, 1)]
    public void FromLatLon_inverts_ToLatLon(double x, double z)
    {
        var t = Make();
        var (lat, lon) = t.ToLatLon(x, z);
        var (bx, bz) = t.FromLatLon(lat, lon);
        Assert.Equal(x, bx, 3);
        Assert.Equal(z, bz, 3);
    }

    [Fact]
    public void Border_walks_four_edges()
    {
        var border = Make().Border(pointsPerEdge: 4);
        Assert.Equal(16, border.Count);
        Assert.Equal((40.0, 30.0), border[0]);
    }

    [Fact]
    public void Towns_parse_from_arrays() =>
        Assert.Equal(new Town("Town", 41.5, 31.5), Make().Towns.Single());

    [Fact]
    public void Terrain_decodes_little_endian_heights()
    {
        var heights = new byte[] { 0x10, 0x00, 0xFF, 0xFF };  // 16, -1
        var msg = new TerrainMessage("Test", 0, 0, 1000, 1, 2, Convert.ToBase64String(heights), Convert.ToBase64String(new byte[] { 1, 3 }));
        var grid = msg.Decode();
        Assert.Equal(16, grid.HeightAt(0, 0));
        Assert.Equal(-1, grid.HeightAt(0, 1));
        Assert.Equal(Surface.Water, grid.SurfaceAt(0, 1));
    }
}
