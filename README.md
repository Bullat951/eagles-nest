# Eagle's Nest

ATC/GCI external application for DCS World (working name; in the code, files and protocol it
is still "DCB" — `dcb.dll`, `DCB-hook.lua`, `Config\DCB.lua`). See [docs/PLAN.md](docs/PLAN.md).

## Layout
- `docs/` — plan and design notes
- `dll/` — C++ server DLL (`dcb.dll`, Lua 5.1 module + TCP server); `build.ps1`
- `lua/` — `DCB-hook.lua` (GameGUI hook) and `collector.lua` (mission env)
- `server/` — local test server: `setup-server.ps1`, `start-server.ps1` (`-Wait`), `deploy.ps1`, config templates
- `client/` — C# .NET 10: `Dcb.Client.Core` (protocol), `Dcb.Probe` (console), `Dcb.Client` (WPF)
- `missions/` — notes on the test mission

## Build
`.\build.ps1` builds everything into one place (one build type, no Debug/Release split) and
runs the tests:
- `Dcb.Client.exe` (project root) — the client, one file (needs the .NET 10 Desktop Runtime)
- `app\Dcb.Probe.exe` — console probe
- `app\tools\DiagramIndexer\DiagramIndexer.exe` — places airport-diagram pictures, reads their names
- `dll\build\Release\dcb.dll` — server DLL (installed by `server\deploy.ps1`)

A running client is closed (it saves its layout), rebuilt and started again automatically.

## Run locally
1. `server\setup-server.ps1` once (creates the `DCS.server` instance).
2. `server\deploy.ps1` — builds the DLL, installs hook/DLL/collector, (re)starts the server.
   Controller passwords live in `Saved Games\DCS.server\Config\DCB.lua`.
3. Later sessions: `start-test-server.cmd` (project root) starts the server if it is not
   running and waits until DCB answers on 127.0.0.1:10309.
4. Run `Dcb.Client.exe` (Connection → Connect), or `app\Dcb.Probe.exe 127.0.0.1 <password>`.

Map controls: left-drag pans, wheel zooms, click selects a track. **Right-drag** measures:
bearing (magnetic) and range in NM, a BRAA (altitude, aspect) when it ends on an aircraft.
**Hold B** for the bullseye call of the mouse position (`BullseyeKey` in settings.json).

Client layout and settings live in `%AppData%\DCB` (override with `DCB_HOME`). Developer aid,
off unless asked for — snapshot mode for checking the UI without a person:
`Dcb.Client.exe --snapshot <out dir> 127.0.0.1 10309 <password> [seconds] [basemap|restore|atc:<airfield>|approach:<airfield>[+q]|carrier:<name>[@<NM>][+q]|lso:<name>|views:<airfield>|packages|srstab[:<SRS user>]|perf] [lat,lon,res[,coast|grid|dimN|zone=<name>]] [zoomtest|pantest[:<layer,layer>]|measure]`
(`pantest` pans at a fixed zoom and reports rendered fps; the layer names it hides are the map's
x:Names, e.g. `FootholdOverlay`; `perf` writes UI timings to status.txt). Use
`DCB_HOME=<empty folder>` for a clean layout. `DCB_SNAPSHOT_EAM` gives `srstab` an EAM password;
`DCB_SNAPSHOT_SRSAPP=1` lets a snapshot read the SRS app's port 7082 (shared with a running
client). Snapshots never send commands to the SRS app.

