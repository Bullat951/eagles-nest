namespace Dcb.Client.Map.Gpu;

/// <summary>Where a map looks: centre in Mercator metres, resolution in Mercator metres per
/// DIP, size in DIPs. Pan and zoom (also animated, for the mouse wheel), kept inside the
/// theatre's pan and zoom bounds.</summary>
public sealed class MapNavigator
{
    private const double ZoomStep = 1.35;          // one wheel notch
    private const double ZoomSeconds = 0.12;       // time constant of the smooth zoom

    public double CenterX { get; private set; }
    public double CenterY { get; private set; }
    /// <summary>Mercator metres per DIP.</summary>
    public double Resolution { get; private set; } = 1200;
    public double Width { get; private set; }
    public double Height { get; private set; }

    /// <summary>The centre stays inside this Mercator rectangle.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY)? PanBounds { get; set; }
    /// <summary>Resolution limits (finest, coarsest).</summary>
    public (double Min, double Max) ZoomBounds { get; set; } = (0.5, 40_000);

    /// <summary>Raised on the UI thread after every change of the view.</summary>
    public event Action? Changed;

    public MapView View => new(CenterX, CenterY, Resolution, Width, Height);
    public bool IsValid => Resolution > 0 && Width > 0 && Height > 0;

    // Smooth zoom: the resolution eases towards the target with the anchor point fixed.
    private double _targetResolution;
    private (double Sx, double Sy) _anchor;
    public bool IsAnimating { get; private set; }

    public void SetSize(double width, double height)
    {
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        Clamp();
        Changed?.Invoke();
    }

    public void CenterOn(double x, double y) => CenterOnAndZoomTo(x, y, Resolution);

    public void ZoomTo(double resolution) => CenterOnAndZoomTo(CenterX, CenterY, resolution);

    public void CenterOnAndZoomTo(double x, double y, double resolution)
    {
        IsAnimating = false;
        CenterX = x;
        CenterY = y;
        Resolution = resolution;
        Clamp();
        Changed?.Invoke();
    }

    /// <summary>Fits a Mercator rectangle into the view.</summary>
    public void ZoomToBox(double minX, double minY, double maxX, double maxY)
    {
        if (Width <= 0 || Height <= 0) return;
        var res = Math.Max((maxX - minX) / Width, (maxY - minY) / Height);
        CenterOnAndZoomTo((minX + maxX) / 2, (minY + maxY) / 2, res);
    }

    /// <summary>Moves the map with the pointer: positive dx drags the map right.</summary>
    public void PanBy(double dxDip, double dyDip)
    {
        CenterX -= dxDip * Resolution;
        CenterY += dyDip * Resolution;
        if (IsAnimating) _anchorWorld = ToWorld(_anchor.Sx, _anchor.Sy);
        Clamp();
        Changed?.Invoke();
    }

    /// <summary>Starts (or extends) a smooth zoom by wheel notches around a screen point.</summary>
    public void WheelZoom(double notches, double sx, double sy)
    {
        var from = IsAnimating ? _targetResolution : Resolution;
        _targetResolution = Math.Clamp(from / Math.Pow(ZoomStep, notches), ZoomBounds.Min, ZoomBounds.Max);
        _anchor = (sx, sy);
        _anchorWorld = ToWorld(sx, sy);
        IsAnimating = true;
    }

    private (double X, double Y) _anchorWorld;

    /// <summary>Advances the smooth zoom; returns false once it has arrived.</summary>
    public bool Animate(double seconds)
    {
        if (!IsAnimating) return false;
        var k = 1 - Math.Exp(-seconds / ZoomSeconds);
        var res = Resolution * Math.Pow(_targetResolution / Resolution, k);
        if (Math.Abs(res / _targetResolution - 1) < 0.002)
        {
            res = _targetResolution;
            IsAnimating = false;
        }
        // Keep the world point under the anchor where it was.
        Resolution = res;
        CenterX = _anchorWorld.X - (_anchor.Sx - Width / 2) * res;
        CenterY = _anchorWorld.Y + (_anchor.Sy - Height / 2) * res;
        Clamp();
        Changed?.Invoke();
        return IsAnimating;
    }

    public (double X, double Y) ToWorld(double sx, double sy) =>
        (CenterX + (sx - Width / 2) * Resolution, CenterY - (sy - Height / 2) * Resolution);

    private void Clamp()
    {
        Resolution = Math.Clamp(Resolution, ZoomBounds.Min, Math.Max(ZoomBounds.Min, ZoomBounds.Max));
        if (PanBounds is { } b)
        {
            CenterX = Math.Clamp(CenterX, b.MinX, b.MaxX);
            CenterY = Math.Clamp(CenterY, b.MinY, b.MaxY);
        }
    }
}
