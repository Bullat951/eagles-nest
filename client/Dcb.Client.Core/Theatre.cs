using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dcb.Client.Core;

public sealed record GeoGrid(
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("lat")] double[] Lat,
    [property: JsonPropertyName("lon")] double[] Lon);

/// <summary>The DCS theatre (terrain) the server runs: bounds in DCS map metres (x north,
/// z east), a lat/lon control grid over them, and town names.</summary>
public sealed record Theatre(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("x0")] double X0,
    [property: JsonPropertyName("z0")] double Z0,
    [property: JsonPropertyName("x1")] double X1,
    [property: JsonPropertyName("z1")] double Z1,
    [property: JsonPropertyName("grid")] GeoGrid Grid,
    [property: JsonPropertyName("towns")] JsonElement[][] TownsRaw,
    [property: JsonPropertyName("magvar")] MagVarGrid? MagVar = null)
{
    private IReadOnlyList<Town>? _towns;

    public IReadOnlyList<Town> Towns => _towns ??= TownsRaw
        .Where(t => t.Length >= 3)
        .Select(t => new Town(t[0].GetString() ?? "", t[1].GetDouble(), t[2].GetDouble()))
        .ToList();

    /// <summary>DCS map x/z → lat/lon by bilinear interpolation in the control grid. DCS maps
    /// are transverse Mercator: the server's 81×81 grid keeps the error under ~8 m even on
    /// Caucasus (the 21×21 grid older servers send is off by up to ~140 m there).</summary>
    public (double Lat, double Lon) ToLatLon(double x, double z)
    {
        var n = Grid.N;
        var fi = Math.Clamp((x - X0) / (X1 - X0) * (n - 1), 0, n - 1);
        var fj = Math.Clamp((z - Z0) / (Z1 - Z0) * (n - 1), 0, n - 1);
        var i = Math.Min((int)fi, n - 2);
        var j = Math.Min((int)fj, n - 2);
        double ti = fi - i, tj = fj - j;
        double Lerp(double[] v) =>
            (v[i * n + j] * (1 - tj) + v[i * n + j + 1] * tj) * (1 - ti) +
            (v[(i + 1) * n + j] * (1 - tj) + v[(i + 1) * n + j + 1] * tj) * ti;
        return (Lerp(Grid.Lat), Lerp(Grid.Lon));
    }

    /// <summary>Lat/lon → DCS map x/z: Newton steps on <see cref="ToLatLon"/> (smooth), from the
    /// theatre centre. Within the grid it converges to well under a metre in a few steps.</summary>
    public (double X, double Z) FromLatLon(double lat, double lon)
    {
        double x = (X0 + X1) / 2, z = (Z0 + Z1) / 2;
        const double h = 1000;  // metres for the numerical Jacobian
        for (int k = 0; k < 8; k++)
        {
            var (la, lo) = ToLatLon(x, z);
            var (lax, lox) = ToLatLon(x + h, z);
            var (laz, loz) = ToLatLon(x, z + h);
            double a = (lax - la) / h, b = (laz - la) / h, c = (lox - lo) / h, d = (loz - lo) / h;
            var det = a * d - b * c;
            if (Math.Abs(det) < 1e-18) break;
            double eLat = lat - la, eLon = lon - lo;
            double dx = (d * eLat - b * eLon) / det, dz = (-c * eLat + a * eLon) / det;
            x += dx;
            z += dz;
            if (Math.Abs(dx) < 0.01 && Math.Abs(dz) < 0.01) break;
        }
        return (x, z);
    }

    /// <summary>Bearing of DCS grid north (+x) from true north at a point, degrees (meridian
    /// convergence): add it to a grid bearing (headings, RWR bearings) to get a true one.</summary>
    public double GridNorthAt(double lat, double lon)
    {
        var (x, z) = FromLatLon(lat, lon);
        var (la0, lo0) = ToLatLon(x, z);
        var (la1, lo1) = ToLatLon(x + 500, z);
        return Math.Atan2((lo1 - lo0) * Math.Cos(la0 * Math.PI / 180), la1 - la0) * 180 / Math.PI;
    }

    /// <summary>Theatre border as lat/lon points, walking the x/z rectangle's edges.</summary>
    public IReadOnlyList<(double Lat, double Lon)> Border(int pointsPerEdge = 40)
    {
        var pts = new List<(double, double)>();
        void Edge(double xa, double za, double xb, double zb)
        {
            for (int k = 0; k < pointsPerEdge; k++)
            {
                var t = (double)k / pointsPerEdge;
                pts.Add(ToLatLon(xa + (xb - xa) * t, za + (zb - za) * t));
            }
        }
        Edge(X0, Z0, X0, Z1);
        Edge(X0, Z1, X1, Z1);
        Edge(X1, Z1, X1, Z0);
        Edge(X1, Z0, X0, Z0);
        return pts;
    }
}

