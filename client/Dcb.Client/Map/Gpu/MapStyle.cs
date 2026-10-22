using System.Globalization;
using System.Text.Json;
using SkiaSharp;

namespace Dcb.Client.Map.Gpu;

/// <summary>The shared map style (repository <c>style/map-style.json</c>, embedded): colours,
/// widths, dashes, fonts and label sizes, the same file the website reads. Looked up by dotted
/// path ("zones.link.width"); a missing entry is a programming error and throws at start-up.</summary>
public static class MapStyle
{
    private static readonly JsonElement Root = Load();

    private static JsonElement Load()
    {
        using var stream = typeof(MapStyle).Assembly.GetManifestResourceStream("map-style.json")
            ?? throw new InvalidOperationException("map-style.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    private static JsonElement At(string path)
    {
        var e = Root;
        foreach (var part in path.Split('.'))
            if (!e.TryGetProperty(part, out e)) throw new KeyNotFoundException("map-style.json: " + path);
        return e;
    }

    public static float Number(string path) => At(path).GetSingle();

    public static string Text(string path) => At(path).GetString() ?? "";

    public static float[] Dash(string path) => At(path).EnumerateArray().Select(v => v.GetSingle()).ToArray();

    public static double[] Numbers(string path) => At(path).EnumerateArray().Select(v => v.GetDouble()).ToArray();

    /// <summary>A CSS hex colour (#RRGGBB or #RRGGBBAA) at a path.</summary>
    public static SKColor Color(string path) => ParseColor(Text(path));

    public static SKColor ParseColor(string css)
    {
        var hex = css.TrimStart('#');
        var v = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return hex.Length switch
        {
            6 => new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v),
            8 => new SKColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v),
            _ => throw new FormatException("colour " + css),
        };
    }
}
