# Generates the branded wizard images used by BagaGame.iss.
# Run from the repository root:  powershell -File installer\make-art.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$installerDir = $PSScriptRoot

function New-RoundedPath([int]$x, [int]$y, [int]$w, [int]$h, [int]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Set-UpText($g, [string]$font, [single]$size, [string]$color, [System.Drawing.FontStyle]$style) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($color)
    return New-Object System.Drawing.Font($font, $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
}

# ---------------------------------------------------------------- large image

$w = 164; $h = 314
$large = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$g = [System.Drawing.Graphics]::FromImage($large)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

# Dark vertical gradient background.
$bgRect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $bgRect,
    [System.Drawing.ColorTranslator]::FromHtml('#131313'),
    [System.Drawing.ColorTranslator]::FromHtml('#232323'),
    90.0)
$g.FillRectangle($bg, $bgRect)

# Soft lime glow behind the logo.
$glowRect = New-Object System.Drawing.Rectangle 22, 74, 120, 120
$glow = New-Object System.Drawing.Drawing2D.PathGradientBrush((New-RoundedPath 22 74 120 120 30))
$glow.CenterColor = [System.Drawing.Color]::FromArgb(46, 165, 242, 107)
$glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 165, 242, 107))
$g.FillPath($glow, (New-RoundedPath 16 68 132 132 34))

# Lime logo tile.
$tile = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#A5F26B'))
$g.FillPath($tile, (New-RoundedPath 50 92 64 64 16))

# Monogram.
$mono = Set-UpText $g 'Segoe UI' 30 '#101A0C' ([System.Drawing.FontStyle]::Bold)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = [System.Drawing.StringAlignment]::Center
$sf.LineAlignment = [System.Drawing.StringAlignment]::Center
$g.DrawString('BG', $mono, (New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#101A0C'))), 82, 124, $sf)

# Wordmark.
$title = Set-UpText $g 'Segoe UI' 17 '#F3F3F3' ([System.Drawing.FontStyle]::Bold)
$g.DrawString('BagaGame', $title, (New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#F3F3F3'))), 82, 196, $sf)

$caption = Set-UpText $g 'Segoe UI' 9 '#8F8F8F' ([System.Drawing.FontStyle]::Regular)
$g.DrawString('GAME LAUNCHER', $caption, (New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#8F8F8F'))), 82, 216, $sf)

# Lime rule and footer caption.
$rule = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#A5F26B'))
$g.FillRectangle($rule, 62, 240, 40, 2)
$version = Set-UpText $g 'Segoe UI' 8 '#6E6E6E' ([System.Drawing.FontStyle]::Regular)
$g.DrawString('WPF  ·  .NET 8', $version, (New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#6E6E6E'))), 82, 252, $sf)

$g.Dispose()
$largePath = Join-Path $installerDir 'wizard-large.bmp'
$large.Save($largePath, [System.Drawing.Imaging.ImageFormat]::Bmp)
$large.Dispose()
Write-Output ("saved {0} ({1} bytes)" -f $largePath, (Get-Item $largePath).Length)

# ---------------------------------------------------------------- small image

$sw = 55; $sh = 55
$small = New-Object System.Drawing.Bitmap $sw, $sh, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
$gs = [System.Drawing.Graphics]::FromImage($small)
$gs.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$gs.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$gs.Clear([System.Drawing.ColorTranslator]::FromHtml('#131313'))

$gs.FillPath(
    (New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#A5F26B'))),
    (New-RoundedPath 9 9 37 37 10))

$monoSmall = Set-UpText $gs 'Segoe UI' 17 '#101A0C' ([System.Drawing.FontStyle]::Bold)
$sfSmall = New-Object System.Drawing.StringFormat
$sfSmall.Alignment = [System.Drawing.StringAlignment]::Center
$sfSmall.LineAlignment = [System.Drawing.StringAlignment]::Center
$gs.DrawString('BG', $monoSmall, (New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#101A0C'))), 27.5, 27.5, $sfSmall)

$gs.Dispose()
$smallPath = Join-Path $installerDir 'wizard-small.bmp'
$small.Save($smallPath, [System.Drawing.Imaging.ImageFormat]::Bmp)
$small.Dispose()
Write-Output ("saved {0} ({1} bytes)" -f $smallPath, (Get-Item $smallPath).Length)