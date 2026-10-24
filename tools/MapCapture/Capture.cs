using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>Capture of the DCS window's map view with PrintWindow (client area, full content): the
/// window's own pixels only — other windows, notifications and overlays on the screen are not in
/// it, and DCS need not be in front. Cuts out the map view (GUI units × the screen scale) and
/// captures again until two shots in a row agree, so imagery still streaming in is not kept.</summary>
static class Capture
{
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    const uint PwClientOnly = 1, PwRenderFullContent = 2;

    static Capture() => SetProcessDPIAware();

    public sealed record Shot(Bitmap Image, double UnitsToPixels);

    /// <summary>The game's window ("Digital Combat Simulator"), not a dedicated server's ("DCS.server").</summary>
    static IntPtr DcsWindow() =>
        System.Diagnostics.Process.GetProcessesByName("DCS")
            .Where(p => p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Contains("Digital Combat Simulator"))
            .Select(p => p.MainWindowHandle).FirstOrDefault() is var h && h != IntPtr.Zero
            ? h : throw new InvalidOperationException("No DCS game window (\"Digital Combat Simulator\") found.");

    public static Shot MapView(Dictionary<string, string> job)
    {
        var inv = CultureInfo.InvariantCulture;
        var hwnd = DcsWindow();
        GetClientRect(hwnd, out var rc);
        int cw = rc.R - rc.L, ch = rc.B - rc.T;
        // GUI units → pixels: the GUI's screen width when the script could read it, else 1:1.
        var scale = job.TryGetValue("screen.w", out var sw) && double.TryParse(sw, NumberStyles.Float, inv, out var guiW) && guiW > 0 ? cw / guiW : 1.0;
        double mx = double.Parse(job["map.x"], inv), my = double.Parse(job["map.y"], inv);
        double mw = double.Parse(job["map.w"], inv), mh = double.Parse(job["map.h"], inv);
        var crop = Rectangle.Intersect(new Rectangle((int)Math.Round(mx * scale), (int)Math.Round(my * scale), (int)Math.Round(mw * scale), (int)Math.Round(mh * scale)),
            new Rectangle(0, 0, cw, ch));
        Bitmap? previous = null;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            Thread.Sleep(attempt == 0 ? 50 : 250);
            using var full = new Bitmap(cw, ch, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                PrintWindow(hwnd, hdc, PwClientOnly | PwRenderFullContent);
                g.ReleaseHdc(hdc);
            }
            var bmp = full.Clone(crop, PixelFormat.Format24bppRgb);
            if (previous is not null && Difference(previous, bmp) < 1.0)
            {
                previous.Dispose();
                return new Shot(bmp, scale);
            }
            previous?.Dispose();
            previous = bmp;
        }
        return new Shot(previous!, scale);  // never settled: keep the last
    }

    /// <summary>Mean absolute difference per channel (0–255) on a sparse sample of pixels.</summary>
    static double Difference(Bitmap a, Bitmap b)
    {
        double sum = 0;
        int n = 0;
        for (int y = 0; y < a.Height; y += 7)
        for (int x = 0; x < a.Width; x += 7)
        {
            Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
            sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B);
            n += 3;
        }
        return sum / Math.Max(1, n);
    }
}
