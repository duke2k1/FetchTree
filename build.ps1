$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path $compiler)) { throw 'The .NET Framework 4.x compiler is required.' }
$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
& $compiler /nologo /optimize+ /target:exe /out:"$bin\FetchTree.exe" /r:System.Net.Http.dll /r:System.Web.Extensions.dll "$PSScriptRoot\src\Scanner.cs" "$PSScriptRoot\src\Program.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Built $bin/FetchTree.exe"
