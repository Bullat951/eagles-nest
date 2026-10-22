using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Dcb.Client.Core;

/// <summary>The server's weather message (public, once a minute): the mission's weather and,
/// per airfield, live wind 10 m above the field, temperature and QNH.</summary>
public sealed record WeatherMessage(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("time")] double Time,
    [property: JsonPropertyName("visibility")] double Visibility,
    [property: JsonPropertyName("fog")] FogWeather Fog,
    [property: JsonPropertyName("dust")] DustWeather Dust,
    [property: JsonPropertyName("clouds")] CloudWeather Clouds,
    [property: JsonPropertyName("airfields")] IReadOnlyList<AirfieldWeather> Airfields)
{
    public AirfieldWeather? For(int airfieldId) => Airfields.FirstOrDefault(a => a.Id == airfieldId);
}

public sealed record FogWeather(
    [property: JsonPropertyName("on")] bool On,
    [property: JsonPropertyName("visibility")] double Visibility,
    [property: JsonPropertyName("thickness")] double Thickness);

public sealed record DustWeather(
    [property: JsonPropertyName("on")] bool On,
    [property: JsonPropertyName("density")] double Density);

/// <summary>Clouds: a DCS preset ("Preset13", "RainyPreset2"), or the classic density 0–10.
/// Precipitation: 0 none, 1 rain, 2 thunderstorm, 3 snow, 4 snowstorm.</summary>
public sealed record CloudWeather(
    [property: JsonPropertyName("base")] double Base,
    [property: JsonPropertyName("thickness")] double Thickness,
    [property: JsonPropertyName("density")] double Density,
    [property: JsonPropertyName("preset")] string? Preset,
    [property: JsonPropertyName("precipitation")] int Precipitation);

public sealed record AirfieldWeather(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("elevation")] double Elevation,
    [property: JsonPropertyName("windFrom")] double WindFromTrue,
    [property: JsonPropertyName("windMs")] double WindMs,
    [property: JsonPropertyName("tempC")] double TempC,
    [property: JsonPropertyName("qnhHpa")] double QnhHpa);

/// <summary>A command's answer from the server; <c>Value</c> carries a result such as a new
/// mark's id.</summary>
public sealed record CommandResult(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("value")] string? Value = null);

