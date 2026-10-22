-- Georeference control grid: lat/lon of an n x n grid of DCS x/z points over a rectangle,
-- as "lat,lon;lat,lon;..." row-major (x outer, z inner).
function DCB_geoGrid(x0, z0, x1, z1, n)
    local out = {}
    for i = 0, n - 1 do
        local x = x0 + (x1 - x0) * i / (n - 1)
        for j = 0, n - 1 do
            local z = z0 + (z1 - z0) * j / (n - 1)
            local lat, lon = coord.LOtoLL({ x = x, y = 0, z = z })
            out[#out + 1] = string.format("%.6f,%.6f", lat, lon)
        end
    end
    return table.concat(out, ";")
end

-- Magnetic variation (degrees, east positive) from DCS's own model for the mission date, on
-- a lat/lon grid: rows from lat0 north in steps, lon0 east in steps; "d,d,...;d,d,..." per row.
function DCB_magvarGrid(lat0, lon0, step, nlat, nlon)
    local magvar = require("magvar")
    local rows = {}
    for i = 0, nlat - 1 do
        local row = {}
        for j = 0, nlon - 1 do
            row[j + 1] = string.format("%.2f", math.deg(magvar.get_mag_decl(lat0 + i * step, lon0 + j * step)))
        end
        rows[#rows + 1] = table.concat(row, ",")
    end
    return table.concat(rows, ";")
end

-- Terrain rows i0..i0+count-1 of a grid (x = x0 + i*step, z = z0 + j*step, j < nz).
-- Each row: "h,h,...|ddd..." (heights in m, surface types as digits); rows joined by "\n".
function DCB_terrainRows(x0, z0, step, nx, nz, i0, count)
    local rows = {}
    for i = i0, math.min(i0 + count, nx) - 1 do
        local x = x0 + i * step
        local h, s = {}, {}
        for j = 0, nz - 1 do
            local p = { x = x, y = z0 + j * step }
            h[j + 1] = string.format("%d", land.getHeight(p))
            s[j + 1] = tostring(land.getSurfaceType(p) or 0)
        end
        rows[#rows + 1] = table.concat(h, ",") .. "|" .. table.concat(s)
    end
    return table.concat(rows, "\n")
end

-- Airfields (airdromes and helipads; ships move and are in the picture): static layout from
-- getRunways/getParking, built once and cached; owners re-read on every call. Returns the
-- "airfields" message JSON.
DCB_airfields = (function()
    local layout = nil  -- { { ab = Airbase, json = "<static fields>" }, ... }

    local function jstr(s)
        s = tostring(s):gsub('[%c"\\]', function(c)
            if c == '"' then return '\\"' end
            if c == "\\" then return "\\\\" end
            return string.format("\\u%04x", c:byte())
        end)
        return '"' .. s .. '"'
    end

    local function build()
        layout = {}
        for _, ab in ipairs(world.getAirbases() or {}) do
            local desc = ab:getDesc() or {}
            local cat = desc.category
            if cat ~= Airbase.Category.SHIP then
                local p = ab:getPoint()
                local runways = {}
                local okR, rws = pcall(ab.getRunways, ab)
                for _, r in ipairs(okR and rws or {}) do
                    local rp = r.position or {}
                    runways[#runways + 1] = string.format('{"name":%s,"course":%.5f,"length":%.0f,"width":%.0f,"x":%.1f,"z":%.1f}',
                        jstr(r.Name or r.name or ""), r.course or 0, r.length or 0, r.width or 0, rp.x or 0, rp.z or 0)
                end
                local parking = {}
                local okP, spots = pcall(ab.getParking, ab)
                for _, s in ipairs(okP and spots or {}) do
                    local sp = s.vTerminalPos or {}
                    parking[#parking + 1] = string.format('[%d,%.1f,%.1f,%d]', s.Term_Index or 0, sp.x or 0, sp.z or 0, s.Term_Type or 0)
                end
                layout[#layout + 1] = {
                    ab = ab,
                    json = string.format('"id":%d,"name":%s,"category":%d,"x":%.1f,"z":%.1f,"runways":[%s],"parking":[%s]',
                        tonumber(ab:getID()) or 0, jstr(ab:getName()), cat or 0, p.x, p.z,
                        table.concat(runways, ","), table.concat(parking, ",")),
                }
            end
        end
    end

    return function()
        local ok, result = pcall(function()
            if not layout then build() end
            local out = {}
            for _, a in ipairs(layout) do
                local okC, side = pcall(a.ab.getCoalition, a.ab)
                out[#out + 1] = string.format('{%s,"coalition":%d}', a.json, okC and side or 0)
            end
            return '{"type":"airfields","airfields":[' .. table.concat(out, ",") .. "]}"
        end)
        return ok and result or ("ERR:" .. tostring(result))
    end
end)()

-- Controller command "message_unit": an on-screen text box (not chat) for one player's
-- aircraft. Only a unit of the controller's own coalition with a player in it. The text
-- arrives as a plain string argument (the hook never runs controller text as code).
-- Returns "ok" or the reason it was not shown.
function DCB_messageUnit(unitId, text, seconds, coalitionName)
    local side = coalitionName == "blue" and coalition.side.BLUE or coalitionName == "red" and coalition.side.RED or nil
    if not side then return "unknown coalition" end
    for _, gcat in ipairs({ Group.Category.AIRPLANE, Group.Category.HELICOPTER }) do
        for _, g in ipairs(coalition.getGroups(side, gcat) or {}) do
            for _, u in ipairs(g:getUnits() or {}) do
                -- getID() returns a string in DCS's mission env ("4940"): compare as numbers.
                if u:isExist() and tonumber(u:getID()) == unitId then
                    if not u:getPlayerName() then return "no player in that aircraft" end
                    trigger.action.outTextForUnit(unitId, text, seconds, false)
                    return "ok"
                end
            end
        end
    end
    return "not an aircraft of your coalition (or it is gone)"
end

-- F10 map marks. Controllers add marks for their own coalition (commands "mark_add"
-- and "mark_remove", text and threat type as plain string arguments) and can remove only the
-- ones DCB made for their coalition; DCB_marks lists every mark each side can see.
DCB_markReg = DCB_markReg or { nextId = 880000, marks = {} }  -- id -> { side, threat }

local MARK_SIDES = { blue = coalition.side.BLUE, red = coalition.side.RED }

local function markJstr(s)
    s = tostring(s):gsub('[%c"\\]', function(c)
        if c == '"' then return '\\"' end
        if c == "\\" then return "\\\\" end
        return string.format("\\u%04x", c:byte())
    end)
    return '"' .. s .. '"'
end

-- Returns "ok:<mark id>" or the reason the mark was not made.
function DCB_markAdd(x, z, text, threat, coalitionName)
    local side = MARK_SIDES[coalitionName]
    if not side then return "unknown coalition" end
    local used = {}
    for _, m in ipairs(world.getMarkPanels() or {}) do used[m.idx] = true end
    local reg = DCB_markReg
    while used[reg.nextId] do reg.nextId = reg.nextId + 1 end
    local id = reg.nextId
    reg.nextId = id + 1
    local pos = { x = x, y = land.getHeight({ x = x, y = z }), z = z }
    -- Not read-only: the coalition's players can delete (or correct) it on their F10 map.
    trigger.action.markToCoalition(id, text, pos, side, false)
    reg.marks[id] = { side = side, threat = threat ~= "" and threat or nil }
    return "ok:" .. id
end

function DCB_markRemove(id, coalitionName)
    local m = DCB_markReg.marks[id]
    if not m or m.side ~= MARK_SIDES[coalitionName] then return "not a DCB mark of your coalition" end
    trigger.action.removeMark(id)
    DCB_markReg.marks[id] = nil
    return "ok"
end

-- Per side one line {"type":"marks","coalition","marks":[{id,text,x,z,lat,lon,time,author,
-- dcb,threat}]}: the marks visible to that coalition (its own, and ones for all), at most 300.
function DCB_marks()
    local ok, result = pcall(function()
        local panels = world.getMarkPanels() or {}
        local lines = {}
        for name, side in pairs(MARK_SIDES) do
            local out = {}
            for _, m in ipairs(panels) do
                local mside = tonumber(m.coalition) or -1
                if (mside == side or mside == -1) and type(m.pos) == "table" and #out < 300 then
                    local author = m.author
                    if m.initiator then
                        local okN, pn = pcall(m.initiator.getPlayerName, m.initiator)
                        if okN and pn then author = pn end
                    end
                    local lat, lon = coord.LOtoLL(m.pos)
                    local reg = DCB_markReg.marks[m.idx]
                    out[#out + 1] = string.format('{"id":%d,"text":%s,"x":%.1f,"z":%.1f,"lat":%.6f,"lon":%.6f,"time":%.0f,"author":%s,"dcb":%s,"threat":%s}',
                        m.idx, markJstr(tostring(m.text or ""):sub(1, 200)), m.pos.x, m.pos.z, lat, lon, tonumber(m.time) or 0,
                        author and markJstr(author) or "null", reg and "true" or "false",
                        reg and reg.threat and markJstr(reg.threat) or "null")
                end
            end
            lines[#lines + 1] = string.format('{"type":"marks","coalition":"%s","marks":[%s]}', name, table.concat(out, ","))
        end
        -- Forget DCB marks someone removed in the game.
        local live = {}
        for _, m in ipairs(panels) do live[m.idx] = true end
        for id in pairs(DCB_markReg.marks) do if not live[id] then DCB_markReg.marks[id] = nil end end
        return table.concat(lines, "\n")
    end)
    return ok and result or ("ERR:" .. tostring(result))
end

-- Weather for ATIS: the mission's weather plus, per airfield, the live wind 10 m above the
-- field, the temperature there and QNH (local pressure reduced to sea level with the ISA
-- lapse rate, so it follows the mission's pressure systems). Returns the "weather" message.
function DCB_weather()
    local ok, result = pcall(function()
        local w = env.mission.weather or {}
        local clouds, fog, vis = w.clouds or {}, w.fog or {}, w.visibility or {}
        local d = env.mission.date or {}
        local fields = {}
        for _, ab in ipairs(world.getAirbases() or {}) do
            local desc = ab:getDesc() or {}
            -- Airdromes only: helipads and FARPs (a few hundred) cost sim-thread time for no ATIS.
            if desc.category == Airbase.Category.AIRDROME then
                local p = ab:getPoint()
                local wind = atmosphere.getWind({ x = p.x, y = p.y + 10, z = p.z })
                local t, pa = atmosphere.getTemperatureAndPressure({ x = p.x, y = p.y + 2, z = p.z })
                local speed = math.sqrt(wind.x * wind.x + wind.z * wind.z)
                -- The vector is where the air goes; wind is named for where it comes from.
                local from = (math.deg(math.atan2(-wind.z, -wind.x)) + 360) % 360
                local qnh = pa and (pa / 100) * (1 - 0.0065 * (p.y + 2) / 288.15) ^ -5.25588 or 0
                fields[#fields + 1] = string.format('{"id":%d,"elevation":%.0f,"windFrom":%.0f,"windMs":%.1f,"tempC":%.1f,"qnhHpa":%.1f}',
                    tonumber(ab:getID()) or 0, p.y, from, speed, (t or 288.15) - 273.15, qnh)
            end
        end
        return string.format('{"type":"weather","date":"%04d-%02d-%02d","time":%.0f,"visibility":%.0f,' ..
            '"fog":{"on":%s,"visibility":%.0f,"thickness":%.0f},"dust":{"on":%s,"density":%.0f},' ..
            '"clouds":{"base":%.0f,"thickness":%.0f,"density":%.0f,"preset":%s,"precipitation":%d},"airfields":[%s]}',
            d.Year or 0, d.Month or 0, d.Day or 0, timer.getAbsTime(), vis.distance or 0,
            w.enable_fog and "true" or "false", fog.visibility or 0, fog.thickness or 0,
            w.enable_dust and "true" or "false", w.dust_density or 0,
            clouds.base or 0, clouds.thickness or 0, clouds.density or 0,
            clouds.preset and string.format("%q", clouds.preset) or "null", clouds.iprecptns or 0,
            table.concat(fields, ","))
    end)
    return ok and result or ("ERR:" .. tostring(result))
end

-- Missiles in flight, from DCS's shot events: { weapon, id, side, shooter, type, t }. The
-- handler is registered once per scripting state (the collector is injected again only
-- into a fresh state, after a mission change); entries go when the weapon stops existing.
DCB_missiles = (function()
    local live, nextId = {}, 1
    local handler = {}
    function handler:onEvent(e)
        pcall(function()
            if e.id ~= world.event.S_EVENT_SHOT or not e.weapon then return end
            local w = e.weapon
            local desc = w:getDesc()
            if not desc or desc.category ~= Weapon.Category.MISSILE then return end
            local shooter = nil
            if e.initiator and e.initiator.getID then
                local okS, sid = pcall(e.initiator.getID, e.initiator)
                shooter = okS and tonumber(sid) or nil
            end
            if #live >= 300 then table.remove(live, 1) end
            live[#live + 1] = { weapon = w, id = nextId, side = w:getCoalition(), shooter = shooter,
                type = w:getTypeName(), t = timer.getTime() }
            nextId = nextId + 1
        end)
    end
    if DCB_missileHandler then world.removeEventHandler(DCB_missileHandler) end
    DCB_missileHandler = handler
    world.addEventHandler(handler)
    return live
end)()

-- Carriers: the types DCB treats as carriers, their beacons from the mission's group tasks
-- (TACAN, ICLS, Link 4, ACLS; read once), the aircraft near each side's carriers (refreshed
-- with every picture, read 10 times a second by DCB_lso) and landings on them
-- (S_EVENT_LAND, kept a minute for the pictures).
DCB_carrier = (function()
    local TYPES = { CVN_70 = true, CVN_71 = true, CVN_72 = true, CVN_73 = true, CVN_75 = true, Stennis = true,
        Forrestal = true, CV_1143_5 = true, KUZNECOW = true, LHA_Tarawa = true, hms_invincible = true, Essex = true }
    local M = { types = TYPES, near = {}, events = {}, nextEvent = 1 }
    local beacons = nil

    local function readBeacons()
        beacons = {}
        for _, side in pairs(env.mission.coalition or {}) do
            for _, country in ipairs(side.country or {}) do
                for _, g in ipairs((country.ship or {}).group or {}) do
                    for _, u in ipairs(g.units or {}) do
                        if TYPES[u.type] then
                            beacons[u.unitId] = beacons[u.unitId] or {}
                            beacons[u.unitId].radio = u.frequency or g.frequency
                        end
                    end
                    for _, p in ipairs((g.route or {}).points or {}) do
                        local tasks = p.task and p.task.params and p.task.params.tasks or {}
                        for _, t in pairs(tasks) do
                            local a = t.params and t.params.action
                            local ap = a and a.params
                            if ap and ap.unitId then
                                local b = beacons[ap.unitId] or {}
                                beacons[ap.unitId] = b
                                if a.id == "ActivateBeacon" then
                                    b.tacan = string.format("%d%s %s", ap.channel or 0, ap.modeChannel or "X", ap.callsign or "")
                                elseif a.id == "ActivateICLS" then
                                    b.icls = ap.channel
                                elseif a.id == "ActivateLink4" then
                                    b.link4 = ap.frequency
                                elseif a.id == "ActivateACLS" then
                                    b.acls = true
                                end
                            end
                        end
                    end
                end
            end
        end
    end

    function M.beacons(id)
        if not beacons then pcall(readBeacons) end
        return (beacons or {})[id] or {}
    end

    local handler = {}
    function handler:onEvent(e)
        pcall(function()
            if e.id ~= world.event.S_EVENT_LAND or not e.initiator or not e.place then return end
            local okU, ship = pcall(e.place.getUnit, e.place)
            if not okU or not ship then ship = e.place end
            local okT, t = pcall(ship.getTypeName, ship)
            if not okT or not TYPES[t] then return end
            local u = e.initiator
            local p, cp = u:getPoint(), ship:getPoint()
            M.events[#M.events + 1] = { id = M.nextEvent, kind = "trap", unit = tonumber(u:getID()), carrier = tonumber(ship:getID()),
                side = u:getCoalition(), t = timer.getTime(), x = p.x, z = p.z, cx = cp.x, cz = cp.z }
            M.nextEvent = M.nextEvent + 1
            if #M.events > 50 then table.remove(M.events, 1) end
        end)
    end
    if DCB_carrierHandler then world.removeEventHandler(DCB_carrierHandler) end
    DCB_carrierHandler = handler
    world.addEventHandler(handler)
    return M
end)()

-- Analytics: DCS events that involve players (spawns, takeoffs, landings, kills either way,
-- crashes, ejections, deaths, leaving a unit), queued here and drained by the hook once a
-- second (DCB_analyticsDrain) into the server's analytics log. Players are named here; the
-- hook replaces names with {id, name} (id = a keyed hash of the UCID). No positions are kept.
DCB_analytics = (function()
    local queue = {}
    local E = world.event

    local function jstr(s)
        s = tostring(s):gsub('[%c"\\]', function(c)
            if c == '"' then return '\\"' end
            if c == "\\" then return "\\\\" end
            return string.format("\\u%04x", c:byte())
        end)
        return '"' .. s .. '"'
    end

    local SIDE = { [0] = "neutral", [1] = "red", [2] = "blue" }

    -- What an object is, for kill statistics: air, helo, sam, ground, infantry, ship, structure.
    local function categoryOf(obj)
        local okC, cat = pcall(Object.getCategory, obj)
        if not okC then return "unknown" end
        if cat == Object.Category.STATIC then return "structure" end
        if cat == Object.Category.SCENERY then return "scenery" end
        if cat ~= Object.Category.UNIT then return "other" end
        local okD, desc = pcall(obj.getDesc, obj)
        local c = okD and desc and desc.category
        if c == 0 then return "air" end
        if c == 1 then return "helo" end
        if c == 3 then return "ship" end
        local function has(attr) local ok, v = pcall(obj.hasAttribute, obj, attr) return ok and v end
        if has("SAM related") or has("AAA") then return "sam" end
        if has("Infantry") then return "infantry" end
        return "ground"
    end

    -- {"type","side","player","category"} of a unit/static, or null.
    local function who(obj)
        if not obj then return "null", nil end
        local okE, exists = pcall(obj.isExist, obj)
        local okT, t = pcall(obj.getTypeName, obj)
        local okS, s = pcall(obj.getCoalition, obj)
        local okP, p = pcall(function() return obj.getPlayerName and obj:getPlayerName() or nil end)
        local player = okP and p or nil
        return string.format('{"type":%s,"side":%s,"player":%s,"category":%s}',
            jstr(okT and t or "?"), jstr(SIDE[okS and s or 0] or "neutral"),
            player and jstr(player) or "null", jstr(categoryOf(obj))), player, okS and s or nil
    end

    local function placeName(place)
        if not place then return "null" end
        local ok, n = pcall(place.getName, place)
        return ok and n and jstr(n) or "null"
    end

    local KINDS = {
        [E.S_EVENT_BIRTH] = "spawn", [E.S_EVENT_TAKEOFF] = "takeoff", [E.S_EVENT_LAND] = "landing",
        [E.S_EVENT_CRASH] = "crash", [E.S_EVENT_EJECTION] = "eject", [E.S_EVENT_PILOT_DEAD] = "pilot_dead",
        [E.S_EVENT_PLAYER_LEAVE_UNIT] = "leave_unit", [E.S_EVENT_KILL] = "kill",
    }

    local handler = {}
    function handler:onEvent(e)
        pcall(function()
            local kind = KINDS[e.id]
            if not kind then return end
            local unit, unitPlayer, unitSide = who(e.initiator)
            if kind == "kill" then
                local target, targetPlayer, targetSide = who(e.target)
                if not unitPlayer and not targetPlayer then return end  -- AI against AI: not kept
                local weapon = e.weapon_name
                if not weapon and e.weapon then
                    local okW, w = pcall(e.weapon.getTypeName, e.weapon)
                    weapon = okW and w or nil
                end
                local friendly = unitSide and targetSide and unitSide == targetSide
                queue[#queue + 1] = string.format('{"kind":"kill","t":%.1f,"unit":%s,"target":%s,"weapon":%s,"friendly":%s}',
                    timer.getTime(), unit, target, weapon and jstr(weapon) or "null", friendly and "true" or "false")
                return
            end
            if not unitPlayer then return end  -- only players' sorties
            local extra = ""
            if kind == "takeoff" or kind == "landing" then extra = ',"place":' .. placeName(e.place) end
            queue[#queue + 1] = string.format('{"kind":%s,"t":%.1f,"unit":%s%s}', jstr(kind), timer.getTime(), unit, extra)
        end)
        if #queue > 2000 then table.remove(queue, 1) end
    end
    if DCB_analyticsHandler then world.removeEventHandler(DCB_analyticsHandler) end
    DCB_analyticsHandler = handler
    world.addEventHandler(handler)

    return { drain = function()
        if #queue == 0 then return "" end
        local out = table.concat(queue, "\n")
        queue = {}
        return out
    end }
end)()

function DCB_analyticsDrain()
    local ok, result = pcall(DCB_analytics.drain)
    return ok and result or ""
end

-- DCB collector. Injected into the mission scripting env ('server' state) by
-- DCB-hook.lua; defines DCB_collect(), which returns the blue picture JSON,
-- a newline, and the red picture JSON. Each side gets its own units plus only
-- the enemy units its radars detect (fog of war).
DCB_collect = (function()
    local SIDES = { { name = "blue", side = coalition.side.BLUE }, { name = "red", side = coalition.side.RED } }
    local CATEGORY = { [0] = "airplane", [1] = "helicopter", [2] = "ground", [3] = "ship", [4] = "structure" }
    local GROUP_CATEGORIES = { Group.Category.AIRPLANE, Group.Category.HELICOPTER, Group.Category.GROUND, Group.Category.SHIP }

    local function jstr(s)
        s = tostring(s)
        s = s:gsub('[%c"\\]', function(c)
            if c == '"' then return '\\"' end
            if c == "\\" then return "\\\\" end
            return string.format("\\u%04x", c:byte())
        end)
        return '"' .. s .. '"'
    end

    local function jnum(n, fmt)
        if type(n) ~= "number" or n ~= n or n == math.huge or n == -math.huge then return "null" end
        return string.format(fmt, n)
    end

    -- Radar transmitting. Only units with a radar sensor are asked; getRadar can
    -- raise on some unit types, so it is guarded.
    local function isEmitting(u)
        local ok, hasRadar = pcall(u.hasSensors, u, Unit.SensorType.RADAR)
        if not ok or not hasRadar then return false end
        local ok2, active = pcall(u.getRadar, u)
        return ok2 and active == true
    end

    -- Kinematics shared by own and detected units. A moving unit's heading is taken from its
    -- velocity (its track): getPosition builds four tables per call, the largest share of the
    -- garbage a picture makes on a big server (perf probe, 2026-10-22).
    local function kinematics(u)
        local p = u:getPoint()
        local lat, lon, alt = coord.LOtoLL(p)
        local v = u:getVelocity()
        local spd = math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z)
        local hdg
        if spd > 2 then
            hdg = math.deg(math.atan2(v.z, v.x))
        else
            local pos = u:getPosition()
            hdg = math.deg(math.atan2(pos.x.z, pos.x.x))
        end
        if hdg < 0 then hdg = hdg + 360 end
        return string.format('"lat":%s,"lon":%s,"alt":%s,"hdg":%s,"spd":%s,"vs":%s,"emitter":%s',
            jnum(lat, "%.6f"), jnum(lon, "%.6f"), jnum(p.y, "%.1f"), jnum(hdg, "%.1f"), jnum(spd, "%.1f"),
            jnum(v.y, "%.1f"), isEmitting(u) and "true" or "false")
    end

    local function unitCategory(u)
        local desc = u:getDesc()
        return CATEGORY[desc and desc.category] or "unknown"
    end

    -- The unchanging part of an own unit's JSON (id, names, type, category, side), kept per unit
    -- and rebuilt only when its player changes (a player taking or leaving a slot).
    local prefixes = {}

    local function ownUnit(u, sideName)
        local id = u:getID()
        local player = u:getPlayerName()
        local c = prefixes[id]
        if not c or c.player ~= player then
            local g = u:getGroup()
            c = { player = player, json = string.format('{"id":%d,"name":%s,"type":%s,"callsign":%s,"player":%s,"group":%s,"category":%s,"coalition":%s,"detected":false,',
                id, jstr(u:getName()), jstr(u:getTypeName()), jstr(u:getCallsign() or ""),
                player and jstr(player) or "null", jstr(g and g:getName() or ""),
                jstr(unitCategory(u)), jstr(sideName)) }
            prefixes[id] = c
        end
        return c.json .. kinematics(u) .. "}"
    end

    -- A radar contact: no name, callsign or player; type only when DCS says it is identified.
    local function contact(u, typeKnown, enemyName)
        return string.format('{"id":%d,"name":null,"type":%s,"callsign":null,"player":null,"group":null,"category":%s,"coalition":%s,"detected":true,%s}',
            u:getID(), typeKnown and jstr(u:getTypeName()) or "null", jstr(unitCategory(u)), jstr(enemyName), kinematics(u))
    end

    local function isLiveUnit(obj)
        if not obj or not obj.isExist or not obj:isExist() then return false end
        local ok, cat = pcall(Object.getCategory, obj)
        return ok and cat == Object.Category.UNIT
    end

    -- Enemy ground units as radar contacts: the server's enemy_ground setting (DCB.lua), "auto"
    -- meaning only in missions dated enemyGroundYear or later. Otherwise they never appear and
    -- ground threats are found by RWR triangulation.
    local enemyGround = (function()
        local s = DCB_settings or {}
        if s.enemyGround == true or s.enemyGround == false then return s.enemyGround end
        local year = env.mission.date and tonumber(env.mission.date.Year) or 0
        return year >= (tonumber(s.enemyGroundYear) or 2012)
    end)()

    local function isGroundUnit(obj)
        local ok, desc = pcall(obj.getDesc, obj)
        return ok and type(desc) == "table" and desc.category == Unit.Category.GROUND_UNIT
    end

    local function addContacts(ctrl, mySide, contacts)
        if not ctrl then return end
        local ok, targets = pcall(ctrl.getDetectedTargets, ctrl, Controller.Detection.RADAR)
        if not ok or type(targets) ~= "table" then return end
        for _, t in ipairs(targets) do
            local obj = t.object
            if isLiveUnit(obj) and (enemyGround or not isGroundUnit(obj)) then
                local c = obj:getCoalition()
                if c ~= mySide and c ~= coalition.side.NEUTRAL then
                    local id = obj:getID()
                    local prev = contacts[id]
                    if not prev or (t.type and not prev.typeKnown) then
                        contacts[id] = { unit = obj, typeKnown = t.type and true or false }
                    end
                end
            end
        end
    end

    local SIDE_NAME = { [coalition.side.RED] = "red", [coalition.side.BLUE] = "blue", [coalition.side.NEUTRAL] = "neutral" }

    -- Live missiles, read once for both pictures; dead ones leave the list. Each carries
    -- its own side and its target's side (who may see it), and the kinematics JSON.
    local function flyingMissiles()
        local live, out, kept = DCB_missiles, {}, {}
        for _, m in ipairs(live) do
            local w = m.weapon
            local okE, exists = pcall(w.isExist, w)
            if okE and exists and timer.getTime() - m.t < 600 then
                kept[#kept + 1] = m
                local p = w:getPoint()
                local lat, lon, alt = coord.LOtoLL(p)
                local v = w:getVelocity()
                local hdg = math.deg(math.atan2(v.z, v.x))
                if hdg < 0 then hdg = hdg + 360 end
                local targetId, targetSide = nil, nil
                local okT, tgt = pcall(w.getTarget, w)
                if okT and isLiveUnit(tgt) then
                    targetId, targetSide = tonumber(tgt:getID()), tgt:getCoalition()
                end
                out[#out + 1] = { m = m, targetId = targetId, targetSide = targetSide,
                    kin = string.format('"lat":%s,"lon":%s,"alt":%s,"hdg":%s,"spd":%s,"vs":%s',
                        jnum(lat, "%.6f"), jnum(lon, "%.6f"), jnum(p.y, "%.1f"), jnum(hdg, "%.1f"),
                        jnum(math.sqrt(v.x * v.x + v.y * v.y + v.z * v.z), "%.1f"), jnum(v.y, "%.1f")) }
            end
        end
        for i = #live, 1, -1 do live[i] = nil end
        for i, m in ipairs(kept) do live[i] = m end
        return out
    end

    -- A side sees its own missiles in full and the enemy's only when aimed at its own units
    -- (then without type or shooter: what a warning receiver could tell).
    local function weaponsFor(side, missiles)
        local out = {}
        for _, f in ipairs(missiles) do
            local m = f.m
            if m.side == side then
                out[#out + 1] = string.format('{"id":%d,"type":%s,"coalition":%s,"shooter":%s,"target":%s,%s}',
                    m.id, jstr(m.type or ""), jstr(SIDE_NAME[m.side] or "neutral"),
                    m.shooter and string.format("%d", m.shooter) or "null",
                    f.targetId and string.format("%d", f.targetId) or "null", f.kin)
            elseif f.targetSide == side then
                out[#out + 1] = string.format('{"id":%d,"type":null,"coalition":%s,"shooter":null,"target":%d,%s}',
                    m.id, jstr(SIDE_NAME[m.side] or "neutral"), f.targetId, f.kin)
            end
        end
        return table.concat(out, ",")
    end

    local NEAR_M = 10 * 1852  -- aircraft this close to one of their side's carriers get the fast feed

    local function headingOf(pos)
        local h = math.deg(math.atan2(pos.x.z, pos.x.x))
        return h < 0 and h + 360 or h
    end

    -- The units of each side by kind, from a full walk of the groups every RESCAN_S seconds or
    -- sooner after a birth (Foothold spawns): the seconds between only check the known units.
    -- Late-activated groups therefore appear within RESCAN_S. Ground and ship groups with a
    -- radar are the ones asked for contacts (most ground groups have none).
    local RESCAN_S, GROUND_S = 5, 5
    local known = { nextScan = 0, sides = {} }
    local groundCache = {}  -- unit id -> { json, next }: see groundUnit

    local function hasRadar(u)
        local ok, r = pcall(u.hasSensors, u, Unit.SensorType.RADAR)
        return ok and r == true
    end

    local function rescan(now)
        for _, entry in ipairs(SIDES) do
            local s = { air = {}, ships = {}, ground = {}, radarGroups = {} }
            for _, gcat in ipairs(GROUP_CATEGORIES) do
                for _, g in ipairs(coalition.getGroups(entry.side, gcat) or {}) do
                    if g:isExist() then
                        local radar = false
                        for _, u in ipairs(g:getUnits() or {}) do
                            if u:isExist() and u:isActive() then
                                if gcat == Group.Category.AIRPLANE or gcat == Group.Category.HELICOPTER then
                                    s.air[#s.air + 1] = u
                                elseif gcat == Group.Category.SHIP then
                                    s.ships[#s.ships + 1] = u
                                    radar = radar or hasRadar(u)
                                else
                                    s.ground[#s.ground + 1] = u
                                    radar = radar or hasRadar(u)
                                end
                            end
                        end
                        if radar then s.radarGroups[#s.radarGroups + 1] = g end
                    end
                end
            end
            known.sides[entry.side] = s
        end
        known.nextScan = now + RESCAN_S
        -- Forget the cached JSON of units that are gone (Foothold spawns and loses many).
        local alive = {}
        for _, s in pairs(known.sides) do
            for _, list in ipairs({ s.air, s.ships, s.ground }) do
                for _, u in ipairs(list) do alive[u:getID()] = true end
            end
        end
        for id in pairs(prefixes) do if not alive[id] then prefixes[id] = nil end end
        for id in pairs(groundCache) do if not alive[id] then groundCache[id] = nil end end
    end

    -- A unit born (spawned or activated): walk again at the next picture.
    local birth = {}
    function birth:onEvent(e)
        if e.id == world.event.S_EVENT_BIRTH then known.nextScan = 0 end
    end
    if DCB_birthHandler then world.removeEventHandler(DCB_birthHandler) end
    DCB_birthHandler = birth
    world.addEventHandler(birth)

    -- Ground units and structures barely move: their JSON is rebuilt every GROUND_S seconds.
    local function groundUnit(u, sideName, now)
        local id = u:getID()
        local c = groundCache[id]
        if not c or now >= c.next then
            c = { json = ownUnit(u, sideName), next = now + GROUND_S }
            groundCache[id] = c
        end
        return c.json
    end

    -- The side's carriers (beacons, wind 20 m above the deck) as JSON; the side's aircraft near
    -- them are remembered for DCB_lso.
    local function carriersFor(entry)
        local list, carriers = {}, {}
        local s = known.sides[entry.side] or { ships = {}, air = {} }
        do
            for _, u in ipairs(s.ships) do
                if u:isExist() and DCB_carrier.types[u:getTypeName()] then
                    carriers[#carriers + 1] = u
                    local p = u:getPoint()
                    local okW, w = pcall(atmosphere.getWind, { x = p.x, y = p.y + 20, z = p.z })
                    local wx, wz = okW and w and w.x or 0, okW and w and w.z or 0
                    local b = DCB_carrier.beacons(tonumber(u:getID()))
                    list[#list + 1] = string.format('{"id":%s,"type":%s,"windFrom":%s,"windMs":%s,"tacan":%s,"icls":%s,"link4":%s,"acls":%s,"radio":%s}',
                        jnum(tonumber(u:getID()), "%.0f"), jstr(u:getTypeName()),
                        jnum((math.deg(math.atan2(-wz, -wx)) + 360) % 360, "%.0f"), jnum(math.sqrt(wx * wx + wz * wz), "%.1f"),
                        b.tacan and jstr(b.tacan) or "null", b.icls and jnum(b.icls, "%.0f") or "null",
                        b.link4 and jnum(b.link4 / 1e6, "%.3f") or "null", b.acls and "true" or "false",
                        b.radio and jnum(b.radio / 1e6, "%.3f") or "null")
                end
            end
        end
        local near = {}
        if #carriers > 0 then
            local cps = {}
            for i, c in ipairs(carriers) do cps[i] = c:getPoint() end
            for _, u in ipairs(s.air) do
                if u:isExist() then
                    local p = u:getPoint()
                    for _, cp in ipairs(cps) do
                        if (p.x - cp.x) ^ 2 + (p.z - cp.z) ^ 2 < NEAR_M ^ 2 then
                            near[#near + 1] = u
                            break
                        end
                    end
                end
            end
        end
        DCB_carrier.near[entry.side] = { carriers = carriers, aircraft = near }
        return table.concat(list, ",")
    end

    -- The side's landings on carriers in the last minute (the client keeps each id once).
    local function eventsFor(entry)
        local out, now = {}, timer.getTime()
        for _, e in ipairs(DCB_carrier.events) do
            if e.side == entry.side and now - e.t < 60 then
                out[#out + 1] = string.format('{"id":%d,"kind":%s,"unit":%s,"carrier":%s,"time":%s,"x":%s,"z":%s,"cx":%s,"cz":%s}',
                    e.id, jstr(e.kind), jnum(e.unit, "%.0f"), jnum(e.carrier, "%.0f"), jnum(e.t, "%.1f"),
                    jnum(e.x, "%.1f"), jnum(e.z, "%.1f"), jnum(e.cx, "%.1f"), jnum(e.cz, "%.1f"))
            end
        end
        return table.concat(out, ",")
    end

    -- The fast LSO feed: per side with aircraft near its carriers, one line with the carriers'
    -- and those aircraft's state (DCS metres, m/s, degrees; gear and hook 0..1 from the model's
    -- draw arguments 0 and 25, which most carrier aircraft use).
    function DCB_lso()
        local ok, result = pcall(function()
            local lines = {}
            for _, entry in ipairs(SIDES) do
                local near = DCB_carrier.near[entry.side]
                if near and #near.aircraft > 0 then
                    local cs, as = {}, {}
                    for _, c in ipairs(near.carriers) do
                        if c:isExist() then
                            local p, v = c:getPoint(), c:getVelocity()
                            cs[#cs + 1] = string.format('{"id":%s,"x":%.2f,"y":%.2f,"z":%.2f,"hdg":%.2f,"vx":%.2f,"vz":%.2f}',
                                jnum(tonumber(c:getID()), "%.0f"), p.x, p.y, p.z, headingOf(c:getPosition()), v.x, v.z)
                        end
                    end
                    for _, u in ipairs(near.aircraft) do
                        if u:isExist() then
                            local p, pos, v = u:getPoint(), u:getPosition(), u:getVelocity()
                            local pitch = math.deg(math.asin(math.max(-1, math.min(1, pos.x.y))))
                            local okG, gear = pcall(u.getDrawArgumentValue, u, 0)
                            local okH, hook = pcall(u.getDrawArgumentValue, u, 25)
                            as[#as + 1] = string.format('{"id":%s,"x":%.2f,"y":%.2f,"z":%.2f,"vx":%.2f,"vy":%.2f,"vz":%.2f,"hdg":%.2f,"pitch":%.2f,"gear":%s,"hook":%s}',
                                jnum(tonumber(u:getID()), "%.0f"), p.x, p.y, p.z, v.x, v.y, v.z, headingOf(pos), pitch,
                                okG and jnum(gear, "%.2f") or "null", okH and jnum(hook, "%.2f") or "null")
                        end
                    end
                    lines[#lines + 1] = string.format('{"type":"lso","coalition":"%s","time":%.2f,"carriers":[%s],"aircraft":[%s]}',
                        entry.name, timer.getTime(), table.concat(cs, ","), table.concat(as, ","))
                end
            end
            return table.concat(lines, "\n")
        end)
        return ok and result or ("ERR:" .. tostring(result))
    end

    local function picture(entry, enemyName, missiles, now)
        local units, contacts = {}, {}
        local s = known.sides[entry.side]
        -- Ground and naval sensors are driven by the group controller, aircraft (AWACS,
        -- fighters) by their unit controllers.
        for _, g in ipairs(s.radarGroups) do
            if g:isExist() then addContacts(g:getController(), entry.side, contacts) end
        end
        for _, u in ipairs(s.air) do
            if u:isExist() then
                units[#units + 1] = ownUnit(u, entry.name)
                addContacts(u:getController(), entry.side, contacts)
            end
        end
        for _, u in ipairs(s.ships) do
            if u:isExist() then units[#units + 1] = ownUnit(u, entry.name) end
        end
        for _, u in ipairs(s.ground) do
            if u:isExist() then units[#units + 1] = groundUnit(u, entry.name, now) end
        end
        for _, c in pairs(contacts) do
            units[#units + 1] = contact(c.unit, c.typeKnown, enemyName)
        end
        -- The side's own bullseye: scripts can move it, so read it with every picture.
        local bulls = "null"
        local okB, ref = pcall(coalition.getMainRefPoint, entry.side)
        if okB and ref then
            local lat, lon = coord.LOtoLL(ref)
            bulls = string.format('{"x":%s,"z":%s,"lat":%s,"lon":%s}', jnum(ref.x, "%.1f"), jnum(ref.z, "%.1f"), jnum(lat, "%.6f"), jnum(lon, "%.6f"))
        end
        return string.format('{"type":"picture","version":1,"coalition":"%s","time":%s,"theatre":%s,"bullseye":%s,"units":[%s],"weapons":[%s],"carriers":[%s],"events":[%s]}',
            entry.name, jnum(timer.getTime(), "%.1f"), jstr(env.mission.theatre or ""), bulls, table.concat(units, ","),
            weaponsFor(entry.side, missiles), carriersFor(entry), eventsFor(entry))
    end

    return function()
        local ok, result = pcall(function()
            local now = timer.getTime()
            if now >= known.nextScan then rescan(now) end
            local missiles = flyingMissiles()
            return picture(SIDES[1], "red", missiles, now) .. "\n" .. picture(SIDES[2], "blue", missiles, now)
        end)
        if ok then return result end
        return "ERR:" .. tostring(result)
    end
end)()
