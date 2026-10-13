<#
.SYNOPSIS
    Erzeugt THIRD-PARTY-NOTICES.txt aus den Metadaten aller (auch indirekt) verwendeten NuGet-Pakete.
.DESCRIPTION
    Liest für jedes Paket die .nuspec-Datei aus dem lokalen NuGet-Cache (Lizenz, Copyright, Projekt-URL)
    und hängt mitgelieferte Lizenztexte an. Microsoft-/System-Pakete werden unter „.NET“ zusammengefasst.
    Nach jedem Paket-Update neu ausführen:  powershell -File tools\Generate-ThirdPartyNotices.ps1
#>
param(
    [string]$Project = "$PSScriptRoot\..\src\Brandline\Brandline.csproj",
    [string]$Output = "$PSScriptRoot\..\src\Brandline\THIRD-PARTY-NOTICES.txt"
)

$ErrorActionPreference = 'Stop'
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }

# Alle Pakete (direkt + transitiv) inkl. aufgelöster Version
$packages = dotnet list $Project package --include-transitive |
    Select-String '^\s*>\s+(\S+)\s+(?:\S+\s+)?(\S+)\s*$' |
    ForEach-Object { [pscustomobject]@{ Id = $_.Matches[0].Groups[1].Value; Version = $_.Matches[0].Groups[2].Value } } |
    Sort-Object Id -Unique

# Pakete ohne Lizenzangabe in der .nuspec – Lizenz laut jeweiligem Projekt-Repository
$knownLicenses = @{
    'Common.Logging'         = 'Apache-2.0'   # https://github.com/net-commons/common-logging
    'Common.Logging.Core'    = 'Apache-2.0'
    'Makaretu.Dns'           = 'MIT'          # https://github.com/richardschneider/net-dns
    'Makaretu.Dns.Multicast' = 'MIT'          # https://github.com/richardschneider/net-mdns
}

$isPlatform ={ param($id) $id -match '^(Microsoft\.|System\.|runtime\.|NETStandard\.)' }

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('brandline – Hinweise zu Drittkomponenten / Third-party notices')
[void]$sb.AppendLine('=' * 72)
[void]$sb.AppendLine('brandline © brandcompete GmbH. Dieses Produkt enthält die folgenden Komponenten Dritter,')
[void]$sb.AppendLine('die unter den jeweils genannten Lizenzen stehen.')
[void]$sb.AppendLine('This product includes the following third-party components under the licenses stated below.')
[void]$sb.AppendLine()

# .NET-Laufzeit und Microsoft-Pakete
$platform = $packages | Where-Object { & $isPlatform $_.Id }
[void]$sb.AppendLine('-' * 72)
[void]$sb.AppendLine('.NET Runtime, WPF, Windows Forms und Microsoft-/System-Pakete')
[void]$sb.AppendLine('Copyright (c) .NET Foundation and Contributors / Microsoft Corporation')
[void]$sb.AppendLine('Lizenz: MIT  –  https://github.com/dotnet/runtime/blob/main/LICENSE.TXT')
[void]$sb.AppendLine('Pakete: ' + (($platform | ForEach-Object { "$($_.Id) $($_.Version)" }) -join ', '))
[void]$sb.AppendLine()

foreach ($p in $packages | Where-Object { -not (& $isPlatform $_.Id) }) {
    $dir = Join-Path $cache ("{0}\{1}" -f $p.Id.ToLowerInvariant(), $p.Version)
    $nuspecFile = Get-ChildItem $dir -Filter *.nuspec -ErrorAction SilentlyContinue | Select-Object -First 1
    [void]$sb.AppendLine('-' * 72)
    [void]$sb.AppendLine("$($p.Id) $($p.Version)")
    if (-not $nuspecFile) { [void]$sb.AppendLine('(Metadaten nicht im NuGet-Cache gefunden)'); [void]$sb.AppendLine(); continue }

    [xml]$xml = Get-Content $nuspecFile.FullName -Raw
    $m = $xml.package.metadata
    if ($m.copyright) { [void]$sb.AppendLine("Copyright: $($m.copyright)") }
    elseif ($m.authors) { [void]$sb.AppendLine("Autoren: $($m.authors)") }
    if ($m.projectUrl) { [void]$sb.AppendLine("Projekt: $($m.projectUrl)") }

    $licenseText = $null
    if ($m.license) {
        $type = $m.license.type; $value = if ($m.license.'#text') { $m.license.'#text' } else { "$($m.license)" }
        if ($type -eq 'file') {
            $path = Join-Path $dir $value
            if (Test-Path $path) { $licenseText = Get-Content $path -Raw }
            [void]$sb.AppendLine("Lizenz: siehe Text unten ($value)")
        } else {
            [void]$sb.AppendLine("Lizenz: $value  –  https://spdx.org/licenses/$value.html")
        }
    } elseif ($m.licenseUrl) {
        [void]$sb.AppendLine("Lizenz: $($m.licenseUrl)")
    } elseif ($knownLicenses.ContainsKey($p.Id)) {
        $value = $knownLicenses[$p.Id]
        [void]$sb.AppendLine("Lizenz: $value (laut Projekt-Repository, Paket enthält keine Angabe)  –  https://spdx.org/licenses/$value.html")
    } else {
        [void]$sb.AppendLine('Lizenz: UNBEKANNT – bitte prüfen')
        Write-Warning "Keine Lizenzangabe für $($p.Id) $($p.Version)"
    }
    # Zusätzlich mitgelieferte Lizenzdateien (z. B. LICENSE.txt im Paketstamm)
    if (-not $licenseText) {
        $file = Get-ChildItem $dir -File | Where-Object Name -match '^(LICEN[CS]E|COPYING)(\.(txt|md))?$' | Select-Object -First 1
        if ($file) { $licenseText = Get-Content $file.FullName -Raw }
    }
    if ($licenseText) { [void]$sb.AppendLine(); [void]$sb.AppendLine($licenseText.Trim()) }
    [void]$sb.AppendLine()
}

# Schrift
[void]$sb.AppendLine('-' * 72)
[void]$sb.AppendLine('Schrift Rubik')
[void]$sb.AppendLine('Copyright 2015 The Rubik Project Authors (https://github.com/googlefonts/rubik)')
[void]$sb.AppendLine('Lizenz: SIL Open Font License 1.1 – vollständiger Text in Fonts\OFL-Rubik.txt')
[void]$sb.AppendLine()

[System.IO.File]::WriteAllText((Resolve-Path (Split-Path $Output) | Join-Path -ChildPath (Split-Path $Output -Leaf)), $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "Geschrieben: $Output ($($packages.Count) Pakete)"
