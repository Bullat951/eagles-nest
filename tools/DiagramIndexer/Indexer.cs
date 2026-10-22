using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dcb.Client.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace DiagramIndexer;

/// <summary>Output (also read by the client: keep in sync with Dcb.Client's DiagramIndex).</summary>
public sealed class DiagramIndex
{
    [JsonPropertyName("theatre")] public string Theatre { get; set; } = "";
    [JsonPropertyName("diagrams")] public List<DiagramEntry> Diagrams { get; set; } = [];
}

public sealed class DiagramEntry
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("icao")] public string? Icao { get; set; }
    [JsonPropertyName("airfield")] public string? Airfield { get; set; }
    [JsonPropertyName("airfieldId")] public int? AirfieldId { get; set; }
    [JsonPropertyName("nameScore")] public double NameScore { get; set; }
    [JsonPropertyName("bars")] public int Bars { get; set; }
    /// <summary>DCS x = a·px + b·py + c, z = d·px + e·py + f (px right, py down).</summary>
    [JsonPropertyName("transform")] public double[]? Transform { get; set; }
    [JsonPropertyName("residualM")] public double ResidualM { get; set; }
    [JsonPropertyName("metersPerPixel")] public double MetersPerPixel { get; set; }
    [JsonPropertyName("rotationDeg")] public double RotationDeg { get; set; }
    /// <summary>"day" (default) or "night" (NVG-green page with the same layout).</summary>
    [JsonPropertyName("variant")] public string Variant { get; set; } = "day";
    [JsonPropertyName("sameLayoutAs")] public string? SameLayoutAs { get; set; }
    [JsonPropertyName("runwaysUsed")] public int RunwaysUsed { get; set; }
    /// <summary>Chart names placed in DCS metres (day pages only).</summary>
    [JsonPropertyName("labels")] public List<ChartLabel>? Labels { get; set; }
    [JsonPropertyName("tower")] public string? Tower { get; set; }
}

/// <summary>Kind "taxiway" (letter disc) or "area" (name of a part of the field).</summary>
public sealed record ChartLabel(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z);

public static class Aliases
{
    /// <summary>ICAO (as OCR reads it) → DCS airfield name, from aliases.json next to the exe.</summary>
    public static IReadOnlyDictionary<string, string> For(string theatre)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "aliases.json");
        if (!File.Exists(path)) return new Dictionary<string, string>();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty(theatre, out var t)
            ? t.EnumerateObject().ToDictionary(p => Matcher.NormalizeIcao(p.Name), p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>();
    }
}

public sealed record Page(string Path, int Width, int Height, byte[] Bgra, SoftwareBitmap Bitmap)
{
    public static async Task<Page> Load(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(System.IO.Path.GetFullPath(path));
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return new Page(path, (int)decoder.PixelWidth, (int)decoder.PixelHeight, pixels.DetachPixelData(), bitmap);
    }
}

public static class Ocr
{
    private static readonly OcrEngine Engine =
        OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
        ?? OcrEngine.TryCreateFromUserProfileLanguages()
        ?? throw new InvalidOperationException("Windows OCR is not available");

    public static async Task<List<(double X, double Y, string Text)>> Lines(Page page)
    {
        var result = await Engine.RecognizeAsync(page.Bitmap);
        return result.Lines.Select(l => (l.Words[0].BoundingRect.X, l.Words[0].BoundingRect.Y, l.Text)).ToList();
    }

    /// <summary>Phrases with their boxes in page pixels, read from the page decoded at
    /// <paramref name="scale"/>× (the chart text is a few pixels tall at 1×). OCR joins
    /// labels that happen to share a row into one line: a phrase ends where the gap to the
    /// next word is wider than about two spaces.</summary>
    public static async Task<List<(Windows.Foundation.Rect Box, string Text)>> LinesScaled(string path, double scale)
    {
        var file = await StorageFile.GetFileFromPathAsync(System.IO.Path.GetFullPath(path));
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        scale = Math.Min(scale, OcrEngine.MaxImageDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)(decoder.PixelWidth * scale),
            ScaledHeight = (uint)(decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Cubic,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await Engine.RecognizeAsync(bitmap);
        var phrases = new List<(Windows.Foundation.Rect Box, string Text)>();
        foreach (var line in result.Lines)
        {
            Windows.Foundation.Rect? box = null;
            var text = "";
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                if (box is { } b && r.Left - b.Right > 0.6 * Math.Max(b.Height, r.Height))
                {
                    phrases.Add((Unscale(b), text));
                    box = null;
                }
                if (box is { } open)
                {
                    open.Union(r);
                    box = open;
                    text += " " + word.Text;
                }
                else
                {
                    box = r;
                    text = word.Text;
                }
            }
            if (box is { } last) phrases.Add((Unscale(last), text));
        }
        return phrases;

        Windows.Foundation.Rect Unscale(Windows.Foundation.Rect r) => new(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);
    }

}

