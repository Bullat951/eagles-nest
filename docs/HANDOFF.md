# Handoff — 2026-10-23 (evening)

Project: **Eagle's Nest** (working name; code, files and protocol still "DCB"), repo
`D:\Claude\DCB`, public on GitHub: https://github.com/Bullat951/eagles-nest (MIT). Read
[PLAN.md](PLAN.md), [PROTOCOL.md](PROTOCOL.md), [MANUAL.md](MANUAL.md),
[DEVELOPMENT.md](DEVELOPMENT.md) and [DCS_MAP_FORMAT.md](DCS_MAP_FORMAT.md) first. Topic
write-ups from this session: [RESEARCH-MAPCAPTURE.md](RESEARCH-MAPCAPTURE.md) (captured satellite
map) and [RESEARCH-IDENTIFICATION.md](RESEARCH-IDENTIFICATION.md) (layered identification —
**the next feature**). Earlier handoffs are in git history.

## Working rules (the user's — they apply in every session)
- State the task **weight** (small / medium) first, then Think → Plan → Options → Explain →
  Deploy. Wait for the user's choice on real decisions.
- **Never commit, push or offer git** until the user asks.
- Real controller passwords live only in `Saved Games\DCS.server\Config\DCB.lua`;
  `server/config/DCB.lua` is git-ignored. Never print or commit them.
- Snapshots/tests never send SRS 9040 commands. `dev_probe` never on a live server.
- `deploy.ps1` restarts the DCS server — the user may be on it; ask first.
- `build.ps1` closes the client, builds, runs tests, relaunches the root `Dcb.Client.exe` —
  that's the way to rebuild; no side-folder builds for the user.
- Verify UI results in the **user's live window** (or ask them), not only via `--snapshot`
  (see MSIX note below — a snapshot fooled us once this session).

## State
- `main` = `origin/main` at `34cc91c` (carrier lights + Case I/II/III). Release **v0.7.0** on
  GitHub (server + client zips, DLL 0.7.0).
- **Uncommitted: 26 paths** (everything in "Done this session" plus the previous session's
  Caucasus work). Before a commit, check `tools/SatPrototype` and `tools/MapCapture` for
  bin/obj and big PNGs (`compare_*.png`) — probably exclude or ignore them.
