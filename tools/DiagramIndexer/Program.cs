// DiagramIndexer: matches airport-diagram images (e.g. CombatWombat's kneeboard pages) to the
// airfields a DCB server reports and computes, per diagram, the transform from image pixels
// to DCS map metres, so the client can draw the picture under live data.
//
// usage: DiagramIndexer index <diagram folder> <host> <port> <password> [out.json]
//        DiagramIndexer probe <image.png>
// Writes <diagram folder>/diagrams.json (or out.json). Local use only: the diagrams are
// third-party artwork and are never sent anywhere.
using System.Text.Json;
using DiagramIndexer;
using Dcb.Client.Core;

if (args.Length >= 2 && args[0] == "probe")
{
    var page = await Page.Load(args[1]);
    foreach (var (x, y, text) in await Ocr.Lines(page)) Console.WriteLine($"[{x,4:F0},{y,4:F0}] {text}");
    var bars = RunwayBars.Find(page);
    foreach (var bar in bars) Console.WriteLine($"bar: {bar}");
    foreach (var (box, text) in await Ocr.LinesScaled(args[1], 3)) Console.WriteLine($"3x [{box.X,4:F0},{box.Y,4:F0} {box.Width:F0}x{box.Height:F0}] {text}");
    foreach (var b in ChartText.Badges(page, bars))
    {
        var read = ChartText.ReadBadge(page, b);
        var mark = read is { Score: >= ChartText.MinScore } ? "" : " (rejected)";
        Console.WriteLine($"taxiway [{b.X0},{b.Y0} {b.X1 - b.X0 + 1}x{b.Y1 - b.Y0 + 1}] '{read?.Text}' {read?.Score:F2}{mark}");
    }
    return 0;
}
if (args.Length < 5 || args[0] != "index")
{
    Console.Error.WriteLine("usage: DiagramIndexer index <diagram folder> <host> <port> <password> [out.json]");
    return 2;
}

var folder = args[1];
var outPath = args.Length > 5 ? args[5] : Path.Combine(folder, "diagrams.json");
var (theatre, airfields) = await Server.FetchAirfields(args[2], int.Parse(args[3]), args[4]);
Console.WriteLine($"server: {theatre.Id}, {airfields.Count} airfields");

var aliases = Aliases.For(theatre.Id);
var entries = new List<DiagramEntry>();
foreach (var file in Directory.EnumerateFiles(folder, "*.png").Order())
{
    var page = await Page.Load(file);
    var lines = await Ocr.Lines(page);
    var text = string.Join(" | ", lines.Select(l => l.Text));
    if (!text.Contains("DIAGRAM", StringComparison.OrdinalIgnoreCase)) continue;

    var title = Titles.Find(lines);
    var bars = RunwayBars.Find(page);
    var match = Matcher.Best(title, bars, airfields, aliases);
    var entry = new DiagramEntry
    {
        File = Path.GetFileName(file),
        Title = title?.Name,
        Icao = title?.Icao,
        Bars = bars.Count,
    };
    if (match is { } m)
    {
        entry.Airfield = m.Field.Name;
        entry.AirfieldId = m.Field.Id;
        entry.NameScore = Math.Round(m.NameScore, 2);
        if (Georef.Fit(m.Field, bars) is { } fit)
        {
            entry.Transform = fit.Coefficients;
            entry.ResidualM = Math.Round(fit.ResidualM, 1);
            entry.MetersPerPixel = Math.Round(fit.MetersPerPixel, 3);
            entry.RotationDeg = Math.Round(fit.RotationDeg, 2);
            entry.RunwaysUsed = fit.RunwaysUsed;
            (entry.Labels, entry.Tower) = await ChartText.Read(page, bars, fit.Coefficients, m.Field);
        }
    }
    entries.Add(entry);
    Console.WriteLine($"{entry.File}: {entry.Title} ({entry.Icao}) → {entry.Airfield ?? "-"} bars={entry.Bars} " +
                      (entry.Transform is null ? "no fit" : $"fit {entry.MetersPerPixel} m/px rot {entry.RotationDeg}° residual {entry.ResidualM} m runways {entry.RunwaysUsed}/{match!.Field.Runways.Count} " +
                                                            $"labels {entry.Labels?.Count ?? 0} tower {entry.Tower ?? "-"}"));
}

// Night (NVG-green) pages repeat a day page's layout with a dark-green runway the bar
// detector skips: give them the placement of the day page for the same airfield.
foreach (var e in entries.Where(e => e.Transform is null && e.AirfieldId is not null))
{
    if (entries.FirstOrDefault(d => d.Transform is not null && d.AirfieldId == e.AirfieldId && d.Title == e.Title) is { } day)
    {
        e.Transform = day.Transform;
        e.MetersPerPixel = day.MetersPerPixel;
        e.RotationDeg = day.RotationDeg;
        e.ResidualM = day.ResidualM;
        e.Variant = "night";
        e.SameLayoutAs = day.File;
    }
}

var index = new DiagramIndex { Theatre = theatre.Id, Diagrams = entries };
File.WriteAllText(outPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"wrote {outPath}: {entries.Count} diagrams, {entries.Count(e => e.Transform is not null)} placed");
return 0;
