using Dcb.Client.Core;
using static Dcb.Client.Core.AirfieldSigns;

namespace Dcb.Client.Core.Tests;

public class AirfieldSignsTests
{
    [Fact]
    public void Sign_models_are_read_from_their_names()
    {
        var types = SignTypes(["barrel", "a_black", "yellow_g", "course_3", "course_dash", "c_black", "taxi_omni_blue", "5_black"]);
        Assert.Equal((Kind.Location, "A"), types[1]);
        Assert.Equal((Kind.Direction, "G"), types[2]);
        Assert.Equal((Kind.Runway, "3"), types[3]);
        Assert.Equal((Kind.Runway, "-"), types[4]);
        Assert.Equal((Kind.Location, "5"), types[7]);
        Assert.False(types.ContainsKey(0));
        Assert.False(types.ContainsKey(6));
        Assert.Equal(Kind.Tower, SignTypes(["kdp_small", "kdp_crash"])[0].Item1);
        Assert.False(SignTypes(["kdp_small", "kdp_crash"]).ContainsKey(1));
    }

    [Fact]
    public void Facing_pairs_merge_into_one_letter()
    {
        // Akrotiri's two "C" signs 32 m apart, an "E" pair, and a direction sign (ignored).
        var letters = TaxiwayLetters(
        [
            new Sign(Kind.Location, "C", -36041.3, -268524.4),
            new Sign(Kind.Location, "C", -36053.4, -268494.3),
            new Sign(Kind.Location, "E", -36371.1, -267659.0),
            new Sign(Kind.Location, "E", -36336.1, -267704.2),
            new Sign(Kind.Direction, "G", -36336.8, -267765.9),
        ]);
        Assert.Equal(2, letters.Count);
        var c = letters.Single(l => l.Text == "C");
        Assert.Equal(-36047.35, c.X, 2);
    }

    [Fact]
    public void Letter_and_digit_panels_of_one_board_form_one_name()
    {
        var letters = TaxiwayLetters(
        [
            new Sign(Kind.Location, "C", 100, 100),
            new Sign(Kind.Location, "5", 100.8, 100),   // the C5 board
            new Sign(Kind.Location, "C", 400, 100),
            new Sign(Kind.Location, "4", 400, 100.8),   // the C4 board
            new Sign(Kind.Location, "7", 900, 900),     // a lone digit: not a name
        ]);
        Assert.Equal(["C4", "C5"], letters.Select(l => l.Text).Order());
    }

    [Fact]
    public void Same_letter_far_apart_stays_two_places()
    {
        var letters = TaxiwayLetters([new Sign(Kind.Location, "A", 0, 0), new Sign(Kind.Location, "A", 0, 500)]);
        Assert.Equal(2, letters.Count);
    }
}
