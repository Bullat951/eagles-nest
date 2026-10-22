using Dcb.Client.Core;
using static Dcb.Client.Core.IcaoMatch;

namespace Dcb.Client.Core.Tests;

public class IcaoMatchTests
{
    // From the C-130J nav database: Akrotiri (runways 108/288 magnetic-ish as stored) and Ercan.
    private static readonly Airport[] Airports =
    [
        new("LCRA", 34.590401, 32.9879, [108, 288]),
        new("LCEN", 35.152337, 33.506584, [107, 287]),
        new("XXXX", 34.600, 32.990, [20, 200]),  // a nearby strip on another axis
    ];

    [Fact]
    public void Nearest_aligned_airport_wins()
    {
        // DCS Akrotiri runway centre, runway 10/28 at 111° true.
        Assert.Equal("LCRA", For(34.5894, 32.9934, [111, 291], Airports));
    }

    [Fact]
    public void Without_alignment_the_nearest_within_3_km_is_taken()
    {
        Assert.Equal("XXXX", For(34.6005, 32.9905, [], Airports));
        Assert.Null(For(30.0, 30.0, [90], Airports));
    }

    [Theory]
    [InlineData(100, 280, 0)]
    [InlineData(10, 355, 15)]
    [InlineData(0, 90, 90)]
    public void Axis_difference(double a, double b, double expected) => Assert.Equal(expected, AxisDiff(a, b), 6);
}
