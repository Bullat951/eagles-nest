using System.Text.Json.Serialization;

namespace Dcb.Client.Core;

/// <summary>A runway from DCS's Airbase:getRunways(): centre in DCS map metres, size in metres.
/// DCS gives the course in radians with the opposite sign of the true heading.</summary>
public sealed record Runway(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("course")] double Course,
    [property: JsonPropertyName("length")] double Length,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z)
{
    /// <summary>True heading of the runway direction named by <see cref="Name"/>, degrees.</summary>
    public double HeadingTrue => ((-Course * 180 / Math.PI) % 360 + 360) % 360;

    /// <summary>Designators for both ends, e.g. "10" → ("10", "28"), "05L" → ("05L", "23R").</summary>
    public (string Near, string Far) Designators
    {
        get
        {
            var digits = new string(Name.TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var n)) return (Name, "");
            var suffix = Name[digits.Length..].Trim().ToUpperInvariant();
            var opposite = suffix switch { "L" => "R", "R" => "L", _ => suffix };
            var far = (n + 18 - 1) % 36 + 1;
            return ($"{n:00}{suffix}", $"{far:00}{opposite}");
        }
    }
}

/// <summary>A parking spot from Airbase:getParking(): number, position, DCS terminal type.</summary>
public sealed record ParkingSpot(int Number, double X, double Z, int Type)
{
    /// <summary>DCS Term_Type 40 = helicopter only; 68 hardened shelter; 72/104 open/airliner; 100 small.</summary>
    public bool HelicopterOnly => Type == 40;
}

/// <summary>An airdrome or helipad (ships are tracked in the picture instead).</summary>
public sealed record Airfield(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] int Category,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("coalition")] int Coalition,
    [property: JsonPropertyName("runways")] IReadOnlyList<Runway> Runways,
    [property: JsonPropertyName("parking")] IReadOnlyList<double[]> ParkingRaw)
{
    private IReadOnlyList<ParkingSpot>? _parking;

    /// <summary>Wire format is compact arrays: [number, x, z, type].</summary>
    public IReadOnlyList<ParkingSpot> Parking => _parking ??= ParkingRaw
        .Where(p => p.Length >= 4)
        .Select(p => new ParkingSpot((int)p[0], p[1], p[2], (int)p[3]))
        .ToList();

    public bool IsHelipad => Category == 1;

    /// <summary>DCS coalition: 0 neutral, 1 red, 2 blue.</summary>
    public string Owner => Coalition switch { 1 => "red", 2 => "blue", _ => "neutral" };
}

public sealed record AirfieldsMessage(
    [property: JsonPropertyName("airfields")] IReadOnlyList<Airfield> Airfields);
