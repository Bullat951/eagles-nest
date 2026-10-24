using System.Text.Json;
using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class RwrTests
{
    // A line as DCB_rwr (collector.lua) writes it.
    private const string Line = """
        {"type":"rwr","version":1,"coalition":"blue","time":812.0,"receivers":[
        {"id":1001,"emitters":[{"key":3,"label":"50","cat":"airplane","bearing":81.4,"err":10,"mode":"search","level":0.42},
        {"key":7,"label":"6","cat":"ground","bearing":12.0,"err":10,"mode":"launch","level":0.80}]},
        {"id":2002,"emitters":[{"key":3,"label":"50","cat":"airplane","bearing":95.0,"err":5,"mode":"track","level":0.10}]}]}
        """;

    [Fact]
    public void Deserializes_the_collector_line()
    {
        var m = JsonSerializer.Deserialize<RwrMessage>(Line)!;
        Assert.Equal("blue", m.Coalition);
        Assert.Equal(2, m.Receivers.Count);
        var e = m.Receivers[0].Emitters[1];
        Assert.Equal(7, e.Key);
        Assert.Equal("6", e.Label);
        Assert.Equal("ground", e.Category);
        Assert.Equal("launch", e.Mode);
        Assert.Equal(0.8, e.Level, 3);
    }

    [Fact]
    public void Store_answers_per_receiver()
    {
        var store = new PictureStore();
        var raised = 0;
        store.RwrChanged += () => raised++;
        store.ApplyRwr(JsonSerializer.Deserialize<RwrMessage>(Line)!);
        Assert.Equal(1, raised);
        Assert.Equal(2, store.RwrFor(1001).Count);
        Assert.Equal("track", Assert.Single(store.RwrFor(2002)).Mode);
        Assert.Empty(store.RwrFor(9999));
    }
}