/// <summary>ATIS and airfield information as text for a player's on-screen message.</summary>
public static class AtisText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const double Knots = 1.943844;  // per m/s
    private const double Feet = 3.28084;    // per m

    /// <summary>Hours to add to UTC for each theatre's mission clock (DCS runs local time).</summary>
    private static readonly Dictionary<string, double> UtcOffsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Syria"] = 3, ["Caucasus"] = 4, ["PersianGulf"] = 4, ["Nevada"] = -7, ["MarianaIslands"] = 10,
        ["Normandy"] = 1, ["TheChannel"] = 1, ["SinaiMap"] = 2, ["Sinai"] = 2, ["Kola"] = 3,
        ["Afghanistan"] = 4.5, ["Iraq"] = 3, ["Falklands"] = -3, ["SouthAtlantic"] = -3,
    };

    public static string Atis(Airfield field, AirfieldWeather wx, WeatherMessage mission, string theatreId, double magVarDeg,
        IReadOnlyList<AirfieldFrequency> frequencies)
    {
        var (time, zulu) = Clock(mission.Time, theatreId);
        var letter = (char)('A' + (int)(time.TotalHours % 26));  // a new letter every hour
        var name = Spoken(field.Name);
        var sb = new StringBuilder();
        sb.AppendLine($"{name} INFORMATION {Phonetic(letter)} {time:hhmm}{(zulu ? "Z" : "L")}");
        if (RunwayInUse(field, wx) is { } rwy) sb.AppendLine($"RUNWAY IN USE {rwy}");
        sb.AppendLine(Wind(wx, magVarDeg));
        sb.AppendLine(Visibility(mission, wx));
        sb.AppendLine(Clouds(mission.Clouds));
        sb.AppendLine($"TEMPERATURE {wx.TempC.ToString("0", Inv)}");
        sb.AppendLine($"QNH {Math.Round(wx.QnhHpa).ToString("0", Inv)} HPA / {(wx.QnhHpa * 0.0295300).ToString("0.00", Inv)} INCHES");
        if (Tower(frequencies) is { } tower) sb.AppendLine($"TOWER {tower}");
        sb.Append($"ADVISE ON INITIAL CONTACT YOU HAVE INFORMATION {Phonetic(letter)}");
        return sb.ToString();
    }

    public static string AirfieldInfo(Airfield field, AirfieldWeather? wx, IReadOnlyList<AirfieldFrequency> frequencies)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Spoken(field.Name)} ({field.Owner.ToUpperInvariant()})");
        if (wx is not null) sb.AppendLine($"ELEVATION {Math.Round(wx.Elevation * Feet).ToString("0", Inv)} FT");
        foreach (var r in field.Runways)
        {
            var (near, far) = r.Designators;
            sb.AppendLine($"RUNWAY {near}/{far} {r.Length.ToString("0", Inv)} M ({Math.Round(r.Length * Feet / 100) * 100:0} FT)");
        }
        foreach (var f in frequencies) sb.AppendLine($"{f.Band.ToUpperInvariant()} {f.Mhz.ToString("0.000", Inv)} {f.Modulation}");
        sb.Append($"PARKING {field.Parking.Count} SPOTS");
        return sb.ToString();
    }

    /// <summary>Mission time of day as UTC when the theatre's offset is known, else local.</summary>
    public static (TimeSpan Time, bool Zulu) Clock(double missionSeconds, string theatreId)
    {
        var local = TimeSpan.FromSeconds(missionSeconds % 86400);
        if (!UtcOffsets.TryGetValue(theatreId, out var offset)) return (local, false);
        var utc = local - TimeSpan.FromHours(offset);
        return (TimeSpan.FromSeconds(((utc.TotalSeconds % 86400) + 86400) % 86400), true);
    }

    /// <summary>The runway end most into the wind (designator); with no wind worth naming, the
    /// first runway's named end.</summary>
    public static string? RunwayInUse(Airfield field, AirfieldWeather wx)
    {
        if (field.Runways.Count == 0) return null;
        if (wx.WindMs * Knots < 3) return field.Runways[0].Designators.Near;
        string? best = null;
        var bestHeadwind = double.MinValue;
        foreach (var r in field.Runways)
        {
            var (near, far) = r.Designators;
            foreach (var (heading, name) in new[] { (r.HeadingTrue, near), (r.HeadingTrue + 180, far) })
            {
                var headwind = Math.Cos((wx.WindFromTrue - heading) * Math.PI / 180) * wx.WindMs;
                if (headwind > bestHeadwind) (best, bestHeadwind) = (name, headwind);
            }
        }
        return best;
    }

    public static string Wind(AirfieldWeather wx, double magVarDeg)
    {
        var kt = wx.WindMs * Knots;
        if (kt < 1) return "WIND CALM";
        var mag = Nav.Normalize(wx.WindFromTrue - magVarDeg);
        var dir = (int)Math.Round(mag / 10) * 10 % 360;
        return $"WIND {(dir == 0 ? 360 : dir):000} AT {Math.Round(kt):0} KNOTS";
    }

    public static string Visibility(WeatherMessage mission, AirfieldWeather wx)
    {
        var vis = mission.Visibility;
        // Fog covers fields below its top; DCS gives its thickness above sea level.
        if (mission.Fog.On && mission.Fog.Visibility > 0 && wx.Elevation <= mission.Fog.Thickness) vis = Math.Min(vis, mission.Fog.Visibility);
        if (mission.Dust.On && mission.Dust.Density > 0) vis = Math.Min(vis, mission.Dust.Density);
        return vis >= 10_000 ? "VISIBILITY 10 KM OR MORE"
            : vis >= 5_000 ? $"VISIBILITY {Math.Floor(vis / 1000):0} KM"
            : $"VISIBILITY {Math.Round(vis / 100) * 100:0} M";
    }

    public static string Clouds(CloudWeather c)
    {
        var precip = c.Precipitation switch { 1 => " RAIN", 2 => " THUNDERSTORM", 3 => " SNOW", 4 => " SNOWSTORM", _ => "" };
        string? cover;
        if (c.Preset is { Length: > 0 } preset)
        {
            // DCS cloud presets: 1–2 light scattered, 3–12 scattered, 13–20 broken, 21–27
            // overcast; RainyPreset1–3 overcast with rain.
            var rainy = preset.StartsWith("Rainy", StringComparison.OrdinalIgnoreCase);
            var n = int.TryParse(new string(preset.Where(char.IsDigit).ToArray()), out var k) ? k : 0;
            cover = rainy || n >= 21 ? "OVERCAST" : n >= 13 ? "BROKEN" : n >= 3 ? "SCATTERED" : "FEW";
            if (rainy && precip.Length == 0) precip = " RAIN";
        }
        else
        {
            cover = c.Density switch { <= 0 => null, <= 2 => "FEW", <= 4 => "SCATTERED", <= 7 => "BROKEN", _ => "OVERCAST" };
        }
        if (cover is null) return "SKY CLEAR" + precip;
        return $"CLOUDS {cover} {Math.Round(c.Base * Feet / 100) * 100:0} FT{precip}";
    }

    private static string? Tower(IReadOnlyList<AirfieldFrequency> frequencies)
    {
        var parts = frequencies.Where(f => f.Band is "UHF" or "VHF high").Select(f => f.Mhz.ToString("0.000", Inv)).ToList();
        return parts.Count > 0 ? string.Join(" / ", parts) : null;
    }

    private static string Spoken(string airfieldName) => airfieldName.Replace('_', ' ').ToUpperInvariant();

    private static readonly string[] Alphabet =
    [
        "ALPHA", "BRAVO", "CHARLIE", "DELTA", "ECHO", "FOXTROT", "GOLF", "HOTEL", "INDIA", "JULIETT", "KILO", "LIMA", "MIKE",
        "NOVEMBER", "OSCAR", "PAPA", "QUEBEC", "ROMEO", "SIERRA", "TANGO", "UNIFORM", "VICTOR", "WHISKEY", "XRAY", "YANKEE", "ZULU",
    ];

    public static string Phonetic(char letter) => Alphabet[Math.Clamp(letter - 'A', 0, 25)];
}
