using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvalonDock.Layout;
using Dcb.Client.Panels;

namespace Dcb.Client;

/// <summary>Development aid, off unless started with --snapshot: connect, open a second map floated in its own
/// window, wait, save PNGs of every window, exit. Lets the UI be checked without a person.
/// usage: Dcb.Client.exe --snapshot &lt;dir&gt; &lt;host&gt; &lt;port&gt; &lt;password&gt; [seconds] [basemap|restore] [lat,lon,res[,coast]]
/// The basemap argument picks the floated map's base map, or "restore" to open nothing and
/// show the saved layout. The view argument centres the floated map; ",coast" adds the
/// Natural Earth coastline on top to check alignment. Layout and settings are saved on exit only when DCB_HOME is set,
/// so a snapshot never touches the real profile.</summary>
public static class DebugSnapshot
{
    public static bool Active { get; private set; }
    public static bool Persist => Environment.GetEnvironmentVariable("DCB_HOME") is { Length: > 0 };

    /// <summary>"…+q" modes: for a minute, every 2 s, queue whatever the runway queue suggests.</summary>
    private static async Task QueueSuggested(AtcPanel atc)
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            atc.Queue.AddAllSuggested();
        }
    }

    public static void TryStart(MainWindow window, string[] args)
    {
        if (args.Length < 5 || args[0] != "--snapshot") return;
        Active = true;
        var dir = args[1];
        var seconds = args.Length > 5 ? int.Parse(args[5]) : 10;
        var mode = args.Length > 6 ? args[6] : nameof(BaseMap.Scope);
        List<string> zoomReport = [];
        window.Loaded += async (_, _) =>
        {
            _ = AppState.Connection.ConnectAsync(args[2], int.Parse(args[3]), args[4]);
            await Task.Delay(TimeSpan.FromSeconds(3));

            if (mode.StartsWith("atis:"))
            {
                // Open the ATIS send window for an airfield, and send one message_unit to the
                // first own aircraft through the client's command path: "atis:<airfield>".
                await Task.Delay(TimeSpan.FromSeconds(65));  // the weather comes once a minute
                if (AppState.Store.Airfields.FirstOrDefault(a => a.Name.Equals(mode[5..], StringComparison.OrdinalIgnoreCase)) is { } field)
                {
                    new SendMessageWindow(field, null) { Owner = window }.Show();
                    var own = AppState.Store.Tracks.Select(t => t.Unit).FirstOrDefault(u => !u.Detected && u.Category == "airplane");
                    if (own is not null)
                    {
                        var r = await AppState.Connection.SendCommandAsync("message_unit", new Dictionary<string, object>
                            { ["unit"] = own.Id, ["text"] = "DCB snapshot test", ["seconds"] = 5 });
                        zoomReport.Add($"atis: command to {own.Name}: ok={r.Ok} error={r.Error}");
                    }
                    zoomReport.Add($"atis: weather {(AppState.Store.Weather is null ? "missing" : "present")}");
                }
            }
            else if (mode.StartsWith("srstab"))
            {
                // The SRS tab against an SRS server: "srstab[:<SRS user to listen to>]"; the EAM
                // password comes from DCB_SNAPSHOT_EAM. Channels 1, 2, 5 and package 1 are set.
                var listen = Srs.SrsListen.Instance;
                listen.SessionPassword = Environment.GetEnvironmentVariable("DCB_SNAPSHOT_EAM");
                listen.Reconnect();
                await Task.Delay(TimeSpan.FromSeconds(3));
                Srs.SrsChannels.Set(1, 251.0);
                Srs.SrsChannels.Set(2, 124.5);
                Srs.SrsChannels.Set(Core.ChannelPlan.Atc, 250.6);
                Srs.SrsChannels.Set(Core.ChannelPlan.ForPackage(1), 262.5);
                Srs.SrsChannels.Transmit(1);
                if (mode.Split(':') is [_, var who])
                {
                    Panels.SrsPanel.DebugListenName = who;
                    AppState.ListenTo(AppState.Store.Tracks.FirstOrDefault(t => !t.Unit.Detected && t.Unit.Category == "airplane")?.Unit.Id);
                }
                window.ShowSrs();
                await Task.Delay(TimeSpan.FromSeconds(2));
                zoomReport.Add($"srstab: {listen.Status} | roster {listen.Roster.Own.Count()} own users");
            }
            else if (mode.StartsWith("views:"))
            {
                // The Diagram and Approach views as panels beside each other, for "views:<airfield>".
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (AppState.Store.Airfields.FirstOrDefault(a => a.Name.Equals(mode[6..], StringComparison.OrdinalIgnoreCase)) is { } field)
                    AppState.SelectAirfield(field.Id);
                window.ShowAtcView(Panels.AtcViewPanel.Diagram);
                window.ShowAtcView(Panels.AtcViewPanel.Approach);
            }
            else if (mode == "dcsfolder")
            {
                // The first-start DCS location dialog with what the search finds on this PC.
                DcsLocationWindow.ShowForSnapshot(window);
                zoomReport.Add("dcsfolder: " + string.Join(" | ", Map.Charts.DcsInstall.Search().Select(c => $"{c.Path} ({c.Source}: {string.Join(",", c.Terrains)})")));
            }
            else if (mode == "perf")
            {
                // The UI-thread cost per picture with the busy panels on show: the ATC tab on a
                // carrier (plan, queue), SRS and Packages beside the map. Timings in status.txt.
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (window.FloatPanel("atc") is AtcPanel atc)
                    for (int tries = 0; tries < 10 && !atc.Select("Stennis"); tries++) await Task.Delay(1000);
                window.ShowPackages();
                window.ShowSrs();
                RenderStats.Reset();
            }
            else if (mode == "packages")
            {
                // Two own airborne aircraft into P1, two into P2 (frequencies set), the Packages
                // panel floated, the main map centred on the first.
                await Task.Delay(TimeSpan.FromSeconds(5));
                var own = AppState.Store.Tracks.Where(t => !t.Unit.Detected && !t.OnGround && t.Unit.Category == "airplane")
                    .OrderBy(t => t.Unit.Id).Select(t => t.Unit.Id).Take(4).ToList();
                for (var k = 0; k < own.Count; k++) AppState.Packages.Add(k < 2 ? 1 : 2, own[k]);
                (AppState.Packages[1].Mhz, AppState.Packages[2].Mhz) = (251.0, 262.5);
                AppState.Packages.Edited();
                var panel = window.ShowPackages() as FrameworkElement;
                if (own.Count > 0 && Descendants(window).OfType<MapPanel>().FirstOrDefault() is { } main) main.CenterOn(own[0]);
                await Task.Delay(TimeSpan.FromSeconds(3));
                zoomReport.Add($"packages: {own.Count} aircraft packaged; panel {panel?.GetType().Name ?? "null"} loaded={panel?.IsLoaded} " +
                    $"{panel?.ActualWidth:0}x{panel?.ActualHeight:0} in {(panel is null ? "-" : Window.GetWindow(panel)?.GetType().Name)}");
            }
            else if (mode.StartsWith("workspace:"))
            {
                // Open a workspace window: "workspace:GCI" or "workspace:ATC".
                await Task.Delay(TimeSpan.FromSeconds(1));
                MainWindow.OpenWorkspace(mode["workspace:".Length..]);
            }
            else if (mode.StartsWith("srs:"))
            {
                // Float the SRS tab and send one tune command: "srs:<radio>:<MHz>[:<airfield>]",
                // the airfield picked as if in the ATC tab.
                await Task.Delay(TimeSpan.FromSeconds(2));
                window.FloatPanel("srs");
                var t = mode.Split(':');
                Srs.SrsBridge.Instance.Tune(int.Parse(t[1]), double.Parse(t[2], System.Globalization.CultureInfo.InvariantCulture));
                if (t.Length > 3 && AppState.Store.Airfields.FirstOrDefault(a => a.Name.Equals(t[3], StringComparison.OrdinalIgnoreCase)) is { } field)
                    AppState.SelectAirfield(field.Id);
            }
            else if (mode.StartsWith("lso:") || mode.StartsWith("carrier:"))
            {
                // Float the ATC tab and pick a carrier: "carrier:<name>[@<NM>]" or "lso:<name>" (e.g. Stennis, Stennis@3).
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (window.FloatPanel("atc") is AtcPanel atc)
                {
                    var name = mode[(mode.IndexOf(':') + 1)..];
                    if (name.EndsWith("+q"))
                    {
                        name = name[..^2];
                        _ = QueueSuggested(atc);
                    }
                    if (name.Split('@') is [var n, var nm] && double.TryParse(nm, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var range))
                    {
                        name = n;
                        atc.CarrierView.RangeNm = range;
                    }
                    for (int tries = 0; tries < 10 && !atc.Select(name); tries++) await Task.Delay(1000);  // carriers come with the pictures
                    if (mode.StartsWith("lso:")) atc.ShowLso();
                }
            }
            else if (mode.StartsWith("atc:") || mode.StartsWith("approach:"))
            {
                // Float the ATC tab and pick an airfield in it: "atc:<airfield name>" (diagram)
                // or "approach:<airfield name>" (the 10 NM approach view).
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (window.FloatPanel("atc") is AtcPanel atc)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    var name = mode[(mode.IndexOf(':') + 1)..];
                    if (name.EndsWith("+q"))
                    {
                        name = name[..^2];
                        _ = QueueSuggested(atc);
                    }
                    atc.Select(name);
                    if (mode.StartsWith("approach:")) atc.ShowApproach();
                    // Like dragging the pane around: its window passes through tiny sizes.
                    if (Environment.GetEnvironmentVariable("DCB_SNAPSHOT_SHRINK") == "1" && Window.GetWindow(atc) is { } floating)
                    {
                        foreach (var (w, h) in new[] { (60.0, 40.0), (200.0, 90.0), (430.0, 150.0), (300.0, 600.0), (990.0, 824.0) })
                        {
                            floating.Width = w;
                            floating.Height = h;
                            floating.UpdateLayout();
                            atc.ShowApproach();
                            floating.UpdateLayout();
                            atc.ShowDiagram();
                            floating.UpdateLayout();
                            await Task.Delay(200);
                        }
                        if (mode.StartsWith("approach:")) atc.ShowApproach();
                    }
                    // Optional lat,lon,res: where the docked main map looks meanwhile.
                    if (args.Length > 7 && Descendants(window).OfType<MapPanel>().FirstOrDefault() is { } main)
                    {
                        var v = args[7].Split(',');
                        main.ShowAt(double.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture),
                            double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture),
                            double.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
            else if (mode != "restore")
            {
                var second = window.OpenMap(Enum.Parse<BaseMap>(mode));
                await Task.Delay(TimeSpan.FromSeconds(2));  // like a user: the tab is shown before it is dragged out
                second.Float();
                if (args.Length > 7 && second.Content is MapPanel panel)
                {
                    var v = args[7].Split(',');
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    panel.ShowAt(double.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture));
                    if (v.Length > 3 && v[3] == "coast") panel.ShowCoastlineOverlay();
                    if (v.Length > 3 && v[3] == "grid") panel.SetGrid(true);
                    if (v.Length > 3 && v[3].StartsWith("dim")) panel.SetBrightness(double.Parse(v[3][3..], System.Globalization.CultureInfo.InvariantCulture));
                    if (v.Length > 3 && v[3].StartsWith("zone=")) panel.CenterOnZone(v[3][5..], westEdge: true);  // keeps the resolution
                    if (args.Length > 8 && args[8] == "point")
                    {
                        // Alt+click at the view centre (test aid).
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        panel.ShowPoint(new Point(panel.Surface.ActualWidth / 2, panel.Surface.ActualHeight / 2));
                    }
                    if (args.Length > 8 && args[8] == "settletest")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                        zoomReport = await SettleTest(panel);
                    }
                    if (args.Length > 8 && args[8] == "switchtest")
                    {
                        // Flip through the tile-heavy base maps at close zoom (each queues many
                        // tiles), then the DCS Map: do its tiles still come first?
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                        panel.ShowAt(34.70, 36.62, 8);
                        foreach (var b in new[] { BaseMap.DcsSatellite, BaseMap.Satellite, BaseMap.DcsCharts, BaseMap.DcsSatellite, BaseMap.Satellite })
                        {
                            panel.SetBaseMap(b);
                            await Task.Delay(1500);
                        }
                        panel.SetBaseMap(BaseMap.DcsMap);
                        zoomReport = await SettleTest(panel);
                        zoomReport.Insert(0, "switchtest: DCS Satellite, Satellite, DCS Charts, DCS Satellite, Satellite (1.5 s each), then DCS Map");
                    }
                    if (args.Length > 8 && args[8] == "wheeltest")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                        zoomReport = await WheelTest(panel, panel.Surface);
                    }
                    if (args.Length > 8 && args[8] == "zoomtest")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(seconds));  // let tiles load first
                        zoomReport = await ZoomTest(panel, double.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture),
                            double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture),
                            double.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    if (args.Length > 8 && args[8] == "measure")
                    {
                        // Ruler from the bullseye to the first own aircraft (BRAA), bulls readout
                        // 120 DIP right of the view centre.
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        var bulls = AppState.Store.Latest?.Bullseye;
                        var air = AppState.Store.Tracks.FirstOrDefault(t => t.Unit.Category is "airplane" && t.Unit.Lat is not null);
                        if (bulls is not null && air is not null)
                        {
                            // Frame both ends.
                            double midLat = (bulls.Lat + air.Unit.Lat!.Value) / 2, midLon = (bulls.Lon + air.Unit.Lon!.Value) / 2;
                            var spanM = Math.Max(Math.Abs(bulls.Lat - air.Unit.Lat!.Value) * 111_000 * 1.25, Math.Abs(bulls.Lon - air.Unit.Lon!.Value) * 111_000);
                            panel.ShowAt(midLat, midLon, Math.Max(20, spanM * 1.6 / Math.Max(300, panel.ActualWidth)));
                            await Task.Delay(TimeSpan.FromSeconds(1));
                            var (bx, by) = panel.DebugScreen(bulls.Lat, bulls.Lon);
                            var (ax, ay) = panel.DebugScreen(air.Unit.Lat!.Value, air.Unit.Lon!.Value);
                            panel.DebugMeasure(new Point(bx, by), new Point(ax, ay), new Point(panel.ActualWidth / 2 + 120, panel.ActualHeight / 2));
                            zoomReport.Add($"measure: bulls {bulls.Lat:F4},{bulls.Lon:F4} -> {air.Unit.Name} {air.Unit.Lat:F4},{air.Unit.Lon:F4} hdg {air.Unit.Hdg:F0} alt {air.Unit.AltFt:F0} ft");
                        }
                        else zoomReport.Add($"measure: bulls={bulls is not null} aircraft={air is not null}");
                    }
                    if (args.Length > 8 && args[8].StartsWith("gci"))
                    {
                        // "gci" or "gci+send": the triangulation board on the first own aircraft.
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        var air = AppState.Store.Tracks.FirstOrDefault(t => !t.Unit.Detected && t.Unit.Category is "airplane" && t.Unit.Hdg is not null && !t.OnGround);
                        zoomReport.Add(air is null ? "gci: no own aircraft in the air" : await panel.DebugGci(air.Unit.Id, args[8] == "gci+send"));
                        await Task.Delay(TimeSpan.FromSeconds(6));  // the marks feed comes back within ~5 s
                        zoomReport.Add($"gci: marks {AppState.Board.Marks.Count}, dcb {AppState.Board.Marks.Count(m => m.Dcb)}, threats {AppState.Board.AllThreats().Count}");
                    }
                    if (args.Length > 8 && args[8] == "pantest")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                        zoomReport = await PanTest(panel, double.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture),
                            double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture),
                            double.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(seconds));

            Directory.CreateDirectory(dir);
            var i = 0;
            // Memory after a full collection: comparable between runs, unlike raw private bytes.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var info = new List<string>
            {
                AppState.Connection.Status, $"tracks={AppState.Store.Tracks.Count}",
                $"timings (count, avg, max ms): {string.Join(" | ", RenderStats.Report().Where(r => r.StartsWith("UI:") || r.StartsWith("panel:") || r.StartsWith("ObjectsPanel")))}",
                $"memory: managed {GC.GetTotalMemory(false) / 1048576} MB, private {proc.PrivateMemorySize64 / 1048576} MB (after full GC)",
            };
            foreach (Window w in Application.Current.Windows)
            {
                var name = w == window ? "main.png" : $"window{++i}.png";
                info.Add($"{name}: {w.GetType().Name} {w.ActualWidth:F0}x{w.ActualHeight:F0} visible={w.IsVisible}");
                foreach (var map in Descendants(w).OfType<MapPanel>())
                {
                    var (cx, cy, res) = map.ViewCenter;
                    info.Add($"  MapPanel {map.ContentId[..12]} {map.ActualWidth:F0}x{map.ActualHeight:F0} visible={map.IsVisible} loaded={map.IsLoaded} " +
                             $"viewport {cx:F0},{cy:F0} res {res:F2} {Renderer(map)}");
                }
                Save(w, Path.Combine(dir, name));
            }
            // Floating panes live in their own HwndSource (AvalonDock hosts them via HwndHost),
            // invisible to the window's visual tree: capture every source's root as well.
            var s = 0;
            foreach (var source in PresentationSource.CurrentSources.OfType<PresentationSource>())
            {
                if (source.RootVisual is not FrameworkElement root || root is Window) continue;
                var maps = Descendants(root).Where(d => d is MapPanel or AtcPanel or SrsPanel or Panels.AtcViewPanel).ToList();
                if (maps.Count == 0 && root.GetType().Name != "PopupRoot") continue;  // pop-ups too (Alt+click point)
                var name = $"source{++s}.png";
                info.Add($"{name}: {root.GetType().Name} {root.ActualWidth:F0}x{root.ActualHeight:F0} maps={maps.Count}");
                foreach (var map in maps.OfType<MapPanel>())
                {
                    var (cx, cy, res) = map.ViewCenter;
                    info.Add($"  MapPanel {map.ContentId[..12]} viewport {cx:F0},{cy:F0} res {res:F2} {Renderer(map)}");
                }
                foreach (var diagram in Descendants(root).OfType<Map.Airfields.AirfieldDiagramView>())
                    info.Add($"  Diagram {diagram.DebugState}");
                Save(root, Path.Combine(dir, name));
            }
            info.AddRange(zoomReport);
            File.WriteAllLines(Path.Combine(dir, "status.txt"), info);
            window.Close();  // through OnClosing, so the layout is saved when Persist
            Application.Current.Shutdown();
        };
    }

    /// <summary>Zooms in and out (and pans) in 60 steps at ~60 Hz like a mouse wheel.</summary>
    private static Task<List<string>> ZoomTest(MapPanel panel, double lat, double lon, double res) =>
        Steps(panel, "zoomtest", 60, i =>
        {
            var k = i < 30 ? i : 59 - i;  // in for 30 steps, back out for 30
            panel.ShowAt(lat + i * 0.002, lon + i * 0.003, res * Math.Pow(0.92, k));
        });

    /// <summary>Pans at a fixed zoom in 90 steps at ~60 Hz like a mouse drag.</summary>
    private static Task<List<string>> PanTest(MapPanel panel, double lat, double lon, double res)
    {
        var degPerStep = res * 8 / 111_000;  // ~8 DIP per step
        return Steps(panel, "pantest", 90, i =>
        {
            var k = i < 45 ? i : 89 - i;
            panel.ShowAt(lat + k * degPerStep * 0.5, lon + k * degPerStep, res);
        });
    }

    /// <summary>Runs view steps paced at ~60 Hz. The honest figure is how long the steps take
    /// (16.7 ms each when the UI keeps up: 1.5 s for 90): a blocked UI thread stretches them.
    /// WPF's Rendering events fire even when frames are not drawn, so they are not counted as
    /// frames; the map surface reports the frames it painted and their cost.</summary>
    private static async Task<List<string>> Steps(MapPanel panel, string name, int steps, Action<int> step)
    {
        RenderStats.Reset();
        panel.Surface.ResetStats();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var next = 0.0;
        for (int i = 0; i < steps; i++)
        {
            step(i);
            next += 1000.0 / 60;
            var wait = next - sw.Elapsed.TotalMilliseconds;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, wait)));
        }
        var stepsMs = sw.ElapsedMilliseconds;
        await Task.Delay(300);
        var ideal = steps * 1000 / 60;
        var report = new List<string> { $"{name}: {steps} steps in {stepsMs} ms (ideal {ideal}) = {steps * 1000.0 / stepsMs:F0} steps/s, renderer {Renderer(panel)}" };
        var (frames, total, max) = panel.Surface.Stats;
        report.Add($"{name}: painted {frames} frames, paint avg {total / Math.Max(1, frames):F2} ms, max {max:F2} ms");
        report.AddRange(RenderStats.Report());
        return report;
    }

    /// <summary>Zooms like a mouse wheel through the smooth-zoom animation: 8 notches in, 120 ms
    /// apart, around a point off-centre, then 8 out; reports gaps between painted frames and
    /// frames drawn with tiles still missing.</summary>
    private static async Task<List<string>> WheelTest(MapPanel panel, Map.Gpu.MapSurface surface)
    {
        RenderStats.Reset();
        surface.ResetStats();
        double x = surface.ActualWidth * 0.6, y = surface.ActualHeight * 0.4;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var notches in new[] { 1, -1 })
        {
            for (int i = 0; i < 8; i++)
            {
                surface.Wheel(notches, x, y);
                await Task.Delay(120);
            }
            await Task.Delay(700);
        }
        var (frames, total, max) = surface.Stats;
        var (gap, over25, over50) = surface.Gaps;
        var report = new List<string>
        {
            $"wheeltest: 16 notches in {sw.ElapsedMilliseconds} ms, renderer {Renderer(panel)}",
            $"wheeltest: painted {frames} frames, paint avg {total / Math.Max(1, frames):F2} ms, max {max:F2} ms; longest gap {gap:F0} ms, gaps >25 ms: {over25}, >50 ms: {over50}",
        };
        report.AddRange(RenderStats.CounterReport());
        report.AddRange(surface.GapLog);
        report.AddRange(RenderStats.Report());
        return report;
    }

    /// <summary>DCS Map: how long after a view change until every tile of the wanted level is on
    /// screen. Jumps into close-up areas (tiles rendered on demand), then zooms in and out.</summary>
    private static async Task<List<string>> SettleTest(MapPanel panel)
    {
        RenderStats.Reset();
        var report = new List<string>();
        (string Name, double Lat, double Lon, double Res)[] steps =
        [
            ("approach: Homs at city zoom", 34.73, 36.71, 40),
            ("jump to Homs, close (cold tiles)", 34.73, 36.71, 4),
            ("pan to next area", 34.76, 36.76, 4),
            ("zoom in 3x", 34.76, 36.76, 1.3),
            ("zoom out 8x", 34.76, 36.76, 10),
        ];
        foreach (var (name, lat, lon, res) in steps)
        {
            panel.ShowAt(lat, lon, res);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var framesAtStart = panel.Surface.Stats.Frames;
            int firstMissing = -1;
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(50);
                if (panel.Surface.Stats.Frames <= framesAtStart) continue;  // no frame of the new view yet
                if (firstMissing < 0) firstMissing = Map.Gpu.DcsMapSk.LastMissing;
                if (Map.Gpu.DcsMapSk.LastMissing == 0) break;
            }
            report.Add($"settletest: {name}: {sw.ElapsedMilliseconds} ms to complete ({firstMissing} tiles missing at first)");
            if (name.StartsWith("approach")) await Task.Delay(1500);  // a person zooming in steps
        }
        report.AddRange(RenderStats.CounterReport());
        report.AddRange(RenderStats.Report());
        return report;
    }

    private static string Renderer(MapPanel panel) => panel.Surface.UsesGpu ? "gpu" : "skia-cpu";

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static void Save(FrameworkElement w, string path)
    {
        if (w.ActualWidth < 1 || !w.IsVisible) return;
        var dpi = VisualTreeHelper.GetDpi(w);
        var bmp = new RenderTargetBitmap((int)(w.ActualWidth * dpi.DpiScaleX), (int)(w.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(w);
        // GPU maps are not in RenderTargetBitmap's picture: draw each one's own CPU render
        // over its place, then the WPF elements above the surface (attribution) again.
        var maps = Descendants(w).OfType<MapPanel>().Where(m => m.Surface.IsVisible).ToList();
        if (maps.Count > 0)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(bmp, new Rect(0, 0, w.ActualWidth, w.ActualHeight));
                foreach (var map in maps)
                {
                    var s = map.Surface;
                    using var image = s.RenderToImage((float)dpi.DpiScaleX);
                    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    var frame = BitmapFrame.Create(new MemoryStream(data.ToArray()), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var at = s.TransformToAncestor(w).Transform(new Point(0, 0));
                    dc.DrawImage(frame, new Rect(at.X, at.Y, s.ActualWidth, s.ActualHeight));
                    // The RWR clock is a WPF overlay above the surface: draw it again on top.
                    if (map.RwrCanvas.IsVisible)
                    {
                        var c = map.RwrCanvas;
                        var cAt = c.TransformToAncestor(w).Transform(new Point(0, 0));
                        var brush = new VisualBrush(c)
                        {
                            Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, c.ActualWidth, c.ActualHeight),
                        };
                        dc.DrawRectangle(brush, null, new Rect(cAt.X, cAt.Y, c.ActualWidth, c.ActualHeight));
                    }
                }
            }
            bmp = new RenderTargetBitmap(bmp.PixelWidth, bmp.PixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(visual);
        }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }
}
