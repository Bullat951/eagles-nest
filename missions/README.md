# DCB test mission

Save as `missions\DCB-Test.miz` (the server's mission list points here).
Map: **Caucasus** (installed). Keep it small; it is a test bench, not a scenario.

## Contents

**Blue**
- 1× EWR (e.g. 1L13 or 55G6) near Kutaisi, so the coalition has a ground radar picture.
- 1× E-3A AWACS orbiting west of Kutaisi, ~30,000 ft.
- 1× CVN (Stennis or similar) in the Black Sea off Batumi, sailing 25 kts into the wind.
- Client slots (skill: **Client**): 2× F/A-18C on the carrier, 2× F-16C at Kutaisi, 1× A-10C II at Senaki, 1× helo (if owned) on a FARP.
- 1× FARP near Senaki with ground units.

**Red**
- 1× EWR near Nalchik/Mozdok.
- 1× SA-6 or SA-11 site between Gori and Tskhinvali (search radar on, so we can derive RWR).
- 2× AI MiG-29 on a CAP racetrack north of the mountains (so there are bogeys to call).
- 1× Red client slot (e.g. Su-25T) at Mineralnye Vody, to test fog of war from the other side.

**Neutral / misc**
- 1–2× AI transport aircraft on a route landing at Kutaisi (approach traffic for ATC).

## Notes
- Mission start: morning, clear weather, wind ~10 kts.
- Leave unit names readable (e.g. `BLUE-EWR-1`, `RED-SA11-SR`) — easier to debug.
- No scripts needed inside the mission; DCB injects from the server hook.
