namespace Dcb.Client.Core;

public enum Identity { Friendly, Hostile, Unknown }

public enum SymbolKind { Aircraft, Helicopter, Ground, Ship }

public enum LabelLevel { Full, Short, None }

/// <summary>Style B symbol rules (docs/PLAN.md, phase 2): what shape, colour and data block
/// a unit gets. Pure functions so the map, the object list and tests agree.</summary>
public static class Symbology
{
    /// <summary>Vertical speed beyond which the block shows a climb/descent arrow (~500 ft/min).</summary>
    public const double TrendThresholdMps = 2.5;

    /// <summary>Own side is friendly. A radar contact is hostile once DCS has identified its
    /// type, unknown until then.</summary>
    public static Identity IdentityOf(Unit u, string ownCoalition) =>
        u.Coalition == ownCoalition && !u.Detected ? Identity.Friendly
        : u.Type is null ? Identity.Unknown
        : Identity.Hostile;

    public static SymbolKind KindOf(Unit u) => u.Category switch
    {
        "airplane" => SymbolKind.Aircraft,
        "helicopter" => SymbolKind.Helicopter,
        "ship" => SymbolKind.Ship,
        _ => SymbolKind.Ground,
    };

    public static bool IsAir(SymbolKind k) => k is SymbolKind.Aircraft or SymbolKind.Helicopter;

    /// <summary>Compact aircraft type for data blocks: "F-16C_50" → "F16C", "MiG-29 Fulcrum" → "MiG29".</summary>
    public static string ShortType(string type)
    {
        var head = type.Split(' ', '_')[0];
        return head.Replace("-", "");
    }

    /// <summary>Who to name in line 1: player nickname first, then callsign. Contacts are never named.</summary>
    public static string? NameOf(Unit u) =>
        u.Detected ? null
        : !string.IsNullOrEmpty(u.Player) ? u.Player
        : !string.IsNullOrEmpty(u.Callsign) ? u.Callsign
        : u.Name;

    public static char TrendOf(Unit u) =>
        u.Vs > TrendThresholdMps ? '↑' : u.Vs < -TrendThresholdMps ? '↓' : ' ';

    /// <summary>Altitude in hundreds of feet, as on a scope: 25,000 ft → "250".</summary>
    public static string AltitudeHundreds(Unit u) =>
        u.AltFt is { } ft ? Math.Max(0, Math.Round(ft / 100)).ToString("000") : "---";

    /// <summary>Data block lines for a unit at a label level. Empty when nothing is shown.</summary>
    public static IReadOnlyList<string> DataBlock(Unit u, LabelLevel level)
    {
        if (level == LabelLevel.None) return [];
        var kind = KindOf(u);
        if (!IsAir(kind))
        {
            // Ground and ships: one line, type only; nothing for unidentified contacts.
            return level == LabelLevel.Full && u.Type is not null ? [u.Type] : [];
        }

        var altLine = $"{AltitudeHundreds(u)}{TrendOf(u)} {u.SpdKt ?? 0:0}";
        if (level == LabelLevel.Short) return [$"{AltitudeHundreds(u)}{TrendOf(u)}"];

        var type = u.Type is null ? "UNK" : ShortType(u.Type);
        var name = NameOf(u);
        return [name is null ? type : $"{name} {type}", altLine];
    }
}
