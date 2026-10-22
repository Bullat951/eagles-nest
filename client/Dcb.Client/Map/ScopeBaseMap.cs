using System.IO;
using System.Text.Json;
using System.Windows;

namespace Dcb.Client.Map;

/// <summary>Offline vector base map (Natural Earth coastlines, borders, lakes clipped to DCS
/// theatres; built by tools/make_basemap.py). Loaded once, shared by every map window.</summary>
public sealed class ScopeBaseMapData
{
    public sealed record Line(double[] Xy, double MinX, double MinY, double MaxX, double MaxY);

    public IReadOnlyList<Line> Coast { get; }
    public IReadOnlyList<Line> Borders { get; }
    public IReadOnlyList<Line> Lakes { get; }

    private static readonly Lazy<ScopeBaseMapData> Shared = new(Load);
    public static ScopeBaseMapData Instance => Shared.Value;

    private ScopeBaseMapData(IReadOnlyList<Line> coast, IReadOnlyList<Line> borders, IReadOnlyList<Line> lakes)
    {
        Coast = coast;
        Borders = borders;
        Lakes = lakes;
    }

    private static ScopeBaseMapData Load()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/basemap.json"))!.Stream;
        using var doc = JsonDocument.Parse(stream);
        return new ScopeBaseMapData(Read(doc, "coast"), Read(doc, "borders"), Read(doc, "lakes"));
    }

    private static List<Line> Read(JsonDocument doc, string layer)
    {
        var lines = new List<Line>();
        foreach (var arr in doc.RootElement.GetProperty(layer).EnumerateArray())
        {
            var n = arr.GetArrayLength() / 2;
            var xy = new double[n * 2];
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                var (x, y) = Mercator.FromLonLat(arr[2 * i].GetDouble(), arr[2 * i + 1].GetDouble());
                xy[2 * i] = x;
                xy[2 * i + 1] = y;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            lines.Add(new Line(xy, minX, minY, maxX, maxY));
        }
        return lines;
    }
}
