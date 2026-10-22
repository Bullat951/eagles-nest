@echo off
rem Starts the local DCS test server (DCS.server instance) and waits until DCB answers,
rem then run Dcb.Client.exe and connect to 127.0.0.1:10309.
rem First time: server\setup-server.ps1, then server\deploy.ps1 (installs hook, DLL, scripts).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0server\start-server.ps1" -Wait
pause
