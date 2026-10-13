<#
.SYNOPSIS
    Erzeugt die Symbole des Outlook-Add-ins (outlook-addin\assets\icon-16/32/64/80/128.png).
.DESCRIPTION
    Markenrotes, abgerundetes Quadrat mit weißem Telefonhörer (Glyphe aus Segoe MDL2 Assets,
    auf Windows 10 und 11 vorhanden).
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path "$PSScriptRoot\.."
$assets = Join-Path $root 'outlook-addin\assets'
New-Item -ItemType Directory -Force $assets | Out-Null

$red = [System.Drawing.Color]::FromArgb(0xA9, 0x12, 0x12)
$phone = [string][char]0xE717

foreach ($size in 16, 32, 64, 80, 128) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Abgerundetes Quadrat
    $r = [Math]::Max(2, [int]($size * 0.18))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $size - 1
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($w - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($w - 2 * $r, $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $red), $path)

    # Hörer zentriert
    $font = New-Object System.Drawing.Font('Segoe MDL2 Assets', [float]($size * 0.56), [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $rect = New-Object System.Drawing.RectangleF 0, ([float]($size * 0.03)), $size, $size
    $g.DrawString($phone, $font, [System.Drawing.Brushes]::White, $rect, $fmt)

    $g.Dispose()
    $file = Join-Path $assets "icon-$size.png"
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Host "Geschrieben: $file"
}