public sealed record Title(string Name, string? Icao);

/// <summary>The page header (top right) reads e.g. "ALEPPO INTERNATIONAL (OSAP)".</summary>
public static partial class Titles
{
    [GeneratedRegex(@"^(?<name>.+?)\s*\((?<icao>[A-Z0-9 ]{3,7})\)")]
    private static partial Regex WithIcao();

    public static Title? Find(List<(double X, double Y, string Text)> lines)
    {
        foreach (var l in lines.Where(l => l.Y < 40).OrderBy(l => l.Y))
        {
            var m = WithIcao().Match(l.Text.Trim());
            if (m.Success) return new Title(m.Groups["name"].Value.Trim(), m.Groups["icao"].Value);
        }
        var top = lines.Where(l => l.Y < 40 && l.X > 300 && !l.Text.Contains("DIAGRAM")).OrderBy(l => l.Y).FirstOrDefault();
        return top.Text is { Length: > 0 } t ? new Title(t.Trim(), null) : null;
    }
}

/// <summary>A runway drawn as a thick black bar: centre, unit axis and half-length in pixels.</summary>
public sealed record Bar(double Cx, double Cy, double Ux, double Uy, double HalfLength, double Thickness)
{
    /// <summary>Bar direction as a compass angle on a north-up page, 0..180 (runways are two-way).</summary>
    public double Bearing180 => ((Math.Atan2(Ux, -Uy) * 180 / Math.PI) % 180 + 180) % 180;
    public (double X, double Y) End(int sign) => (Cx + sign * Ux * HalfLength, Cy + sign * Uy * HalfLength);
    public override string ToString() => $"centre ({Cx:F0},{Cy:F0}) bearing {Bearing180:F1}° length {2 * HalfLength:F0}px thick {Thickness:F1}px";
}

