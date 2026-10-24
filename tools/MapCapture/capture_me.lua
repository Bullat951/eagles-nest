-- Eagle's Nest map capture: runs inside DCS's Mission Editor (loaded by one line the MapCapture
-- tool appends to MissionEditor/modules/me_map_window.lua; `MapCapture restore` removes it).
-- Inert until a command file appears in <Saved Games>\DCS\EaglesNest\capture\. The helper
-- (tools/MapCapture) plans everything; this script only obeys (docs/RESEARCH-MAPCAPTURE.md):
--   { op = 'setup', mpp = 8, hide = {...} }  Sat mode, DCS's markings off, zoom; writes job.lua
--   { op = 'visit', cams = { { id = 'a-12', x = .., z = .. }, ... } }  per camera: move, wait,
--        write step_<id>.lua (georeference) and ready.txt = id; the helper captures, done.txt = id
--   { op = 'finish' }  layers back as they were
-- Notes: the editor's Lua has no `debug` library; getMapPoint takes WINDOW coordinates.
local lfs = require('lfs')
local UpdateManager = require('UpdateManager')
local NewMapView = require('NewMapView')

local dir = lfs.writedir() .. 'EaglesNest\\capture\\'
local mw = package.loaded['me_map_window']
if not mw then return end

-- Older copies of this script retire when a newer one loads (generation); copies from before
-- the generation counter never read this version's command file.
EaglesNestCapture = EaglesNestCapture or {}
local COMMAND = 'command.v6.lua'
EaglesNestCapture.gen = (EaglesNestCapture.gen or 0) + 1
local myGen = EaglesNestCapture.gen

local function readFile(p)
    local f = io.open(p, 'rb')
    if not f then return nil end
    local s = f:read('*a')
    f:close()
    return s
end

local function writeFile(p, s)
    local f = io.open(p .. '.tmp', 'wb')
    if not f then return end
    f:write(s)
    f:close()
    os.remove(p)
    os.rename(p .. '.tmp', p)
end

local function log(s)
    local f = io.open(dir .. 'capture.log', 'a')
    if f then f:write(os.date('%H:%M:%S '), s, '\n'); f:close() end
end

local function num(v) return string.format('%.3f', v) end

-- The map window's rectangle (GUI units): MissionEditor.lua creates it at
-- (left_toolbar_w, top_toolbar_h, main_w − left_toolbar_w, right_panel_height).
local function mapBounds()
    if main_w and left_toolbar_w and top_toolbar_h and right_panel_height then
        return { x = left_toolbar_w, y = top_toolbar_h, w = main_w - left_toolbar_w, h = right_panel_height }
    end
end

local function metresPerUnit()
    local x1, y1 = mw.getMapPoint(0, 0)
    local x2, y2 = mw.getMapPoint(100, 0)
    return math.sqrt((x2 - x1) ^ 2 + (y2 - y1) ^ 2) / 100
end

-- Not hidden: AIRPORTS — in Sat mode it draws the airfields themselves (textured runways,
-- taxiways, shelters) along with each airfield's name; hiding it loses the pavement.
-- USER OBJECTS holds the beacon symbols (TACAN "KBL Chan 67X" …).
local DEFAULT_HIDE = { 'GEO NAME', 'MGRS GRID', 'GEOGRAPHICAL GRID', 'USER OBJECTS', 'SUPPLIERS OBJECTS',
    'PARKINGS', 'DRAWINGS', 'ELECTRIC POWER TRANSMISSION', 'BUILDINGS' }

local bounds
local queue, current, phase, wait, settle, busy = {}, nil, nil, 0, 120, false

-- Layers through the editor's own controller (as its layer panel does; mw.showLayer alone is
-- undone when the editor refreshes user objects such as beacons). Visibility is put back on finish.
local function setLayers(hide)
    local controller = package.loaded['Mission.MapLayerController']
    EaglesNestCapture.restoreLayers = EaglesNestCapture.restoreLayers or {}
    for _, title in ipairs(hide) do
        if EaglesNestCapture.restoreLayers[title] == nil then
            local ok, visible = pcall(function() return controller.getLayerVisible(title) end)
            EaglesNestCapture.restoreLayers[title] = not ok or visible ~= false
        end
        if controller then pcall(controller.setLayerVisible, title, false) end
        pcall(mw.showLayer, title, false)
    end
end

