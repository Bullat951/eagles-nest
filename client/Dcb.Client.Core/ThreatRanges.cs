namespace Dcb.Client.Core;

/// <summary>Approximate maximum engagement ranges of SAM/AAA systems by DCS type name, for
/// threat rings. Ballpark values for situational awareness, not weapon-model data.</summary>
public static class ThreatRanges
{
    private static readonly Dictionary<string, double> RangeKm = new(StringComparer.Ordinal)
    {
        // SA-2 / SA-3
        ["SNR_75V"] = 40, ["S_75M_Volhov"] = 40,
        ["snr s-125 tr"] = 18, ["5p73 s-125 ln"] = 18,
        // SA-6
        ["Kub 1S91 str"] = 25, ["Kub 2P25 ln"] = 25,
        // SA-8 / SA-15 / SA-19 / SA-13 / SA-9
        ["Osa 9A33 ln"] = 10, ["Tor 9A331"] = 12, ["2S6 Tunguska"] = 8,
        ["Strela-10M3"] = 5, ["Strela-1 9P31"] = 4,
        // SA-11
        ["SA-11 Buk SR 9S18M1"] = 35, ["SA-11 Buk LN 9A310M1"] = 35, ["SA-11 Buk CC 9S470M1"] = 35,
        // SA-10
        ["S-300PS 40B6M tr"] = 75, ["S-300PS 5P85C ln"] = 75, ["S-300PS 5P85D ln"] = 75,
        // AAA
        ["ZSU-23-4 Shilka"] = 2.5, ["ZSU_57_2"] = 3,
        // Western
        ["Patriot str"] = 100, ["Patriot ln"] = 100,
        ["Hawk tr"] = 40, ["Hawk ln"] = 40,
        ["Roland ADS"] = 8, ["rapier_fsa_launcher"] = 7,
        ["NASAMS_LN_C"] = 25, ["HQ-7_LN_SP"] = 12, ["HQ-7_STR_SP"] = 12,
    };

    /// <summary>Range in metres, or null when the type has no threat ring.</summary>
    public static double? MetersFor(string? type) =>
        type is not null && RangeKm.TryGetValue(type, out var km) ? km * 1000 : null;
}
