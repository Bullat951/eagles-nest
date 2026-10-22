using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dcb.Client.Core;
using Dcb.Client.Map.Charts;

namespace Dcb.Client.Map.Airfields;

/// <summary>Airport-diagram pictures (e.g. a kneeboard book's pages) placed on the map:
/// <c>diagrams.json</c> in the folder, written by tools/DiagramIndexer, maps each page to an
/// airfield and gives the page-pixel → DCS-metre transform. Local files only.</summary>
public sealed class AirfieldDiagrams
{
    public sealed class Entry
    {
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("airfieldId")] public int? AirfieldId { get; set; }
        [JsonPropertyName("transform")] public double[]? Transform { get; set; }
        [JsonPropertyName("variant")] public string Variant { get; set; } = "day";
    }

    private sealed class IndexFile
    {
        [JsonPropertyName("theatre")] public string Theatre { get; set; } = "";
        [JsonPropertyName("diagrams")] public List<Entry> Diagrams { get; set; } = [];
    }

    // Page area inside the chart frame (fractions of width/height): the header, footer and
    // frame of these kneeboard pages stay out of the map.
    private const double ClipLeft = 0.055, ClipTop = 0.055, ClipRight = 0.955, ClipBottom = 0.94;

    private readonly string _folder;
    private readonly Dictionary<int, List<Entry>> _byAirfield;

    private AirfieldDiagrams(string folder, IEnumerable<Entry> entries)
    {
        _folder = folder;
        _byAirfield = entries.Where(e => e.AirfieldId is not null && e.Transform is { Length: 6 })
            .GroupBy(e => e.AirfieldId!.Value).ToDictionary(g => g.Key, g => g.ToList());
    }

    public bool Has(int airfieldId) => _byAirfield.ContainsKey(airfieldId);

    public Entry? For(int airfieldId, bool night) =>
        _byAirfield.TryGetValue(airfieldId, out var list)
            ? list.FirstOrDefault(e => e.Variant == (night ? "night" : "day")) ?? list.FirstOrDefault()
            : null;

    private static readonly Dictionary<string, AirfieldDiagrams?> Cache = [];

    /// <summary>The diagram set configured for the theatre (Window → Map data), or null.</summary>
    public static AirfieldDiagrams? For(Theatre theatre)
    {
        if (!AppState.Settings.Diagrams.TryGetValue(theatre.Id, out var folder)) return null;
        lock (Cache)
        {
            if (!Cache.TryGetValue(folder, out var set))
            {
                try
                {
                    var index = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(Path.Combine(folder, "diagrams.json")));
                    set = index is not null && index.Theatre == theatre.Id ? new AirfieldDiagrams(folder, index.Diagrams) : null;
                }
                catch (Exception)
                {
                    set = null;  // missing or unreadable index: no diagrams
                }
                Cache[folder] = set;
            }
            return set;
        }
    }

    public static void Forget(string folder)
    {
        lock (Cache) Cache.Remove(folder);
    }

    /// <summary>Draws the page clipped to its chart area, page background made transparent so
    /// only the drawing sits on the map. Null image (still loading) draws nothing.</summary>
    public void Draw(SkiaSharp.SKCanvas canvas, MapView view, Theatre theatre, Entry entry, float opacity)
    {
        var path = Path.Combine(_folder, entry.File);
        var image = TileCache.Get("diagram:" + path, () =>
        {
            var (px, w, h) = LoadTransparentPixels(path);
            return Gpu.SkDraw.FromBgra(px, w, h);
        });
        if (image is null || entry.Transform is not { } t) return;
        double w = image.Width, h = image.Height;
        SkiaSharp.SKPoint Screen(double px, double py) =>
            Gpu.SkDraw.Screen(view, theatre, t[0] * px + t[1] * py + t[2], t[3] * px + t[4] * py + t[5]);
        var tl = Screen(0, 0);
        var tr = Screen(w, 0);
        var bl = Screen(0, h);
        var m = new SkiaSharp.SKMatrix((tr.X - tl.X) / (float)w, (bl.X - tl.X) / (float)h, tl.X, (tr.Y - tl.Y) / (float)w, (bl.Y - tl.Y) / (float)h, tl.Y, 0, 0, 1);
        canvas.Save();
        canvas.Concat(in m);
        canvas.ClipRect(new SkiaSharp.SKRect((float)(ClipLeft * w), (float)(ClipTop * h), (float)(ClipRight * w), (float)(ClipBottom * h)));
        canvas.DrawImage(image, 0, 0, Gpu.Ink.Smooth, Gpu.Ink.Image(opacity));
        canvas.Restore();
    }

    /// <summary>Loads a page and turns its background colour (sampled inside the frame, near
    /// the top-left corner) transparent.</summary>
    private static (byte[] Bgra, int W, int H) LoadTransparentPixels(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight;
        var px = new byte[w * h * 4];
        source.CopyPixels(px, w * 4, 0);
        var o0 = ((int)(h * 0.12) * w + (int)(w * 0.08)) * 4;
        int bb = px[o0], bg = px[o0 + 1], br = px[o0 + 2];
        for (int o = 0; o < px.Length; o += 4)
        {
            var d = Math.Abs(px[o] - bb) + Math.Abs(px[o + 1] - bg) + Math.Abs(px[o + 2] - br);
            if (d < 36) px[o + 3] = 0;
            else if (d < 72) px[o + 3] = (byte)((d - 36) * 255 / 36);  // soften the antialiased edge
        }
        return (px, w, h);
    }
}
