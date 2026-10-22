using System.Windows.Threading;
using Dcb.Client.Core;

namespace Dcb.Client.Srs;

/// <summary>DCB's own listen-only SRS connection (docs/PLAN.md, phase 6 B): the own coalition's
/// SRS users and their frequencies, and who is talking. Runs while DCB is connected to a server,
/// on that server's address at <see cref="SrsSettings.ListenPort"/> with the EAM password given
/// in the connect dialog; reconnects every 10 s. Lives on the UI thread.</summary>
public sealed class SrsListen
{
    public static SrsListen Instance { get; } = new();

    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);
    private readonly Dispatcher _ui = System.Windows.Application.Current.Dispatcher;
    private readonly DispatcherTimer _replan = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _talkTick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private SrsListener? _listener;
    private CancellationTokenSource? _run;
    private bool _dirty, _wasTalking;
    private SrsSettings Settings => AppState.Settings.Srs;

    public SrsRoster Roster { get; } = new();
    public string Status { get; private set; } = "Not listening.";
    /// <summary>What happened to the SRS app when DCB connected.</summary>
    public string AppStatus { get; private set; } = "";

    /// <summary>The EAM password for this session when it is not remembered.</summary>
    public string? SessionPassword { get; set; }
    private string? Password => Settings.EamPassword ?? SessionPassword;
    public bool Connected => _listener is not null;
    /// <summary>The coalition of the EAM password (1 red, 2 blue), 0 when not connected.</summary>
    public int EamCoalition => _listener?.Coalition ?? 0;

    /// <summary>Clients or their radios changed.</summary>
    public event Action? Changed;
    /// <summary>Someone started or stopped talking (raised at most every 100 ms).</summary>
    public event Action? TalkChanged;

    private SrsListen()
    {
        _replan.Tick += (_, _) => Replan();
        _talkTick.Tick += (_, _) => TickTalk();
        _replan.Start();
        _talkTick.Start();
        AppState.Store.Changed += CheckCoalition;
        AppState.Connection.StatusChanged += () => _ui.BeginInvoke(Follow);
    }

    /// <summary>Hooks the connection: listening starts and stops with DCB's.</summary>
    public void Start() => Follow();

    /// <summary>Port or password changed: connect again with them.</summary>
    public void Reconnect()
    {
        Disconnect();
        Follow();
    }

    private void Follow()
    {
        var want = AppState.Connection.IsConnected && Password is { Length: > 0 };
        if (want && _run is null)
        {
            if (Settings.StartClient && !DebugSnapshot.Active) AppStatus = SrsApp.EnsureRunning();
            Connect();
        }
        else if (!want && _run is not null)
        {
            Disconnect();
        }
        else if (!AppState.Connection.IsConnected) Status = "Not listening: connect to a DCB server.";
        else if (Password is not { Length: > 0 }) Status = "Not listening: no SRS (EAM) password in the connect dialog.";
        Changed?.Invoke();
    }

    private void Connect()
    {
        _run = new CancellationTokenSource();
        _ = RunAsync(_run.Token);
    }

    public void Disconnect()
    {
        _run?.Cancel();
        _run = null;
        if (_listener is { } l) _ = l.DisposeAsync().AsTask();
        _listener = null;
        Roster.Clear();
        Status = "Not listening.";
        Changed?.Invoke();
        TalkChanged?.Invoke();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var host = AppState.Settings.Host;  // the SRS server runs beside the DCB server
            var port = Settings.ListenPort;
            Status = $"Connecting to {host}:{port}…";
            Changed?.Invoke();
            SrsListener? listener = null;
            try
            {
                listener = await SrsListener.ConnectAsync(host, port, Password ?? "", Settings.ListenName, ct);
                if (ct.IsCancellationRequested) { await listener.DisposeAsync(); return; }
                _listener = listener;
                Roster.OwnGuid = listener.Guid;
                CheckCoalition();
                listener.MessageReceived += m => _ui.BeginInvoke(() => OnMessage(listener, m));
                listener.VoiceReceived += v => _ui.BeginInvoke(() => OnVoice(listener, v));
                Status = $"Listening on {host}:{port} as {Side(listener.Coalition)}.";
                Changed?.Invoke();
                await listener.RunAsync();
            }
            catch (AuthenticationException ex)
            {
                // A wrong password does not get better by retrying (until the next connect).
                Status = ex.Message;
                Changed?.Invoke();
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Status = $"SRS: {ex.Message} Retrying in {RetryAfter.TotalSeconds:0} s.";
            }
            if (listener is not null)
            {
                await listener.DisposeAsync();
                if (_listener == listener) _listener = null;
                Roster.Clear();
                TalkChanged?.Invoke();
            }
            if (ct.IsCancellationRequested) return;
            if (!Status.StartsWith("SRS:")) Status = $"SRS server connection lost. Retrying in {RetryAfter.TotalSeconds:0} s.";
            Changed?.Invoke();
            try { await Task.Delay(RetryAfter, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private void OnMessage(SrsListener from, SrsMessage m)
    {
        if (from != _listener) return;
        Roster.Apply(m);
        if (m.MsgType is SrsMsg.Sync or SrsMsg.RadioUpdate or SrsMsg.Update or SrsMsg.ClientDisconnect)
        {
            _dirty = true;
            Changed?.Invoke();
        }
    }

    private void OnVoice(SrsListener from, VoiceSender v)
    {
        if (from != _listener) return;
        if (Roster.Heard(v, DateTime.UtcNow) && !_wasTalking) TickTalk();
    }

    private void TickTalk()
    {
        var talking = Roster.AnyoneTalking(DateTime.UtcNow);
        if (talking || _wasTalking) TalkChanged?.Invoke();
        _wasTalking = talking;
    }

    /// <summary>Retunes DCB's receivers to the own coalition's most used frequencies, at most once a second.</summary>
    private void Replan()
    {
        if (!_dirty || _listener is not { } l) return;
        _dirty = false;
        if (Roster.Replan() is { } radios) _ = SendRadios(l, radios);
    }

    private async Task SendRadios(SrsListener l, SrsRadioInfo radios)
    {
        try { await l.SetRadiosAsync(radios); }
        catch (Exception) { /* the run loop sees the broken connection */ }
    }

    /// <summary>Shows only the side DCB itself is logged in as: an EAM password of the other
    /// side shows nothing (fog of war).</summary>
    private void CheckCoalition()
    {
        if (_listener is not { } l) return;
        var dcb = AppState.Store.Coalition switch { "red" => 1, "blue" => 2, _ => 0 };
        var own = dcb == 0 || dcb == l.Coalition ? l.Coalition : 0;
        if (own == Roster.OwnCoalition) return;
        Roster.OwnCoalition = own;
        if (own == 0) Status = $"The EAM password is {Side(l.Coalition)}'s but DCB is logged in as {Side(dcb)}: nothing shown.";
        _dirty = true;
        Changed?.Invoke();
    }

    private static string Side(int c) => c switch { 1 => "red", 2 => "blue", _ => "spectator" };

    /// <summary>The own SRS user flying this track, if any (matched by DCS player name).</summary>
    public SrsClient? ClientFor(Unit u) => u.Detected ? null : Roster.ByPlayer(u.Player);

    /// <summary>The channel this track's player is talking on now, if any.</summary>
    public SrsChannel? TalkingOn(Unit u) =>
        ClientFor(u) is { } c ? Roster.TalkingOn(c.ClientGuid, DateTime.UtcNow) : null;
}
