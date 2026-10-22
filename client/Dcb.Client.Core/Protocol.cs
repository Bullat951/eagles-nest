using System.Text.Json.Serialization;

namespace Dcb.Client.Core;

// Wire format v1: one JSON object per line. See docs/PLAN.md, "Protocol v1".

public sealed record AuthRequest(
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("client")] string Client)
{
    [JsonPropertyName("type")] public string Type => "auth";
}

public sealed record Message(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("coalition")] string? Coalition);

public sealed record Picture(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("coalition")] string Coalition,
    [property: JsonPropertyName("time")] double Time,
    [property: JsonPropertyName("theatre")] string? Theatre,
    [property: JsonPropertyName("units")] IReadOnlyList<Unit> Units,
    [property: JsonPropertyName("bullseye")] Bullseye? Bullseye = null,
    [property: JsonPropertyName("weapons")] IReadOnlyList<Weapon>? Weapons = null,
    [property: JsonPropertyName("carriers")] IReadOnlyList<CarrierInfo>? Carriers = null,
    [property: JsonPropertyName("events")] IReadOnlyList<CarrierEvent>? Events = null);

/// <summary>One of the coalition's carriers: unit id and type, wind 20 m above the deck (from,
/// true degrees; m/s) and the beacons the mission sets for it (TACAN "74X JCS", ICLS channel,
/// Link 4 and ship radio in MHz).</summary>
public sealed record CarrierInfo(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("windFrom")] double WindFrom,
    [property: JsonPropertyName("windMs")] double WindMs,
    [property: JsonPropertyName("tacan")] string? Tacan,
    [property: JsonPropertyName("icls")] int? Icls,
    [property: JsonPropertyName("link4")] double? Link4,
    [property: JsonPropertyName("acls")] bool Acls,
    [property: JsonPropertyName("radio")] double? Radio);

/// <summary>A landing on a carrier (DCS's landing event): unit, carrier, mission time and where
/// both were (DCS metres).</summary>
public sealed record CarrierEvent(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("unit")] long Unit,
    [property: JsonPropertyName("carrier")] long Carrier,
    [property: JsonPropertyName("time")] double Time,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("cx")] double Cx,
    [property: JsonPropertyName("cz")] double Cz);

/// <summary>The fast LSO feed (10 a second, own coalition): carriers and the aircraft near them,
/// in DCS metres (x north, y up, z east), m/s and degrees from grid north.</summary>
public sealed record LsoMessage(
    [property: JsonPropertyName("coalition")] string Coalition,
    [property: JsonPropertyName("time")] double Time,
    [property: JsonPropertyName("carriers")] IReadOnlyList<LsoShip> Carriers,
    [property: JsonPropertyName("aircraft")] IReadOnlyList<LsoAircraft> Aircraft);

public sealed record LsoShip(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("hdg")] double Hdg,
    [property: JsonPropertyName("vx")] double Vx,
    [property: JsonPropertyName("vz")] double Vz);

/// <summary>An aircraft near a carrier: position, velocity, heading, pitch, and gear/hook (0..1,
/// from the model's draw arguments 0 and 25; null when the model has none).</summary>
public sealed record LsoAircraft(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("vx")] double Vx,
    [property: JsonPropertyName("vy")] double Vy,
    [property: JsonPropertyName("vz")] double Vz,
    [property: JsonPropertyName("hdg")] double Hdg,
    [property: JsonPropertyName("pitch")] double Pitch,
    [property: JsonPropertyName("gear")] double? Gear,
    [property: JsonPropertyName("hook")] double? Hook);

/// <summary>A missile in flight: the picture's own coalition's, or an enemy one aimed at one of
/// its units (then without type or shooter). <c>Id</c> is the server's own missile number, not
/// a unit id; <c>Shooter</c> and <c>Target</c> are unit ids.</summary>
public sealed record Weapon(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("coalition")] string Coalition,
    [property: JsonPropertyName("shooter")] long? Shooter,
    [property: JsonPropertyName("target")] long? Target,
    [property: JsonPropertyName("lat")] double Lat,
    [property: JsonPropertyName("lon")] double Lon,
    [property: JsonPropertyName("alt")] double Alt,
    [property: JsonPropertyName("hdg")] double Hdg,
    [property: JsonPropertyName("spd")] double Spd,
    [property: JsonPropertyName("vs")] double Vs);

/// <summary>The coalition's own bullseye (sent only in that coalition's picture).</summary>
public sealed record Bullseye(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("lat")] double Lat,
    [property: JsonPropertyName("lon")] double Lon);

/// <summary>A unit in a coalition picture. Detected (enemy radar) contacts carry no name,
/// callsign, player or group, and a type only once DCS has identified it.</summary>
public sealed record Unit(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("callsign")] string? Callsign,
    [property: JsonPropertyName("player")] string? Player,
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("coalition")] string Coalition,
    [property: JsonPropertyName("detected")] bool Detected,
    [property: JsonPropertyName("lat")] double? Lat,
    [property: JsonPropertyName("lon")] double? Lon,
    [property: JsonPropertyName("alt")] double? Alt,
    [property: JsonPropertyName("hdg")] double? Hdg,
    [property: JsonPropertyName("spd")] double? Spd,
    [property: JsonPropertyName("vs")] double? Vs = null,
    [property: JsonPropertyName("emitter")] bool Emitter = false)
{
    public const double FeetPerMeter = 3.28084;
    public const double KnotsPerMps = 1.943844;

    public double? AltFt => Alt * FeetPerMeter;
    public double? SpdKt => Spd * KnotsPerMps;
}
