using SkiaSharp;

namespace Dcb.Client.Map.Gpu;

/// <summary>Clips shapes to the view before Skia strokes them. Zoomed in, zone circles, threat
/// rings and the theatre border are hundreds of thousands of DIPs across; dashing such a path
/// in full costs CPU on every frame even on the GPU path. Only the visible part is drawn, and
/// dash patterns keep their phase so they do not crawl while the map moves.</summary>
public static class SkClip
{
    /// <summary>Circles smaller than this (DIP radius) are drawn as plain circles.</summary>
    private const float PlainRadius = 1500;
    private const float Pad = 20;

    /// <summary>The area drawn into: the view plus a margin for pen widths.</summary>
    public static SKRect Area(MapView view) => new(-Pad, -Pad, (float)view.Width + Pad, (float)view.Height + Pad);

    public static void Circle(SKCanvas canvas, SKColor? fill, Ink.Pen? pen, SKPoint c, float r, SKRect area)
    {
        // Nearest and farthest distances from the centre to the area.
        float nx = Math.Clamp(c.X, area.Left, area.Right) - c.X, ny = Math.Clamp(c.Y, area.Top, area.Bottom) - c.Y;
        var near = MathF.Sqrt(nx * nx + ny * ny);
        float fx = Math.Max(Math.Abs(c.X - area.Left), Math.Abs(c.X - area.Right));
        float fy = Math.Max(Math.Abs(c.Y - area.Top), Math.Abs(c.Y - area.Bottom));
        var far = MathF.Sqrt(fx * fx + fy * fy);
        if (near > r) return;                          // entirely outside the view
        if (far < r)                                   // the view is inside the circle
        {
            if (fill is { } f) canvas.DrawRect(area, Ink.Fill(f));
            return;
        }
        if (r <= PlainRadius || area.Contains(c))
        {
            if (fill is { } f) canvas.DrawCircle(c, r, Ink.Fill(f));
            if (pen is { } p) canvas.DrawCircle(c, r, p.Paint());
            return;
        }

        // Big circle crossing the view, centre outside it: only the arc over the view.
        var mid = Math.Atan2(area.MidY - c.Y, area.MidX - c.X);
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var q in new[] { new SKPoint(area.Left, area.Top), new SKPoint(area.Right, area.Top), new SKPoint(area.Left, area.Bottom), new SKPoint(area.Right, area.Bottom) })
        {
            var a = Math.IEEERemainder(Math.Atan2(q.Y - c.Y, q.X - c.X) - mid, 2 * Math.PI);
            lo = Math.Min(lo, a);
            hi = Math.Max(hi, a);
        }
        lo += mid;
        hi += mid;
        var step = Math.Min(0.05, 40 / r);  // chords this long sag well under a pixel here
        var n = Math.Max(2, (int)Math.Ceiling((hi - lo) / step) + 1);
        var arc = new SKPoint[n];
        for (int i = 0; i < n; i++)
        {
            var a = lo + (hi - lo) * i / (n - 1);
            arc[i] = new SKPoint((float)(c.X + r * Math.Cos(a)), (float)(c.Y + r * Math.Sin(a)));
        }
        if (fill is { } fillColor)
        {
            var pie = new List<SKPoint>(n + 1) { c };
            pie.AddRange(arc);
            Polygon(canvas, ClipPolygon(pie, area), Ink.Fill(fillColor));
        }
        if (pen is { } pen2)
        {
            using var path = new SKPath();
            path.AddPoly(arc, close: false);
            // Arc length from angle 0 keeps the dashes where a full circle would put them.
            var start = (lo % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) * r;
            canvas.DrawPath(path, pen2.WithPhase(start).Paint());
        }
    }

    /// <summary>Draws the part of segment a–b inside the area, dashes in phase with the whole line.</summary>
    public static void Line(SKCanvas canvas, Ink.Pen pen, SKPoint a, SKPoint b, SKRect area, double startLength = 0)
    {
        if (!ClipSegment(a, b, area, out var t0, out var t1)) return;
        var d = b - a;
        var p0 = a + new SKPoint(d.X * t0, d.Y * t0);
        var p1 = a + new SKPoint(d.X * t1, d.Y * t1);
        canvas.DrawLine(p0, p1, pen.WithPhase(startLength + d.Length * t0).Paint());
    }

    /// <summary>Liang–Barsky: the parameter range [t0, t1] of a + t(b − a) inside the area.</summary>
    public static bool ClipSegment(SKPoint a, SKPoint b, SKRect area, out float t0, out float t1)
    {
        t0 = 0;
        t1 = 1;
        float dx = b.X - a.X, dy = b.Y - a.Y;
        Span<float> p = [-dx, dx, -dy, dy];
        Span<float> q = [a.X - area.Left, area.Right - a.X, a.Y - area.Top, area.Bottom - a.Y];
        for (int i = 0; i < 4; i++)
        {
            if (p[i] == 0)
            {
                if (q[i] < 0) return false;
                continue;
            }
            var t = q[i] / p[i];
            if (p[i] < 0) t0 = Math.Max(t0, t);
            else t1 = Math.Min(t1, t);
            if (t0 > t1) return false;
        }
        return true;
    }

    /// <summary>Sutherland–Hodgman clip of a polygon to a rectangle.</summary>
    public static List<SKPoint> ClipPolygon(IReadOnlyList<SKPoint> polygon, SKRect area)
    {
        var output = polygon.ToList();
        for (int edge = 0; edge < 4 && output.Count > 0; edge++)
        {
            var input = output;
            output = new List<SKPoint>(input.Count + 4);
            for (int i = 0; i < input.Count; i++)
            {
                SKPoint cur = input[i], prev = input[(i + input.Count - 1) % input.Count];
                bool curIn = Inside(cur, edge, area), prevIn = Inside(prev, edge, area);
                if (curIn != prevIn) output.Add(Cross(prev, cur, edge, area));
                if (curIn) output.Add(cur);
            }
        }
        return output;
    }

    private static bool Inside(SKPoint p, int edge, SKRect a) => edge switch
    {
        0 => p.X >= a.Left,
        1 => p.X <= a.Right,
        2 => p.Y >= a.Top,
        _ => p.Y <= a.Bottom,
    };

    private static SKPoint Cross(SKPoint a, SKPoint b, int edge, SKRect area)
    {
        // Exact intersection with the edge's line (a and b lie on opposite sides of it).
        float t = edge switch
        {
            0 => (area.Left - a.X) / (b.X - a.X),
            1 => (area.Right - a.X) / (b.X - a.X),
            2 => (area.Top - a.Y) / (b.Y - a.Y),
            _ => (area.Bottom - a.Y) / (b.Y - a.Y),
        };
        return new SKPoint(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
    }

    public static void Polygon(SKCanvas canvas, IReadOnlyList<SKPoint> points, SKPaint paint)
    {
        if (points.Count < 3) return;
        using var path = new SKPath();
        path.AddPoly(points as SKPoint[] ?? points.ToArray(), close: true);
        canvas.DrawPath(path, paint);
    }
}
