using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class PackageTests
{
    [Fact]
    public void Four_packages_on_channels_7_to_10()
    {
        var book = new PackageBook();
        Assert.Equal(4, book.All.Count);
        Assert.Equal([7, 8, 9, 10], book.All.Select(p => p.Radio));
        Assert.Equal(["P1", "P2", "P3", "P4"], book.All.Select(p => p.Tag));
    }

    [Fact]
    public void Channel_plan()
    {
        Assert.Equal([1, 2, 3, 4, 5], ChannelPlan.Free);
        Assert.Equal(["", "", "", "", "", "ATC", "P1", "P2", "P3", "P4"], Enumerable.Range(1, 10).Select(ChannelPlan.Role));
        Assert.Equal(3, ChannelPlan.PackageOf(9));
        Assert.Equal(0, ChannelPlan.PackageOf(6));
    }

    [Fact]
    public void An_aircraft_is_in_one_package()
    {
        var book = new PackageBook();
        var changes = 0;
        book.Changed += () => changes++;
        book.Add(1, 100);
        book.Add(1, 101);
        book.Add(3, 100);
        Assert.Equal([101L], book[1].Members);
        Assert.Equal(3, book.PackageOf(100)!.Number);
        book.Remove(101);
        Assert.Null(book.PackageOf(101));
        book.Remove(999);  // not in any: no change raised
        Assert.Equal(4, changes);
    }

    [Fact]
    public void Gone_aircraft_drop_out()
    {
        var book = new PackageBook();
        book.Add(2, 1);
        book.Add(2, 2);
        book.Prune(id => id == 2);
        Assert.Equal([2L], book[2].Members);
    }

    [Fact]
    public void On_ground_rule()
    {
        Assert.True(PictureStore.IsOnGround(false, 102, 100, false));    // parked: 2 m up
        Assert.False(PictureStore.IsOnGround(false, 160, 100, false));   // 60 m up: airborne
        Assert.False(PictureStore.IsOnGround(true, 110, 100, false));    // hovering helicopter
        Assert.True(PictureStore.IsOnGround(false, 22, 0, true));        // on a carrier deck
        Assert.False(PictureStore.IsOnGround(false, 22, 0, false));      // low over the sea, no ship
        Assert.False(PictureStore.IsOnGround(false, 5, null, false));    // terrain not known yet
    }
}
