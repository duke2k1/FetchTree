$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path $compiler)) { throw 'The .NET Framework 4.x compiler is required.' }
$bin = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
& $compiler /nologo /optimize+ /target:exe /out:"$bin\FetchTree.Scanner.exe" /r:System.Net.Http.dll /r:System.Web.Extensions.dll "$PSScriptRoot\src\Scanner.cs" "$PSScriptRoot\src\Program.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
& $compiler /nologo /optimize+ /target:winexe /out:"$bin\FetchTree.exe" /win32icon:"$PSScriptRoot\assets\FetchTree.ico" "/resource:$PSScriptRoot\assets\FetchTree.ico,FetchTree.ico" "/resource:$PSScriptRoot\assets\logo.png,FetchTree.logo.png" /r:System.Net.Http.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "$PSScriptRoot\src\Scanner.cs" "$PSScriptRoot\src\Transfers.cs" "$PSScriptRoot\src\ShellDrag.cs" "$PSScriptRoot\src\ExplorerWindow.cs" "$PSScriptRoot\src\BrandPanel.cs"
if ($LASTEXITCODE -ne 0) { throw 'GUI build failed.' }
Write-Host "Built $bin/FetchTree.exe"
