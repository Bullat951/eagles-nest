using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dcb.Client.Core;

// DCB as a listen-only SRS client (docs/PLAN.md, phase 6, B): the SRS 2.4 sync protocol's
// messages, the sender part of its voice packets, and what DCB keeps from them. Names and
// numbers follow the SRS server, which reads JSON case-sensitively.

/// <summary>SRS sync message types (numbers on the wire).</summary>
public static class SrsMsg
{
    public const int Update = 0, Ping = 1, Sync = 2, RadioUpdate = 3, ServerSettings = 4, ClientDisconnect = 5,
        VersionMismatch = 6, EamPassword = 7, EamDisconnect = 8;
}

/// <summary>SRS radio modulations.</summary>
public static class SrsModulation
{
    public const int Am = 0, Fm = 1, Intercom = 2, Disabled = 3, HaveQuick = 4, Satcom = 5, Mids = 6, Sincgars = 7;

    public static string Name(int m) => m switch
    {
        Am => "AM", Fm => "FM", Intercom => "INTERCOM", HaveQuick => "HQ", Satcom => "SATCOM", Mids => "MIDS",
        Sincgars => "SINCGARS", _ => "—",
    };
}

public sealed class SrsMessage
{
    [JsonPropertyName("Client")] public SrsClient? Client { get; set; }
    [JsonPropertyName("MsgType")] public int MsgType { get; set; }
    [JsonPropertyName("Clients")] public List<SrsClient>? Clients { get; set; }
    [JsonPropertyName("ServerSettings")] public Dictionary<string, string>? ServerSettings { get; set; }
    [JsonPropertyName("ExternalAWACSModePassword")] public string? ExternalAwacsModePassword { get; set; }
    [JsonPropertyName("Version")] public string? Version { get; set; }
}

public sealed class SrsClient
{
    [JsonPropertyName("ClientGuid")] public string ClientGuid { get; set; } = "";
    [JsonPropertyName("Name")] public string? Name { get; set; }
    [JsonPropertyName("Coalition")] public int Coalition { get; set; }
    [JsonPropertyName("AllowRecord")] public bool AllowRecord { get; set; }
    [JsonPropertyName("Seat")] public int Seat { get; set; }
    [JsonPropertyName("RadioInfo")] public SrsRadioInfo? RadioInfo { get; set; }
    [JsonPropertyName("LatLngPosition")] public SrsPosition? LatLngPosition { get; set; }
}

public sealed class SrsPosition
{
    [JsonPropertyName("lat")] public double Lat { get; set; }
    [JsonPropertyName("lng")] public double Lng { get; set; }
    [JsonPropertyName("alt")] public double Alt { get; set; }
}

public sealed class SrsRadioInfo
{
    public const int RadioCount = 11;  // 0 is the intercom

    [JsonPropertyName("radios")] public SrsRadio[] Radios { get; set; } = NewRadios();
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("unitId")] public uint UnitId { get; set; }
    // The server compares these on every update and fails on null: always sent, as SRS's defaults.
    [JsonPropertyName("iff")] public JsonObject Iff { get; set; } = [];
    [JsonPropertyName("ambient")] public JsonObject Ambient { get; set; } = new() { ["vol"] = 0.0, ["abType"] = "" };

    private static SrsRadio[] NewRadios() => Enumerable.Range(0, RadioCount).Select(_ => new SrsRadio()).ToArray();
}

public sealed class SrsRadio
{
    [JsonPropertyName("freq")] public double Freq { get; set; } = 1;  // Hz
    [JsonPropertyName("modulation")] public int Modulation { get; set; } = SrsModulation.Disabled;
    [JsonPropertyName("secFreq")] public double SecFreq { get; set; } = 1;  // Hz, the guard receiver
    [JsonPropertyName("enc")] public bool Enc { get; set; }
    [JsonPropertyName("encKey")] public byte EncKey { get; set; }
    [JsonPropertyName("retransmit")] public bool Retransmit { get; set; }
    [JsonPropertyName("IntercomUnitId")] public uint IntercomUnitId { get; set; }
    [JsonPropertyName("Model")] public string Model { get; set; } = "";
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
}

/// <summary>A frequency and modulation as SRS matches them (within 500 Hz).</summary>
public readonly record struct SrsChannel(double Hz, int Modulation)
{
    public double Mhz => Hz / 1e6;
    public bool Matches(double hz, int modulation) => Math.Abs(Hz - hz) < 500 && Modulation == modulation;
    public override string ToString() => $"{Mhz:0.000} {SrsModulation.Name(Modulation)}";
}

