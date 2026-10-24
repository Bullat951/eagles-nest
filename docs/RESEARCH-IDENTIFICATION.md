# Research — layered identification for the operator (RWR / ESM, NCTR, missiles)

Request (user, 2026-10-23): instead of contacts arriving with their type, the operator works
for it in layers — (1) the RWR / ESM picture of each own jet, radar or ship, matched to radar
contacts and assigned by the operator; (2) where no RWR evidence exists, NCTR attempts, each
method with its own range and conditions; (3) missiles are not missiles at first: a track is
classified as a missile only above Mach 2.5 or when NCTR finds a small target; an unidentified
track above Mach 2.5 becomes a missile, raises a warning, and Space jumps to it.

Status: **phase 1 built** (server-side RWR / ESM, `rwr` channel; PROTOCOL.md); the rest is
design.

## Today
- Contacts: `collector.lua` asks each own radar platform's controller for
  `getDetectedTargets(RADAR)`; a contact carries its DCS type as soon as DCS reports it as known
  (`detected.type`). The client draws threat rings from that type.
- Missiles (`weapons`): own coalition's always; enemy missiles whenever they are aimed at an own
  unit (omniscient — no sensor needed); type hidden for enemy missiles.
- Emitters: `emitter` flag on own units (Unit:getRadar()).

## What DCS offers (scripting API)
| API | Gives | Limits |
|---|---|---|
| `Controller:getDetectedTargets(Detection.RADAR/IRST/OPTIC/VISUAL/RWR/DLINK)` | per sensor kind: object, visible, type known, distance known | AI controllers only (players have none); RWR reported silent until something else detects; stale entries for dead units (forum) |
| `Controller:isTargetDetected(obj, …)` | the same per target, plus last position/velocity | as above |
| `Unit:getRadar()` | radar on?, tracked object | reported reliable for aircraft |
| `Unit:getSensors()` / `hasSensors(SensorType.RWR …)` | whether a unit carries RWR, radar, IRST, optics | — |
| events | shot (weapon, launcher, target), hit, kill | — |

**Consequence:** a per-platform RWR picture for players cannot come from DCS's RWR detection.
It can be computed on the server from what it knows for certain: every enemy emitter
(`getRadar`), what it tracks, launches (shot events), positions and line of sight
(`land.isVisible`). That is the same for AI and players and cannot be cheated from the client.

## Layered design (proposal)
**Layer 0 — raw radar.** Contacts keep position, altitude, speed, heading; **no type** unless a
later layer earns it (server setting, like `enemy_ground`: `identification = "manual"` or the
current `"auto"`).

