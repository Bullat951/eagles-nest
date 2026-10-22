-- DCB server hook (GameGUI env). Loads dcb.dll, injects the collector into the
-- mission scripting env and publishes each coalition's picture every interval.
local lfs = require("lfs")

local DCB = {
    root = lfs.writedir() .. "Mods\\Services\\DCB\\",
    cfg = { port = 10309, blue_password = "", red_password = "", interval = 1.0, terrain_step = 2000, dev_probe = false,
            analytics = true, export = true,
            -- Enemy ground units in the picture when own radars see them: true, false, or "auto"
            -- (only in missions dated enemy_ground_year or later: modern AWACS/JSTARS-era sensors).
            -- Otherwise ground threats are found by RWR triangulation.
            enemy_ground = "auto", enemy_ground_year = 2012 },
    -- collector timing, logged once a minute
    stats = { n = 0, total = 0, max = 0, bytes = 0, nextLog = 0 },
    started = false,
    nextCollect = 0,
    lastError = nil,
    collectorReady = false,
    theatreSent = false,
    -- terrain sampling: nil (not started), or { x0, z0, step, nx, nz, nextRow, rowsPerCall, done }
    terrain = nil,
    -- Foothold: exporter injected into the trigger env this mission; last published JSON
    foothold = { injected = false, absent = false, next = 0, last = nil },
    airfields = { next = 0, last = nil },
    weather = { next = 0 },
    marks = { next = 0, last = {} },  -- F10 marks per side: last published line
}

local FOOTHOLD_INTERVAL = 10  -- seconds between Foothold zone snapshots
local AIRFIELDS_INTERVAL = 60 -- seconds between airfield owner checks
local WEATHER_INTERVAL = 60   -- seconds between weather (ATIS) snapshots

local GEO_GRID = 21          -- control grid size for x/z -> lat/lon on the client
local TERRAIN_BUDGET = 0.004 -- seconds of sim-thread time per frame for terrain sampling

local function dcbLog(msg)
    log.write("DCB", log.INFO, msg)
end

local function readFile(path)
    local f = io.open(path, "rb")
    if not f then return nil end
    local s = f:read("*a")
    f:close()
    return s
end

-- First start: Config\DCB.lua is made from Config\DCB.example.lua (shipped next to it) with a
-- new random password for each side, so a fresh install works without editing anything. The
-- admin reads the passwords there (and may change them). An existing DCB.lua is never touched.
local function createConfig(path)
    local example = readFile(lfs.writedir() .. "Config\\DCB.example.lua")
    if not example then return false end
    local blue, red = dcb.random_hex(10), dcb.random_hex(10)
    if not blue or not red then return false end
    local text = example:gsub('blue_password%s*=%s*""', 'blue_password = "' .. blue .. '"', 1)
        :gsub('red_password%s*=%s*""', 'red_password = "' .. red .. '"', 1)
    local f = io.open(path, "w")
    if not f then return false end
    f:write(text)
    f:close()
    dcbLog("created " .. path .. " with new controller passwords for blue and red: give them to your controllers")
    return true
end

local function loadConfig()
    local path = lfs.writedir() .. "Config\\DCB.lua"
    local chunk = loadfile(path)
    if not chunk and createConfig(path) then chunk = loadfile(path) end
    if not chunk then
        dcbLog("no config at " .. path .. ", using defaults (logins disabled)")
        return
    end
    local env = {}
    setfenv(chunk, env)
    local ok, err = pcall(chunk)
    if not ok then
        dcbLog("config error: " .. tostring(err))
        return
    end
    for k, v in pairs(env) do
        if DCB.cfg[k] ~= nil then DCB.cfg[k] = v end
    end
end

package.cpath = DCB.root .. "bin\\?.dll;" .. package.cpath
local okRequire, dcb = pcall(require, "dcb")
if not okRequire then
    dcbLog("failed to load dcb.dll: " .. tostring(dcb))
    return
end
loadConfig()
DCB.collectorSource = readFile(DCB.root .. "Scripts\\collector.lua")
DCB.footholdSource = readFile(DCB.root .. "Scripts\\foothold.lua")
dcbLog("hook loaded, dll " .. dcb.version())

