using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class ScopeLayoutTests
{
    [Fact]
    public void Free_labels_stay_where_they_are()
    {
        var spots = ScopeLayout.Declutter([(100, 50, 20, 14), (150, 100, 20, 14)], 100, 100, 100);
        Assert.Equal((100.0, 50.0), spots[0]);
        Assert.Equal((150.0, 100.0), spots[1]);
    }

    [Fact]
    public void Stacked_labels_are_pushed_apart_and_stay_inside()
    {
        // Five emitters on the same bearing and strength: none may overlap.
        var labels = Enumerable.Repeat((100.0, 40.0, 22.0, 15.0), 5).ToList();
        var spots = ScopeLayout.Declutter(labels, 100, 100, 100);
        Assert.Equal((100.0, 40.0), spots[0]);  // the first (most important) keeps its point
        for (int i = 0; i < spots.Length; i++)
        {
            Assert.True(Math.Sqrt(Math.Pow(spots[i].X - 100, 2) + Math.Pow(spots[i].Y - 100, 2)) <= 100);
            for (int j = i + 1; j < spots.Length; j++)
                Assert.False(Math.Abs(spots[i].X - spots[j].X) < 22 && Math.Abs(spots[i].Y - spots[j].Y) < 15, $"{i} overlaps {j}");
        }
    }
}
