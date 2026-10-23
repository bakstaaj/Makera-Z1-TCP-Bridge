$ErrorActionPreference = 'Stop'
$project = $PSScriptRoot
$version = '0.3.11'
$output = Join-Path $project "cnc-machine-tcp-bridge-v$version.zip"
$stage = Join-Path ([System.IO.Path]::GetTempPath()) "cnc-machine-tcp-bridge-$([guid]::NewGuid().ToString('N'))"
$include = @('MakeraZ1TcpBridge.exe', 'Program.cs', 'Build-Bridge.ps1', 'Package-Bridge.ps1', 'Install-Z1Bridge.ps1', 'Update-InstalledService.ps1', 'Uninstall-Z1Bridge.ps1', 'nssm.exe', 'NSSM-NOTICE.txt', 'README.md', 'screenshots')
New-Item -ItemType Directory -Path $stage | Out-Null
try {
  foreach ($item in $include) {
    $source = Join-Path $project $item
    if (-not (Test-Path -LiteralPath $source)) { throw "Package input missing: $item" }
    Copy-Item -LiteralPath $source -Destination $stage -Recurse
  }
  if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $output, [System.IO.Compression.CompressionLevel]::Optimal, $false)
} finally {
  if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
Get-FileHash -LiteralPath $output -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  $(Split-Path $_.Path -Leaf)" }
Write-Host "Created $output"
