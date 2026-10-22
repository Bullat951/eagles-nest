using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dcb.Client.Core;
using SkiaSharp;

namespace Dcb.Client.Map.Gpu;

/// <summary>Skia images from the existing decoders, and drawing in DCS map coordinates.</summary>
public static class SkDraw
{
    public static SKPoint Screen(MapView view, Theatre theatre, double x, double z)
    {
        var (lat, lon) = theatre.ToLatLon(x, z);
        var (sx, sy) = view.ToScreenLonLat(lon, lat);
        return new SKPoint((float)sx, (float)sy);
    }

    public static SKPoint ScreenLonLat(MapView view, double lon, double lat)
    {
        var (sx, sy) = view.ToScreenLonLat(lon, lat);
        return new SKPoint((float)sx, (float)sy);
    }

    /// <summary>Draws an image whose top-left corner is at DCS (xTop, zLeft) and which spans
    /// <paramref name="width"/> metres east and <paramref name="height"/> metres south, warped
    /// affinely through three projected corners (accurate for areas up to ~100 km).</summary>
    public static void Warped(SKCanvas canvas, MapView view, Theatre theatre, SKImage image,
        double xTop, double zLeft, double width, double height, float opacity = 1) =>
        Axes(canvas, view, theatre, image, xTop, zLeft, 0, width, -height, 0, opacity);

    /// <summary>Draws an image placed by its own axes in DCS metres: top-left corner at (x, z),
    /// full width along (uX, uZ), full height along (vX, vZ).</summary>
    public static void Axes(SKCanvas canvas, MapView view, Theatre theatre, SKImage image,
        double x, double z, double uX, double uZ, double vX, double vZ, float opacity = 1)
    {
        var o = Screen(view, theatre, x, z);
        var u = Screen(view, theatre, x + uX, z + uZ);
        var v = Screen(view, theatre, x + vX, z + vZ);
        var w = u + (v - o);
        float minX = Math.Min(Math.Min(o.X, u.X), Math.Min(v.X, w.X)), maxX = Math.Max(Math.Max(o.X, u.X), Math.Max(v.X, w.X));
        float minY = Math.Min(Math.Min(o.Y, u.Y), Math.Min(v.Y, w.Y)), maxY = Math.Max(Math.Max(o.Y, u.Y), Math.Max(v.Y, w.Y));
        if (maxX < 0 || maxY < 0 || minX > view.Width || minY > view.Height) return;
        Affine(canvas, image, o, u, v, opacity);
    }

    /// <summary>Draws an image with its top-left pixel corner at o, top-right at u, bottom-left at v.</summary>
    public static void Affine(SKCanvas canvas, SKImage image, SKPoint o, SKPoint u, SKPoint v, float opacity = 1)
    {
        float iw = image.Width, ih = image.Height;
        var m = new SKMatrix((u.X - o.X) / iw, (v.X - o.X) / ih, o.X, (u.Y - o.Y) / iw, (v.Y - o.Y) / ih, o.Y, 0, 0, 1);
        canvas.Save();
        canvas.Concat(in m);
        canvas.DrawImage(image, new SKRect(0, 0, iw, ih), Ink.Smooth, Ink.Image(opacity));
        canvas.Restore();
    }

    /// <summary>An image from BGRA bytes (straight alpha unless <paramref name="premultiplied"/>).</summary>
    public static SKImage FromBgra(byte[] bgra, int width, int height, bool premultiplied = false)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, premultiplied ? SKAlphaType.Premul : SKAlphaType.Unpremul);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), width * 4);
            return SKImage.FromPixelCopy(pixmap);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>A copy of a WPF bitmap (any format) as a Skia image.</summary>
    public static SKImage FromBitmapSource(BitmapSource source)
    {
        var premul = source.Format == PixelFormats.Pbgra32;
        if (source.Format != PixelFormats.Bgra32 && !premul)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = source.PixelWidth, h = source.PixelHeight;
        var px = new byte[w * h * 4];
        source.CopyPixels(px, w * 4, 0);
        return FromBgra(px, w, h, premul);
    }

    /// <summary>Approximate ground metres per DIP at a latitude.</summary>
    public static double MetersPerDip(MapView view, double lat) => view.UnitsPerDip / Mercator.ScaleAt(lat);

    /// <summary>Latitude at the view centre.</summary>
    public static double CenterLat(MapView view)
    {
        var (_, cy) = view.Center;
        return (2 * Math.Atan(Math.Exp(cy / 6378137)) - Math.PI / 2) * 180 / Math.PI;
    }
}
