-- env: server
-- Jamming probe 3 (docs/RESEARCH-IDENTIFICATION.md): does any DCS radar report a jamming aircraft
-- differently through the scripting API? Variables: jammer type, ECM option (always / never),
-- receiver kind (ship, AWACS, EWR, SAM). Every jet is immortal, weapons hold, no reaction, and
-- flies alone 40 km from the next. Every 5 s, per jet and receiver: radar detected, distance and
-- type known (getDetectedTargets), and the error of the position DCS reports (isTargetDetected
-- lastPos). Only changes are logged (DCB_jamM), plus the loadout DCS gave each jet (DCB_jamLoad).
local NM = 1852
local function opt(id, value) return { id = "WrappedAction", params = { action = { id = "Option", params = { name = id, value = value } } } } end

local function spawn(name, country_, typ, fuel, pylons, ecm, sx, sz, tx, tz)
    local g = Group.getByName(name) if g then g:destroy() end
    local hdg = math.atan2(tz - sz, tx - sx)
    coalition.addGroup(country_, Group.Category.AIRPLANE, { name = name, task = "CAP", route = { points = {
        { x = sx, y = sz, alt = 7000, alt_type = "BARO", speed = 220, type = "Turning Point", action = "Turning Point",
          task = { id = "ComboTask", params = { tasks = {
            opt(AI.Option.Air.id.ECM_USING, ecm), opt(AI.Option.Air.id.ROE, AI.Option.Air.val.ROE.WEAPON_HOLD),
            opt(AI.Option.Air.id.REACTION_ON_THREAT, AI.Option.Air.val.REACTION_ON_THREAT.NO_REACTION),
            { id = "WrappedAction", params = { action = { id = "SetImmortal", params = { value = true } } } },
          } } } },
        { x = tx, y = tz, alt = 7000, alt_type = "BARO", speed = 220, type = "Turning Point", action = "Turning Point", task = { id = "ComboTask", params = { tasks = {} } } },
    } }, units = { { name = name .. " 1", type = typ, x = sx, y = sz, alt = 7000, speed = 220, skill = "High", heading = hdg,
        payload = { pylons = pylons, fuel = fuel, flare = 60, chaff = 60, gun = 100 } } } })
end

local ALWAYS, NEVER = AI.Option.Air.val.ECM_USING.ALWAYS_USE, AI.Option.Air.val.ECM_USING.NEVER_USE
local cvn = Unit.getByName("CVN-74"):getPoint()
local ewr = Group.getByName("Red EWR Fixed 2"):getUnit(1):getPoint()

-- Red jammers inbound to the carriers from the east (blue receivers: ships, E-3).
local RU, US = country.id.RUSSIA, country.id.USA
local red = {
    { "JM Su34 ECM", "Su-34", 12100, {}, ALWAYS }, { "JM Su34 off", "Su-34", 12100, {}, NEVER },
    { "JM Tu22 ECM", "Tu-22M3", 50000, {}, ALWAYS }, { "JM Tu22 off", "Tu-22M3", 50000, {}, NEVER },
}
for i, j in ipairs(red) do
    local lateral = (i - 2.5) * 40000
    spawn(j[1], RU, j[2], j[3], j[4], j[5], cvn.x + lateral, cvn.z + 75 * NM, cvn.x + lateral * 0.2, cvn.z)
end
-- Blue jammers with ECM pods inbound to Red EWR Fixed 2 from the sea to the west (red receivers: EWRs, SAMs, A-50).
local ALQ184, ALQ131 = { { num = 5, CLSID = "ALQ_184" } }, { { num = 5, CLSID = "{6D21ECEA-F85B-4E8D-9D51-31DC9B8AA4EF}" } }
local blue = {
    { "JM F16 ECM", "F-16C_50", 3249, ALQ184, ALWAYS }, { "JM F16 off", "F-16C_50", 3249, ALQ184, NEVER },
    { "JM F4 ECM", "F-4E-45MC", 4800, ALQ131, ALWAYS }, { "JM F4 off", "F-4E-45MC", 4800, ALQ131, NEVER },
}
for i, j in ipairs(blue) do
    local lateral = (i - 2.5) * 40000
    spawn(j[1], US, j[2], j[3], j[4], j[5], ewr.x + lateral, ewr.z - 75 * NM, ewr.x + lateral * 0.2, ewr.z)
