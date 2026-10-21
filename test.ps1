param([string]$LiveHtml)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\build.ps1"
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /out:"$PSScriptRoot\bin\ScannerTests.exe" /r:System.Net.Http.dll "$PSScriptRoot\src\Scanner.cs" "$PSScriptRoot\tests\ScannerTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
if ($LiveHtml) { & "$PSScriptRoot\bin\ScannerTests.exe" $LiveHtml }
else { & "$PSScriptRoot\bin\ScannerTests.exe" }
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
