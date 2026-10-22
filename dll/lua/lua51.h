// Minimal Lua 5.1 C API declarations for linking against DCS bin\lua.dll.
// DCS ships no headers; these match the stock Lua 5.1 ABI (lua_Integer = ptrdiff_t).
#pragma once
#include <cstddef>

extern "C" {

typedef struct lua_State lua_State;
typedef int (*lua_CFunction)(lua_State* L);
typedef ptrdiff_t lua_Integer;

typedef struct luaL_Reg {
    const char* name;
    lua_CFunction func;
} luaL_Reg;

__declspec(dllimport) void lua_pushboolean(lua_State* L, int b);
__declspec(dllimport) void lua_pushstring(lua_State* L, const char* s);
__declspec(dllimport) void lua_pushlstring(lua_State* L, const char* s, size_t len);
__declspec(dllimport) void lua_pushinteger(lua_State* L, lua_Integer n);
__declspec(dllimport) void lua_createtable(lua_State* L, int narr, int nrec);
__declspec(dllimport) void lua_rawseti(lua_State* L, int idx, int n);
__declspec(dllimport) void lua_setfield(lua_State* L, int idx, const char* k);
__declspec(dllimport) int lua_toboolean(lua_State* L, int idx);
__declspec(dllimport) lua_Integer luaL_checkinteger(lua_State* L, int narg);
__declspec(dllimport) const char* luaL_checklstring(lua_State* L, int narg, size_t* len);
__declspec(dllimport) const char* luaL_optlstring(lua_State* L, int narg, const char* def, size_t* len);
__declspec(dllimport) void luaL_register(lua_State* L, const char* libname, const luaL_Reg* l);

}
