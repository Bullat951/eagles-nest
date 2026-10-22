# Builds the release packages into release\:
#   EaglesNest-Server-<version>.zip  - copy-into-saved-games\ (Mods, Scripts, Config\DCB.example.lua,
#                                      DCB\terrain_*_500.bin) + INSTALL.txt
#   EaglesNest-Client-<version>.zip  - Dcb.Client.exe + CLIENT.txt
# The client is the one build.ps1 last built (repo root); run build.ps1 first after client changes.
# Terrain grids come from the test server's DCB folder (-TerrainFrom): every terrain sampled there at
# the default 500 m step ships, so a server on that terrain samples nothing.
param([string]$TerrainFrom = (Join-Path $env:USERPROFILE 'Saved Games\DCS.server\DCB'))
$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$dll = & (Join-Path $repo 'dll\build.ps1') | Select-Object -Last 1
$version = [regex]::Match((Get-Content (Join-Path $repo 'dll\src\dcb.cpp') -Raw), 'kVersion\[\] = "([^"]+)"').Groups[1].Value
if (-not $version) { throw 'version not found in dcb.cpp' }

$release = Join-Path $repo 'release'
$server = Join-Path $release "EaglesNest-Server-$version"
$paste = Join-Path $server 'copy-into-saved-games'
Remove-Item $server -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $paste 'Mods\Services\DCB\bin'), (Join-Path $paste 'Mods\Services\DCB\Scripts'),
    (Join-Path $paste 'Scripts\Hooks'), (Join-Path $paste 'Config') | Out-Null
Copy-Item $dll (Join-Path $paste 'Mods\Services\DCB\bin\dcb.dll')
Copy-Item (Join-Path $repo 'lua\collector.lua') (Join-Path $paste 'Mods\Services\DCB\Scripts\')
Copy-Item (Join-Path $repo 'lua\foothold.lua') (Join-Path $paste 'Mods\Services\DCB\Scripts\')
Copy-Item (Join-Path $repo 'lua\DCB-hook.lua') (Join-Path $paste 'Scripts\Hooks\')
# Settings ship as an example only: the hook makes DCB.lua from it on the first start (random
# passwords), and an update never overwrites the server's own DCB.lua.
Copy-Item (Join-Path $PSScriptRoot 'config\DCB.example.lua') (Join-Path $paste 'Config\DCB.example.lua')
(Get-Content (Join-Path $PSScriptRoot 'release\INSTALL.txt') -Raw).Replace('__VERSION__', $version) |
    Set-Content (Join-Path $server 'INSTALL.txt') -Encoding utf8 -NoNewline
$grids = @(Get-ChildItem (Join-Path $TerrainFrom 'terrain_*_500.bin') -ErrorAction SilentlyContinue)
if ($grids.Count -gt 0) {
    New-Item -ItemType Directory -Force (Join-Path $paste 'DCB') | Out-Null
    $grids | Copy-Item -Destination (Join-Path $paste 'DCB')
}
Write-Host "Terrain grids: $(if ($grids.Count) { ($grids | % { $_.Name }) -join ', ' } else { 'none' })"

$client = Join-Path $release "EaglesNest-Client-$version"
Remove-Item $client -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $client | Out-Null
$exe = Join-Path $repo 'Dcb.Client.exe'
if (-not (Test-Path $exe)) { throw 'Dcb.Client.exe missing: run build.ps1 first' }
Copy-Item $exe $client
Copy-Item (Join-Path $PSScriptRoot 'release\CLIENT.txt') (Join-Path $client 'CLIENT.txt')

foreach ($dir in $server, $client) {
    $zip = "$dir.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $dir '*') -DestinationPath $zip
    Write-Host "$zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}
Write-Host "Client built $((Get-Item $exe).LastWriteTime)"
