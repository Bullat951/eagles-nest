namespace Dcb.Client.Core;

/// <summary>RWR labels as the server's RWR library gives them (collector.lua RWR_LABELS), the
/// threat each stands for on the ground, and which emitters a radar contact may be.</summary>
public static class RwrLabels
{
    /// <summary>Labels for the Identify menu, by group (aircraft, SAM and radars, ships).</summary>
    public static readonly IReadOnlyList<(string Group, IReadOnlyList<string> Labels)> Groups =
    [
        ("Aircraft", ["29", "30", "34", "24", "31", "25", "23", "21", "19", "JF", "10", "14", "15", "16", "18", "F4", "F5", "M2", "F1",
            "RF", "EF", "TO", "AV", "50", "KJ", "E3", "E2", "22", "95", "142", "160", "64", "28", "52"]),
        ("SAM and radars", ["2", "3", "6", "8", "10", "11", "15", "19", "SD", "BB", "CS", "A", "P", "HA", "HK", "RO", "RA", "NS", "HQ", "S"]),
        ("Ships", ["SH"]),
    ];

    private static readonly Dictionary<string, string> GroundThreat = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2"] = "SA-2", ["3"] = "SA-3", ["6"] = "SA-6", ["8"] = "SA-8", ["10"] = "SA-10", ["11"] = "SA-11",
        ["15"] = "SA-15", ["19"] = "SA-19", ["SD"] = "SA-11", ["BB"] = "SA-10", ["CS"] = "SA-10", ["A"] = "AAA",
        ["P"] = "Patriot", ["HA"] = "Hawk", ["HK"] = "Hawk", ["NS"] = "NASAMS", ["RO"] = "Roland", ["RA"] = "Rapier", ["HQ"] = "HQ-7",
    };

    /// <summary>The threat (with its ring) an identity stands for on a ground unit or ship: an RWR
    /// label ("6" → SA-6) or a threat name ("SA-11", "Patriot"). Aircraft labels share digits with
    /// SAMs ("15": F-15 or SA-15), so airborne contacts never get one.</summary>
    public static ThreatType? ThreatFor(string? label, string category)
    {
        if (string.IsNullOrWhiteSpace(label) || category is "airplane" or "helicopter") return null;
        return GroundThreat.TryGetValue(label.Trim(), out var name) ? ThreatType.Find(name) : ThreatType.Parse(label);
    }

    private static string Group(string category) => category switch
    {
        "airplane" or "helicopter" => "air",
        "ship" => "ship",
        _ => "ground",
    };

    /// <summary>An RWR label that fits a contact: heard by <c>Receivers</c> own units whose
    /// bearings pass within their error of it, <c>Off</c> the mean miss in degrees.</summary>
    public sealed record Candidate(string Label, string Category, int Receivers, double Off);

    /// <summary>The emitters whose bearings (from every own receiver in the RWR picture) pass
    /// through the contact, within each receiver's error (+2°), of the contact's kind (airborne,
    /// ground, ship); grouped by label, most receivers and nearest first.</summary>
    public static IReadOnlyList<Candidate> Candidates(Unit contact, RwrMessage? rwr, Func<long, Unit?> find, Func<double, double, double> gridToTrue)
    {
        if (rwr is null || contact.Lat is not { } cLat || contact.Lon is not { } cLon) return [];
        var kind = Group(contact.Category);
        var hits = new List<(string Label, string Category, long Receiver, double Off)>();
        foreach (var r in rwr.Receivers)
        {
            if (find(r.Id) is not { Lat: { } lat, Lon: { } lon }) continue;
            var toContact = Nav.BearingRange(lat, lon, cLat, cLon).TrueDeg;
            var convergence = gridToTrue(lat, lon);
            foreach (var e in r.Emitters)
            {
                if (Group(e.Category) != kind) continue;
                var off = Math.Abs(Nav.Normalize(e.Bearing + convergence - toContact + 180) - 180);
                if (off <= e.Err + 2) hits.Add((e.Label, e.Category, r.Id, off));
            }
        }
        return hits.GroupBy(h => h.Label)
            .Select(g => new Candidate(g.Key, g.First().Category, g.Select(h => h.Receiver).Distinct().Count(), g.Average(h => h.Off)))
            .OrderByDescending(c => c.Receivers).ThenBy(c => c.Off)
            .ToList();
    }
}
