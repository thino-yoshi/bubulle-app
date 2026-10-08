# Fabrique et publie une version de Bubulle sur GitHub.
#   1. Mets à jour <Version> dans src\Bubulle.csproj (ex. 0.0.4) et fais le commit.
#   2. Lance : powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Notes "Ce qui change"
# Résultat : une « Release » vX.Y.Z avec l'installateur (nouvelles installations) et le .zip
# (mises à jour automatiques, que Bubulle télécharge tout seul au démarrage).
param(
    [string]$Notes = "",
    [switch]$NoPublish
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root "src\Bubulle.csproj"
$version = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "Version introuvable dans $csproj" }
$dist = Join-Path $root "dist"
$app = Join-Path $dist "app"
$zip = Join-Path $dist "Bubulle-$version-win-x64.zip"
$setup = Join-Path $dist "Bubulle-Setup-$version.exe"
Write-Host "== Bubulle v$version"

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null

Write-Host "-- Compilation (version autonome, rien à installer chez tes potes)"
dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $app -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "La compilation a échoué." }

Write-Host "-- Paquet de mise à jour"
Compress-Archive -Path (Join-Path $app "*") -DestinationPath $zip

Write-Host "-- Installateur"
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "C:\Program Files (x86)\Inno Setup 6\ISCC.exe", "C:\Program Files\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup (ISCC.exe) introuvable." }
& $iscc "/DAppVersion=$version" "/DSourceDir=$app" (Join-Path $root "installer\Bubulle.iss") | Out-Null
if ($LASTEXITCODE -ne 0) { throw "L'installateur n'a pas pu être créé." }

Write-Host ("   zip : {0:N0} Mo | installateur : {1:N0} Mo" -f ((Get-Item $zip).Length / 1MB), ((Get-Item $setup).Length / 1MB))
if ($NoPublish) { Write-Host "== Fichiers prêts dans $dist (non publiés)"; return }

Write-Host "-- Publication sur GitHub"
if (-not $Notes) { $Notes = "Bubulle v$version" }
gh release create "v$version" $setup $zip --repo thino-yoshi/bubulle-app --title "Bubulle v$version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "La publication a échoué." }
Write-Host "== Publié : https://github.com/thino-yoshi/bubulle-app/releases/tag/v$version"
