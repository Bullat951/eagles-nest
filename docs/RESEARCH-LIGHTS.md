# Research — switching airfield and carrier lights from Eagle's Nest

Question (user, 2026-10-22): can a controller turn on an airfield's or a carrier's lights from
Eagle's Nest, so pilots do not have to call the game's ATC for it?

Status: **carriers verified in multiplayer (2026-10-22); airfields: lit at night by themselves at
Akrotiri, no control found; not built.**
Nothing was changed in the code; the test server runs a night copy of the mission
(`_downloads\foothold\Foothold_SY_extended_5.3.5_NIGHT_2130.miz`, start 21:30; the previous
`serverSettings.lua` is kept as `serverSettings.lua.before-night`).

## Carriers — a trigger action exists

- DCS's Mission Editor has the trigger action **SET CARRIER LIGHTING MODE**
  (`MissionEditor\modules\me_trigrules.lua`): `a_set_carrier_illumination_mode`, fields = the
  carrier (unit) and `lightsMode`: **−2 OFF, −1 AUTO, 0 NAVIGATION, 1 AC LAUNCH, 2 AC RECOVERY**.
- On the dedicated test server (DCS 2.9.x, Foothold SY 5.3.5) the function **exists in the
  mission (trigger) env** — the same env `lua/foothold.lua` is injected into via
  `net.dostring_in("mission", …)`. Called from the hook as
  `net.dostring_in("mission", "return pcall(a_set_carrier_illumination_mode, 445, 2)")` for
  CVN-72 (id 445) and CVN-74 Stennis (id 4289): **no error** (`true nil`).
- **Verified in multiplayer (2026-10-22, 20:17–20:20 local).** Test server on a night copy of
  Foothold (start 21:30), the user in a client near the carriers watching, the hook calling
  `net.dostring_in("mission", "return tostring(pcall(a_set_carrier_illumination_mode, id, mode))")`
  through a dev probe. CVN-72 (Supercarrier, id 445): 2 AC RECOVERY → recovery lights on;
  0 NAVIGATION → navigation lights only; −2 OFF → dark; −1 AUTO → lights back on; 1 AC LAUNCH →
  changed. CVN-74 Stennis (old model, id 4289): 2 → changed as well. So the forum claim that the
  script call does nothing on a dedicated server does not hold here; the ME-trigger fallback is
  not needed. The Mission Editor lists exactly these five modes (`me_trigrules.lua` l. 720–727).
  Not tested: Tarawa/Invincible (helicopter carriers), Kuznetsov, whether another client sees
  the change (only the user's client was there), whether the mode survives AUTO logic later.
- Earlier, without a pilot: the carriers' draw arguments 0–1200 showed only animation changes
  (args 1, 2, 3, 65, 314 — radars/flags, not lights), so the effect cannot be read server-side.
- Forum reports (ED forums block automated reading; titles and snippets only): the script
  command reportedly "only works in SP, nothing happens on a dedicated server" (bug thread,
  2025), while the ME trigger itself works in MP; a 2024 wish-list asks for it as a proper
  scripting function. Supercarrier's Air Boss slot can also set the lights. "OFF" takes ~12 s.
  Old Stennis lights are tied to the player's "Inbound" radio call.
- **To build:** command `carrier_lights` {carrier, mode −2…2} (own coalition's carriers only,
  rate-limited, logged, run in the mission env through a fixed function like the other
  commands) and five buttons (OFF / AUTO / NAV / LAUNCH / RECOVERY) in the ATC tab's Carrier
  view. DCS does not report the current mode, so the view can only show the last one set.

## Airfields — no scripting control found

**Tested 2026-10-22, 20:23–20:25 local, with the user parked at Akrotiri (A-10C) at 21:30
mission time:**
- Akrotiri's runway/taxiway lights were **already on at night from mission start**, before any
  ATC traffic — so there was nothing to switch on.
- `Airbase:getCommunicator()` works in the server env (methods: checkCommunicator,
  countTransivers, getFrequenciesModulations, getFrequency, getModulation, hasTransiver,
  isReadyToReceive, sendMessage, sendRawMessage); the `Message` table is there too
  (`wMsgLeaderInbound` = 4105, `wMsgLeaderRequestLanding` = 4107). The field's own communicator
  "sending" Inbound returned without error and changed nothing visible.
- `Airbase:setRadioSilentMode(true)` (official) silenced ATC but **the lights stayed on**: they
  do not follow ATC here. Restored with `setRadioSilentMode(false)` at 20:24:53.
- A player unit has `getCommunicator()` as well, so a script could send "Inbound" in a player's
  name (step 2, not run: not needed while the lights are on anyway, and ATC would answer that
  player on the radio).
- **Conclusion:** at least at Akrotiri on Syria in DCS 2.9, airfield lights are on at night by
  themselves; no script function was found to switch them off or on. Building an airfield
  lights control is not worth it unless a field turns out to stay dark at night — then repeat
  step 2 there with a pilot watching. Untested: other fields and maps.

- No API: `Airbase` methods on the server are autoCapture, getCommunicator, getRadioSilentMode /
  setRadioSilentMode, getRunways, getParking, getWarehouse, … — nothing for lights.
  `trigger.action` has only `illuminationBomb`; the mission env's `a_*` functions have
  `a_set_ATC_silent_mode` but nothing for runway lights.
- DCS turns a field's lights on as part of the ATC dialog (players report: after "Inbound" /
  "Request landing", sometimes only on the second call; some fields/maps light by time of day).
