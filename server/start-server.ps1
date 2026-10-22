# Starts DCS as a headless server using the DCS.server Saved Games instance.
# -Wait: then wait until the DCB hook listens (mission loaded), so a client can connect.
param([string]$InstanceName = 'DCS.server', [switch]$Wait, [int]$Port = 10309, [int]$TimeoutSeconds = 300)

function Test-Dcb {
    $tcp = New-Object System.Net.Sockets.TcpClient
    try { return $tcp.ConnectAsync('127.0.0.1', $Port).Wait(1000) -and $tcp.Connected }
    catch { return $false }
    finally { $tcp.Dispose() }
}

$running = Get-CimInstance Win32_Process -Filter "Name = 'DCS.exe'" |
    Where-Object { $_.CommandLine -match "-w\s+`"?$([regex]::Escape($InstanceName))`"?(\s|$)" }
if ($running) {
    Write-Host "$InstanceName is already running (pid $($running.ProcessId -join ', ')); use deploy.ps1 to restart it."
} else {
    $dcs = (Get-ItemProperty 'HKCU:\Software\Eagle Dynamics\DCS World').Path
    # Through Windows' process service, not Start-Process: a process started from a tool that
    # runs in a Windows job (Claude Code) would end when that tool restarts.
    $exe = Join-Path $dcs 'bin\DCS.exe'
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = "`"$exe`" --server --norender -w $InstanceName"; CurrentDirectory = (Join-Path $dcs 'bin') }
    if ($r.ReturnValue -ne 0) { throw "DCS did not start (Win32_Process.Create returned $($r.ReturnValue))" }
    Write-Host "Started. Log: $env:USERPROFILE\Saved Games\$InstanceName\Logs\dcs.log"
    Write-Host "Web GUI:  http://127.0.0.1:8088"
}

if ($Wait) {
    Write-Host -NoNewline "Waiting for DCB on 127.0.0.1:$Port "
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not (Test-Dcb)) {
        if ((Get-Date) -gt $deadline) {
            Write-Host ""
            Write-Host "DCB did not answer within $TimeoutSeconds s: check the log above."
            exit 1
        }
        Write-Host -NoNewline "."
        Start-Sleep -Seconds 3
    }
    Write-Host ""
    Write-Host "DCB is up on 127.0.0.1:$Port. Passwords: Saved Games\$InstanceName\Config\DCB.lua"
}
