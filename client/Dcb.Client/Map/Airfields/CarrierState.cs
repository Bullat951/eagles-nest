using Dcb.Client.Core;

namespace Dcb.Client.Map.Airfields;

/// <summary>A carrier's state for the carrier and LSO views: from the fast LSO feed when it is
/// running (aircraft near the ship), else from the picture (once a second).</summary>
public static class CarrierState
{
    public static LsoShip? Ship(long id)
    {
        var store = AppState.Store;
        if (store.Lso?.Carriers.FirstOrDefault(c => c.Id == id) is { } fast && store.Latest is { } p && p.Time - store.Lso.Time < 3) return fast;
        if (store.Find(id)?.Unit is not { Lat: { } lat, Lon: { } lon } u || store.Theatre is not { } theatre) return null;
        var (x, z) = theatre.FromLatLon(lat, lon);
        var hdg = u.Hdg ?? 0;
        var spd = u.Spd ?? 0;
        var h = hdg * Math.PI / 180;
        return new LsoShip(id, x, 0, z, hdg, Math.Cos(h) * spd, Math.Sin(h) * spd);
    }

    /// <summary>Grid → true north at the ship (meridian convergence), degrees.</summary>
    public static double GridToTrue(LsoShip ship) =>
        AppState.Store.Theatre is { } t ? new LocalFrame(t, ship.X, ship.Z).GridNorthBearing : 0;

    /// <summary>Magnetic variation at the ship, degrees east; null without a model.</summary>
    public static double? Variation(LsoShip ship)
    {
        if (AppState.Store.Theatre is not { MagVar: { } mv } t) return null;
        var (lat, lon) = t.ToLatLon(ship.X, ship.Z);
        return mv.At(lat, lon);
    }

    /// <summary>A grid bearing as a magnetic one "071°" (or true "071°T" without variation).</summary>
    public static string Mag(LsoShip ship, double gridDeg)
    {
        var trueDeg = Nav.Normalize(gridDeg + GridToTrue(ship));
        return Variation(ship) is { } v ? $"{Nav.Bearing3(Nav.Magnetic(trueDeg, v))}°" : $"{Nav.Bearing3(trueDeg)}°T";
    }

    /// <summary>Wind over the deck: the wind (from, true; m/s) less the ship's motion, split into
    /// the component down the landing centreline (headwind +) and across it (from starboard +), kt.</summary>
    public static (double Knots, double AxialKt, double CrossKt) WindOverDeck(LsoShip ship, CarrierInfo info, CarrierGeometry g)
    {
        // Air velocity (where the air goes), grid frame: the wind's "from" is true.
        var toGrid = (info.WindFrom + 180 - GridToTrue(ship)) * Math.PI / 180;
        double ax = Math.Cos(toGrid) * info.WindMs, az = Math.Sin(toGrid) * info.WindMs;
        // Relative to the ship.
        double rx = ax - ship.Vx, rz = az - ship.Vz;
        var l = Groove.FinalBearingGrid(ship, g) * Math.PI / 180;
        double lx = Math.Cos(l), lz = Math.Sin(l), sx = -Math.Sin(l), sz = Math.Cos(l);
        var axial = -(rx * lx + rz * lz);   // air coming down the deck from the bow end: headwind
        var cross = -(rx * sx + rz * sz);   // air coming from starboard
        var kt = Groove.KnotsPerMs;
        return (Math.Sqrt(rx * rx + rz * rz) * kt, axial * kt, cross * kt);
    }
}
