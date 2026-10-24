# Research — what the server can control (units, campaign, GCI, datalink, waypoints)

Request (user, 2026-10-24): what controls do we have on the server over units and the
campaign; can we drive the MiG-29A's Lazur (full-fidelity module) and guide it by hand from
GCI; can we control the datalink targets others see; can we push waypoints / DTC / flight-plan
updates to other players.

Status: **research only, nothing built.** Probed on the test server (Caucasus, Foothold CA 5.3.5,
DCS dedicated server) through the hook's dev probe; web sources listed at the end.

## The one rule that decides most answers
Each player's aircraft — avionics, radios, datalink pages, DTC, waypoints — is simulated **on
that player's own PC**. Server scripts run on the server: they can command **AI**, change the
**world** (spawn, destroy, airbases, warehouses, drawings) and **talk to players** (text, sound,
marks, F10 menus), but they cannot write into a remote player's cockpit. The cockpit trigger
actions DCS has (`a_cockpit_perform_clickable_action`, `a_set_command`, `a_set_failure`,
`a_cockpit_highlight…`) are training-mission tools that act on the *local* player only — on a
dedicated server, on nobody.

## Scripting environments on the server (enumerated)
| Env | How we reach it | What it has |
|---|---|---|
| hook (GUI) | our `DCB-hook.lua` | `net.*` (players, slots, `force_player_slot`, `kick`, chat to one player, mission list, `load_mission`, `dostring_in`), `DCS.*` (pause, mission, `spawnPlayer`, `getUnitProperty`…) |
| `server` (mission scripting) | `net.dostring_in("server")` — the collector | `coalition`, `Group`, `Unit`, `Controller`, `Airbase`, `trigger.action`, `missionCommands`, `world`, `land`, `timer`, `Spot`, plus DB helpers (`getSTN`, `convertUnitIdToSTN` — Link 16 numbers for the Mission Editor) and `io`/`lfs`/`os` |
| `mission` (trigger env) | `net.dostring_in("mission")`, `a_do_script` | every Mission Editor action `a_*` and condition `c_*` (we use `a_set_carrier_illumination_mode`), Foothold's `bc` |
| `export` | `net.dostring_in("export")` | `LoGetRoute` etc. — the *local* player's aircraft only (none on a dedicated server) |

`setFlightCurrentWP` (found in the server env) is not a player-route call: it is a counter in
`Scripts/rtsScripting.lua` for campaign CAP synchronisation.

## What we can control (confirmed APIs)
**AI units and groups** (`Controller`, `trigger.action`, `coalition`)
- Tasks: `setTask` / `pushTask` / `popTask` / `resetTask` — routes, orbit, CAP, attack a
  group / unit / point, escort, follow, refuel, land, RTB. `knowTarget(object)` makes an AI
  know a target (a way for GCI to hand a contact to AI fighters).
- Options: `setOption` — ROE, reaction to threat, radar use, ECM use, formation, bingo/RTB…
- Commands: `setCommand` — **EPLRS / datalink on or off** for AI groups, TACAN / ICLS beacons,
  frequency, invisible / immortal, start / stop.
- Group control: activate / deactivate, stop / continue moving, AI on / off, destroy, spawn
  (`coalition.addGroup`, dynamic groups); `enableEmission` (radar on / off) per unit or group.
- Mission Editor actions by name: `trigger.action.setAITask` / `pushAITask`.

**World and logistics**
- Airbases: `setCoalition`, `autoCapture`, `setRadioSilentMode` (ATC silent), warehouses
  (`getWarehouse`: add / remove aircraft, weapons, fuel — what players can spawn and load).
- Weather reads, `land` (heights, LOS, roads), explosions, smoke, flares, illumination.

**Talking to players** (all per unit / group / coalition)
- `outText…` and `outSound…` (an on-screen message or a sound for one pilot or flight).
- `radioTransmission` — a sound file transmitted on a DCS radio frequency (heard on the
  cockpit radio without SRS).
- F10 marks (`markToGroup` / `markToCoalition`) and drawings (`lineToAll`, `arrowToAll`,
  `circleToAll`, `textToAll`, `quadToAll`… with colours, per coalition).
- F10 radio menus per group (`missionCommands`) — pilots can send requests to the controllers.

**Server** (`net`): move a player to a slot, kick, chat to one player, change / restart the
mission, mission list.

**Campaign — Foothold** (`bc` in the trigger env, this version: ~900 methods). Among them:
`addFunds`, `credit` / `debit`, `getPlayerCredits`, `buyShopItem`, `requestFriendlySupplyMission`,
`requestCaptureMission`, `engageZone`, `engageSead`, `fireAtZone`, `jamRadarsAtZone`,
`smokeTargets` / `illumTargets` / `flareTargets`, `triggerRedMassAttack`, `increaseDifficulty` /
`decreaseDifficulty`, carrier movement (`_sendCarrierToNavigationArea`, `deployCarrierNavigationArea`),
`spawnCarrierRecoveryTanker`, `_setDynamicTankerRacetrack`, `launchPlayerGroundAttack`. These
are Foothold internals: names and arguments can change with each Foothold version.

