using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dcb.Client.Core;

namespace Dcb.Client.Map.Airfields;

/// <summary>Names lifted from placed airport charts (taxiway letters, areas), stored by
/// tools/DiagramIndexer in the diagrams folder's <c>diagrams.json</c>, already in DCS metres.
/// Local only: the names come from third-party charts.</summary>
public static class ChartLabels
{
    private sealed class Entry
    {
        [JsonPropertyName("airfieldId")] public int? AirfieldId { get; set; }
        [JsonPropertyName("variant")] public string Variant { get; set; } = "day";
        [JsonPropertyName("labels")] public List<Label>? Labels { get; set; }
        [JsonPropertyName("tower")] public string? Tower { get; set; }
        [JsonPropertyName("icao")] public string? Icao { get; set; }
    }

    private sealed record Chart(IReadOnlyList<DiagramLabel> Labels, string? Tower, string? Icao);

    private sealed class Label
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("x")] public double X { get; set; }
        [JsonPropertyName("z")] public double Z { get; set; }
    }

    private sealed class IndexFile
    {
        [JsonPropertyName("theatre")] public string Theatre { get; set; } = "";
        [JsonPropertyName("diagrams")] public List<Entry> Diagrams { get; set; } = [];
    }

    private static readonly Dictionary<string, Dictionary<int, Chart>> Cache = [];

    public static IReadOnlyList<DiagramLabel> For(Airfield field) => ChartFor(field)?.Labels ?? [];

    /// <summary>Tower frequencies (VHF/UHF, MHz) printed on the chart, e.g. "251.70 128.00".</summary>
    public static string? TowerFor(Airfield field) => ChartFor(field)?.Tower;

    /// <summary>The ICAO code printed on the chart, if read.</summary>
    public static string? IcaoFor(Airfield field) => ChartFor(field)?.Icao is { Length: 4 } icao ? icao : null;

    private static Chart? ChartFor(Airfield field)
    {
        if (AppState.Store.Theatre is not { } theatre || !AppState.Settings.Diagrams.TryGetValue(theatre.Id, out var folder)) return null;
        lock (Cache)
        {
            if (!Cache.TryGetValue(folder, out var byField))
            {
                byField = [];
                try
                {
                    var index = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(Path.Combine(folder, "diagrams.json")));
                    foreach (var e in index?.Diagrams ?? [])
                    {
                        if (e.AirfieldId is not { } id || e.Variant != "day" || e.Labels is null || byField.ContainsKey(id)) continue;
                        byField[id] = new Chart(e.Labels.Select(l => new DiagramLabel(l.Text, l.Kind, l.X, l.Z)).ToList(), e.Tower, e.Icao);
                    }
                }
                catch (Exception)
                {
                    // No or unreadable index: the diagram simply has no chart names.
                }
                Cache[folder] = byField;
            }
            return byField.GetValueOrDefault(field.Id);
        }
    }

    public static void Forget(string folder)
    {
        lock (Cache) Cache.Remove(folder);
    }
}
