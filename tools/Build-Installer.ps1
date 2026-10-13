<#
.SYNOPSIS
    Baut das MSI-Paket für brandline.
.DESCRIPTION
    1. Lizenzhinweise der Drittkomponenten aktualisieren
    2. App „self-contained“ als einzelne .exe veröffentlichen (.NET-Laufzeit enthalten – Zielrechner brauchen nichts)
    3. MSI mit WiX bauen  →  artifacts\brandline-<Version>-x64.msi
    Die Version kommt aus src\Brandline\Brandline.csproj (<Version>). Für ein Update dort hochzählen.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Build-Installer.ps1
#>
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
$project = Join-Path $root 'src\Brandline\Brandline.csproj'
$appDir = Join-Path $root 'artifacts\app'
$outDir = Join-Path $root 'artifacts'

[xml]$csproj = Get-Content $project -Raw
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'Keine <Version> in Brandline.csproj gefunden.' }
Write-Host "brandline $version" -ForegroundColor Cyan

Write-Host '1/3 Lizenzhinweise …'
& (Join-Path $PSScriptRoot 'Generate-ThirdPartyNotices.ps1')

Write-Host '2/3 App veröffentlichen (self-contained) …'
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
# Kein Single-File: der Installer legt die Laufzeit als normale Dateien ab (nichts wird nach %TEMP% entpackt).
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -o $appDir -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish fehlgeschlagen.' }

Write-Host '3/3 MSI bauen …'
dotnet build (Join-Path $root 'installer\Brandline.Installer.wixproj') -c Release `
    -p:ProductVersion=$version -p:AppDir=$appDir -o $outDir -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'MSI-Build fehlgeschlagen.' }

$msi = Join-Path $outDir "brandline-$version-x64.msi"
Write-Host ("Fertig: {0} ({1:N1} MB)" -f $msi, ((Get-Item $msi).Length / 1MB)) -ForegroundColor Green
