using System.Text.RegularExpressions;

namespace Dcb.Client.Core;

/// <summary>A DCS World install found on this PC: its folder, how it was found, its terrains.</summary>
public sealed record DcsCandidate(string Path, string Source, IReadOnlyList<string> Terrains);

/// <summary>Finding DCS World without asking: the rules that need no registry (Steam's library
/// list, the usual install folders, telling a real install from any folder).</summary>
public static class DcsLocator
{
    /// <summary>Steam's app id for DCS World; its folder under steamapps\common is "DCSWorld".</summary>
    public const int SteamAppId = 223750;

    /// <summary>A DCS World install: the game executable and the terrains folder.</summary>
    public static bool IsDcsFolder(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
        && (File.Exists(System.IO.Path.Combine(path, "bin", "DCS.exe")) || File.Exists(System.IO.Path.Combine(path, "bin-mt", "DCS.exe")))
        && Directory.Exists(System.IO.Path.Combine(path, "Mods", "terrains"));

    /// <summary>The terrains an install has (folder names under Mods\terrains).</summary>
    public static IReadOnlyList<string> Terrains(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(System.IO.Path.Combine(path, "Mods", "terrains"))
                .Select(d => System.IO.Path.GetFileName(d)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Library folders from Steam's <c>steamapps\libraryfolders.vdf</c> ("path" entries,
    /// backslashes escaped as in the file).</summary>
    public static IReadOnlyList<string> SteamLibraries(string vdf) =>
        Regex.Matches(vdf, "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"")
            .Select(m => m.Groups[1].Value.Replace("\\\\", "\\"))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Where DCS usually is under a drive root (installer defaults, Steam libraries, and
    /// what people commonly choose).</summary>
    public static IEnumerable<string> UsualFolders(string driveRoot)
    {
        foreach (var rel in new[]
                 {
                     @"DCS World", @"DCS World OpenBeta", @"DCS",
                     @"Program Files\Eagle Dynamics\DCS World", @"Program Files\Eagle Dynamics\DCS World OpenBeta",
                     @"Eagle Dynamics\DCS World", @"Eagle Dynamics\DCS World OpenBeta",
                     @"Games\DCS World", @"Games\DCS World OpenBeta", @"Games\Eagle Dynamics\DCS World",
                     @"SteamLibrary\steamapps\common\DCSWorld", @"Steam\steamapps\common\DCSWorld",
                     @"Program Files (x86)\Steam\steamapps\common\DCSWorld", @"Program Files\Steam\steamapps\common\DCSWorld",
                 })
            yield return System.IO.Path.Combine(driveRoot, rel);
    }

    /// <summary>The DCS folder inside a Steam library.</summary>
    public static string InSteamLibrary(string library) => System.IO.Path.Combine(library, "steamapps", "common", "DCSWorld");

    /// <summary>Real installs among the given (path, source) guesses, each folder once (first
    /// source wins), in the order given.</summary>
    public static IReadOnlyList<DcsCandidate> Verify(IEnumerable<(string Path, string Source)> guesses)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<DcsCandidate>();
        foreach (var (path, source) in guesses)
        {
            string full;
            try { full = System.IO.Path.GetFullPath(path).TrimEnd('\\'); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!seen.Add(full) || !IsDcsFolder(full)) continue;
            list.Add(new DcsCandidate(full, source, Terrains(full)));
        }
        return list;
    }
}