Window → Theme: System (follows Windows' app mode), Dark or Light — the "tactical console" look;
the scopes stay dark and the airport diagram stays white paper. Window → ATC views opens the
Diagram, Approach, Carrier or LSO view as a panel of its own (e.g. on a second screen); they
follow the ATC tab's pick. Window → Save layout keeps the windows and panels as they are now.

Release: `server\package.ps1` builds `release\EaglesNest-Server-<version>.zip` (copy into the
server's Saved Games folder, nothing to run; the first start makes `Config\DCB.lua` with random
controller passwords; Syria's terrain grid included; INSTALL.txt inside) and
`release\EaglesNest-Client-<version>.zip`.
Analytics and website export: [docs/ANALYTICS.md](docs/ANALYTICS.md); live demo page
`node tools\web-demo\server.js` (http://127.0.0.1:8099).

Research write-ups: [docs/RESEARCH-WEBSITE.md](docs/RESEARCH-WEBSITE.md) (public website) and
[docs/RESEARCH-EXTERNAL-CONTROL.md](docs/RESEARCH-EXTERNAL-CONTROL.md) (controlling units and
Foothold from DCB).

Airport diagrams: `app\tools\DiagramIndexer\DiagramIndexer.exe index <folder of page PNGs> 127.0.0.1 10309 <password>`
writes `diagrams.json` into the folder (page placement plus the charts' taxiway letters, area
names and tower frequency); set the folder in the client under Window → Map data. The ATC tab
draws its own airport diagram as a document page from DCS data — pavement from the terrain
mesh, runways with heading arrows and ILS feathers, one taxiway letter per leg from the signs
DCS places (chart letters where DCS has none, in grey), stand groups, tower and navaids
(beacons.lua), and an information block in feet; ICAO codes from the C-130J's nav database
when installed — and adds the charts' area names. Its Approach tab is a live 10 NM view: final
approach with 3° altitudes at 2/5/10 NM, departure arrow, ILS/TACAN, traffic against the
glidepath, the inbound sequence and a profile strip. `DiagramIndexer.exe probe <page.png>`
prints what is read from one page.

Window → New window → GCI or ATC opens another full DCB window (e.g. for a second screen),
sharing the connection and selection; open windows, their layouts and positions come back on
the next start. Closing the main window closes DCB.

ATIS and airfield information to a player: ATC tab → "ATIS…" / "Airfield info…", or right-click
one of your coalition's player aircraft on a map. The text (built from the server's live
weather and the airfield) can be edited, then appears as a text box on that player's screen.
Commands are rate-limited and logged in the server's dcs.log.

Missiles: your coalition's missiles (type, trail, line to the target) and enemy missiles fired
at your units ("MSL") are drawn on the maps while they fly.

RWR triangulation: when a pilot calls a spike ("spiked, 3 o'clock"), press **R** over the map
with that aircraft under the pointer or selected (or right-click it → "RWR spike…") and click
the clock position: 12 is the aircraft's nose. Each call draws a bearing line with a ±10°
wedge; two or more from one aircraft give a fix with its error ellipse and bullseye call.
Right-click the fix → "Place threat at fix" → type (the ring follows the type; the lines are
cleared), or anywhere → "Place threat here" (your own note until sent). Right-click a threat →
"Send to F10 map" (a mark for your coalition with the type as its text), "Coordinates…" to read
it out. Right-click the map → "Add F10 mark here…" for a mark with your own text. Every mark
whose text starts with a threat type — sent from DCB or typed by a player on the F10 map
("SA-11", "sa6 by the river") — is a live threat with its ring on every controller's map, also
when DCB connects later; when a player deletes it in the game, it is gone from DCB too. Other
F10 marks (players' in green, the mission's in white, DCB's in red; Foothold's zone labels
hidden) show with the toolbar's "F10 marks". Whether enemy ground units appear on radar at all is the server's
`enemy_ground` setting (by default only in missions from 2012 on).

Alt+click on a map shows that point as DDM, DMS, decimal degrees, MGRS, bullseye call and
ground elevation, with a Copy button.

SRS: the SRS tab talks to your own SRS client on this PC (connected in External AWACS Mode) the
way controller tools such as LotATC do — your radio position (the ATC tab's airfield, or a fixed
point) for SRS range and line of sight, answers to its line-of-sight questions from the terrain,
and tuning its radios (typed MHz, or the airfield's tower frequencies from the DCS install's
radio.lua). Local UDP ports 10710, 10711, 9040 (SRS defaults; `Srs` in settings.json). Line of
sight needs it enabled on the SRS server; LotATC running at the same time takes port 10711.
SRS tab → Listen: DCB also joins the SRS server itself (server, port, your coalition's External
AWACS Mode password; receive only, nothing played or sent; others see "DCB (listen)" in their
client lists). It lists your coalition's frequencies and who is on each, marks who is talking
(in the list and with pulsing rings on the map), and right-clicking one of your players offers
their radios to tune yours to. The password is kept DPAPI-encrypted in settings.json.
For testing, `start-srs-server.cmd` (root) starts a local SRS server on port 5002 with External
AWACS Mode, line of sight and range on and UPnP off; its config and the EAM passwords are in
`Saved Games\DCS.server\SRS\server.cfg` (made on first start).

Maps are drawn by Skia on the GPU (OpenGL), falling back to Skia on the CPU when OpenGL is
unavailable; `DCB_RENDERER=cpu` forces the CPU path. Colours, line widths, dashes and label fonts
come from `style/map-style.json`, the style file the website will share. Render errors go to
`%LocalAppData%\DCB\render-errors.log`.

DCS install: on the first start the client searches for DCS World (ED installer's registry
entries, Steam's library folders, the usual folders on every fixed disk; a folder counts only
with `bin\DCS.exe` or `bin-mt\DCS.exe` and `Mods\terrains`) and asks the controller to confirm
it, pick another (Browse…) or continue without DCS. Window → Map data → DCS World folder…
changes it later (a restart applies it). Kept as `DcsPath` in settings.json ("" = without DCS).

Base maps: Scope (Natural Earth), Satellite (Esri), DCS Map (DCS terrain in DCS's F10 palette with the terrain's area fills — water,
towns, forest, fields — plus its full road network and F10 vector lines, all from the local
install, MGRS grid toggle; roads and areas are extracted once and cached under
`%LocalAppData%\DCB\cache`, and the map is drawn into image tiles under
`%LocalAppData%\DCB\tiles` — the overview once in the background, ~2 minutes and ~400 MB for
Syria, finer tiles when first viewed),
DCS Charts (read from the local DCS install), DCS Satellite (DCS's own satellite ground
imagery, 128 m to 1 m per pixel, read in place from the install's terrain clipmaps).
OpenStreetMap, DCS Terrain and DCS Imagery were removed (user, 2026-10-21); map windows saved
on them open on Scope, DCS Map and DCS Satellite.

The test server currently runs Foothold SY extended 5.3.5 (`server\setup-server.ps1 -MissionPath <miz>`
switches missions; `_downloads\` is git-ignored).

Natural Earth data: `python -I tools\make_basemap.py <Natural Earth dir> client\Dcb.Client\Assets\basemap.json`
(1:10m coastline, admin-0 land boundaries, lakes from naturalearthdata.com).