## The specific questions
**MiG-29A GCI / Lazur — can a controller guide it by hand?** Not through its datalink, today.
- DCS models the MiG-29A's GCI with a ground unit, **`GCI_station_MiG29`** (MAZ-543 with the
  `GCI` attribute, `CoreMods/tech/TechWeaponPack`). According to ED, the station uses compatible
  surveillance radars within 250 km, picks the best three contacts and guides interceptors; the
  pilot sees director cues and target range / speed / altitude once the right channel and code
  are set. Lazur as a separate system is reportedly not modelled.
- The station's logic is inside the engine, not Lua. A script **can spawn and remove GCI
  stations** (tested: spawned near Krymsk, attribute `GCI`, removed) and switch their radars'
  emission, but there is **no API to choose the target or send commands** to the pilot.
- Untested (needs a MiG-29A pilot; the module is not installed here): whether the station
  follows what the coalition's radars detect closely enough that switching radars steers it.
- What works for manual GCI today: voice (SRS), text to that flight (`outTextForGroup`:
  "VECTOR 270, ANGELS 25, 450, TARGET 40 MILES"), a sound per command, and an F10 drawing
  of the vector for the coalition.

**Datalink targets others see.** Only indirectly.
- Link 16 / SADL pictures are built by each player's avionics from what DCS provides (AWACS
  surveillance, donors and team members set in the Mission Editor).
- The server can switch **EPLRS (datalink) on or off for AI groups** (`setCommand`; forum
  reports of crashes when toggled too fast), spawn or remove AWACS and datalink-capable AI, and
  hand targets to AI (`knowTarget`). It cannot inject or remove tracks in a player's datalink.

**Waypoints, DTC, flight plans for players.** No.
- DTC: set up in the Mission Editor / briefing on the player's side; no runtime API
  (`DCS.toggleDTC` only opens the local UI).
- Routes: tasks apply to AI. Forum reports: setting a client group's route by script is
  unreliable — partly in single player, not in multiplayer; FC3 cockpits do not update; the
  F10 map keeps old waypoints.
- What reaches a player instead: F10 marks / drawings per group (the pilot enters them; tools
  on the pilot's PC such as DCSTheWay import F10 points into some modules), text and voice.

## Ideas for Eagle's Nest (not decided)
- **GCI command panel:** per flight, send "vector / angels / speed / BRAA" as on-screen text
  (and a sound), with the intercept vector drawn on the coalition's F10 map — the closest thing
  to Lazur any aircraft can receive.
- **Control AI from the scope:** commit AI fighters to a contact (`AttackUnit`, `knowTarget`),
  ROE, RTB, orbit, tanker track.
- **MiG-29A GCI coverage:** place / remove `GCI_station_MiG29` and show its 250 km coverage.
- **Datalink for AI:** EPLRS on / off per AI group; AWACS on / off.
- **Foothold operations from the client:** request supply / capture missions, SEAD / CAS,
  carrier moves, tanker tracks — tied to the Foothold version.

## Sources
- [ED forum: MiG-29A mini-updates (GCI station: radars within 250 km, best three contacts)](https://forum.dcs.world/topic/378017-dcs-mig-29a-fulcrum-mini-updates/)
- [ED forum: MiG-29A Ground Control Intercept](https://forum.dcs.world/topic/379287-ground-control-intercept-gci/)
- [DCS: MiG-29A Fulcrum (module page)](https://www.digitalcombatsimulator.com/en/shop/modules/fulcrum/)
- [ED forum: enable EPLRS / Link 16 on a KC-135 (setCommand EPLRS)](https://forum.dcs.world/topic/293504-enable-eplrs-l16-to-the-kc-135-tanker)
- [DCS 2.9.16 changelog (EPLRS setCommand crash fix)](https://www.digitalcombatsimulator.com/es/news/changelog/release/2.9.16.10973/)
- [DCS news: Data Transfer Cartridge](https://digitalcombatsimulator.com/en/news/2025-04-11)
- [ED forum: FC3 cockpits do not update with scripted waypoints](https://forum.dcs.world/topic/302730-cockpits-in-fc3-aircraft-do-not-update-when-new-waypoints-are-set-through-mission-scripting)
- [ED forum: changing waypoints while running](https://forum.dcs.world/topic/160041-settingchangingupdating-waypoints-in-a-mission-while-its-running-using-lua/)
- [DCSTheWay (F10 points into aircraft, pilot-side)](https://github.com/okopanja/DCSTheWay)
