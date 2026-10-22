using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class LineOfSightTests
{
    // 100×100 cells of 500 m (≈50 km square), flat at `ground`, optionally a ridge across x = 25 km.
    private static TerrainGrid Grid(short ground = 0, short? ridge = null)
    {
        const int n = 100;
        var heights = new short[n * n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            heights[i * n + j] = ridge is { } r && i is 49 or 50 or 51 ? r : ground;
        return new TerrainGrid("Test", 0, 0, 500, n, n, heights, new byte[n * n]);
    }

    [Fact]
    public void Flat_ground_short_range_is_visible() =>
        // 20 km between 10 m masts: bulge at mid-path ≈ 10²/(2·8 495) km ≈ 6 m < 10 m.
        Assert.True(LineOfSight.Visible(Grid(), 1000, 1000, 10, 21_000, 1000, 10));

    [Fact]
    public void Ridge_between_low_points_blocks() =>
        Assert.False(LineOfSight.Visible(Grid(ridge: 800), 1000, 1000, 50, 45_000, 1000, 50));

    [Fact]
    public void High_aircraft_sees_over_the_ridge() =>
        Assert.True(LineOfSight.Visible(Grid(ridge: 800), 1000, 1000, 50, 45_000, 1000, 8000));

    [Fact]
    public void Earth_curvature_hides_two_low_points_far_apart()
    {
        // Two 10 m masts 49 km apart: bulge at mid-path ≈ 24.5²/(2·8 495) km ≈ 35 m > 10 m.
        Assert.False(LineOfSight.Visible(Grid(), 0, 1000, 10, 49_000, 1000, 10));
    }

    [Fact]
    public void Path_off_the_grid_counts_as_clear() =>
        Assert.True(LineOfSight.Visible(Grid(ground: 3000), -90_000, -90_000, 10, -80_000, -80_000, 10));
}

public class AirfieldRadiosTests
{
    private const string Sample = """
        radio = {
            {
                -- Akrotiri
                radioId = 'airfield44_0';
                role = {"ground", "tower", "approach"};
                frequency = {[HF] = {MODULATIONTYPE_AM, 4775000.000000}, [UHF] = {MODULATIONTYPE_AM, 252000000.000000}, [VHF_HI] = {MODULATIONTYPE_AM, 128000000.000000}, [VHF_LOW] = {MODULATIONTYPE_FM, 40450000.000000}};
            };
            {
                -- Damascus
                radioId = 'airfield7_0';
                frequency = {[UHF] = {MODULATIONTYPE_AM, 253250000.000000}};
            };
        }
        """;

    [Fact]
    public void Reads_frequencies_per_airbase_id_ordered_by_band()
    {
        var radios = AirfieldRadios.Parse(Sample);
        Assert.Equal([44, 7], radios.Keys);
        Assert.Equal(
            [new AirfieldFrequency("UHF", 252.0, "AM"), new AirfieldFrequency("VHF high", 128.0, "AM"),
             new AirfieldFrequency("VHF low", 40.45, "FM"), new AirfieldFrequency("HF", 4.775, "AM")],
            radios[44]);
        Assert.Equal(253.25, radios[7].Single().Mhz, 6);
    }
}