**Layer 1 — RWR / ESM picture (server-computed).** For each own platform with an RWR (aircraft)
or ESM (ships, EWR/ground radars): every enemy emitter that is on, within its emission range of
the receiver and in line of sight →
`{ receiver, bearing ± error (type-dependent, e.g. ±5–15°), emitter label from an RWR library
("29", "SA-6", "S"), mode: search | track (tracking this receiver) | launch (missile guiding) }`.
Sent to the receiver's coalition only. Client: select an own aircraft → its RWR scope (bearing
ring, labels) and bearing lines on the map; the operator drags or right-clicks to **assign** an
RWR emitter to a radar contact (contact gets the label as its identity, shared with the
coalition's other controllers). The triangulation feature (bearing lines, fix) already does the
same for ground emitters by pilot call.

**Layer 2 — NCTR (server-computed on request).** Operator selects a contact → "NCTR". The server
checks, for each own radar platform tracking it, a capability table:
| Method | Range | Conditions | Result |
|---|---|---|---|
| JEM (jet engine modulation) | ≤ 30–40 NM for fighters, more for AWACS | target nose or tail within ±25–35°, radar tracking it | aircraft type (jets only) |
| HRR profile | ≤ 20 NM | broadside allowed, target above clutter | type class (fighter / bomber / transport / helo) |
| Size / RCS class | radar range | any | "small" (missile, drone), "medium", "large" |
| Rotor modulation | ≤ 20 NM | helicopter | helicopter |
After a dwell time (e.g. 5–15 s of continuous track meeting the conditions) the answer comes
with a confidence; outside conditions: "no result" with the reason (aspect, range, no track).

**Layer 3 — missiles.** Server tracks fast objects (weapons in flight) only where an own sensor
can see them (radar platform within range and LOS — small RCS, so shorter range than for
aircraft). They arrive as **unknown fast tracks**; classified as missile when speed > Mach 2.5
(local speed of sound from altitude) or NCTR "small" — then the client raises a warning
(banner, sound) and **Space** centres the map on the newest warning (repeat: next).

## To verify on the test server (dev probe)
- `getRadar()` tracked object for ground and ship radars (SA-6, SA-11, ships), and for AI vs
  player aircraft.
- Whether `getDetectedTargets` ever lists weapons (missiles) for radars.
- Cost of computing RWR pictures every second (emitters × receivers × LOS checks): LOS only
  for pairs within range, cached per second.

## User's decisions (2026-10-23)
- **RWR / ESM: computed on the server** — but measure the performance impact first.
- **NCTR: on request, for a selected area** (box on the map), with **a choice of NCTR methods**
  to try (JEM, HRR, size class, rotor…); each answers within its own range/aspect/dwell rules.
- **Missiles: only in own sensor coverage**, then classified by Mach 2.5 or NCTR "small";
  warning + Space jumps to it.
- **Server setting** `identification = "manual" | "auto"` (auto = today's behaviour).

## Performance estimate (to be measured before building)
Per second, receivers × emitters pairs: a busy Foothold server has ~20–40 own aircraft with an
RWR and ~30–80 enemy emitters on (SAM, EWR, ships, fighter radars) → ≤ 3 200 pairs; a distance
check removes most; `land.isVisible` (the only costly call) only for pairs within emission range,
estimated a few hundred per second. At an estimated 20–50 µs per LOS call that is ≲ 25 ms per
second on the sim thread — of the order of today's whole picture (2–4 ms per second average,
spikes 20–50 ms). Mitigations if needed: LOS cached per pair for 5 s (geometry changes slowly),
spread over frames, ground emitters' LOS to a receiver reused for its wingmen. **First step:
a probe on the test server measuring land.isVisible cost and the real pair counts** (needs
`dev_probe = true`, currently off).

## Measured (2026-10-23, test server, Foothold CA 5.3.5, no players)
Probe `tools/research/identification/probe_census.lua` (results beside it):
- `land.isVisible`: **4.5–7 µs per call** (7 600 and 2 250 repeated calls, pairs up to 200 km) —
  3–7× cheaper than estimated.
- Census of all units + sensors: 6–8 ms (one-off, not per second).
- At 8.5 min: red 3 aircraft, 1 helo, 196 ground (91 radar-capable, 5 on), 15 ships; blue 2
  aircraft, 1 helo, 23 ground, 5 ships. 259 receiver × emitter-on pairs, 45 within assumed range,
  36 with LOS. Foothold stays quiet without players (its traffic reacts to them), so a busy
  server needs players or spawned test traffic (`A-50 test` spawn used for that).
- Collector with this mission: 1.9 ms per run on average.
- No radar listed a weapon among detected targets (none in flight — still to check).
- Worst case for planning: 40 receivers × 80 emitters, all in range and LOS = 3 200 LOS checks
  ≈ 15–22 ms per full refresh; with a 5 s LOS cache per pair ≈ 3–5 ms per second.

## Phase 1 built (2026-10-23)
`DCB_rwr()` in `collector.lua`, called by the hook with every picture (`rwr = true` in DCB.lua),
published per side as `rwr` (format in PROTOCOL.md). Choices made:
- Receivers: own aircraft with an RWR (sensor data), own ships and ground units with a radar
  (ESM). Emitters: enemy units with a radar that is on (`getRadar`).
- Heard within 1.5× the emitter's own radar detection range (largest figure in `getSensors`,
  capped at 600 km; by category when DCS gives none), inside its scan sector (fighters ±60°,
  helicopters ±90°, AWACS / ships / ground all round), with LOS.
- LOS per pair kept 4–6 s (spread so pairs do not all expire together); each refresh draws a
  new bearing bias within ±err, so the error is a steady offset, not jitter.
- Mode: `track` when the emitter's `getRadar` tracked object is the receiver; `launch` when a
  live missile from the emitter's unit or group (SAM launcher fires, its radar guides) is aimed
  at the receiver. Active missiles as emitters: not yet.
