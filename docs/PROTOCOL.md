# DCB protocol (v1)

TCP, UTF-8, one JSON object per line (`\n`). Default port 10309. Every message has a
`type`. Readers must ignore unknown fields and unknown types, so fields can be added
without breaking older clients (or the future website backend).

**Visibility** says who may see a message on the controller link: `public` goes to both
sides, `coalition` only to that side. **Export** says whether it goes to the planned
website: all mission information does, **except units** (positions, tracks, pictures).

| Type | Direction | Visibility | Export | When |
|---|---|---|---|---|
| `auth` | client → server | — | — | first line after connecting |
| `auth_ok` / `auth_fail` | server → client | — | — | reply to `auth` |
| `picture` | server → client | coalition | **no** (units) | every `interval` (1 s) |
| `theatre` | server → client | public | yes | after login, on mission change |
| `terrain` | server → client | public | yes | after login (sampled once per terrain, cached) |
| `foothold` | server → client | public | yes | after login, then when zones change (checked every 10 s) |
| `airfields` | server → client | public | yes | after login, then when an owner changes (checked every 60 s) |
| `weather` | server → client | public | yes | every 60 s |
| `lso` | server → client | coalition | **no** (units) | 10 a second while own aircraft are within 10 NM of an own carrier |
| `marks` | server → client | coalition | no | after login, then when the coalition's F10 marks change (checked every 5 s) |
| `command` | client → server | — | no | when a controller acts (rate-limited) |
| `command_result` | server → client | that client | no | answer to each `command` |

## command / command_result
A controller acting on the server. The DLL accepts only known command names with checked
fields, at most 5 in one second and 30 a minute per connection, and runs them on the sim
thread through fixed mission functions (client text is passed as a string, never run).
Every command and refusal is logged to dcs.log with coalition and client address.

`{"type":"command","id":7,"name":"message_unit","unit":4940,"text":"…","seconds":30}` — an
on-screen text box (`trigger.action.outTextForUnit`, not chat) for one aircraft of the
controller's own coalition with a player in it. `unit`: DCS unit id from the picture; `text`:
≤ 1 500 bytes, no control characters except newlines; `seconds`: 5–120.

`{"type":"command","id":8,"name":"mark_add","x":30000,"z":40000,"text":"SA-6 (GCI)","threat":"SA-6"}`
(DLL 0.6) — an F10 map mark for the controller's own coalition
(`trigger.action.markToCoalition`, not read-only: the coalition's players can delete it in the
game). `x`, `z`: DCS metres (integers, ±2 000 km); `text`: ≤ 200 bytes, one line; `threat`
(optional): ≤ 24 letters, digits, `-`, `/`, spaces — kept with the mark. Result
`"value":"880000"`: the mark id. Clients treat any mark whose text starts with a threat type
("SA-6", "sa11 …", "Patriot") as a live threat with its ring, whoever placed it; the mark is
the threat, so deleting it in the game removes the threat from every controller.

