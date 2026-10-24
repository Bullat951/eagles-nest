// Offline prototype: DCS Caucasus ground composite (F10 Sat look) around a point.
//   proto tex <name> <out.png> [alpha]      dump one texture (top mip, max 1024 px)
//   proto render <x> <z> <sizeM> <mPerPx> <out.png> [layers]
if (args.Length >= 3 && args[0] == "tex")
{
    var t = Zips.Get(args[1]) ?? throw new FileNotFoundException(args[1]);
    t.SavePng(args[2], 1024, args.Length > 3);
    Console.WriteLine($"{args[1]}: {t.W}x{t.H}");
    return;
}
if (args.Length >= 6 && args[0] == "render")
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    Render.Run(double.Parse(args[1], inv), double.Parse(args[2], inv), double.Parse(args[3], inv), double.Parse(args[4], inv), args[5],
        args.Length > 6 ? args[6] : "base,noise,forest,field,town,road,river,runway");
    return;
}
if (args.Length >= 5 && args[0] == "compare")
{
    // compare <ours.png> <cropTop> <cropHeight> <reference image> <out.png>: ours cropped, stacked over the reference.
    using var ours = SkiaSharp.SKBitmap.Decode(args[1]);
    int top = int.Parse(args[2]), h = int.Parse(args[3]);
    using var reference = SkiaSharp.SKBitmap.Decode(args[4]);
    int w = ours.Width, rh = reference.Height * w / reference.Width;
    using var outBmp = new SkiaSharp.SKBitmap(w, h + rh + 40);
    using (var canvas = new SkiaSharp.SKCanvas(outBmp))
    {
        canvas.Clear(SkiaSharp.SKColors.Black);
        canvas.DrawBitmap(ours, new SkiaSharp.SKRect(0, top, w, top + h), new SkiaSharp.SKRect(0, 20, w, 20 + h));
        canvas.DrawBitmap(reference, new SkiaSharp.SKRect(0, 0, reference.Width, reference.Height), new SkiaSharp.SKRect(0, h + 40, w, h + 40 + rh));
        using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 18);
        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White };
        canvas.DrawText("Prototype: DCS Caucasus files, rendered offline", 6, 16, font, paint);
        canvas.DrawText("DCS F10 Sat (your screenshot)", 6, h + 36, font, paint);
    }
    using var f = File.Create(args[5]);
    outBmp.Encode(f, SkiaSharp.SKEncodedImageFormat.Png, 95);
    Console.WriteLine($"wrote {args[5]}");
    return;
}
if (args.Length >= 5 && args[0] == "score")
{
    // score <town-only.png> <cropTop> <cropHeight> <reference.png>: normalised cross-correlation of
    // local detail (luminance minus a 9x9 box mean) over the pixels the town layer drew.
    using var ours = SkiaSharp.SKBitmap.Decode(args[1]);
    int top = int.Parse(args[2]), h = int.Parse(args[3]);
    using var reference = SkiaSharp.SKBitmap.Decode(args[4]);
    int w = Math.Min(ours.Width, reference.Width);
    h = Math.Min(h, reference.Height);
    float L(SkiaSharp.SKColor c) => 0.3f * c.Red + 0.59f * c.Green + 0.11f * c.Blue;
    var a = new float[w * h]; var b = new float[w * h]; var mask = new bool[w * h];
    for (int y = 0; y < h; y++)
    for (int x = 0; x < w; x++)
    {
        var c = ours.GetPixel(x, top + y);
        mask[y * w + x] = c.Red + c.Green + c.Blue > 0;
        a[y * w + x] = L(c); b[y * w + x] = L(reference.GetPixel(x, y));
    }
    float[] Detail(float[] img)
    {
        var o = new float[img.Length];
        for (int y = 4; y < h - 4; y++)
        for (int x = 4; x < w - 4; x++)
        {
            float s = 0;
            for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++) s += img[(y + dy) * w + x + dx];
            o[y * w + x] = img[y * w + x] - s / 81;
        }
        return o;
    }
    var da = Detail(a); var db = Detail(b);
    // Best over shifts of the reference (its registration is only good to ~±50 m), every 2 px.
    var idx = Enumerable.Range(0, da.Length).Where(i => mask[i] && i % 2 == 0 && (i / w) % 2 == 0).ToArray();
    double best = -1; int bx = 0, by = 0;
    for (int sy = -40; sy <= 40; sy += 2)
    for (int sx = -40; sx <= 40; sx += 2)
    {
        double sab = 0, saa = 0, sbb = 0;
        foreach (var i in idx)
        {
            int y = i / w + sy, x = i % w + sx;
            if (y < 0 || x < 0 || y >= h || x >= w) continue;
            var vb = db[y * w + x];
            sab += da[i] * vb; saa += da[i] * da[i]; sbb += vb * vb;
        }
        var ncc = sab / Math.Sqrt(saa * sbb + 1e-9);
        if (ncc > best) (best, bx, by) = (ncc, sx, sy);
    }
    Console.WriteLine($"{Path.GetFileName(args[1])}: best NCC {best:F4} at shift ({bx}, {by}) px over {idx.Length} sampled town pixels");
    return;
}
Console.Error.WriteLine("usage: tex <name> <out.png> | render <x> <z> <sizeM> <mPerPx> <out.png> [layers]");
