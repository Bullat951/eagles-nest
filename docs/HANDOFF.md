# Handoff — 2026-10-22 (evening)

**Update (later the same evening):** A and B are written — [RESEARCH-WEBSITE.md](RESEARCH-WEBSITE.md)
and [RESEARCH-EXTERNAL-CONTROL.md](RESEARCH-EXTERNAL-CONTROL.md) (npm versions checked on the
registry: MapLibre GL JS is 6.15.0, not 5.x; all 34 Foothold functions confirmed in the 5.3.5
mission's scripts with line numbers). C's first item is done (PLAN.md section "Performance, ATC
view panels, layout, theme", README snapshot modes, theme and ATC views). Still open from C: the
user trying the theme and ATC view panels, and the older open items listed there.

Where the work stands for the next session. Read with [PLAN.md](PLAN.md) (phases, decisions)
and [PROTOCOL.md](PROTOCOL.md). The user's request this round had six parts; four are built,
two research write-ups are **not written yet** (findings below, so nothing needs re-researching).

## Environment facts that bite

- Build: `D:\Claude\DCB\build.ps1` (closes the client, builds, runs tests — 158 pass —, builds
  dcb.dll, relaunches `Dcb.Client.exe` in the repo root). Relaunch and `server\start-server.ps1`,
  `server\start-srs-server.ps1` now start processes with `Win32_Process.Create`: Claude Code's
  shell runs in a Windows job, and a Claude desktop update at 15:48 killed DCS and the client
  started from it. Start anything long-lived the same way.
- Test server: `Saved Games\DCS.server`, Foothold SY 5.3.5, DCB port 10309, passwords in
  `Config\DCB.lua`. Local SRS server on 5002 (`Saved Games\DCS.server\SRS\server.cfg` has the EAM
  passwords). The user's own SRS app runs and is connected in EAM.
- Collector live-reload without restarting DCS: copy `lua\collector.lua` to
  `Saved Games\DCS.server\Mods\Services\DCB\Scripts\collector.lua`, then write
  `Saved Games\DCS.server\DCB\probe.lua` = `-- env: server` + the file + `return 'ok'` (UTF-8
  **without BOM**; Lua 5.1, no `//`; never busy-wait on `timer.getTime()` — it does not advance
  within a call). Output in `probe.out`. `deploy.ps1` restarts the server.
- **Snapshots must never send SRS commands** (port 9040): one did earlier and retuned the user's
  real SRS app. `SrsBridge.Tune` now refuses in snapshot runs, and `DebugSnapshot.TryStart` runs
  before the SRS services start (it used not to, so a snapshot opened the 7082 listener).
  Floating AvalonDock windows capture blank in snapshots; the snapshot captures any HwndSource
  holding a MapPanel/AtcPanel/SrsPanel/AtcViewPanel.
- Snapshot modes added this round: `perf` (UI timings in status.txt), `views:<airfield>`,
  `srstab[:<SRS user>]` (`DCB_SNAPSHOT_EAM`), `packages`; `DCB_SNAPSHOT_SRSAPP=1` lets a snapshot
  read 7082 (it then shares packets with the user's client).

## Built this round (not yet written into PLAN.md — add a section)

1. **Performance, measured.** Server collector (`lua/collector.lua`): unit lists cached (full
   rescan every 5 s or after `S_EVENT_BIRTH`), ground units' JSON rebuilt every 5 s, contacts
   only from ground/ship groups that have a radar, heading from velocity when moving (no
   `getPosition`), static JSON prefix per unit, carrier feed reuses the air list, caches pruned
   at rescan. Probe: 10 ms / 0.7–1.1 MB garbage per collect → ~1 ms / 51 KB, same 175 entries.
   Live log (once a minute): avg 6.6–13.4 ms → 1.7–5.2 ms; max 11–60 (160 seen) → 5–17 ms in 10
   of 12 minutes, two minutes with 58 and 72 ms — probably Lua GC of the shared ~200 MB heap
   (Foothold's; one full GC = 34 ms), not addressed (changing GC parameters would affect the
   mission). Client: `RenderStats` "UI: picture" avg 0.6 / max 2.9 ms, "UI: lso frame" 0.01 ms,
   panels 0.1–0.3 ms — already under target, left as is.
2. **ATC views as panels**: `Panels/AtcViewPanel.cs` (Diagram, Approach, Carrier, LSO), Window →
   ATC views; a new one floats 900×700. They follow the ATC pick (`AppState.SelectAirfield` /
   new `AppState.SelectCarrier`), each keeping its last one while the other kind is picked.
3. **Window → Save layout** (`SaveLayouts()`, also used on exit; main placement taken from the
   main window). Reset layout already existed (bottom of the Window menu).
4. **Tactical console theme, dark/light** (user picked "B · Tactical console"):
   `Themes/Colors.Dark.xaml`, `Colors.Light.xaml` (same `Dcb.*` keys), `Themes/Tactical.xaml`
   (flat templates: Button, chip CheckBox for toolbars, CheckBox, RadioButton, TextBox,
   PasswordBox, ComboBox, Menu/MenuItem/ContextMenu, ToolTip, TabControl/TabItem, GroupBox,
   DataGrid, ScrollBar, ToolBar, StatusBar, GridSplitter), `Themes/ThemeManager.cs` (Window →
   Theme: System/Dark/Light, setting `AppSettings.Theme`; follows Windows' app mode live; dark
   title bars via DWM; AvalonDock VS2013 Dark/Light), `Themes/ThemeBrush.cs`. Panels that built
   colours in code now use theme brushes and redraw on `ThemeManager.Changed`. Checked by
   snapshot in both modes. On purpose unchanged: the scopes (Approach, Carrier, LSO) stay dark,
   the airport diagram stays white paper. Not styled: Slider, the ScrollViewer corner square.
   The user has not looked at it yet.
5. Client close "did not exit in 15 s": not reproducible (connected, with SRS, with the 7082
   listener — all close in ~0.2 s). Likely a modal dialog/menu was open (Windows does not deliver
   WM_CLOSE then); `build.ps1` force-closes after 15 s and settings are then not saved.

## To do next

### A. Research write-up 1 — website (React) with map, zones, missions → `docs/RESEARCH-WEBSITE.md`

Constraints already decided (PLAN.md "Principle — website export"): everything except units
(no positions/tracks/pictures, not even delayed); server is the source of truth; no inbound
connections to the DCS server; versioned messages tagged export yes/no; desktop and web share
only data messages and `style/map-style.json`; ED-derived map data never goes to the web.

Data available now (export: yes): `theatre` (bounds, lat/lon grid, magvar, towns), `airfields`
(owner, runways), `foothold` (zones: name, side, x/z, r, active, suspended, upgrades; links),
`weather`. To add: Foothold **mission board** — global `mc` (MissionCommander), missions added
by `mc:trackMission{title, description, isActive()…}` (texts via `LTGet`); an event stream
(captures, kills, sorties, landings); Foothold credits/ranks/stats.

Findings to use:
- Foothold persists everything to `Saved Games\DCS.server\Missions\Saves\<FootholdSaveBaseName>.lua`
  every 60 s (`BattleCommander:new(filepath, 10, 60)`), built by `bc:getStateTable()`: zones,
  `accounts` (credits), shops, `playerStats` **keyed by UCID** plus `ucidToName`. UCIDs are
  permanent DCS identities → never export them (names only, if the group wants stats public).
- Architecture options to present: (1) recommended — hook writes export JSON (lat/lon converted
  server-side with `coord.LOtoLL`) to a folder, a small Node sidecar on the server machine pushes
  snapshots + events to the website backend over HTTPS with a key (retries; DCS never talks to
  the internet); (2) DLL pushes HTTPS itself (WinHTTP; network inside the DCS process); (3) Node
  parses Foothold's save file directly (no DCS change, 60 s lag, Foothold-only, Lua parsing);
  (4) Node as a DCB TCP client with a public role (reuses the protocol, but inbound to DCS —
  against the principle). Backend: REST `GET /api/state`, `GET /api/events?since=`, SSE or
  WebSocket stream; ingest `POST /ingest/snapshot|events`.
- Frontend: React + MapLibre GL JS via `react-map-gl/maplibre` (`npm install react-map-gl
  maplibre-gl`); base map from **Protomaps PMTiles** (one self-hosted file per theatre region,
  read in the browser via the `pmtiles` protocol plugin — register `addProtocol` once at the
  root); do **not** use tile.openstreetmap.org (OSMF policy: limited capacity, heavy/production
  use and prefetching blocked without notice; don't hardcode its URL). OpenLayers as the
  alternative. Zones as GeoJSON circles (turf) filled by side + labels, links as lines, airfield
  owner icons, missions list, weather, events timeline. Verify current versions on npm (search
  found react-map-gl 8.x, MapLibre GL JS 5.x; secondary sources).
- Sources: https://docs.protomaps.com/pmtiles/maplibre ,
  https://operations.osmfoundation.org/policies/tiles/ ,
  https://operations.osmfoundation.org/policies/vector/
- Open questions for the user: hosting, website auth, history retention, several servers,
  whether player stats are public.

### B. Research write-up 2 — external control of units and Foothold → `docs/RESEARCH-EXTERNAL-CONTROL.md`

Base: the command path exists (client → DLL queue → hook → whitelisted mission-env functions,
roles, rate limits, audit log; tier 1 `message_unit` built). PLAN item 10 plans tier 2 (AI
control) and tier 3 (admin, separate password).

DCS scripting (mission env): spawn `coalition.addGroup/addStaticObject`; `Group.activate`,
`trigger.action.activateGroup/deactivateGroup`, `setGroupAIOn/Off`, `destroy`;
`Controller:setTask/pushTask/setCommand/setOption` (orbit, race-track, attack group/zone, RTB,
ROE, alarm state, radar use, reaction to threat); `trigger.action` smoke, flares, illumination,
explosion, `markToAll/markToCoalition/markupToAll`, text/sound, `setUserFlag` (flag-driven
mission logic). Server (hook/GUI env): `net.send_chat`, `net.force_player_slot`, `net.kick`,
`net.load_mission/load_next_mission`, `DCS.setPause`, player list/info.

Foothold 5.3.5 (`zoneCommanderv2.lua`, global `bc` = BattleCommander, in the trigger env — reach
via `a_do_script` from the hook, as `lua/foothold.lua` does; see PLAN.md Foothold note):
- Coalition: `bc:addFunds(side, n)`, `bc:buyShopItem(side, id, …)` (the shop runs CAP/CAS/SEAD/
  supply/etc.), `bc:registerShopItem`, `bc:printShopStatus`.
- Zones (`bc:getZoneByName(name)` → ZoneCommander): `capture(side, silent)`, `upgrade()`,
  `MakeZoneBlue/MakeZoneRedAndUpgrade`, `suspend/resume`, `killAll`, `markWithSmoke`,
  `displayStatus`.
- Operations: `bc:requestCaptureMission(zone, opts)`, `bc:requestFriendlySupplyMission(zone)`,
  `bc:triggerRedMassAttack(targets)`, `bc:increaseDifficulty/decreaseDifficulty`,
  `bc:spawnNowBySide(side)`, `bc:launchPlayerGroundAttack(zone)`, `bc:fireAtZone/startFiringAtZone`,
  `bc:jamRadarsAtZone`, `bc:smokeTargets/illumTargets/flareTargets(zone)`.
- Missions: `mc:printMissions`, `mc:getActiveStrikeMissions(side)`, `mc:trackMission`.
- State: `bc:getStateTable()`, `bc:saveToDisk()` (admin changes persist through the 60 s save).
- Players: `bc:getPlayerCredits/getPlayerRank`, `bc:addPlayerRankCredits`, stats printers.
- `Zeus.lua` (MOOSE): F10 mark text `-create <unit>` spawns from a fixed list (SA-8…SA-19,
  Shilka, BMP2, Tank, CTLD cargo, JTAC…), plus area destroy — callable directly as
  `handleSpawnRequest(text, coord)` rather than via marks. CTLD scripts also present.
- Write as: capability matrix (command · API · tier · coalition rule · risk), tiers 1/2/3,
  safety (whitelist only, fixed functions, `%q` arguments, coalition checks, audit, UI
  confirmation, rate limits, persistence), a UI concept (right-click own AI group: orbit here /
  vector / RTB / weapons hold-free; Foothold zone menu: capture/supply mission, status;
  admin panel), and the risk that Foothold's internal API changes between versions (adapter
  layer with feature detection). Ask the user who gets tier 2/3 and the server group's rules.

### C. Then

- Add this round to PLAN.md (perf numbers, panels, theme) and the snapshot modes to README.md.
- Let the user try the theme and the ATC view panels; fix what they report.
- Open from earlier rounds: Listen traffic on a real player, JUMP / Set CH6 by hand, SRS app
  auto-start on connect, carrier/airfield queue with departures, Approach zoom by hand.
