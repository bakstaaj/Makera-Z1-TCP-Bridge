$ErrorActionPreference = 'Stop'
$ServiceName = 'MakeraZ1TcpBridge'
$InstallDir = 'C:\makera_z1_tcp_bridge'
$nssm = Join-Path $InstallDir 'nssm.exe'
if (Test-Path -LiteralPath $nssm) {
    & $nssm stop $ServiceName confirm | Out-Null
    & $nssm remove $ServiceName confirm | Out-Null
}
Write-Host "Service removed. Configuration and logs remain in $InstallDir. Remove that folder manually if you no longer need them."
