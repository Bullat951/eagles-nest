-- DCB Foothold exporter. Injected by DCB-hook.lua into the mission's trigger scripting env
-- (via a_do_script), where Foothold's BattleCommander `bc` lives; that env is not the
-- 'server' state the collector runs in. DCB_footholdPush() serialises the zone picture and
-- hands it to the 'server' state as DCB_footholdJson for the hook to publish. Read-only:
-- nothing in Foothold is changed.
DCB_footholdPush = (function()
    local positions = {}  -- trigger zone centre/radius by zone name (static)

    local function jstr(s)
        s = tostring(s):gsub('[%c"\\]', function(c)
            if c == '"' then return '\\"' end
            if c == "\\" then return "\\\\" end
            return string.format("\\u%04x", c:byte())
        end)
        return '"' .. s .. '"'
    end

    local function count(t)
        local n = 0
        if type(t) == "table" then for _ in pairs(t) do n = n + 1 end end
        return n
    end

    local function position(name)
        local p = positions[name]
        if p == nil then
            local tz = trigger.misc.getZone(name)
            p = tz and { x = tz.point.x, z = tz.point.z, r = tz.radius or 0 } or false
            positions[name] = p
        end
        return p or nil
    end

    local prevSide, prevMissions = nil, nil   -- for capture and mission start/end events
    local events = {}
    local nextStats = 0

    local function event(json) events[#events + 1] = json end

    -- Foothold's mission board: running missions ("active") and escort missions on offer
    -- ("available"), with the texts decoded the way Foothold prints them (L10N).
    local function missions()
        if type(mc) ~= "table" or type(mc.missions) ~= "table" then return "", {} end
        local list, titles = {}, {}
        for _, v in ipairs(mc.missions) do
            local status
            if v.isRunning then
                status = "active"
            elseif v.isEscortMission and not v.accept and not v.denied then
                local okA, active = pcall(v.isActive, v)
                if okA and active then status = "available" end
            end
            if status then
                local okT, title = pcall(mc.decodeMessage, mc, v.title, L10N)
                local okD, desc = pcall(mc.decodeMessage, mc, v.description, L10N)
                local okS, side = pcall(mc.getMissionSide, mc, v)
                local okK, strike = pcall(mc.isStrikeMission, mc, v)
                title = okT and title or "?"
                list[#list + 1] = string.format('{"title":%s,"description":%s,"side":%d,"status":%s,"strike":%s}',
                    jstr(title), jstr(okD and desc or ""), tonumber(okS and side or v.side) or 0, jstr(status),
                    (okK and strike) and "true" or "false")
                if status == "active" then titles[title] = tonumber(okS and side or v.side) or 0 end
            end
        end
        return table.concat(list, ","), titles
    end

    -- Every 5 minutes: Foothold's own player scores. Keyed by UCID in Foothold; the raw UCID
    -- only travels to the hook, which replaces it with the keyed id before writing anything.
    local function playerStats()
        if type(bc.playerStatsByUcid) ~= "table" then return nil end
        local rows = {}
        for ucid, rec in pairs(bc.playerStatsByUcid) do
            if type(rec) == "table" and type(rec.stats) == "table" then
                local stats = {}
                for k, v in pairs(rec.stats) do
                    if type(v) == "number" then stats[#stats + 1] = jstr(k) .. ":" .. string.format("%.0f", v) end
                end
                local okR, rank = pcall(bc.getPlayerRank, bc, rec.name)
                local okN, rankName = pcall(bc.getRankName, bc, okR and rank or 0)
                local okC, credits = pcall(bc.getPlayerCredits, bc, rec.name)
                rows[#rows + 1] = string.format('{"ucid":%s,"name":%s,"rank":%s,"credits":%s,"stats":{%s}}',
                    jstr(ucid), jstr(rec.name or "?"), (okN and rankName) and jstr(rankName) or "null",
                    (okC and type(credits) == "number") and string.format("%.0f", credits) or "null", table.concat(stats, ","))
            end
        end
        return '{"kind":"foothold_stats","players":[' .. table.concat(rows, ",") .. "]}"
    end

    local function build()
        local zones, visible, sides = {}, {}, {}
        for _, z in ipairs(bc.zones or {}) do
            local p = position(z.zone)
            if p and not z.isHidden then
                visible[z.zone] = true
                local side = tonumber(z.side) or 0
                sides[z.zone] = side
                local upgrades = type(z.upgrades) == "table" and z.upgrades[side == 2 and "blue" or "red"] or nil
                local wpt = type(z.flavorText) == "string" and z.flavorText:match("WPT%s*(%d+)") or nil
                local lat, lon = coord.LOtoLL({ x = p.x, y = 0, z = p.z })
                zones[#zones + 1] = string.format(
                    '{"name":%s,"side":%d,"x":%.0f,"z":%.0f,"r":%.0f,"lat":%.5f,"lon":%.5f,"active":%s,"suspended":%s,"airbase":%s,"facility":%s,"logistic":%s,"built":%d,"max":%d,"wpt":%s}',
                    jstr(z.zone), side, p.x, p.z, p.r, lat, lon,
                    z.active == false and "false" or "true", z.suspended and "true" or "false",
                    z.airbaseName and jstr(z.airbaseName) or "null", z.facility and jstr(z.facility) or "null",
                    z.LogisticCenter and "true" or "false", count(z.built), count(upgrades), wpt or "null")
            end
        end
        -- Captures since the last snapshot (the first snapshot only learns the sides).
        local t = timer.getTime()
        if prevSide then
            for name, side in pairs(sides) do
                local was = prevSide[name]
                if was and was ~= side then
                    event(string.format('{"kind":"zone_captured","t":%.1f,"zone":%s,"from":%d,"to":%d}', t, jstr(name), was, side))
                end
            end
        end
        prevSide = sides
        local missionJson, titles = missions()
        if prevMissions then
            for title, side in pairs(titles) do
                if prevMissions[title] == nil then event(string.format('{"kind":"mission_started","t":%.1f,"title":%s,"side":%d}', t, jstr(title), side)) end
            end
            for title, side in pairs(prevMissions) do
                if titles[title] == nil then event(string.format('{"kind":"mission_ended","t":%.1f,"title":%s,"side":%d}', t, jstr(title), side)) end
            end
        end
        prevMissions = titles
        local accounts = type(bc.accounts) == "table" and bc.accounts or {}
        local links = {}
        local ok, conns = pcall(function() return bc:_dynamicHybridGetConnections() end)
        if not ok or type(conns) ~= "table" then conns = bc.connections or {} end
        for _, c in ipairs(conns) do
            if visible[c.from] and visible[c.to] then links[#links + 1] = "[" .. jstr(c.from) .. "," .. jstr(c.to) .. "]" end
        end
        return '{"type":"foothold","zones":[' .. table.concat(zones, ",") .. '],"links":[' .. table.concat(links, ",") ..
            '],"missions":[' .. missionJson .. '],"credits":{"red":' .. string.format("%.0f", tonumber(accounts[1]) or 0) ..
            ',"blue":' .. string.format("%.0f", tonumber(accounts[2]) or 0) .. "}}"
    end

    return function()
        local json
        if type(bc) ~= "table" or type(bc.zones) ~= "table" then
            json = "none"
        else
            local ok, result = pcall(build)
            json = ok and result or ("ERR:" .. tostring(result))
        end
        net.dostring_in("server", "DCB_footholdJson = " .. string.format("%q", json))
        -- Events and (every 5 minutes) player scores go to the hook's analytics log.
        if type(bc) == "table" and timer.getTime() >= nextStats then
            nextStats = timer.getTime() + 300
            local okS, stats = pcall(playerStats)
            if okS and stats then events[#events + 1] = stats end
        end
        if #events > 0 then
            net.dostring_in("server", "DCB_footholdEvents = (DCB_footholdEvents or '') .. " .. string.format("%q", table.concat(events, "\n") .. "\n"))
            events = {}
        end
    end
end)()
