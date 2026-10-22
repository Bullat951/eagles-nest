namespace Dcb.Client.Core;

/// <summary>How DCB uses the controller's ten SRS radios ("channels"): 6 is the ATC channel
/// (set from the ATC tab), 7–10 belong to packages 1–4, 1–5 are free.</summary>
public static class ChannelPlan
{
    public const int Count = 10, Atc = 6, FirstPackage = 7;

    /// <summary>The channel kept for package <paramref name="number"/> (1–4).</summary>
    public static int ForPackage(int number) => FirstPackage + number - 1;

    /// <summary>The package a channel belongs to, or 0.</summary>
    public static int PackageOf(int channel) => channel >= FirstPackage && channel <= Count ? channel - FirstPackage + 1 : 0;

    /// <summary>"ATC", "P1"… or "" for a free channel.</summary>
    public static string Role(int channel) => channel == Atc ? "ATC" : PackageOf(channel) is > 0 and var p ? $"P{p}" : "";

    /// <summary>Channels the controller may use for anything (1–5).</summary>
    public static IEnumerable<int> Free => Enumerable.Range(1, Count).Where(c => c != Atc && PackageOf(c) == 0);
}
