using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class AtisTests
{
    // Runway "35": DCS course is minus the true heading in radians (heading 350°).
    private static Airfield Field() => new(21, "Bassel Al-Assad", 0, 0, 0, 2,
        [new Runway("35", -350 * Math.PI / 180, 2800, 45, 0, 0)], [[1, 0, 0, 72], [2, 0, 0, 72]]);

    private static WeatherMessage Mission(CloudWeather? clouds = null, double vis = 80_000, FogWeather? fog = null) =>
        new("2025-07-01", 50_400, vis, fog ?? new FogWeather(false, 0, 0), new DustWeather(false, 0),
            clouds ?? new CloudWeather(5000, 200, 0, null, 0), []);

    private static AirfieldWeather Wx(double from, double ms) => new(21, 28, from, ms, 19.8, 1016.7);

    [Theory]
    [InlineData(350, 8, "35")]   // wind straight down 35
    [InlineData(170, 8, "17")]   // from the south: land on 17
    [InlineData(260, 8, "35")]   // crosswind slightly from the north side wins 35
    [InlineData(170, 1, "35")]   // calm: the named end
    public void Runway_in_use_faces_the_wind(double from, double ms, string expected) =>
        Assert.Equal(expected, AtisText.RunwayInUse(Field(), Wx(from, ms)));

    [Fact]
    public void Wind_is_magnetic_rounded_to_ten_degrees() =>
        Assert.Equal("WIND 170 AT 8 KNOTS", AtisText.Wind(Wx(180, 4.1), magVarDeg: 5.4));

    [Fact]
    public void Wind_north_is_360_and_light_wind_is_calm()
    {
        Assert.Equal("WIND 360 AT 10 KNOTS", AtisText.Wind(Wx(2, 5.1), magVarDeg: 0));
        Assert.Equal("WIND CALM", AtisText.Wind(Wx(90, 0.4), magVarDeg: 0));
    }

    [Theory]
    [InlineData(null, 0, 0, "SKY CLEAR")]
    [InlineData(null, 5, 0, "CLOUDS BROKEN 16400 FT")]
    [InlineData("Preset3", 0, 0, "CLOUDS SCATTERED 16400 FT")]
    [InlineData("Preset22", 0, 0, "CLOUDS OVERCAST 16400 FT")]
    [InlineData("RainyPreset1", 0, 0, "CLOUDS OVERCAST 16400 FT RAIN")]
    [InlineData(null, 9, 2, "CLOUDS OVERCAST 16400 FT THUNDERSTORM")]
    public void Clouds_from_presets_or_density(string? preset, double density, int precip, string expected) =>
        Assert.Equal(expected, AtisText.Clouds(new CloudWeather(5000, 300, density, preset, precip)));

    [Fact]
    public void Fog_below_its_top_limits_visibility()
    {
        Assert.Equal("VISIBILITY 800 M", AtisText.Visibility(Mission(fog: new FogWeather(true, 800, 300)), Wx(0, 0)));
        Assert.Equal("VISIBILITY 10 KM OR MORE", AtisText.Visibility(Mission(fog: new FogWeather(true, 800, 10)), Wx(0, 0)));
    }

    [Fact]
    public void Clock_is_zulu_from_the_theatre_offset()
    {
        var (time, zulu) = AtisText.Clock(50_400, "Syria");  // 14:00 local, UTC+3
        Assert.True(zulu);
        Assert.Equal(TimeSpan.FromHours(11), time);
        Assert.False(AtisText.Clock(50_400, "SomeNewMap").Zulu);
    }

    [Fact]
    public void Atis_reads_like_a_broadcast()
    {
        var text = AtisText.Atis(Field(), Wx(170, 4.1), Mission(), "Syria", 5.4,
            [new AirfieldFrequency("UHF", 250.5, "AM"), new AirfieldFrequency("VHF high", 122.2, "AM"), new AirfieldFrequency("HF", 4.0, "AM")]);
        var lines = text.Split(Environment.NewLine);
        Assert.Equal("BASSEL AL-ASSAD INFORMATION LIMA 1100Z", lines[0]);  // 11 h → L
        Assert.Equal("RUNWAY IN USE 17", lines[1]);
        Assert.Equal("WIND 160 AT 8 KNOTS", lines[2]);  // 170° true − 5.4° east variation
        Assert.Contains("QNH 1017 HPA / 30.02 INCHES", lines);
        Assert.Contains("TOWER 250.500 / 122.200", lines);
        Assert.Equal("ADVISE ON INITIAL CONTACT YOU HAVE INFORMATION LIMA", lines[^1]);
    }

    [Fact]
    public void Airfield_info_lists_runways_frequencies_and_parking()
    {
        var text = AtisText.AirfieldInfo(Field(), Wx(0, 0), [new AirfieldFrequency("UHF", 250.5, "AM")]);
        Assert.Equal(
            ["BASSEL AL-ASSAD (BLUE)", "ELEVATION 92 FT", "RUNWAY 35/17 2800 M (9200 FT)", "UHF 250.500 AM", "PARKING 2 SPOTS"],
            text.Split(Environment.NewLine));
    }
}
