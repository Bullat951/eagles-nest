# DCB — Digital Combat Battlespace

External ATC/GCI application for DCS World. A server-side DLL publishes the
battlespace picture; controllers connect with an external client by IP + password.

## Decisions (2026-10-18)

| Topic | Decision |
|---|---|
| Fog of war | Yes. A controller sees only their coalition's sensor picture. Admin god-view is a later option. |
| Voice | SRS is in scope (TTS + controller voice). Text (`outTextForUnit`) as fallback. |
| Server side | C++ DLL (`dcb.dll`, MSVC 2022 Build Tools, x64), loaded by a GameGUI hook. Links against DCS `bin\lua.dll` (Lua 5.1 API). |
| Client | C# / .NET 10 WPF (easiest to build on this machine; `dotnet build`). Maps drawn with Skia on the GPU (phase 4; Mapsui before). |
| Transport | TCP, newline-delimited JSON for v1 (easy to debug), password auth on connect. Binary/compression later if needed. |
| Repo | `D:\Claude\DCB` |

## Architecture

```
DCS server process
 ├─ Saved Games\DCS.server\Scripts\Hooks\DCB-hook.lua
 │     onSimulationFrame: every N frames → net.dostring_in('mission', collector)
 │     collector (mission env) returns snapshot as Lua table/string
 │     hook passes snapshot to DLL; drains DLL command queue
 └─ Mods\Services\DCB\bin\dcb.dll
       state store (units, sensors, airbases), per-coalition filtering
       TCP server thread, auth, per-client send queues
                 │
        DCB Client (WPF)  map · tracks · airbases · BRAA/bogey dope · PAR/ASR
```

Rule: Lua only collects. The DLL never blocks the sim thread — all network I/O on
worker threads, handoff via lock-protected queues.

## Data sources

- Units: `coalition.getGroups`, `Unit:getPoint/getVelocity/getPosition`, type, callsign, player name.
- Radar picture: `Controller:getDetectedTargets(Controller.Detection.RADAR)` on EWRs, SAM search radars, AWACS, ships.
- RWR (derived): emitters with `Unit:getRadar()` active, range + LOS (`land.isVisible`), lock = radar's tracked target. Real cockpit RWR requires a client-side Export plugin — out of scope for v1.
- Airbases/FARPs/carriers: `world.getAirbases()`, `Airbase:getRunways()`, terrain config for ILS/TACAN/frequencies.
- Coordinates: `coord.LOtoLL` for map; keep DCS x/z for math.

## Phases

1. **Pipeline skeleton** — hook loads DLL, DLL serves TCP with password, client connects and lists units. *Proves the whole chain.*
2. **Map + tracks** — map, unit symbology, coalition-filtered sensor picture.
3. **Airbases** — airfields, FARPs, carriers, runways, frequencies.
4. **GCI tools** — BRAA, bogey dope, picture calls, tracks history.
5. **ATC/landing** — approach sequencing, PAR/ASR glidepath deviation.
6. **SRS** — TTS calls to players, controller voice on frequency.

## Phase 1 detail — pipeline skeleton

**Config** `Saved Games\<instance>\Config\DCB.lua`: `port` (default 10309), `blue_password`,
`red_password`, `interval` (seconds). The password a controller logs in with decides their
coalition, so nobody can pick the other side's picture.

**Collector** (injected once into the `server` env via `net.dostring_in`, called each interval):
for each coalition, own units (full data) plus enemy units seen by own radars
(`getDetectedTargets(RADAR)` on group/unit controllers). Encodes each coalition picture as one
JSON string; returns `blue .. "\n" .. red`.

**DLL API** (`require('dcb')`, Lua 5.1, import lib generated from DCS `lua.dll` exports):
- `dcb.start(port, blue_pw, red_pw)` → starts the network thread
- `dcb.publish(coalition, json)` → stores the latest picture for `"blue"`/`"red"`
- `dcb.poll()` → returns queued log lines (connects, auth failures) for the hook to log
- `dcb.stop()`

**Protocol v1**: TCP, one JSON object per line (UTF-8, `\n`-terminated).
- client → `{"type":"auth","password":"…","client":"DCB 0.1"}`
- server → `{"type":"auth_ok","coalition":"blue"}` or `{"type":"auth_fail"}` then close
- server → `{"type":"picture","coalition":"blue","time":…,"units":[…]}` whenever a new picture is published
- unit: `id,name,type,callsign,player,group,category,coalition,lat,lon,alt,hdg,spd,detected`

**Client**: `Dcb.Client.Core` (connection + models), `Dcb.Probe` (console, used for automated
checks), `Dcb.Client` (WPF: connect form + unit table).

**Done when**: on the running test server, the probe logs in with the blue password, gets
Blue's own units plus only the red units Blue's radars see; red password gives the mirror;
a wrong password is refused; the WPF client shows the same table.

## Phase 2 detail — map, symbols, dockable layout

Decisions (2026-10-18): symbol style B (scope + data block) with separate air / ground /
ship markers; maps switchable, several map windows allowed; layout dynamic, any panel can
float in its own OS window. Panels: **Map**, **Objects**, **SRS**, **ATC** (SRS and ATC are
empty shells in phase 2, filled in phases 5–6).

### Symbols (style B)
| | Friendly | Hostile | Unknown |
|---|---|---|---|
| Identity shape | circle | diamond | square |

- **Air**: outline shape, speed vector (length = 1 min of travel), leader line to block.
  Line 1 `player-or-callsign type`, line 2 `altitude(100s ft) ↑/↓ speed(kt)`.
  Helicopter: rotor bar on top. Contacts: line 1 is type or `UNK`, never a name.
- **Ground**: filled shape on a base bar, no vector; one-line type label, shown only below a
  zoom threshold.
- **Emitter** (radar transmitting): ground marker + radiating arcs. Hostile SAM threat ring
  (dashed), toggle per map window; ranges from a client-side table by type.
- **Ship**: shape on a hull arc, name label.
- **Lost contact**: keeps last position, fades over 30 s, then removed.
- Label level per map window: full / short / symbol only.

### Server changes
- Unit gains `vs` (vertical speed, m/s) for ↑/↓ and `emitter` (radar on, via `Unit:getRadar()`).
- Picture gets `"version":1`; client ignores unknown fields.

