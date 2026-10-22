using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class PavementTests
{
    private static Pavement Sample() => new(new Dictionary<(int I, int J), float[]>
    {
        [(-18, -135)] = [-35000, -269000, -35010, -269000, -35000, -268950],  // Akrotiri-ish
        [(21, 2)] = [42236, 5836, 42246, 5836, 42236, 5876],                  // Bassel-ish
    });

    [Fact]
    public void Near_returns_only_buckets_around_the_point()
    {
        var p = Sample();
        Assert.Single(p.Near(-35000, -269000, 1000));
        Assert.Empty(p.Near(0, 0, 3000));
        Assert.Equal(2, p.TriangleCount);
    }

    [Fact]
    public void Cache_round_trips()
    {
        using var ms = new MemoryStream();
        Sample().Save(ms);
        ms.Position = 0;
        var back = Pavement.Load(ms);
        Assert.Equal(2, back.TriangleCount);
        Assert.Equal(42246f, back.Near(42236, 5836, 100).Single()[2]);
    }

    [Fact]
    public void Wrong_cache_is_refused()
    {
        using var ms = new MemoryStream("DCBLC 2xxxxxxxx"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => Pavement.Load(ms));
    }
}
