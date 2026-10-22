using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dcb.Client.Core;

/// <summary>DCB's own connection to an SRS server as a listen-only External AWACS Mode client:
/// TCP sync (everyone's radios) and UDP voice packets (who talks, never the audio). It sends no
/// audio. Events come on background threads.</summary>
public sealed class SrsListener : IAsyncDisposable
{
    public const int DefaultPort = 5002;
    /// <summary>The SRS version DCB speaks (the server accepts 1.9 and later).</summary>
    public const string ProtocolVersion = "2.4.1.0";
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly UdpClient _udp;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<SrsMessage> _early;
    private SrsRadioInfo _radios = new() { Unit = SrsRoster.ListenerUnit };

    public string Guid { get; }
    public string Name { get; }
    /// <summary>1 red, 2 blue: the side the EAM password belongs to.</summary>
    public int Coalition { get; }

    public event Action<SrsMessage>? MessageReceived;
    public event Action<VoiceSender>? VoiceReceived;
    /// <summary>The connection ended (null: closed by us).</summary>
    public event Action<string?>? Closed;

    private SrsListener(TcpClient tcp, StreamReader reader, UdpClient udp, string guid, string name, int coalition, List<SrsMessage> early)
    {
        (_tcp, _reader, _udp, Guid, Name, Coalition, _early) = (tcp, reader, udp, guid, name, coalition, early);
    }

    /// <summary>A 22-character SRS client id (URL-safe base64 of a new GUID).</summary>
    public static string NewGuid() =>
        Convert.ToBase64String(System.Guid.NewGuid().ToByteArray()).Replace('/', '_').Replace('+', '-')[..22];

    /// <summary>Connects, asks for the client list and logs in to External AWACS Mode. Throws
    /// <see cref="AuthenticationException"/> when the server rejects the password.</summary>
    public static async Task<SrsListener> ConnectAsync(string host, int port, string eamPassword, string name, CancellationToken ct = default)
    {
        var guid = NewGuid();
        var tcp = new TcpClient { NoDelay = true };
        UdpClient? udp = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(host, port, timeout.Token);
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            var stream = tcp.GetStream();
            var me = new SrsClient { ClientGuid = guid, Name = name, RadioInfo = new SrsRadioInfo { Unit = SrsRoster.ListenerUnit }, LatLngPosition = new SrsPosition() };
            await stream.WriteAsync(Line(new SrsMessage { MsgType = SrsMsg.Sync, Client = me, Version = ProtocolVersion }), timeout.Token);
            await stream.WriteAsync(Line(new SrsMessage { MsgType = SrsMsg.EamPassword, Client = me, ExternalAwacsModePassword = eamPassword, Version = ProtocolVersion }), timeout.Token);

            // The client list and others' updates may come before the password answer: keep them.
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            var early = new List<SrsMessage>();
            while (true)
            {
                var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("SRS server closed the connection.");
                if (Parse(line) is not { } m) continue;
                if (m.MsgType == SrsMsg.VersionMismatch) throw new IOException("SRS server refused DCB's protocol version.");
                if (m.MsgType != SrsMsg.EamPassword) { early.Add(m); continue; }
                var side = m.Client?.Coalition ?? 0;
                if (side == 0) throw new AuthenticationException("Wrong External AWACS Mode password (or EAM is off on that server).");

                var address = (await Dns.GetHostAddressesAsync(host, timeout.Token)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
                udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Connect(new IPEndPoint(address, port));
                return new SrsListener(tcp, reader, udp, guid, name, side, early);
            }
        }
        catch
        {
            udp?.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Runs until the connection ends: reads sync messages and voice packets, pings both.</summary>
    public async Task RunAsync()
    {
        var token = _stop.Token;
        foreach (var m in _early) MessageReceived?.Invoke(m);
        _early.Clear();
        string? reason = null;
        var tasks = new[] { ReadSyncAsync(token), ReadVoiceAsync(token), PingAsync(token) };
        try
        {
            await await Task.WhenAny(tasks);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            reason = ex.Message;
        }
        if (!token.IsCancellationRequested) reason ??= "SRS server closed the connection.";
        _stop.Cancel();
        try { await Task.WhenAll(tasks); } catch { }
        Closed?.Invoke(reason);
    }

    /// <summary>Tunes DCB's receivers (sent as a radio update; others see it in their client lists).</summary>
    public Task SetRadiosAsync(SrsRadioInfo radios)
    {
        _radios = radios;
        return SendAsync(SrsMsg.RadioUpdate);
    }

    private Task SendAsync(int type) => SendAsync(new SrsMessage
    {
        MsgType = type,
        // Every message names us; the coalition is sent each time or the server resets it.
        Client = new SrsClient { ClientGuid = Guid, Name = Name, Coalition = Coalition, RadioInfo = _radios, LatLngPosition = new SrsPosition() },
        Version = ProtocolVersion,
    });

    private async Task SendAsync(SrsMessage message)
    {
        var bytes = Line(message);
        await _sendLock.WaitAsync(_stop.Token);
        try { await _tcp.GetStream().WriteAsync(bytes, _stop.Token); }
        finally { _sendLock.Release(); }
    }

    private async Task ReadSyncAsync(CancellationToken token)
    {
        while (await _reader.ReadLineAsync(token) is { } line)
            if (Parse(line) is { } m) MessageReceived?.Invoke(m);
    }

    private async Task ReadVoiceAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var r = await _udp.ReceiveAsync(token);
            if (r.Buffer.Length > 22 && VoiceSender.Read(r.Buffer) is { } sender) VoiceReceived?.Invoke(sender);
        }
    }

    private async Task PingAsync(CancellationToken token)
    {
        var guid = Encoding.ASCII.GetBytes(Guid);
        while (!token.IsCancellationRequested)
        {
            // The UDP ping tells the server where to send voice; the TCP one keeps the session alive.
            await _udp.SendAsync(guid, token);
            await SendAsync(SrsMsg.Ping);
            await Task.Delay(PingInterval, token);
        }
    }

    private static byte[] Line(SrsMessage m) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, Json) + "\n");

    public static SrsMessage? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<SrsMessage>(line); }
        catch (JsonException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            try
            {
                // Leave EAM politely so others' lists drop us at once.
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _tcp.GetStream().WriteAsync(Line(new SrsMessage
                {
                    MsgType = SrsMsg.EamDisconnect,
                    Client = new SrsClient { ClientGuid = Guid, Name = Name, Coalition = Coalition },
                    Version = ProtocolVersion,
                }), quick.Token);
            }
            catch { }
            _stop.Cancel();
        }
        _udp.Dispose();
        _tcp.Dispose();
    }
}
