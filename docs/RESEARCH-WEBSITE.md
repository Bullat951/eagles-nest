# Research — a public website for the server (React: map, zones, missions)

Status: research and proposal, nothing built (2026-10-22). Decisions marked **open** are the
user's. Read with [PLAN.md](PLAN.md) ("Principle — website export") and [PROTOCOL.md](PROTOCOL.md).

## Goal

A web page anyone in the group can open to see the campaign: the theatre map with Foothold
zones (owner, upgrades, links), airfield owners, the mission board, weather, and a timeline of
what happened (captures, kills, sorties, landings). Optionally player stats.

## Rules already decided (PLAN.md)

- **Everything except units.** No positions, tracks or pictures on the web, not even delayed.
- The DCS server is the source of truth. **No inbound connections to the DCS server** from the
  internet.
- Messages are versioned and tagged *export yes/no*; the desktop client and the website share
  only data messages and `style/map-style.json`.
- **ED-derived map data never goes to the web** (DCS map tiles, satellite clipmaps, surface5
  pavement, chart pictures). The web uses its own public base map.

## What exists and what is missing

| Data | Message today | Export | For the web |
|---|---|---|---|
| Theatre bounds, lat/lon grid, magvar, towns | `theatre` | yes | map extent, town labels |
| Airfields, owner, runways | `airfields` | yes | owner icons |
| Foothold zones (side, x/z, radius, active, suspended, upgrades) and links | `foothold` | yes | zone circles, links |
| Weather (mission + per airdrome wind/QNH) | `weather` | yes | weather card |
| Foothold mission board | — | to add | mission list |
| Event stream (captures, kills, sorties, landings) | — | to add | timeline |
| Credits, ranks, player stats | — | to add, **open** | stats page |

Positions in the web export are converted to lat/lon on the server (`coord.LOtoLL`), so the
website needs no DCS map maths.

### Mission board (Foothold 5.3.5)

The global `mc` is Foothold's `MissionCommander`. Missions are added with
`MissionCommander:trackMission(params)` (`zoneCommanderv2.lua` line 93355): a title, a
description, an `isActive()` check and texts through `LTGet` (localization). The export walks
`mc`'s tracked missions, keeps the active ones and sends title + description per coalition
(missions are public in Foothold — both sides see the board in game; confirm, **open**).

### Event stream

From the collector's event handler: zone captured (Foothold zone side change, already detected
by the `foothold` check), unit killed (`S_EVENT_DEAD`/`S_EVENT_KILL` — type and side only, no
position), takeoff/landing per airfield, mission completed. Events are kept in a small ring
buffer with ids; the web side asks "since id N".

### Player stats

Foothold saves its whole state every 60 s to
`Saved Games\DCS.server\Missions\Saves\<FootholdSaveBaseName>.lua`
(`BattleCommander:new(filepath, 10, 60)`, built by `BattleCommander:getStateTable()`, line
23124): zones, `accounts` (credits), shops, `playerStats` **keyed by UCID**, plus `ucidToName`.

- **UCIDs are permanent DCS account identities: never export them.** If stats go public, export
  names only, and only after the group agrees (**open**).

## Architecture — how data leaves the server

| | How | For | Against |
|---|---|---|---|
| **1 (recommended)** | The hook writes export JSON (snapshot + events, lat/lon already converted) to a local folder; a small **Node sidecar** on the server machine pushes it to the website backend over HTTPS with an API key (retries, backoff) | DCS never talks to the internet; a crashing or slow website cannot stall the sim; the sidecar can be updated without restarting DCS; one place for the key | One more process to run (as a Windows service) |
| 2 | The DLL pushes HTTPS itself (WinHTTP) | No extra process | Network I/O and TLS inside the DCS process; a hung request or bad certificate becomes a DCS problem; DLL changes need a server restart |
| 3 | Node parses Foothold's save file directly | No DCS change at all | 60 s lag, Foothold only, parsing Lua source in Node, misses events and weather |
| 4 | Node connects to DCB as a TCP client with a "public" role | Reuses the protocol | An inbound connection to the DCS server — against the principle — and the DLL would need a role that strips units |

**Recommendation: 1.** It keeps the existing export rules in one place (the hook decides what
is exported), fits the "no inbound" rule, and isolates DCS from the web completely.

### Backend

Small HTTP service (Node + a database: SQLite to start, Postgres if several servers):

- Ingest (sidecar only, API key): `POST /ingest/snapshot`, `POST /ingest/events`.
- Public read: `GET /api/state` (latest snapshot: theatre, zones, links, airfields, missions,
  weather), `GET /api/events?since=<id>`, and a live stream — **SSE** is enough (one-way,
  survives proxies, auto-reconnect); WebSocket only if the page later sends anything.
- History: keep events for N days (**open**), snapshots once per change.

## Frontend

React with MapLibre GL JS (WebGL vector maps, open source):

| Package | Version on npm (checked 2026-10-22) | Use |
|---|---|---|
| `react` | 19.3.1 | UI |
| `react-map-gl` | 8.1.3 | React wrapper; import from `react-map-gl/maplibre` |
| `maplibre-gl` | 6.15.0 | map engine |
| `pmtiles` | 4.5.0 | reads a PMTiles base map in the browser |
| `@turf/circle` | 7.4.0 | zone circles as GeoJSON polygons |

`npm install react-map-gl maplibre-gl pmtiles @turf/circle`. (A handoff note said MapLibre
5.x from secondary sources; the registry shows 6.15.0 — use the registry's.)

**Base map: Protomaps PMTiles**, one self-hosted file cut to the theatre region (e.g. the
Eastern Mediterranean for Syria), read through the `pmtiles` protocol plugin — register
`maplibregl.addProtocol("pmtiles", …)` once at the app root
(<https://docs.protomaps.com/pmtiles/maplibre>). No tile server needed; a static file host or
the backend serves it with HTTP range requests.

**Do not use `tile.openstreetmap.org`**: the OSMF tile policy allows it only within limited
capacity and blocks heavy/production use and prefetching without notice; do not hardcode its
URL (<https://operations.osmfoundation.org/policies/tiles/>,
<https://operations.osmfoundation.org/policies/vector/>). OpenLayers is the alternative engine
if MapLibre ever does not fit.

Layers and panels:

- Zones: `@turf/circle` polygons filled by side (colours from `style/map-style.json`, the same
  file the desktop client uses), names and upgrade counts as labels, suspended zones hatched.
- Links: lines between zone centres.
- Airfields: owner icons with names.
- Side panels: mission board, weather card (wind, QNH, clouds per selected airfield), events
  timeline (newest first, live via SSE).
- No units anywhere.

## Open questions for the user

1. Hosting: where the backend and page live (a VPS, a home server behind a tunnel, a static host
   plus a small API host)?
2. Who may see the page: public, a password, or the group's Discord login?
3. How long to keep history (events, zone changes)?
4. One server or several (affects the data model: a server id on everything)?
5. Player stats public or not; if yes, names only (never UCIDs)?
6. Mission board: are Foothold's missions shown to both sides in game (then public), or per side?

## Next steps once answered

1. Hook: export writer (snapshot + events JSON, lat/lon) behind a setting, off by default.
2. Sidecar: Node service, pushes with retries; Windows service install script.
3. Backend: ingest + read API + SSE; SQLite.
4. Frontend: map with zones/links/airfields, then missions, weather, timeline.
