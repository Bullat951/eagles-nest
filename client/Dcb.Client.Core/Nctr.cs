using System.Text.Json.Serialization;

namespace Dcb.Client.Core;

/// <summary>The "nctr" message: the coalition's NCTR requests (running, or done in the last two
/// minutes) with a result per contact and method. <c>Area</c>: x1, z1, x2, z2 in DCS metres.</summary>
public sealed record NctrMessage(
    [property: JsonPropertyName("coalition")] string Coalition,
    [property: JsonPropertyName("requests")] IReadOnlyList<NctrRequest> Requests);

public sealed record NctrRequest(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("time")] double Time,
    [property: JsonPropertyName("left")] double Left,
    [property: JsonPropertyName("area")] IReadOnlyList<double> Area,
    [property: JsonPropertyName("methods")] IReadOnlyList<string> Methods,
    [property: JsonPropertyName("radars")] int Radars,
    [property: JsonPropertyName("results")] IReadOnlyList<NctrResult> Results,
    [property: JsonPropertyName("cones")] IReadOnlyList<NctrCone>? Cones = null)
{
    public bool Running => State == "running";
}

/// <summary>The cone a radar taking part looks along while a request runs: from its position (DCS
/// metres), centre bearing and half-width in degrees from grid north, out to <c>Range</c> metres.
/// Contacts inside keep updating; the rest that radar alone holds freeze.</summary>
public sealed record NctrCone(
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("center")] double Center,
    [property: JsonPropertyName("half")] double Half,
    [property: JsonPropertyName("range")] double Range);

/// <summary>One contact and method: <c>Status</c> "pending", "ok" (<c>Result</c>, found by
/// <c>Platform</c>) or "none" (<c>Reason</c>: the nearest miss, "CVN-74: aspect 48 deg"; a pending
/// one may carry why it is not working yet). <c>Active</c>: a radar is working it right now.</summary>
public sealed record NctrResult(
    [property: JsonPropertyName("unit")] long Unit,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("active")] bool Active = false);

public static class NctrMethods
{
    /// <summary>The methods in the order the menus show them, with their names.</summary>
    public static readonly IReadOnlyList<(string Id, string Name, string Hint)> All =
    [
        ("jem", "JEM", "jet engine modulation: engine family; nose or tail aspect, ≤ 40 NM (60 big radars)"),
        ("hrrp", "HRRP", "range profile: type; AWACS, F-15/16/18 only, ≤ 25 NM (50 AWACS)"),
        ("isar", "ISAR", "radar image: type; ships, or aircraft turning > 15° in 5 s"),
        ("size", "Size", "radar size class: small / medium / large; any radar"),
        ("rotor", "Rotor", "rotor modulation: type; helicopters only, ≤ 20 NM"),
        ("raid", "Raid", "raid count: how many aircraft in one contact; modern radars, ≤ 30 NM (50 big)"),
    ];

    public static string NameOf(string id) => All.FirstOrDefault(m => m.Id == id).Name ?? id.ToUpperInvariant();

    /// <summary>An NCTR result as an identity label (letters, digits, '-', '/', spaces, ≤ 24):
    /// "Su-27 family (AL-31F)" → "Su-27 family", "F-16C_50" → "F-16C 50"; null for size classes
    /// and raid counts (hints, not identities).</summary>
    public static string? LabelOf(NctrResult r)
    {
        if (r.Result is not { } text || r.Method is "size" or "raid") return null;
        var cut = text.IndexOf(" (", StringComparison.Ordinal);
        if (cut > 0) text = text[..cut];
        var clean = new string(text.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '/' or ' ' ? ch : ' ').ToArray()).Trim();
        while (clean.Contains("  ")) clean = clean.Replace("  ", " ");
        return clean.Length > 24 ? clean[..24].TrimEnd() : clean.Length == 0 ? null : clean;
    }
}
