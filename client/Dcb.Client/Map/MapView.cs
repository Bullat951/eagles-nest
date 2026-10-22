namespace Dcb.Client.Map;

/// <summary>Spherical (web) Mercator (EPSG:3857), without allocating.</summary>
public static class Mercator
{
    private const double Radius = 6378137;

    public static (double X, double Y) FromLonLat(double lon, double lat)
    {
        var x = lon * Math.PI / 180 * Radius;
        var y = Math.Log(Math.Tan(Math.PI / 4 + lat * Math.PI / 360)) * Radius;
        return (x, y);
    }

    public static (double Lon, double Lat) ToLonLat(double x, double y) =>
        (x / Radius * 180 / Math.PI, (2 * Math.Atan(Math.Exp(y / Radius)) - Math.PI / 2) * 180 / Math.PI);

    /// <summary>Mercator units per ground metre at a latitude (the projection stretches poleward).</summary>
    public static double ScaleAt(double lat) => 1 / Math.Cos(lat * Math.PI / 180);
}

/// <summary>A map view for drawing in DIPs: centred on Mercator (cx, cy) at a scale (Mercator
/// metres per DIP), of a given DIP size.</summary>
public readonly struct MapView
{
    private readonly double _cx, _cy, _res, _halfW, _halfH;

    public MapView(double cx, double cy, double unitsPerDip, double width, double height)
    {
        _cx = cx;
        _cy = cy;
        _res = unitsPerDip;
        _halfW = width / 2;
        _halfH = height / 2;
        Width = width;
        Height = height;
    }

    /// <summary>Mercator centre of the view.</summary>
    public (double X, double Y) Center => (_cx, _cy);

    public double Width { get; }
    public double Height { get; }
    public bool IsValid => _res > 0 && Width > 0 && Height > 0;

    /// <summary>Metres (Mercator units) per overlay DIP.</summary>
    public double UnitsPerDip => _res;

    public (double X, double Y) ToScreen(double mx, double my) =>
        ((mx - _cx) / UnitsPerDip + _halfW, (_cy - my) / UnitsPerDip + _halfH);

    /// <summary>Overlay point → lat/lon.</summary>
    public (double Lat, double Lon) ToLatLon(double sx, double sy)
    {
        var (lon, lat) = Mercator.ToLonLat(_cx + (sx - _halfW) * UnitsPerDip, _cy - (sy - _halfH) * UnitsPerDip);
        return (lat, lon);
    }

    public (double X, double Y) ToScreenLonLat(double lon, double lat)
    {
        var (mx, my) = Mercator.FromLonLat(lon, lat);
        return ToScreen(mx, my);
    }

    /// <summary>Visible Mercator rectangle (minX, minY, maxX, maxY).</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) WorldBounds =>
        (_cx - _halfW * UnitsPerDip, _cy - _halfH * UnitsPerDip, _cx + _halfW * UnitsPerDip, _cy + _halfH * UnitsPerDip);
}
