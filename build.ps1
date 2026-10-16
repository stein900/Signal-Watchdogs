# Génère l'installateur : dist\SignalWatchdogs-Setup-<version>.exe
#  - installe Signal Watchdogs dans C:\Program Files\Signal Watchdogs (dossier protégé), menu Démarrer,
#    démarrage automatique optionnel, désinstallation propre (Paramètres > Applications)
#  - cible .NET Framework 4.8, intégré à Windows 10 (1903+) et Windows 11
#
# Prérequis : SDK .NET + Inno Setup 7 (ISCC.exe)
# Usage     : powershell -ExecutionPolicy Bypass -File .\build.ps1 [-SkipTests] [-Portable]
#             -Portable copie aussi l'exe seul dans dist\ (usage dépannage, sans installation)
param([switch]$SkipTests, [switch]$Portable)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

$exe = Join-Path $PSScriptRoot 'bin\Release\net48\SignalWatchdogs.exe'
$ico = Join-Path $PSScriptRoot 'assets\SignalWatchdogs.ico'
$dist = Join-Path $PSScriptRoot 'dist'

$iscc = @("$env:ProgramFiles\Inno Setup 7\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup (ISCC.exe) introuvable : installez Inno Setup 7 depuis https://jrsoftware.org" }

if (-not (Test-Path $ico)) {
    Step "Génération de l'icône"
    dotnet build -c Release -nologo -v q | Out-Null
    if ($LASTEXITCODE) { throw "Échec de la compilation initiale" }
    Start-Process -FilePath $exe -ArgumentList "--export-icon `"$ico`"" -Wait
}

if (-not $SkipTests) {
    Step "Tests du moteur de diagnostic"
    dotnet build tests\SignalWatchdogs.Tests -c Release -nologo -v q | Out-Null
    if ($LASTEXITCODE) { throw "Échec de la compilation des tests" }
    & (Join-Path $PSScriptRoot 'tests\SignalWatchdogs.Tests\bin\Release\net48\SignalWatchdogs.Tests.exe')
    if ($LASTEXITCODE) { throw "Des tests ont échoué" }
}

Step "Compilation Release"
dotnet build -c Release -nologo -v q --no-incremental
if ($LASTEXITCODE) { throw "Échec de la compilation" }

$version = (Get-Item $exe).VersionInfo.FileVersion -replace '^(\d+\.\d+\.\d+).*$', '$1'

Step "Création de l'installateur (version $version)"
New-Item -ItemType Directory -Force $dist | Out-Null
Get-ChildItem $dist -Filter 'SignalWatchdogs*.exe' | Remove-Item -Force
& $iscc /Qp "/DAppVersion=$version" "/DSourceExe=$exe" (Join-Path $PSScriptRoot 'installer\SignalWatchdogs.iss')
if ($LASTEXITCODE) { throw "Échec de la création de l'installateur" }
Copy-Item (Join-Path $PSScriptRoot 'LISEZMOI.txt') $dist -Force
if ($Portable) { Copy-Item $exe $dist -Force }

$setup = Get-Item (Join-Path $dist "SignalWatchdogs-Setup-$version.exe")
Write-Host ("OK : {0} ({1:N0} Ko)" -f $setup.Name, ($setup.Length / 1KB)) -ForegroundColor Green
Write-Host ("SHA-256 : {0}" -f (Get-FileHash $setup.FullName -Algorithm SHA256).Hash) -ForegroundColor Green
