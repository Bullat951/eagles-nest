using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class CoordinatesTests
{
    [Fact]
    public void Mgrs_at_the_equator_and_prime_meridian() =>
        // Zone 31's central meridian is 3°E: easting 500 000 − 333 978.6 = 166 021.4 m.
        Assert.Equal("31N AA 66021 00000", Coordinates.Mgrs(0, 0));

    [Fact]
    public void Mgrs_of_the_washington_monument()
    {
        // NGS's US National Grid example: 18S UJ 23487 06483 (38.8895 N, 77.0353 W, rounded here).
        var mgrs = Coordinates.Mgrs(38.8895, -77.0353);
        Assert.StartsWith("18S UJ 23", mgrs);
        Assert.Contains(" 06", mgrs);
    }

    [Theory]
    [InlineData(-10, 20, 'L')]
    [InlineData(34.7, 36.7, 'S')]
    [InlineData(43.5, 41.0, 'T')]
    public void Mgrs_latitude_bands(double lat, double lon, char band) =>
        Assert.Equal(band, Coordinates.Mgrs(lat, lon).Split(' ')[0][^1]);

    [Fact]
    public void Mgrs_precision_truncates() =>
        Assert.Equal("31N AA 660 000", Coordinates.Mgrs(0, 0, digits: 3));

    [Fact]
    public void Utm_southern_hemisphere_has_false_northing() =>
        Assert.True(Coordinates.Utm(-10, 20).Northing is > 8_800_000 and < 10_000_000);

    [Fact]
    public void Ddm_like_dcs() =>
        Assert.Equal("N 34°42.120' E 036°41.460'", Coordinates.Ddm(34.702, 36.691));

    [Fact]
    public void Ddm_west_and_south() =>
        Assert.Equal("S 09°30.000' W 077°02.118'", Coordinates.Ddm(-9.5, -77.0353));

    [Fact]
    public void Dms_rounds_to_tenths_of_seconds() =>
        Assert.Equal("N 34°42'07.2\" E 36°41'27.6\"", Coordinates.Dms(34.702, 36.691));
}
