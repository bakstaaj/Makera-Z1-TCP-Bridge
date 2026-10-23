$ErrorActionPreference = 'Stop'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw "Microsoft .NET Framework C# compiler not found: $compiler" }
$source = Join-Path $PSScriptRoot 'Program.cs'
$output = Join-Path $PSScriptRoot 'MakeraZ1TcpBridge.exe'
& $compiler /nologo /optimize+ /target:exe /platform:anycpu /out:$output $source
if ($LASTEXITCODE -ne 0) { throw 'Bridge compilation failed.' }
Write-Host "Built $output"
