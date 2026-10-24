// Lua 5.1 module "dcb", loaded by Scripts\Hooks\DCB-hook.lua via require('dcb').
#include <cstring>
#include <string>
#include <vector>

#include <windows.h>
#include <bcrypt.h>

#include "lua51.h"
#include "net_server.h"
#include "terrain.h"

namespace {

constexpr char kVersion[] = "0.8.0";  // 0.3: controller commands; 0.4: coalition channels (LSO feed); 0.5: analytics ids; 0.6: F10 mark commands; 0.7: carrier lights; 0.8: identify, shared threats, nctr
dcb::NetServer g_server;
dcb::TerrainGrid g_terrain;

// dcb.start(port, blue_password, red_password) -> true | false, error
int l_start(lua_State* L) {
    lua_Integer port = luaL_checkinteger(L, 1);
    size_t blueLen = 0, redLen = 0;
    const char* blue = luaL_optlstring(L, 2, "", &blueLen);
    const char* red = luaL_optlstring(L, 3, "", &redLen);
    if (port <= 0 || port > 65535) {
        lua_pushboolean(L, 0);
        lua_pushstring(L, "port out of range");
        return 2;
    }
    std::string error;
    if (!g_server.start(static_cast<uint16_t>(port), std::string(blue, blueLen), std::string(red, redLen), error)) {
        lua_pushboolean(L, 0);
        lua_pushstring(L, error.c_str());
        return 2;
    }
    lua_pushboolean(L, 1);
    return 1;
}

// dcb.publish("blue" | "red", json) -> true | false
int l_publish(lua_State* L) {
    size_t sideLen = 0, jsonLen = 0;
    const char* side = luaL_checklstring(L, 1, &sideLen);
    const char* json = luaL_checklstring(L, 2, &jsonLen);
    dcb::Coalition c;
    if (std::strcmp(side, "blue") == 0) {
        c = dcb::Blue;
    } else if (std::strcmp(side, "red") == 0) {
        c = dcb::Red;
    } else {
        lua_pushboolean(L, 0);
        return 1;
    }
    g_server.publish(c, std::string(json, jsonLen));
    lua_pushboolean(L, 1);
    return 1;
}

// dcb.publish_side(side, channel, json): a coalition's own channel (the fast LSO feed)
int l_publish_side(lua_State* L) {
    size_t sideLen = 0, nameLen = 0, jsonLen = 0;
    const char* side = luaL_checklstring(L, 1, &sideLen);
    const char* name = luaL_checklstring(L, 2, &nameLen);
    const char* json = luaL_checklstring(L, 3, &jsonLen);
    dcb::Coalition c;
    if (std::strcmp(side, "blue") == 0) c = dcb::Blue;
    else if (std::strcmp(side, "red") == 0) c = dcb::Red;
    else return 0;
    g_server.publishSide(c, std::string(name, nameLen), std::string(json, jsonLen));
    return 0;
}

// dcb.publish_all(channel, json): message for every coalition (theatre, ...)
int l_publish_all(lua_State* L) {
    size_t nameLen = 0, jsonLen = 0;
    const char* name = luaL_checklstring(L, 1, &nameLen);
    const char* json = luaL_checklstring(L, 2, &jsonLen);
    g_server.publishAll(std::string(name, nameLen), std::string(json, jsonLen));
    return 0;
}

// dcb.terrain_begin(id, x0, z0, step, nx, nz, cache_path) -> true when loaded from cache
int l_terrain_begin(lua_State* L) {
    size_t idLen = 0, pathLen = 0;
    const char* id = luaL_checklstring(L, 1, &idLen);
    double x0 = static_cast<double>(luaL_checkinteger(L, 2));
    double z0 = static_cast<double>(luaL_checkinteger(L, 3));
    double step = static_cast<double>(luaL_checkinteger(L, 4));
    lua_Integer nx = luaL_checkinteger(L, 5);
    lua_Integer nz = luaL_checkinteger(L, 6);
    const char* path = luaL_checklstring(L, 7, &pathLen);
    if (nx <= 0 || nz <= 0 || nx > 8192 || nz > 8192 || step <= 0) {
        lua_pushboolean(L, 0);
        return 1;
    }
    bool cached = g_terrain.begin(std::string(id, idLen), x0, z0, step, static_cast<int>(nx), static_cast<int>(nz),
                                  std::string(path, pathLen));
    if (cached) g_server.publishAll("terrain", g_terrain.toJson());
    lua_pushboolean(L, cached ? 1 : 0);
    return 1;
}

// dcb.terrain_row(i, heights_csv, surface_digits) -> true when the row was accepted
int l_terrain_row(lua_State* L) {
    lua_Integer i = luaL_checkinteger(L, 1);
    size_t csvLen = 0, surfLen = 0;
    const char* csv = luaL_checklstring(L, 2, &csvLen);
    const char* surf = luaL_checklstring(L, 3, &surfLen);
    lua_pushboolean(L, g_terrain.setRow(static_cast<int>(i), csv, surf, surfLen) ? 1 : 0);
    return 1;
}

// dcb.terrain_finish() -> true when the grid was complete and published (cache written too)
int l_terrain_finish(lua_State* L) {
    if (!g_terrain.complete()) {
        lua_pushboolean(L, 0);
        return 1;
    }
    g_terrain.saveCache();
    g_server.publishAll("terrain", g_terrain.toJson());
    lua_pushboolean(L, 1);
    return 1;
}

// dcb.poll() -> { log lines }
int l_poll(lua_State* L) {
    auto lines = g_server.drainLog();
    lua_createtable(L, static_cast<int>(lines.size()), 0);
    for (size_t i = 0; i < lines.size(); ++i) {
        lua_pushlstring(L, lines[i].data(), lines[i].size());
        lua_rawseti(L, -2, static_cast<int>(i + 1));
    }
    return 1;
}

// dcb.commands() -> { { client, coalition, address, id, name, unit, text, seconds }, ... }
// Commands already checked by the network thread (whitelist, limits, rate).
int l_commands(lua_State* L) {
    auto commands = g_server.drainCommands();
    lua_createtable(L, static_cast<int>(commands.size()), 0);
    for (size_t i = 0; i < commands.size(); ++i) {
        const auto& c = commands[i];
        lua_createtable(L, 0, 8);
        lua_pushinteger(L, static_cast<lua_Integer>(c.client));
        lua_setfield(L, -2, "client");
        lua_pushstring(L, c.coalition == dcb::Blue ? "blue" : "red");
        lua_setfield(L, -2, "coalition");
        lua_pushlstring(L, c.address.data(), c.address.size());
        lua_setfield(L, -2, "address");
        lua_pushinteger(L, static_cast<lua_Integer>(c.id));
        lua_setfield(L, -2, "id");
        lua_pushlstring(L, c.name.data(), c.name.size());
        lua_setfield(L, -2, "name");
        lua_pushinteger(L, static_cast<lua_Integer>(c.unit));
        lua_setfield(L, -2, "unit");
        lua_pushlstring(L, c.text.data(), c.text.size());
        lua_setfield(L, -2, "text");
        lua_pushinteger(L, c.seconds);
        lua_setfield(L, -2, "seconds");
        lua_pushinteger(L, static_cast<lua_Integer>(c.x));
        lua_setfield(L, -2, "x");
        lua_pushinteger(L, static_cast<lua_Integer>(c.z));
        lua_setfield(L, -2, "z");
        lua_pushlstring(L, c.threat.data(), c.threat.size());
        lua_setfield(L, -2, "threat");
        lua_pushinteger(L, static_cast<lua_Integer>(c.mark));
        lua_setfield(L, -2, "mark");
        lua_pushinteger(L, c.mode);
        lua_setfield(L, -2, "mode");
        lua_pushinteger(L, static_cast<lua_Integer>(c.x2));
        lua_setfield(L, -2, "x2");
        lua_pushinteger(L, static_cast<lua_Integer>(c.z2));
        lua_setfield(L, -2, "z2");
        lua_rawseti(L, -2, static_cast<int>(i + 1));
    }
    return 1;
}

// dcb.command_result(client, id, ok, error_or_nil)
int l_command_result(lua_State* L) {
    auto client = static_cast<uint64_t>(luaL_checkinteger(L, 1));
    auto id = static_cast<int64_t>(luaL_checkinteger(L, 2));
    bool ok = lua_toboolean(L, 3) != 0;
    size_t len = 0;
    const char* error = luaL_optlstring(L, 4, "", &len);
    g_server.commandResult(client, id, ok, std::string(error, len));
    return 0;
}

int l_stop(lua_State*) {
    g_server.stop();
    return 0;
}

int l_version(lua_State* L) {
    lua_pushstring(L, kVersion);
    return 1;
}

std::string toHex(const unsigned char* data, size_t n) {
    static const char digits[] = "0123456789abcdef";
    std::string out;
    out.reserve(n * 2);
    for (size_t i = 0; i < n; ++i) {
        out += digits[data[i] >> 4];
        out += digits[data[i] & 15];
    }
    return out;
}

// dcb.random_hex(bytes) -> hex string from the system's CSPRNG (the analytics key)
int l_random_hex(lua_State* L) {
    lua_Integer n = luaL_checkinteger(L, 1);
    if (n <= 0 || n > 64) return 0;
    std::vector<unsigned char> buf(static_cast<size_t>(n));
    if (!BCRYPT_SUCCESS(BCryptGenRandom(nullptr, buf.data(), static_cast<ULONG>(buf.size()), BCRYPT_USE_SYSTEM_PREFERRED_RNG))) return 0;
    std::string hex = toHex(buf.data(), buf.size());
    lua_pushlstring(L, hex.data(), hex.size());
    return 1;
}

// dcb.hmac_hex(key, text) -> HMAC-SHA256(key, text) as hex: a player's stable id from their
// UCID that cannot be turned back into the UCID without the server's key.
int l_hmac_hex(lua_State* L) {
    size_t keyLen = 0, textLen = 0;
    const char* key = luaL_checklstring(L, 1, &keyLen);
    const char* text = luaL_checklstring(L, 2, &textLen);
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    unsigned char digest[32];
    bool ok = BCRYPT_SUCCESS(BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, BCRYPT_ALG_HANDLE_HMAC_FLAG)) &&
              BCRYPT_SUCCESS(BCryptCreateHash(alg, &hash, nullptr, 0, (PUCHAR)key, static_cast<ULONG>(keyLen), 0)) &&
              BCRYPT_SUCCESS(BCryptHashData(hash, (PUCHAR)text, static_cast<ULONG>(textLen), 0)) &&
              BCRYPT_SUCCESS(BCryptFinishHash(hash, digest, sizeof(digest), 0));
    if (hash) BCryptDestroyHash(hash);
    if (alg) BCryptCloseAlgorithmProvider(alg, 0);
    if (!ok) return 0;
    std::string hex = toHex(digest, sizeof(digest));
    lua_pushlstring(L, hex.data(), hex.size());
    return 1;
}

const luaL_Reg kFunctions[] = {
    {"start", l_start},
    {"publish", l_publish},
    {"publish_all", l_publish_all},
    {"publish_side", l_publish_side},
    {"terrain_begin", l_terrain_begin},
    {"terrain_row", l_terrain_row},
    {"terrain_finish", l_terrain_finish},
    {"poll", l_poll},
    {"commands", l_commands},
    {"command_result", l_command_result},
    {"stop", l_stop},
    {"version", l_version},
    {"random_hex", l_random_hex},
    {"hmac_hex", l_hmac_hex},
    {nullptr, nullptr},
};

}  // namespace

extern "C" __declspec(dllexport) int luaopen_dcb(lua_State* L) {
    luaL_register(L, "dcb", kFunctions);
    return 1;
}
