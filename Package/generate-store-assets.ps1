# Revenant Theme Studio — Store tile asset generator
#
# Resamples the 2560x2560 app mark (Assets\rts_app-alt.png) and the 2:1 RTS lockup
# (Assets\rts.png) into the full set of tiles the MSIX manifest references. Output
# lands in Package\Assets\ so the user can wire them into a Windows Application
# Packaging Project (.wapproj) without rerunning this script.
#
# Outputs: square tiles from rts_app-alt.png (transparent padded), wide tile +
# splash screen from rts.png (letterboxed to 2.07:1 target aspect).
#
# Re-run safe: overwrites existing files in Package\Assets\.

Add-Type -AssemblyName System.Drawing

$repoRoot       = Split-Path -Parent $PSScriptRoot
$sourceSquare   = Join-Path $repoRoot 'Assets\rts_app-alt.png'
$sourceWide     = Join-Path $repoRoot 'Assets\rts.png'
$outDir         = Join-Path $PSScriptRoot 'Assets'

if (-not (Test-Path $sourceSquare)) { throw "Missing source: $sourceSquare" }
if (-not (Test-Path $sourceWide))   { throw "Missing source: $sourceWide" }
if (-not (Test-Path $outDir))       { New-Item -ItemType Directory -Path $outDir | Out-Null }

function Resize-Square {
    param([System.Drawing.Image]$src, [int]$size, [string]$dest)
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
    $g.Dispose()
    $bmp.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  wrote $([IO.Path]::GetFileName($dest)) ($size x $size)"
}

function Resize-Wide {
    param([System.Drawing.Image]$src, [int]$width, [int]$height, [string]$dest)
    $bmp = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Letterbox: scale to fit the target rectangle without distortion.
    $srcRatio  = $src.Width / $src.Height
    $destRatio = $width / $height
    if ($srcRatio -gt $destRatio) {
        $drawW = $width
        $drawH = [int]([math]::Round($width / $srcRatio))
    } else {
        $drawH = $height
        $drawW = [int]([math]::Round($height * $srcRatio))
    }
    $x = [int](($width  - $drawW) / 2)
    $y = [int](($height - $drawH) / 2)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle $x, $y, $drawW, $drawH))
    $g.Dispose()
    $bmp.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  wrote $([IO.Path]::GetFileName($dest)) ($width x $height)"
}

Write-Host 'Generating Store tile assets...'
$square = [System.Drawing.Image]::FromFile($sourceSquare)
try {
    Resize-Square $square 50  (Join-Path $outDir 'StoreLogo.png')
    Resize-Square $square 44  (Join-Path $outDir 'Square44x44Logo.png')
    Resize-Square $square 88  (Join-Path $outDir 'Square44x44Logo.scale-200.png')
    Resize-Square $square 150 (Join-Path $outDir 'Square150x150Logo.png')
    Resize-Square $square 300 (Join-Path $outDir 'Square150x150Logo.scale-200.png')
    Resize-Square $square 310 (Join-Path $outDir 'Square310x310Logo.png')
    Resize-Square $square 620 (Join-Path $outDir 'Square310x310Logo.scale-200.png')
} finally { $square.Dispose() }

$wide = [System.Drawing.Image]::FromFile($sourceWide)
try {
    Resize-Wide $wide 310 150 (Join-Path $outDir 'Wide310x150Logo.png')
    Resize-Wide $wide 620 300 (Join-Path $outDir 'Wide310x150Logo.scale-200.png')
    Resize-Wide $wide 620 300 (Join-Path $outDir 'SplashScreen.png')
    Resize-Wide $wide 1240 600 (Join-Path $outDir 'SplashScreen.scale-200.png')
} finally { $wide.Dispose() }

Write-Host ''
Write-Host "Done. Generated $(Get-ChildItem $outDir -Filter *.png | Measure-Object).Count tiles under Package\Assets\."