local function restoreLayers()
    local controller = package.loaded['Mission.MapLayerController']
    for title, visible in pairs(EaglesNestCapture.restoreLayers or {}) do
        if controller then pcall(controller.setLayerVisible, title, visible) end
        pcall(mw.showLayer, title, visible)
    end
    EaglesNestCapture.restoreLayers = nil
end

local function setup(cmd)
    bounds = mapBounds()
    if not bounds then return 'no map window (open the Mission Editor with a mission)' end
    pcall(NewMapView.setMapMode, NewMapView, 'satellite')
    setLayers(cmd.hide or DEFAULT_HIDE)
    settle = cmd.settle or 120
    if cmd.x and cmd.z then mw.setCamera(cmd.x, cmd.z) end
    for _ = 1, 5 do
        local m, s = metresPerUnit(), mw.getScale()
        if m <= 0 or type(s) ~= 'number' then break end
        mw.setScale(s * cmd.mpp / m)
    end
    local mpu = metresPerUnit()
    local camX, camZ = mw.getCamera()
    local midX, midZ = mw.getMapPoint(bounds.x + bounds.w / 2, bounds.y + bounds.h / 2)
    local check = math.sqrt((camX - midX) ^ 2 + (camZ - midZ) ^ 2)
    writeFile(dir .. 'job.lua', string.format('return { mpu = %s, check = %s, map = { x = %d, y = %d, w = %d, h = %d }, screen = { w = %s, h = %s } }',
        num(mpu), num(check), bounds.x, bounds.y, bounds.w, bounds.h, tostring(main_w), tostring(main_h)))
    log(string.format('setup: %.3f m per unit (asked %s), map %dx%d at %d,%d, centre check %.1f m', mpu, tostring(cmd.mpp), bounds.w, bounds.h, bounds.x, bounds.y, check))
end

local function step()
    if phase == nil then
        current = table.remove(queue, 1)
        if not current then return end
        mw.setCamera(current.x, current.z)
        phase, wait = 'settle', settle
    elseif phase == 'settle' then
        wait = wait - 1
        if wait > 0 then return end
        local w, h = bounds.w, bounds.h
        local pts = {}
        for _, p in ipairs({ { 0, 0 }, { w, 0 }, { 0, h }, { w, h }, { w / 2, h / 2 } }) do
            local mx, my = mw.getMapPoint(bounds.x + p[1], bounds.y + p[2])
            pts[#pts + 1] = string.format('{ u = %s, v = %s, x = %s, z = %s }', num(p[1]), num(p[2]), num(mx), num(my))
        end
        writeFile(dir .. 'step_' .. current.id .. '.lua', 'return { id = "' .. current.id .. '", points = { ' .. table.concat(pts, ', ') .. ' } }')
        writeFile(dir .. 'ready.txt', current.id)
        phase, wait = 'capture', 0
    elseif phase == 'capture' then
        wait = wait + 1
        if readFile(dir .. 'done.txt') == current.id then
            phase = nil
        elseif wait > 60 * 120 then
            log('no capture for ' .. current.id .. ' in two minutes: dropping the batch')
            queue, phase = {}, nil
        end
    end
end

EaglesNestCapture.updater = function()
    if EaglesNestCapture.gen ~= myGen then return true end  -- a newer copy took over
    if busy then
        local ok, err = pcall(step)
        if not ok then log('error: ' .. tostring(err)); queue, phase = {}, nil end
        if phase == nil and #queue == 0 then
            busy = false
            writeFile(dir .. 'ready.txt', 'batch-done')
        end
        return false
    end
    local text = readFile(dir .. COMMAND)
    if not text then return false end
    os.remove(dir .. COMMAND)
    local chunk = loadstring(text)
    local ok, cmd = pcall(chunk)
    if not ok or type(cmd) ~= 'table' then log('bad command'); return false end
    if cmd.op == 'setup' then
        local okS, err = pcall(setup, cmd)
        if not okS or err then log('setup failed: ' .. tostring(err)); writeFile(dir .. 'job.lua', 'return { error = true }') end
    elseif cmd.op == 'visit' then
        queue = cmd.cams or {}
        current, phase, busy = nil, nil, #queue > 0
        if not busy then writeFile(dir .. 'ready.txt', 'batch-done') end
    elseif cmd.op == 'finish' then
        restoreLayers()
        writeFile(dir .. 'ready.txt', 'finished')
        log('finished, layers restored')
    end
    return false
end
UpdateManager.add(EaglesNestCapture.updater)
writeFile(dir .. 'loaded.txt', tostring(os.time()))
log('loaded v6')