`{"type":"command","id":9,"name":"mark_remove","mark":880000}` — removes a mark DCB made for
the controller's coalition (any controller of it); other marks are refused (`not a DCB mark of
your coalition`).

`{"type":"command","id":10,"name":"carrier_lights","unit":445,"mode":2}` (DLL 0.7) — a carrier's
lighting mode, as the Mission Editor's SET CARRIER LIGHTING MODE (`a_set_carrier_illumination_mode`
in the mission env): `mode` −2 off, −1 auto, 0 navigation, 1 launch, 2 recovery. Only a carrier
type DCB knows, of the controller's own coalition (`not a ship of your coalition (or it is
gone)`, `not a carrier`). Verified in multiplayer on CVN-72 and Stennis (RESEARCH-LIGHTS.md).
DCS does not report a carrier's current mode.

Answer to the same client: `{"type":"command_result","id":7,"ok":true}` (with `"value"` when
the command returns one) or
`{"type":"command_result","id":7,"ok":false,"error":"no player in that aircraft"}`. Errors
include `unknown command`, field errors (`seconds: 5 to 120`, `text has control characters`),
`too many commands, wait a moment`, `server busy`, `server not ready yet`, `not an aircraft of
your coalition (or it is gone)`.

## marks (coalition)
`{"type":"marks","coalition":"blue","marks":[{"id","text","x","z","lat","lon","time","author",
"dcb","threat"}]}` — the F10 map marks the coalition can see (its own and those for all, at most
300; `world.getMarkPanels`): players', scripts' (Foothold zone labels, CSAR downed pilots) and
DCB's. `author`: the placing player's name, or "mission data" for scripts; `dcb`: made by a
controller with `mark_add`, then `threat` is its threat type or null; `time`: mission seconds.
Sent per side on its own channel (`publish_side`), only when the list changes. Not exported.

## weather
`{"type":"weather","date":"2025-07-01","time":<mission s since local midnight>,"visibility":<m>,
"fog":{"on","visibility","thickness"},"dust":{"on","density"},"clouds":{"base","thickness",
"density","preset","precipitation"},"airfields":[{"id","elevation","windFrom","windMs","tempC",
"qnhHpa"},…]}` — mission weather plus, per airdrome, the live wind 10 m above the field (from,
true degrees; m/s), temperature (°C) and QNH (hPa, local pressure reduced with the ISA lapse
rate). `preset` is a DCS cloud preset name or null (then `density` 0–10 applies);
`precipitation` 0 none, 1 rain, 2 thunderstorm, 3 snow, 4 snowstorm.

## lso (coalition)
`{"type":"lso","coalition":"blue","time":<mission s>,"carriers":[{"id","x","y","z","hdg","vx",
"vz"}],"aircraft":[{"id","x","y","z","vx","vy","vz","hdg","pitch","gear","hook"}]}` — the fast
feed for the LSO view: the side's carriers and its aircraft within 10 NM of one, in DCS metres
(x north, y up, z east), m/s and degrees from grid north. `gear`/`hook` 0..1 from the model's
draw arguments 0 and 25, null when the model has none (25 is not the hook on every type).
Sent 10 times a second only while such aircraft exist; the aircraft list is refreshed with each
picture. The DLL keeps it in its own per-side slot (`publish_side`), so it never displaces the
picture. Coalition-private, not exported.

## auth
`{"type":"auth","password":"…","client":"DCB Client 0.1"}`. The password decides the
coalition. Reply: `{"type":"auth_ok","coalition":"blue"}`, or `{"type":"auth_fail"}` and
the connection closes.

## picture (coalition)
`{"type":"picture","version":1,"coalition":"blue","time":<mission s>,"theatre":"Syria","bullseye":{"x","z","lat","lon"},"units":[…],"weapons":[…]}`

`weapons`: missiles in flight, `[{"id","type","coalition","shooter","target","lat","lon","alt",
"hdg","spd","vs"}]`. The coalition's own missiles carry everything; an enemy missile is
included only while aimed at one of the coalition's units, with `type` and `shooter` null.
`id` is the server's own missile number (not a unit id); `shooter` and `target` are unit ids.
A missile missing from the next picture no longer exists. Coalition-private, not exported.

`carriers`: the coalition's carriers (DLL 0.4), `[{"id","type","windFrom","windMs","tacan",
"icls","link4","acls","radio"}]` — unit id and DCS type, the wind 20 m above the deck (from, true
degrees; m/s), and the beacons the mission's group tasks set (`tacan` "74X JCS", `icls` channel,
`link4` and `radio` MHz, `acls` on/off; null when not set). `events`: the coalition's landings
on carriers in the last minute (`S_EVENT_LAND` on a ship), `[{"id","kind":"trap","unit",
"carrier","time","x","z","cx","cz"}]` with DCS metres of aircraft and ship; a client keeps each
`id` once. Both coalition-private, not exported.

`bullseye`: the coalition's own bullseye (`coalition.getMainRefPoint`), read every picture
since scripts can move it; `null` if unavailable. Coalition-private like the rest of the picture.

Unit: `id, name, type, callsign, player, group, category (airplane|helicopter|ground|ship|structure),
coalition, detected, lat, lon, alt (m MSL), hdg (deg true), spd (m/s), vs (m/s), emitter`.
Own units carry everything. Radar contacts (`detected: true`) carry no name, callsign,
player or group, and `type` only once DCS has identified them.

## theatre (public)
`{"type":"theatre","id":"Syria","x0","z0","x1","z1","grid":{"n":21,"lat":[…],"lon":[…]},"magvar":{"lat0","lon0","step","nlat","nlon","deg":[…]},"towns":[[name,lat,lon],…]}`

Bounds are DCS map metres (x north, z east). `grid` is an n×n lat/lon control grid over
the bounds (row-major, x outer); x/z → lat/lon by bilinear interpolation.
`magvar`: magnetic variation in degrees, east positive, from DCS's own model
(`magvar.get_mag_decl`, mission date) on a lat/lon grid covering the theatre — row i at
`lat0 + i·step`, column j at `lon0 + j·step`, row-major; bilinear between nodes. Magnetic =
true − variation. `null` if the server could not sample it (clients then show true bearings, marked "T").

## terrain (public)
`{"type":"terrain","id","x0","z0","step","nx","nz","heights":<base64 int16 LE>,"surfaces":<base64 uint8>}`

Row i at x = x0 + i·step, column j at z = z0 + j·step. Surfaces: 1 land, 2 shallow water,
3 water, 4 road, 5 runway.

## airfields (public)
`{"type":"airfields","airfields":[{"id","name","category" (0 airdrome, 1 helipad),"x","z","coalition",
"runways":[{"name","course","length","width","x","z"}],"parking":[[number,x,z,type],…]}]}`

From DCS `world.getAirbases()`, `Airbase:getRunways()`, `Airbase:getParking()`; ships are
excluded (they are units). DCS map metres; runway `course` is radians with the opposite sign
of the true heading. Parking `type` is DCS Term_Type (40 helicopter only, 68 hardened
shelter, 72/104 open, 100 small).

## foothold (public)
`{"type":"foothold","zones":[…],"links":[[from,to],…],"missions":[…],"credits":{"red","blue"}}`

Zone: `name, side (0 neutral, 1 red, 2 blue), x, z, r (trigger zone, DCS metres), active,
suspended (idle, units despawned), airbase, facility, logistic, built, max (owner's upgrade
slots), wpt, lat, lon` (zone centre, server-converted for the website). Hidden zones are not sent.
`missions`: Foothold's mission board — `{title, description, side, status: "active"|"available", strike}`
(texts as Foothold prints them). `credits`: each side's Foothold credits. Sent when zones, links or
missions change; a change in credits alone at most once a minute.