public static class RunwayBars
{
    /// <summary>Connected components of near-black pixels that are long, straight and a few
    /// pixels thick: the runway bars. Text, taxiway letter discs and thin leader lines fail
    /// the size, elongation or thickness tests.</summary>
    public static List<Bar> Find(Page page)
    {
        int w = page.Width, h = page.Height;
        var black = new bool[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int b = page.Bgra[4 * i], g = page.Bgra[4 * i + 1], r = page.Bgra[4 * i + 2];
            black[i] = r < 45 && g < 45 && b < 45;
        }
        var seen = new bool[w * h];
        var bars = new List<Bar>();
        var stack = new Stack<int>();
        var pts = new List<int>();
        for (int start = 0; start < w * h; start++)
        {
            if (!black[start] || seen[start]) continue;
            pts.Clear();
            stack.Push(start);
            seen[start] = true;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                pts.Add(p);
                int px = p % w, py = p / w;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = px + dx, ny = py + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    var n = ny * w + nx;
                    if (black[n] && !seen[n]) { seen[n] = true; stack.Push(n); }
                }
            }
            if (pts.Count < 400) continue;
            // Principal axis of the component.
            double mx = 0, my = 0;
            foreach (var p in pts) { mx += p % w; my += p / w; }
            mx /= pts.Count; my /= pts.Count;
            double sxx = 0, syy = 0, sxy = 0;
            foreach (var p in pts)
            {
                double dx = p % w - mx, dy = p / w - my;
                sxx += dx * dx; syy += dy * dy; sxy += dx * dy;
            }
            var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            double ux = Math.Cos(angle), uy = Math.Sin(angle);
            double min = double.MaxValue, max = double.MinValue, amin = double.MaxValue, amax = double.MinValue;
            foreach (var p in pts)
            {
                double dx = p % w - mx, dy = p / w - my;
                var t = dx * ux + dy * uy;
                var across = -dx * uy + dy * ux;
                min = Math.Min(min, t); max = Math.Max(max, t);
                amin = Math.Min(amin, across); amax = Math.Max(amax, across);
            }
            var length = max - min;
            var thickness = pts.Count / Math.Max(1, length);
            if (length < 120 || thickness < 5 || thickness > 30 || length / thickness < 8) continue;
            // A solid bar is as wide across as it is thick on average; outlines (the page frame)
            // and crossing shapes are much wider.
            if (amax - amin > 2.5 * thickness + 6) continue;
            var c = (min + max) / 2;
            bars.Add(new Bar(mx + ux * c, my + uy * c, ux, uy, length / 2, thickness));
        }
        return MergeCollinear(bars);
    }

    /// <summary>A taxiway or label drawn across a runway splits its bar in two: join pieces
    /// with the same bearing that lie on one line into a single bar spanning both.</summary>
    private static List<Bar> MergeCollinear(List<Bar> bars)
    {
        var merged = true;
        while (merged)
        {
            merged = false;
            for (int i = 0; i < bars.Count && !merged; i++)
            for (int j = i + 1; j < bars.Count && !merged; j++)
            {
                Bar a = bars[i], b = bars[j];
                if (Matcher.AngleDiff(a.Bearing180, b.Bearing180) > 2) continue;
                // Perpendicular distance of b's centre from a's axis.
                double dx = b.Cx - a.Cx, dy = b.Cy - a.Cy;
                if (Math.Abs(-dx * a.Uy + dy * a.Ux) > Math.Max(a.Thickness, b.Thickness)) continue;
                // Extent of both along a's axis.
                double[] ts =
                [
                    -a.HalfLength, a.HalfLength,
                    dx * a.Ux + dy * a.Uy - b.HalfLength, dx * a.Ux + dy * a.Uy + b.HalfLength,
                ];
                double t0 = ts.Min(), t1 = ts.Max(), mid = (t0 + t1) / 2;
                bars[i] = new Bar(a.Cx + a.Ux * mid, a.Cy + a.Uy * mid, a.Ux, a.Uy, (t1 - t0) / 2, (a.Thickness + b.Thickness) / 2);
                bars.RemoveAt(j);
                merged = true;
            }
        }
        return bars;
    }
}

/// <summary>Names on a placed chart: taxiway letters (white on a black disc), parking-group
/// badges (white on blue), area names, and the tower frequencies from the box top left.</summary>
public static partial class ChartText
{
    public sealed record Badge(int X0, int Y0, int X1, int Y1, int[] RowMin, int[] RowMax)
    {
        public double Cx => (X0 + X1) / 2.0;
        public double Cy => (Y0 + Y1) / 2.0;
    }

    private static readonly string[] NotNames =
    [
        "ELEV", "MHZ", "CHAN", "RATE", "CHANGE", "ANNUAL", "EFFECTIVE", "DCS", "DIAGRAM", "ATIS",
        "APPROACH", "NAVIGATION", "JULY", "JUNE", "USAF", "ICAO", "CAUTION",
    ];

    /// <summary>VHF/UHF only: the HF and FM entries (4.625, 40.15) are read unreliably.</summary>
    [GeneratedRegex(@"\b\d{3}\.\d{1,3}")]
    private static partial Regex Frequency();

    [GeneratedRegex(@"^[A-Z][A-Z .'/]+$")]
    private static partial Regex Words();

    /// <summary>Plain words, no figures, no chart furniture; a lone word must be a real one
    /// (OCR fragments such as "CHA" or "LEV" are short).</summary>
    private static bool IsName(string text) =>
        text.Length >= 5 && Words().IsMatch(text) && !NotNames.Any(text.Contains);

    /// <summary>Weakest glyph correlation accepted for a taxiway letter (true reads score
    /// 0.75–0.95, symbols mistaken for discs mostly below).</summary>
    public const double MinScore = 0.74;

    [GeneratedRegex(@"^[A-Z]{1,2}\d?$")]
    private static partial Regex TaxiwayName();

