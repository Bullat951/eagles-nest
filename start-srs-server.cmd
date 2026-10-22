@echo off
rem Starts a local SRS server for testing DCB's SRS tab (External AWACS Mode, line of sight on).
rem Config and passwords: Saved Games\DCS.server\SRS\server.cfg (made on first start).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0server\start-srs-server.ps1"
pause
