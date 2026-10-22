using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class SymbologyTests
{
    internal static Unit Make(string category = "airplane", string coalition = "blue", bool detected = false,
        string? type = "F-16C_50", string? player = null, string? callsign = "Uzi11",
        double altM = 7620, double spdMps = 231.5, double vs = 0, long id = 1) =>
        new(id, detected ? null : "Aerial-4-1", type, detected ? null : callsign, player, detected ? null : "Aerial-4",
            category, coalition, detected, 42, 42, altM, 90, spdMps, vs);

    [Fact]
    public void Own_unit_is_friendly() =>
        Assert.Equal(Identity.Friendly, Symbology.IdentityOf(Make(), "blue"));

    [Fact]
    public void Identified_contact_is_hostile() =>
        Assert.Equal(Identity.Hostile, Symbology.IdentityOf(Make(coalition: "red", detected: true, type: "MiG-29 Fulcrum"), "blue"));

    [Fact]
    public void Unidentified_contact_is_unknown() =>
        Assert.Equal(Identity.Unknown, Symbology.IdentityOf(Make(coalition: "red", detected: true, type: null), "blue"));

    [Theory]
    [InlineData("airplane", SymbolKind.Aircraft)]
    [InlineData("helicopter", SymbolKind.Helicopter)]
    [InlineData("ship", SymbolKind.Ship)]
    [InlineData("ground", SymbolKind.Ground)]
    [InlineData("structure", SymbolKind.Ground)]
    public void Category_maps_to_kind(string category, SymbolKind kind) =>
        Assert.Equal(kind, Symbology.KindOf(Make(category: category)));

    [Theory]
    [InlineData("F-16C_50", "F16C")]
    [InlineData("FA-18C_hornet", "FA18C")]
    [InlineData("MiG-29 Fulcrum", "MiG29")]
    [InlineData("E-3A", "E3A")]
    public void Short_type(string type, string expected) =>
        Assert.Equal(expected, Symbology.ShortType(type));

    [Fact]
    public void Player_name_wins_over_callsign()
    {
        var block = Symbology.DataBlock(Make(player: "Bullat", vs: 10), LabelLevel.Full);
        Assert.Equal(["Bullat F16C", "250↑ 450"], block);
    }

    [Fact]
    public void Ai_aircraft_uses_callsign_and_descent_arrow()
    {
        var block = Symbology.DataBlock(Make(vs: -10), LabelLevel.Full);
        Assert.Equal(["Uzi11 F16C", "250↓ 450"], block);
    }

    [Fact]
    public void Contact_block_never_names_and_marks_unknown()
    {
        var block = Symbology.DataBlock(Make(coalition: "red", detected: true, type: null, player: "Enemy"), LabelLevel.Full);
        Assert.Equal("UNK", block[0]);
    }

    [Fact]
    public void Short_level_is_altitude_only() =>
        Assert.Equal(["250 "], Symbology.DataBlock(Make(), LabelLevel.Short));

    [Fact]
    public void Ground_shows_type_only_at_full_level()
    {
        var sam = Make(category: "ground", type: "Kub 1S91 str");
        Assert.Equal(["Kub 1S91 str"], Symbology.DataBlock(sam, LabelLevel.Full));
        Assert.Empty(Symbology.DataBlock(sam, LabelLevel.Short));
    }

    [Fact]
    public void Unidentified_ground_contact_has_no_label() =>
        Assert.Empty(Symbology.DataBlock(Make(category: "ground", coalition: "red", detected: true, type: null), LabelLevel.Full));

    [Fact]
    public void Threat_range_known_for_sa6_not_for_trucks()
    {
        Assert.Equal(25000, ThreatRanges.MetersFor("Kub 2P25 ln"));
        Assert.Null(ThreatRanges.MetersFor("Ural-375"));
    }
}