### Client
- **Shell**: AvalonDock (dock, tab, float to another monitor). Window menu: New map,
  Objects, SRS, ATC, Connect…. Layout and per-map settings saved to `%AppData%\DCB\` and
  restored on start.
- **PictureStore** (Core): latest picture + per-track history and lost-contact timers;
  raises one update event per picture; all panels read from it.
- **Map panel**: Mapsui map control; per-window base map and symbol settings. Base maps:
  1. *Scope* — dark vector map from Natural Earth coastlines/borders (public domain),
     clipped to DCS theatres and shipped with the app; works offline.
  2. *OpenStreetMap* tiles — disk cache, proper User-Agent (OSM tile policy).
  3. *Satellite* — ESRI World Imagery tiles (subject to its terms).
  Symbol layer drawn with SkiaSharp (Mapsui's renderer). Click a track → select in all panels.
- **Objects panel**: current table + filters (air / ground / ship / contacts); double-click
  centres the active map.
- **Core tests** (xUnit): data-block formatting, identity/category → symbol mapping,
  lost-contact ageing.

### Done when
Two map windows can show different base maps at once, one floated on its own window;
air, ground, emitter and ship symbols render per the table above from the live test
server; contacts show no names; layout survives a restart; tests pass.

### Third-party (needs download)
- NuGet: `SkiaSharp.Views.WPF` (MIT; replaced `Mapsui.Wpf` in phase 4), `Dirkster.AvalonDock`
  + `Dirkster.AvalonDock.Themes.VS2013` (Ms-PL), `xunit` + `Microsoft.NET.Test.Sdk` (Apache-2.0/MIT).
- Natural Earth 1:10m coastline, land and admin-0 boundaries (public domain, a few MB,
  from naturalearthdata.com).

## Phase 2b — theatre limit and DCS maps (2026-10-18)

User asked for DCS maps and for the view to stay inside the DCS theatre; both map options
(A: DCS raster charts, B: terrain map generated from DCS) are to be tried.

### Georeference (shared)
DCS map coordinates (x north, z east, metres) are a per-terrain transverse Mercator. The
server sends a **control grid**: lat/lon of an N×N grid of x/z points over the theatre
bounds, computed with `coord.LOtoLL`. The client converts any x/z by bilinear interpolation
in that grid. Terrain-agnostic, no projection constants in the client. Grid size 81×81
(2026-10-23): 21×21 was 5–12 m off on Syria but 30–140 m on Caucasus, which lies ~9° from
its central meridian (aircraft drawn beside the runway in the airfield views); 81×81 is
≤ 8 m everywhere, for a ~130 KB theatre message once per mission.

### Server
- Hook reads `Terrain.GetTerrainConfig('id' | 'SW_bound' | 'NE_bound')` (as the Mission
  Editor does) and `Mods\terrains\<id>\Map\towns.lua`.
- New message `theatre` (all coalitions): id, bounds (x/z), control grid, towns.
- New message `terrain` (all coalitions): x/z grid at `terrain_step` m (DCB.lua, default
  2000): int16 height (m) + uint8 surface type (`land.getSurfaceType`), base64. Sampled in
  the mission env a few ms per frame; DLL assembles rows and caches the grid on disk
  (`Saved Games\<instance>\DCB\terrain_<id>_<step>.bin`), so it is sampled once per terrain.
- DLL: "all-coalition" channels besides the per-coalition pictures; each authed client gets
  the latest version of every channel once.

### Client
- **Theatre lock**: pan limited to the theatre's Mercator bbox, zoom-out limited to fit it;
  theatre border drawn, outside dimmed. Applies to every base map.
- **B · DCS terrain** base map: hypsometric tint + hillshade + water from the `terrain`
  grid, town names (zoom dependent). Drawn as affine-warped blocks via the control grid.
- **A · DCS charts** base map, read from the local DCS install only (never copied):
  `Mods\terrains\<id>\RasterCharts\rasterCharts.sup5` gives each tile's name and x/z bbox;
  tiles are 1024² DXT5 `.dds` inside the scale folders' zips (16–256 m/px). Client picks the
  scale and mip level from zoom, decodes DXT5 itself, LRU-caches bitmaps. DCS folder from
  the registry (`HKCU\Software\Eagle Dynamics\DCS World`), overridable in settings. If the
  terrain is not installed locally the option says so.

### Done when
Caucasus test mission: view cannot leave the theatre; DCS terrain map shows relief, sea
and towns aligned with the Natural Earth coastline; DCS charts map shows chart tiles
aligned the same way at several zoom levels; both work docked and floating.

### Results (2026-10-20)
- Charts: `rasterCharts.sup5` places each tile with the record *before* its name (pairing
  shifted by one, size-checked). Caucasus 16–256 m and Syria 32/64 m (single
  `rasterCharts.zip`) both render seamlessly.
- User-provided Syria data: `GoogleEarthSuperres.zip` (DCS F10-style render, KML
  super-overlay, names `{z}_{y}_{x}`) → **DCS Imagery** base map, archive chosen per theatre
  via Window → Map data (stored in settings, never copied). `DCS_Syria_High_Detail_Maps.zip`
  (stitched 32/64 m charts, no georeference) is redundant with the DCS Charts base map.

## Phase 2c — Foothold (2026-10-20)

Test server runs **Foothold SY extended 5.3.5** (official release, `_downloads/foothold`),
no persistence (DCS `MissionScripting.lua` untouched).

- **Two scripting envs**: `net.dostring_in('server')` (our collector) is *not* the trigger
  env where mission scripts and Foothold's `bc` live. Bridge: hook runs
  `a_do_script(...)` via the `'mission'` state; code there calls
  `net.dostring_in('server', ...)` to hand data back.
- `lua/foothold.lua` (injected into the trigger env) exports, every 10 s, zones (name, side
  0/1/2, trigger-zone x/z/r, active, suspended, airbase, facility, logistic, built/max
  upgrades, WPT number) and links (`bc:_dynamicHybridGetConnections()`); hidden zones are
  left out. Channel `foothold`, published to all coalitions only when changed.
- Client: Foothold layer on every map (toggle "Zones"), Zones panel (Window → Zones),
  shared zone selection, double-click centres the map.
- Load: collector avg ~9 ms / max ~33 ms per 1 s run, ~50 KB pictures (vs negligible on the
  11-unit test). Next: refresh ground units less often than air to cut the spikes.
- Dev aid: `dev_probe = true` in `DCB.lua` runs `Saved Games\<instance>\DCB\probe.lua` once
  (first line `-- env: <state>` picks the state) and writes `probe.out`.

## Phase 2d — DCS Map base map (2026-10-20)

User wanted the real DCS F10 maps. F10 "map" mode's vector lines decode from
`Mods/terrains/<id>/Map/<id>.sup5` (docs/DCS_MAP_FORMAT.md); its coloured fills and the
satellite mode are not in files we can read. **DcsMap** base map = DCS terrain relief
(server sampling now 500 m: Syria 1681×1801 in 59 s, 9 MB cache) + DCS vector lines (roads,
rivers, rail, power lines; MGRS grid toggle) read client-side from the local DCS install,
+ town names. Lines are cached as a bitmap that is only shifted/scaled while moving and
re-rendered ~120 ms after the view settles (vector layer 11 ms → 0.03 ms per frame).

Open: Syria's sup5 holds few roads around cities (F10 may draw roads from
`roads/*.rn4`); `.gn4` place names not decoded (towns.lua used); the 500 m terrain message
is ~12 MB per login — compress or cache by hash on the client before internet use.

## Phase 3 — airfields for ATC (2026-10-20)

User: ATC needs the full airfield picture to control traffic. Decided: **C** — vector
airfield diagrams first, then a DCS satellite capture of airfields as an optional background.

### 3a · Vector diagrams
- **Server** (mission env, documented API): `world.getAirbases()` → per airdrome/helipad:
  name, id, category, owner, position; `Airbase:getRunways()` (name, course, length, width,
  centre); `Airbase:getParking()` (spot number, position, type). Channel `airfields`
  (public, export yes); owners re-checked every 60 s (Foothold captures change them), sent
  when changed. Ships excluded (they move; they are in the picture).
- **Taxiways**: decode `Mods/terrains/<id>/AirfieldsTaxiways/<field>.rn4/.rn5`
  (`landscape4::lRoadNetwork`: runway "vpp", taxiway markings, stand types). Client-side from
  the local install first, like the DCS Map lines.
- **Client**: airfield layer on every map, shown when zoomed in (runways with numbers,
  taxiways, numbered parking spots, live ground traffic from the picture); ATC window lists
  airfields (owner, runways, parking count) and centres the map on one.

Result (2026-10-20): Syria 234 airfields (225 airdromes, 9 helipads), 147 KB. Runways and
parking from the API, taxiways decoded from `.rn4` (`tools/dcs_taxiways.py`,
`Map/Airfields/Taxiways.cs`); checked at Akrotiri over real satellite imagery: runway,
taxiways and numbered stands line up, live ground traffic on its spots. Map shows runway
lines + names when zoomed out, full diagram when zoomed in; ATC window lists airfields and
opens one on the map.

### 3a+ · Airport-diagram pictures under live data (2026-10-20)
User-provided kneeboard book (CombatWombat's Syria airfield diagrams, PNG pages in
`Diagrams/`, git-ignored, local only). `tools/DiagramIndexer` (C#, Windows OCR, no
downloads): OCR the page title/ICAO → match the server airfield (name, ICAO aliases in
`tools/DiagramIndexer/aliases.json`, runway headings as confirmation); detect black runway
bars; fit a north-up similarity transform page px → DCS metres from runway ends (all
bar→runway assignments tried; rotation ≤1.5°, plausible scale; ties broken by where the
other bars land); night (NVG) pages reuse the day page's fit. Writes `Diagrams/diagrams.json`.
Syria: 109 diagram pages, 86 placed (43 day + night pairs). Client: Window → Map data → Set
airfield diagrams folder; map toggle "Diagrams"; page background made transparent, clipped
to the chart frame, day pages on light maps and night pages on dark ones; DCS runway drawn as
a cyan outline on top as ground truth, then parking numbers and live traffic.
Checked: Aleppo, Damascus (both runways) exact; Bassel Al-Assad: chart draws its parallel
runways closer and unstaggered than DCS — one runway exact, the other ~50 m off.
Not placed: helipads (no runway bar), Nicosia, Rosh Pina, Kingsfield (chart rotated vs DCS).

### 3c · DCB airport diagrams (2026-10-21)
User: build our own diagram per airfield from the collected data, as a separate zone — not
the global map (zooming out must not show the map). General maps show runways only.
- **AirfieldDiagramView** (ATC tab): own canvas, no base map; true-north-up local frame
  around the field; pan/zoom clamped to the field's extent (min zoom = whole field).
  Draws DCS taxiways (rn4) at width, exact runways (API) with designators and centre lines,
  numbered parking, live traffic (picture units converted to local metres), north arrow,
  scale bar.
- **Labels from the charts** (DiagramIndexer), mapped page px → DCS metres with the
  existing fit, stored per day page in `diagrams.json` (`labels`, `tower`):
  - Taxiway letters: white-on-black oval discs (runway bars masked first so discs touching
    a runway separate). The glyphs are ~5 px tall — Windows OCR skips lone characters that
    small (tried: single crops, cubic upscales, strips, sheets), so they are read by
    **template matching** against A–Z/0–9 rendered with WPF in common sans fonts
    (correlation on a height-normalised 14×14 grid, accept ≥ 0.74). Checked by eye:
    Incirlik 16/16, Akrotiri 7/7 (+1 rejected), Damascus ~18/21 (R→K, B1→B; a few missed).
    The client snaps each letter onto the nearest DCS taxiway within 80 m (not runways).
  - Area names: OCR of the page decoded at 3×, split into phrases at wide gaps, stacked
    lines joined, chart furniture (ELEV, MHz, …) dropped, kept within 1.5 km of the field.
    Some OCR misspellings remain ("CHA DISPERSAL", "AVAT'ON").
  - Tower: VHF/UHF frequencies from the TOWER box (HF/FM entries read too unreliably);
    shown in the ATC header.
  - Blue stand-group badges (A1-A4) are not read: glyphs touch, both OCR and templates
    fail; DCS parking numbers cover stands.
  Result on Syria: 302 taxiway letters, 152 area names, 40 tower entries over 44 day pages.
  Labels are third-party (CombatWombat) → local only, not exported; the geometry is DCS data.

### 3b · DCS satellite imagery (2026-10-21) — read, not captured
No screen capture needed: the terrain's own satellite ground texture ships in the install as
`clipmaps\colortexture\<R>m\*.tif.clipmap` (Syria: 128/16/8/4/1 m per px, 18 GB for the
default season) and is read in place by the client — base map **DcsSatellite**
(`Map/Satellite/ClipmapIndex.cs`, `Map/DcsSatelliteLayer.cs`; format in DCS_MAP_FORMAT.md).
- Each resolution is drawn at the clipmap's own mip level for the zoom, coarsest first, so 1 m
  imagery (airfields, towns) covers 4 m/16 m where it exists; tiles decode on 2 workers into
  the shared 512 MB tile cache.
- "No imagery" fill is made transparent (solid blocks, blended coastal blocks via their BC3
  indices); DCS's 500 m water cells paint the sea over the seabed DCS stores under water.
- Verified: orientation by correlation with DCS's water mask (0.82 vs ≤ 0.08 for the
  alternatives); Cyprus/Akrotiri peninsula in place; DCS's imaged runway at Akrotiri lies
  under our 10/28 runway. Pan: 115–121 fps zoomed in, 57 fps theatre-wide (same as the
  other base maps there).
- Local only (ED imagery), never exported. Seasons (spring/winter folders) not offered yet.
- The F10 *map* mode is our DcsMap (decoded sup5 vector lines over DCS-sampled relief).

### 3d · DcsMap closer to DCS's F10 map (2026-10-21), staged
Stage 1 (done): roads and railways from the terrain's road network `roads/<id>.rn4` —
646k typed edges, 423 000 km (sup5 had a coarse subset): motorway (palette dark red),
primary/secondary (palette orange), tracks (thin dashed, close zoom), rail; cached locally.
Relief coloured by DCS's own F10 palette ramp (`mapmodepalette.tif`), water by its water
swatch; height scale uncalibrated (0–5000 m) until compared with a DCS F10 screenshot.
Measured: pan 104–119 fps at city/street zoom, 51 fps theatre-wide; +240 MB peak memory
over the Scope map. Stage 2 (done, after a brief park): area fills — water, built-up,
forest, fields, beach — from the terrain mesh `surface/<id>.surface5` (format: DCS_MAP_FORMAT.md
"Terrain surface"), extracted once (~11 s at 100 m) into `%LocalAppData%\DCB\cache\landcover_*.bin`
(2.5 MB), coloured with DCS's palette swatches. Zoomed out they are baked into the 500 m
relief image (majority class per cell, shaded with the relief) at no draw cost; below
60 m/px `LandCoverLayer` draws the 100 m blocks, built on demand into the tile cache.
Measured: close/mid pan 89–118 fps. Theatre-wide pan is 34–37 fps on **every** base map at
the moment (Scope too, 57–66 earlier the same day), so that drop is not the fills — open:
find the common cause. (The gaps noted then — central Damascus without a town, forest
blocks in southern Turkey — were the node-placement bug fixed in 3e.)

### 3e · DcsMap as disk tiles (2026-10-21, user's choices: hybrid pre-render, live MGRS)
Everything static on the DCS Map — relief, area fills, roads, rivers — is now one picture:
256 px PNG tiles on DCS's x/z grid at 2^L m/px (L = 1…10), `Map/DcsMapTiles` +
`Map/DcsMapLayer`, under `%LocalAppData%\DCB\tiles\<terrain>\<version>_<hash of the source
files>\<L>\<tx>_<tz>.png` (old versions deleted). Levels 10…5 (to 32 m/px) are rendered once
in the background (Syria 15 577 tiles, ~100 s, ~385 MB, progress note on the map); finer
tiles are made when first viewed and kept. 2–4 STA render threads, urgent (on-screen) first;
road and land-cover sources load on demand and are released after 30 s idle; an underlay 3
levels coarser covers tiles still rendering. MGRS grid stays a live `PictureOverlay`.
Memory after full GC: theatre zoom 540 MB vs Scope 487; city zoom 586 vs 481 (before tiles
the DCS Map cost ~+240 MB). Failures go to `errors.log` in the tile folder.

**Coastline / misplaced areas (user: "misaligned", "huge parts of land missing").** Cause:
surface5 meshes were placed with the neighbouring node's translation (DCS_MAP_FORMAT.md,
"Nodes"), so the sea mesh ran over the coastal land and lakes/forests/towns sat ~8 km east.
Fixed in `LandCover` (cache `_v4`), plus: only the finest level of detail is rasterised;
sea is drawn where DCS's surface grid has water nearby and the height is below 1 m (the coast
follows the relief and matches DCS exactly); lakes from the lake meshes. Checked against DCS
Satellite at the same views: Levant coast, Tartus/Arwad, Lake Homs, Lake Assad, Damascus.

## Principle — website export (decided 2026-10-20, built later)

Mission information will be exported to the server group's website (React frontend +
Node.js backend) to track team progress externally. Until that exporter is built, all
development keeps it possible:

- **All mission information goes to the website, except units** (user, 2026-10-20). Theatre,
  Foothold zones/ownership/links, events (captures, kills, sorties, landings), statistics
  and team progress are exported. Unit data — positions, tracks, radar pictures, live or
  delayed — is never exported.
- **The server is the source of truth.** Anything the website needs is produced server-side
  (hook/DLL), never only inside a controller client.
- **Documented, versioned messages** ([PROTOCOL.md](PROTOCOL.md)); readers ignore unknown
  fields; changes are additive. Each message is marked **export: yes/no**; `picture` (units)
  is the only "no" today, and any new unit-level data is "no" too.
- **Likely shape** (to confirm with the user): the DLL pushes public snapshots plus an event
  stream (zone captures, kills, sorties, landings) to the Node backend over HTTPS with an
  API key; the React app reads from the backend. No inbound connections to the DCS server.

## Performance notes (2026-10-20)

Zoom test (Debug snapshot `zoomtest`, 60 steps at ~60 Hz): label text and halo geometry
were rebuilt every redraw (Foothold layer up to 73 ms). Fixed with a frozen-drawing label
cache, shared frozen pens/brushes and one overlay redraw per rendered frame: every layer
now under 1 ms per redraw, test at its timing floor. Base map brightness is a per-map
dimming layer between base map and overlays.

**Panning at close zoom (2026-10-21)** was still very slow although every OnRender took
< 0.5 ms: the cost was on WPF's render thread. New snapshot `pantest[:layers to hide]`
(90 pan steps, counts rendered frames). At max zoom (res 0.6) over Damascus: 4 fps with all
layers; hiding Foothold → 19 fps; hiding Foothold + theatre border → 108 fps (terrain,
vector map, symbols cost nothing). Cause: shapes whose screen size grows with zoom —
Foothold zone circles and dashed links, threat rings, the dashed theatre border (millions
of DIPs long) and its CombinedGeometry "outside" fill — tessellated and dashed in full each
frame. Fix: `Map/ViewClip` clips in screen space first (circles: skipped / view-fill /
visible arc only, lines Liang–Barsky, polygons Sutherland–Hodgman, dash phase kept so dashes
don't crawl; outside dim = even-odd fill of view + clipped border). After: 110–119 fps at max
zoom on DCS Map, Charts, Imagery and Scope, same as with those layers hidden. Rule for new
overlays: anything that can grow past the view goes through ViewClip.

## BRAA ruler and bullseye (2026-10-21)
User choices: right-drag draws a line, shown only while dragging; hold **B** (setting
`BullseyeKey`) for the cursor's bullseye call.
- **Magnetic** everywhere: DCS's own `magvar` model (server env, mission date) sampled on a
  0.5° grid in the `theatre` message, so bearings match the cockpit. Variation is taken at
  the line's start (ruler) or at the bullseye (BE calls). True bearings only as fallback ("T").
- **Ranges** in NM on the WGS84 ellipsoid (Vincenty), checked against an independent run.
- **Ruler**: `072°  34.2 NM`; ends snap to a track under the pointer and follow it. With an
  aircraft at the far end it is a BRAA: altitude (ft) and aspect HOT ≤30° / FLANK ≤70° /
  BEAM ≤110° / DRAG, the target's nose angle off the near end.
- **Bullseye**: own coalition's (`picture.bullseye`, fog of war kept) drawn on every map;
  holding the key shows `BE 247/38` next to the pointer, in any window (class handler on
  Window; ignored while typing in a text box).

**Static overlays as pictures (2026-10-21, user's choice "option 2").** Measured at
theatre zoom: base maps alone pan at 105 fps; with the overlays redrawn as shapes and
labels every frame, 39 fps (Foothold and airfield labels the heaviest). Now everything static
— Foothold zones, airfields, theatre border/towns — derives from `Map/PictureOverlay`: drawn
once into a bitmap of the view + 35 % margin, shifted/scaled while the map moves, redrawn
sharp 120 ms after it settles or at once when its inputs change (each layer's `ContentKey`:
data references, selection, style flags; `MarkDirty` for loaded taxiways/diagram tiles).
Track symbols and the ruler/bullseye stay live. Result: theatre-wide pan 96–104 fps on DCS
Map, Satellite and Scope; city zoom 93 fps. Cost: one redraw after a pan/zoom or a content
update — ~60 ms (airfields) + ~38 ms (zones) at theatre zoom, 5–12 ms close in.
The base maps were already pictures (tiles, relief, cached road bitmap, baked fills).

## Approach paths (2026-10-21)
The airfield selected in the ATC tab gets, on the general maps, an extended centre line per
runway end out to 10 NM with round-capped ticks at 2, 5 and 10 NM and an "RWY xx" tag
beyond the outer end (AirfieldLayer, shown up to ~1500 Mercator m/DIP, clipped by ViewClip).
Both directions are drawn; picking the active runway from wind is for later.

## Phase 4 — GPU map renderer (2026-10-21, user's choice "option 2")

User: map performance not acceptable on a strong PC. The map was Mapsui (Skia on the **CPU**
into a full-window bitmap re-uploaded every frame) under 13 stacked WPF layers, each composed
by WPF's render thread. Rest of the app (docking, panels, data) stays WPF.

Spike (same scene: 1584×961, tiles + 400 rings + 400 labels): Skia GPU (OpenGL via
SKGLElement, SkiaSharp 3.119 already in the tree through Mapsui) 1.3 ms CPU per frame, Skia CPU
surface 10.9 ms. Both capped at the display rate.

**Design.** One drawing path, Skia, for everything on a map:
- `Map/Gpu/MapSurface` — SKGLElement (GPU), falling back to SKElement (Skia CPU) if OpenGL
  fails. Draws only when something changed; continuous only while animating.
- `Map/Gpu/MapNavigator` — our own pan/zoom (drag, wheel zoom at the cursor, smooth zoom),
  theatre bounds, CenterOn/ZoomTo/ZoomToBox; Mapsui is removed.
- `Map/Gpu/MapLayer` — `Draw(SKCanvas, MapView)`; layers drawn bottom-up into one surface,
  in DIPs (canvas scaled by the DPI). Static overlays are plain draws (GPU replays them for
  ~1 ms); `PictureOverlay` goes away.
- Drawing kit: cached paints (fill, stroke, dash with phase), labels with halo (cached text
  blobs), ViewClip on SKPoint/SKPath.
- Images: `TileCache` holds `SKImage`. Tile *producers* stay as they are (DCS Map tile
  renderer, clipmap/DDS/imagery/diagram decoders); bitmaps convert at the cache boundary.
- OSM/Esri tiles: own fetcher (HttpClient, same disk cache folders, OSM policy: 2 connections,
  User-Agent).
- Shared style: `style/map-style.json` — identity/coalition colours, zone styles, symbol
  sizes, line widths/dashes, label fonts and sizes. Read by the desktop at start and later by
  the website (React map library over a public base map; ED-derived data never exported).

**Switch.** The old WPF path was kept behind a `Renderer` setting while the two were
compared; after the user's OK ("zooming feels smooth now", 2026-10-21) it was deleted with
Mapsui, `PictureOverlay`, `ViewClip`, `Palette` and the WPF variants of the tile decoders.
`DCB_RENDERER=cpu` remains: Skia on the CPU, for a PC whose OpenGL fails. The ATC tab's own
diagram view (`AirfieldDiagramView`) is not a map layer and stays WPF.

**Stages.** 1 surface + navigator + frame stats · 2 drawing kit + style file · 3 base maps
(Scope, DCS Terrain, DCS Map, MGRS, Charts, Imagery, DCS Satellite, OSM, Satellite) ·
4 overlays (theatre, zones, airfields + diagrams, symbols, ruler/bullseye, dim, notes) ·
5 snapshots from the GPU surface (CPU raster of the same draw) and new frame measurements
(presented frames and draw ms, not WPF's Rendering events) · 6 docs, then removal of the old
path after the user's OK.

**Verify.** Every base map and overlay compared side by side with the WPF path at theatre,
city and max zoom; pan/zoom timings against the baseline below; the user drives it.

**Measuring honestly.** The old snapshot tests counted WPF `Rendering` events as frames; they
fire whether or not a frame gets drawn, so "135 fps" hid a stalling map. The tests now pace
steps at 60 Hz and report how long the steps take (a blocked UI thread stretches them; 90
steps = 1.5 s when keeping up) plus, on the GPU surface, frames actually painted and their cost.

**Results (2026-10-21, same views, 90-step pan / 60-step zoom):**

| View | WPF/Mapsui pan | GPU pan | GPU paint avg / max |
|---|---|---|---|
| DCS Map, theatre (400 m/DIP) | 2.0 s (≈45/s) | 1.50 s (60/s) | 3.6 / 12 ms |
| DCS Map, Damascus (3 m/DIP) | 3.2 s (≈28/s) | 1.54 s (59/s) | 1.1 / 9 ms |
| DCS Satellite, theatre | 3.3 s (≈28/s) | 1.51 s (60/s) | 4.9 / 10 ms |
| Scope, theatre | 3.3 s (≈27/s) | 1.50 s (60/s) | 4.4 / 15 ms |

Zoom (60 steps): GPU 1.00 s on DCS Map and DCS Satellite (paint max 14–20 ms, first file
reads), WPF 1.10 s. 60/s is the test's own pace, so the GPU figures are a floor, not a limit.
Skia on the CPU (`DCB_RENDERER=cpu`, the OpenGL fallback): 18 ms per frame, 51 steps/s.
Pictures compared with the WPF renderer: DCS Map (theatre, Damascus), DCS Satellite, Scope,
DCS Charts, DCS Terrain, OpenStreetMap, MGRS grid, ruler/BRAA/bullseye — same content.

Found on the way: Skia's rasteriser can drop a whole path reaching far beyond ±32k pixels
(the Natural Earth coast is one polyline across the Mediterranean): long lines go in as their
visible runs only (`VisibleRuns`); MGRS buckets and taxiways likewise. Mipmaps were dropped
(each layer already picks the level matching the screen; building them on upload cost up to
59 ms in a zoom).

**Zoom smoothness (user: "still a bit chunky zooming").** New snapshot test `wheeltest`
zooms through the real smooth-zoom path (8 wheel notches in, 8 out, 120 ms apart) and logs
gaps between painted frames with their cause (UI-thread ticks late or frames not painted,
collections in between, idle time excluded). Our paints were never the problem (≤ 15 ms);
the causes were:
- **The Objects table** rebuilt its DataGrid on every picture update: 28–48 ms on the UI
  thread about once a second, a hitch in every pan and zoom. Rows now persist and update in
  place (only changed cells redraw), refreshes coalesce at background priority: 0.1 ms.
- **Tiles going blurry** when the zoom crossed a level: missing tiles fell back to a level
  8× coarser. Now the nearest levels already in memory fill in (1–2 coarser zooming in, 1
  finer zooming out; DCS Satellite: the parent tile), and tile decoding has up to 8 workers.
  Frames with tiles missing: 54 → 4 (theatre), 251 → 5 (city).
- **Clipmap headers** were read on the UI thread when a file first came into view (one
  frame took 34 ms): now read on a worker, the file joining a later frame.
- **Town names flickered** while zooming (declutter by screen cell changes every frame):
  cells are now fixed in map metres per doubling of the zoom.

Result: longest gap between frames during wheel zoom 146–156 ms → 27–37 ms, frames over
25 ms 11–18 → 1–5 per test (of ~240), none over 50 ms, on DCS Map, DCS Satellite and Scope.

**Close-up tiles slow to appear (user: "really slow updates on chunks when zoom is
changed").** New snapshot test `settletest`: approach a city, jump to street zoom in an area
with no close-up tiles yet, pan, zoom in and out — each timed until every tile of the wanted
level is on screen; tile timing split into pixels / lines / PNG save, renders and requests
counted per level. Causes and fixes (`DcsMapTiles`):
- **Pixel colouring 94 ms per tile** (lines 2 ms, save 3 ms): WPF `Color` arithmetic and a
  closure per pixel. Now plain integer colours with the same rules: ~21 ms per tile alone,
  ~40 ms with 8 threads busy; 99.7 % of pixels within 3 levels of before.
- **Each close-up tile rendered ~2.4 times**: a tile taken by a render thread was queued again
  by the next frame's request. Tiles being drawn are now tracked (`_rendering`): 96 rendered
  for 96 requested.
- **Road/area data reloaded after 30 s idle** (~0.8 s before the first close-up tile): kept
  5 minutes, and loaded ahead (`Warm`) when the DCS Map zooms within two levels of the
  on-demand levels.
- Up to 8 render threads (was 4), below-normal priority.

| settletest (close-up tiles cleared first) | before | after |
|---|---|---|
| jump to street zoom, Homs | 4.9 s | 0.36 s |
| pan to the next area | 4.3 s | 0.21 s |
| zoom out 8× (new level) | 1.7 s | 0.16 s |

**Slow to recover after switching base maps (user: "stopped updating… loaded now, but took a
while").** Tile loading had no notion of "no longer needed": every tile any base map had asked
for was loaded in turn, and Esri downloads held a decoding thread while waiting for one of two
download slots or a slow server. Now:
- `TileCache`: own worker threads, newest request first; a request no map repeated for 1.5 s
  is dropped (maps re-ask every missing tile on each frame, and a drop triggers a redraw so
  tiles still on screen are asked for again).
- Web tiles: a separate async downloader (two lanes, newest first, requests idle 3 s dropped,
  a failed tile retried after 30 s); decoding threads never wait for the network.
- DCS Map renderer: on-demand requests idle 2 s dropped the same way.
`switchtest`: DCS Satellite → Satellite → DCS Charts → DCS Satellite → Satellite at close zoom,
1.5 s each, then DCS Map in a fresh area: complete in 0.31 s (same as without switching),
96 + 24 tiles rendered for 96 + 24 requested; every base map still fills completely on a still
view.

Open: real mouse input (drag, wheel, smooth zoom) needs the user's hands — the tests drive
the view programmatically; docking a GPU map into another window re-creates its OpenGL
surface (checked in floating windows, not every docking move); the ATC tab's own diagram view
is still WPF.

**Base maps trimmed (user, 2026-10-21).** OpenStreetMap, DCS Terrain and DCS Imagery
removed (layers, the imagery archive reader and its Map data menu items). Remaining: Scope,
Satellite (Esri), DCS Map, DCS Charts, DCS Satellite. Settings saved on a removed map load
on the nearest remaining one (`BaseMapConverter`). The server-sampled terrain grid stays: the
DCS Map and DCS Satellite water use it.

## Phase 5 — SRS and acting on the server: research (2026-10-21)

**SRS** (DCS-SimpleRadioStandalone, GPL-3.0, 2.4.1.0 installed here at
`E:\DCS World\DCS-SimpleRadio-Standalone`: client, server, ExternalAudio). Integration
surfaces, read from the 2.4 sources (protocols only; no SRS code is copied, so DCB's licence
is unaffected):

| Where | Port (default) | What | Use for DCB |
|---|---|---|---|
| SRS client, in | UDP 10710 | `{"controller":{"latitude","longitude","altitude","isFlying","displayName"}}` — the "LotATC" position feed; `displayName` new in 2.4 (the client shows the tool's name) | the controller's radio position = their GCI site/tower: SRS range and line of sight from there |
| SRS client, out → in | UDP 10711 → 10710 | asks `{"lat1","long1","alt1","lat2","long2","alt2","clientId"}`, wants `{"los":{"clientId","see"}}` | answer line of sight from DCB's terrain grid |
| SRS client, in | UDP 9040 | radio commands: `FREQUENCY_SET`, `ACTIVE_RADIO`, `GUARD`, channel up/down, volume, transponder modes/codes/ident, simultaneous transmit (`{"Command":12,"RadioId":1,"Frequency":251.0}`) | tune the controller's SRS radios from the map (click an airfield → tower frequency) |
| SRS server, out | UDP 10712 (`LOTATC_EXPORT_*`) | every 2 s: clients with unit id, type, coalition, name, position, IFF (modes 1/2/3/4, ident, status) | IFF squawks on tracks; who is on SRS |
| SRS server | HTTP (`HTTP_SERVER_*`, `X-API-KEY`) | `GET /clients` (with radios = tuned frequencies), kick/ban by GUID or name | frequencies per aircraft; admin kick |
| SRS server | file | `clients-list.json` every 5 s (`CLIENT_EXPORT_*`) | same data, no network |
| `DCS-SR-ExternalAudio.exe` | to SRS server (local only) | plays text (Windows voices offline, or Google/Azure) or mp3/ogg on frequencies, per coalition, optionally from a position | spoken calls: ATIS, bogey dope, ATC clearances |
| SRS voice protocol | TCP+UDP 5002 | Opus voice, sync messages | a full radio inside DCB — large, later |

Controllers already talk on SRS in External AWACS Mode (`EXTERNAL_AWACS_MODE` + per-coalition
passwords on the SRS server). Who is *transmitting* is in no export (only the voice stream).

**Acting on the server.** Today the DLL drops all client input after `auth`
(`net_server.cpp:304`); the planned path is client → DLL command queue → hook drains it in
`onSimulationFrame` → whitelisted Lua run in the right DCS environment. Available there:
- Mission env: `trigger.action.outTextForCoalition/Group/Unit`, `outSound*`, F10 map drawings
  (`markToCoalition`, `lineToAll`, `circleToAll`, `textToAll`, `removeMark`), smoke, flares,
  illumination, `setUserFlag` (drives mission triggers); AI: `Group:activate`,
  `Controller:setTask/pushTask` (orbit, intercept, RTB, land), `setOption` (ROE, SAM alarm
  state/EMCON), `coalition.addGroup` (spawn), `destroy`.
- Hooks env: `net.send_chat(_to)`, `net.force_player_slot` (move to spectators), `net.kick`,
  `DCS.setPause`, `net.load_mission/load_next_mission`.
- Foothold's `bc` (mission env via `a_do_script`): its own actions; to be read from the script.
Rules if built: no free Lua from clients — named commands with validated arguments only;
roles (coalition controller vs admin password); own coalition only; audit log; rate limits.

**User's choices (2026-10-21):** SRS first, pieces A + B (position/line-of-sight bridge and
radio tuning); server commands later, tier 1 (text, F10 marks) — plus a look at controlling
JTACs, possibly through Foothold's scripts, when that phase starts.

### 5a · SRS bridge (client only, talks to the controller's own SRS client)
- **Position feed** (UDP → 127.0.0.1:10710, every 2 s; SRS drops it after 5 s silence and
  ignores it while the controller sits in a cockpit): `{"controller":{"latitude","longitude",
  "altitude","isFlying":false,"displayName":"DCB"}}`. Where the controller "stands", chosen in
  the SRS panel: off · the ATC tab's airfield (tower) · a fixed point (set from the active
  map's centre). Altitude = terrain height there (SRS adds its own 50 m mast).
- **Line of sight** (listen 127.0.0.1:10711, answer to 10710 `{"los":{"clientId","see"}}`):
  along the path in DCS x/z, terrain from the server's 500 m grid against the straight line
  with 4/3-Earth radio bulge. No terrain yet → no answer (SRS then assumes visible).
  Needs `Theatre.FromLatLon` (Newton on the control grid). Only one tool can own 10711:
  LotATC and DCB cannot both run.
- **Radio tuning** (UDP → 127.0.0.1:9040): `FREQUENCY_SET` (12) and `ACTIVE_RADIO` (1) for
  radios 1–10 in MHz; works on External AWACS Mode radios. The SRS panel: radio number,
  frequency box, and the ATC airfield's tower frequencies (UHF, VHF high/low, HF) from the
  local install's `Mods\terrains\<id>\radio.lua` (`airfield<N>_0` = DCS airbase id), each with
  a Tune button.
- Status in the panel: position sent, line-of-sight questions answered/blocked, last command.
- Tests: line of sight (flat, ridge, Earth curvature), radio.lua parsing, lat/lon round trip.
  Live check against the local SRS 2.4.1 server + client in External AWACS Mode.

**Built (2026-10-21):** `Srs/SrsBridge` (started with the main window), `Panels/SrsPanel`
(replaces the placeholder), `SrsSettings` in settings.json, core `LineOfSight`,
`AirfieldRadios`, `Theatre.FromLatLon` (9 new tests, 78 in all). Checked with a stand-in for the
SRS client speaking its exact formats: position `{"controller":{"latitude":33.85,
"longitude":35.6,"altitude":505,"isFlying":false,"displayName":"DCB"}}` every 2 s; line of sight
across the Lebanon range at 700 m → blocked, at 9 000 m → clear, Homs plain at 560/600 m →
clear; tune → `{"Command":12,"RadioId":2,"Frequency":251}` then `{"Command":1,"RadioId":2}`;
Akrotiri's list UHF 252.000 / VHF high 128.000 / VHF low 40.450 / HF 4.775 from radio.lua.
Not yet checked: the real SRS client taking these (needs SRS server + client in External
AWACS Mode, by the user). Test server: `start-srs-server.cmd` → `server\start-srs-server.ps1`
(config `Saved Games\DCS.server\SRS\server.cfg`: EAM on with random passwords, LOS and
distance on, LotATC export to 127.0.0.1:10712, UPnP off). SRS client facts for the test: its
tool listeners run only while it is connected to a server; EAM radios 1–10 are all
overlay-tunable (1, 2, 4, 5 UHF 1–400 MHz; 6, 7, 9, 10 VHF; 3, 8 VHF FM 1–76 MHz). A tool already holding UDP 10711 (LotATC) leaves line of sight
unanswered; the panel says so.

## Phase 6 — user's next list (2026-10-21): roadmap

| # | Wish | How | Size | Needs |
|---|---|---|---|---|
| 1 | GCI and ATC on two screens | A second top-level DCB window with its own docking layout, sharing the one connection, selection and settings; layouts saved per window ("GCI", "ATC") | medium | client only |
| 2 | Flash who talks on SRS | SRS exports hide the talker (`TransmittingFrequency`, `LastTransmissionReceived` are `[JsonIgnore]`; the transmission log is written after a call ends). DCB joins the SRS server as a listen-only External AWACS client (TCP sync + UDP voice, no audio decoding), tuned to the frequencies to watch; each voice packet names its sender → SRS client → DCS unit → flashing marker | large | SRS protocol work (B) |
| 3 | Who is on which channel; right-click → tune there | The same SRS connection carries every client's radios; per track "on 251.0 / 305.0"; right-click → Tune (bridge, port 9040) | medium | B |
| 4 | BVR envelope, top-down and side view for 2 selected jets | Own loadouts from `Unit:getAmmo()` (server, own coalition); a missile performance table authored by us (range vs altitude/speed/aspect, approximations); top-down WEZ fans + side view panel | large | decision on enemy armament |
| 5 | ATIS / airfield info to a player as a pop-up | First server command: `trigger.action.outTextForUnit/Group` (on-screen text box, not chat); ATIS from mission weather (wind, QNH, temperature, clouds), runway in use from wind, tower frequencies (radio.lua), navaids | medium | command path (A) |
| 6 | Airfields redrawn from DCS itself | The terrain mesh (surface5, already decoded) holds every airfield's real pavement: runways, taxiways, aprons; ED's own charts exist for only 3 Syria fields (kneeboard: Akrotiri, Incirlik, Rafic Hariri) and can be placed with the diagram tool | medium | surface5 pavement classes (research) |
| 7 | Alt+click → coordinates to pass on | Pop-up: lat/lon (DDM, DMS), MGRS, bullseye bearing/range, ground elevation; copy button; later "send as F10 mark / text" | small | send needs A |
| 8 | See missiles | Collector tracks weapons from `S_EVENT_SHOT` while they exist (position, speed, target) | medium | fog-of-war decision |
| 9 | Carrier info, act as LSO | Carrier panel (BRC, speed, wind over deck from `atmosphere.getWind`), LSO view per approaching aircraft: glideslope and lineup against the angled deck (per-carrier geometry), like the planned PAR | large | — |
| 10 | More control over units/events | Tier 2 AI (vector AI flights, SAM emission control/ROE, activate groups), tier 3 admin (separate password), JTAC through Foothold (read its script first) | large | A |

**Foundations.** A = server command path (client → DLL queue → hook → whitelisted Lua, roles,
audit log, own coalition only). B = DCB as a listen-only SRS client (protocol from the 2.4
sources). Proposed order: quick client wins (7, 1) → A with 5 and tier-1 marks → 8 → B with 3
and 2 → 6 → 9 → 4 → 10.

**7 · Alt+click coordinates — built (2026-10-21).** Alt+click on any map: a pop-up at the
cursor with DDM (as DCS shows it), DMS, decimal degrees, MGRS (1 m, WGS84 → UTM, digits
truncated; Norway/Svalbard zones), the bullseye call (magnetic) and the terrain grid's ground
elevation, plus Copy (all lines) and a pin on the map while it is open; Esc, ✕ or a click
elsewhere closes it. Alt+click neither pans nor selects, and its Alt release does not open
the menu bar. Core `Coordinates` with 10 tests (exact at 0°/0°, the NGS Washington Monument
example, bands, truncation, formats). Checked at Bassel Al-Assad: 36S YE 67793 21530,
28 m / 92 ft (DCS: 93 ft). Sending the point to players (F10 mark, text) waits for the
server command path.

**1 · Workspace windows — design (2026-10-21).** The main window becomes one of several
*workspace windows* of the same class: each a full top-level window (own taskbar entry, can be
maximised on any screen) with its own menu, status bar and docking area, all sharing the one
connection, picture, selection and settings. Window → New window → *GCI* (map + Objects +
Zones) or *ATC* (ATC tab + SRS + a DCS Map). The main window keeps the app-wide jobs (picture
expiry, SRS bridge, snapshot aid) and closing it closes the app; closing another window
forgets it. On exit every open window's layout (`layout.xml` for the main one,
`layout-<id>.xml` for the others), screen position, size and maximised state are saved and
restored on start (a position off every screen falls back to the default). Map settings are
kept for maps in any open window.
**Built (2026-10-21).** `MainWindow(workspaceId, preset, placement)`; settings
`MainPlacement` and `Workspaces`; `AppSettings.LayoutPathFor`. Checked by snapshot: an ATC
window opened, saved on exit (`layout-<id>.xml` + placement) and restored on restart with the
same map and view; the GCI and ATC presets laid out as designed. The ATC preset gives its
column a fixed 700 DIP (AvalonDock gives the document area every star share). Maximised
windows are re-maximised on their own screen; not checked on a real second monitor.

**A + 5 · Server command path, ATIS and airfield pop-ups — design (2026-10-21).**
- *Protocol* (additive, PROTOCOL.md): client → server `{"type":"command","id":n,"name":
  "message_unit","unit":<DCS unit id>,"text":"…","seconds":30}`; server → that client
  `{"type":"command_result","id":n,"ok":true|false,"error":"…"}`. New public message
  `weather` (export: yes) every 60 s: mission date/time, visibility, fog, dust, clouds
  (base, thickness, density or preset, precipitation) and per airfield the live wind at 10 m
  (`atmosphere.getWind`), temperature and QNH (local pressure from
  `atmosphere.getTemperatureAndPressure` reduced to sea level — follows the mission's pressure
  systems). Checked with the probe at Bassel Al-Assad: wind from 180° 2.1 m/s, 19.8 °C, QNH
  1016.6 hPa (mission 762.5 mmHg ✓).
- *DLL*: after login, lines with `"type":"command"` are parsed; only whitelisted names
  (`message_unit` for now) with checked fields (unit id > 0, text ≤ 1 500 chars without control
  characters except newlines, 5–120 s) are queued (≤ 100) with the client's coalition and a
  client serial; rate limit per client 5 in a burst, 30 a minute; anything else → an error
  result. `dcb.commands()` hands them to Lua; `dcb.command_result(...)` answers the client.
- *Hook*: drains commands every frame and runs them in the mission env through fixed functions
  (no client text is ever executed: it is passed as a `%q` string): `DCB_messageUnit` checks
  the unit exists, is the controller's coalition and has a player, then
  `trigger.action.outTextForUnit` (an on-screen text box, not chat). Every command is logged
  to dcs.log with coalition, client address, target and outcome.
- *Client*: Core `Weather` + `AtisText` (ATIS: field, information letter, time Z, runway in
  use from the wind, wind magnetic, visibility, clouds, temperature, QNH hPa/inHg, tower
  frequencies; Airfield info: runways with lengths, elevation, frequencies, parking count),
  tested. ATC tab: "ATIS…" and "Airfield info…" open a send box — editable text, a player
  of the own coalition (nearest first), display time, Send, result shown. Map: right-click
  (without dragging) on an own player aircraft → the same, for the ATC tab's airfield.

**Built (2026-10-21).** DLL 0.3.0 (`commands`, `command_result`; `lua_setfield`,
`lua_toboolean` added to the import list), hook (`commandsTick` every frame, `weatherTick`
every 60 s), collector (`DCB_messageUnit`, `DCB_weather`), client (`ConnectionManager.
SendCommandAsync`, Core `WeatherMessage`/`AtisText` with 16 tests, `SendMessageWindow`, ATC tab
buttons, map right-click on an own player aircraft). Checked on the test server: weather for
225 airdromes in 3 ms of sim time once a minute (the first query after start costs ~265 ms
once); a protocol test refused bad fields, unknown commands, control characters, another
coalition's unit and a burst over the rate limit, all logged; the client's own path sent to an
AI aircraft and got "no player in that aircraft"; ATIS for Bassel Al-Assad composed
(INFORMATION ECHO 0402Z, RWY 17, wind 170/4, QNH 1017 / 30.02, tower 250.600/118.100). Found
on the way: `Unit:getID()` returns a string in the mission env. Not yet checked: the text box
on a real player's screen (the test mission flies AI only).

**8 · Missiles — built (2026-10-22).** Collector: a `world.addEventHandler` for
`S_EVENT_SHOT` (registered once per scripting state, replaced if injected again) keeps every
`Weapon.Category.MISSILE` while it exists (≤ 300, ≤ 10 min); each picture reads them once for
both sides (position, heading from velocity, speed, `getTarget()`). A side gets its own
missiles in full (type, shooter, target) and enemy missiles only while aimed at one of its own
units, then without type or shooter (what a warning receiver could tell). Protocol: additive
`weapons` array in `picture`; missile ids are the server's own numbers, not unit ids. Client:
`PictureStore.Missiles` with a 12-point trail; the map draws an arrowhead along the flight path,
an 8 s vector, the trail, a dashed line to the target when it is on the map, and the type (own)
or "MSL" (incoming) with altitude in thousands of feet. Style `missiles` in map-style.json.
Checked on the test server with a staged red Tor and a stream of unarmed blue F-16s: red saw
SA9M330 with shooter and target, blue saw the same missiles as untyped incoming at its jets,
drawn as designed; collect time unchanged (7–9 ms). Positions update once a second like
tracks (no in-between movement yet).

**B + 3 + 2 · DCB as a listen-only SRS client — built (2026-10-22).** From the SRS 2.4.1.0
sources (shallow clone in `_downloads\srs-2.4.1`, protocol read only, no code copied):
- *Sync (TCP, SRS port)*: one JSON line per message, read case-sensitively; `MsgType` numbers
  (0 UPDATE, 1 PING, 2 SYNC, 3 RADIO_UPDATE, 5 CLIENT_DISCONNECT, 6 VERSION_MISMATCH, 7/8 EAM
  password/disconnect); every message carries `Client` and `Version` (≥ 1.9.0.0). The server
  sends **every** client's radios (`SYNC` list, `RADIO_UPDATE` multicasts) to everyone, so
  channels per player need no audio; DCB keeps only its own coalition (fog of war).
  Frequencies are Hz, matched within 500 Hz and by modulation (0 AM, 1 FM, 2 intercom,
  3 off…); 11 radios, 0 = intercom; `iff`/`ambient` must be present or the server's compare fails.
- *Voice (UDP, same port)*: a 22-byte GUID ping registers where to send voice; the server
  forwards a packet to every client with a radio on its frequency and modulation (guard
  receivers by frequency only), filtered by coalition only with `COALITION_AUDIO_SECURITY`;
  no range or line of sight on the server. The packet's fixed tail names the original
  transmitter's GUID → who talks, without decoding Opus.
- *Built*: Core `SrsListen` (messages with exact names, `VoiceSender.Read/Write`, `SrsRoster`:
  own-coalition clients without DCB listeners — radio unit "DCB" — channels by use, talkers held
  0.6 s, watch plan = the 20 most used own frequencies on radios 1–10 plus their guard
  receivers), `SrsListener` (TCP sync, EAM login, PING and UDP ping every 15 s, `RADIO_UPDATE`
  when the watch set changes, EAM disconnect on close), 7 tests. Client `Srs/SrsListen`
  (UI-thread roster, reconnect every 10 s, retune at most once a second; an EAM password of the
  other side than DCB's login shows nothing), SRS tab "Listen" (server, port, EAM password
  DPAPI-encrypted, frequency list with who is on each, talkers in orange with ▶, Tune), map:
  pulsing rings + frequency on a talking player's track, right-click on an own player →
  "Tune my radio N to <their radio>" (via the 5a bridge). Tracks match SRS users by DCS player
  name (SRS `unitId` is the export-side id, not the mission's).
- *Checked* against the local SRS 2.4.1 server with a scripted stand-in pilot (EAM blue, radios
  251.000/243 guard and 124.500, voice-shaped packets on 251.000 in 3 s bursts): two DCB
  snapshot clients logged in, listed the three frequencies with the pilot only, and showed
  him talking on 251.000. Not yet checked: the map rings and the right-click Tune with a real
  player (the test mission has none), a real SRS client talking.

**6 · Airfields from DCS itself — built (2026-10-22).** User's choices: the DCS pavement only
in the ATC tab's diagram (maps keep runways only); diagrams of our own from DCS data in the
style of the CombatWombat charts; taxiway letters from DCS where possible — and (user's idea)
from the taxiway signs DCS places at the fields. Research (DCS_MAP_FORMAT.md): no taxiway
names in any readable DCS file (`AirfieldsTaxiways/*.rn4` carry width classes only,
`terrain.cfg.lua` is encrypted); the pavement is the surface5 class `Runway`; the signs are
scene objects whose model names are their letters.
- *Diagram* (`AirfieldDiagramView`): white sheet, DCS pavement in warm grey (one frozen
  geometry per field), API runways black with a white edge, designators beyond each end,
  "length X width" in feet, stand badges ("13-25": runs of consecutive DCS parking numbers
  standing within 120 m; single numbers from 0.6 DIP/m), title block (AIRPORT DIAGRAM, field,
  tower frequencies from radio.lua, field elevation from the weather message, where the
  letters come from), north arrow with DCS magnetic variation, scale bar, live traffic. No DCS
  install → the old taxiway lines.
- *Letters*: DCS location signs within 4.5 km and nearer this field than any other airdrome,
  letter+digit panels joined, facing pairs merged, those at a runway's hold line moved 90 m
  beyond the runway edge, then snapped onto the taxiway; chart letters for taxiways DCS has no
  signs on are kept in grey ovals; fields without signs use the chart letters as before; area
  names stay from the charts (local only). Compared with the charts: the same letters where
  both exist at most fields (Hatay, Gazipasa, Gecitkale, Beirut 10 of 13), differences where the
  sim's layout changed (Akrotiri E on DCS, D/H on the chart); 17 fields have DCS signs and no
  chart (Ben Gurion, Hatzor, H3, Konya…).
- *Checked* by snapshot: Incirlik (A–E from DCS, F/G/H from the chart), Hama (chart only, runway
  surface not in the mesh), Ben Gurion (signs only), Damascus (two staggered runways; fit
  computed right — the snapshot windows were being scrolled meanwhile). 119 tests.
  Not changed: the chart pictures on general maps (their toggle stays).

**6b · Diagram as a document, approach view (2026-10-22).** User's review: one letter per
taxiway leg (not per sign), stands grouped far more, Ercan incomplete, add beacons, arrows,
tower, imperial info, the ICAO codes the C-130J uses, a tidy document fit for kneeboards later;
plus a 10 NM approach view with inbound/outbound symbology, 3° altitudes at 2/5/10 NM, ILS and
TACAN. Choices: two views (Diagram, Approach); export later, as the last step, saved into the
project; ICAO from the C-130J nav DB then charts; arrows = runway heading arrows, ILS feathers,
approach/departure arrows.
- *Data*: `Core/Beacons` (beacons.lua: type, callsign, frequency, TACAN channel, position,
  direction — a localizer's beam points away from the runway, inbound = direction + 180;
  `beaconId 'airfield<id>_n'` ties it to the field); `Core/IcaoMatch` + `NavDatabase` (C-130J
  `nav_data.db`, SQLite via Microsoft.Data.Sqlite, read-only: nearest airport within 6 km with
  aligned runways — Damascus OSDI, Akrotiri LCRA, Ercan LCEN, Incirlik LTAG); towers = scene
  tower models (`kdp`, `atc_control_tower`…) else the chart's "TOWER" label; `Core/TaxiwayGraph`
  (airfield rn4 edges carry kind 0, type `taxiway_<w>m`/`runway_65m` and node ids).
- *Legs*: edges cut at bends > 35° (over ~30 m), chained through joints < 35° and straight
  through junctions < 20°; each sign board votes for its nearest leg (50 m); one label per leg
  at its middle, repeated every ~1.5 km. Damascus 81 sign boards → 24 labels (parallels K
  4.6 km and O 3.7 km one leg each).
- *Stands*: chained within 150 m → one badge with number ranges ("10-29, 38, 46").
- *Gap fill*: the network drawn at its widths under the mesh pavement. Ercan's older pavement is
  only ground texture in DCS (the mesh holds its outlines), so it stays outlines.
- *Page*: header (field + ICAO, DDM position, elevation ft, variation), plan frame, info block
  (tower frequencies, navaids, runways: magnetic headings, ft dimensions, threshold elevations,
  ILS ident/frequency/course), footer (NOT FOR REAL NAVIGATION, date). Plan: heading arrows at
  thresholds, ILS feathers, tower star, TACAN/VOR/NDB/LOC/GS symbols, ft scale bar. Parallel
  runways DCS names alike get L/R; designators are oriented by course (DCS names Damascus's
  second runway "23" with a 050° course).
- *Approach view* (`ApproachView`, dark scope): runway end from the wind (as the ATIS; header
  click picks another), rings 2/5/10 NM from the threshold, dashed final with inbound arrow and
  2/5/10 NM ticks with 3° altitudes (threshold elevation + 318 ft/NM), departure arrow, field
  navaids, traffic with one-minute lead line, altitude, groundspeed, range and ± ft against the
  path, own inbound sequence by range, profile strip (0–10 NM, ft above threshold).
- *Checked* by snapshot: Damascus (05L/23R, 05R/23L, ILS on the right runways, 2640/3600/5190 ft),
  Akrotiri (approach view with two hostile Su-24Ms at 7 NM), Ercan, Incirlik. 133 tests.
  Not yet: export (deferred by choice), Ercan's texture-only pavement.
- *Fixes (2026-10-22, user: "app crashes when I move the ATC widget", "elements overlapping"):*
  a docking pane passes through tiny sizes while dragged and the page layout built a Rect with
  a negative size (WPF throws → unhandled → app closed). Layout rectangles are clamped, below
  ~420×300 a note replaces the page, and a render fault in either view is logged to
  render-errors.log instead of ending the app. Checked with a snapshot that shrinks the floating
  ATC window to 60×40…300×600 with both tabs: exit 0, no crash events. Labels are placed in
  order (corners, runway texts, navaids, letters by leg length, stands, areas) and never over
  one another: a label takes a free spot nearby or gives way until zoomed in; stand badges that
  would touch merge ("4-10, 41-45, 51").

**9 · Carrier panel and LSO — built (2026-10-22).** User's choices: a fast feed near the ship
(10 a second), the carrier tools inside the ATC tab (carriers listed with the airfields, their
tabs Carrier and LSO), extras = pass log with grades and the marshal stack (no LSO calls to the
pilot this round).
- *Server*: DLL 0.4.0 `publish_side` (a per-side slot beside the picture). Collector
  `DCB_carrier`: carrier types, beacons from the mission's group tasks (TACAN, ICLS, Link 4,
  ACLS, radio), the side's aircraft within 10 NM of its carriers (refreshed with each picture),
  `S_EVENT_LAND` on a ship kept a minute; picture fields `carriers` and `events`; hook LSO tick
  (`DCB_lso`, 10 Hz, returns at once with no aircraft near). Protocol: `lso` message, both
  coalition-private, not exported (PROTOCOL.md).
- *Core* (`Carrier.cs`): `CarrierGeometry` per class (Nimitz measured from an AI F/A-18C trap on
  CVN-74: final −9.3° to the ship, touchdown 96 m aft of the origin, 1 m starboard, deck 22.0 m;
  Forrestal, Kuznetsov, Tarawa, Invincible estimated; only angled decks get the LSO tools);
  `Groove` (astern, lineup, height, ± glideslope, closure, AoA estimated as pitch less the flight
  path through the air); `PassTracker` (a pass starts 0.5–1 NM astern in the groove, within
  ~200 ft of the glideslope and with the gear down when the model reports it, samples at X/IM/IC/AR, ends as TRAP from DCS's event or at rest on
  the deck, BOLTER when it crossed the deck low, else WAVE OFF; Navy-style grade OK/(OK)/---/C
  with H/LO, LUL/LUR comments, F/SLO noted from the AoA estimate but never graded; the grade can
  be changed by click); `Marshal` (Case I/II/III from ceiling and visibility; Case I angels 2, 3…;
  Case II/III on the marshal radial at angels 6+, DME angels + 15, push times a minute apart).
- *Client*: Carrier tab (`CarrierView`): header BRC, speed, final bearing, wind over the deck
  (axial/cross), case; bow-up plan (user, 2026-10-22: wheel zoom 1–20 NM, opens at 10 NM) with
  range rings, the final to 10 NM ticked every NM (¼ NM when zoomed in), the Case I holding
  circle and pattern (initial, break, downwind, 180, 90, X; a schematic relative to the ship,
  dimmed in Case II/III), the hull at least 26 px long, own traffic and "N on deck"; beacons,
  deck info, marshal stack. LSO tab (`LsoView`): PLAT crosshair, readout (range, height, high/low,
  lineup, AoA on/fast/slow, gear, seconds to the wires), side and top views of the groove with
  trail, pass log. Snapshot modes `carrier:<name>` and `lso:<name>`.
- *Checked* on the test server with AI F/A-18Cs spawned 12 NM astern of CVN-74 (Foothold SY):
  Carrier tab (BRC 062°, FB 053°, WOD 24 kt, Case I, beacons, marshal stack); LSO tab live at
  0.5 NM (high 44 ft, left 16 ft, on speed, gear down) and the pass logged TRAP, OK. Fixes after
  the review: one pass through a brief excursion, a landing event decides only the latest pass,
  wind-corrected AoA (view and tracker alike), aircraft parked after a trap no longer watched, a
  wave-off still inside the groove box is decided, and the AI's overhead arrival (725 ft, gear
  up, 350 kt, 370 ft above the slope at 1 NM, replayed from a recorded feed) is no longer logged
  as a wave-off. 144 tests. Not yet: a human pilot (AI only), a real bolter or
  wave-off in the sim (unit tests only), hook position (no common draw argument).

**Runway queue and panning — built (2026-10-22).** User: drag the ATC scopes (max 10 NM off
centre) and a takeoff/landing queue table as towers use, with priority. Research: towers order
flight strips in a runway bay (strip order = sequence, moved by hand; no standard "priority"
control), departures and arrivals in different colours (no universal scheme; NATS: departures
blue, arrivals orange), departures fill gaps between arrivals (one VATSIM SOP: 6 NM spacing when
departures wait, 3 NM otherwise). User's choices: Carrier and Approach views both pan; queue in
this client only; no messages to pilots; one queue per airfield and per carrier; no lines on the
scopes, only the place number beside each contact.
- *Core* (`RunwayQueue.cs`): `QueueObs` (range, height above the base, speed relative to it —
  a carrier moves —, closing), `RunwayQueue` (add/remove/move/priority, strips drop out once an
  arrival has landed and slowed below 15 m/s within 3 NM, a departure is airborne beyond 3 NM, or
  the unit is gone; state INBOUND/FINAL/LANDED, TAXI/HOLDING/ROLLING/AIRBORNE; suggestions: own
  aircraft closing within 15 NM, or moving on the ground within 3 NM; departure gap hint at 6 NM),
  `RunwayQueues` (keys `af:<id>`/`cv:<id>`, a unit in one queue at a time). 7 tests, 151 in all.
- *Client*: `RunwayQueuePanel` beside the ATC views (splitter): strips with place, ARR/DEP,
  callsign, type, state, magnetic bearing/range from the base, altitude, speed, time to the base;
  ▲ ▼ ⏫ priority ⇄ ✕; Suggested with + buttons. `QueueBase` measures own aircraft against an
  airfield (runways' middle, weather elevation) or a carrier (deck height, ship velocity).
  Approach view: its drawn inbound list is replaced by the queue (scope takes the full width),
  traffic to 22 NM, "#n" beside queued contacts, drag pan / double-click centre. Carrier plan: the
  same. Snapshot modes take `+q` (queue every suggestion for a minute).
- *Checked* by snapshot on the test server: CVN-74 with two AI Hornets and the S-3B (three ARR
  strips, states and times following the recovery); Akrotiri with two AI Hornets (#1 FINAL 4.4 NM,
  #2 INBOUND 7.2 NM). Not yet: the drag with a real mouse, departures (no taxiing own aircraft
  during the test), a human controller using the buttons.

**Filters, approach zoom, packages — built (2026-10-22).** User: no ground units or landed
aircraft in ATC mode, no landed aircraft in GCI mode, but landed aircraft stay on the airfield
diagram (ground traffic); a player filter in Objects; wheel zoom on the approach view; packages.
User's choices: landed = anything on the ground or a deck; ATC mode = maps in an ATC window +
Approach + Carrier (Diagram keeps them); at most 4 packages, added by right-click on a jet (map
or Objects), shown in their own colour with the package number by the icon; each package on one
of the controller's SRS radios 5–8.
- *On the ground*: `Track.OnGround` once per picture — an aircraft within 15 m (helicopter 5 m)
  of the terrain grid under it, or below 40 m within 250 m of a ship. Unknown terrain → airborne.
- *Maps*: per-map "Ground units" and "Landed" boxes (`MapSettings.ShowGroundUnits/ShowLanded`,
  null = by window: an ATC window hides both, others hide landed only). Hidden tracks are not
  hit-tested; hidden SAMs draw no threat rings.
- *Approach view*: wheel zoom 2–20 NM (10 at first), rings 15/20 NM when zoomed out, traffic to
  32 NM, landed aircraft left out. Carrier plan: landed aircraft only counted ("N on deck").
- *Objects*: "Players only" box, a "Pkg" badge column, right-click → package menu.
- *Packages* (`Core/Packages.cs`, `PackagesPanel`, `PackageMenu`): P1 magenta, P2 cyan, P3 lime,
  P4 amber (`Map/PackageColors`); name, MHz and radio saved in settings, members for the
  session (dropped when gone); an aircraft in one package; own aircraft only. JUMP: SRS
  `FREQUENCY_SET` (12) on the package's radio, then `ACTIVE_RADIO` (1). Panel in every default
  layout (Window → Packages for saved ones). Snapshot mode `packages`.
- *Checked* by snapshot: main map with Darkstar/Shell in P1 (magenta, "P1"), the carrier Hornets
  on deck hidden, Objects badges, the Packages panel docked; an ATC window's map without own
  ground units. 155 tests. Not yet: JUMP against a running SRS client, the Approach zoom and the
  right-click menus by hand. Found: a floating anchorable's snapshot comes out blank (the
  content stays parented to the main window) — check panels docked.

**SRS rework, channel plan, detached starts (2026-10-22).** User: no separate SRS login (same
server address, own port) in the connect dialog; SRS starts with the DCB connection; the SRS
tab reduced to position, the controller's 10 channels (frequency, transmit, people on it and
who) and "Listen traffic" for a player; packages on channels 7–10 with JUMP = set + transmit;
channel 5 for ATC, set from the ATC tab. Choices: the SRS app stays the radio (DCB drives it);
Listen traffic only shows the player's radios (a "Put on CHn" per radio); counts from DCB's
listen connection; start DCS, SRS and DCB outside Claude's Windows job.
- *Why detached*: at 15:48 a Claude desktop update restarted the app and Windows ended its job —
  the DCS server and the client started from the session's shell with it (log just stops, no
  crash events; the shell is in a job, explorer is not). `start-server.ps1`,
  `start-srs-server.ps1` and `build.ps1`'s relaunch now use `Win32_Process.Create`.
- *Connect dialog*: SRS port and EAM password (Remember keeps both, else the EAM password lasts
  the session). `SrsListen` follows `ConnectionManager.StatusChanged` on the DCB server's address,
  and starts the SRS app (`SrsApp`, path from HKCU\Software\DCS-SR-Standalone) when it is not
  running. SRS has no command line to connect, so its Connect is clicked once in its window.
- *Channels*: `Core/ChannelPlan` (5 ATC, 7–10 packages P1–P4, 1–4 and 6 free); `SrsChannels`
  (1–6 kept in settings, 7–10 the packages' frequencies; Set = `FREQUENCY_SET`, TX = also
  `ACTIVE_RADIO`); `SrsRoster.ClientsOn(hz)` for the people on a channel (guard included). Shown
  as DCB set them: changes made inside the SRS app are not read back.
- *SRS tab*: status (listen, SRS app with Start, last command), position (ATC airfield / fixed
  point), 10 channel rows (role badge, MHz, Set, TX, "N on it ▾" with names, talkers ▶), Listen
  traffic (the player's radios with people and "Put on CHn" for a free channel). Right-click
  "Listen traffic" on players (map, Objects) replaces the old "Tune my radio N" items.
- *ATC tab*: a frequency list (airfield tower frequencies, carrier radio) and "Set CH5".
- *Checked* against the local SRS server with the stand-in pilot (Viper 1-1, 251.000/243 guard,
  124.500, talking): channel rows and counts, the talker highlighted, Viper's radios listed. 157
  tests. *Mistake found*: that snapshot sent its channel commands to the user's real SRS app
  (radios 1, 2, 5, 7 retuned, 1 selected) — `SrsBridge.Tune` now sends nothing in snapshot runs.
  Not yet: Listen traffic on a real player (the test mission has none; a snapshot aid points it
  at an SRS name), the SRS app being started on connect, Set CH5 by hand.

**Live channels, ATC on 6 (2026-10-22).** User: channels taken from SRS instead of blank; the
ATC channel moves to 6. The SRS app (2.4.1) sends its whole state as JSON to 127.0.0.1:7082
("flight panels") every 200 ms while connected, External AWACS Mode included
(`DCSRadioSyncHandler.SendRadioUpdateToDCSAsync`): radios 0–10 (0 intercom; freq Hz, modulation,
name, secFreq), `selected`, `RadioSendingState` (IsSending, SendingOn), `TunedClients`. Core
`SrsAppState.Parse` (tested on a captured packet), client `SrsAppListener` (shared bind on 7082,
UI-thread change events, stale after 2 s), `SrsChannels` reads live first (frequency, selected,
transmitting now, the app's own counts as fallback), a package without a frequency takes its
channel's. SRS tab: a free channel shows the SRS radio's name, TX orange = selected, red
"TX ●" = transmitting now. `ChannelPlan.Atc` = 6, free 1–5; ATC tab button "Set CH6". Checked:
158 tests; the user's client holds 7082; the user confirmed the SRS tab shows the real SRS
channels (a second listener on 7082 gets no packets while the user's client runs, so snapshots
can't show this). A flight-panel tool on 7082 would compete for packets.

**Performance, ATC view panels, layout, theme (2026-10-22, evening).** User's six-part request;
four built, two answered as research write-ups.
- *Performance, measured.* Collector: unit lists cached (full rescan every 5 s or after
  `S_EVENT_BIRTH`), ground units' JSON rebuilt every 5 s, contacts only from ground/ship groups
  that have a radar, heading from velocity when moving (no `getPosition`), a static JSON prefix
  per unit, the carrier feed reuses the air list, caches pruned at rescan. Probe: 10 ms and
  0.7–1.1 MB garbage per collect → ~1 ms / 51 KB for the same 175 entries. Live (per minute):
  avg 6.6–13.4 → 1.7–5.2 ms; max 11–60 (160 seen) → 5–17 ms in 10 of 12 minutes, two minutes at
  58 and 72 ms — probably Lua GC of the ~200 MB heap shared with Foothold (one full GC = 34 ms);
  left alone, since GC parameters would change the mission's behaviour. Client: "UI: picture"
  avg 0.6 / max 2.9 ms, "UI: lso frame" 0.01 ms, panels 0.1–0.3 ms — under target, unchanged.
- *ATC views as panels* (`Panels/AtcViewPanel.cs`: Diagram, Approach, Carrier, LSO; Window →
  ATC views; a new one floats at 900×700). They follow the ATC pick (`AppState.SelectAirfield`,
  new `AppState.SelectCarrier`), each keeping its last pick while the other kind is chosen.
- *Window → Save layout* (`SaveLayouts()`, also on exit; the main placement from the main
  window). Reset layout was already there.
- *Theme "B · Tactical console", dark and light* (user's pick): `Themes/Colors.Dark.xaml`,
  `Colors.Light.xaml` (same `Dcb.*` keys), `Themes/Tactical.xaml` (flat templates for buttons,
  toolbar chips, check/radio boxes, text boxes, combo boxes, menus, tooltips, tabs, group boxes,
  data grids, scroll bars, toolbars, status bar, splitters), `Themes/ThemeManager.cs` (Window →
  Theme: System/Dark/Light, `AppSettings.Theme`, follows Windows' app mode live, dark title bars
  through DWM, AvalonDock VS2013 Dark/Light), `Themes/ThemeBrush.cs`. Panels that built colours
  in code use theme brushes and redraw on `ThemeManager.Changed`. Checked by snapshot in both
  modes. On purpose unchanged: the scopes (Approach, Carrier, LSO) stay dark, the airport
  diagram stays white paper. Not styled: Slider, the ScrollViewer corner. Not yet seen by the user.
- *Client close "did not exit in 15 s"*: not reproducible (connected, with SRS, with the 7082
  listener: ~0.2 s). Most likely a modal dialog or open menu (Windows then does not deliver
  WM_CLOSE); `build.ps1` force-closes after 15 s and settings are then not saved.
- *Research*: [RESEARCH-WEBSITE.md](RESEARCH-WEBSITE.md) (React website: export through a hook
  writer + Node sidecar pushing over HTTPS — recommended; MapLibre GL JS 6.15 via react-map-gl
  8.1, Protomaps PMTiles base map, no tile.openstreetmap.org; never UCIDs; open questions on
  hosting, access, history, stats) and
  [RESEARCH-EXTERNAL-CONTROL.md](RESEARCH-EXTERNAL-CONTROL.md) (DCS and Foothold 5.3.5 functions —
  all 34 checked in the mission's scripts —, a capability matrix by tier, the safety model,
  a Foothold adapter with feature detection, UI concept, open questions on who gets tiers 2/3).
- *Snapshot modes added*: `perf` (UI timings in status.txt), `views:<airfield>`,
  `srstab[:<SRS user>]` (`DCB_SNAPSHOT_EAM`), `packages`; `DCB_SNAPSHOT_SRSAPP=1` lets a snapshot
  read 7082 (it then shares packets with the user's client). Snapshots never send SRS commands.

**Release package, analytics, website export (2026-10-22, night).** User: deploy as a simple
copy-paste with simple settings; gather mission and player scores/achievements for later
analysis and the website; see whether that and Foothold's active missions can be shown on a
website in real time. Choices: players as hashed UCID + name; core flying + Foothold campaign;
JSON-lines files; keep everything.
- *Release* (renamed `EaglesNest-*` 2026-10-22; since then the hook makes `Config\DCB.lua` from
  the example with random passwords on the first start, and the server zip carries the 500 m
  terrain grids sampled on the test server — Syria): `server\package.ps1` → `release\DCB-Server-<v>.zip` (`copy-into-saved-games\`
  Mods/Scripts/Config + INSTALL.txt) and `DCB-Client-<v>.zip` (exe + CLIENT.txt). Settings ship
  as `DCB.example.lua` with empty passwords (logins closed until set), never overwriting a
  server's `DCB.lua`; no `dev_probe`. The DLL links the CRT statically (needs only DCS's
  lua.dll). Checked by installing the zip as INSTALL.txt says on the test instance: hook loaded,
  listening, login ok, wrong password refused. Plain TCP noted (VPN or address-limited firewall).
- *Analytics* (docs/ANALYTICS.md): DLL 0.5.0 `random_hex`, `hmac_hex` (Windows CNG); hook key
  `DCB\analytics.key`; player id = HMAC-SHA256(key, UCID)[0..16] — same id from .NET checked;
  collector event handler (players' spawn/takeoff/landing/crash/eject/death/leave, kills with
  target category and friendly flag); hook sessions (connect/disconnect/slot) and the per-session
  `DCB\analytics\*.jsonl`; Foothold exporter adds zone lat/lon, missions (active/available via
  `mc:decodeMessage`), credits, capture and mission start/end events, and every 5 minutes
  Foothold's player scores (`bc.playerStatsByUcid`, re-keyed to ids in the hook).
- *Export*: `DCB\export\state.json` (checked every 10 s, written on change) and `events.jsonl`
  (public kinds, names only). Cost measured: docs/ANALYTICS.md.
- *Real-time demo*: `tools/web-demo` (Node built-ins, SSE): front line from lat/lon, credits,
  missions per side, top players, live events. Checked live: 72 zones, credits, a real
  "Mission started: Attack Paphos (2)" and a synthetic player kill (hook dev probe, `env: hook`)
  pushed to the page within a second.

**User's choices (2026-10-21):** the proposed order; missiles = own coalition's always plus
enemy missiles fired at own units (others hidden); BVR = own jets' envelopes only, nothing
assumed for the enemy; two screens = a second DCB window sharing the connection.

## RWR triangulation and F10 marks (2026-10-22, DLL 0.6)

**User's choices:** enemy ground tracks only when the mission is modern enough — a server
setting (`enemy_ground = "auto"|true|false`, `enemy_ground_year = 2012`); otherwise SAMs are
found by triangulation: the pilot calls "spiked, 3 o'clock", the controller enters it on a
radial clock (all 12 positions, 12 on the nose; RWR key `R` or the aircraft's right-click
menu), orders an orbit for more bearings, and places a threat (type → ring) where they cross.
Threat ring on controllers' screens only; the server gets a plain F10 text mark with the type.
Coordinates are read out by the controller on SRS (bullseye / DDM / MGRS with Copy).

- *Server*: radar contacts skip enemy ground units unless allowed (collector, `DCB_settings`
  injected by the hook). Commands `mark_add` / `mark_remove` (DLL checks fields; collector
  `DCB_markAdd/Remove`, coalition marks from id 880000 that players can delete, removal of
  DCB's own marks only). `marks` feed per side every 5 s when changed (`world.getMarkPanels`).
- *User's corrections after testing (2026-10-22)*: marks must not be read-only (players delete
  them); the mark is the live threat (deleted in the game → gone from DCB); marks read the other
  way too: any coalition mark starting with a threat type is a threat with its ring, also ones
  found on connecting; "Add F10 mark here…" for a plain mark with the controller's text.
- *Client*: `GciBoard` (Core: lines, per-aircraft fix by weighted least squares with a 10° RWR
  σ and its 1σ ellipse, threats, marks), `GciSk` layer (lines with ±σ wedge, fix with ellipse
  and bullseye call, threat rings, F10 mark pins — Foothold's zone labels hidden), map
  right-click menus (place threat here / at fix, send to / remove from F10, coordinates),
  "F10 marks" toolbar toggle. Snapshot mode `… gci` / `gci+send`.
- *Checked live*: mark_add → feed → mark_remove, refusals (foreign mark, bad threat name); a
  fix from three made-up bearings with 2–4° error landed 1.4–1.7 NM from the emitter; the clock
  around an E-3 with 12 on its nose. Not checked live: the enemy-ground switch (this mission's
  radars reported no ground units at all at the time), a real pilot's spike.

## Carrier lights and recovery-case guidance (2026-10-22, DLL 0.7)

Research and live tests: [RESEARCH-LIGHTS.md](RESEARCH-LIGHTS.md). The test server runs a
night copy of Foothold (`_downloads\foothold\Foothold_SY_extended_5.3.5_NIGHT_2130.miz`, start
21:30; previous `serverSettings.lua` kept as `.before-night`).
- *Carrier lights*: command `carrier_lights` {unit, mode −2…2} (DLL whitelist, own coalition's
  carrier checked in the server env, `a_set_carrier_illumination_mode` in the mission env,
  logged with the mode). Verified in MP by the user: all five modes on CVN-72, recovery on
  Stennis. Carrier view: Lights bar OFF / AUTO / NAV / LAUNCH / RECOVERY with the last mode set
  (DCS does not report it); a click while one is still on its way is sent right after (quick
  clicks used to be lost while the buttons were disabled). Airfields: Akrotiri is lit at night
  by itself and the lights do not follow ATC (radio-silent test); no control built.
- *Recovery case*: `Marshal.CaseFor(wx, sunElevation)` — Case III whenever the sun is below the
  horizon at the ship (as DCS's Supercarrier: night → Case III; day: ceiling < 1000 ft or
  visibility < 5 NM). `Core/Sun.cs` (solar elevation ±0.5°, mission UTC from the weather date,
  mission time and the theatre offset). 177 tests.
- *Guidance lines* (user: toggles, the current case on by default): chips Case I / II / III +
  "auto" on the Carrier view's bar (`CarrierCases`, overrides in `AppSettings.CarrierCases`).
  Case I in blue (as before), Case II in amber (depart marshal, descend as Case III to 10 NM at
  1200 ft, visual to 800 ft and the Case I break), Case III in violet (left-hand marshal
  racetrack on the final's reciprocal, A6 at 21 DME, +1 NM / +1000 ft per slot, push 250 kt at
  4000 ft/min, platform 5000 ft → 2000 ft/min, level 1200 ft by 12 NM, dirty up 10 NM, 6 NM,
  glideslope ~3 NM, ball at ¾ NM). Carrier plan zoom to 40 NM, pan to 30 NM. Checked by
  snapshot (auto at night → Case III only; all three).
- *ACLS (research only, user: no link switching)*: ACLS needs the carrier's ACLS and Link 4
  active — Mission Editor carrier commands `ActivateACLS` / `ActivateLink4` (+ Deactivate,
  ICLS) via `Controller:setCommand`, accepted from a script on CVN-72 (re-sent with the
  mission's own values). The case itself is not switchable (DCS picks it). Found no sign that
  DCS datalinks Case III marshal data to the Hornet; scripts cannot write to an aircraft's
  datalink. The Carrier view shows the mission's ACLS/Link 4 state read-only.

## Test environment

Local dedicated-style server: `DCS.exe --server --norender -w DCS.server`
(Saved Games\DCS.server). Scripts in `server\`. Test mission at `missions\DCB-Test.miz`.
