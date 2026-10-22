using SkiaSharp;

namespace Dcb.Client.Map.Gpu;

/// <summary>Map labels: Skia text with an optional halo (a wide stroke under the fill), placed
/// by their top-left corner like WPF text. Fonts come from the shared style.</summary>
public static class Text
{
    public enum Face { Regular, Bold, Mono }

    private static readonly SKTypeface Regular = SKTypeface.FromFamilyName(MapStyle.Text("fonts.regular"));
    private static readonly SKTypeface Bold = SKTypeface.FromFamilyName(MapStyle.Text("fonts.bold"), SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
    private static readonly SKTypeface Mono = SKTypeface.FromFamilyName(MapStyle.Text("fonts.mono"));
    private static readonly float HaloWidth = MapStyle.Number("fonts.haloWidth");

    private static readonly Dictionary<(Face, float), SKFont> Fonts = [];
    private static readonly Dictionary<(string, Face, float), float> Widths = [];

    private static readonly SKPaint FillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint HaloPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round };

    public static Face FaceOf(string styleName) => styleName switch { "bold" => Face.Bold, "mono" => Face.Mono, _ => Face.Regular };

    public static SKFont Font(Face face, float size)
    {
        if (!Fonts.TryGetValue((face, size), out var f))
        {
            var tf = face switch { Face.Bold => Bold, Face.Mono => Mono, _ => Regular };
            Fonts[(face, size)] = f = new SKFont(tf, size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        }
        return f;
    }

    /// <summary>Width and line height of a label (DIPs).</summary>
    public static (float Width, float Height) Measure(string text, Face face, float size)
    {
        var font = Font(face, size);
        var key = (text, face, size);
        if (!Widths.TryGetValue(key, out var w))
        {
            if (Widths.Count > 20_000) Widths.Clear();  // live values churn; start over
            Widths[key] = w = font.MeasureText(text);
        }
        return (w, font.Spacing);
    }

    /// <summary>Draws a label with its top-left corner at (x, y); returns its size.</summary>
    public static (float Width, float Height) Draw(SKCanvas canvas, string text, float x, float y, SKColor ink, SKColor? halo, Face face, float size)
    {
        var font = Font(face, size);
        var baseline = y - font.Metrics.Ascent;
        if (halo is { } h)
        {
            HaloPaint.Color = h.WithAlpha((byte)(h.Alpha * ink.Alpha / 255));
            HaloPaint.StrokeWidth = HaloWidth;
            canvas.DrawText(text, x, baseline, font, HaloPaint);
        }
        FillPaint.Color = ink;
        canvas.DrawText(text, x, baseline, font, FillPaint);
        return Measure(text, face, size);
    }

    /// <summary>Draws a label centred horizontally on x, top at y.</summary>
    public static (float Width, float Height) DrawCentered(SKCanvas canvas, string text, float x, float y, SKColor ink, SKColor? halo, Face face, float size)
    {
        var (w, _) = Measure(text, face, size);
        return Draw(canvas, text, x - w / 2, y, ink, halo, face, size);
    }

    /// <summary>A short note in the map's bottom-left corner (progress, missing data).</summary>
    public static void Note(SKCanvas canvas, MapView view, string text, bool light = true)
    {
        var (w, h) = Measure(text, Face.Regular, 12);
        var y = (float)view.Height - h - 10;
        canvas.DrawRect(8, y - 2, w + 8, h + 4, Ink.Fill(light ? new SKColor(0xFF, 0xFF, 0xFF, 0xB0) : new SKColor(0, 0, 0, 0xB0)));
        Draw(canvas, text, 12, y, light ? SKColors.Black : SKColors.White, null, Face.Regular, 12);
    }
}