/// <summary>Who sent a voice packet, read from its frequency and fixed segments (the Opus
/// audio is not touched).</summary>
public readonly record struct VoiceSender(string Guid, IReadOnlyList<SrsChannel> Channels, uint UnitId)
{
    private const int GuidLength = 22;
    private const int HeaderLength = 6;                        // packet, audio, frequency lengths
    private const int FrequencyLength = 10;                    // double Hz, modulation, encryption
    private const int FixedLength = 4 + 8 + 1 + 2 * GuidLength;  // unit id, packet id, hops, 2 GUIDs

    /// <summary>The sender of a voice packet, or null for pings and anything malformed.</summary>
    public static VoiceSender? Read(ReadOnlySpan<byte> p)
    {
        if (p.Length < HeaderLength + FixedLength) return null;
        int total = BitConverter.ToUInt16(p), audio = BitConverter.ToUInt16(p[2..]), freqs = BitConverter.ToUInt16(p[4..]);
        if (total != p.Length || audio == 0 || freqs == 0 || freqs % FrequencyLength != 0
            || HeaderLength + audio + freqs + FixedLength != p.Length) return null;
        var channels = new List<SrsChannel>(freqs / FrequencyLength);
        for (int o = HeaderLength + audio; o < HeaderLength + audio + freqs; o += FrequencyLength)
        {
            var hz = BitConverter.ToDouble(p[o..]);
            if (hz > 0) channels.Add(new SrsChannel(hz, p[o + 8]));
        }
        if (channels.Count == 0) return null;
        var unitId = BitConverter.ToUInt32(p[(HeaderLength + audio + freqs)..]);
        // The original transmitter's GUID; the last one is whoever relayed it (the same unless retransmitted).
        return new VoiceSender(Encoding.ASCII.GetString(p[^(2 * GuidLength)..^GuidLength]), channels, unitId);
    }

    /// <summary>A packet as SRS lays it out (for tests and the test talker).</summary>
    public static byte[] Write(string guid, IReadOnlyList<SrsChannel> channels, uint unitId, int audioBytes = 40)
    {
        var freqs = channels.Count * FrequencyLength;
        var p = new byte[HeaderLength + audioBytes + freqs + FixedLength];
        BitConverter.TryWriteBytes(p, (ushort)p.Length);
        BitConverter.TryWriteBytes(p.AsSpan(2), (ushort)audioBytes);
        BitConverter.TryWriteBytes(p.AsSpan(4), (ushort)freqs);
        var o = HeaderLength + audioBytes;
        foreach (var c in channels)
        {
            BitConverter.TryWriteBytes(p.AsSpan(o), c.Hz);
            p[o + 8] = (byte)c.Modulation;
            o += FrequencyLength;
        }
        BitConverter.TryWriteBytes(p.AsSpan(o), unitId);
        var g = Encoding.ASCII.GetBytes(guid);
        g.CopyTo(p, p.Length - 2 * GuidLength);
        g.CopyTo(p, p.Length - GuidLength);
        return p;
    }
}

/// <summary>One frequency in use by the own coalition: who has a radio on it.</summary>
public sealed record SrsChannelUse(SrsChannel Channel, IReadOnlyList<SrsClient> Clients, bool Watched);

/// <summary>The SRS clients DCB knows from the sync messages and who was last heard talking.
/// Keeps every coalition's clients as the server sends them, but answers only for
/// <see cref="OwnCoalition"/> (fog of war). Not thread-safe: use from one thread.</summary>
public sealed class SrsRoster
{
    public static readonly TimeSpan TalkHold = TimeSpan.FromMilliseconds(600);
    public const int MaxWatched = 2 * (SrsRadioInfo.RadioCount - 1);

    private readonly Dictionary<string, SrsClient> _clients = [];
    private readonly Dictionary<string, (DateTime At, SrsChannel Channel)> _talk = [];

    public string? OwnGuid { get; set; }
    public int OwnCoalition { get; set; }
    public IReadOnlyList<SrsChannel> Watched { get; private set; } = [];

    public void Apply(SrsMessage m)
    {
        switch (m.MsgType)
        {
            case SrsMsg.Sync when m.Clients is { } all:
                _clients.Clear();
                foreach (var c in all) if (c.ClientGuid.Length > 0) _clients[c.ClientGuid] = c;
                break;
            case SrsMsg.RadioUpdate when m.Client is { ClientGuid.Length: > 0 } c:
                _clients[c.ClientGuid] = c;
                break;
            case SrsMsg.Update when m.Client is { ClientGuid.Length: > 0 } c:
                // Metadata only: keep the radios we have.
                if (_clients.TryGetValue(c.ClientGuid, out var old)) c.RadioInfo ??= old.RadioInfo;
                _clients[c.ClientGuid] = c;
                break;
            case SrsMsg.ClientDisconnect when m.Client is { } c:
                _clients.Remove(c.ClientGuid);
                _talk.Remove(c.ClientGuid);
                break;
        }
    }

    public void Clear()
    {
        _clients.Clear();
        _talk.Clear();
        Watched = [];
    }