    public static async Task<(List<ChartLabel> Labels, string? Tower)> Read(Page page, List<Bar> bars, double[] t, Airfield field)
    {
        var labels = new List<ChartLabel>();
        (double X, double Z) ToDcs(double px, double py) => (t[0] * px + t[1] * py + t[2], t[3] * px + t[4] * py + t[5]);
        // Text far from the field is legend, sea or terrain: keep what lies within 1.5 km of
        // a runway end or a parking spot.
        var anchors = field.Runways.SelectMany(r =>
        {
            var h = r.HeadingTrue * Math.PI / 180;
            double dx = Math.Cos(h) * r.Length / 2, dz = Math.Sin(h) * r.Length / 2;
            return new[] { (r.X + dx, r.Z + dz), (r.X, r.Z), (r.X - dx, r.Z - dz) };
        }).Concat(field.Parking.Select(p => (p.X, p.Z))).ToList();
        bool Near((double X, double Z) p) => anchors.Any(a => (a.Item1 - p.X) * (a.Item1 - p.X) + (a.Item2 - p.Z) * (a.Item2 - p.Z) < 1500 * 1500);

        foreach (var badge in Badges(page, bars))
        {
            if (ReadBadge(page, badge) is not { Score: >= MinScore } read || !TaxiwayName().IsMatch(read.Text)) continue;
            var p = ToDcs(badge.Cx, badge.Cy);
            if (Near(p)) labels.Add(new ChartLabel(read.Text, "taxiway", Math.Round(p.X, 1), Math.Round(p.Z, 1)));
        }

        var lines = await Ocr.LinesScaled(page.Path, 3);
        string? tower = null;
        if (lines.FirstOrDefault(l => l.Text.StartsWith("TOWER", StringComparison.OrdinalIgnoreCase)) is { Text: not null } tl)
        {
            // The box lists the frequencies right-aligned, the first beside the word TOWER.
            var freqs = lines.Where(l => l.Box.Top >= tl.Box.Top - 2 && l.Box.Top < tl.Box.Bottom + 60 &&
                                         l.Box.Left >= tl.Box.Left - 10 && l.Box.Left < tl.Box.Left + 150)
                .OrderBy(l => l.Box.Top)
                .Select(l => Frequency().Match(l.Text))
                .Where(m => m.Success)
                .Select(m => m.Value.Replace(',', '.'))
                .ToList();
            if (freqs.Count > 0) tower = string.Join(" ", freqs);
        }

        // Inside the chart frame only (not the header, footer or the side margins).
        var names = lines
            .Where(l => l.Box.Top > page.Height * 0.06 && l.Box.Bottom < page.Height * 0.93 &&
                        l.Box.Left > page.Width * 0.04 && l.Box.Right < page.Width * 0.96)
            .Select(l => (l.Box, Text: l.Text.Trim().ToUpperInvariant()))
            .OrderBy(l => l.Box.Top)
            .ToList();
        // Names stacked over two or three lines ("MILITARY / AIRCRAFT / SHELTERS"): join each
        // line with the one right below it and centred under it, then judge the whole name
        // ("FIELD / ELEV" is not a name, "MAIN / TOWER" is).
        var used = new bool[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            if (used[i]) continue;
            var (box, text) = names[i];
            var last = box;
            for (int j = i + 1; j < names.Count; j++)
            {
                var b = names[j].Box;
                var gap = b.Top - last.Bottom;
                if (used[j] || gap < -last.Height * 0.3) continue;
                if (gap > last.Height * 0.8) break;
                if (Math.Abs((b.Left + b.Right) / 2 - (last.Left + last.Right) / 2) > Math.Max(b.Width, last.Width) / 2) continue;
                text += " " + names[j].Text;
                box.Union(b);
                last = b;
                used[j] = true;
            }
            if (!IsName(text)) continue;
            var p = ToDcs(box.Left + box.Width / 2, box.Top + box.Height / 2);
            if (Near(p)) labels.Add(new ChartLabel(text, "area", Math.Round(p.X, 1), Math.Round(p.Z, 1)));
        }
        return (labels, tower);
    }

