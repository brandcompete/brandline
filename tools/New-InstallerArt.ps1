<#
.SYNOPSIS
    Erzeugt die Bilder für den MSI-Installer im brandline-Design (installer\Assets\dialog.bmp, banner.bmp).
.DESCRIPTION
    dialog.bmp (493 × 312): Willkommens-/Abschlussseite. Links ein dunkles Feld mit Wortmarke,
    rechts bleibt Platz für den Text des Installers (weiß).
    banner.bmp (493 × 58): Kopfzeile der übrigen Seiten; links steht der Titel des Installers,
    rechts die Wortmarke.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path "$PSScriptRoot\.."
$assets = Join-Path $root 'installer\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

# Hausschrift Rubik aus dem Projekt laden (Rückfall: Segoe UI)
$fonts = New-Object System.Drawing.Text.PrivateFontCollection
# Nur Regular und Bold laden – mit allen Schnitten wählt GDI+ für „Regular“ sonst einen fetteren.
foreach ($f in 'Rubik-Regular.ttf', 'Rubik-Bold.ttf') { $fonts.AddFontFile((Join-Path $root "src\Brandline\Fonts\$f")) }
$rubik = $fonts.Families | Where-Object Name -eq 'Rubik' | Select-Object -First 1
function New-Font([float]$size, [System.Drawing.FontStyle]$style) {
    if ($rubik) { New-Object System.Drawing.Font($rubik, $size, $style, [System.Drawing.GraphicsUnit]::Pixel) }
    else { New-Object System.Drawing.Font('Segoe UI', $size, $style, [System.Drawing.GraphicsUnit]::Pixel) }
}

$red = [System.Drawing.Color]::FromArgb(0xA9, 0x12, 0x12)
$dark = [System.Drawing.Color]::FromArgb(0x1A, 0x1A, 0x2E)
$darkRed = [System.Drawing.Color]::FromArgb(0x3A, 0x14, 0x16)

function Draw-Wordmark($g, [float]$x, [float]$y, [float]$size, $color) {
    $square = $size * 0.62
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $red), $x, $y + $size * 0.24, $square, $square)
    $bold = New-Font $size ([System.Drawing.FontStyle]::Bold)
    $regular = New-Font $size ([System.Drawing.FontStyle]::Regular)
    $brush = New-Object System.Drawing.SolidBrush $color
    $fmt = [System.Drawing.StringFormat]::GenericTypographic
    $tx = $x + $square + $size * 0.35
    $g.DrawString('brand', $bold, $brush, $tx, $y, $fmt)
    $w = $g.MeasureString('brand', $bold, 1000, $fmt).Width
    $g.DrawString('line', $regular, $brush, $tx + $w, $y, $fmt)
}

function Save-Bmp($bmp, $name) {
    $path = Join-Path $assets $name
    # WiX erwartet klassische 24-Bit-BMPs
    $out = New-Object System.Drawing.Bitmap($bmp.Width, $bmp.Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($out); $g.DrawImage($bmp, 0, 0, $bmp.Width, $bmp.Height); $g.Dispose()
    $out.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp); $out.Dispose()
    Write-Host "Geschrieben: $path"
}

# ---------- dialog.bmp ----------
$bmp = New-Object System.Drawing.Bitmap 493, 312
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::White)
$panel = New-Object System.Drawing.Rectangle 0, 0, 164, 312
$grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($panel, $dark, $darkRed, 60)
$g.FillRectangle($grad, $panel)
# dezenter roter Schimmer unten rechts im Feld
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath; $glow.AddEllipse(40, 170, 220, 220)
$pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
$pgb.CenterColor = [System.Drawing.Color]::FromArgb(90, 0xC4, 0x15, 0x15); $pgb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0xC4, 0x15, 0x15))
$g.SetClip($panel); $g.FillPath($pgb, $glow); $g.ResetClip()
Draw-Wordmark $g 18 26 24 ([System.Drawing.Color]::White)
$sub = New-Font 12 ([System.Drawing.FontStyle]::Regular)
$g.DrawString('SIP-Softphone', $sub, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(180, 255, 255, 255))), 20, 62)
$g.DrawString('brandcompete GmbH', $sub, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 255, 255, 255))), 20, 284)
$g.Dispose(); Save-Bmp $bmp 'dialog.bmp'; $bmp.Dispose()

# ---------- banner.bmp ----------
$bmp = New-Object System.Drawing.Bitmap 493, 58
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::White)
Draw-Wordmark $g 356 17 18 $dark
$g.FillRectangle((New-Object System.Drawing.SolidBrush $red), 0, 56, 493, 2)
$g.Dispose(); Save-Bmp $bmp 'banner.bmp'; $bmp.Dispose()
