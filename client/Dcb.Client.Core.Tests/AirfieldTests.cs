using System.Text.Json;
using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class AirfieldTests
{
    // Akrotiri as the live server sent it.
    private const string Json = """
        {"type":"airfields","airfields":[{"id":12,"name":"Akrotiri","category":0,"x":-35297.1,"z":-270072.3,
          "runways":[{"name":"10","course":-1.93862,"length":2523,"width":65,"x":-35778.6,"z":-268906.1}],
          "parking":[[46,-36525.9,-267610.2,104],[3,-36000.0,-268000.0,40]],"coalition":2}]}
        """;

    private static Airfield Akrotiri() => JsonSerializer.Deserialize<AirfieldsMessage>(Json)!.Airfields.Single();

    [Fact]
    public void Runway_heading_is_negated_course()
    {
        // DCS course -1.93862 rad → 111.07° true: runway 10 with ~4° east declination.
        Assert.Equal(111.07, Akrotiri().Runways[0].HeadingTrue, 2);
    }

    [Theory]
    [InlineData("10", "10", "28")]
    [InlineData("5L", "05L", "23R")]
    [InlineData("36", "36", "18")]
    [InlineData("18R", "18R", "36L")]
    public void Designators_for_both_ends(string name, string near, string far)
    {
        var rw = new Runway(name, 0, 2000, 45, 0, 0);
        Assert.Equal((near, far), rw.Designators);
    }

    [Fact]
    public void Parking_parses_compact_arrays()
    {
        var a = Akrotiri();
        Assert.Equal("blue", a.Owner);
        Assert.Equal(2, a.Parking.Count);
        Assert.Equal(new ParkingSpot(46, -36525.9, -267610.2, 104), a.Parking[0]);
        Assert.True(a.Parking[1].HelicopterOnly);
    }
}