end

-- Receivers: blue ships and radiating AWACS for the red jets; red radiating ground radars and AWACS for the blue jets.
local function receivers(side)
    local list = {}
    for _, g in ipairs(coalition.getGroups(side, Group.Category.SHIP) or {}) do
        local u = g:getUnit(1)
        if u and u:isExist() then list[#list + 1] = { name = g:getName(), ctrl = g:getController(), unit = u } end
    end
    for _, g in ipairs(coalition.getGroups(side, Group.Category.GROUND) or {}) do
        for _, u in ipairs(g:getUnits()) do
            local okO, on = pcall(u.getRadar, u)
            if u:isExist() and okO and on then list[#list + 1] = { name = g:getName(), ctrl = g:getController(), unit = u } break end
        end
    end
    for _, g in ipairs(coalition.getGroups(side, Group.Category.AIRPLANE) or {}) do
        for _, u in ipairs(g:getUnits()) do
            if u:isExist() and u:hasAttribute("AWACS") and u:getRadar() then list[#list + 1] = { name = u:getName(), ctrl = u:getController(), unit = u } end
        end
    end
    return list
end
local blueRx, redRx = receivers(coalition.side.BLUE), receivers(coalition.side.RED)

DCB_jamGen = (DCB_jamGen or 0) + 1
DCB_jamM, DCB_jamLoad = {}, {}
local gen, last = DCB_jamGen, {}
local function sample()
    if gen ~= DCB_jamGen then return nil end
    local now = timer.getTime()
    for _, set in ipairs({ { red, blueRx }, { blue, redRx } }) do
        for _, j in ipairs(set[1]) do
            local u = Unit.getByName(j[1] .. " 1")
            if u and u:isExist() then
                if not DCB_jamLoad[j[1]] then
                    local ammo = {}
                    for _, a in ipairs(u:getAmmo() or {}) do ammo[#ammo + 1] = (a.desc and a.desc.displayName or "?") .. " x" .. tostring(a.count) end
                    DCB_jamLoad[j[1]] = table.concat(ammo, ", ")
                end
                local up = u:getPoint()
                for _, rx in ipairs(set[2]) do
                    if rx.unit:isExist() then
                        local rp = rx.unit:getPoint()
                        local dNm = math.sqrt((up.x - rp.x) ^ 2 + (up.z - rp.z) ^ 2) / NM
                        local state = "absent"
                        local ok, list = pcall(rx.ctrl.getDetectedTargets, rx.ctrl, Controller.Detection.RADAR)
                        for _, t in ipairs(ok and list or {}) do
                            if t.object and t.object:isExist() and t.object:getName() == u:getName() then
                                state = string.format("seen dist=%s type=%s", tostring(t.distance), tostring(t.type))
                            end
                        end
                        local r = { pcall(rx.ctrl.isTargetDetected, rx.ctrl, u, Controller.Detection.RADAR) }
                        local errKm = "-"
                        if r[1] and r[2] and type(r[7]) == "table" and r[7].x then
                            errKm = string.format("%.1f", math.sqrt((r[7].x - up.x) ^ 2 + (r[7].z - up.z) ^ 2) / 1000)
                        end
                        -- position error in 1 km steps so small drift does not flood the log
                        local key = j[1] .. ">" .. rx.name
                        local sig = state .. " poserr " .. (errKm == "-" and "-" or tostring(math.floor(tonumber(errKm) + 0.5)))
                        if last[key] ~= sig then
                            last[key] = sig
                            DCB_jamM[#DCB_jamM + 1] = string.format("%5.0f %-12s > %-24s %3.0f NM  %s  poserr %s km", now, j[1], rx.name:sub(1, 24), dNm, state, errKm)
                        end
                    end
                end
            end
        end
    end
    if #DCB_jamM < 3000 then return now + 5 end
end
timer.scheduleFunction(sample, nil, timer.getTime() + 3)
return string.format("spawned 8 jets; blue receivers %d, red receivers %d", #blueRx, #redRx)