    /// <summary>A disc's letters by template matching, glyph by glyph, with the weakest glyph's
    /// score; null when no glyph is found.</summary>
    public static (string Text, double Score)? ReadBadge(Page page, Badge b)
    {
        // Spans may reach one pixel past the disc's black shape on either side.
        int left = b.X0 - 1, bw = b.X1 - b.X0 + 3, bh = b.Y1 - b.Y0 + 1;
        // Ink = how light a pixel is, inside the disc's row spans (the rim excluded).
        var ink = new double[bw * bh];
        for (int y = 0; y < bh; y++)
        for (int x = b.RowMin[y] + 1; x < b.RowMax[y]; x++)
        {
            var i = (b.Y0 + y) * page.Width + x;
            var light = (page.Bgra[4 * i] + page.Bgra[4 * i + 1] + page.Bgra[4 * i + 2]) / 3.0;
            ink[y * bw + x - left] = Math.Clamp((light - 70) / 150, 0, 1);
        }
        if (Environment.GetEnvironmentVariable("DCB_INK") == $"{b.X0},{b.Y0}")
            for (int y = 0; y < bh; y++)
                Console.WriteLine(string.Concat(Enumerable.Range(0, bw).Select(x => " .:-=+*#%@"[(int)(ink[y * bw + x] * 9.99)])) + "|");
        // Glyphs are column runs with ink, split at empty columns. Light specks of the rim at
        // the span ends make thin runs of little ink: keep runs with at least a third of the
        // strongest run's ink.
        var columns = Enumerable.Range(0, bw).Select(x => Enumerable.Range(0, bh).Sum(y => ink[y * bw + x])).ToArray();
        var runs = new List<(int X0, int X1, double Ink)>();
        for (int x = 0; x < bw; x++)
        {
            if (columns[x] < 0.4) continue;
            var x0 = x;
            while (x + 1 < bw && columns[x + 1] >= 0.4) x++;
            runs.Add((x0, x, columns[x0..(x + 1)].Sum()));
        }
        if (runs.Count == 0) return null;
        var strongest = runs.Max(r => r.Ink);
        var text = "";
        var worst = 1.0;
        foreach (var (x0, x1, mass) in runs)
        {
            if (mass < strongest / 3) continue;
            int gw = x1 - x0 + 1;
            var glyph = new double[gw * bh];
            for (int y = 0; y < bh; y++)
            for (int gx = 0; gx < gw; gx++) glyph[y * gw + gx] = ink[y * bw + x0 + gx];
            var (ch, score) = Glyphs.Classify(glyph, gw, bh, lettersOnly: text.Length == 0);
            text += ch;
            worst = Math.Min(worst, score);
        }
        return text.Length == 0 ? null : (text, worst);
    }

    private static bool IsBlack(byte[] px, int i) => px[4 * i] < 60 && px[4 * i + 1] < 60 && px[4 * i + 2] < 60;

    private static bool IsDark(byte[] px, int i) => px[4 * i] + px[4 * i + 1] + px[4 * i + 2] < 3 * 130;

    private static bool IsLight(byte[] px, int i) => px[4 * i] > 170 && px[4 * i + 1] > 170 && px[4 * i + 2] > 170;

