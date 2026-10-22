using System.Globalization;
using System.Text.RegularExpressions;

namespace Dcb.Client.Core;

/// <summary>A radio navigation aid of the terrain (ILS, TACAN, VOR, NDB…) from the install's
/// <c>Mods/terrains/&lt;id&gt;/beacons.lua</c>. Position in DCS map metres, altitude m ASL.
/// <c>Direction</c> is true degrees; for a localizer it is where the beam points from the
/// antenna, so the inbound course is <see cref="InboundCourseTrue"/>.</summary>
public sealed partial record Beacon(string Type, string Callsign, string Name, double FrequencyHz, int? Channel,
    double X, double Alt, double Z, double Direction, int? AirfieldId)
{
    public bool IsLocalizer => Type is "ILS_LOCALIZER" or "PRMG_LOCALIZER";
    public bool IsGlideslope => Type is "ILS_GLIDESLOPE" or "PRMG_GLIDESLOPE";
    public bool IsTacan => Type is "TACAN" or "VORTAC";

    /// <summary>The course an aircraft flies down a localizer, true degrees.</summary>
    public double InboundCourseTrue => ((Direction + 180) % 360 + 360) % 360;

    /// <summary>"109.70" (MHz), "365" (kHz) for NDBs, or the TACAN channel "107X".</summary>
    public string FrequencyText => FrequencyHz <= 0 ? "" : FrequencyHz >= 30e6 ? (FrequencyHz / 1e6).ToString("0.00", CultureInfo.InvariantCulture)
        : (FrequencyHz / 1e3).ToString("0", CultureInfo.InvariantCulture);

    public string? ChannelText => Channel is { } c ? $"{c}X" : null;

    /// <summary>Short kind for tables: ILS, GS, TACAN, VORTAC, VOR/DME, NDB, DME…</summary>
    public string Kind => Type switch
    {
        "ILS_LOCALIZER" => "ILS",
        "ILS_GLIDESLOPE" => "GS",
        "PRMG_LOCALIZER" => "PRMG",
        "PRMG_GLIDESLOPE" => "PRMG GS",
        "AIRPORT_HOMER" or "AIRPORT_HOMER_WITH_MARKER" or "HOMER" => "NDB",
        "VOR_DME" => "VOR/DME",
        _ => Type.Replace('_', ' '),
    };

    /// <summary>Parses a beacons.lua: the entries of its `beacons = { … }` table.</summary>
    public static IReadOnlyList<Beacon> Parse(string lua)
    {
        var list = new List<Beacon>();
        foreach (Match m in Entry().Matches(lua))
        {
            var e = m.Value;
            var type = Field(e, @"type\s*=\s*BEACON_TYPE_(\w+)");
            var pos = Position().Match(e);
            if (type is null || !pos.Success) continue;
            var id = Field(e, @"beaconId\s*=\s*'airfield(\d+)_");
            list.Add(new Beacon(type,
                Field(e, @"callsign\s*=\s*'([^']*)'") ?? "",
                Field(e, @"display_name\s*=\s*_\('([^']*)'\)") ?? "",
                Number(Field(e, @"frequency\s*=\s*([-\d.eE+]+)")) ?? 0,
                Field(e, @"channel\s*=\s*(\d+)") is { } ch ? int.Parse(ch, CultureInfo.InvariantCulture) : null,
                Number(pos.Groups[1].Value) ?? 0, Number(pos.Groups[2].Value) ?? 0, Number(pos.Groups[3].Value) ?? 0,
                Number(Field(e, @"direction\s*=\s*([-\d.eE+]+)")) ?? 0,
                id is null ? null : int.Parse(id, CultureInfo.InvariantCulture)));
        }
        return list;
    }

    private static string? Field(string s, string pattern) => Regex.Match(s, pattern) is { Success: true } m ? m.Groups[1].Value : null;

    private static double? Number(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    // One table entry: a brace block holding a beaconId (entries do not nest).
    [GeneratedRegex(@"\{[^{}]*?beaconId[^{}]*?(?:\{[^{}]*\}[^{}]*?)*\}", RegexOptions.Singleline)]
    private static partial Regex Entry();

    [GeneratedRegex(@"position\s*=\s*\{\s*([-\d.eE+]+)\s*,\s*([-\d.eE+]+)\s*,\s*([-\d.eE+]+)\s*\}")]
    private static partial Regex Position();
}
