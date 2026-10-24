-- env: server
-- Identification phase 1, census (docs/RESEARCH-IDENTIFICATION.md): units, radars on, what they
-- track, RWR receivers, receiver x emitter pairs within assumed emission range, and whether radars
-- list weapons among detected targets. Set LOS_REPEAT > 0 to also run land.isVisible on every
-- in-range pair that many times (probe time difference = LOS cost).
local LOS_REPEAT = 0
local RANGE = { [0] = 150000, [1] = 60000, [2] = 200000, [3] = 200000 }  -- by group category: airplane, helo, ground, ship
local out = {}
local function p(fmt, ...) out[#out + 1] = string.format(fmt, ...) end

local catName = { [0] = "airplane", [1] = "helicopter", [2] = "ground", [3] = "ship" }
local units = { [1] = {}, [2] = {} }
local stats = {}
local function stat(k) stats[k] = (stats[k] or 0) + 1 end
local trackedCat = {}

for _, side in ipairs({ 1, 2 }) do
    for cat = 0, 3 do
        for _, g in ipairs(coalition.getGroups(side, cat) or {}) do
            for _, u in ipairs(g:getUnits() or {}) do
                if u:isExist() and u:isActive() then
                    local e = { u = u, cat = cat, pos = u:getPoint(), player = u:getPlayerName() ~= nil }
                    local okR, hasRadar = pcall(u.hasSensors, u, Unit.SensorType.RADAR)
                    local okW, hasRwr = pcall(u.hasSensors, u, Unit.SensorType.RWR)
                    e.radar = okR and hasRadar
                    e.rwr = okW and hasRwr
                    local key = side .. "/" .. catName[cat]
                    stat(key .. " units")
                    if e.player then stat(key .. " players") end
                    if e.rwr then stat(key .. " with RWR") end
                    if e.radar then
                        stat(key .. " with radar")
                        local ok, on, tracked = pcall(u.getRadar, u)
                        if ok and on then
                            e.on = true
                            stat(key .. " radar ON")
                            if tracked then
                                stat(key .. " tracking")
                                local okc, oc = pcall(tracked.getCategory, tracked)
                                local k = key .. " tracks cat " .. tostring(okc and oc or "?")
                                trackedCat[k] = (trackedCat[k] or 0) + 1
                            end
                        end
                    end
                    units[side][#units[side] + 1] = e
                end
            end
        end
    end
end

local keys = {}
for k in pairs(stats) do keys[#keys + 1] = k end
table.sort(keys)
for _, k in ipairs(keys) do p("%-34s %d", k, stats[k]) end
for k, n in pairs(trackedCat) do p("%-34s %d", k, n) end

-- Receiver (own, RWR, airborne or ship) x emitter (enemy, radar on) pairs.
local pairsAll, pairsInRange, los = 0, 0, {}
for _, side in ipairs({ 1, 2 }) do
    local enemy = side == 1 and 2 or 1
    for _, r in ipairs(units[side]) do
        if r.rwr or r.cat == 3 then
            for _, e in ipairs(units[enemy]) do
                if e.on then
                    pairsAll = pairsAll + 1
                    local dx, dz = r.pos.x - e.pos.x, r.pos.z - e.pos.z
                    if dx * dx + dz * dz <= RANGE[e.cat] ^ 2 then
                        pairsInRange = pairsInRange + 1
                        los[#los + 1] = { { x = e.pos.x, y = e.pos.y + 5, z = e.pos.z }, { x = r.pos.x, y = r.pos.y + 2, z = r.pos.z } }
                    end
                end
            end
        end
    end
end
p("pairs receiver x emitter-on: %d, within assumed range: %d", pairsAll, pairsInRange)

local visible = 0
for _, l in ipairs(los) do if land.isVisible(l[1], l[2]) then visible = visible + 1 end end
p("in-range pairs with line of sight: %d", visible)
for _ = 1, LOS_REPEAT do
    for _, l in ipairs(los) do land.isVisible(l[1], l[2]) end
end
if LOS_REPEAT > 0 then p("LOS calls repeated: %d x %d = %d", LOS_REPEAT, #los, LOS_REPEAT * #los) end

-- Do radars list weapons among detected targets?
local weaponsSeen, radarControllers = 0, 0
for _, side in ipairs({ 1, 2 }) do
    for _, e in ipairs(units[side]) do
        if e.on and not e.player then
            local ok, ctrl = pcall(e.u.getController, e.u)
            if ok and ctrl then
                radarControllers = radarControllers + 1
                local okd, list = pcall(ctrl.getDetectedTargets, ctrl, Controller.Detection.RADAR)
                if okd and list then
                    for _, t in ipairs(list) do
                        local okc, c = pcall(function() return t.object:getCategory() end)
                        if okc and c == Object.Category.WEAPON then weaponsSeen = weaponsSeen + 1 end
                    end
                end
            end
        end
    end
end
p("radar controllers asked: %d, weapons among their detected targets: %d", radarControllers, weaponsSeen)
p("mission time %.0f s", timer.getTime())
return table.concat(out, "\n")
