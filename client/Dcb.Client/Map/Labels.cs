using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Dcb.Client.Map;

/// <summary>Map labels with a halo, built once as frozen drawings and re-positioned on each
/// redraw. Building text and halo geometry was most of a redraw's cost while zooming.</summary>
public static class Labels
{
    private const int MaxEntries = 6000;
    private static readonly Dictionary<(string, Brush, Brush?, double, bool), (Drawing Drawing, Size Size)> Cache = [];
    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface Mono = new("Consolas");

    public enum Font { Regular, Bold, Mono }

    /// <summary>Size of a label without drawing it.</summary>
    public static Size Measure(string text, Brush brush, Brush? halo, double size, Font font, double pixelsPerDip) =>
        Get(text, brush, halo, size, font, pixelsPerDip).Size;

    /// <summary>Draws a label with its top-left corner at <paramref name="at"/>.</summary>
    public static Size Draw(DrawingContext dc, string text, Point at, Brush brush, Brush? halo, double size, Font font, double pixelsPerDip)
    {
        var (drawing, labelSize) = Get(text, brush, halo, size, font, pixelsPerDip);
        dc.PushTransform(new TranslateTransform(at.X, at.Y));
        dc.DrawDrawing(drawing);
        dc.Pop();
        return labelSize;
    }

    private static (Drawing Drawing, Size Size) Get(string text, Brush brush, Brush? halo, double size, Font font, double pixelsPerDip)
    {
        var key = (text, brush, halo, size, font == Font.Bold);
        if (font == Font.Mono) key.Item4 = -size;  // distinct key space for the mono face
        if (Cache.TryGetValue(key, out var hit)) return hit;

        var typeface = font switch { Font.Bold => Bold, Font.Mono => Mono, _ => Regular };
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            if (halo is not null)
            {
                var pen = new Pen(halo, 3) { LineJoin = PenLineJoin.Round };
                pen.Freeze();
                dc.DrawGeometry(null, pen, ft.BuildGeometry(new Point(0, 0)));
            }
            dc.DrawText(ft, new Point(0, 0));
        }
        group.Freeze();
        if (Cache.Count >= MaxEntries) Cache.Clear();  // labels with live values churn; start over
        var entry = ((Drawing)group, new Size(ft.WidthIncludingTrailingWhitespace, ft.Height));
        Cache[key] = entry;
        return entry;
    }
}
