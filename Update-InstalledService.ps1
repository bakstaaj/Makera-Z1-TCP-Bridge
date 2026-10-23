$ErrorActionPreference = 'Stop'

$serviceName = 'MakeraZ1TcpBridge'
$installDir = 'C:\makera_z1_tcp_bridge'
$sourceExe = Join-Path $PSScriptRoot 'MakeraZ1TcpBridge.exe'
$installedExe = Join-Path $installDir 'MakeraZ1TcpBridge.exe'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window (Run as administrator).'
}
if (-not (Test-Path -LiteralPath $sourceExe)) { throw "Bridge executable not found: $sourceExe" }
if (-not (Test-Path -LiteralPath $installedExe)) { throw "Installed bridge executable not found: $installedExe" }

$service = Get-Service -Name $serviceName
if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName -Force
    (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force
$sourceHash = (Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash
$installedHash = (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash
if ($sourceHash -ne $installedHash) { throw 'Installed executable hash does not match the package.' }

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host "Updated $serviceName and verified installed executable SHA-256: $installedHash"
Get-Service -Name $serviceName | Select-Object Name, Status, StartType
