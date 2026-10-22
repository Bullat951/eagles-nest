-- Runs dcb.dll outside DCS with DCS's own interpreter, publishing fake pictures.
-- usage: "E:\DCS World\bin\luae.exe" standalone.lua <dir with dcb.dll> [seconds]
package.cpath = arg[1] .. "\\?.dll;" .. package.cpath
local dcb = require("dcb")
local seconds = tonumber(arg[2]) or 15
print("dcb " .. dcb.version())
print("start:", dcb.start(10399, "bluepw", "redpw"))

local nextPublish, deadline, n = 0, os.clock() + seconds, 0
while os.clock() < deadline do
    if os.clock() >= nextPublish then
        n = n + 1
        dcb.publish("blue", string.format('{"type":"picture","coalition":"blue","time":%d,"units":[{"name":"B%d"}]}', n, n))
        dcb.publish("red", string.format('{"type":"picture","coalition":"red","time":%d,"units":[{"name":"R%d"}]}', n, n))
        nextPublish = os.clock() + 1
    end
    for _, line in ipairs(dcb.poll()) do print("dcb: " .. line) end
end
dcb.stop()
print("stopped")
