[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $PSScriptRoot 'manifest.json'
$manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
$dllPath = Join-Path $projectRoot "bin\\$Configuration\\FuriousServerCharacters.dll"
$readmePath = Join-Path $projectRoot 'README.md'
$stagePath = Join-Path $PSScriptRoot 'staging'
$iconPath = Join-Path $PSScriptRoot 'icon.png'
$archivePath = Join-Path $PSScriptRoot ("{0}-{1}.zip" -f $manifest.name, $manifest.version_number)

if (-not (Test-Path -LiteralPath $dllPath)) {
    throw "Build output was not found: $dllPath. Build the project before creating the package."
}

function New-ThunderstoreIcon {
    param([string]$Path)

    Add-Type -AssemblyName System.Drawing

    $size = 256
    $scale = 4.0
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::FromArgb(9, 13, 11))

    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, $size, $size),
        [System.Drawing.Color]::FromArgb(29, 48, 35),
        [System.Drawing.Color]::FromArgb(9, 13, 11),
        45)
    $graphics.FillRectangle($background, 0, 0, $size, $size)
    $background.Dispose()

    $shield = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $shield.AddPolygon([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(32 * $scale, 3 * $scale),
        [System.Drawing.PointF]::new(55 * $scale, 13 * $scale),
        [System.Drawing.PointF]::new(55 * $scale, 30 * $scale),
        [System.Drawing.PointF]::new(32 * $scale, 61 * $scale),
        [System.Drawing.PointF]::new(9 * $scale, 30 * $scale),
        [System.Drawing.PointF]::new(9 * $scale, 13 * $scale)
    ))
    $shieldFill = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, $size, $size),
        [System.Drawing.Color]::FromArgb(29, 48, 35),
        [System.Drawing.Color]::FromArgb(9, 13, 11),
        45)
    $gold = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, $size, $size),
        [System.Drawing.Color]::FromArgb(246, 213, 141),
        [System.Drawing.Color]::FromArgb(195, 132, 50),
        90)
    $graphics.FillPath($shieldFill, $shield)
    $graphics.DrawPath([System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(246, 213, 141), 3 * $scale), $shield)
    $shieldFill.Dispose()
    $gold.Dispose()
    $shield.Dispose()

    $inner = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $inner.AddPolygon([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(32 * $scale, 8 * $scale),
        [System.Drawing.PointF]::new(50 * $scale, 16 * $scale),
        [System.Drawing.PointF]::new(50 * $scale, 30 * $scale),
        [System.Drawing.PointF]::new(32 * $scale, 55.5 * $scale),
        [System.Drawing.PointF]::new(14 * $scale, 30 * $scale),
        [System.Drawing.PointF]::new(14 * $scale, 16 * $scale)
    ))
    $graphics.FillPath([System.Drawing.Brushes]::Transparent, $inner)
    $graphics.DrawPath([System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(72, 239, 200, 118), 1 * $scale), $inner)
    $inner.Dispose()

    $rune = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $rune.AddPolygon([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new(43 * $scale, 20 * $scale),
        [System.Drawing.PointF]::new(36 * $scale, 15 * $scale),
        [System.Drawing.PointF]::new(24 * $scale, 15 * $scale),
        [System.Drawing.PointF]::new(17 * $scale, 22 * $scale),
        [System.Drawing.PointF]::new(17 * $scale, 42 * $scale),
        [System.Drawing.PointF]::new(24 * $scale, 49 * $scale),
        [System.Drawing.PointF]::new(36 * $scale, 49 * $scale),
        [System.Drawing.PointF]::new(43 * $scale, 44 * $scale),
        [System.Drawing.PointF]::new(38 * $scale, 38 * $scale),
        [System.Drawing.PointF]::new(33 * $scale, 42 * $scale),
        [System.Drawing.PointF]::new(27 * $scale, 42 * $scale),
        [System.Drawing.PointF]::new(24 * $scale, 39 * $scale),
        [System.Drawing.PointF]::new(24 * $scale, 25 * $scale),
        [System.Drawing.PointF]::new(27 * $scale, 22 * $scale),
        [System.Drawing.PointF]::new(33 * $scale, 22 * $scale),
        [System.Drawing.PointF]::new(38 * $scale, 26 * $scale)
    ))
    $runeGold = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, $size, $size),
        [System.Drawing.Color]::FromArgb(246, 213, 141),
        [System.Drawing.Color]::FromArgb(195, 132, 50),
        90)
    $graphics.FillPath($runeGold, $rune)
    $runeGold.Dispose()
    $rune.Dispose()
    $graphics.FillEllipse([System.Drawing.Brushes]::Black, 30 * $scale, 30 * $scale, 4 * $scale, 4 * $scale)

    $graphics.Dispose()
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

New-ThunderstoreIcon -Path $iconPath

if (Test-Path -LiteralPath $stagePath) {
    Remove-Item -LiteralPath $stagePath -Recurse -Force
}
New-Item -ItemType Directory -Path $stagePath -Force | Out-Null

Copy-Item -LiteralPath $iconPath -Destination (Join-Path $stagePath 'icon.png')
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stagePath 'manifest.json')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CHANGELOG.md') -Destination (Join-Path $stagePath 'CHANGELOG.md')
Copy-Item -LiteralPath $readmePath -Destination (Join-Path $stagePath 'README.md')
Copy-Item -LiteralPath $dllPath -Destination (Join-Path $stagePath 'FuriousServerCharacters.dll')

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -Path (Join-Path $stagePath '*') -DestinationPath $archivePath -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entries = @($archive.Entries.FullName)
    $required = @('icon.png', 'README.md', 'manifest.json', 'CHANGELOG.md', 'FuriousServerCharacters.dll')
    $missing = @($required | Where-Object { $_ -notin $entries })
    if ($missing.Count -gt 0) {
        throw "The package is missing required entries: $($missing -join ', ')"
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Created and validated: $archivePath"
