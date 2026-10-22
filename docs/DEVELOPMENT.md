# Development

How to build Eagle's Nest (code name DCB), run the local test server, and the tools used to
check it. Design history and decisions: [PLAN.md](PLAN.md); wire format: [PROTOCOL.md](PROTOCOL.md).

## Layout

| Folder | What |
|---|---|
| `dll/` | C++ server DLL `dcb.dll`: Lua 5.1 module + TCP server (`build.ps1`, CMake, MSVC) |
| `lua/` | `DCB-hook.lua` (DCS GameGUI hook), `collector.lua` (mission env), `foothold.lua` (Foothold exporter) |
| `server/` | local test server scripts (`setup-server.ps1`, `start-server.ps1`, `deploy.ps1`), config templates, release packaging (`package.ps1`, `release/`) |
| `client/` | C# .NET 10: `Dcb.Client.Core` (protocol, maths, testable), `Dcb.Client` (WPF app), `Dcb.Probe` (console), tests |
| `style/` | `map-style.json`: colours, widths, fonts shared by the client and a future website |
| `tools/` | `DiagramIndexer` (airport-diagram pictures), `web-demo` (live website export demo), map/data helpers |
| `docs/` | manual, protocol, analytics, plan, research notes |

## Requirements

- Windows, **.NET 10 SDK**, **Visual Studio Build Tools** (MSVC) and **CMake** for the DLL.
- A DCS World install for the test server and the DCS base maps.

## Build

`.\build.ps1` builds everything in one go (one build type, no Debug/Release split) and runs the
tests:

- `Dcb.Client.exe` (project root): the client, a single file (needs the .NET 10 Desktop Runtime)
- `app\Dcb.Probe.exe`: console probe
- `app\tools\DiagramIndexer\DiagramIndexer.exe`: places airport-diagram pictures, reads their names
- `dll\build\Release\dcb.dll`: the server DLL (installed by `server\deploy.ps1`)

A running client is closed (it saves its layout), rebuilt and started again.

Release packages: `server\package.ps1` → `release\EaglesNest-Server-<version>.zip` (copy-only
install; the hook makes `Config\DCB.lua` with random passwords on the first start; the 500 m
terrain grids sampled on the test server are included) and `release\EaglesNest-Client-<version>.zip`.
The version is `kVersion` in `dll/src/dcb.cpp`.

## Local test server

1. `server\setup-server.ps1` once: creates the `Saved Games\DCS.server` instance
   (`-MissionPath <miz>` switches missions).
2. `server\deploy.ps1`: builds the DLL, installs hook, DLL and scripts, (re)starts the server.
   Controller passwords: `Saved Games\DCS.server\Config\DCB.lua` (git-ignored, never committed).
3. Later sessions: `start-test-server.cmd` starts the server if needed and waits until DCB
   answers on 127.0.0.1:10309.
4. Run `Dcb.Client.exe`, or `app\Dcb.Probe.exe 127.0.0.1 <password>`.

`start-srs-server.cmd` starts a local SRS server on port 5002 (External AWACS Mode, line of sight
and range on, UPnP off); its config and EAM passwords are in `Saved Games\DCS.server\SRS\server.cfg`.

**Dev probe** (`dev_probe = true` in the test server's `DCB.lua`, never on a live server): the
hook runs `Saved Games\DCS.server\DCB\probe.lua` once (mission env, or the hook's own env with a
first line `-- env: hook`), writes the result to `probe.out` and deletes the probe. Write probes
without a BOM.

## Snapshot mode (checking the UI without a person)

```
Dcb.Client.exe --snapshot <out dir> 127.0.0.1 10309 <password> [seconds] [mode] [lat,lon,res[,coast|grid|dimN|zone=<name>]] [test]
```

- `mode`: a base map name (opens it floated), `restore`, `atc:<airfield>`, `approach:<airfield>[+q]`,
  `carrier:<name>[@<NM>][+q]`, `lso:<name>`, `views:<airfield>`, `packages`, `srstab[:<SRS user>]`,
  `perf`, `dcsfolder` (the DCS location dialog).
- `test` (after a base map and view): `zoomtest`, `pantest[:<layers>]`, `wheeltest`, `settletest`,
  `switchtest`, `measure`, `point`, `gci` / `gci+send` (triangulation board; `+send` makes a real F10 mark).
- Writes PNGs of every window and `status.txt` (connection, timings, memory, the test's report).
- Use `DCB_HOME=<empty folder>` for a clean profile. `DCB_SNAPSHOT_EAM` gives `srstab` an EAM
  password; `DCB_SNAPSHOT_SRSAPP=1` lets it read the SRS app's port 7082. **Snapshots never send
  commands to the SRS app.**

## Client internals worth knowing

- Settings and layouts: `%AppData%\DCB` (override with `DCB_HOME`); caches and DCS Map tiles:
  `%LocalAppData%\DCB` (`cache`, `tiles`); render errors: `%LocalAppData%\DCB\render-errors.log`.
- Maps are drawn by Skia on the GPU (OpenGL), with a CPU fallback; `DCB_RENDERER=cpu` forces it.
- DCS install: `DcsInstall.Find()` (client) — `Settings.DcsPath`, else the first install found by
  `DcsLocator` (ED registry entries, Steam libraries, usual folders).
- DCS Map: tiles at 2^L m/px; levels ≥ 32 m/px pre-rendered in the background, finer on view;
  keyed by renderer version, terrain grid and the DCS files read, so a DCS update starts fresh.
- Airport diagrams from third-party charts (optional, local only):
  `DiagramIndexer.exe index <folder of page PNGs> 127.0.0.1 10309 <password>` writes
  `diagrams.json`; set the folder under *Window → Map data*. `DiagramIndexer.exe probe <page.png>`
  prints what is read from one page.
- Natural Earth base map: `python -I tools\make_basemap.py <Natural Earth dir> client\Dcb.Client\Assets\basemap.json`.

## Server internals worth knowing

- The hook (GameGUI env) calls the collector in the mission env once per `interval` (1 s) and
  publishes one picture per coalition through the DLL; other feeds (theatre, terrain, airfields,
  weather, Foothold, F10 marks, LSO) have their own channels and rates.
- Costs are logged once a minute in `dcs.log` ("collect:" and "timing:" lines).
- Foothold's state lives in the trigger scripting env, not `net.dostring_in('server')`; the
  exporter is injected there (`foothold.lua`).
- Analytics and website export: [ANALYTICS.md](ANALYTICS.md); demo page
  `node tools\web-demo\server.js` (http://127.0.0.1:8099).

## Research notes

[RESEARCH-WEBSITE.md](RESEARCH-WEBSITE.md) (public website) and
[RESEARCH-EXTERNAL-CONTROL.md](RESEARCH-EXTERNAL-CONTROL.md) (controlling units and Foothold).
