using SkiaSharp;

namespace Dcb.Client.Map.Gpu;

/// <summary>Shared Skia paints: created once per colour/width/dash and reused by every frame.
/// Paints are only touched on the UI thread (the surface draws there).</summary>
public static class Ink
{
    private static readonly Dictionary<SKColor, SKPaint> Fills = [];
    private static readonly Dictionary<(SKColor, float, bool), SKPaint> Strokes = [];

    public static SKPaint Fill(SKColor color)
    {
        if (!Fills.TryGetValue(color, out var p))
            Fills[color] = p = new SKPaint { Color = color, Style = SKPaintStyle.Fill, IsAntialias = true };
        return p;
    }

    /// <summary>A solid stroke; <paramref name="round"/> gives round caps and joins.</summary>
    public static SKPaint Stroke(SKColor color, float width, bool round = false)
    {
        var key = (color, width, round);
        if (!Strokes.TryGetValue(key, out var p))
        {
            Strokes[key] = p = new SKPaint
            {
                Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = width, IsAntialias = true,
                StrokeCap = round ? SKStrokeCap.Round : SKStrokeCap.Butt,
                StrokeJoin = round ? SKStrokeJoin.Round : SKStrokeJoin.Miter,
            };
        }
        return p;
    }

    /// <summary>A dashed stroke. Its pattern starts <paramref name="phase"/> DIPs along it, so a
    /// line clipped to the view keeps its dashes where the whole line would have them.</summary>
    public static Pen Dashed(SKColor color, float width, float[] dash, double phase = 0) => new(color, width, dash, phase);

    /// <summary>A stroke description for drawing helpers: solid when <see cref="Dash"/> is null.</summary>
    public readonly record struct Pen(SKColor Color, float Width, float[]? Dash = null, double Phase = 0, bool Round = false)
    {
        public Pen WithPhase(double phase) => this with { Phase = Phase + phase };

        /// <summary>Applies the pen to a scratch paint (dashes need a per-use phase).</summary>
        public SKPaint Paint()
        {
            if (Dash is null) return Stroke(Color, Width, Round);
            var period = Dash.Sum();
            var p = Scratch;
            p.Color = Color;
            p.StrokeWidth = Width;
            p.StrokeCap = Round ? SKStrokeCap.Round : SKStrokeCap.Butt;
            p.PathEffect?.Dispose();
            p.PathEffect = SKPathEffect.CreateDash(Dash, (float)(Phase % period));
            return p;
        }
    }

    private static readonly SKPaint Scratch = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };

    public static SKPaint Image(float opacity = 1)
    {
        ImagePaint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(opacity * 255, 0, 255));
        return ImagePaint;
    }

    private static readonly SKPaint ImagePaint = new() { IsAntialias = false };

    /// <summary>Linear filtering, no mipmaps: every tile layer picks the level whose pixels match
    /// the screen (at most 2:1), and building mipmaps for each newly uploaded tile cost tens of
    /// milliseconds in a zoom that brings in a new level.</summary>
    public static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.None);

    public static SKColor WithOpacity(SKColor c, double opacity) => c.WithAlpha((byte)Math.Clamp(c.Alpha * opacity, 0, 255));
}