    /// <summary>Taxiway-letter discs: small wide black ovals with light text inside.</summary>
    public static List<Badge> Badges(Page page, IReadOnlyList<Bar> bars)
    {
        int w = page.Width, h = page.Height;
        // Discs drawn against a runway would join the runway's shape: mask the bars out.
        var seen = new bool[w * h];
        foreach (var bar in bars)
        {
            var reach = bar.HalfLength + bar.Thickness;
            int bx0 = (int)Math.Max(0, bar.Cx - reach), bx1 = (int)Math.Min(w - 1, bar.Cx + reach);
            int by0 = (int)Math.Max(0, bar.Cy - reach), by1 = (int)Math.Min(h - 1, bar.Cy + reach);
            for (int y = by0; y <= by1; y++)
            for (int x = bx0; x <= bx1; x++)
            {
                double dx = x - bar.Cx, dy = y - bar.Cy;
                if (Math.Abs(dx * bar.Ux + dy * bar.Uy) <= bar.HalfLength + 1 && Math.Abs(-dx * bar.Uy + dy * bar.Ux) <= bar.Thickness / 2 + 1)
                    seen[y * w + x] = true;
            }
        }
        var found = new List<Badge>();
        var stack = new Stack<int>();
        var pts = new List<int>();
        for (int start = 0; start < w * h; start++)
        {
            if (seen[start] || !IsBlack(page.Bgra, start)) continue;
            pts.Clear();
            stack.Push(start);
            seen[start] = true;
            int x0 = w, y0 = h, x1 = 0, y1 = 0;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                pts.Add(p);
                int px = p % w, py = p / w;
                x0 = Math.Min(x0, px); x1 = Math.Max(x1, px); y0 = Math.Min(y0, py); y1 = Math.Max(y1, py);
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = px + dx, ny = py + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    var n = ny * w + nx;
                    if (!seen[n] && IsBlack(page.Bgra, n)) { seen[n] = true; stack.Push(n); }
                }
            }
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            // Inside the chart frame only: the bold page header's letters look like discs.
            if (y0 < h * 0.06 || y1 > h * 0.93 || x0 < w * 0.04 || x1 > w * 0.96) continue;
            // Taxiway letters sit in wide ovals (15×8 to 21×12 px); round or upright dark
            // shapes are symbols (shelters, navaids, the tower).
            if (bw is < 12 or > 26 || bh is < 6 or > 16 || bw < 1.4 * bh) continue;
            var fill = pts.Count / (double)(bw * bh);
            if (fill is < 0.35 or > 0.92) continue;
            // Row spans of the disc, between its leftmost and rightmost dark pixels: the light
            // pixels inside are the text. "Dark" is loose here, and a row above and below is
            // tried: the anti-aliased rim is grey, and a letter reaching it would otherwise
            // lose its top or bottom stroke.
            int ey0 = Math.Max(0, y0 - 1), ey1 = Math.Min(h - 1, y1 + 1);
            var rowMin = new int[ey1 - ey0 + 1];
            var rowMax = new int[ey1 - ey0 + 1];
            for (int y = ey0; y <= ey1; y++)
            {
                int lo = int.MaxValue, hi = int.MinValue;
                for (int x = Math.Max(0, x0 - 1); x <= Math.Min(w - 1, x1 + 1); x++)
                {
                    if (!IsDark(page.Bgra, y * w + x)) continue;
                    lo = Math.Min(lo, x);
                    hi = Math.Max(hi, x);
                }
                // A row needs rim on both sides to have an inside.
                (rowMin[y - ey0], rowMax[y - ey0]) = hi - lo >= 3 ? (lo, hi) : (0, -1);
            }
            var light = 0;
            for (int y = ey0; y <= ey1; y++)
            for (int x = rowMin[y - ey0] + 1; x < rowMax[y - ey0]; x++)
                if (IsLight(page.Bgra, y * w + x)) light++;
            if (light >= 3) found.Add(new Badge(x0, ey0, x1, ey1, rowMin, rowMax));
        }
        return found;
    }
}

public sealed record Match(Airfield Field, double NameScore);

public static class Matcher
{
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "INTERNATIONAL", "INTL", "AIRPORT", "AIRBASE", "AB", "AIR", "BASE", "MILITARY", "REGIONAL",
        "HELIPORT", "HELIPT", "AIRFIELD", "FIELD", "AFB", "RAF", "MILITARY", "THE",
    };

    /// <summary>Best airfield for a page: name similarity, confirmed by runway headings (each
    /// bar must have a runway within 6° of its bearing).</summary>
    public static Match? Best(Title? title, List<Bar> bars, IReadOnlyList<Airfield> airfields, IReadOnlyDictionary<string, string>? aliases = null)
    {
        if (title is null) return null;
        if (title.Icao is { } icao && aliases is not null && aliases.TryGetValue(NormalizeIcao(icao), out var aliased) &&
            airfields.FirstOrDefault(a => a.Name.Equals(aliased, StringComparison.OrdinalIgnoreCase)) is { } named)
            return new Match(named, 1);
        var pageTokens = Tokens(title.Name);
        Match? best = null;
        foreach (var a in airfields)
        {
            var score = Similarity(pageTokens, Tokens(a.Name));
            if (score < 0.5) continue;
            if (bars.Count > 0 && a.Runways.Count > 0 && !bars.All(b => a.Runways.Any(r => AngleDiff(b.Bearing180, r.HeadingTrue % 180) < 6)))
                score -= 0.3;  // name fits but the drawing does not: likely a different field
            if (best is null || score > best.NameScore) best = new Match(a, score);
        }
        return best is { NameScore: >= 0.5 } ? best : null;
    }

    /// <summary>OCR reads the letter O as zero and inserts spaces ("0S65 " for OS65).</summary>
    public static string NormalizeIcao(string icao) => icao.Replace(" ", "").Replace('0', 'O').ToUpperInvariant();

    public static double AngleDiff(double a, double b)
    {
        var d = Math.Abs(a - b) % 180;
        return Math.Min(d, 180 - d);
    }

    private static List<string> Tokens(string name) =>
        Regex.Split(name.ToUpperInvariant().Replace("'", ""), @"[^A-Z0-9]+")
            .Where(t => t.Length > 1 && !Noise.Contains(t))
            .ToList();

    /// <summary>Share of the shorter name's tokens found (fuzzily) in the other name.</summary>
    private static double Similarity(List<string> a, List<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        // OCR merges words ("BASSELAL.ASSAD"): compare the names without separators too.
        string ja = string.Concat(a), jb = string.Concat(b);
        if (ja.Contains(jb) || jb.Contains(ja)) return 1;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        double hits = small.Count(t => large.Any(u => u == t || (t.Length >= 4 && u.Length >= 4 && Levenshtein(t, u) <= 1)));
        return hits / small.Count;
    }

    private static int Levenshtein(string s, string t)
    {
        var d = new int[s.Length + 1, t.Length + 1];
        for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= t.Length; j++) d[0, j] = j;
        for (int i = 1; i <= s.Length; i++)
        for (int j = 1; j <= t.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (s[i - 1] == t[j - 1] ? 0 : 1));
        return d[s.Length, t.Length];
    }
}

