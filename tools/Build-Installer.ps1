<#
.SYNOPSIS
    Baut das MSI-Paket für brandline und die Versionsdatei für automatische Updates.
.DESCRIPTION
    1. Lizenzhinweise der Drittkomponenten aktualisieren
    2. App „self-contained“ veröffentlichen (.NET-Laufzeit enthalten – Zielrechner brauchen nichts)
    3. MSI mit WiX bauen                       →  artifacts\brandline-<Version>-x64.msi
    4. Versionsdatei für Updates erzeugen      →  artifacts\latest.json (Version, Dateiname, SHA-256, Hinweise)
    Die Version kommt aus src\Brandline\Brandline.csproj (<Version>). Für ein Update dort hochzählen.
    Vorgaben: installer\defaults.json, überlagert von installer\defaults.local.json (nicht im Repository,
    z. B. M365ClientId/M365TenantId) → artifacts\defaults.json, das ins MSI kommt.

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
# Vorgaben zusammenführen: Werte aus defaults.local.json ersetzen die aus defaults.json
$preset = Get-Content (Join-Path $root 'installer\defaults.json') -Raw | ConvertFrom-Json
$localPreset = Join-Path $root 'installer\defaults.local.json'
if (Test-Path $localPreset) {
    (Get-Content $localPreset -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object {
        $preset | Add-Member -NotePropertyName $_.Name -NotePropertyValue $_.Value -Force
    }
}
if (-not $preset.M365ClientId -or -not $preset.M365TenantId) {
    Write-Warning 'Keine M365ClientId/M365TenantId – Microsoft-365-Kontakte sind im MSI nicht vorkonfiguriert (siehe README).'
}
$defaultsJson = Join-Path $outDir 'defaults.json'
[System.IO.File]::WriteAllText($defaultsJson, ($preset | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))

dotnet build (Join-Path $root 'installer\Brandline.Installer.wixproj') -c Release `
    -p:ProductVersion=$version -p:AppDir=$appDir -p:DefaultsJson=$defaultsJson -o $outDir -nologo -v q
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
