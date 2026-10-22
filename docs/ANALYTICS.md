# Analytics and website export

What the DCB hook records on the server and what it exports for a website (built
2026-10-22). Both are local files in the server's Saved Games folder; nothing is sent anywhere.
Switches in `Config\DCB.lua`: `analytics = true`, `export = true` (both on by default).

## Player identity

Players appear as `{"id": "0473dbaff3090b15", "name": "Viper 1-1"}`.

- `id` = the first 16 hex digits of HMAC-SHA256(server key, UCID). The key is made once on the
  first start (`DCB\analytics.key`, 32 random bytes from Windows' CNG) and must stay on the server.
- The id is stable across name changes and identical every session; without the key it cannot be
  turned back into the UCID. **Raw UCIDs are never written** (Foothold's own UCID-keyed scores
  are re-keyed the same way before they reach a file).
- Checked: the DLL inside DCS and .NET's `HMACSHA256` give the same id for the same UCID.
- Keep a backup of `analytics.key`: a new key means new ids (old data can no longer be joined).

## Analytics log — `DCB\analytics\<UTC start>_<mission>.jsonl`

One file per mission session, one JSON object per line, every line with `utc` (wall clock) and
mission events also with `t` (mission seconds). Kinds:

| kind | fields | source |
|---|---|---|
| `session_start` / `session_end` | `mission`, `dcb` | hook |
| `connect` / `disconnect` | `player` | hook (`onPlayerConnect/Disconnect`) |
| `slot` | `player`, `side` (0/1/2), `unit` (aircraft type) | hook (`onPlayerChangeSlot`) |
| `spawn`, `takeoff`, `landing`, `crash`, `eject`, `pilot_dead`, `leave_unit` | `unit` {type, side, player, category}; `place` for takeoff/landing (airfield or ship name) | mission events, players only |
| `kill` | `unit` (killer), `target` {type, side, player, category}, `weapon`, `friendly` | mission events; kept when a player is on either side |
| `zone_captured` | `zone`, `from`, `to` (sides) | Foothold, compared every 10 s |
| `mission_started` / `mission_ended` | `title`, `side` | Foothold mission board, every 10 s |
| `foothold_stats` | `players`: [{id, name, rank, credits, stats{Points, Air, Helo, SAM, Ground Units, Infantry, Ship, Structure, Deaths, Zone capture, Zone upgrade, Pilot Rescue, Refueling, Flight time, Points spent, …}}] | Foothold's own scoring, every 5 minutes |

Kill `category`: air, helo, sam (SAM/AAA), ground, infantry, ship, structure, scenery.
No positions are kept. AI-against-AI kills are not kept (volume). Foothold's mission board does
not say whether a mission succeeded or failed — `mission_ended` covers both.

Example lines (from the test server):

```json
{"utc":"2026-10-22T14:46:38Z","kind":"session_start","mission":"Foothold_SY_extended_5.3.5_…","dcb":"0.5.0"}
{"utc":"2026-10-22T14:49:40Z","kind":"kill","t":1.0,"unit":{"type":"FA-18C_hornet","side":"blue","player":{"id":"0473dbaff3090b15","name":"Viper 1-1"},"category":"air"},"target":{"type":"MiG-29S","side":"red","player":null,"category":"air"},"weapon":"AIM_120C","friendly":false}
```

Scores and achievements are computed **later** from these files (rules like "first trap",
"5 air kills in one sortie", "100 sorties", hours per type), so rules can change without losing
history. Files are small (a busy evening: a few MB); they are kept until deleted by hand.

## Website export — `DCB\export\`

The public projection for a website (docs/RESEARCH-WEBSITE.md): **no units, no positions of
units, no ids**.

- `state.json`, checked every 10 s and written only when its content changes (`updated` = the
  time of that change); a change in credits alone (Foothold's income ticks every 10 s) is written at
  most once a minute, zones and missions at once:
  `{"updated","mission","foothold":{zones:[{name, side, lat, lon, r, active, suspended, airbase,
  built, max, …}], links, missions:[{title, description, side, status: active|available, strike}],
  credits:{red, blue}}, "players":[{name, rank, credits, stats{…}}]}`.
- `events.jsonl`, appended: the public kinds (kill, takeoff, landing, crash, eject,
  zone_captured, mission_started, mission_ended) with names only.

## Cost (measured 2026-10-22, test server, Foothold Syria, no players)

Hook timing log (`timing:` lines in dcs.log, once a minute), three minutes:

| Part | Per minute | Average per call | Peak |
|---|---|---|---|
| Analytics + website export (drain, enrich, append, `state.json` every 10 s) | 13–25 ms | 0.2–0.4 ms | 4.5–5.7 ms (the `state.json` write) |
| Foothold tick (zones + new mission board + events; works every 10 s) | 7–16 ms | 0.1–0.3 ms | 1.6–7.1 ms |
| For comparison: DCB's picture collection | 130–220 ms | 2.2–3.7 ms | 10–25 ms |

So the export adds about 20–40 ms a minute (≈ 0.05 % of a core), 10–20 % on top of DCB's own
cost. Disk: the session log grew 841 bytes in 4 minutes without players (each event is
150–350 bytes; a scores snapshot ~300 bytes per player every 5 minutes); `state.json` stays at
~17 KB, rewritten every 10 s. No network traffic (local files); the controllers' `foothold`
message grew from 14 to 16 KB and is still sent only when it changes. Since then `state.json` is
written only when its content changes (see below).

After "write only on change" (same server, four minutes): the only part that changed every 10 s
was Foothold's credits (its income ticks), so a change in credits alone is now written — and sent
to controllers — at most once a minute, zones and missions at once. `state.json` writes went from
24 to 8 in four minutes (once a minute, plus missions starting); analytics + export 2–8 ms a
minute in quiet minutes (was 13–25), 21 ms in a minute with several missions starting; the
controllers' 16 KB `foothold` message went from every 10 s to on change + once a minute
(≈ 80 KB/min less per controller).

Reading a session file while DCS has it open: Windows lists its size as 0 until it is closed,
and a reader must open it with write-sharing allowed (e.g. .NET `FileShare.ReadWrite`).

## Real-time demo

`node tools/web-demo/server.js [export dir] [port]` (Node built-ins only; default the test
server's `DCB\export`, port 8099, bound to 127.0.0.1) serves a page that shows the front line
(zones by owner from lat/lon), credits, the mission board per side, the Foothold top players and
a live event feed. The server pushes `state.json` changes and new event lines to the page with
server-sent events within about a second. The real website would read the same two files through
the sidecar described in RESEARCH-WEBSITE.md.