- Emitters carry a key per unit (counting up per mission), not the unit id.

Measured on the test server (Foothold CA, blue AWACS / A-50 test, no players): 372 receivers
× emitters = 3 744 pairs per run, 528 in range and sector, 176 LOS calls on the first run;
per run 16 ms cold (sensor and type caches filling, once per mission), ~4 ms after a rescan,
0.8–2 ms otherwise. Lines: blue ~13 KB, red ~14 KB a second (red's many ground radars each
hear the E-3). Not seen yet: `track` and `launch` (no engagement during the test).
Deployed (hook running it every second, fresh mission): first minute `rwr 60×, avg 3.04 ms,
max 76.3 ms` (the max is the cold first run at mission start), second minute `avg 1.81 ms,
max 3.1 ms` (~82 receivers, ~700 pairs, ~95 in range, ~17 LOS calls per run). Blue and red
clients each get their own line once a second (2–7 KB).

## Phase 2 started (2026-10-23): RWR scope in the client
The R key (and *RWR…* on an aircraft's menu) opens the scope over the own unit under the
pointer or selected: the `rwr` picture's emitters at their bearings (grid → true with the
theatre's convergence), radius from `level`, colour by mode, a bar over airborne ones. A click
on an emitter draws its bearing line (wedge = its `err`); a click elsewhere on the scope draws
a line at that azimuth (±10°, replacing the 12-button clock for pilots' calls). Lines keep their
own error and the fix weights them by it (Triangulation.Solve); fixes stay per unit (user's
choice). Scope sizes with the mouse wheel; overlapping labels are decluttered (ScopeLayout).

## Phases 2–3 built (2026-10-23): assigning identities, manual mode
- Server setting `identification = "auto" | "manual"` (DCB.lua); manual: contacts never carry
  `type`. Test server set to manual.
- Command `identify` (DLL 0.8): unit must be an existing enemy unit; label ≤ 24 safe chars;
  empty clears. The hook keeps identities per coalition, drops gone units every 10 s, resets on
  mission change, and publishes `ids` (with the mode) to that coalition only.
- Client: right-click a contact → *Identify as*: suggestions from RWR correlation (emitters of
  the contact's kind whose true bearing from each receiver passes within err + 2° of it,
  grouped by label, most receivers first), all labels by group, or a typed name; *Clear
  identity*. The label shows where the type would be (data blocks, Objects, ATC views); a ground
  or ship identity naming a SAM gives the threat ring (airborne labels never: "15" = F-15).
- Verified on the test server: manual mode sends contacts without type; identify "50" on an
  A-50 accepted, bad label refused by the DLL, own unit refused; the `ids` line reached blue
  at once and on a new login, red saw none; the second A-50 got "50" suggested by 16 receivers
  at 3°; an identity set from the user's own client reached the snapshot client.
- Identified contacts are drawn hostile (red), unidentified unknown (yellow): IdentityOf uses
  the type or the identity.
- Placed threats shared among the coalition's controllers (user, 2026-10-23; bearing lines not:
  clutter): commands `threat_add` / `threat_remove`, `threats` channel. Verified: second blue
  login sees it, red does not and cannot remove it, bad type refused, remove reaches everyone.

## Phase 4 design: NCTR and jamming (user's decisions, 2026-10-24)
NCTR is computed only for AI radar platforms (the server knows what they track). Players doing
NCTR in their cockpit report the result to the operator, who enters it (Identify as).

| Method | Who can do it | Targets | Notes |
|---|---|---|---|
| JEM | jet and radar platforms that are not cold-war **and** mission dated after a year (both) | jets | nose or tail aspect, range-limited, dwell |
| HRRP | AWACS, F-15, F/A-18, F-16 only | aircraft | more aspects, shorter range |
| ISAR | (as JEM platforms) | ships; aircraft only while the target turns > 15° within 5 s | needs the target's motion |
| Size class | any radar | anything | coarse: small / medium / large (from the type's size) |
| Rotor | (as JEM platforms) | helicopters only | short range |

Kinematics and RWR/ESM: already there (layer 1, missile rule planned). IFF and VID: not wanted.

- **NCTR freezes the radar's tracks:** while a platform runs NCTR (dwell), contacts that only it
  holds stop updating (last position, shown frozen); contacts other radars also hold move on.
- **Jamming — DROPPED (user, 2026-10-24):** the probes below found that DCS does not expose
  jamming to scripts, and only DCS's own ECM was wanted. Original idea, kept for reference:
  a contact own AI radars detect with bearing but no range
  (`getDetectedTargets` distance unknown) is jammed — to be verified by probe. On the GCI scope:
  a yellow strobe (cone) from the radar along the bearing, the contact's range jumping, until
  **burn-through** (DCS gives range again) or the **operator** crosses strobes and places it.
  The server does not correlate strobes itself.

### Jamming probe (2026-10-24, test server): inconclusive
Su-34s spawned by script (one ECM always, one never, then a lone clean one) flying at the
radiating E-3A (AWACS_BLUE, AN/APY-1): its controller **never** listed any of them under
`Detection.RADAR` (`isTargetDetected(u, RADAR)` false, absent from `getDetectedTargets`) down to
42–64 NM, although `isTargetDetected(u)` (any sensor) was true within 10 s and the same E-3A
radar-tracked the mission's own Su-34s at 67–70 NM with `distance = true`. So the spawned test
aircraft behave differently for this radar, and nothing could be learned about ECM. The other
blue AWACS (E-2Cs, a second E-3A) had their radars off. Open: test with radars that track the
spawned jets (ships, ground EWR) or with mission-placed jammers. **User's decision
(2026-10-24): jamming only from DCS's own ECM — no server-simulated jammer table.**

### Jamming probe 2 (2026-10-24): DCS shows no jamming through the API
Receivers CVN-74 and CVN-72 (they do track spawned jets). Two Su-34s 40 km apart, immortal,
weapons hold, inbound from ~70 NM: "Jam test" `ECM_USING = ALWAYS_USE`, "Clean test" `NEVER_USE`.
Both first detected by radar at 62–63 NM by both carriers, both listed in `getDetectedTargets`
with `distance = true` and `type = true`, unchanged down to 28–34 NM. So for ship radars the
scripting API reports a jamming Su-34 exactly like a clean one: no bearing-only state, no later
detection, no burn-through point. Untested: other jammers (aircraft with ECM pods), SAM/EWR
receivers, and whether DCS ECM acts only on locks / missile guidance.

### Jamming probe 3 (2026-10-24, `tools/research/identification/probe_jam_matrix.lua`): none
Eight immortal jets, each alone, ECM always vs never: Su-34 and Tu-22M3 (red) against 5 blue
receivers (CVN-74, CVN-72, Tarawa, NASAMS, E-3), F-16C with ALQ-184 and F-4E with ALQ-131 (blue)
against 18 red receivers (five 55G6 EWRs, SAMs, A-50). Logged every 5 s per jet and receiver.
- `distance = false` never occurred, for any jet and receiver.
- Pods: ECM on and off identical (F-16 and F-4). Pods do not appear in getAmmo, so the loadout
  could not be confirmed.
- The jamming Su-34's reported position (isTargetDetected lastPos) drifted up to 24 km — but it
  grew 1.1 km per 5 s (its speed): a stale last fix while not painted, not jamming. The clean
  Tu-22 went stale the same way. Fresh fixes started at 114 NM for the jamming Su-34 and 113 NM
  for the clean one.
**Conclusion: through the scripting API, DCS shows no difference between a jamming and a clean
aircraft** (ships, AWACS, EWRs, SAMs). If DCS ECM acts at all, it acts inside its own sensor and
weapon logic (locks, missile guidance), which scripts cannot see. → Jamming feature dropped.

## Phase 4 NCTR built (2026-10-24)
Command `nctr` (DLL 0.8: box ≤ 400 km a side, methods), `DCB_nctrStart` / `DCB_nctr` in the
collector, `nctr` channel, setting `nctr_year` (1985). Contacts remember their source platforms;
the platforms taking part (AI, capable of a chosen method) are busy for the request and the
contacts only they hold are sent frozen. Dwell: JEM 8 s, HRRP 10, ISAR 6, size 3, rotor 6, without
a break; window 20 s; at most 3 running per side; results kept 120 s. Client: N + drag + method
picker, the running area with seconds left, answers under contacts and first in Identify as
(JEM family / type as the label; size only as a hint), FRZ in frozen data blocks.
Verified on the test server: hot-injected — red JEM "F-16 (F100/F110)" and "F-14 (TF30/F110)"
from an EWR, ISAR type on a turning F-16, HRRP refused (no capable red radar), blue JEM on
MiG-25s "range 61 NM (max 40)"; all 3 red contacts frozen while a request ran. Deployed — over
TCP as red on two spawned F-16s: request accepted, 20 updates, both frozen, size "medium", JEM
"aspect 137 deg", ISAR "not turning", rotor "not a helicopter"; bad box and upper-case method
refused by the DLL. Not yet tried in the user's window (N-box, picker, map drawing).

**Freezing by cone (user, 2026-10-24).** A radar doing NCTR still scans where it looks: the box
only picks the targets; for each radar taking part the server derives a cone from it covering
the targets it holds (+3°, at most 30° wide), recomputed every picture. Contacts inside a radar's
cone keep updating; contacts that only busy radars hold and that lie outside all their cones
freeze. A target outside a radar's capped cone gets "outside the radar's NCTR cone". The cones
go to the client in the `nctr` message and are drawn as wedges. Verified by snapshot: one target
NCTR'd, AWACS cone 93° ±3° to 90 NM drawn, contacts in it kept updating. Reported by the user:
two other contacts vanished for good after an NCTR on one (old rule); not reproduced — in every
server and client run all contacts stayed in the picture and the freeze ended on time.

**Cones only where something is possible; one reason; active methods (user, 2026-10-24).** A
radar gets a cone (and is busy) only for targets with an open method it may use within range —
once size class is done and the rest is out of range, its cone goes and it freezes nothing. A
failed method keeps one reason: the nearest miss among the radars that held the target, with
that radar's name ("AWACS_BLUE: range 128 NM (max 60)"; a list per radar was rejected as
clutter). JEM aspect stays ±30°. Each result carries `active` (a radar dwelling on it now); the
client writes those methods in flashing blue beside the contact.

**DCS merges close formations (observed 2026-10-24).** A spawned Su-34 pair 1.2 km apart, same
altitude, 37–42 NM from CVN-74 and CVN-72 with line of sight: both carriers detected the leader,
neither detected the wingman (`isTargetDetected` false for every sensor). So a tight pair shows
as one contact until it splits or comes close — like a real radar's resolution cell. Seen by the
user as "a jet vanished after NCTR"; NCTR was not the cause.

**Raid count (NCTR method `raid`, 2026-10-24).** How many aircraft share one contact's return:
the target plus aircraft of its side within 3 km and 600 m altitude that the requesting side
does not hold as contacts of their own. Modern radars (as JEM), any aspect, ≤ 30 NM (50 for
AWACS, ships, ground radars), 5 s dwell, aircraft only; a hint in Identify as, not an identity.
First version counted resolved wingmen too ("2 aircraft" on each of two separate contacts);
fixed to count only the unresolved ones.

## Server cost with everything on (2026-10-24, Foothold CA, no players)
15 minutes of the hook's once-a-minute timing lines, RWR, ids, threats and NCTR running: picture
(collect, now with contact sources and NCTR) avg 2.3 ms per run, RWR 2.0 ms (≈ 600 receiver ×
emitter pairs, ~70 in range, ~13 LOS calls per run), F10 marks 1.2 ms, Foothold 0.3 ms, NCTR
channel 0.1 ms, analytics 0.07 ms — about 6 ms of sim-thread time per second (0.6 %). Before
this work the same mission ran the picture at 1.9 ms and marks at 1.1 ms: the identification
features add about 2.5 ms per second, mostly RWR. Worst single picture 75 ms (during collector
re-injection by probe). Picture ~48 KB a second per side, `rwr` 3–14 KB.

## Phases
1. Measure (probe) → RWR/ESM computation in the collector, `rwr` channel per coalition
   (receiver → emitters with bearing, label, mode); timing logged like "collect:".
2. Client: RWR scope for the selected own aircraft/ship/radar, bearing lines; assign an emitter
   to a contact (identity shared with the coalition's controllers via the server).
3. Server setting `identification`; contacts without type in manual mode; threat rings only
   from identified contacts.
4. NCTR: area selection + method choice → server checks per contact over a dwell time → result
   or reason.
5. Missiles: coverage-limited weapon tracks, Mach 2.5 / NCTR rule, warning list, Space to jump.
