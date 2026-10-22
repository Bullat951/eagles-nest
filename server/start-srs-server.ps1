# Starts a local SRS server for testing DCB's SRS bridge (docs/PLAN.md, phase 5a), with its own
# config beside the DCS.server instance: External AWACS Mode on (random per-coalition passwords,
# made on first start), line of sight and range on, LotATC transponder export to 127.0.0.1,
# UPnP off (the default would open router ports to the internet). Needs SRS installed.
param([string]$InstanceName = 'DCS.server', [int]$Port = 5002)

$srs = (Get-ItemProperty 'HKCU:\SOFTWARE\DCS-SR-Standalone' -ErrorAction SilentlyContinue).SRPathStandalone
$exe = if ($srs) { Join-Path $srs 'ServerCommandLine-Windows\SRS-Server-Commandline.exe' }
if (-not $exe -or -not (Test-Path $exe)) {
    Write-Host "SRS is not installed (no SRS-Server-Commandline.exe): install DCS-SimpleRadio-Standalone first."
    exit 1
}

$dir = Join-Path $env:USERPROFILE "Saved Games\$InstanceName\SRS"
$cfg = Join-Path $dir 'server.cfg'
New-Item -ItemType Directory -Force $dir | Out-Null
if (-not (Test-Path $cfg)) {
    function New-Password { -join ((48..57) + (97..122) | Get-Random -Count 10 | ForEach-Object { [char]$_ }) }
    @"
[General Settings]
EXTERNAL_AWACS_MODE=true
LOS_ENABLED=true
DISTANCE_ENABLED=true
LOTATC_EXPORT_ENABLED=true
LOTATC_EXPORT_PORT=10712
LOTATC_EXPORT_IP=127.0.0.1
CLIENT_EXPORT_ENABLED=false
TRANSMISSION_LOG_ENABLED=false
TEST_FREQUENCIES=247.2,120.3

[Server Settings]
SERVER_PORT=$Port
UPNP_ENABLED=false
SERVER_IP=0.0.0.0
HTTP_SERVER_ENABLED=false
CHECK_FOR_BETA_UPDATES=false

[External AWACS Mode Settings]
EXTERNAL_AWACS_MODE_BLUE_PASSWORD=$(New-Password)
EXTERNAL_AWACS_MODE_RED_PASSWORD=$(New-Password)
"@ | Set-Content $cfg -Encoding ascii
    Write-Host "Wrote $cfg (External AWACS Mode passwords inside)."
}

$running = Get-CimInstance Win32_Process -Filter "Name = 'SRS-Server-Commandline.exe'" |
    Where-Object { $_.CommandLine -match [regex]::Escape($cfg) }
if ($running) {
    Write-Host "The test SRS server is already running (pid $($running.ProcessId -join ', '))."
} else {
    # Through Windows' process service, not Start-Process (see start-server.ps1).
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$exe`" --cfg=`"$cfg`""; CurrentDirectory = $dir }
    if ($r.ReturnValue -ne 0) { throw "SRS server did not start (Win32_Process.Create returned $($r.ReturnValue))" }
    Write-Host "Started the SRS server on port $Port (its own console window shows the log)."
}
Write-Host "Connect the SRS client to 127.0.0.1:$Port, External AWACS Mode, password from:"
Write-Host "  $cfg"
