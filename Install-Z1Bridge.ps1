# Installs or updates the standalone Makera Z1 TCP bridge Windows service.
$ErrorActionPreference = 'Stop'
$ServiceName = 'MakeraZ1TcpBridge'
$InstallDir = 'C:\makera_z1_tcp_bridge'
$ConfigPath = Join-Path $InstallDir 'config.yaml'
$NssmSource = Join-Path $PSScriptRoot 'nssm.exe'
$NssmNotice = Join-Path $PSScriptRoot 'NSSM-NOTICE.txt'
$BridgeSource = Join-Path $PSScriptRoot 'MakeraZ1TcpBridge.exe'

function Read-DefaultValue([string]$Prompt, [string]$Default) {
    $answer = Read-Host "$Prompt [$Default]"
    if ([string]::IsNullOrWhiteSpace($answer)) { return $Default }
    return $answer.Trim()
}

try {
    $existingHost = '192.168.68.146'
    $existingPort = '2222'
    $debugLogging = 'false'
    $rotaryInstalled = 'false'
    $grblSettings = @(
        '20: 1 # Z1 config.txt: soft_endstop.enable true',
        '21: 1 # Firmware XYZ limit inputs are enabled',
        '22: 1 # Firmware XYZ homing configuration is enabled',
        '23: 7 # Firmware XYZ homing directions are all home_to_max',
        '27: 1 # Firmware XYZ homing retract is 1 mm',
        '100: 1600 # Z1 firmware default X steps/mm',
        '101: 1600 # Z1 firmware default Y steps/mm',
        '102: 3200 # Z1 firmware default Z steps/mm',
        '103: 88.888889 # Z1 firmware optional A-axis steps/degree',
        '110: 1200 # Z1 config.txt: X max feed rate mm/min',
        '111: 1200 # Z1 config.txt: Y max feed rate mm/min',
        '112: 600 # Z1 config.txt: Z max feed rate mm/min',
        '113: 3600 # Z1 config.txt: optional A-axis max rate degrees/min',
        '120: 150 # Z1 config.txt: X acceleration',
        '121: 150 # Z1 config.txt: Y acceleration',
        '122: 150 # Z1 config.txt: Z acceleration',
        '123: 360 # Z1 config.txt: optional A-axis acceleration',
        '130: 200 # Conservative gSender X bound from Makera published work area',
        '131: 200 # Conservative gSender Y bound from Makera published work area',
        '132: 100 # Conservative gSender Z bound from Makera published work area',
        '133: 380 # Z1 firmware optional A-axis max travel; verify rotary limits on your machine'
    )
    if (Test-Path -LiteralPath $ConfigPath) {
        $cfg = Get-Content -LiteralPath $ConfigPath -Raw
        if ($cfg -match '(?m)^\s*host:\s*([^\s#]+)') { $existingHost = $Matches[1] }
        if ($cfg -match '(?m)^\s*port:\s*(\d+)') { $existingPort = $Matches[1] }
        if ($cfg -match '(?m)^\s*debug:\s*(true|false)\s*(?:#.*)?$') { $debugLogging = $Matches[1].ToLowerInvariant() }
        if ($cfg -match '(?m)^\s*installed:\s*(true|false)\s*(?:#.*)?$') { $rotaryInstalled = $Matches[1].ToLowerInvariant() }
        $savedSettings = @()
        $insideSettings = $false
        foreach ($configLine in ($cfg -split "`r?`n")) {
            if ($configLine -match '^grbl-settings\s*:') { $insideSettings = $true; continue }
            if ($insideSettings -and $configLine -match '^\s+(\d{1,3})\s*:\s*([^\s#]+)') {
                $savedSettings += "$($Matches[1]): $($Matches[2])"
                continue
            }
            if ($insideSettings -and $configLine -match '^\S' -and $configLine -notmatch '^\s*#') { $insideSettings = $false }
        }
        if ($savedSettings.Count -gt 0) { $grblSettings = $savedSettings }
    }

    $machineIp = Read-DefaultValue 'Makera Z1 IPv4 address' $existingHost
    $parsedIp = $null
    if (-not [System.Net.IPAddress]::TryParse($machineIp, [ref]$parsedIp) -or $parsedIp.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw "Invalid IPv4 address: $machineIp"
    }
    $machinePortText = Read-DefaultValue 'Makera Z1 TCP port' $existingPort
    $machinePort = 0
    if (-not [int]::TryParse($machinePortText, [ref]$machinePort) -or $machinePort -lt 1 -or $machinePort -gt 65535) {
        throw "Invalid TCP port: $machinePortText"
    }

    $nssm = Get-Command $NssmSource -ErrorAction SilentlyContinue
    if (-not $nssm) { throw "nssm.exe is missing from the package: $NssmSource" }
    if (-not (Test-Path -LiteralPath $BridgeSource)) { throw "Bridge executable is missing from the package: $BridgeSource" }

    # Stop and remove the existing service before replacing its NSSM executable.
    # Windows locks the service host image while NSSM is running as the service.
    $serviceExists = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($serviceExists) {
        try { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue } catch { }
        $deadline = (Get-Date).AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 500
            $serviceExists = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        } while ($serviceExists -and $serviceExists.Status -ne 'Stopped' -and (Get-Date) -lt $deadline)
        $installedNssm = Join-Path $InstallDir 'nssm.exe'
        if (Test-Path -LiteralPath $installedNssm) {
            & $installedNssm remove $ServiceName confirm | Out-Null
            Start-Sleep -Seconds 1
        }
        $remaining = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($remaining) { throw "Could not stop and remove the existing $ServiceName service. Stop it in Services and rerun the installer." }
    }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $InstallDir 'logs') -Force | Out-Null
    Copy-Item -LiteralPath $BridgeSource -Destination (Join-Path $InstallDir 'MakeraZ1TcpBridge.exe') -Force
    Copy-Item -LiteralPath $NssmSource -Destination (Join-Path $InstallDir 'nssm.exe') -Force
    if (Test-Path -LiteralPath $NssmNotice) { Copy-Item -LiteralPath $NssmNotice -Destination (Join-Path $InstallDir 'NSSM-NOTICE.txt') -Force }

    @"
