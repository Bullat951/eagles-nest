using System.Text.Json;
using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class IdentificationTests
{
    private static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static Unit Own(long id, double lat, double lon) => SymbologyTests.Make(id: id) with { Lat = lat, Lon = lon };
    private static Unit Contact(long id, double lat, double lon, string category = "airplane") =>
        SymbologyTests.Make(id: id, coalition: "red", detected: true) with { Lat = lat, Lon = lon, Category = category, Type = null };

    [Theory]
    [InlineData("6", "ground", "SA-6")]
    [InlineData("15", "ground", "SA-15")]
    [InlineData("SA-11", "ground", "SA-11")]
    [InlineData("P", "ship", "Patriot")]
    [InlineData("15", "airplane", null)]   // F-15, not SA-15
    [InlineData("29", "ground", null)]
    [InlineData("", "ground", null)]
    public void Labels_give_ground_threats_only(string label, string category, string? threat) =>
        Assert.Equal(threat, RwrLabels.ThreatFor(label, category)?.Name);

    [Fact]
    public void Candidates_are_emitters_whose_bearings_pass_through_the_contact()
    {
        // Two own aircraft; the contact is north-east of the first and north-west of the second.
        var a = Own(1, 42.0, 41.0);
        var b = Own(2, 42.0, 42.0);
        var c = Contact(9, 42.5, 41.5);
        var toC1 = Nav.BearingRange(42.0, 41.0, 42.5, 41.5).TrueDeg;
        var toC2 = Nav.BearingRange(42.0, 42.0, 42.5, 41.5).TrueDeg;
        var rwr = new RwrMessage("blue", 1, [
            new RwrReceiver(1, [new RwrEmitter(1, "34", "airplane", toC1 + 4, 10, "search", 0.5),
                                new RwrEmitter(2, "6", "ground", toC1, 10, "search", 0.5),        // right bearing, wrong kind
                                new RwrEmitter(3, "50", "airplane", toC1 + 60, 10, "search", 0.1)]), // elsewhere
            new RwrReceiver(2, [new RwrEmitter(1, "34", "airplane", toC2 - 2, 5, "track", 0.5)]),
        ]);
        var units = new[] { a, b }.ToDictionary(u => u.Id);
        var list = RwrLabels.Candidates(c, rwr, id => units.GetValueOrDefault(id), (_, _) => 0);
        var only = Assert.Single(list);
        Assert.Equal("34", only.Label);
        Assert.Equal(2, only.Receivers);
        Assert.Equal(3, only.Off, 1);
    }

    [Theory]
    [InlineData("jem", "Su-27 family (AL-31F)", "Su-27 family")]
    [InlineData("hrrp", "F-16C_50", "F-16C 50")]
    [InlineData("isar", "USS_Arleigh_Burke_IIa", "USS Arleigh Burke IIa")]
    [InlineData("size", "medium", null)]
    [InlineData("raid", "2 aircraft", null)]
    public void Nctr_results_become_identity_labels(string method, string result, string? label) =>
        Assert.Equal(label, NctrMethods.LabelOf(new NctrResult(1, method, "ok", result, null, "AWACS")));

    [Fact]
    public void Nctr_message_gives_answers_per_contact_and_frozen_contacts_parse()
    {
        const string line = """
            {"type":"nctr","coalition":"red","requests":[
            {"id":1,"state":"done","time":10,"left":0,"area":[0,0,1,1],"methods":["jem","size"],"radars":2,"results":[
              {"unit":7,"method":"jem","status":"ok","result":"F-16 (F100/F110)","reason":null,"platform":"EWR"},
              {"unit":7,"method":"size","status":"ok","result":"medium","reason":null,"platform":"EWR"},
              {"unit":8,"method":"jem","status":"none","result":null,"reason":"range 61 NM (max 40)","platform":null}]},
            {"id":2,"state":"running","time":40,"left":12,"area":[0,0,1,1],"methods":["jem"],"radars":1,"results":[
              {"unit":7,"method":"jem","status":"ok","result":"F-15 family (F100)","reason":null,"platform":"Flanker"}]}]}
            """;
        var store = new PictureStore();
        store.ApplyNctr(JsonSerializer.Deserialize<NctrMessage>(line)!);
        var seven = store.NctrFor(7);
        Assert.Equal(2, seven.Count);
        Assert.Equal("F-15 family (F100)", seven.Single(n => n.Method == "jem").Result);  // newest request wins
        Assert.Empty(store.NctrFor(8));  // no answer, only a reason
        Assert.True(store.Nctr!.Requests[1].Running);

        var unit = JsonSerializer.Deserialize<Unit>("""{"id":9,"name":null,"type":null,"callsign":null,"player":null,"group":null,"category":"airplane","coalition":"red","detected":true,"frozen":true,"lat":42,"lon":41,"alt":8000,"hdg":90,"spd":250}""")!;
        Assert.True(unit.Frozen);
        Assert.EndsWith("FRZ", Symbology.DataBlock(unit, LabelLevel.Full)[1]);
    }

    [Fact]
    public void Store_puts_identities_on_contacts_and_follows_changes()
    {
        var store = new PictureStore();
        store.Apply("blue", new Picture(1, "blue", 0, "Caucasus", [Contact(9, 42, 41)]), T0);
        Assert.Null(store.Find(9)!.Unit.Assigned);
        Assert.Equal(Identity.Unknown, Symbology.IdentityOf(store.Find(9)!.Unit, "blue"));

        var ids = JsonSerializer.Deserialize<IdsMessage>("""{"type":"ids","coalition":"blue","mode":"manual","ids":[{"unit":9,"label":"34","time":12.5}]}""")!;
        store.ApplyIds(ids);
        Assert.Equal("manual", store.IdentificationMode);
        Assert.Equal("34", store.Find(9)!.Unit.Assigned);
        Assert.Equal("34", store.Find(9)!.Unit.Shown);
        Assert.Equal("34", Symbology.DataBlock(store.Find(9)!.Unit, LabelLevel.Full)[0]);
        Assert.Equal(Identity.Hostile, Symbology.IdentityOf(store.Find(9)!.Unit, "blue"));  // identified: red

        // The next picture keeps it; clearing removes it.
        store.Apply("blue", new Picture(1, "blue", 1, "Caucasus", [Contact(9, 42.01, 41)]), T0.AddSeconds(1));
        Assert.Equal("34", store.Find(9)!.Unit.Assigned);
        store.ApplyIds(ids with { Ids = [] });
        Assert.Null(store.Find(9)!.Unit.Assigned);
    }
}