local function ensureStarted()
    if DCB.started or not DCS.isServer() then return end
    local ok, err = dcb.start(DCB.cfg.port, DCB.cfg.blue_password, DCB.cfg.red_password)
    if ok then
        DCB.started = true
    else
        dcbLog("start failed: " .. tostring(err))
    end
end

local function reportError(msg)
    -- Log each distinct error once, not every interval.
    if msg ~= DCB.lastError then
        dcbLog(msg)
        DCB.lastError = msg
    end
end

-- Other ticks' cost (Foothold export, analytics + website export), logged once a minute as
-- "timing: foothold n×, avg, max, total ms/min; analytics …".
local tickTimes = { nextLog = 0 }

local function timed(name, fn)
    local t0 = DCS.getRealTime()
    local ok, err = pcall(fn)
    local dt = DCS.getRealTime() - t0
    local s = tickTimes[name] or { n = 0, total = 0, max = 0 }
    s.n, s.total, s.max = s.n + 1, s.total + dt, math.max(s.max, dt)
    tickTimes[name] = s
    local now = DCS.getRealTime()
    if now >= tickTimes.nextLog then
        local parts = {}
        for k, v in pairs(tickTimes) do
            if type(v) == "table" and v.n > 0 then
                parts[#parts + 1] = string.format("%s %d×, avg %.2f ms, max %.1f ms, %.1f ms/min", k, v.n, v.total / v.n * 1000, v.max * 1000, v.total * 1000)
            end
        end
        if #parts > 0 then dcbLog("timing: " .. table.concat(parts, "; ")) end
        tickTimes = { nextLog = now + 60 }
    end
    return ok, err
end

local function recordTiming(seconds, bytes)
    local s = DCB.stats
    s.n, s.total, s.bytes = s.n + 1, s.total + seconds, s.bytes + bytes
    if seconds > s.max then s.max = seconds end
    local now = DCS.getRealTime()
    if now >= s.nextLog then
        if s.n > 0 then
            dcbLog(string.format("collect: %d runs, avg %.1f ms, max %.1f ms, avg %.0f KB", s.n, s.total / s.n * 1000, s.max * 1000, s.bytes / s.n / 1024))
        end
        DCB.stats = { n = 0, total = 0, max = 0, bytes = 0, nextLog = now + 60 }
    end
end

-- Dev aid (dev_probe = true in DCB.lua): runs Saved Games\<instance>\DCB\probe.lua once and
-- writes what it returns to probe.out. Local files only. A first line "-- env: <state>"
-- picks the net.dostring_in state (default "server").
local function runProbe()
    local dir = lfs.writedir() .. "DCB\\"
    local code = readFile(dir .. "probe.lua")
    if not code then return end
    os.remove(dir .. "probe.lua")
    local env = code:match("^%-%- env: (%w+)") or "server"
    local t0 = DCS.getRealTime()
    local result
    if env == "hook" then
        -- The hook's own env (dev only): analytics helpers are in DCB_DEV.
        local chunk, err = loadstring(code)
        if chunk then
            setfenv(chunk, getfenv(1))  -- DCS gives each hook file its own globals
            local ok, r = pcall(chunk)
            result = ok and r or ("ERR: " .. tostring(r))
        else
            result = "ERR: " .. tostring(err)
        end
    else
        result = net.dostring_in(env, code)
    end
    local f = io.open(dir .. "probe.out", "w")
    if f then
        f:write(string.format("-- %.1f ms\n", (DCS.getRealTime() - t0) * 1000), tostring(result))
        f:close()
    end
    dcbLog("probe run")
end

local function collect()
    local t0 = DCS.getRealTime()
    local result = net.dostring_in("server", "return DCB_collect and DCB_collect() or 'NOCOLLECTOR'")
    if type(result) == "string" and result:sub(1, 1) == "{" then recordTiming(DCS.getRealTime() - t0, #result) end
    if result == "NOCOLLECTOR" then
        if not DCB.collectorSource then
            reportError("collector.lua missing")
            return
        end
        local eg = DCB.cfg.enemy_ground
        net.dostring_in("server", string.format("DCB_settings = { enemyGround = %s, enemyGroundYear = %d }\n",
            eg == true and "true" or eg == false and "false" or '"auto"', tonumber(DCB.cfg.enemy_ground_year) or 2012)
            .. DCB.collectorSource)
        dcbLog("collector injected")
        return
    end
    if type(result) ~= "string" or result:sub(1, 1) ~= "{" then
        reportError("collect failed: " .. tostring(result))
        return
    end
    local nl = result:find("\n", 1, true)
    if not nl then
        reportError("collect returned one picture only")
        return
    end
    dcb.publish("blue", result:sub(1, nl - 1))
    dcb.publish("red", result:sub(nl + 1))
    DCB.collectorReady = true
    DCB.lastError = nil
end

-- ---- Theatre: bounds, georeference grid, towns ------------------------------------------

local function jstr(s)
    s = tostring(s):gsub('[%c"\\]', function(c)
        if c == '"' then return '\\"' end
        if c == "\\" then return "\\\\" end
        return string.format("\\u%04x", c:byte())
    end)
    return '"' .. s .. '"'
end

-- Terrain config bounds come as {x=, z=} or {x, y, z}/{x, z} depending on version, in km
-- (Caucasus: x -600..380, z -560..1130); returned in metres.
local function boundXZ(b)
    if type(b) ~= "table" then return nil end
    local x, z
    if b.x then x, z = b.x, b.z or b.y
    elseif #b >= 3 then x, z = b[1], b[3]
    else x, z = b[1], b[2] end
    if not (x and z) then return nil end
    return x * 1000, z * 1000
end

local function readTowns(id)
    local chunk = loadfile("Mods\\terrains\\" .. id .. "\\Map\\towns.lua")
    if not chunk then return {} end
    local env = { require = function() return { translate = function(s) return s end } end }
    setfenv(chunk, env)
    if not pcall(chunk) or type(env.towns) ~= "table" then return {} end
    local list = {}
    for name, t in pairs(env.towns) do
        if type(t) == "table" and t.latitude and t.longitude then
            list[#list + 1] = string.format("[%s,%.5f,%.5f]", jstr(t.display_name or name), t.latitude, t.longitude)
        end
    end
    return list
end

local function buildTheatre()
    local terrain = require("terrain")
    local id = terrain.GetTerrainConfig("id")
    local sx, sz = boundXZ(terrain.GetTerrainConfig("SW_bound"))
    local nx, nz = boundXZ(terrain.GetTerrainConfig("NE_bound"))
    if not (id and sx and sz and nx and nz) then
        reportError("terrain bounds unavailable")
        return
    end
    local x0, x1 = math.floor(math.min(sx, nx)), math.ceil(math.max(sx, nx))
    local z0, z1 = math.floor(math.min(sz, nz)), math.ceil(math.max(sz, nz))
    dcbLog(string.format("theatre %s x %d..%d z %d..%d", id, x0, x1, z0, z1))

    local grid = net.dostring_in("server", string.format("return DCB_geoGrid(%d,%d,%d,%d,%d)", x0, z0, x1, z1, GEO_GRID))
    if type(grid) ~= "string" or not grid:find(",", 1, true) then
        reportError("geo grid failed: " .. tostring(grid))
        return
    end
    local lats, lons = {}, {}
    for lat, lon in grid:gmatch("([%-%d%.]+),([%-%d%.]+)") do
        lats[#lats + 1] = lat
        lons[#lons + 1] = lon
    end
    local towns = readTowns(id)
    -- Magnetic variation over the theatre's lat/lon box, 0.5° steps (it varies slowly).
    local latMin, latMax, lonMin, lonMax = 90, -90, 180, -180
    for i = 1, #lats do
        local la, lo = tonumber(lats[i]), tonumber(lons[i])
        latMin, latMax = math.min(latMin, la), math.max(latMax, la)
        lonMin, lonMax = math.min(lonMin, lo), math.max(lonMax, lo)
    end
    local step = 0.5
    local lat0, lon0 = math.floor(latMin / step) * step, math.floor(lonMin / step) * step
    local nlat, nlon = math.ceil((latMax - lat0) / step) + 1, math.ceil((lonMax - lon0) / step) + 1
    local mv = net.dostring_in("server", string.format("return DCB_magvarGrid(%.2f,%.2f,%.2f,%d,%d)", lat0, lon0, step, nlat, nlon))
    local magvar = "null"
    if type(mv) == "string" and mv:find(",", 1, true) then
        magvar = string.format('{"lat0":%.2f,"lon0":%.2f,"step":%.2f,"nlat":%d,"nlon":%d,"deg":[%s]}',
            lat0, lon0, step, nlat, nlon, (mv:gsub(";", ",")))
    else
        reportError("magvar grid failed: " .. tostring(mv))
    end
    dcb.publish_all("theatre", string.format(
        '{"type":"theatre","id":%s,"x0":%d,"z0":%d,"x1":%d,"z1":%d,"grid":{"n":%d,"lat":[%s],"lon":[%s]},"magvar":%s,"towns":[%s]}',
        jstr(id), x0, z0, x1, z1, GEO_GRID, table.concat(lats, ","), table.concat(lons, ","), magvar, table.concat(towns, ",")))
    DCB.theatreSent = true
    dcbLog(string.format("theatre published: %d towns", #towns))

    local step = math.max(250, math.floor(DCB.cfg.terrain_step))
    local t = { id = id, x0 = x0, z0 = z0, step = step, nextRow = 0, rowsPerCall = 2 }
    t.nx = math.floor((x1 - x0) / step) + 1
    t.nz = math.floor((z1 - z0) / step) + 1
    local dir = lfs.writedir() .. "DCB"
    lfs.mkdir(dir)
    local cache = string.format("%s\\terrain_%s_%d.bin", dir, id, step)
    t.done = dcb.terrain_begin(id, x0, z0, step, t.nx, t.nz, cache)
    t.started = DCS.getRealTime()
    DCB.terrain = t
    dcbLog(string.format("terrain %dx%d at %d m: %s", t.nx, t.nz, step, t.done and "from cache" or "sampling"))
end

-- Samples a few terrain rows per frame, keeping to TERRAIN_BUDGET of sim-thread time.
local function sampleTerrain()
    local t = DCB.terrain
    local t0 = DCS.getRealTime()
    local rows = net.dostring_in("server", string.format("return DCB_terrainRows(%d,%d,%d,%d,%d,%d,%d)",
        t.x0, t.z0, t.step, t.nx, t.nz, t.nextRow, t.rowsPerCall))
    if type(rows) ~= "string" or rows == "" then
        reportError("terrain sampling failed: " .. tostring(rows))
        t.done = true
        return
    end
    for row in rows:gmatch("[^\n]+") do
        local csv, digits = row:match("^(.-)|(%d*)$")
        if not (csv and dcb.terrain_row(t.nextRow, csv, digits)) then
            reportError("bad terrain row " .. t.nextRow)
            t.done = true
            return
        end
        t.nextRow = t.nextRow + 1
    end
    local spent = DCS.getRealTime() - t0
    if spent < TERRAIN_BUDGET / 2 then t.rowsPerCall = t.rowsPerCall + 1
    elseif spent > TERRAIN_BUDGET and t.rowsPerCall > 1 then t.rowsPerCall = t.rowsPerCall - 1 end
    if t.nextRow >= t.nx then
        t.done = true
        local ok = dcb.terrain_finish()
        dcbLog(string.format("terrain sampled in %.0f s: %s", DCS.getRealTime() - t.started, ok and "published" or "incomplete"))
    end
end

-- ---- Foothold (optional): zone ownership and links, read-only ----------------------------

-- Foothold runs in the trigger scripting env; reach it with a_do_script through the
-- 'mission' state. The exporter pushes its result into the 'server' state.
local function footholdTick()
    local fh = DCB.foothold
    if fh.absent then return end
    local now = DCS.getRealTime()
    if now < fh.next then return end
    fh.next = now + FOOTHOLD_INTERVAL
    if not fh.injected then
        if not DCB.footholdSource then return end
        net.dostring_in("mission", "a_do_script(" .. string.format("%q", DCB.footholdSource) .. ")")
        fh.injected = true
    end
    net.dostring_in("mission", 'a_do_script("if DCB_footholdPush then DCB_footholdPush() end")')
    local json = net.dostring_in("server", "local j = DCB_footholdJson; DCB_footholdJson = nil; return j or ''")
    if json == "none" then
        fh.absent = true
        dcbLog("no Foothold in this mission")
    elseif type(json) == "string" and json:sub(1, 4) == "ERR:" then
        reportError("foothold export failed: " .. json:sub(5))
    elseif type(json) == "string" and json:sub(1, 1) == "{" and json ~= fh.last then
        -- Credits tick with Foothold's income every 10 s: a change in credits alone is passed on
        -- at most once a minute; zones, links and missions at once.
        local key = json:gsub(',"credits":%b{}', "")
        if fh.lastKey == key and now < (fh.nextCredits or 0) then return end
        if not fh.last then dcbLog(string.format("Foothold detected: publishing zones (%d KB)", math.floor(#json / 1024))) end
        fh.last, fh.lastKey, fh.nextCredits = json, key, now + 60
        dcb.publish_all("foothold", json)
    end
end

-- ---- Airfields: runways and parking (layout once), owners re-checked ----------------------

local function airfieldsTick()
    local af = DCB.airfields
    local now = DCS.getRealTime()
    if now < af.next then return end
    af.next = now + AIRFIELDS_INTERVAL
    local t0 = DCS.getRealTime()
    local json = net.dostring_in("server", "return DCB_airfields and DCB_airfields() or ''")
    if type(json) ~= "string" or json:sub(1, 1) ~= "{" then
        reportError("airfields failed: " .. tostring(json))
        return
    end
    if json ~= af.last then
        if not af.last then
            dcbLog(string.format("airfields published (%d KB, %.0f ms)", math.floor(#json / 1024), (DCS.getRealTime() - t0) * 1000))
        end
        af.last = json
        dcb.publish_all("airfields", json)
    end
end

-- ---- Weather for ATIS (public), once a minute -------------------------------------------

local function weatherTick()
    local wx = DCB.weather
    local now = DCS.getRealTime()
    if now < wx.next then return end
    wx.next = now + WEATHER_INTERVAL
    local json = net.dostring_in("server", "return DCB_weather and DCB_weather() or ''")
    if type(json) ~= "string" or json:sub(1, 1) ~= "{" then
        reportError("weather failed: " .. tostring(json))
        return
    end
    dcb.publish_all("weather", json)
end

-- ---- F10 map marks (coalition channels), checked every 5 s, sent when they change ----------

local MARKS_INTERVAL = 5

local function marksTick()
    local mk = DCB.marks
    local now = DCS.getRealTime()
    if now < mk.next then return end
    mk.next = now + MARKS_INTERVAL
    local result = net.dostring_in("server", "return DCB_marks and DCB_marks() or ''")
    if type(result) ~= "string" or result == "" then return end
    if result:sub(1, 4) == "ERR:" then
        reportError("marks failed: " .. result)
        return
    end
    for line in result:gmatch("[^\n]+") do
        local side = line:match('"coalition":"(%a+)"')
        if side and mk.last[side] ~= line then
            mk.last[side] = line
            dcb.publish_side(side, "marks", line)
        end
    end
end

-- ---- Fast LSO feed (coalition channels), 10 Hz while aircraft are near own carriers ------
-- DCB_lso() returns one JSON line per coalition that has aircraft near its carriers (or '').

local LSO_INTERVAL = 0.1

local function lsoTick()
    local now = DCS.getRealTime()
    if now < (DCB.nextLso or 0) then return end
    DCB.nextLso = now + LSO_INTERVAL
    local result = net.dostring_in("server", "return DCB_lso and DCB_lso() or ''")
    if type(result) ~= "string" or result == "" then return end
    if result:sub(1, 4) == "ERR:" then
        reportError("lso failed: " .. result)
        return
    end
    for line in result:gmatch("[^\n]+") do
        local side = line:match('"coalition":"(%a+)"')
        if side then dcb.publish_side(side, "lso", line) end
    end
end

-- ---- Controller commands ------------------------------------------------------------------
-- Already checked by the DLL (known name, field limits, rate). Each runs a fixed function in
-- the mission env with the controller's values as arguments (%q-quoted strings, %d numbers),
-- answers the controller, and is logged with who asked for what.

local function runCommand(cmd)
    if not DCB.collectorReady then return false, "server not ready yet" end
    if cmd.name == "message_unit" then
        local result = net.dostring_in("server", string.format("return DCB_messageUnit(%d, %q, %d, %q)",
            cmd.unit, cmd.text, cmd.seconds, cmd.coalition))
        if result == "ok" then return true end
        return false, tostring(result)
    end
    if cmd.name == "mark_add" then
        local result = tostring(net.dostring_in("server", string.format("return DCB_markAdd(%d, %d, %q, %q, %q)",
            cmd.x, cmd.z, cmd.text, cmd.threat or "", cmd.coalition)))
        local id = result:match("^ok:(%d+)$")
        if id then
            DCB.marks.next = 0  -- send the new mark to the side's controllers at once
            return true, id
        end
        return false, result
    end
    if cmd.name == "carrier_lights" then
        -- Checked in the server env (own coalition's carrier), set in the mission env, where the
        -- Mission Editor's SET CARRIER LIGHTING MODE lives. Verified in MP on CVN-72 and Stennis.
        local check = tostring(net.dostring_in("server", string.format("return DCB_carrierLightsCheck(%d, %q)", cmd.unit, cmd.coalition)))
        if check ~= "ok" then return false, check end
        local result = tostring(net.dostring_in("mission", string.format(
            "return tostring(pcall(a_set_carrier_illumination_mode, %d, %d))", cmd.unit, cmd.mode)))
        if result:sub(1, 4) == "true" then return true end
        return false, "lights not set: " .. result
    end
    if cmd.name == "mark_remove" then
        local result = net.dostring_in("server", string.format("return DCB_markRemove(%d, %q)", cmd.mark, cmd.coalition))
        if result == "ok" then
            DCB.marks.next = 0
            return true
        end
        return false, tostring(result)
    end
    return false, "unknown command"
end

local function commandsTick()
    for _, cmd in ipairs(dcb.commands()) do
        local okRun, ok, err = pcall(runCommand, cmd)
        if not okRun then ok, err = false, "server error: " .. tostring(ok) end
        dcb.command_result(cmd.client, cmd.id, ok and true or false, err)
        dcbLog(string.format("command %s by %s (%s) unit %d mark %d mode %d, %d chars: %s",
            cmd.name, cmd.address, cmd.coalition, cmd.unit, cmd.mark or 0, cmd.mode or 0, #cmd.text,
            ok and ("done" .. (err and (" " .. err) or "")) or ("refused: " .. tostring(err))))
    end
end

-- ---- Analytics log and public export (both local files on the server) ---------------------
-- Analytics: Saved Games\<server>\DCB\analytics\<UTC start>_<mission>.jsonl, one JSON object
-- per line, every event of a mission session. Players appear as {"id","name"}: id = the first
-- 16 hex digits of HMAC-SHA256(server key, UCID); the key (DCB\analytics.key) is made once and
-- never leaves the server, so ids are stable across name changes but do not reveal the UCID.
-- Export (for a website, docs/RESEARCH-WEBSITE.md): DCB\export\state.json (Foothold zones with
-- lat/lon, missions, credits, top players; checked every 10 s, written when it changes) and DCB\export\events.jsonl
-- (the public events, names only: no ids, no positions).

local AN = { file = nil, players = {}, byName = {}, key = nil, nextExport = 0, statsPublic = nil }
local PUBLIC_KINDS = { kill = true, takeoff = true, landing = true, crash = true, eject = true,
    zone_captured = true, mission_started = true, mission_ended = true }

local function dcbDir(sub)
    local d = lfs.writedir() .. "DCB\\"
    lfs.mkdir(d)
    if sub then
        d = d .. sub .. "\\"
        lfs.mkdir(d)
    end
    return d
end

local function utcNow() return os.date("!%Y-%m-%dT%H:%M:%SZ") end

local function analyticsKey()
    if AN.key then return AN.key end
    local path = dcbDir() .. "analytics.key"
    local k = readFile(path)
    k = k and k:match("^%s*(%x+)%s*$")
    if not k or #k < 32 then
        k = dcb.random_hex(32)
        local f = io.open(path, "w")
        if f then
            f:write(k)
            f:close()
        end
    end
    AN.key = k
    return k
end

local function playerId(ucid)
    if not ucid or ucid == "" then return nil end
    local h = dcb.hmac_hex(analyticsKey(), ucid)
    return h and h:sub(1, 16) or nil
end

local function playerJson(id, name)
    return string.format('{"id":%s,"name":%s}', id and jstr(id) or "null", jstr(name or "?"))
end

local function appendLine(path, line)
    local f = io.open(path, "a")
    if not f then return end
    f:write(line, "\n")
    f:close()
end

-- One event: into the session's analytics file, and (public kinds, ids removed) into the export.
local function record(json, kind)
    local line = '{"utc":' .. jstr(utcNow()) .. "," .. json:sub(2)
    if DCB.cfg.analytics and AN.file then
        AN.file:write(line, "\n")
        AN.file:flush()
    end
    if DCB.cfg.export and PUBLIC_KINDS[kind] then
        appendLine(dcbDir("export") .. "events.jsonl", (line:gsub('"id":"%x+",', ""):gsub('"id":null,', "")))
    end
end

local function sessionOpen()
    if not (DCB.cfg.analytics or DCB.cfg.export) then return end
    local mission = tostring(DCS.getMissionName and DCS.getMissionName() or "mission")
    if DCB.cfg.analytics then
        local safe = mission:gsub("[^%w%-_.]", "_"):sub(1, 60)
        AN.file = io.open(dcbDir("analytics") .. os.date("!%Y%m%d-%H%M%S") .. "_" .. safe .. ".jsonl", "a")
    end
    AN.mission = mission
    record(string.format('{"kind":"session_start","mission":%s,"dcb":%s}', jstr(mission), jstr(dcb.version())), "session_start")
end

local function sessionClose()
    if AN.file then
        record('{"kind":"session_end"}', "session_end")
        AN.file:close()
        AN.file = nil
    end
end

-- Names from the mission env → {id, name}; Foothold's raw UCIDs → ids (never written raw).
local function enrich(line)
    line = line:gsub('"player":"(.-)"', function(name)
        local p = AN.byName[name]
        return '"player":' .. playerJson(p and p.id, p and p.name or name)
    end)
    line = line:gsub('"ucid":"(.-)"', function(ucid) return '"id":' .. jstr(playerId(ucid) or "") end)
    return line
end

local function analyticsTick()
    if not (DCB.cfg.analytics or DCB.cfg.export) then return end
    local batch = net.dostring_in("server", "return DCB_analyticsDrain and DCB_analyticsDrain() or ''") or ""
    local fh = net.dostring_in("server", "local e = DCB_footholdEvents; DCB_footholdEvents = nil; return e or ''") or ""
    for _, text in ipairs({ batch, fh }) do
        for raw in tostring(text):gmatch("[^\n]+") do
            if raw:sub(1, 1) == "{" then
                local line = enrich(raw)
                local kind = line:match('"kind":"([%w_]+)"') or "?"
                if kind == "foothold_stats" then
                    -- Keep the scoreboard for the export (names only).
                    AN.statsPublic = line:match('"players":(%b[])')
                    if AN.statsPublic then AN.statsPublic = AN.statsPublic:gsub('"id":"%x*",', "") end
                end
                record(line, kind)
            end
        end
    end
    -- The public state for a website, every 10 s: Foothold's picture, missions, credits, scores.
    local now = DCS.getRealTime()
    if DCB.cfg.export and now >= AN.nextExport then
        AN.nextExport = now + 10
        local fhJson = DCB.foothold and DCB.foothold.last
        -- Written only when something in it changed; "updated" is the time of that change.
        local content = string.format('"mission":%s,"foothold":%s,"players":%s}', jstr(AN.mission or ""), fhJson or "null", AN.statsPublic or "[]")
        if content == AN.lastState then return end
        AN.lastState = content
        local state = '{"updated":' .. jstr(utcNow()) .. "," .. content
        local dir = dcbDir("export")
        local tmp = dir .. "state.json.tmp"
        local f = io.open(tmp, "w")
        if f then
            f:write(state)
            f:close()
            os.remove(dir .. "state.json")
            os.rename(tmp, dir .. "state.json")
        end
    end
end

-- Dev aid only (dev_probe = true, never on a live server): the analytics helpers for probes.
if DCB.cfg.dev_probe then
    DCB_DEV = { AN = AN, playerId = playerId, enrich = enrich, record = record }
end

local callbacks = {}

function callbacks.onPlayerConnect(id)
    pcall(function()
        local info = net.get_player_info(id)
        if not info then return end
        local p = { id = playerId(info.ucid), name = info.name }
        AN.players[id] = p
        AN.byName[info.name] = p
        record(string.format('{"kind":"connect","player":%s}', playerJson(p.id, p.name)), "connect")
    end)
end

function callbacks.onPlayerDisconnect(id)
    pcall(function()
        local p = AN.players[id]
        if not p then return end
        record(string.format('{"kind":"disconnect","player":%s}', playerJson(p.id, p.name)), "disconnect")
        AN.players[id] = nil
    end)
end

function callbacks.onPlayerChangeSlot(id)
    pcall(function()
        local info = net.get_player_info(id)
        local p = AN.players[id]
        if not info or not p then return end
        local unitType = info.slot and info.slot ~= "" and DCS.getUnitType and DCS.getUnitType(info.slot) or nil
        record(string.format('{"kind":"slot","player":%s,"side":%d,"unit":%s}', playerJson(p.id, p.name),
            tonumber(info.side) or 0, unitType and jstr(unitType) or "null"), "slot")
    end)
end

function callbacks.onSimulationStop()
    pcall(sessionClose)
end

function callbacks.onSimulationStart()
    pcall(sessionClose)
    local okA, errA = pcall(sessionOpen)
    if not okA then dcbLog("analytics: " .. tostring(errA)) end
    ensureStarted()
    DCB.nextCollect = 0
    -- A new mission may be on another terrain; the collector is re-injected as well.
    DCB.collectorReady = false
    DCB.theatreSent = false
    DCB.terrain = nil
    DCB.foothold = { injected = false, absent = false, next = 0, last = nil }
    DCB.airfields = { next = 0, last = nil }
    DCB.weather = { next = 0 }
    DCB.marks = { next = 0, last = {} }
end

function callbacks.onSimulationFrame()
    if not DCB.started then return end
    for _, line in ipairs(dcb.poll()) do dcbLog(line) end
    -- Commands every frame: a controller's call reaches the player without waiting a second.
    local okCmd, errCmd = pcall(commandsTick)
    if not okCmd then reportError("commands error: " .. tostring(errCmd)) end
    if DCB.terrain and not DCB.terrain.done then
        local ok, err = pcall(sampleTerrain)
        if not ok then
            reportError("terrain error: " .. tostring(err))
            DCB.terrain.done = true
        end
    end
    if DCB.collectorReady then
        local okLso, errLso = pcall(lsoTick)
        if not okLso then reportError("lso error: " .. tostring(errLso)) end
    end
    local now = DCS.getRealTime()
    if now < DCB.nextCollect then return end
    DCB.nextCollect = now + DCB.cfg.interval
    local ok, err = pcall(collect)
    if not ok then reportError("collect error: " .. tostring(err)) end
    if DCB.collectorReady and DCB.theatreSent then
        ok, err = timed("foothold", footholdTick)
        if not ok then reportError("foothold error: " .. tostring(err)) end
        ok, err = pcall(airfieldsTick)
        if not ok then reportError("airfields error: " .. tostring(err)) end
        ok, err = pcall(weatherTick)
        if not ok then reportError("weather error: " .. tostring(err)) end
    end
    if DCB.collectorReady then
        ok, err = timed("analytics", analyticsTick)
        if not ok then reportError("analytics error: " .. tostring(err)) end
        ok, err = timed("marks", marksTick)
        if not ok then reportError("marks error: " .. tostring(err)) end
    end
    if DCB.cfg.dev_probe and DCB.collectorReady then
        ok, err = pcall(runProbe)
        if not ok then reportError("probe error: " .. tostring(err)) end
    end
    if DCB.collectorReady and not DCB.theatreSent then
        ok, err = pcall(buildTheatre)
        if not ok then
            reportError("theatre error: " .. tostring(err))
            DCB.theatreSent = true  -- do not retry every second
        end
    end
end

DCS.setUserCallbacks(callbacks)
