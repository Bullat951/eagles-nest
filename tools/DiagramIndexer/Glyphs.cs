using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DiagramIndexer;

/// <summary>Reads the 5–6 px glyphs inside chart badges by template matching: OCR engines
/// skip lone characters that small. Reference glyphs (A–Z, 0–9 in a few common sans fonts)
/// are rendered once; a glyph and every template are reduced to the same height-normalised
/// grid and compared by correlation.</summary>
public static class Glyphs
{
    private const int Grid = 14;
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digits = "0123456789";

    private static readonly Lazy<List<(char Ch, double[] Cells, double Aspect)>> Templates = new(Build);

    /// <summary>Best character for an ink map (weights 0..1, row-major) and its correlation.</summary>
    public static (char Ch, double Score) Classify(double[] ink, int width, int height, bool lettersOnly)
    {
        if (Normalise(ink, width, height) is not { } g) return ('?', 0);
        var best = ('?', double.MinValue);
        foreach (var (ch, cells, aspect) in Templates.Value)
        {
            if (lettersOnly && !char.IsLetter(ch)) continue;
            // Aspect must roughly agree ("I" is narrow, "M" wide); correlation does the rest.
            var aspectPenalty = Math.Abs(Math.Log(aspect / g.Aspect)) * 0.25;
            var score = Correlate(g.Cells, cells) - aspectPenalty;
            if (score > best.Item2) best = (ch, score);
        }
        return best;
    }

    /// <summary>The ink's bounding box scaled to Grid rows tall (aspect kept, centred), by
    /// bilinear sampling. Null when there is no ink.</summary>
    private static (double[] Cells, double Aspect)? Normalise(double[] ink, int width, int height)
    {
        int x0 = width, y0 = height, x1 = -1, y1 = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (ink[y * width + x] < 0.35) continue;
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        if (x1 < 0) return null;
        double bw = x1 - x0 + 1, bh = y1 - y0 + 1;
        var cells = new double[Grid * Grid];
        var scale = bh / Grid;                       // source pixels per cell
        var offset = (Grid - bw / scale) / 2;       // centre horizontally
        for (int gy = 0; gy < Grid; gy++)
        for (int gx = 0; gx < Grid; gx++)
        {
            var sx = x0 + (gx - offset + 0.5) * scale - 0.5;
            var sy = y0 + (gy + 0.5) * scale - 0.5;
            cells[gy * Grid + gx] = Sample(ink, width, height, sx, sy);
        }
        return (cells, bw / bh);
    }

    private static double Sample(double[] img, int w, int h, double x, double y)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy;
        double At(int px, int py) => px < 0 || py < 0 || px >= w || py >= h ? 0 : img[py * w + px];
        return At(ix, iy) * (1 - fx) * (1 - fy) + At(ix + 1, iy) * fx * (1 - fy) +
               At(ix, iy + 1) * (1 - fx) * fy + At(ix + 1, iy + 1) * fx * fy;
    }

    private static double Correlate(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double da = a[i] - ma, db = b[i] - mb;
            sab += da * db; saa += da * da; sbb += db * db;
        }
        return saa < 1e-9 || sbb < 1e-9 ? 0 : sab / Math.Sqrt(saa * sbb);
    }

    /// <summary>Renders the reference glyphs small (the size the charts use, so anti-aliasing
    /// blurs them alike) on an STA thread, as WPF requires.</summary>
    private static List<(char, double[], double)> Build()
    {
        var list = new List<(char, double[], double)>();
        var thread = new Thread(() =>
        {
            string[] families = ["Arial", "Tahoma", "Segoe UI", "Verdana", "Microsoft Sans Serif"];
            FontWeight[] weights = [FontWeights.Normal, FontWeights.Bold];
            foreach (var family in families)
            foreach (var weight in weights)
            foreach (var size in new[] { 7.0, 8.0, 9.0 })
            foreach (var ch in Letters + Digits)
            {
                var typeface = new Typeface(new FontFamily(family), FontStyles.Normal, weight, FontStretches.Normal);
                var text = new FormattedText(ch.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1.0);
                int w = (int)Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 4, h = (int)Math.Ceiling(text.Height) + 4;
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                    dc.DrawText(text, new Point(2, 2));
                }
                var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(visual);
                var px = new byte[w * h * 4];
                bmp.CopyPixels(px, w * 4, 0);
                var ink = new double[w * h];
                for (int i = 0; i < w * h; i++) ink[i] = 1 - (px[4 * i] + px[4 * i + 1] + px[4 * i + 2]) / (3 * 255.0);
                if (Normalise(ink, w, h) is { } g) list.Add((ch, g.Cells, g.Aspect));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return list;
    }
}
