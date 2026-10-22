-- DCB server settings. Rename this file to DCB.lua (in Saved Games\<server>\Config) and set
-- the two passwords. The password a controller logs in with decides their coalition.
-- An empty password keeps that side's logins closed.

blue_password = ""   -- e.g. a long random phrase, different from red's
red_password = ""

port = 10309          -- TCP port controllers connect to (open it in the firewall)
interval = 1.0        -- seconds between pictures (1 is right for most servers)
terrain_step = 500    -- metres between terrain samples for maps and line of sight (sampled once, cached)

-- Enemy ground units (SAMs, vehicles) on the controllers' scopes when own radars detect them:
-- "auto" = only in missions dated enemy_ground_year or later (modern AWACS/JSTARS era), true =
-- always, false = never. Without them controllers find SAMs by RWR triangulation with pilots.
enemy_ground = "auto"
enemy_ground_year = 2012

-- Analytics and website export: local files in Saved Games\<server>\DCB (nothing is sent anywhere).
analytics = true      -- DCB\analytics\*.jsonl: every mission session's events (players as keyed ids + names)
export = true         -- DCB\export\: state.json (zones, missions, credits, scores) + events.jsonl for a website
