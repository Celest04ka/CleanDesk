# Builds CleanDesk.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# Nothing to install. Usage:
#   powershell -ExecutionPolicy Bypass -File build.ps1               -> bin\CleanDesk.exe
#   powershell -ExecutionPolicy Bypass -File build.ps1 -OutDir <dir>

param([string]$OutDir = 'bin')

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }

$OutDir = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, $OutDir))
$exe = Join-Path $OutDir 'CleanDesk.exe'
if (Get-Process CleanDesk -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw "$exe is running. Exit it from the tray icon menu and build again."
}
New-Item -ItemType Directory -Force $OutDir | Out-Null

& $csc /nologo /codepage:65001 /target:winexe /optimize+ "/out:$exe" `
    /win32icon:assets\icon.ico '/resource:assets\icon.ico,CleanDesk.ico' `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll src\CleanDesk.cs
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host "Built $exe"
