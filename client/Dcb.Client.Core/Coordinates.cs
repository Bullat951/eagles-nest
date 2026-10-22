using System.Globalization;

namespace Dcb.Client.Core;

/// <summary>A point written the ways controllers and cockpits use: degrees and decimal minutes
/// (DCS and most jets), degrees-minutes-seconds, and MGRS.</summary>
public static class Coordinates
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"N 34°42.123' E 036°41.456'" (minutes to three decimals, as DCS shows them).</summary>
    public static string Ddm(double lat, double lon) => $"{Ddm(lat, 2, 'N', 'S')} {Ddm(lon, 3, 'E', 'W')}";

    private static string Ddm(double v, int degDigits, char pos, char neg)
    {
        var a = Math.Abs(v);
        var deg = (int)a;
        var min = Math.Round((a - deg) * 60, 3);
        if (min >= 60) { deg++; min = 0; }
        return $"{(v < 0 ? neg : pos)} {deg.ToString(new string('0', degDigits), Inv)}°{min.ToString("00.000", Inv)}'";
    }

    /// <summary>"N 34°42'07.4\" E 36°41'27.3\"".</summary>
    public static string Dms(double lat, double lon) => $"{Dms(lat, 'N', 'S')} {Dms(lon, 'E', 'W')}";

    private static string Dms(double v, char pos, char neg)
    {
        var tenths = (long)Math.Round(Math.Abs(v) * 36000);  // tenths of a second
        long deg = tenths / 36000, min = tenths / 600 % 60, sec10 = tenths % 600;
        return $"{(v < 0 ? neg : pos)} {deg}°{min:00}'{(sec10 / 10.0).ToString("00.0", Inv)}\"";
    }

    // ---- MGRS (WGS84) -------------------------------------------------------------------------

    private const double A = 6378137, F = 1 / 298.257223563, K0 = 0.9996;
    private const string Bands = "CDEFGHJKLMNPQRSTUVWXX";
    private const string RowLetters = "ABCDEFGHJKLMNPQRSTUV";
    private static readonly string[] ColumnLetters = ["ABCDEFGH", "JKLMNPQR", "STUVWXYZ"];

    /// <summary>"37S BV 12345 67890": zone, band, 100 km square, easting and northing to
    /// <paramref name="digits"/> digits each (5 = 1 m), truncated as MGRS requires. Valid from
    /// 80°S to 84°N (beyond that MGRS uses polar UPS).</summary>
    public static string Mgrs(double lat, double lon, int digits = 5)
    {
        var (zone, easting, northing) = Utm(lat, lon);
        var band = Bands[Math.Clamp((int)Math.Floor((lat + 80) / 8), 0, Bands.Length - 1)];
        var set = (zone - 1) % 6 + 1;  // 100 km letter sets repeat every 6 zones
        var column = ColumnLetters[(set - 1) % 3][(int)Math.Floor(easting / 100_000) - 1];
        var rowIndex = (int)Math.Floor(northing / 100_000) % 20;
        var row = RowLetters[(rowIndex + (set % 2 == 0 ? 5 : 0)) % 20];
        var scale = Math.Pow(10, 5 - digits);
        var e = (long)Math.Floor(easting % 100_000 / scale);
        var n = (long)Math.Floor(northing % 100_000 / scale);
        var fmt = new string('0', digits);
        return $"{zone}{band} {column}{row} {e.ToString(fmt, Inv)} {n.ToString(fmt, Inv)}";
    }

    /// <summary>Lat/lon → UTM zone, easting, northing (m; southern hemisphere with the 10 000 km
    /// false northing). Zone exceptions for Norway and Svalbard included.</summary>
    public static (int Zone, double Easting, double Northing) Utm(double lat, double lon)
    {
        var zone = (int)Math.Floor((lon + 180) / 6) + 1;
        if (lat is >= 56 and < 64 && lon is >= 3 and < 12) zone = 32;
        if (lat is >= 72 and < 84)
            zone = lon switch { >= 0 and < 9 => 31, >= 9 and < 21 => 33, >= 21 and < 33 => 35, >= 33 and < 42 => 37, _ => zone };
        zone = Math.Clamp(zone, 1, 60);

        var e2 = F * (2 - F);
        var ep2 = e2 / (1 - e2);
        var phi = lat * Math.PI / 180;
        var dLambda = (lon - ((zone - 1) * 6 - 180 + 3)) * Math.PI / 180;
        double sin = Math.Sin(phi), cos = Math.Cos(phi), tan = Math.Tan(phi);
        var n = A / Math.Sqrt(1 - e2 * sin * sin);
        var t = tan * tan;
        var c = ep2 * cos * cos;
        var a = dLambda * cos;
        double e4 = e2 * e2, e6 = e4 * e2;
        var m = A * ((1 - e2 / 4 - 3 * e4 / 64 - 5 * e6 / 256) * phi
                     - (3 * e2 / 8 + 3 * e4 / 32 + 45 * e6 / 1024) * Math.Sin(2 * phi)
                     + (15 * e4 / 256 + 45 * e6 / 1024) * Math.Sin(4 * phi)
                     - 35 * e6 / 3072 * Math.Sin(6 * phi));
        var easting = K0 * n * (a + (1 - t + c) * Math.Pow(a, 3) / 6
                                + (5 - 18 * t + t * t + 72 * c - 58 * ep2) * Math.Pow(a, 5) / 120) + 500_000;
        var northing = K0 * (m + n * tan * (a * a / 2 + (5 - t + 9 * c + 4 * c * c) * Math.Pow(a, 4) / 24
                                             + (61 - 58 * t + t * t + 600 * c - 330 * ep2) * Math.Pow(a, 6) / 720));
        if (lat < 0) northing += 10_000_000;
        return (zone, easting, northing);
    }
}
