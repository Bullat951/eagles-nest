# Research — controlling units and Foothold from DCB

Status: research and proposal, nothing beyond tier 1 built (2026-10-22). Decisions marked
**open** are the user's (and the server group's). Builds on PLAN.md item 10 and the command path
in [PROTOCOL.md](PROTOCOL.md) (`command` / `command_result`).

## What exists

The command path: client → DLL (parses, whitelists names, checks fields, rate-limits 5/s and
30/min per connection, queues ≤ 100) → hook (runs a **fixed** mission-env function with the
values as `%q`/`%d` arguments; controller text is never executed) → result to that client;
every command and refusal logged to dcs.log with coalition and address. Tier 1 is built:
`message_unit` (on-screen text to one own player aircraft).

The same path carries everything below: each new command is a name in the DLL whitelist with
its field checks, plus one fixed Lua function that checks coalition and arguments again.

## What DCS scripting offers

Mission env (where the collector runs):

- Spawn: `coalition.addGroup`, `coalition.addStaticObject`.
- Groups: `Group.activate`, `trigger.action.activateGroup/deactivateGroup`,
  `trigger.action.setGroupAIOn/Off`, `Group.destroy`.
- AI: `Controller:setTask/pushTask/popTask` (orbit, race-track, attack group/zone, follow, RTB,
  land), `Controller:setCommand`, `Controller:setOption` (ROE, alarm state, radar use, reaction
  to threat, formation).
- Effects: `trigger.action.smoke`, `signalFlare`, `illuminationBomb`, `explosion`.
- Map and text: `trigger.action.markToAll/markToCoalition/markupToAll`, `removeMark`,
  `outTextForGroup/Unit/Coalition`, `outSound…`.
- Mission logic: `trigger.action.setUserFlag` (drives flag-based triggers).

Server (hook/GUI env): `net.send_chat`, `net.force_player_slot` (to spectators),
`net.kick`, `net.load_mission/load_next_mission`, `DCS.setPause`, the player list.

## What Foothold 5.3.5 offers

Foothold's `bc` (BattleCommander) and `mc` (MissionCommander) live in the trigger env; DCB
reaches them with `a_do_script` from the hook, as `lua/foothold.lua` already does. All functions
below were found in the mission's `zoneCommanderv2.lua` (line numbers for 5.3.5):

| Area | Function (line) | Does |
|---|---|---|
| Coalition | `bc:addFunds(coalition, ammount)` (17546) | credits for a side |
| | `bc:buyShopItem(coalition, id, alternateParams, buyerGroupId, buyerGroupObj)` (19558) | buys from Foothold's shop: CAP, CAS, SEAD, supply… |
| | `bc:registerShopItem`, `bc:printShopStatus` | shop contents |
| Zones | `bc:getZoneByName(name)` (24234) → ZoneCommander | |
| | `zone:capture(newside, silent)` (51844) | change owner |
| | `zone:upgrade(silent, …)` (52154) | add an upgrade |
| | `zone:MakeZoneBlue()` (46105), `MakeZoneRedAndUpgrade` | set side and fill |
| | `zone:suspend(idle)` (43801), `zone:resume(internal)` (43900) | despawn / respawn zone units |
| | `zone:killAll()` (51805), `markWithSmoke`, `displayStatus` | |
| Operations | `bc:requestCaptureMission(chosenZone, options)` (20947) | sends a capture convoy/helo |
| | `bc:requestFriendlySupplyMission(chosenZone, onDone)` (20820) | supply run |
| | `bc:triggerRedMassAttack(preferredTargetNames)` (31946) | red offensive |
| | `bc:increaseDifficulty()` (17447), `decreaseDifficulty()` | |
| | `bc:spawnNowBySide(side)` (27905) | spawn the side's queued groups now |
| | `bc:launchPlayerGroundAttack(targetZone, allowFarp)` (18606) | |
| | `bc:fireAtZone(tgtzone, groupname, precise, ammount, ammountPerTarget)` (22393), `startFiringAtZone` | artillery |
| | `bc:jamRadarsAtZone(groupname, zonename)` (22057) | |
| | `bc:smokeTargets(zoneObj)` (21121), `illumTargets`, `flareTargets` | target marking |
| Missions | `mc:getActiveStrikeMissions(side)` (92948), `mc:trackMission(params)` (93355), `mc:printMissions` | |
| State | `bc:getStateTable()` (23124), `bc:saveToDisk()` (40876) | changes persist through Foothold's 60 s save |
| Players | `bc:getPlayerCredits(pname, record)` (40723), `getPlayerRank`, `bc:addPlayerRankCredits(pname, amount, rec)` (40744) | |
| Zeus | `handleSpawnRequest(text, coord)` (Zeus.lua, a global) | spawns from a fixed list (SA-8…SA-19, Shilka, BMP-2, tank, CTLD cargo, JTAC…), area destroy; DCB calls it directly instead of through F10 mark text |

CTLD scripts are also present (troop/crate logistics) — not looked at in detail.

## Capability matrix

