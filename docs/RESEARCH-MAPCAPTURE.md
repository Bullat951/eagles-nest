# Research — capturing DCS's own Sat map into one map file

Goal (user, 2026-10-23): the map exactly as DCS draws it (F10 / Mission Editor "Sat"), saved as
**one file per terrain** that Eagle's Nest ships and draws from. Reason: decoding DCS's terrain
materials (DCS_MAP_FORMAT.md "Town ground") got the village placement but not the full shading,
and DCS's own render is exact by definition.

Status: **full Caucasus captured, assembled and shown in the user's live client (2026-10-23)** —
see "Full run (done)" below. History of the proof:
**proof works (2026-10-23, Kobuleti).** `tools/MapCapture` captured 8 views (3390×1371 px
at exactly 2.00 m/px, scale 7000) from the user's Mission Editor in 17 s; `assemble` wrote
`%LocalAppData%\DCB\maps\Caucasus.ensat` (374 tiles at 2 m/px + pyramid, 58 MB for ~6×6 km);
the client draws it in DCS Satellite — crisp, matching F10. Lessons:
- The ME Lua has **no `debug` library**; the map window rectangle comes from the ME's layout
  globals (`left_toolbar_w`, `top_toolbar_h`, `main_w`, `right_panel_height`), the GUI size from
  `main_w`/`main_h` (= the window's pixels here: 3440×1440).
- `me_map_window.getScales()` fails (the map object has no such method); zoom by measuring
  `getMapPoint` and correcting `setScale` (scale ∝ m/px; 7000 = 2 m/px on that screen).
- Copies of the script cannot remove each other without `debug`: each version reads its own
  command file (`command.v5.lua`); a generation counter retires older copies from now on.
- Captured as well: DCS's airfield labels and TACAN symbols (layers not all hidden), screen
  overlays (SRS overlay text, Windows notifications), mission drawings. Before a full run: an
  empty mission, overlays and notifications off, and find the label layers to hide.
- DCS's own Sat texture paints Kobuleti's runway ~100 m off the runway DCS reports — the
  capture reproduces DCS faithfully, offset included.

## What DCS provides (all in plain Lua under `E:\DCS World`)

| Need | DCS function | Where |
|---|---|---|
| Sat mode | `NewMapView:setMapMode("satellite")` (also `"map"`, `"altitude"`) | `MissionEditor/modules/me_statusbar.lua` l.197–207 |
| Position | `me_map_window.setCamera(x, y)` / `getCamera()` (map coords) | `me_map_window.lua` l.341–356 |
| Zoom | `me_map_window.setScale(s)` / `getScale()` / `getScales()` | `me_map_window.lua` l.291–316 |
| Georeference | `me_map_window.getMapPoint(winX, winY)` → map point of a window pixel | `me_map_window.lua` l.361 |
| Clean picture | `me_map_window.showLayer(name, false)` / `getLayers()` (units, icons, grid…) | `me_map_window.lua` l.320–337 |
| Per-frame steps | `require('UpdateManager').add(fn)` — fn runs every GUI frame until it returns true | `Scripts/UpdateManager.lua` |

Screen pixels: no Lua screenshot call (`net.screenshot_request` is the server's player-screenshot
feature), so an external helper captures the DCS window (Windows Graphics Capture or
`PrintWindow` with full-content flag) — a plain desktop capture, no injection into DCS.

## Proposed pipeline (maintainer-only tool; users get the result file)

1. **Capture script** (GUI hook in the *player's* DCS, `Saved Games\DCS\Scripts\Hooks\`), inert
   unless a command file exists. With the Mission Editor open on a mission of the terrain:
   Sat mode, layers off, fixed scale; then a grid of camera positions over the theatre. Per
   step (UpdateManager): set camera → wait until the imagery has streamed in (fixed frames +
   stability check by the helper) → write the window's corner map points (`getMapPoint`) and
   "ready N" to a file → wait for the helper's "done N".
   *To verify first:* that hooks share the Mission Editor's Lua state (`require('me_map_window')`
   works from a hook); otherwise load the script from the ME's own module path.
2. **Capture helper** (C#, `tools/`): waits for "ready N", captures the DCS window's map area,
   saves it with its georeference, answers "done N".
3. **Assembler**: warps the overlapping captures onto the DCS x/z tile grid the client already
   uses (`DcsMapTiles`: 256 px at 2^L m/px) and writes **one file per terrain**, e.g. a SQLite
   tile store (MBTiles-like) or a single zip of JPEG tiles + index. The client reads it as the
   DCS Satellite source (and needs no DCS install for it).

## Alignment fix (2026-10-23)
`me_map_window.getMapPoint` takes **window** coordinates, not map-view coordinates: proven by
`getCamera()` = `getMapPoint(view centre + view offset)` (now logged as a self-check every run;
1.0 m = half a pixel). The first captures were therefore off by the view offset (50 × 40 units
= 100 m × 80 m); after the fix the app's runway (from `Airbase:getRunways()`) covers DCS's
painted Kobuleti runway exactly. Captures are taken with `PrintWindow` (the DCS window's own
pixels: no notifications, no other windows); the assembler skips a 10 % margin (SRS's in-game
status text, scale bar). Still captured: the TACAN beacon symbols ("KBL Chan 67X") — not in the
switchable layers; to find before the full run.

## Full Caucasus plan (measured 2026-10-23)
Detailed region (10 km blocks with towns/fields/forest in surface5): **~296 000 km²**,
x −490…260 km, z −140…960 km. Measured cost: 2.25 s per capture step (3390×1371 view, 30 %
overlap), 0.58 MB/km² as PNG at 2 m/px (JPEG ~4–5× smaller — the assembler still writes PNG).

| Option | Steps | Capture time | File (JPEG) |
|---|---|---|---|
| 2 m/px everywhere | ~37 000 | ~23 h | ~55 GB |
| 4 m/px everywhere | ~9 300 | ~6 h | ~14 GB |
| **8 m/px everywhere + 2 m/px 15×15 km around the 21 airfields** | ~2 300 + ~600 | **~2 h** | **~4 GB** |
| 16 m/px everywhere + 2 m/px airfields | ~600 + ~600 | ~45 min | ~1.5 GB |

Done for the full run (2026-10-23): the helper plans (`plan <landcover cache> <airfields.json>
<plan.json> 8 2 15` → `tools/MapCapture/plans/caucasus.json`: base = 10 km detail blocks + one
block margin, 374 400 km², **2 927 steps** at 8 m/px; 21 airfields 15×15 km, **756 steps** at
2 m/px; ~2.1 h at the measured 2.1 s/step); `run` is resumable (each capture is a file; rerun
skips them); the ME script obeys `setup` / `visit` / `finish`; layers go through the editor's
own `MapLayerController` and are restored on finish. **`AIRPORTS` stays visible**: in Sat mode it
draws the airfields themselves (textured runways, taxiways, shelters) — hiding it removed them
(seen in a test) — so airfield name labels remain in the picture; beacon symbols (USER OBJECTS)
are gone. Assembler: levels with captures rendered from them (finer on top, box-filtered),
coarser levels from tiles, JPEG q85 for full tiles, PNG where partly covered, streaming with a
48-capture cache; the Kobuleti test (8 m base 20 km + 2 m airfield) is 16 MB.

## Full run (done 2026-10-23)
- 3 683 steps captured into `D:\MapCapture\caucasus\` (PNG per step + `step_<id>.lua` georef,
  21.8 GB). Speed-up mid-run: settle 20 frames, `done` written before the PNG is saved (saves
  run async, 4 at a time), stability check starting at 50 ms → **0.62 s/step** (was 2.1 s).
- File races fixed: `WriteShared` retries on IOException/UnauthorizedAccess, `ReadShared`
  tolerates them.
- `assemble` → `D:\MapCapture\Caucasus.ensat`: **1.69 GB**, levels 1–10, ~174 k tiles, ~6 min.
- Installed for the client at **`D:\Claude\DCB\maps\Caucasus.ensat`** (git-ignored `/maps/`).
  Not `%LocalAppData%`: Claude's writes there are MSIX-redirected and the user's client can't
  see them (HANDOFF "Environment facts").
- The user confirmed it is crisp in their own window.
- Known leftovers in the picture: airfield name labels (AIRPORTS layer must stay on), DCS's own
  runway-texture offsets reproduced as-is. Outside the base plan's area the client falls back to
  LandTextures.

## Size and time (first estimate, superseded by the measured plan above)

Caucasus land area ~300 000 km². One 1920×1080 capture at:
- 8 m/px covers ~133 km² → ~2 300 shots, ~1.3 h at 2 s/shot, ~1 GB of JPEG tiles;
- 4 m/px → ~9 000 shots, ~5 h, ~4 GB;
- mixed (8 m/px everywhere, 2 m/px around airfields and towns) keeps the file near 1–2 GB.
Coarser pyramid levels are derived from the captured level by the assembler.

## Open questions for the user

- Target resolution (8 m/px whole map + 2 m/px airfields/towns is the suggested compromise).
- Season (summer only, or one file per season).
- Running it: needs the user's DCS with graphics for a few hours (overnight run), Mission
  Editor open; the PC is busy meanwhile.
- File format (SQLite tile store recommended: one file, random access, standard tools).
