using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dcb.Client.Map.Charts;
using Microsoft.Win32.SafeHandles;

namespace Dcb.Client.Map.Satellite;

/// <summary>One DCS terrain clipmap file: the ground's satellite colour texture for one block,
/// <c>clipmaps\colortexture\&lt;R&gt;m\&lt;R&gt;m[x]&lt;AA&gt;&lt;col&gt;.tif.clipmap</c>.
/// A block is 8192 px at R metres per pixel; its own pyramid has a few levels, each a grid of
/// 256 px tiles stored zlib-compressed as raw BC3 (docs/DCS_MAP_FORMAT.md, "clipmap").
/// The images are not north-up: columns run north (DCS +x), rows run east (DCS +z) — the
/// one orientation that matches DCS's own water mask (correlation 0.82; the others ≤ 0.08).</summary>
public sealed partial class ClipmapFile
{
    public const int BlockPixels = 8192;
    public const int TilePixels = 256;
    /// <summary>The solid fill DCS stores where a block has no imagery: RGB565 0x8B8B, which
    /// decodes to (140, 113, 90).</summary>
    private const byte FillR = 140, FillG = 113, FillB = 90;

    private static bool NearFill(ushort c)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        r = (r << 3) | (r >> 2);
        g = (g << 2) | (g >> 4);
        b = (b << 3) | (b >> 2);
        return Math.Abs(r - FillR) <= 8 && Math.Abs(g - FillG) <= 8 && Math.Abs(b - FillB) <= 8;
    }

    public string Path { get; }
    public int MetersPerPixel { get; }
    public int Row { get; }   // block index along DCS x (north)
    public int Col { get; }   // block index along DCS z (east)

    /// <summary>The block's south-west corner and size in DCS metres.</summary>
    public double SouthX => Row * BlockMeters;
    public double WestZ => Col * BlockMeters;
    public double BlockMeters => (double)BlockPixels * MetersPerPixel;

    private Level[]? _levels;
    private SafeFileHandle? _handle;

    /// <summary>One pyramid level: tile grid and the file offset of each tile (0 = none),
    /// indexed [north * side + east] counting from the south-west tile.</summary>
    public sealed record Level(int TilesPerSide, long[] Offsets);

    private ClipmapFile(string path, int mpp, int row, int col) => (Path, MetersPerPixel, Row, Col) = (path, mpp, row, col);

    [GeneratedRegex(@"^(\d+)m(x?)([a-z])([a-z])(-?\d+)\.tif\.clipmap$", RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();

    /// <summary>"4mxAF-33.tif.clipmap" → 4 m/px, row −5, col −33. Letters count the row from
    /// AA = 0 (AB = 1 … BA = 26); an "x" prefix makes it negative (south of the origin).</summary>
    public static ClipmapFile? FromPath(string path)
    {
        var m = NamePattern().Match(System.IO.Path.GetFileName(path));
        if (!m.Success) return null;
        var letters = (char.ToUpperInvariant(m.Groups[3].Value[0]) - 'A') * 26 + (char.ToUpperInvariant(m.Groups[4].Value[0]) - 'A');
        var row = m.Groups[2].Value.Length > 0 ? -letters : letters;
        return new ClipmapFile(path, int.Parse(m.Groups[1].Value), row, int.Parse(m.Groups[5].Value));
    }

    /// <summary>The pyramid levels, read from the header on first use (thread-safe).</summary>
    public IReadOnlyList<Level> Levels
    {
        get
        {
            if (_levels is not null) return _levels;
            lock (this)
            {
                return _levels ??= ReadLevels();
            }
        }
    }

    /// <summary>Raised on a worker when <see cref="LevelsIfReady"/> finished reading a header.</summary>
    public static event Action? LevelsLoaded;
    private int _loading;

    /// <summary>The pyramid levels if already read; otherwise starts reading them on a worker
    /// and returns null (a map frame must not wait for dozens of file headers).</summary>
    public IReadOnlyList<Level>? LevelsIfReady()
    {
        if (_levels is not null) return _levels;
        if (Interlocked.Exchange(ref _loading, 1) == 0)
        {
            Task.Run(() =>
            {
                try
                {
                    _ = Levels;
                    LevelsLoaded?.Invoke();
                }
                catch (Exception)
                {
                    // Unreadable file: it stays unread and its block is left to coarser imagery.
                }
            });
        }
        return null;
    }

    private Level[] ReadLevels()
    {
        // Header: u32 version, f32 m/px, u32 tile px, u32 n + format ("bc3"), u8, u32 levels;
        // then per level: u32 1, u64 0, u32 tile px, u32 n, and n runs of u32 count and
        // count × (u32 present, u64 offset). Run k holds the tiles k steps north of the south
        // edge, west → east (assembling level 4's 2×2 so reproduces level 5; checked on
        // Syria 16mAA00 and against the water mask).
        using var s = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var r = new BinaryReader(s);
        if (r.ReadUInt32() != 3) throw new InvalidDataException("unknown clipmap version");
        r.ReadSingle();
        if (r.ReadUInt32() != TilePixels) throw new InvalidDataException("unexpected tile size");
        var format = new string(r.ReadChars(r.ReadInt32()));
        if (!format.Equals("bc3", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"clipmap format {format}");
        r.ReadByte();
        var count = r.ReadInt32();
        var levels = new Level[count];
        for (int l = 0; l < count; l++)
        {
            r.ReadUInt32();
            r.ReadUInt64();
            if (r.ReadUInt32() != TilePixels) throw new InvalidDataException("unexpected tile size");
            var side = r.ReadInt32();
            var offsets = new long[side * side];
            for (int north = 0; north < side; north++)
            {
                var n = r.ReadInt32();
                for (int east = 0; east < n; east++)
                {
                    var present = r.ReadUInt32();
                    var offset = (long)r.ReadUInt64();
                    if (east < side) offsets[north * side + east] = present != 0 ? offset : 0;
                }
            }
            levels[l] = new Level(side, offsets);
        }
        return levels;
    }

    /// <summary>Decodes a tile of a level (counted from the south-west tile). The bitmap is as
    /// stored: columns north, rows east; "no imagery" fill blocks are transparent.</summary>
    public SkiaSharp.SKImage ReadTile(int level, int north, int east) =>
        Gpu.SkDraw.FromBgra(ReadBgra(level, north, east), TilePixels, TilePixels);

    private byte[] ReadBgra(int level, int north, int east)
    {
        var lv = Levels[level];
        var offset = lv.Offsets[north * lv.TilesPerSide + east];
        if (offset == 0) throw new FileNotFoundException("no tile");
        SafeFileHandle handle;
        lock (this) handle = _handle ??= File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        Span<byte> len = stackalloc byte[4];
        RandomAccess.Read(handle, len, offset);
        var compressed = new byte[BitConverter.ToInt32(len)];
        RandomAccess.Read(handle, compressed, offset + 4);
        var raw = new byte[TilePixels * TilePixels];  // BC3: one byte per pixel
        using (var z = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress))
            z.ReadExactly(raw);
        var bgra = Dds.DecodeBc3(raw, 0, TilePixels, TilePixels);
        // Alpha is always opaque; the no-imagery fill becomes transparent so coarser imagery
        // underneath shows through. Whole 4×4 blocks whose two colour endpoints are both (near)
        // fill — the coarser pyramid levels re-encode fill a step off the exact value — and,
        // at the edges of the imagery where fill and image share a block, exact fill pixels.
        // Single near-fill pixels stay: desert is this colour.
        // A block with one fill endpoint blends fill into the image (coastlines): its 2-bit
        // indices give each pixel's share of fill (index 0 = c0, 1 = c1, 2 = ⅔c0+⅓c1,
        // 3 = ⅓c0+⅔c1), which becomes transparency over the other endpoint's colour.
        ReadOnlySpan<byte> shareOfC0 = [255, 0, 170, 85];
        const int blocksPerRow = TilePixels / 4;
        for (int b = 0; b < blocksPerRow * blocksPerRow; b++)
        {
            ushort c0 = BitConverter.ToUInt16(raw, b * 16 + 8), c1 = BitConverter.ToUInt16(raw, b * 16 + 10);
            bool f0 = NearFill(c0), f1 = NearFill(c1);
            if (!f0 && !f1) continue;
            int bx = b % blocksPerRow * 4, by = b / blocksPerRow * 4;
            uint indices = BitConverter.ToUInt32(raw, b * 16 + 12);
            var keep = f0 ? c1 : c0;
            byte kr = (byte)(((keep >> 11) & 31) << 3), kg = (byte)(((keep >> 5) & 63) << 2), kb = (byte)((keep & 31) << 3);
            for (int p = 0; p < 16; p++)
            {
                var o = ((by + p / 4) * TilePixels + bx + p % 4) * 4;
                if (f0 && f1)
                {
                    bgra[o + 3] = 0;
                    continue;
                }
                var c0Share = shareOfC0[(int)(indices >> (2 * p)) & 3];
                var fillShare = f0 ? c0Share : 255 - c0Share;
                if (fillShare == 0) continue;
                bgra[o] = kb;
                bgra[o + 1] = kg;
                bgra[o + 2] = kr;
                bgra[o + 3] = (byte)(255 - fillShare);
            }
        }
        for (int k = 0; k < bgra.Length; k += 4)
            if (bgra[k] == FillB && bgra[k + 1] == FillG && bgra[k + 2] == FillR) bgra[k + 3] = 0;
        return bgra;
    }
}

/// <summary>The satellite colour clipmaps a DCS install has for one terrain (default season),
/// read in place from the local install — never copied or exported.</summary>
public sealed class ClipmapIndex
{
    public IReadOnlyList<ClipmapFile> Files { get; }
    /// <summary>Resolutions present, coarsest first (e.g. 128, 16, 8, 4, 1).</summary>
    public IReadOnlyList<int> Resolutions { get; }

    private ClipmapIndex(List<ClipmapFile> files)
    {
        Files = files;
        Resolutions = files.Select(f => f.MetersPerPixel).Distinct().OrderDescending().ToList();
    }

    private static readonly Dictionary<string, ClipmapIndex?> Cache = [];

    public static ClipmapIndex? For(string terrainId)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(terrainId, out var cached)) return cached;
            ClipmapIndex? index = null;
            try
            {
                if (DcsInstall.Find() is { } dcs)
                {
                    var dir = System.IO.Path.Combine(dcs, "Mods", "terrains", terrainId, "clipmaps", "colortexture");
                    if (Directory.Exists(dir))
                    {
                        // "16m", "4m"… — not the "(spring)"/"(winter)" variants.
                        var files = Directory.EnumerateDirectories(dir)
                            .Where(d => !System.IO.Path.GetFileName(d).Contains('('))
                            .SelectMany(d => Directory.EnumerateFiles(d, "*.clipmap"))
                            .Select(ClipmapFile.FromPath)
                            .OfType<ClipmapFile>()
                            .ToList();
                        if (files.Count > 0) index = new ClipmapIndex(files);
                    }
                }
            }
            catch (Exception)
            {
                index = null;  // unknown layout: no satellite map rather than a crash
            }
            Cache[terrainId] = index;
            return index;
        }
    }
}
