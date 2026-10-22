using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class LandCoverTests
{
    [Fact]
    public void Triangle_fills_cells_whose_centres_are_inside()
    {
        var cells = new byte[4 * 4];
        // Lower-left half of a 4×4 grid (i along x, j along z).
        LandCover.FillTriangle(cells, 4, 4, LandCover.Class.Town, 0, 0, 4, 0, 0, 4);
        var lc = new LandCover(0, 0, 1, 4, 4, cells);
        Assert.Equal(LandCover.Class.Town, lc.At(0, 0));
        Assert.Equal(LandCover.Class.Town, lc.At(2, 1));
        Assert.Equal(LandCover.Class.None, lc.At(3, 3));
        Assert.Equal(LandCover.Class.None, lc.At(2, 2));  // centre (2.5, 2.5) is outside
    }

    [Fact]
    public void Higher_class_wins_where_classes_overlap()
    {
        var cells = new byte[2 * 2];
        LandCover.FillTriangle(cells, 2, 2, LandCover.Class.Water, 0, 0, 4, 0, 0, 4);
        LandCover.FillTriangle(cells, 2, 2, LandCover.Class.Field, 0, 0, 4, 0, 0, 4);
        Assert.Equal((byte)LandCover.Class.Water, cells[0]);
    }

    [Theory]
    [InlineData("Sea", LandCover.Class.Sea)]
    [InlineData("lake", LandCover.Class.Water)]
    [InlineData("Town", LandCover.Class.Town)]
    [InlineData("Trees", LandCover.Class.Trees)]
    [InlineData("field", LandCover.Class.Field)]
    [InlineData("Road", LandCover.Class.None)]
    [InlineData("Land", LandCover.Class.None)]
    public void Maps_surface_classes(string name, LandCover.Class cls) => Assert.Equal(cls, LandCover.ClassOf(name));

    [Fact]
    public void Cache_round_trips()
    {
        var lc = new LandCover(-460000, -400000, 100, 2, 3, [0, 1, 2, 3, 4, 6]);
        var ms = new MemoryStream();
        lc.Save(ms);
        ms.Position = 0;
        var back = LandCover.Load(ms);
        Assert.Equal((-460000.0, -400000.0, 100.0, 2, 3), (back.X0, back.Z0, back.Cell, back.Nx, back.Nz));
        Assert.Equal(lc.Cells, back.Cells);
    }
}