# Makera Z1 connection used by the standalone TCP bridge.
z1:
  host: $machineIp
  port: $machinePort

# gSender connects to this local GRBL-style TCP endpoint.
listener:
  host: 127.0.0.1
  port: 2323

# Enable only while the optional Makera rotary module is physically installed.
rotary:
  installed: $rotaryInstalled

# GRBL-compatible settings reported to gSender. These are a UI/profile layer;
# the bridge does not write them to the Z1. Edit only with verified machine data.
grbl-settings:
$($grblSettings | ForEach-Object { "  $_" } | Out-String)

# Set true to log every bridged command and Z1 response frame.
logging:
  debug: $debugLogging
"@ | Set-Content -LiteralPath $ConfigPath -Encoding ascii

    $nssmExe = Join-Path $InstallDir 'nssm.exe'
    $bridgePath = Join-Path $InstallDir 'MakeraZ1TcpBridge.exe'
    & $nssmExe install $ServiceName $bridgePath '--config' $ConfigPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'NSSM could not install the service.' }
    & $nssmExe set $ServiceName AppDirectory $InstallDir | Out-Null
    & $nssmExe set $ServiceName DisplayName 'Makera Z1 TCP Bridge' | Out-Null
    & $nssmExe set $ServiceName Description 'Standalone GRBL-style TCP adapter between gSender and a Makera Z1.' | Out-Null
    & $nssmExe set $ServiceName Start SERVICE_AUTO_START | Out-Null
    & $nssmExe set $ServiceName AppStdout (Join-Path $InstallDir 'logs\bridge-out.log') | Out-Null
    & $nssmExe set $ServiceName AppStderr (Join-Path $InstallDir 'logs\bridge-error.log') | Out-Null
    & $nssmExe set $ServiceName AppRotateFiles 1 | Out-Null
    & $nssmExe set $ServiceName AppRotateOnline 1 | Out-Null
    & $nssmExe set $ServiceName AppRotateBytes 1048576 | Out-Null
    try {
        Start-Service -Name $ServiceName
        Start-Sleep -Seconds 2
        $startedService = Get-Service -Name $ServiceName
        if ($startedService.Status -ne 'Running') { throw "Service state is $($startedService.Status)." }
    } catch {
        $errorLog = Join-Path $InstallDir 'logs\bridge-error.log'
        $outputLog = Join-Path $InstallDir 'logs\bridge-out.log'
        $details = @("Service startup failed: $($_.Exception.Message)")
        if (Test-Path -LiteralPath $errorLog) { $details += '--- bridge-error.log ---'; $details += Get-Content -LiteralPath $errorLog -Tail 20 }
        if (Test-Path -LiteralPath $outputLog) { $details += '--- bridge-out.log ---'; $details += Get-Content -LiteralPath $outputLog -Tail 20 }
        throw ($details -join [Environment]::NewLine)
    }

    Write-Host ''
    Write-Host 'Makera Z1 TCP Bridge installed and started.' -ForegroundColor Green
    Write-Host "Z1: $machineIp`:$machinePort"
    Write-Host 'In gSender, select Ethernet and connect to 127.0.0.1:2323.'
    Write-Host "Configuration: $ConfigPath"
} catch {
    Write-Error $_
    exit 1
}


