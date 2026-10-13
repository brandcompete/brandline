<#
.SYNOPSIS
    Baut das MSI-Paket für brandline und die Versionsdatei für automatische Updates.
.DESCRIPTION
    1. Lizenzhinweise der Drittkomponenten aktualisieren
    2. App „self-contained“ veröffentlichen (.NET-Laufzeit enthalten – Zielrechner brauchen nichts)
    3. MSI mit WiX bauen                       →  artifacts\brandline-<Version>-x64.msi
    4. Versionsdatei für Updates erzeugen      →  artifacts\latest.json (Version, Dateiname, SHA-256, Hinweise)
    Die Version kommt aus src\Brandline\Brandline.csproj (<Version>). Für ein Update dort hochzählen.

    Veröffentlichen: MSI und latest.json auf den Update-Server laden (gleicher Ordner, zuerst das MSI,
    dann latest.json – sonst finden Clients kurzzeitig eine Version, deren MSI noch fehlt).
.PARAMETER Notes
    Kurzer Hinweistext zur neuen Version (erscheint im Update-Hinweis der App).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Build-Installer.ps1 -Notes "Verschlüsselte Telefonie (TLS/SRTP)"
#>
param([string]$Notes = '')

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
$project = Join-Path $root 'src\Brandline\Brandline.csproj'
$appDir = Join-Path $root 'artifacts\app'
$outDir = Join-Path $root 'artifacts'

[xml]$csproj = Get-Content $project -Raw
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'Keine <Version> in Brandline.csproj gefunden.' }
Write-Host "brandline $version" -ForegroundColor Cyan

Write-Host '1/4 Lizenzhinweise …'
& (Join-Path $PSScriptRoot 'Generate-ThirdPartyNotices.ps1')

Write-Host '2/4 App veröffentlichen (self-contained) …'
if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }
# Kein Single-File: der Installer legt die Laufzeit als normale Dateien ab (nichts wird nach %TEMP% entpackt).
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -o $appDir -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish fehlgeschlagen.' }

Write-Host '3/4 MSI bauen …'
dotnet build (Join-Path $root 'installer\Brandline.Installer.wixproj') -c Release `
    -p:ProductVersion=$version -p:AppDir=$appDir -o $outDir -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'MSI-Build fehlgeschlagen.' }

$msiName = "brandline-$version-x64.msi"
$msi = Join-Path $outDir $msiName

Write-Host '4/4 Versionsdatei latest.json …'
$latest = [ordered]@{
    version   = $version
    msi       = $msiName
    sha256    = (Get-FileHash $msi -Algorithm SHA256).Hash
    notes     = $Notes
    published = (Get-Date).ToString('yyyy-MM-dd')
}
$json = $latest | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $outDir 'latest.json'), $json, [System.Text.UTF8Encoding]::new($false))

# Outlook-Add-in: Dateien für den Webserver (…/brandline/outlook/), ohne Tests
$outlookOut = Join-Path $outDir 'outlook'
if (Test-Path $outlookOut) { Remove-Item -Recurse -Force $outlookOut }
Copy-Item -Recurse (Join-Path $root 'outlook-addin') $outlookOut
Remove-Item -Recurse -Force (Join-Path $outlookOut 'test')

Write-Host ("Fertig: {0} ({1:N1} MB) + latest.json + outlook\" -f $msi, ((Get-Item $msi).Length / 1MB)) -ForegroundColor Green