public sealed record Fit(double[] Coefficients, double ResidualM, double MetersPerPixel, double RotationDeg)
{
    public int RunwaysUsed { get; init; }
}

public static class Georef
{
    /// <summary>Similarity transform (scale, rotation, shift) from page pixels to DCS metres,
    /// least squares over runway ends. Every assignment of bars to runways of matching
    /// heading is tried (parallel runways share a heading) and the best fit kept.</summary>
    public static Fit? Fit(Airfield field, List<Bar> bars)
    {
        Fit? best = null;
        var bestPairs = 0;
        var bestSpread = double.MaxValue;
        foreach (var assignment in Assignments(bars, field.Runways, 0, []))
        {
            var used = assignment.Count(r => r is not null);
            if (used == 0) continue;
            var fit = FitPairs(bars, assignment);
            if (fit is null || !Plausible(fit)) continue;
            // More runways matched beats a smaller residual: one runway (two points) always
            // fits exactly, so it says nothing about whether the pairing is right. Between
            // equal counts, prefer the fit that also puts the unpaired bars nearest the other
            // runways: parallel runways the chart draws inexactly then still get the right side.
            var spread = BarSpread(fit, bars, field.Runways);
            var better = best is null || used > bestPairs ||
                         (used == bestPairs && (spread < bestSpread - 1 || (Math.Abs(spread - bestSpread) <= 1 && fit.ResidualM < best.ResidualM)));
            if (better)
            {
                best = fit with { RunwaysUsed = used };
                bestPairs = used;
                bestSpread = spread;
            }
        }
        return best;
    }

    /// <summary>Mean distance (m) from every bar's centre, mapped through the fit, to the
    /// nearest runway centre.</summary>
    private static double BarSpread(Fit fit, List<Bar> bars, IReadOnlyList<Runway> runways)
    {
        var c = fit.Coefficients;
        return bars.Average(b =>
        {
            double x = c[0] * b.Cx + c[1] * b.Cy + c[2], z = c[3] * b.Cx + c[4] * b.Cy + c[5];
            return runways.Min(r => Math.Sqrt((r.X - x) * (r.X - x) + (r.Z - z) * (r.Z - z)));
        });
    }

    /// <summary>Diagrams are north-up charts of one airfield: a fit that rotates the page or
    /// lands far from the drawn runways belongs to a wrong pairing.</summary>
    private static bool Plausible(Fit fit) =>
        Math.Abs(fit.RotationDeg) <= 1.5 && fit.MetersPerPixel is >= 0.3 and <= 20 && fit.ResidualM <= 150;

    /// <summary>Injective bar → runway (or none) assignments with headings within 6°.</summary>
    private static IEnumerable<Runway?[]> Assignments(List<Bar> bars, IReadOnlyList<Runway> runways, int i, Runway?[] current)
    {
        if (i == bars.Count)
        {
            yield return current;
            yield break;
        }
        foreach (var rw in runways.Where(r => Matcher.AngleDiff(bars[i].Bearing180, r.HeadingTrue % 180) <= 6 && !current.Contains(r)))
            foreach (var a in Assignments(bars, runways, i + 1, [.. current, rw]))
                yield return a;
        foreach (var a in Assignments(bars, runways, i + 1, [.. current, null]))
            yield return a;
    }

