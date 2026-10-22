using System.Globalization;

namespace Dcb.Client.Core;

/// <summary>The sun's elevation above the horizon (simplified solar position, ±0.5°: enough
/// to tell day from night for the recovery case).</summary>
public static class Sun
{
    public static double ElevationDeg(DateTime utc, double latDeg, double lonDeg)
    {
        var n = utc.DayOfYear;
        var declination = 23.44 * Math.Sin(Rad(360.0 / 365 * (284 + n)));
        var b = Rad(360.0 / 365 * (n - 81));
        var equationOfTimeMin = 9.87 * Math.Sin(2 * b) - 7.53 * Math.Cos(b) - 1.5 * Math.Sin(b);
        var solarHours = utc.TimeOfDay.TotalHours + lonDeg / 15 + equationOfTimeMin / 60;
        var hourAngle = Rad(15 * (solarHours - 12));
        double lat = Rad(latDeg), dec = Rad(declination);
        var sinEl = Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(hourAngle);
        return Math.Asin(Math.Clamp(sinEl, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>The mission's UTC date and time from the weather's date ("2025-07-01"), the
    /// mission time of day (local seconds) and the theatre's offset; null when either is unknown.</summary>
    public static DateTime? MissionUtc(WeatherMessage? wx, double? missionSeconds, string? theatreId)
    {
        if (wx is null || missionSeconds is not { } local || theatreId is null) return null;
        if (!DateTime.TryParseExact(wx.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        if (AtisText.UtcOffsetHours(theatreId) is not { } offset) return null;
        return DateTime.SpecifyKind(date.AddSeconds(local).AddHours(-offset), DateTimeKind.Utc);
    }

    private static double Rad(double deg) => deg * Math.PI / 180;
}
