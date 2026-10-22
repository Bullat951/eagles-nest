namespace Dcb.Client.Map;

/// <summary>What the base maps credit, and the User-Agent online tile requests identify with.
/// The tiles themselves: <see cref="Gpu.WebTilesSk"/>.</summary>
public static class TileBaseMaps
{
    public const string UserAgent = "DCB-Client/0.2 (DCS World ATC/GCI tool)";

    public static string AttributionFor(BaseMap map) => map switch
    {
        BaseMap.Scope => "Natural Earth",
        BaseMap.Satellite => "Esri, Maxar, Earthstar Geographics",
        BaseMap.DcsMap => "DCS World terrain and map data (local install)",
        BaseMap.DcsCharts => "Charts: local DCS World install",
        BaseMap.DcsSatellite => "DCS World satellite imagery (local install)",
        _ => "",
    };
}