    /// <summary>The unit name DCB listeners give their radios: not people, never listed.</summary>
    public const string ListenerUnit = "DCB";

    /// <summary>The own coalition's clients, without DCB listeners (this one or others).</summary>
    public IEnumerable<SrsClient> Own =>
        _clients.Values.Where(c => OwnCoalition != 0 && c.Coalition == OwnCoalition && c.ClientGuid != OwnGuid
            && c.RadioInfo?.Unit != ListenerUnit);

    /// <summary>Notes a voice packet when it comes from an own-coalition client.</summary>
    public bool Heard(VoiceSender sender, DateTime now)
    {
        if (!_clients.TryGetValue(sender.Guid, out var c) || c.Coalition != OwnCoalition || c.ClientGuid == OwnGuid) return false;
        _talk[sender.Guid] = (now, sender.Channels[0]);
        return true;
    }

    /// <summary>The channel a client is talking on now, if any.</summary>
    public SrsChannel? TalkingOn(string guid, DateTime now) =>
        _talk.TryGetValue(guid, out var t) && now - t.At < TalkHold ? t.Channel : null;

    public bool AnyoneTalking(DateTime now) => _talk.Values.Any(t => now - t.At < TalkHold);

    /// <summary>The own client flying as this DCS player name, if any.</summary>
    public SrsClient? ByPlayer(string? player) =>
        player is null ? null : Own.FirstOrDefault(c => string.Equals(c.Name, player, StringComparison.OrdinalIgnoreCase));

    /// <summary>A client's tuned radios: (radio number 1–10, channel, guard), without intercom
    /// and switched-off radios.</summary>
    public static IEnumerable<(int Radio, SrsChannel Channel, bool Guard)> RadiosOf(SrsClient c)
    {
        var radios = c.RadioInfo?.Radios ?? [];
        for (int i = 1; i < radios.Length; i++)
        {
            var r = radios[i];
            if (r.Modulation is SrsModulation.Disabled or SrsModulation.Intercom || r.Freq <= 10000) continue;
            yield return (i, new SrsChannel(r.Freq, r.Modulation), false);
            if (r.SecFreq > 10000) yield return (i, new SrsChannel(r.SecFreq, r.Modulation), true);
        }
    }

    /// <summary>Own clients with a radio (guard included) on a frequency, any modulation: the
    /// people on one of the controller's channels.</summary>
    public IReadOnlyList<SrsClient> ClientsOn(double hz) =>
        Own.Where(c => RadiosOf(c).Any(r => Math.Abs(r.Channel.Hz - hz) < 500)).OrderBy(c => c.Name).ToList();

    /// <summary>Frequencies the own coalition uses, most listeners first, with who is on each.</summary>
    public IReadOnlyList<SrsChannelUse> Channels()
    {
        var groups = new List<(SrsChannel Channel, List<SrsClient> Clients)>();
        foreach (var c in Own)
            foreach (var (_, ch, _) in RadiosOf(c))
            {
                var g = groups.FindIndex(x => x.Channel.Matches(ch.Hz, ch.Modulation));
                if (g < 0) groups.Add((ch, [c]));
                else if (!groups[g].Clients.Contains(c)) groups[g].Clients.Add(c);
            }
        return groups
            .OrderByDescending(g => g.Clients.Count).ThenBy(g => g.Channel.Hz)
            .Select(g => new SrsChannelUse(g.Channel, g.Clients, Watched.Any(w => w.Matches(g.Channel.Hz, g.Channel.Modulation))))
            .ToList();
    }

    /// <summary>Chooses what DCB listens to: the most used own frequencies, up to
    /// <see cref="MaxWatched"/>. Returns the radios to send, or null when unchanged.</summary>
    public SrsRadioInfo? Replan()
    {
        var wanted = Channels().Take(MaxWatched).Select(u => u.Channel).ToList();
        if (wanted.Count == Watched.Count && wanted.All(w => Watched.Any(x => x.Matches(w.Hz, w.Modulation)))) return null;
        Watched = wanted;
        return RadiosFor(wanted);
    }

    /// <summary>Radios 1–10 on the first ten channels; further channels ride on their guard
    /// receivers (SRS checks only the frequency there, not the modulation).</summary>
    public static SrsRadioInfo RadiosFor(IReadOnlyList<SrsChannel> channels)
    {
        var info = new SrsRadioInfo { Unit = ListenerUnit, UnitId = 0 };
        for (int i = 0; i < channels.Count && i < MaxWatched; i++)
        {
            var r = info.Radios[1 + i % (SrsRadioInfo.RadioCount - 1)];
            if (i < SrsRadioInfo.RadioCount - 1)
            {
                r.Freq = channels[i].Hz;
                r.Modulation = channels[i].Modulation;
                r.Name = "dcb";
            }
            else r.SecFreq = channels[i].Hz;
        }
        return info;
    }
}
