using System.IO;
using Dcb.Client.Core;
using Dcb.Client.Map.Charts;

namespace Dcb.Client.Map;

/// <summary>The terrain's land-cover grid (LandCover, from surface5), extracted once at 100 m
/// (~11 s for Syria) and kept on disk under %LocalAppData%\DCB\cache, keyed by the surface
/// file's size and date. Loaded by the DCS Map tile renderer while it renders.</summary>
public static class LandCoverCache
{
    public const double Cell = 100;

    /// <summary>The terrain's surface5 file, or null when this PC has none.</summary>
    public static string? SurfacePath(string terrainId) =>
        DcsInstall.Find() is { } dcs && Path.Combine(dcs, "Mods", "terrains", terrainId, "surface", terrainId + ".surface5") is var p && File.Exists(p) ? p : null;

    /// <summary>Loads (or extracts and caches) the grid over the theatre; null when unavailable.</summary>
    public static LandCover? Load(Theatre theatre)
    {
        try
        {
            if (SurfacePath(theatre.Id) is not { } surface) return null;
            var info = new FileInfo(surface);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DCB", "cache");
            var cache = Path.Combine(dir, $"landcover_{theatre.Id}_{info.Length}_{info.LastWriteTimeUtc.Ticks}_{Cell:0}_v5.bin");  // v5: every shader
            if (File.Exists(cache))
            {
                try
                {
                    using var c = File.OpenRead(cache);
                    return LandCover.Load(c);
                }
                catch (Exception)
                {
                    // Damaged cache: extract again below.
                }
            }
            var lc = LandCover.Extract(surface, theatre.X0, theatre.Z0, theatre.X1, theatre.Z1, Cell);
            Directory.CreateDirectory(dir);
            // Older caches go; one another client still reads is left for next time.
            foreach (var old in Directory.EnumerateFiles(dir, $"landcover_{theatre.Id}_*.bin"))
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            using (var c = File.Create(cache)) lc.Save(c);
            return lc;
        }
        catch (Exception)
        {
            return null;  // unknown terrain file layout: the DCS Map without area fills
        }
    }
}
