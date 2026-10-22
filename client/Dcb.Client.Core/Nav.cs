namespace Dcb.Client.Core;

/// <summary>Controller navigation maths: bearings and ranges between lat/lon points, magnetic
/// bearings, bullseye calls and BRAA aspect. Bearings are given magnetic, as pilots fly them;
/// ranges in nautical miles.</summary>
public static class Nav
{
    public const double MetersPerNm = 1852;
    private const double EarthRadius = 6_371_008.8;  // mean radius, for the spherical fallback
    private const double WgsA = 6_378_137.0, WgsF = 1 / 298.257223563, WgsB = WgsA * (1 - WgsF);

    /// <summary>Initial bearing (true, 0..360) and distance (m) from 1 to 2 on the WGS84
    /// ellipsoid (Vincenty); a sphere only if that fails to converge (near-antipodal points).</summary>
    public static (double TrueDeg, double Meters) BearingRange(double lat1, double lon1, double lat2, double lon2) =>
        Vincenty(lat1, lon1, lat2, lon2) ?? Spherical(lat1, lon1, lat2, lon2);

    private static (double, double)? Vincenty(double lat1, double lon1, double lat2, double lon2)
    {
        var L = Rad(lon2 - lon1);
        double u1 = Math.Atan((1 - WgsF) * Math.Tan(Rad(lat1))), u2 = Math.Atan((1 - WgsF) * Math.Tan(Rad(lat2)));
        double sinU1 = Math.Sin(u1), cosU1 = Math.Cos(u1), sinU2 = Math.Sin(u2), cosU2 = Math.Cos(u2);
        double λ = L, sinσ, cosσ, σ, cos2α, cos2σm;
        for (int i = 0; ; i++)
        {
            double sinλ = Math.Sin(λ), cosλ = Math.Cos(λ);
            sinσ = Math.Sqrt(Math.Pow(cosU2 * sinλ, 2) + Math.Pow(cosU1 * sinU2 - sinU1 * cosU2 * cosλ, 2));
            if (sinσ == 0) return (0, 0);  // same point
            cosσ = sinU1 * sinU2 + cosU1 * cosU2 * cosλ;
            σ = Math.Atan2(sinσ, cosσ);
            var sinα = cosU1 * cosU2 * sinλ / sinσ;
            cos2α = 1 - sinα * sinα;
            cos2σm = cos2α == 0 ? 0 : cosσ - 2 * sinU1 * sinU2 / cos2α;  // on the equator
            var c = WgsF / 16 * cos2α * (4 + WgsF * (4 - 3 * cos2α));
            var prev = λ;
            λ = L + (1 - c) * WgsF * sinα * (σ + c * sinσ * (cos2σm + c * cosσ * (-1 + 2 * cos2σm * cos2σm)));
            if (Math.Abs(λ - prev) < 1e-12) break;
            if (i == 200) return null;
        }
        var uSq = cos2α * (WgsA * WgsA - WgsB * WgsB) / (WgsB * WgsB);
        var a = 1 + uSq / 16384 * (4096 + uSq * (-768 + uSq * (320 - 175 * uSq)));
        var b = uSq / 1024 * (256 + uSq * (-128 + uSq * (74 - 47 * uSq)));
        var dσ = b * sinσ * (cos2σm + b / 4 * (cosσ * (-1 + 2 * cos2σm * cos2σm) -
                 b / 6 * cos2σm * (-3 + 4 * sinσ * sinσ) * (-3 + 4 * cos2σm * cos2σm)));
        var meters = WgsB * a * (σ - dσ);
        var bearing = Math.Atan2(cosU2 * Math.Sin(λ), cosU1 * sinU2 - sinU1 * cosU2 * Math.Cos(λ)) * 180 / Math.PI;
        return (Normalize(bearing), meters);
    }

    private static (double TrueDeg, double Meters) Spherical(double lat1, double lon1, double lat2, double lon2)
    {
        double φ1 = Rad(lat1), φ2 = Rad(lat2), dλ = Rad(lon2 - lon1), dφ = φ2 - φ1;
        var y = Math.Sin(dλ) * Math.Cos(φ2);
        var x = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(dλ);
        var bearing = Normalize(Math.Atan2(y, x) * 180 / Math.PI);
        var a = Math.Sin(dφ / 2) * Math.Sin(dφ / 2) + Math.Cos(φ1) * Math.Cos(φ2) * Math.Sin(dλ / 2) * Math.Sin(dλ / 2);
        var meters = 2 * EarthRadius * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        return (bearing, meters);
    }

    /// <summary>The point <paramref name="meters"/> from (lat, lon) along a true bearing (sphere:
    /// within a few metres per 100 km of the ellipsoid, plenty for drawing).</summary>
    public static (double Lat, double Lon) Destination(double lat, double lon, double trueDeg, double meters)
    {
        double φ1 = Rad(lat), λ1 = Rad(lon), θ = Rad(trueDeg), δ = meters / EarthRadius;
        var φ2 = Math.Asin(Math.Sin(φ1) * Math.Cos(δ) + Math.Cos(φ1) * Math.Sin(δ) * Math.Cos(θ));
        var λ2 = λ1 + Math.Atan2(Math.Sin(θ) * Math.Sin(δ) * Math.Cos(φ1), Math.Cos(δ) - Math.Sin(φ1) * Math.Sin(φ2));
        return (φ2 * 180 / Math.PI, λ2 * 180 / Math.PI);
    }

    /// <summary>True → magnetic with the variation east positive (east variation: subtract).</summary>
    public static double Magnetic(double trueDeg, double variationDeg) => Normalize(trueDeg - variationDeg);

    /// <summary>Bearing as said on the radio: three digits, 360 for north ("005", "360").</summary>
    public static string Bearing3(double deg)
    {
        var b = (int)Math.Round(Normalize(deg));
        return (b == 0 ? 360 : b).ToString("000");
    }

    public static string RangeNm(double meters) => Math.Round(meters / MetersPerNm).ToString("0");

    /// <summary>Bullseye call for a point, e.g. "BE 247/38": magnetic bearing from the
    /// bullseye (variation taken at the bullseye) and range in NM.</summary>
    public static string BullseyeCall(Bullseye bulls, double lat, double lon, MagVarGrid? magvar)
    {
        var (brg, m) = BearingRange(bulls.Lat, bulls.Lon, lat, lon);
        var mag = Magnetic(brg, magvar?.At(bulls.Lat, bulls.Lon) ?? 0);
        return $"BE {Bearing3(mag)}/{RangeNm(m)}";
    }

    /// <summary>BRAA aspect of a target heading <paramref name="targetHdgTrue"/> as seen from a
    /// point that bears <paramref name="bearingTargetToObserverTrue"/> from it: how far the
    /// target's nose is off the observer.</summary>
    public static string Aspect(double targetHdgTrue, double bearingTargetToObserverTrue)
    {
        var off = Math.Abs(Normalize(targetHdgTrue - bearingTargetToObserverTrue + 180) - 180);
        return off switch
        {
            <= 30 => "HOT",
            <= 70 => "FLANK",
            <= 110 => "BEAM",
            _ => "DRAG",
        };
    }

    public static double Normalize(double deg) => (deg % 360 + 360) % 360;

    private static double Rad(double deg) => deg * Math.PI / 180;
}
