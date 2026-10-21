param([string]$LiveHtml)
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\build.ps1"
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /out:"$PSScriptRoot\bin\ScannerTests.exe" /r:System.Net.Http.dll "$PSScriptRoot\src\Scanner.cs" "$PSScriptRoot\tests\ScannerTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
if ($LiveHtml) { & "$PSScriptRoot\bin\ScannerTests.exe" $LiveHtml }
else { & "$PSScriptRoot\bin\ScannerTests.exe" }
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
& $compiler /nologo /target:exe /out:"$PSScriptRoot\bin\DesktopTests.exe" /r:System.Net.Http.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"$PSScriptRoot\bin\FetchTree.exe" "$PSScriptRoot\tests\DesktopTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Desktop test build failed.' }
& "$PSScriptRoot\bin\DesktopTests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Desktop tests failed.' }
& $compiler /nologo /target:exe /main:ShellDropTests /out:"$PSScriptRoot\bin\ShellDropTests.exe" /r:System.Net.Http.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"$PSScriptRoot\bin\FetchTree.exe" "$PSScriptRoot\tests\DesktopTests.cs" "$PSScriptRoot\tests\ShellDropTests.cs"
if ($LASTEXITCODE -ne 0) { throw 'Shell integration test build failed.' }
$dropTarget = Join-Path $PSScriptRoot ('bin\shell-test-' + [Guid]::NewGuid().ToString('N'))
& "$PSScriptRoot\bin\ShellDropTests.exe" $dropTarget
if ($LASTEXITCODE -ne 0) { throw 'Shell drop integration failed.' }
