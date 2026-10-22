using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Threading;
using Dcb.Client.Core;

namespace Dcb.Client.Srs;

/// <summary>Reads the controller's SRS app's radios from the state it sends to
/// 127.0.0.1:7082 every 200 ms while connected: the channels as they really are, the one
/// selected to transmit and the one transmitting now. <see cref="Changed"/> is raised on the UI
/// thread only when something shown changes; the state counts as gone 2 s after the last packet.</summary>
public sealed class SrsAppListener
{
    public static SrsAppListener Instance { get; } = new();

    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(2);
    private readonly Dispatcher _ui = System.Windows.Application.Current.Dispatcher;
    private readonly DispatcherTimer _stale = new() { Interval = TimeSpan.FromSeconds(1) };
    private UdpClient? _udp;
    private SrsAppState? _state;
    private DateTime _lastAt;
    private string _shown = "";

    /// <summary>The app's state, or null when it sends none (not running or not connected).</summary>
    public SrsAppState? State => _state is not null && DateTime.UtcNow - _lastAt < StaleAfter ? _state : null;

    /// <summary>Why there is no state, for the SRS tab.</summary>
    public string Status { get; private set; } = "Waiting for the SRS app's radios…";

    public event Action? Changed;

    private SrsAppListener()
    {
        _stale.Tick += (_, _) =>
        {
            if (_state is not null && State is null && _shown.Length > 0)
            {
                _shown = "";
                Changed?.Invoke();
            }
        };
    }

    public void Start()
    {
        // A snapshot run reads the port only when asked (DCB_SNAPSHOT_SRSAPP=1): it only listens,
        // but shares the packets with the controller's own client meanwhile.
        if (_udp is not null || (DebugSnapshot.Active && Environment.GetEnvironmentVariable("DCB_SNAPSHOT_SRSAPP") != "1")) return;
        try
        {
            var udp = new UdpClient();
            // Shared: a flight-panel tool may listen on the same port.
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, SrsAppState.Port));
            _udp = udp;
            _stale.Start();
            _ = Task.Run(ReceiveLoop);
        }
        catch (SocketException ex)
        {
            Status = $"Cannot read the SRS app's radios (port {SrsAppState.Port}: {ex.Message}).";
        }
    }

    private async Task ReceiveLoop()
    {
        while (_udp is { } udp)
        {
            UdpReceiveResult packet;
            try { packet = await udp.ReceiveAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            if (SrsAppState.Parse(Encoding.UTF8.GetString(packet.Buffer)) is not { } state) continue;
            _ = _ui.BeginInvoke(() => Apply(state));
        }
    }

    private void Apply(SrsAppState state)
    {
        _state = state;
        _lastAt = DateTime.UtcNow;
        Status = "Channels read live from the SRS app.";
        var shown = string.Join(";", state.Radios.Select(r => $"{r.Hz}/{r.Modulation}/{r.Name}")) + $"|{state.Selected}|{state.SendingOn}|{string.Join(",", state.Tuned)}";
        if (shown == _shown) return;
        _shown = shown;
        Changed?.Invoke();
    }
}