- **Possible route (undocumented):** `Airbase:getCommunicator()` returns the field's ATC
  communicator with `sendMessage` and `sendRawMessage`. DCS's own radio menu sends a pilot's
  calls as `pComm:sendRawMessage(command, parameters)`
  (`Scripts\UI\RadioCommandDialogPanel\RadioCommandDialogsPanel.lua` l.150) with `Message.*`
  ids (e.g. `Message.wMsgLeaderInboundStraight`, `wMsgLeaderAbortInbound` in
  `Config\Common\ATC2.lua`). There the *player's* communicator is the sender and the ATC the
  receiver; whether a script can make ATC "receive" an inbound call (and switch lights) is
  unknown — the parameters, the `Message` table's availability in the mission env, and the
  side effects (ATC messages heard by players, a phantom aircraft in ATC's queue) all need
  probing.
- Forum workaround: spawn an AI aircraft tasked to land at the field at night; its ATC traffic
  turns the lights on (costs a spawn and a parking spot; hacky).
- Recommendation: carriers first (an official function exists); airfields only after a short
  probe of `getCommunicator():sendRawMessage` with a pilot watching, and drop it if it needs
  undocumented internals that may break with DCS updates.

## Sources

- [Regarding the "SET CARRIER LIGHT MODE" trigger — ED Forums](https://forum.dcs.world/topic/339358-regarding-the-set-carrier-light-mode-trigger/)
- [Cannot control carrier lights through script — ED Forums](https://forum.dcs.world/topic/379569-cannot-control-carrier-lights-through-script)
- [SET_CARRIER_LIGHTING_MODE mission scripting function — ED Forums wish list](https://forum.dcs.world/topic/357684-set_carrier_lighting_mode-mission-scripting-function/)
- [How to Activate Supercarrier Landing Area Lights at Night? — ED Forums](https://forum.dcs.world/topic/237275-how-to-activate-supercarrier-landing-area-lights-at-night/)
- [Any editing script or trick to turn on airport lights at night? — ED Forums](https://forum.dcs.world/topic/197742-any-editing-script-or-trick-to-turn-on-airport-lights-at-night/)
- [How do you turn on runway lights? — ED Forums](https://forum.dcs.world/topic/175102-how-do-you-turn-on-runway-lights/)
- [MOOSE Ops.Airboss](https://flightcontrol-master.github.io/MOOSE_DOCS_DEVELOP/Documentation/Ops.Airboss.html)
