using System.Text.Json;
using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class SrsListenTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private const string Pilot = "AAAAAAAAAAAAAAAAAAAAAA", Wingman = "BBBBBBBBBBBBBBBBBBBBBB", Enemy = "CCCCCCCCCCCCCCCCCCCCCC", Me = "DDDDDDDDDDDDDDDDDDDDDD";

    private static SrsClient Client(string guid, string name, int side, params (double Mhz, int Mod, double GuardMhz)[] radios)
    {
        var info = new SrsRadioInfo();
        for (int i = 0; i < radios.Length; i++)
        {
            info.Radios[i + 1].Freq = radios[i].Mhz * 1e6;
            info.Radios[i + 1].Modulation = radios[i].Mod;
            if (radios[i].GuardMhz > 0) info.Radios[i + 1].SecFreq = radios[i].GuardMhz * 1e6;
        }
        return new SrsClient { ClientGuid = guid, Name = name, Coalition = side, RadioInfo = info };
    }

    private static SrsRoster Roster()
    {
        var r = new SrsRoster { OwnGuid = Me, OwnCoalition = 2 };
        r.Apply(new SrsMessage
        {
            MsgType = SrsMsg.Sync,
            Clients =
            [
                Client(Pilot, "Viper 1-1", 2, (251, SrsModulation.Am, 243), (124.5, SrsModulation.Am, 0)),
                Client(Wingman, "Viper 1-2", 2, (251, SrsModulation.Am, 0)),
                Client(Enemy, "Flanker", 1, (251, SrsModulation.Am, 0)),
                Client(Me, "DCB (listen)", 2),
                Other_listener(),
            ],
        });
        return r;
    }

    private static SrsClient Other_listener()
    {
        var c = Client("EEEEEEEEEEEEEEEEEEEEEE", "DCB (listen)", 2, (251, SrsModulation.Am, 0));
        c.RadioInfo!.Unit = SrsRoster.ListenerUnit;
        return c;
    }

    [Fact]
    public void Srs_app_state_packet_is_read()
    {
        // Trimmed from a packet the SRS 2.4.1 app sent to 7082 in External AWACS Mode (2026-10-22).
        const string json = """
            {"RadioInfo":{"name":"Bullat95","ptt":false,"radios":[
            {"freq":100,"modulation":2,"name":"SATCOM","secFreq":0},
            {"freq":253000000,"modulation":0,"name":"UHF Guard","secFreq":243000000},
            {"freq":30000000,"modulation":1,"name":"VHF FM","secFreq":1}],
            "selected":1,"unit":"EAM"},
            "RadioSendingState":{"IsSending":true,"SendingOn":1,"IsEncrypted":0},
            "ClientCountConnected":3,"TunedClients":[0,2,1]}
            """;
        var s = SrsAppState.Parse(json)!;
        Assert.Equal(253.0, s.Radio(1)!.Value.Mhz, 6);
        Assert.Equal(243e6, s.Radio(1)!.Value.GuardHz);
        Assert.Equal(("VHF FM", SrsModulation.Fm), (s.Radio(2)!.Value.Name, s.Radio(2)!.Value.Modulation));
        Assert.Equal((1, 1, 2, "Bullat95"), (s.Selected, s.SendingOn!.Value, s.TunedOn(1), s.Name));
        Assert.Null(s.Radio(7));
        Assert.Null(SrsAppState.Parse("{\"something\":1}"));
        Assert.Null(SrsAppState.Parse("not json"));
    }

    [Fact]
    public void People_on_a_frequency_are_own_users_only()
    {
        var r = Roster();
        Assert.Equal(["Viper 1-1", "Viper 1-2"], r.ClientsOn(251e6).Select(c => c.Name));  // no enemy, no DCB listeners
        Assert.Equal(["Viper 1-1"], r.ClientsOn(243e6).Select(c => c.Name));              // guard receiver counts
        Assert.Empty(r.ClientsOn(305e6));
    }

    [Fact]
    public void Voice_packet_sender_round_trips()
    {
        var bytes = VoiceSender.Write(Pilot, [new SrsChannel(251e6, SrsModulation.Am)], 16777472);
        var s = VoiceSender.Read(bytes)!.Value;
        Assert.Equal(Pilot, s.Guid);
        Assert.Equal(16777472u, s.UnitId);
        Assert.True(s.Channels[0].Matches(251e6 + 100, SrsModulation.Am));
    }

    [Fact]
    public void Malformed_or_ping_packets_are_ignored()
    {
        var bytes = VoiceSender.Write(Pilot, [new SrsChannel(251e6, SrsModulation.Am)], 1);
        Assert.Null(VoiceSender.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Null(VoiceSender.Read(System.Text.Encoding.ASCII.GetBytes(Pilot)));
        Assert.Null(VoiceSender.Read(VoiceSender.Write(Pilot, [new SrsChannel(0, SrsModulation.Am)], 1)));
    }

    [Fact]
    public void Only_own_coalition_is_listed_and_heard()
    {
        var r = Roster();
        var uses = r.Channels();
        Assert.Equal(251e6, uses[0].Channel.Hz);
        Assert.Equal(["Viper 1-1", "Viper 1-2"], uses[0].Clients.Select(c => c.Name));
        Assert.Contains(uses, u => u.Channel.Hz == 243e6);  // guard receivers count too
        Assert.DoesNotContain(uses.SelectMany(u => u.Clients), c => c.ClientGuid is Enemy or Me || c.RadioInfo?.Unit == SrsRoster.ListenerUnit);
        Assert.Equal(2, uses[0].Clients.Count);  // the other DCB listener on 251 is not a user

        var ch = new SrsChannel(251e6, SrsModulation.Am);
        Assert.False(r.Heard(new VoiceSender(Enemy, [ch], 0), T0));
        Assert.True(r.Heard(new VoiceSender(Pilot, [ch], 0), T0));
        Assert.Equal(ch, r.TalkingOn(Pilot, T0.AddMilliseconds(300)));
        Assert.Null(r.TalkingOn(Pilot, T0.AddSeconds(1)));
        Assert.Equal(Pilot, r.ByPlayer("viper 1-1")!.ClientGuid);
    }

    [Fact]
    public void Updates_keep_radios_and_disconnects_drop_clients()
    {
        var r = Roster();
        r.Apply(new SrsMessage { MsgType = SrsMsg.Update, Client = new SrsClient { ClientGuid = Wingman, Name = "Viper 1-2", Coalition = 2 } });
        Assert.Contains(r.Channels()[0].Clients, c => c.ClientGuid == Wingman);
        r.Apply(new SrsMessage { MsgType = SrsMsg.ClientDisconnect, Client = new SrsClient { ClientGuid = Wingman } });
        Assert.DoesNotContain(r.Own, c => c.ClientGuid == Wingman);
    }

    [Fact]
    public void Watch_plan_uses_radios_then_guard_receivers()
    {
        var channels = Enumerable.Range(0, 14).Select(i => new SrsChannel((230 + i) * 1e6, i % 2)).ToList();
        var info = SrsRoster.RadiosFor(channels);
        Assert.Equal(SrsModulation.Disabled, info.Radios[0].Modulation);  // intercom stays off
        Assert.Equal(230e6, info.Radios[1].Freq);
        Assert.Equal(239e6, info.Radios[10].Freq);
        Assert.Equal(240e6, info.Radios[1].SecFreq);
        Assert.Equal(243e6, info.Radios[4].SecFreq);
        Assert.Equal(1, info.Radios[5].SecFreq);

        var r = Roster();
        Assert.NotNull(r.Replan());
        Assert.Null(r.Replan());  // unchanged
        Assert.Equal(3, r.Watched.Count);
    }

    [Fact]
    public void Messages_use_the_names_the_server_reads()
    {
        var json = JsonSerializer.Serialize(new SrsMessage { MsgType = SrsMsg.RadioUpdate, Client = Client(Me, "DCB", 2, (251, 0, 0)), Version = SrsListener.ProtocolVersion });
        foreach (var name in new[] { "\"MsgType\":3", "\"ClientGuid\"", "\"RadioInfo\"", "\"radios\"", "\"freq\":251000000", "\"modulation\":0", "\"secFreq\"", "\"iff\":{}", "\"ambient\"", "\"Version\":\"2.4.1.0\"" })
            Assert.Contains(name, json);
        Assert.Equal(22, SrsListener.NewGuid().Length);
    }
}
