using Dcb.Client.Core;

namespace Dcb.Client.Core.Tests;

public class BeaconsTests
{
    // Shaped like Syria's beacons.lua (entries from Akrotiri, id 44, and a world VORTAC).
    private const string Lua = """
        beaconsTableFormat = 2
        beacons = {
        	{
        		display_name = _('');
        		beaconId = 'airfield44_0';
        		type = BEACON_TYPE_ILS_LOCALIZER;
        		callsign = 'IAK';
        		frequency = 109700000.000000;
        		position = { -35143.730469, 20.984022, -270549.812500 };
        		direction = 111.071989;
        		positionGeo = { latitude = 34.595184, longitude = 32.969540 };
        		sceneObjects = {'t:126290339'};
        		chartOffsetX = 3211.500000;
        	};
        	{
        		display_name = _('Akrotiri');
        		beaconId = 'airfield44_2';
        		type = BEACON_TYPE_TACAN;
        		callsign = 'AKR';
        		frequency = 116000000.000000;
        		channel = 107;
        		position = { -36624.153826, 20.984020, -266665.539958 };
        		direction = -17.825354;
        		positionGeo = { latitude = 34.583989, longitude = 33.012622 };
        		sceneObjects = {'t:126299555'};
        	};
        	{
        		display_name = _('GHI');
        		beaconId = 'world_13';
        		type = BEACON_TYPE_VORTAC;
        		callsign = 'GHI';
        		frequency = 113800000.000000;
        		channel = 85;
        		position = { -355603.781250, 498.786998, 72566.945313 };
        		direction = 0.000000;
        	};
        };
        """;

    [Fact]
    public void Entries_are_read_with_their_airfield()
    {
        var b = Beacon.Parse(Lua);
        Assert.Equal(3, b.Count);
        var ils = b[0];
        Assert.True(ils.IsLocalizer);
        Assert.Equal("IAK", ils.Callsign);
        Assert.Equal("109.70", ils.FrequencyText);
        Assert.Equal(44, ils.AirfieldId);
        Assert.Equal(291.07, ils.InboundCourseTrue, 2);  // beam points 111°, aircraft fly 291° (runway 28)
        Assert.Equal("107X", b[1].ChannelText);
        Assert.True(b[1].IsTacan);
        Assert.Null(b[2].AirfieldId);
        Assert.Equal(-355603.78125, b[2].X, 3);
    }
}
