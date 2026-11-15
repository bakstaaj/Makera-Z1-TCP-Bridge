# Repairs the configuration shortcut for an existing Makera Z1 TCP Bridge install.
$ErrorActionPreference = 'Stop'
$InstallDir = 'C:\makera_z1_tcp_bridge'
$ServiceName = 'MakeraZ1TcpBridge'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $powerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $powerShellExe)) { throw "Windows PowerShell was not found: $powerShellExe" }
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    Start-Process -FilePath $powerShellExe -ArgumentList $arguments -Verb RunAs
    exit
}

$service = Get-Service -Name $ServiceName -ErrorAction Stop
$configUi = Join-Path $PSScriptRoot 'Configure-Z1Bridge.ps1'
$discovery = Join-Path $PSScriptRoot 'Discover-Z1.ps1'
foreach ($source in @($configUi, $discovery)) {
    if (-not (Test-Path -LiteralPath $source)) { throw "Required UI file is missing from the extracted package: $source" }
}
Copy-Item -LiteralPath $configUi -Destination $InstallDir -Force
Copy-Item -LiteralPath $discovery -Destination $InstallDir -Force

$shortcutDir = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Makera Z1 TCP Bridge'
New-Item -ItemType Directory -Path $shortcutDir -Force | Out-Null
$shortcutPath = Join-Path $shortcutDir 'Configure Makera Z1 TCP Bridge.lnk'
$powerShellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $powerShellExe)) { throw "Windows PowerShell was not found: $powerShellExe" }
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $powerShellExe
$shortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $InstallDir 'Configure-Z1Bridge.ps1') + '"'
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = 'Rediscover the Makera Z1 and update the TCP bridge connection.'
$shortcut.IconLocation = "$env:SystemRoot\System32\shell32.dll,13"
$shortcut.Save()

Write-Host "Repaired the Start menu shortcut. The $ServiceName service was not restarted or reinstalled."
