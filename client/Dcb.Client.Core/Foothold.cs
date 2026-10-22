using System.Text.Json.Serialization;

namespace Dcb.Client.Core;

/// <summary>A Foothold campaign zone (Leka's Foothold, read from its BattleCommander). Position
/// and radius are the zone's trigger zone in DCS map metres.</summary>
public sealed record FootholdZone(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("side")] int Side,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("r")] double Radius,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("suspended")] bool Suspended,
    [property: JsonPropertyName("airbase")] string? Airbase,
    [property: JsonPropertyName("facility")] string? Facility,
    [property: JsonPropertyName("logistic")] bool Logistic,
    [property: JsonPropertyName("built")] int Built,
    [property: JsonPropertyName("max")] int Max,
    [property: JsonPropertyName("wpt")] int? Waypoint)
{
    /// <summary>Foothold sides: 0 neutral, 1 red, 2 blue.</summary>
    public string Owner => Side switch { 1 => "red", 2 => "blue", _ => "neutral" };

    /// <summary>Groups present out of the owner's upgrade slots, 0..1 (null when unknown).</summary>
    public double? Strength => Max > 0 ? Math.Min(1.0, (double)Built / Max) : null;
}

public sealed record FootholdState(
    [property: JsonPropertyName("zones")] IReadOnlyList<FootholdZone> Zones,
    [property: JsonPropertyName("links")] IReadOnlyList<string[]> Links)
{
    public FootholdZone? Find(string name) => Zones.FirstOrDefault(z => z.Name == name);
}
