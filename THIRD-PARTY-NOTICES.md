# Third-party notices

Eagle's Nest is MIT-licensed (see [LICENSE](LICENSE)). It uses or works with the following,
each under its own terms.

## Libraries in the client

| Library | License |
|---|---|
| [SkiaSharp](https://github.com/mono/SkiaSharp) (Skia) | MIT (Skia: BSD-3-Clause) |
| [AvalonDock](https://github.com/Dirkster99/AvalonDock) | Microsoft Public License (MS-PL) |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) / SQLitePCLRaw | MIT / Apache-2.0; SQLite is public domain |
| .NET runtime and WPF | MIT |

The server DLL declares the Lua 5.1 C API itself (`dll/lua`) and links to the Lua that DCS
ships; Lua is MIT-licensed.

## Map data

- **Natural Earth** (`client/Dcb.Client/Assets/basemap.json`, the Scope map): public domain,
  [naturalearthdata.com](https://www.naturalearthdata.com).
- **Esri World Imagery** (the Satellite base map): loaded online from Esri under its terms of
  use; the attribution is shown on the map.
- **DCS World data**: the DCS Map, DCS Charts and DCS Satellite base maps and airfield details
  are read from the user's own DCS World installation at run time. The server package includes
  terrain height grids sampled from DCS World. DCS World, its terrains and their data belong to
  Eagle Dynamics SA and the terrain developers.

## Trademarks

DCS and DCS World are trademarks of Eagle Dynamics SA. SimpleRadio Standalone (SRS) and
Foothold are the work of their own authors. Eagle's Nest is an independent community project,
not affiliated with or endorsed by any of them.
