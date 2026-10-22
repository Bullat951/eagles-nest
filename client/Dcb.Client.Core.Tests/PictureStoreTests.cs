using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class PictureStoreTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static Picture Pic(params Unit[] units) => new(1, "blue", 0, "Caucasus", units);

    private static Unit Own(long id) => SymbologyTests.Make(id: id);
    private static Unit Contact(long id) => SymbologyTests.Make(id: id, coalition: "red", detected: true, type: "MiG-29 Fulcrum");

    private static Weapon Missile(long id, double lat) => new(id, "AIM_120C", "blue", 1, 7, lat, 36, 8000, 90, 900, 0);

    [Fact]
    public void Missile_keeps_a_trail_and_goes_when_gone()
    {
        var store = new PictureStore();
        for (int i = 0; i < MissileTrack.TrailLength + 3; i++)
            store.Apply("blue", Pic() with { Weapons = [Missile(5, 35 + i * 0.01)] }, T0.AddSeconds(i));
        var m = Assert.Single(store.Missiles);
        Assert.Equal(MissileTrack.TrailLength, m.Trail.Count);
        Assert.Equal(m.Weapon.Lat, m.Trail[^1].Lat);

        store.Apply("blue", Pic(), T0.AddSeconds(20));
        Assert.Empty(store.Missiles);
    }

    [Fact]
    public void Picture_with_weapons_parses()
    {
        var json = """{"type":"picture","version":1,"coalition":"blue","time":1,"theatre":"Syria","bullseye":null,"units":[],"weapons":[{"id":3,"type":null,"coalition":"red","shooter":null,"target":4940,"lat":35.1,"lon":36.2,"alt":5000,"hdg":270,"spd":800,"vs":-20}]}""";
        var pic = System.Text.Json.JsonSerializer.Deserialize<Picture>(json)!;
        var w = Assert.Single(pic.Weapons!);
        Assert.Null(w.Type);
        Assert.Equal(4940, w.Target);
    }

    [Fact]
    public void Destroyed_own_unit_disappears_at_once()
    {
        var store = new PictureStore();
        store.Apply("blue", Pic(Own(1), Own(2)), T0);
        store.Apply("blue", Pic(Own(1)), T0.AddSeconds(1));
        Assert.Single(store.Tracks);
    }

    [Fact]
    public void Lost_contact_fades_then_expires()
    {
        var store = new PictureStore();
        store.Apply("blue", Pic(Contact(7)), T0);
        store.Apply("blue", Pic(), T0.AddSeconds(1));

        var track = store.Find(7)!;
        Assert.True(track.Lost);
        Assert.Equal(0.5, track.Freshness(T0.AddSeconds(15)), 3);

        store.Expire(T0.AddSeconds(31));
        Assert.Null(store.Find(7));
    }

    [Fact]
    public void Reacquired_contact_is_tracked_again()
    {
        var store = new PictureStore();
        store.Apply("blue", Pic(Contact(7)), T0);
        store.Apply("blue", Pic(), T0.AddSeconds(1));
        store.Apply("blue", Pic(Contact(7)), T0.AddSeconds(5));
        Assert.False(store.Find(7)!.Lost);
    }

    [Fact]
    public void Expiring_the_selected_track_clears_selection()
    {
        var store = new PictureStore();
        store.Apply("blue", Pic(Contact(7)), T0);
        store.Select(7);
        store.Apply("blue", Pic(), T0.AddSeconds(1));
        store.Expire(T0.AddSeconds(40));
        Assert.Null(store.SelectedId);
    }
}
