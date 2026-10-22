using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Dcb.Client.Map.Gpu;

/// <summary>What a layer draws for: the view (DIPs) and the device pixels per DIP.</summary>
public readonly record struct MapFrame(MapView View, float PixelsPerDip);

/// <summary>One map layer drawn with Skia into the map's surface, bottom-up.</summary>
public abstract class MapLayer
{
    private bool _visible = true;

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            Invalidate();
        }
    }

    public MapSurface? Surface { get; internal set; }

    /// <summary>Asks for a redraw; safe from any thread.</summary>
    public void Invalidate() => Surface?.Invalidate();

    public abstract void Draw(SKCanvas canvas, in MapFrame frame);
}

/// <summary>A map drawn by Skia on the GPU (OpenGL through SKGLElement), or by Skia on the CPU
/// when OpenGL is unavailable (or DCB_RENDERER=cpu). Draws only when something changed — at most
/// once per display frame — and every frame while a smooth zoom runs. Left-drag pans, the
/// wheel zooms around the pointer.</summary>
public sealed class MapSurface : Decorator
{
    private readonly FrameworkElement _element;
    private bool _queued;
    private bool _dragging;
    private Point _last;
    private long _lastTick;

    public MapNavigator Navigator { get; } = new();
    public IReadOnlyList<MapLayer> Layers => _layers;
    private readonly List<MapLayer> _layers = [];
    public SKColor BackColor { get; set; } = new(0x0A, 0x14, 0x1C);
    public bool UsesGpu { get; }

    /// <summary>Frames painted and the CPU time spent painting them (layers + GPU submit).</summary>
    public (int Frames, double TotalMs, double MaxMs) Stats => _stats;
    private (int Frames, double TotalMs, double MaxMs) _stats;

    /// <summary>Gaps between consecutive painted frames while the map keeps moving (paints less
    /// than 250 ms apart): the longest, and how many exceeded 25 ms (a visibly missed frame).</summary>
    public (double MaxGapMs, int Over25, int Over50) Gaps => _gaps;
    private (double MaxGapMs, int Over25, int Over50) _gaps;
    private long _lastPaint;

    public void ResetStats()
    {
        _stats = default;
        _gaps = default;
        _lastPaint = 0;
        _statsStart = Stopwatch.GetTimestamp();
        GapLog.Clear();
    }

    public MapSurface()
    {
        Focusable = false;
        ClipToBounds = true;
        UsesGpu = Environment.GetEnvironmentVariable("DCB_RENDERER") != "cpu";
        if (UsesGpu)
        {
            try
            {
                var gl = new SKGLElement();
                gl.PaintSurface += (_, e) => Paint(e.Surface, e.BackendRenderTarget.Width);
                _element = gl;
            }
            catch (Exception ex)
            {
                Log("OpenGL unavailable, drawing on the CPU: " + ex.Message);
                UsesGpu = false;
            }
        }
        if (!UsesGpu || _element is null)
        {
            var cpu = new SKElement();
            cpu.PaintSurface += (_, e) => Paint(e.Surface, e.Info.Width);
            _element = cpu;
        }
        Child = _element;
        Navigator.Changed += Invalidate;
        SizeChanged += (_, _) => Navigator.SetSize(ActualWidth, ActualHeight);
    }

    public void Add(MapLayer layer)
    {
        layer.Surface = this;
        _layers.Add(layer);
        Invalidate();
    }

