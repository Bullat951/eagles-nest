using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Dcb.Client.Core;

public sealed class AuthenticationException(string message) : Exception(message);

/// <summary>A controller's connection to a DCB server.</summary>
public sealed class DcbConnection : IAsyncDisposable
{
    public const int DefaultPort = 10309;
    private const string ClientName = "DCB Client 0.1";

    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string Coalition { get; }

    private DcbConnection(TcpClient tcp, StreamReader reader, string coalition)
    {
        _tcp = tcp;
        _reader = reader;
        Coalition = coalition;
    }

    /// <summary>Sends one JSON object as a line (commands). Safe from several callers at once.</summary>
    public async Task SendAsync(object message, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
        await _sendLock.WaitAsync(ct);
        try
        {
            await _tcp.GetStream().WriteAsync(bytes, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Connects and logs in. The server decides the coalition from the password.</summary>
    public static async Task<DcbConnection> ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await tcp.ConnectAsync(host, port, timeout.Token);
            }
            var stream = tcp.GetStream();
            var auth = JsonSerializer.Serialize(new AuthRequest(password, ClientName)) + "\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(auth), ct);

            var reader = new StreamReader(stream, new UTF8Encoding(false));
            var line = await reader.ReadLineAsync(ct) ?? throw new AuthenticationException("Server closed the connection.");
            var reply = JsonSerializer.Deserialize<Message>(line);
            if (reply?.Type != "auth_ok" || reply.Coalition is null)
                throw new AuthenticationException("Wrong password.");
            return new DcbConnection(tcp, reader, reply.Coalition);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Yields each picture the server sends until the connection closes.</summary>
    public async IAsyncEnumerable<Picture> ReadPicturesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var m in ReadMessagesAsync(ct))
            if (m is Picture p) yield return p;
    }

    /// <summary>Yields every known server message (<see cref="Picture"/>, <see cref="Theatre"/>,
    /// <see cref="TerrainMessage"/>, <see cref="FootholdState"/>) until the connection closes;
    /// unknown types are skipped.</summary>
    public async IAsyncEnumerable<object> ReadMessagesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await _reader.ReadLineAsync(ct) is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("type", out var type)) continue;
            object? message = type.GetString() switch
            {
                "picture" => doc.RootElement.Deserialize<Picture>(),
                "theatre" => doc.RootElement.Deserialize<Theatre>(),
                "terrain" => doc.RootElement.Deserialize<TerrainMessage>(),
                "foothold" => doc.RootElement.Deserialize<FootholdState>(),
                "airfields" => doc.RootElement.Deserialize<AirfieldsMessage>(),
                "weather" => doc.RootElement.Deserialize<WeatherMessage>(),
                "command_result" => doc.RootElement.Deserialize<CommandResult>(),
                "lso" => doc.RootElement.Deserialize<LsoMessage>(),
                "marks" => doc.RootElement.Deserialize<MarksMessage>(),
                _ => null,
            };
            if (message is not null) yield return message;
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
