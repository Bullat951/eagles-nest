using System.Text.Json;
using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class NavTests
{
    [Fact]
    public void One_degree_of_latitude_is_60_nm_due_north()
    {
        var (brg, m) = Nav.BearingRange(33, 36, 34, 36);
        Assert.Equal(0, brg, 6);
        Assert.Equal(59.9, m / Nav.MetersPerNm, 1);  // a degree of latitude at 33.5° on WGS84
    }

    [Fact]
    public void Matches_a_wgs84_reference()
    {
        // Syria bullseye → AWACS over Cyprus; reference from an independent Vincenty run.
        var (brg, m) = Nav.BearingRange(35.7335, 37.1383, 33.9289, 33.0869);
        Assert.Equal(242.79, brg, 2);
        Assert.Equal(227.41, m / Nav.MetersPerNm, 2);
    }

    [Fact]
    public void Due_east_on_the_equator_is_090()
    {
        var (brg, _) = Nav.BearingRange(0, 10, 0, 11);
        Assert.Equal(90, brg, 6);
    }

    [Fact]
    public void East_variation_is_subtracted()
    {
        Assert.Equal(85, Nav.Magnetic(90, 5), 9);
        Assert.Equal(355, Nav.Magnetic(2, 7), 9);   // wraps through north
        Assert.Equal(5, Nav.Magnetic(358, -7), 9);  // west variation adds
    }

    [Fact]
    public void Bearings_are_three_digits_and_north_is_360()
    {
        Assert.Equal("005", Nav.Bearing3(5.2));
        Assert.Equal("360", Nav.Bearing3(359.7));
        Assert.Equal("247", Nav.Bearing3(246.6));
    }

    [Fact]
    public void Bullseye_call_uses_variation_at_the_bullseye()
    {
        var bulls = new Bullseye(0, 0, 33, 36);
        var magvar = new MagVarGrid(32, 35, 1, 3, 3, [5, 5, 5, 5, 5, 5, 5, 5, 5]);
        // 60 NM due north, 5° east variation → 355 magnetic.
        Assert.Equal("BE 355/60", Nav.BullseyeCall(bulls, 34, 36, magvar));
    }

    // Observer due north of the target (bearing target → observer 000).
    [Theory]
    [InlineData(0, "HOT")]      // nose on the observer
    [InlineData(340, "HOT")]
    [InlineData(50, "FLANK")]
    [InlineData(270, "BEAM")]
    [InlineData(160, "DRAG")]   // flying away
    public void Aspect_from_nose_angle_off_the_observer(double hdg, string expected) =>
        Assert.Equal(expected, Nav.Aspect(hdg, 0));

    [Fact]
    public void Magvar_grid_interpolates_and_clamps()
    {
        var g = new MagVarGrid(30, 30, 1, 2, 2, [4, 6, 4, 6]);
        Assert.Equal(5, g.At(30.5, 30.5), 9);
        Assert.Equal(6, g.At(50, 50), 9);
    }

    [Fact]
    public void Picture_and_theatre_parse_bullseye_and_magvar()
    {
        var p = JsonSerializer.Deserialize<Picture>("""{"version":1,"coalition":"blue","time":1,"theatre":"Syria","bullseye":{"x":1,"z":2,"lat":33.5,"lon":36.1},"units":[]}""")!;
        Assert.Equal(33.5, p.Bullseye!.Lat);
        var t = JsonSerializer.Deserialize<Theatre>("""{"id":"Syria","x0":0,"z0":0,"x1":1,"z1":1,"grid":{"n":2,"lat":[0,0,1,1],"lon":[0,1,0,1]},"magvar":{"lat0":30,"lon0":30,"step":0.5,"nlat":2,"nlon":2,"deg":[5,5,5,5]},"towns":[]}""")!;
        Assert.Equal(5, t.MagVar!.At(30.2, 30.2), 9);
    }
}
