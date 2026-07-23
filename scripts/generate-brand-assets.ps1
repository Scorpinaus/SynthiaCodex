[CmdletBinding()]
param(
    [string]$SourcePath
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path $repoRoot "assets\branding\synthiacode-logo-symbol-v1.png"
}

$sourceFullPath = [System.IO.Path]::GetFullPath($SourcePath)
$outputDirectory = Join-Path $repoRoot "src\SynthiaCode.App\Assets\Branding"
$outputFullPath = [System.IO.Path]::GetFullPath($outputDirectory)
$repoFullPath = [System.IO.Path]::GetFullPath($repoRoot)
$expectedPrefix = $repoFullPath.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar

if (-not $outputFullPath.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write branding assets outside the repository: $outputFullPath"
}

if (-not (Test-Path -LiteralPath $sourceFullPath -PathType Leaf)) {
    throw "The approved logo source was not found: $sourceFullPath"
}

Add-Type -AssemblyName System.Drawing

function New-ResizedBitmap {
    param(
        [Parameter(Mandatory)]
        [System.Drawing.Image]$Source,

        [Parameter(Mandatory)]
        [int]$Size
    )

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.DrawImage(
            $Source,
            [System.Drawing.Rectangle]::new(0, 0, $Size, $Size))
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Write-MultiSizeIcon {
    param(
        [Parameter(Mandatory)]
        [System.Drawing.Image]$Source,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $entries = foreach ($size in $sizes) {
        $bitmap = New-ResizedBitmap -Source $Source -Size $size
        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{
                Size = $size
                Data = $stream.ToArray()
            }
        }
        finally {
            $stream.Dispose()
            $bitmap.Dispose()
        }
    }

    $fileStream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $writer = [System.IO.BinaryWriter]::new($fileStream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$entries.Count)

        $offset = 6 + (16 * $entries.Count)
        foreach ($entry in $entries) {
            $dimension = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$entry.Data.Length)
            $writer.Write([uint32]$offset)
            $offset += $entry.Data.Length
        }

        foreach ($entry in $entries) {
            $writer.Write($entry.Data)
        }
    }
    finally {
        $writer.Dispose()
        $fileStream.Dispose()
    }
}

New-Item -ItemType Directory -Path $outputFullPath -Force | Out-Null

$logoPath = Join-Path $outputFullPath "SynthiaCodeLogo.png"
$iconPath = Join-Path $outputFullPath "SynthiaCode.ico"
$logoTemporaryPath = "$logoPath.tmp.png"
$iconTemporaryPath = "$iconPath.tmp.ico"

$source = [System.Drawing.Bitmap]::new($sourceFullPath)
try {
    if ($source.Width -ne 1254 -or $source.Height -ne 1254) {
        throw "The approved logo source must remain 1254x1254; found $($source.Width)x$($source.Height)."
    }

    $crop = [System.Drawing.Rectangle]::new(225, 200, 780, 780)
    $master = [System.Drawing.Bitmap]::new(
        512,
        512,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($master)
    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.DrawImage(
            $source,
            [System.Drawing.Rectangle]::new(0, 0, 512, 512),
            $crop,
            [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally {
        $graphics.Dispose()
    }

    try {
        $master.Save($logoTemporaryPath, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-MultiSizeIcon -Source $master -Path $iconTemporaryPath
    }
    finally {
        $master.Dispose()
    }

    Move-Item -LiteralPath $logoTemporaryPath -Destination $logoPath -Force
    Move-Item -LiteralPath $iconTemporaryPath -Destination $iconPath -Force
}
finally {
    $source.Dispose()
    Remove-Item -LiteralPath $logoTemporaryPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $iconTemporaryPath -Force -ErrorAction SilentlyContinue
}

Write-Host "Generated branding assets:"
Write-Host "  $logoPath"
Write-Host "  $iconPath"
