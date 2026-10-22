using System.Globalization;
using System.Text.RegularExpressions;

namespace Dcb.Client.Core;

/// <summary>One frequency of an airfield's tower: band name (UHF, VHF high/low, HF), MHz and
/// modulation ("AM"/"FM").</summary>
public sealed record AirfieldFrequency(string Band, double Mhz, string Modulation);

/// <summary>Airfield tower frequencies from a terrain's <c>radio.lua</c> (DCS install,
/// <c>Mods\terrains\&lt;id&gt;\radio.lua</c>): entries with <c>radioId = 'airfield&lt;N&gt;_0'</c>,
/// where N is the airbase id the server reports, and a <c>frequency</c> table of
/// <c>[BAND] = {MODULATIONTYPE_AM, Hz}</c>.</summary>
public static partial class AirfieldRadios
{
    [GeneratedRegex(@"radioId\s*=\s*'airfield(\d+)_\d+'")]
    private static partial Regex RadioId();

    [GeneratedRegex(@"\[(HF|UHF|VHF_HI|VHF_LOW)\]\s*=\s*\{\s*MODULATIONTYPE_(AM|FM)\s*,\s*([0-9.]+)\s*\}")]
    private static partial Regex Frequency();

    private static readonly string[] BandOrder = ["UHF", "VHF high", "VHF low", "HF"];

    public static Dictionary<int, IReadOnlyList<AirfieldFrequency>> Parse(string radioLua)
    {
        var result = new Dictionary<int, IReadOnlyList<AirfieldFrequency>>();
        var ids = RadioId().Matches(radioLua);
        for (int k = 0; k < ids.Count; k++)
        {
            // The entry runs to the next radioId (its frequency line follows its id).
            var start = ids[k].Index;
            var end = k + 1 < ids.Count ? ids[k + 1].Index : radioLua.Length;
            var block = radioLua.AsSpan(start, end - start).ToString();
            var freqs = Frequency().Matches(block)
                .Select(m => new AirfieldFrequency(BandName(m.Groups[1].Value),
                    double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) / 1e6, m.Groups[2].Value))
                .OrderBy(f => Array.IndexOf(BandOrder, f.Band))
                .ToList();
            if (freqs.Count > 0) result[int.Parse(ids[k].Groups[1].Value, CultureInfo.InvariantCulture)] = freqs;
        }
        return result;
    }

    private static string BandName(string lua) => lua switch
    {
        "VHF_HI" => "VHF high",
        "VHF_LOW" => "VHF low",
        _ => lua,
    };
}