public sealed record Town(string Name, double Lat, double Lon);

/// <summary>Magnetic variation in degrees (east positive) from DCS's own model for the
/// mission date, sampled by the server on a lat/lon grid: row i at Lat0 + i·Step, column j at
/// Lon0 + j·Step.</summary>
public sealed record MagVarGrid(
    [property: JsonPropertyName("lat0")] double Lat0,
    [property: JsonPropertyName("lon0")] double Lon0,
    [property: JsonPropertyName("step")] double Step,
    [property: JsonPropertyName("nlat")] int NLat,
    [property: JsonPropertyName("nlon")] int NLon,
    [property: JsonPropertyName("deg")] double[] Deg)
{
    /// <summary>Variation at a point, bilinear between grid nodes, clamped to the grid.</summary>
    public double At(double lat, double lon)
    {
        if (NLat < 2 || NLon < 2 || Deg.Length < NLat * NLon) return Deg.Length > 0 ? Deg[0] : 0;
        var fi = Math.Clamp((lat - Lat0) / Step, 0, NLat - 1);
        var fj = Math.Clamp((lon - Lon0) / Step, 0, NLon - 1);
        var i = Math.Min((int)fi, NLat - 2);
        var j = Math.Min((int)fj, NLon - 2);
        double ti = fi - i, tj = fj - j;
        return (Deg[i * NLon + j] * (1 - tj) + Deg[i * NLon + j + 1] * tj) * (1 - ti) +
               (Deg[(i + 1) * NLon + j] * (1 - tj) + Deg[(i + 1) * NLon + j + 1] * tj) * ti;
    }
}

/// <summary>DCS surface types (land.SurfaceType).</summary>
public enum Surface : byte { Unknown = 0, Land = 1, ShallowWater = 2, Water = 3, Road = 4, Runway = 5 }

/// <summary>Terrain heights and surface types sampled by the server on a regular x/z grid:
/// row i at x = X0 + i·Step, column j at z = Z0 + j·Step.</summary>
public sealed record TerrainMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("x0")] double X0,
    [property: JsonPropertyName("z0")] double Z0,
    [property: JsonPropertyName("step")] double Step,
    [property: JsonPropertyName("nx")] int Nx,
    [property: JsonPropertyName("nz")] int Nz,
    [property: JsonPropertyName("heights")] string HeightsBase64,
    [property: JsonPropertyName("surfaces")] string SurfacesBase64)
{
    public TerrainGrid Decode()
    {
        var h = Convert.FromBase64String(HeightsBase64);
        var s = Convert.FromBase64String(SurfacesBase64);
        var n = Nx * Nz;
        if (h.Length != n * 2 || s.Length != n) throw new InvalidDataException("terrain size mismatch");
        var heights = new short[n];
        Buffer.BlockCopy(h, 0, heights, 0, h.Length);
        return new TerrainGrid(Id, X0, Z0, Step, Nx, Nz, heights, s);
    }
}

public sealed record TerrainGrid(string Id, double X0, double Z0, double Step, int Nx, int Nz, short[] Heights, byte[] Surfaces)
{
    public short HeightAt(int i, int j) => Heights[i * Nz + j];
    public Surface SurfaceAt(int i, int j) => (Surface)Surfaces[i * Nz + j];
}
