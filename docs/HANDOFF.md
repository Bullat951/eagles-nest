# Handoff — 2026-10-22 (late evening)

Project: **Eagle's Nest** (working name; code, files and protocol still "DCB"), repo
`D:\Claude\DCB`, public on GitHub: https://github.com/Bullat951/eagles-nest (MIT). Read
[PLAN.md](PLAN.md) (phases, decisions), [PROTOCOL.md](PROTOCOL.md), [MANUAL.md](MANUAL.md) and
[DEVELOPMENT.md](DEVELOPMENT.md) first.

## State

- `main` = `origin/main` at `128d7f2` (license, front page, manual, screenshots). Release
  **v0.6.0** on GitHub with `EaglesNest-Server-0.6.0.zip` and `EaglesNest-Client-0.6.0.zip`.
- Uncommitted: `docs/RESEARCH-LIGHTS.md` (new) and this file. The user decides when to commit
  and push — never commit or push unasked.
- Build: `build.ps1` (closes, builds, runs 176 tests, builds dcb.dll 0.6.0, relaunches the
  client). Release: `server\package.ps1`. Test server: `Saved Games\DCS.server`, Foothold SY
  5.3.5 (mission dated 2025), DLL 0.6.0 deployed, `dev_probe = true` (test instance only).
- Real controller passwords live only in `Saved Games\DCS.server\Config\DCB.lua`;
  `server/config/DCB.lua` is git-ignored. Never print or commit them. Git history was restarted
  once because of them.

## Done this session (all in PLAN.md / docs)

1. Server load measured (≈0.4 % of a core, 22 MB, 16–39 KB/s per controller, 12 MB terrain at
   login) — in the conversation only; numbers are in MANUAL.md §12.
2. RWR triangulation + threats + F10 marks both ways (PLAN.md "RWR triangulation and F10
   marks"); enemy ground units by mission year (`enemy_ground`).
3. DCS install auto-search + first-start dialog (`DcsLocator`, `DcsLocationWindow`).
4. Copy-only server install: hook creates `Config\DCB.lua` with random passwords; Syria terrain
   grid in the zip; packages renamed `EaglesNest-*`.
5. Git: fresh history, private → public repo, v0.6.0 release, MIT LICENSE,
   THIRD-PARTY-NOTICES.md, README front page with screenshots (`docs/images`, made with snapshot
   mode), MANUAL.md, DEVELOPMENT.md.

## In progress — research: lights

[RESEARCH-LIGHTS.md](RESEARCH-LIGHTS.md): carriers have an official trigger action
(`a_set_carrier_illumination_mode`, callable in the mission env on the dedicated server without
error; effect in MP unverified — needs the user flying near CVN-72 at night while a probe sets
modes). Airfields: no API; only an undocumented route via `Airbase:getCommunicator():sendRawMessage`
or spawning an AI lander. Report these findings to the user and agree on the in-game test.

## Open, waiting for the user

- **Map packs** (pre-made DCS map data so controllers extract nothing): options were measured —
  vector data ~60 MB/terrain, + overview tiles ~650 MB/terrain, + charts ~1.3–1.5 GB; satellite
  (53 GB) not shippable; distribute as GitHub Release assets (not in git); terrains on this PC:
  Caucasus, Persian Gulf, Syria. User has not chosen the contents yet.
- Steam detection of DCS untested (no Steam DCS here).
- A clean install from the release zip on a fresh server not tested end to end.
- Website export paused by the user; licensing done (MIT).
- Older: Listen traffic with a real player, JUMP / Set CH6 by hand, carrier/airfield queue with
  departures.

## Environment facts that bite

- Dev probe: write `Saved Games\DCS.server\DCB\probe.lua` (UTF-8 **without BOM**; first line
  `-- env: hook` for the hook's own env, else mission `server` env; `net.dostring_in("mission", …)`
  reaches the trigger env where `a_*` trigger functions and Foothold live). Output in
  `probe.out`; the hook deletes the probe after running it.
- Collector hot-reload without restarting DCS: copy `lua\collector.lua` into
  `Mods\Services\DCB\Scripts\` and re-run the changed functions through a probe.
- `deploy.ps1` restarts the DCS server — the user may be playing on it; ask or hot-patch.
- Snapshots: `Dcb.Client.exe --snapshot …` (DEVELOPMENT.md); never send SRS commands; window
  captures have transparent strips at the bottom/right (crop them); the RWR clock overlay is
  re-drawn in captures.
- The ED forums answer 403 to automated fetches; DCS's own Lua (Mission Editor, radio menu) in
  `E:\DCS World` is the reliable source.
- In `MapPanel`, `Nav` is the map navigator: use `Core.Nav` for the navigation maths.
