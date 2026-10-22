# Builds dcb.dll and installs DCB into a DCS server instance. DCS locks the DLL
# while running, so the instance is stopped first (only that instance - matched by
# its -w argument) and restarted afterwards unless -NoStart.
param(
    [string]$InstanceName = 'DCS.server',
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'

$repo     = Split-Path $PSScriptRoot -Parent
$instance = Join-Path "$env:USERPROFILE\Saved Games" $InstanceName
$dll      = & (Join-Path $repo 'dll\build.ps1') | Select-Object -Last 1

$procs = Get-CimInstance Win32_Process -Filter "Name = 'DCS.exe'" |
    Where-Object { $_.CommandLine -match "-w\s+`"?$([regex]::Escape($InstanceName))`"?(\s|$)" }
foreach ($p in $procs) {
    Write-Host "Stopping $InstanceName (pid $($p.ProcessId))"
    Stop-Process -Id $p.ProcessId -Force
    Wait-Process -Id $p.ProcessId -Timeout 60 -ErrorAction SilentlyContinue
}

$svc = Join-Path $instance 'Mods\Services\DCB'
New-Item -ItemType Directory -Force (Join-Path $svc 'bin'), (Join-Path $svc 'Scripts'), (Join-Path $instance 'Scripts\Hooks') | Out-Null
# Windows can hold the DLL lock for a moment after the process is gone.
$target = Join-Path $svc 'bin\dcb.dll'
for ($i = 0; ; $i++) {
    try { Copy-Item $dll $target -Force; break }
    catch { if ($i -ge 30) { throw }; Start-Sleep -Seconds 1 }
}
Copy-Item (Join-Path $repo 'lua\collector.lua') (Join-Path $svc 'Scripts\collector.lua') -Force
Copy-Item (Join-Path $repo 'lua\foothold.lua') (Join-Path $svc 'Scripts\foothold.lua') -Force
Copy-Item (Join-Path $repo 'lua\DCB-hook.lua') (Join-Path $instance 'Scripts\Hooks\DCB-hook.lua') -Force
$cfg = Join-Path $instance 'Config\DCB.lua'
# The example goes next to it; on a fresh instance the hook makes DCB.lua from it with random
# passwords on the first start (as a release install does).
Copy-Item (Join-Path $PSScriptRoot 'config\DCB.example.lua') (Join-Path $instance 'Config\DCB.example.lua') -Force
if (-not (Test-Path $cfg)) { Write-Host "First start creates $cfg with random controller passwords" }
Write-Host "Installed DCB into $instance"

if (-not $NoStart) { & (Join-Path $PSScriptRoot 'start-server.ps1') -InstanceName $InstanceName }