    private static Fit? FitPairs(List<Bar> bars, Runway?[] assignment)
    {
        var pairs = new List<((double X, double Y) Px, (double X, double Z) Dcs)>();
        for (int i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            if (assignment[i] is not { } rw) continue;
            var h = rw.HeadingTrue * Math.PI / 180;
            // DCS: x north, z east. Page: px east, py south.
            var dirX = Math.Cos(h) * rw.Length / 2;
            var dirZ = Math.Sin(h) * rw.Length / 2;
            // Bar end whose page direction (east, north) agrees with the runway heading direction.
            var sign = bar.Ux * Math.Sin(h) + -bar.Uy * Math.Cos(h) >= 0 ? 1 : -1;
            pairs.Add((bar.End(sign), (rw.X + dirX, rw.Z + dirZ)));
            pairs.Add((bar.End(-sign), (rw.X - dirX, rw.Z - dirZ)));
        }
        if (pairs.Count < 2) return null;

        // Solve x = a·px + b·py + c, z = -b·px + a·py + f   (north-up similarity:
        // east = s·cosθ·px − ..., written in DCS axes) by linear least squares on (a, b, c, f).
        // Unknowns u = [a, b, c, f]; rows: x_i = a·py... — set up generally:
        //   x = p·(−py) + q·px + c     (north grows as py shrinks)
        //   z = p·px    + q·py + f
        // with p = s·cosθ, q = s·sinθ.
        var rows = new List<(double[] A, double B)>();
        foreach (var (px, d) in pairs)
        {
            rows.Add(([-px.Y, px.X, 1, 0], d.X));
            rows.Add(([px.X, px.Y, 0, 1], d.Z));
        }
        var u = LeastSquares(rows, 4);
        if (u is null) return null;
        double p = u[0], q = u[1], c = u[2], f = u[3];
        double[] coeffs = [q, -p, c, p, q, f];  // x = q·px − p·py + c ; z = p·px + q·py + f
        double sum = 0;
        foreach (var (px, d) in pairs)
        {
            var x = coeffs[0] * px.X + coeffs[1] * px.Y + coeffs[2];
            var z = coeffs[3] * px.X + coeffs[4] * px.Y + coeffs[5];
            sum += (x - d.X) * (x - d.X) + (z - d.Z) * (z - d.Z);
        }
        var scale = Math.Sqrt(p * p + q * q);
        return new Fit(coeffs, Math.Sqrt(sum / pairs.Count), scale, Math.Atan2(q, p) * 180 / Math.PI);
    }

    private static double[]? LeastSquares(List<(double[] A, double B)> rows, int n)
    {
        var ata = new double[n, n];
        var atb = new double[n];
        foreach (var (a, b) in rows)
            for (int i = 0; i < n; i++)
            {
                atb[i] += a[i] * b;
                for (int j = 0; j < n; j++) ata[i, j] += a[i] * a[j];
            }
        // Gaussian elimination with partial pivoting.
        for (int col = 0; col < n; col++)
        {
            var pivot = Enumerable.Range(col, n - col).MaxBy(r => Math.Abs(ata[r, col]));
            if (Math.Abs(ata[pivot, col]) < 1e-12) return null;
            for (int k = 0; k < n; k++) (ata[col, k], ata[pivot, k]) = (ata[pivot, k], ata[col, k]);
            (atb[col], atb[pivot]) = (atb[pivot], atb[col]);
            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                var factor = ata[r, col] / ata[col, col];
                for (int k = 0; k < n; k++) ata[r, k] -= factor * ata[col, k];
                atb[r] -= factor * atb[col];
            }
        }
        return Enumerable.Range(0, n).Select(i => atb[i] / ata[i, i]).ToArray();
    }
}

public static class Server
{
    public static async Task<(Theatre Theatre, IReadOnlyList<Airfield> Airfields)> FetchAirfields(string host, int port, string password)
    {
        await using var conn = await DcbConnection.ConnectAsync(host, port, password);
        Theatre? theatre = null;
        IReadOnlyList<Airfield>? airfields = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var m in conn.ReadMessagesAsync(timeout.Token))
        {
            if (m is Theatre t) theatre = t;
            if (m is AirfieldsMessage a) airfields = a.Airfields;
            if (theatre is not null && airfields is not null) break;
        }
        return (theatre ?? throw new InvalidOperationException("no theatre from server"),
                airfields ?? throw new InvalidOperationException("no airfields from server"));
    }
}
