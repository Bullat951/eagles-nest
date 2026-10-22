using System.Globalization;
using System.IO;
using Dcb.Client.Core;
using Microsoft.Data.Sqlite;

namespace Dcb.Client.Map.Airfields;

/// <summary>Airport identifiers from the C-130J module's navigation database
/// (<c>Mods/aircraft/C130J/Cockpit/Resources/nav_data.db</c>, SQLite: airports with ICAO code
/// and position, runways with headings) — the codes the aircraft's FMS uses. Read-only, local
/// only (the module's data, never exported); empty when the module is not installed.</summary>
public static class NavDatabase
{
    public static string PathIn(string dcs) => Path.Combine(dcs, "Mods", "aircraft", "C130J", "Cockpit", "Resources", "nav_data.db");

    public static IReadOnlyList<IcaoMatch.Airport> Airports(string dcs)
    {
        var path = PathIn(dcs);
        if (!File.Exists(path)) return [];
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
            using var con = new SqliteConnection(cs);
            con.Open();
            var headings = new Dictionary<string, List<double>>();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "select icao, hdg from runways";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0) || r.IsDBNull(1)) continue;
                    if (!double.TryParse(Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) continue;
                    var icao = r.GetString(0);
                    if (!headings.TryGetValue(icao, out var list)) headings[icao] = list = [];
                    list.Add(h);
                }
            }
            var airports = new List<IcaoMatch.Airport>();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "select icao, lat, lon from airports";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (r.IsDBNull(0) || r.IsDBNull(1) || r.IsDBNull(2)) continue;
                    var icao = r.GetString(0);
                    if (icao.Length != 4 || !headings.TryGetValue(icao, out var hs)) continue;  // airfields with runways only
                    airports.Add(new IcaoMatch.Airport(icao, r.GetDouble(1), r.GetDouble(2), hs));
                }
            }
            return airports;
        }
        catch (Exception)
        {
            return [];  // other module version / layout: chart codes only
        }
    }
}