- Last build clean, 177 tests pass. Client running with the captured map.
- **Test server** (`Saved Games\DCS.server`): `Saved Games\DCS\Missions\DCB-test.miz`
  (Caucasus, 21 June 08:00), `dev_probe = false`, hook with `GEO_GRID = 81`. Settings backups
  in `Config\`: `serverSettings.lua.syria-day` (Foothold SY 5.3.5 — restore when Caucasus
  testing is over), `.night`, `.before-night`.
- **External testing:** ports 10308 TCP/UDP (DCS), 10309 TCP (Eagle's Nest), 5002 TCP/UDP
  (SRS) forwarded on the Keenetic (PPPoE connection) to 192.168.1.34; 8088 must stay closed.
- **Mission Editor patch still installed:** one line appended to
  `E:\DCS World\MissionEditor\modules\me_map_window.lua` (backup `.eaglesnest-backup`) that
  loads `Saved Games\DCS\EaglesNest\capture_me.lua`. Undo with
  `tools\MapCapture` → `MapCapture restore`. Until then: **don't join IC-enforced servers.**
  The user hasn't said whether more terrains will be captured; ask before removing.

## Done this session (uncommitted)

### A. Caucasus land cover on the DCS Map
`LandCover.Extract` reads every shader (`Surface5.Spans(stream, null)`), `ClassOf` maps
`Trees`/`Forest`; caches `_v5`, tiles `r9`. Syria changed slightly (+~1 100 km²). New
`Dcb.Probe --census <surface5>` (per-shader record/triangle counts).

### B. Satellite research (superseded, kept for reference)
Decoding DCS's terrain materials to draw F10 "Sat" ourselves: town placement decoded
(`HardSplatArray5.1` `parametricUVW` → node's `MeshAssetManifold5` `xyz2uvw`), but the
per-material photo transform (`setMaterialManifold`) was not found, so villages never matched.
Prototype `tools/SatPrototype` (+ `score` NCC command), scripts `tools/research/caucasus/`.
Details: DCS_MAP_FORMAT.md "Town ground". **Decision: capture DCS's own render instead.**

### C. Captured DCS satellite map — done for Caucasus
User's goal: "accurate and crisp as in the screenshots", **one file per terrain** shipped with
the app. Full write-up: RESEARCH-MAPCAPTURE.md.
- **Tool `tools/MapCapture`** (net10.0-windows): `install [dcs]` / `restore [dcs]` (ME patch),
  `plan <landcover cache> <airfields.json> <plan.json> [base m/px] [airfield m/px] [km]`,
  `run <plan.json> <dir>` (resumable, batches of 20), `assemble <dir> <terrain> <out.ensat>`.
- **`capture_me.lua` (v6)** runs inside the Mission Editor's Lua (separate from hooks; no
  `debug` lib): Sat mode, hides layers via `Mission.MapLayerController` (`DEFAULT_HIDE`: GEO NAME,
  MGRS/GEOGRAPHICAL GRID, USER OBJECTS = beacons, SUPPLIERS OBJECTS, PARKINGS, DRAWINGS, power
  lines, BUILDINGS). **AIRPORTS must stay visible** — in Sat mode it draws runways/taxiways.
  Zoom by measure-and-correct (`getScales` fails). `getMapPoint` takes **window** coordinates
  (+50,40 bounds offset) — that was the ~100 m runway misalignment; self-check logged each run.
  Versioned command files + generation counter so stale script copies retire.
- **Capture:** `PrintWindow(PW_CLIENTONLY|PW_RENDERFULLCONTENT)` on the "Digital Combat
  Simulator" window (not DCS.server), stability = two matching shots; 10 % margin dropped by the
  assembler (SRS overlay, scale bar).
- **User's choices:** 8 m/px everywhere + 2 m/px 15×15 km around the 21 airfields, JPEG tiles,
  beacons hidden, resumable. Speed-up approved mid-run: 2.1 s → 0.62 s/step.
- **Result:** 3 683 captures (`D:\MapCapture\caucasus\`, 21.8 GB PNG — the raw material, keep
  until the user says otherwise), `D:\MapCapture\Caucasus.ensat` 1.69 GB (SQLite
  `tiles(level,tx,tz,data)` + `meta`, 256 px at 2^L m/px, DCS x/z grid, row 0 north; JPEG q85 full
  tiles, PNG partial), levels 1–10, ~174 k tiles. **Installed at
  `D:\Claude\DCB\maps\Caucasus.ensat`**; `/maps/` added to `.gitignore`.
- **Client:** `Map/Satellite/CapturedMap.cs` opens `<app>\maps\<terrain>.ensat` or
  `%LocalAppData%\DCB\maps\…` read-only, keeps the key set in memory, re-checks a missing file
  every 10 s, logs open/missing to `%LocalAppData%\DCB\captured-map.log`.
  `BaseMapLayers.DrawCaptured` draws level+2 (if in cache) under the current level, ≤ 400 tiles
  per level, over LandTextures. **User confirmed crisp in their live window.**
- **The "blurry on my end" bug** was not code: Claude desktop is MSIX, so my copy into
  `%LocalAppData%` went to `Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\DCB\maps` — visible to
  my snapshots, invisible to the user's client.

### D. Identification research (design only, nothing built)
RESEARCH-IDENTIFICATION.md. Idea (user): the operator *works* for identity in layers instead of
contacts arriving typed.
1. **RWR / ESM picture** per own jet / radar / ship; operator matches RWR emitters to radar
   contacts and assigns them.
2. Where no RWR evidence: **NCTR** methods, each with its own range/aspect/dwell limits
   (JEM, HRR, size/RCS class, rotor modulation).
3. **Missiles** appear as unknown fast tracks; become missiles above Mach 2.5 or on NCTR
   "small"; unidentified > Mach 2.5 → missile; warning, **Space** jumps to it (repeat = next).

Key API finding: `Controller:getDetectedTargets(Detection.RWR)` exists for AI only and is
unreliable, players have no controller → **compute RWR on the server** from `Unit:getRadar()`
(on + tracked object), shot events, positions and `land.isVisible` LOS. Same for AI and
players, can't be cheated from the client.

**User's decisions:**
- RWR: computed on the server, **but measure the performance impact first**.
- NCTR: **on request**, for an **area the operator selects** on the map, with a **choice of
  NCTR methods** to try.
- Missiles: **only within own sensor coverage**.
- Server setting `identification = "manual" | "auto"` (auto = today's behaviour).

Estimate: ≤ 3 200 receiver×emitter pairs/s on a busy Foothold server, a few hundred LOS calls
after distance culling, ≲ 25 ms/s on the sim thread (today's whole picture: 2–4 ms/s avg).
Mitigations: LOS cache per pair 5 s, spread over frames, reuse for wingmen.

## Next steps (in the order I'd suggest)

1. **Identification phase 1 — measure** (offered, not yet approved by the user). Needs
   `dev_probe = true` on the *test* server (ask; `deploy.ps1` restarts it). Probe:
   `land.isVisible` cost per call, real counts of own RWR carriers × enemy emitters on,
   `getRadar()` tracked object for SAMs/ships/AI/player aircraft, whether radars ever list
   weapons in `getDetectedTargets`. Then decide the update rate.
2. Phases 2–5 per RESEARCH-IDENTIFICATION.md: `rwr` channel per coalition (receiver → emitters
   with bearing ± error, label from an RWR library, mode search/track/launch) → client RWR
   scope + bearing lines + assign → `identification` setting, untyped contacts, threat rings
   only from identified → NCTR area + method picker with dwell and "no result: reason" →
   coverage-limited weapon tracks, Mach 2.5 rule, warning list, Space.
   Open design questions to put to the user: RWR bearing error per receiver type; whether
   assigned identities are shared to the whole coalition's controllers (proposed yes);
   whether "auto" stays the default; how NCTR results show (label + confidence?).
3. **Captured-map cleanup / packaging** (ask before each):
   - delete the 1.8 GB redirected duplicate and redirected tile/land-cover caches under
     `%LocalAppData%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\DCB\`;
   - `MapCapture restore` when no more capturing is planned;
   - shipping: the file is too big for the client zip → separate release asset / map pack
     download (ties into the "map packs" item below); maybe a lower-res variant;
   - move the `CapturedMap` index load (174 k keys from SQLite) off the UI thread;
   - airfield name labels are baked into the captures (accepted for now);
   - other terrains (Syria, PG…) with the same tool: needs a plan file per terrain and a few
     hours of the user's DCS with the ME open.
4. **Foothold on Caucasus**: Leka's "Foothold Caucasus" 4.5.0 — the user downloads it into
   `D:\Claude\DCB\_downloads\foothold_ca` (Cloudflare blocks fetching). Then switch the server,
   check `lua/foothold.lua` against 4.5.0 (built for 5.3.5), change its GameMaster password if
   public.
5. Persian Gulf: palette fix applies; pavement, signs, satellite not checked.
6. Older: map packs (user to choose contents), Steam detection untested, clean install from the
   release zip untested, website export paused, listen-traffic with a real player.
7. Commit the session's work — **only when the user asks.**

## Previous session's Caucasus work (also uncommitted)
GEO_GRID 21→81 (`lua/DCB-hook.lua`, 30–140 m error fixed); DCS Map palette from DXT5
`mapmodepalette.tif.dds`; Caucasus pavement shaders and finest-level detection (cache `_v2`);
airfield signs with Caucasus naming (letter boards exist at only 6 fields); taxi networks matched
by location; blank-diagram fix on `TheatreChanged`; LandTextures (38.4 m/px F10 colour texture,
placement P = x/38.398 − 0.64, Q = z/38.3929 + 1.10). Taxiway letters from kneeboard charts were
**reverted on the user's decision** (letters only where DCS has real signs).

## Environment facts that bite
- **Claude desktop is MSIX:** Claude's writes under `%LocalAppData%` / `%AppData%` are
  redirected to `%LocalAppData%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\…` (even with the
  sandbox off) and are seen only by processes Claude starts. Put files the user's apps must read
  on `D:` (app folder). `captured-map.log` written by the user's client *is* real.
- Snapshots: `Dcb.Client.exe --snapshot <dir> <host> <port> <pw> [s] [mode] [lat,lon,res]` —
  CPU `RenderToImage`, not the live GPU path. Use a fresh `DCB_HOME` per check.
- Start DCS/SRS/client via `Win32_Process.Create` (Claude's shell runs in a job).
- ME capture: the ME's Lua is not the hooks' state; the window title "Digital Combat
  Simulator" (DCS.server's window must not be picked).
- Python has no PIL/numpy; PowerShell `Set-Content -Encoding utf8` writes a BOM.
- ED forums / DCS user files answer 403 (Cloudflare); GitHub uploads blocked in the sandbox
  (the user runs `gh release upload`).
- Dev probe (`DCB\probe.lua`) needs `dev_probe = true` — currently **false**.
