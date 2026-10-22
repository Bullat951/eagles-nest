# Creates a separate DCS server instance in Saved Games\DCS.server.
# Safe to re-run: only writes Config\serverSettings.lua and Config\autoexec.cfg
# (backs up existing ones), never touches Saved Games\DCS.
param(
    [string]$InstanceName = 'DCS.server',
    [string]$Password = 'dcb',
    # The Mission Editor saves here; pointing the server at it avoids copying after each edit.
    [string]$MissionPath = "$env:USERPROFILE\Saved Games\DCS\Missions\DCB-test.miz"
)

$instance = Join-Path "$env:USERPROFILE\Saved Games" $InstanceName
$config   = Join-Path $instance 'Config'
if (-not (Test-Path $MissionPath)) { Write-Warning "Mission not found: $MissionPath" }
$mission  = $MissionPath -replace '\\', '\\'

New-Item -ItemType Directory -Force $config, (Join-Path $instance 'Scripts\Hooks'), (Join-Path $instance 'Mods\Services') | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
foreach ($f in 'serverSettings.lua', 'autoexec.cfg') {
    $p = Join-Path $config $f
    if (Test-Path $p) { Copy-Item $p "$p.bak-$stamp" }
}

$settings = Get-Content (Join-Path $PSScriptRoot 'config\serverSettings.lua') -Raw
$settings = $settings.Replace('__MISSION__', $mission).Replace('__PASSWORD__', $Password)
Set-Content -Path (Join-Path $config 'serverSettings.lua') -Value $settings -Encoding ascii
Copy-Item (Join-Path $PSScriptRoot 'config\autoexec.cfg') (Join-Path $config 'autoexec.cfg') -Force

Write-Host "Server instance ready: $instance"
Write-Host "Mission:              $MissionPath"
Write-Host "DCS server password:  $Password"