Tier 1 = any controller of a coalition (built path). Tier 2 = AI control by a coalition's
controllers. Tier 3 = admin (separate password, not a coalition's).

| Command | API | Tier | Coalition rule | Risk |
|---|---|---|---|---|
| Text to a player / group / coalition | `outTextForUnit/Group/Coalition` | 1 | own only | low (built for units) |
| F10 mark / line / circle | `markToCoalition`, `markupToAll` (own coalition id) | 1 | own coalition's marks | low |
| Smoke / flare / illumination at a point | `trigger.action.smoke/signalFlare/illuminationBomb` | 1 | near own units or zones (**open**) | low, visible to all |
| Orbit / race-track at a point | `Controller:pushTask` orbit | 2 | own AI group | medium: AI behaviour |
| Vector to a point / RTB / land | `setTask` mission route, RTB | 2 | own AI group | medium |
| Weapons hold / return fire / free | `setOption` ROE | 2 | own AI group | medium |
| SAM emission control, alarm state | `setOption` alarm state, radar use | 2 | own ground group | medium |
| Activate a late-activation group | `Group.activate` | 2 | own group | medium: changes the mission |
| Foothold: capture or supply mission to a zone | `bc:requestCaptureMission`, `requestFriendlySupplyMission` | 2 | own side's request, target allowed by Foothold | medium: uses Foothold's own logic |
| Foothold: buy a shop item (CAP, CAS…) | `bc:buyShopItem` | 2 | own side, costs credits | medium: spends the side's credits |
| Foothold: smoke/illuminate targets in a zone | `bc:smokeTargets/illumTargets` | 2 | zones the side may attack | low |
| JTAC (Zeus list) at a point | `handleSpawnRequest("-create jtac", coord)` | 2 (**open**) | own side | medium: spawns units |
| Spawn from the Zeus list / destroy area | `handleSpawnRequest` | 3 | — | high: changes the battle |
| Capture / upgrade / suspend a zone | `zone:capture/upgrade/suspend/resume` | 3 | — | high, persists |
| Funds, difficulty, red mass attack | `bc:addFunds`, `increase/decreaseDifficulty`, `triggerRedMassAttack` | 3 | — | high |
| Kick / to spectators / chat as server | `net.kick`, `net.force_player_slot`, `net.send_chat` | 3 | — | high: players |
| Pause, load next mission | `DCS.setPause`, `net.load_next_mission` | 3 | — | high: everyone |

## Safety model (same as tier 1, extended)

1. **Whitelist only.** Each command is a fixed name in the DLL with typed, range-checked fields
   (ids, coordinates inside the theatre, enum values); anything else is refused and logged.
2. **Fixed functions.** The hook calls one fixed Lua function per command with `%q`/`%d`
   arguments; no client text is ever run as Lua. Foothold calls go through one DCB adapter.
3. **Coalition checks twice:** the DLL tags the command with the login's coalition; the Lua
   function checks the target group/unit/zone belongs to (or may be attacked by) that side.
4. **Roles.** Tier 2 needs a coalition login; tier 3 a separate admin password in `DCB.lua`
   (not a coalition's), and the DLL refuses tier-3 names on coalition logins.
5. **Rate limits** per connection (existing) plus per command kind (e.g. one capture request per
   zone per 5 minutes).
6. **Audit.** Every command, its arguments, who (address, coalition/admin) and the outcome in
   dcs.log; optionally a DCB audit file and an in-game chat line for tier-3 actions.
7. **Confirmation in the client** for anything that spends credits, changes zones or affects
   players.
8. **Persistence.** Foothold saves its state every 60 s; tier-3 zone changes therefore stick —
   the confirmation says so.

## Foothold adapter (version risk)

Foothold's API is internal and changes between versions (5.3.5 now). DCB keeps one adapter
(`lua/foothold.lua` side): on injection it checks each function exists (`type(bc.addFunds) ==
"function"`, …) and reports the available set to the client, which only offers what is there.
A Foothold update then hides a button instead of breaking a command.

## UI concept

- **Right-click an own AI group** on a map: Orbit here · Vector to… (click the point) · RTB ·
  Weapons hold / return fire / free · (SAM) Radar on/off, alarm state.
- **Foothold zone menu** (right-click a zone): status · Request capture mission · Request supply ·
  Smoke/illuminate targets (tier 2); Capture for… · Upgrade · Suspend/Resume (tier 3, admin).
- **Admin panel** (tier 3 login only): funds, difficulty, red offensive, spawn from the Zeus
  list at a map point, kick / to spectators, pause, next mission; an audit list of recent
  commands.
- Every command shows its result (`command_result`) and is greyed out while refused by role.

## Open questions for the user and the server group

1. Who gets tier 2 (all controllers of a side, or named ones with their own password)?
2. Who gets tier 3, and should tier-3 actions be announced in game chat?
3. Which Foothold actions are acceptable for controllers at all (spending a side's credits,
   requesting capture missions, Zeus spawns, JTACs)?
4. Should smoke/flares be limited to near own units/zones?
5. Is an audit file outside dcs.log wanted (for the group's admins)?

## Next steps once answered

1. Roles in the DLL (admin password, tier per command name).
2. Tier 2 AI commands (orbit, vector, RTB, ROE, EMCON) with the right-click menu.
3. Foothold adapter with feature detection; zone menu (capture/supply requests).
4. Admin panel and tier-3 commands.
