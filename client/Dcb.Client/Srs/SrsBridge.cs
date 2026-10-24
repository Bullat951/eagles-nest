using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Dcb.Client.Core;
using Dcb.Client.Map.Charts;

namespace Dcb.Client.Srs;

/// <summary>Talks to the controller's own SRS client over local UDP, the interface SRS offers
/// controller tools (docs/PLAN.md, phase 5a): our radio position every 2 s, answers to its
/// line-of-sight questions from the terrain grid, and radio commands (tune, select).</summary>
public sealed class SrsBridge
{
    public static SrsBridge Instance { get; } = new();

    private const string ToolName = "DCB";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly UdpClient _sender = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private SrsSettings Settings => AppState.Settings.Srs;
    private bool _started;

    /// <summary>What the panel shows.</summary>
    public string PositionStatus { get; private set; } = "Position: off";
    public string LineOfSightStatus { get; private set; } = "Line of sight: waiting for SRS";
    public string CommandStatus { get; private set; } = "";
    private int _losAnswered, _losBlocked;
    private DateTime _lastQuestion;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _timer.Tick += (_, _) => SendPosition();
        _timer.Start();
        Task.Run(ListenLineOfSight);
    }

    // ---- Position --------------------------------------------------------------------------

    /// <summary>Where our radio stands now (lat, lon, ground height m ASL) and what it is, or
    /// null when the position is off or unknown.</summary>
    public (double Lat, double Lon, double Alt, string Where)? Position()
    {
        var store = AppState.Store;
        if (store.Theatre is not { } theatre) return null;
        switch (Settings.Position)
        {
            case SrsPosition.AtcAirfield when AppState.SelectedAirfieldId is { } id && store.Airfields.FirstOrDefault(a => a.Id == id) is { } field:
            {
                var (lat, lon) = theatre.ToLatLon(field.X, field.Z);
                return (lat, lon, Ground(field.X, field.Z), field.Name);
            }
            case SrsPosition.Unit when Settings.PositionUnit is { } name
                && store.Tracks.FirstOrDefault(t => !t.Unit.Detected && t.Unit.Name == name)?.Unit is { Lat: { } ulat, Lon: { } ulon } unit:
            {
                // On the carrier or AWACS, at its own altitude (an AWACS sees far).
                var (ux, uz) = theatre.FromLatLon(ulat, ulon);
                return (ulat, ulon, Math.Max(unit.Alt ?? 0, Ground(ux, uz)), Symbology.NameOf(unit) ?? name);
            }
            case SrsPosition.Fixed when Settings.FixedLat is { } lat && Settings.FixedLon is { } lon:
            {
                var (x, z) = theatre.FromLatLon(lat, lon);
                return (lat, lon, Ground(x, z), $"{lat:F4}, {lon:F4}");
            }
            default:
                return null;
        }
    }

    private static double Ground(double x, double z) =>
        AppState.Store.Terrain is { } g && LineOfSight.HeightAt(g, x, z) is { } h ? Math.Max(0, h) : 0;

    private void SendPosition()
    {
        if (Position() is not { } p)
        {
            PositionStatus = Settings.Position switch
            {
                SrsPosition.Off => "Position: off (SRS uses no position for you)",
                SrsPosition.AtcAirfield => "Position: pick an airfield (SRS tab list or ATC tab)",
                SrsPosition.Unit => $"Position: {Settings.PositionUnit} is not in the picture",
                _ => "Position: set the fixed point (Set to map centre)",
            };
            return;
        }
        // SRS reads these fields by their exact names (no case folding).
        var json = $"{{\"controller\":{{\"latitude\":{p.Lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                   $"\"longitude\":{p.Lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                   $"\"altitude\":{p.Alt.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)}," +
                   $"\"isFlying\":false,\"displayName\":\"{ToolName}\"}}}}";
        Send(Settings.PositionPort, json);
        PositionStatus = $"Position: {p.Where}, {p.Alt:F0} m — sent {DateTime.Now:HH:mm:ss}";
    }

    // ---- Line of sight -----------------------------------------------------------------------

    private sealed record LosRequest(double Lat1, double Long1, double Alt1, double Lat2, double Long2, double Alt2, string? ClientId);

    private async Task ListenLineOfSight()
    {
        while (true)
        {
            UdpClient listener;
            try
            {
                listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, Settings.LineOfSightPort));
            }
            catch (SocketException)
            {
                LineOfSightStatus = $"Line of sight: port {Settings.LineOfSightPort} is in use (LotATC running?) — retrying";
                await Task.Delay(TimeSpan.FromSeconds(5));
                continue;
            }
            using (listener)
            {
                while (true)
                {
                    UdpReceiveResult result;
                    try
                    {
                        result = await listener.ReceiveAsync();
                    }
                    catch (SocketException)
                    {
                        break;  // reopen
                    }
                    foreach (var line in Encoding.UTF8.GetString(result.Buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        try
                        {
                            Answer(line);
                        }
                        catch (Exception ex)
                        {
                            // One bad question must not end the listener.
                            LineOfSightStatus = $"Line of sight: error answering ({ex.GetType().Name}: {ex.Message})";
                        }
                    }
                }
            }
        }
    }

    private void Answer(string line)
    {
        LosRequest? q;
        try
        {
            q = JsonSerializer.Deserialize<LosRequest>(line, Json);
        }
        catch (JsonException)
        {
            return;
        }
        _lastQuestion = DateTime.Now;
        var store = AppState.Store;
        // No terrain yet: no answer, and SRS keeps treating the path as clear.
        if (q?.ClientId is not { } id || store.Theatre is not { } theatre || store.Terrain is not { } grid)
        {
            LineOfSightStatus = "Line of sight: asked, but no terrain from the server yet";
            return;
        }
        var (x1, z1) = theatre.FromLatLon(q.Lat1, q.Long1);
        var (x2, z2) = theatre.FromLatLon(q.Lat2, q.Long2);
        var see = LineOfSight.Visible(grid, x1, z1, q.Alt1, x2, z2, q.Alt2);
        Send(Settings.PositionPort, $"{{\"los\":{{\"clientId\":{JsonSerializer.Serialize(id)},\"see\":{(see ? "true" : "false")}}}}}");
        _losAnswered++;
        if (!see) _losBlocked++;
        LineOfSightStatus = $"Line of sight: {_losAnswered} answered, {_losBlocked} blocked by terrain — last {_lastQuestion:HH:mm:ss}";
    }

    // ---- Radio commands --------------------------------------------------------------------

    /// <summary>Sets an SRS radio (1–10) to a frequency in MHz, and selects it to transmit when
    /// <paramref name="select"/> (default: the settings) says so. Works on radios SRS lets the
    /// overlay set (External AWACS Mode).</summary>
    public void Tune(int radio, double mhz, bool? select = null)
    {
        var f = mhz.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var transmit = select ?? Settings.SelectOnTune;
        if (DebugSnapshot.Active)
        {
            // A snapshot run must never retune the controller's real SRS app.
            CommandStatus = $"(snapshot) radio {radio} → {mhz:F3} MHz{(transmit ? ", transmit" : "")} not sent";
            return;
        }
        Send(Settings.CommandPort, $"{{\"Command\":12,\"RadioId\":{radio},\"Frequency\":{f}}}");  // FREQUENCY_SET
        if (transmit) Send(Settings.CommandPort, $"{{\"Command\":1,\"RadioId\":{radio}}}");  // ACTIVE_RADIO
        CommandStatus = $"Radio {radio} → {mhz:F3} MHz{(transmit ? ", transmitting on it" : "")} ({DateTime.Now:HH:mm:ss})";
    }

    private void Send(int port, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        try
        {
            _sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
        }
        catch (SocketException)
        {
            // Nothing listens (SRS not running): UDP to localhost may report it; try again later.
        }
    }

    // ---- Airfield frequencies ------------------------------------------------------------

    private static readonly Dictionary<string, Dictionary<int, IReadOnlyList<AirfieldFrequency>>?> Radios = [];

    /// <summary>Tower frequencies of an airfield from the local install's terrain radio.lua.</summary>
    public static IReadOnlyList<AirfieldFrequency> FrequenciesOf(string theatreId, int airfieldId)
    {
        lock (Radios)
        {
            if (!Radios.TryGetValue(theatreId, out var table))
            {
                try
                {
                    var path = DcsInstall.Find() is { } dcs ? Path.Combine(dcs, "Mods", "terrains", theatreId, "radio.lua") : null;
                    table = path is not null && File.Exists(path) ? AirfieldRadios.Parse(File.ReadAllText(path)) : null;
                }
                catch (IOException)
                {
                    table = null;
                }
                Radios[theatreId] = table;
            }
            return table is not null && table.TryGetValue(airfieldId, out var list) ? list : [];
        }
    }
}
