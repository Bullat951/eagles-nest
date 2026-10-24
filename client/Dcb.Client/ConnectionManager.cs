using System.Collections.Concurrent;
using System.Windows.Threading;
using Dcb.Client.Core;

namespace Dcb.Client;

/// <summary>Owns the one server connection: feeds pictures into the shared store on the UI
/// thread, and sends controller commands, matching each answer to its command.</summary>
public sealed class ConnectionManager(PictureStore store, Dispatcher dispatcher)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private CancellationTokenSource? _session;
    private DcbConnection? _connection;
    private long _nextCommandId;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<CommandResult>> _waiting = new();

    public bool IsConnected => _session is not null;
    public string Status { get; private set; } = "Not connected";
    public event Action? StatusChanged;

    public async Task ConnectAsync(string host, int port, string password)
    {
        Disconnect();
        var session = _session = new CancellationTokenSource();
        SetStatus($"Connecting to {host}:{port}…");
        try
        {
            await using var conn = await DcbConnection.ConnectAsync(host, port, password, session.Token);
            _connection = conn;
            SetStatus($"Connected to {host}:{port} as {conn.Coalition}");
            await foreach (var message in conn.ReadMessagesAsync(session.Token))
            {
                switch (message)
                {
                    case Picture picture:
                        await dispatcher.InvokeAsync(() =>
                        {
                            // Everything the UI thread does for one picture, panels reacting included.
                            using var perf = RenderStats.Measure("UI: picture");
                            store.Apply(conn.Coalition, picture, DateTime.UtcNow);
                        });
                        break;
                    case Theatre theatre:
                        await dispatcher.InvokeAsync(() => store.SetTheatre(theatre));
                        break;
                    case AirfieldsMessage airfields:
                        await dispatcher.InvokeAsync(() => store.SetAirfields(airfields.Airfields));
                        break;
                    case FootholdState foothold:
                        await dispatcher.InvokeAsync(() => store.SetFoothold(foothold));
                        break;
                    case WeatherMessage weather:
                        await dispatcher.InvokeAsync(() => store.SetWeather(weather));
                        break;
                    case LsoMessage lso:
                        await dispatcher.InvokeAsync(() =>
                        {
                            using var perf = RenderStats.Measure("UI: lso frame");
                            store.ApplyLso(lso);
                        });
                        break;
                    case RwrMessage rwr:
                        await dispatcher.InvokeAsync(() => store.ApplyRwr(rwr));
                        break;
                    case NctrMessage nctr:
                        await dispatcher.InvokeAsync(() => store.ApplyNctr(nctr));
                        break;
                    case IdsMessage ids:
                        await dispatcher.InvokeAsync(() => store.ApplyIds(ids));
                        break;
                    case ThreatsMessage threats:
                        await dispatcher.InvokeAsync(() =>
                        {
                            AppState.Board.ToLatLon ??= (x, z) => AppState.Store.Theatre?.ToLatLon(x, z);
                            AppState.Board.ApplyShared(threats.Threats);
                        });
                        break;
                    case MarksMessage marks:
                        await dispatcher.InvokeAsync(() => AppState.Board.ApplyMarks(marks.Marks, DateTime.UtcNow));
                        break;
                    case CommandResult result:
                        if (_waiting.TryRemove(result.Id, out var waiter)) waiter.TrySetResult(result);
                        break;
                    case TerrainMessage terrain:
                        var grid = await Task.Run(terrain.Decode, session.Token);  // ~1 MB of base64: off the UI thread
                        await dispatcher.InvokeAsync(() => store.SetTerrain(grid));
                        break;
                }
            }
            SetStatus("Server closed the connection");
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            SetStatus("Disconnected");
        }
        catch (AuthenticationException ex)
        {
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            SetStatus($"Connection failed: {ex.Message}");
        }
        finally
        {
            if (_session == session)
            {
                _session = null;
                _connection = null;
            }
            FailWaiting("connection closed");
            session.Dispose();
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Sends a command (e.g. "message_unit" with its fields) and waits for the
    /// server's answer; a failed result when not connected or no answer comes in time.</summary>
    public async Task<CommandResult> SendCommandAsync(string name, IReadOnlyDictionary<string, object> fields)
    {
        if (_connection is not { } conn) return new CommandResult(0, false, "not connected");
        var id = Interlocked.Increment(ref _nextCommandId);
        var message = new Dictionary<string, object> { ["type"] = "command", ["id"] = id, ["name"] = name };
        foreach (var (k, v) in fields) message[k] = v;
        var waiter = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = waiter;
        try
        {
            await conn.SendAsync(message);
        }
        catch (Exception ex)
        {
            _waiting.TryRemove(id, out _);
            return new CommandResult(id, false, "not sent: " + ex.Message);
        }
        var done = await Task.WhenAny(waiter.Task, Task.Delay(CommandTimeout));
        if (done == waiter.Task) return waiter.Task.Result;
        _waiting.TryRemove(id, out _);
        return new CommandResult(id, false, "no answer from the server");
    }

    private void FailWaiting(string reason)
    {
        foreach (var id in _waiting.Keys)
            if (_waiting.TryRemove(id, out var w)) w.TrySetResult(new CommandResult(id, false, reason));
    }

    public void Disconnect()
    {
        _session?.Cancel();
        _session = null;
        _connection = null;
        store.Clear();
        AppState.Board.ClearMarks();  // the next connection resends them
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }
}
