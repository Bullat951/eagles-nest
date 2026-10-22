cmake_minimum_required(VERSION 3.20)
project(dcb LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: servers need no VC++ redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Import library for DCS bin\lua.dll, generated from the export list.
set(LUA_IMPLIB ${CMAKE_CURRENT_BINARY_DIR}/lua.lib)
add_custom_command(
    OUTPUT ${LUA_IMPLIB}
    COMMAND ${CMAKE_AR} /nologo /def:${CMAKE_CURRENT_SOURCE_DIR}/lua/lua.def /out:${LUA_IMPLIB} /machine:x64
    DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/lua/lua.def)
add_custom_target(lua_implib DEPENDS ${LUA_IMPLIB})

add_library(dcb SHARED
    src/dcb.cpp
    src/net_server.cpp
    src/net_server.h
    src/terrain.cpp
    src/terrain.h)
add_dependencies(dcb lua_implib)
target_include_directories(dcb PRIVATE lua)
target_compile_definitions(dcb PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX _WINSOCK_DEPRECATED_NO_WARNINGS)
target_compile_options(dcb PRIVATE /W4 /permissive-)
target_link_libraries(dcb PRIVATE ${LUA_IMPLIB} ws2_32 bcrypt)
