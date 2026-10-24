-- Eagle's Nest (DCB) server settings: Saved Games\<server>\Config\DCB.lua. On the first start
-- the server makes DCB.lua from DCB.example.lua with a random password for each side; change
-- them here if you like (restart the server after a change). The password a controller logs
-- in with decides their coalition. An empty password keeps that side's logins closed.

blue_password = ""   -- the password blue controllers log in with
red_password = ""    -- the one for red, different from blue's

port = 10309          -- TCP port controllers connect to (open it in the firewall)
interval = 1.0        -- seconds between pictures (1 is right for most servers)
terrain_step = 500    -- metres between terrain samples for maps and line of sight (sampled once, cached)

-- Enemy ground units (SAMs, vehicles) on the controllers' scopes when own radars detect them:
-- "auto" = only in missions dated enemy_ground_year or later (modern AWACS/JSTARS era), true =
-- always, false = never. Without them controllers find SAMs by RWR triangulation with pilots.
enemy_ground = "auto"
enemy_ground_year = 2012

-- RWR / ESM picture: what each own aircraft's warning receiver (and each ship's or ground radar's
-- ESM) hears, computed on the server every second and sent to that coalition only.
rwr = true

-- Identification of radar contacts: "auto" = a contact shows its type once DCS identifies it;
-- "manual" = never: controllers identify contacts themselves (RWR emitters, right-click →
-- Identify as…), shared with the coalition's other controllers.
identification = "auto"

-- NCTR (non-cooperative target recognition) by your AI radars on request: JEM, HRRP, ISAR and
-- rotor modulation only in missions dated this year or later (size class always).
nctr_year = 1985

-- Analytics and website export: local files in Saved Games\<server>\DCB (nothing is sent anywhere).
analytics = true      -- DCB\analytics\*.jsonl: every mission session's events (players as keyed ids + names)
export = true         -- DCB\export\: state.json (zones, missions, credits, scores) + events.jsonl for a website