    /// <summary>Redraw at the next display frame (coalesced); safe from any thread.</summary>
    public void Invalidate()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Invalidate);
            return;
        }
        if (_queued) return;
        _queued = true;
        _lastTick = Stopwatch.GetTimestamp();
        // Asked to draw after a quiet spell: the time since the last paint was idle, not a stall.
        if (_lastPaint != 0 && Stopwatch.GetElapsedTime(_lastPaint, _lastTick).TotalMilliseconds > 20) _resumed = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _queued = false;
        var now = Stopwatch.GetTimestamp();
        _ticksSincePaint++;
        if (_lastTickAny != 0) _longestTick = Math.Max(_longestTick, Stopwatch.GetElapsedTime(_lastTickAny, now).TotalMilliseconds);
        _lastTickAny = now;
        var dt = Math.Min(0.05, (now - _lastTick) / (double)Stopwatch.Frequency);
        _lastTick = now;
        if (Navigator.IsAnimating)
        {
            Navigator.Animate(dt);  // raises Changed → queued again for the next frame
            if (!Navigator.IsAnimating) Invalidate();
        }
        _element.InvalidateVisual();
    }

    private void Paint(SKSurface surface, int pixelWidth)
    {
        var start = Stopwatch.GetTimestamp();
        var scale = ActualWidth > 0 ? (float)(pixelWidth / ActualWidth) : 1f;
        DrawAll(surface.Canvas, scale);
        surface.Flush();
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _stats = (_stats.Frames + 1, _stats.TotalMs + ms, Math.Max(_stats.MaxMs, ms));
        var gc = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        if (_lastPaint != 0)
        {
            var gap = Stopwatch.GetElapsedTime(_lastPaint, start).TotalMilliseconds;
            if (gap < 250 && !_resumed)
            {
                _gaps = (Math.Max(_gaps.MaxGapMs, gap), _gaps.Over25 + (gap > 25 ? 1 : 0), _gaps.Over50 + (gap > 50 ? 1 : 0));
                // Where a long gap came from: frames asked for (Rendering ticks) but not painted,
                // or the UI thread itself late (ticks far apart), and collections in between.
                if (gap > 40 && GapLog.Count < 40)
                    GapLog.Add($"gap {gap:F0} ms at {Stopwatch.GetElapsedTime(_statsStart, start).TotalMilliseconds:F0} ms, animating {Navigator.IsAnimating}: ticks {_ticksSincePaint} (longest tick gap {_longestTick:F0} ms), " +
                               $"GC gen0/1/2 +{gc.Item1 - _gcAtPaint.Item1}/{gc.Item2 - _gcAtPaint.Item2}/{gc.Item3 - _gcAtPaint.Item3}, paint {ms:F1} ms");
            }
        }
        _lastPaint = start;
        _resumed = false;
        _gcAtPaint = gc;
        _ticksSincePaint = 0;
        _longestTick = 0;
    }

    /// <summary>Diagnosis of long gaps between painted frames (tests).</summary>
    public List<string> GapLog { get; } = [];
    private (int, int, int) _gcAtPaint;
    private int _ticksSincePaint;
    private double _longestTick;
    private long _lastTickAny;
    private long _statsStart;
    private bool _resumed;

    /// <summary>Smooth zoom by wheel notches around a point (mouse wheel, tests).</summary>
    public void Wheel(double notches, double x, double y)
    {
        Navigator.WheelZoom(notches, x, y);
        Invalidate();
    }

    private void DrawAll(SKCanvas canvas, float scale)
    {
        canvas.Clear(BackColor);
        if (!Navigator.IsValid) return;
        var frame = new MapFrame(Navigator.View, scale);
        canvas.Save();
        canvas.Scale(scale);
        foreach (var layer in _layers)
        {
            if (!layer.Visible) continue;
            var save = canvas.Save();
            try
            {
                using var perf = RenderStats.Measure(layer.GetType().Name);
                layer.Draw(canvas, frame);
            }
            catch (Exception ex)
            {
                Log($"{layer.GetType().Name}: {ex}");  // one broken layer must not blank the map
            }
            canvas.RestoreToCount(save);
        }
        canvas.Restore();
    }

    /// <summary>The map drawn on the CPU at a device scale (snapshots: an OpenGL surface is not
    /// in WPF's RenderTargetBitmap).</summary>
    public SKImage RenderToImage(float scale)
    {
        int w = Math.Max(1, (int)Math.Round(ActualWidth * scale)), h = Math.Max(1, (int)Math.Round(ActualHeight * scale));
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        DrawAll(surface.Canvas, scale);
        return surface.Snapshot();
    }

    // ---- Mouse: left-drag pans, wheel zooms around the pointer ------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        _last = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        var p = e.GetPosition(this);
        Navigator.PanBy(p.X - _last.X, p.Y - _last.Y);
        _last = p;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(this);
        Wheel(e.Delta / 120.0, p.X, p.Y);
        e.Handled = true;
    }

    // ---- Errors -----------------------------------------------------------------------------

    private static int _logged;

    /// <summary>Appends to %LocalAppData%\DCB\render-errors.log (at most 20 lines a session);
    /// also used by the airfield views so a drawing fault never takes the app down.</summary>
    internal static void Log(string text)
    {
        if (Interlocked.Increment(ref _logged) > 20) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DCB");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "render-errors.log"), $"{DateTime.Now:s} {text}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never break drawing.
        }
    }
}
