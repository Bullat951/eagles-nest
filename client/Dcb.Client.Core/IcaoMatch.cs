namespace Dcb.Client.Core;

/// <summary>Real-world airport identifiers (ICAO) for DCS airfields, matched by position and
/// runway direction against an airport list (e.g. the C-130J's nav database).</summary>
public static class IcaoMatch
{
    /// <summary>An airport of the list: identifier, position, runway headings (true, any end).</summary>
    public sealed record Airport(string Icao, double Lat, double Lon, IReadOnlyList<double> RunwayHeadings);

    /// <summary>The identifier for a field at (lat, lon) with these runway headings (true): the
    /// nearest airport within 6 km whose runways run the same way (±6°, either end), else the
    /// nearest within 3 km; null when none.</summary>
    public static string? For(double lat, double lon, IReadOnlyList<double> headings, IEnumerable<Airport> airports)
    {
        Airport? aligned = null, nearest = null;
        double bestAligned = 6000, bestNearest = 3000;
        foreach (var a in airports)
        {
            var d = Nav.BearingRange(lat, lon, a.Lat, a.Lon).Meters;
            if (d < bestNearest) (bestNearest, nearest) = (d, a);
            if (d < bestAligned && headings.Count > 0 && headings.Any(h => a.RunwayHeadings.Any(r => AxisDiff(h, r) <= 6)))
                (bestAligned, aligned) = (d, a);
        }
        return (aligned ?? nearest)?.Icao;
    }

    /// <summary>Angle between two runway axes (degrees, 0–90): 100° and 280° are the same axis.</summary>
    public static double AxisDiff(double a, double b)
    {
        var d = Math.Abs(((a - b) % 180 + 180) % 180);
        return Math.Min(d, 180 - d);
    }
}
