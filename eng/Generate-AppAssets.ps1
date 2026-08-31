param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\Zashboard.App\Assets')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class NativeIconMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr handle);
}
"@

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Rectangle,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Rectangle.Left, $Rectangle.Top, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Top, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.Left, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-BrandMark {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.RectangleF]$Bounds
    )

    $scale = [Math]::Min($Bounds.Width, $Bounds.Height) / 100
    $radius = 18 * $scale
    $backgroundPath = New-RoundedRectanglePath -Rectangle $Bounds -Radius $radius
    $backgroundBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 32, 33, 36))
    $Graphics.FillPath($backgroundBrush, $backgroundPath)

    $teal = [System.Drawing.Color]::FromArgb(255, 42, 169, 157)
    $coral = [System.Drawing.Color]::FromArgb(255, 229, 105, 91)
    $lineWidth = [Math]::Max(2, 7 * $scale)
    $tealPen = [System.Drawing.Pen]::new($teal, $lineWidth)
    $coralPen = [System.Drawing.Pen]::new($coral, $lineWidth)
    $tealPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $tealPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $coralPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $coralPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $x = $Bounds.Left
    $y = $Bounds.Top
    $Graphics.DrawBezier($tealPen, $x + 18 * $scale, $y + 70 * $scale, $x + 42 * $scale, $y + 70 * $scale, $x + 58 * $scale, $y + 30 * $scale, $x + 82 * $scale, $y + 30 * $scale)
    $Graphics.DrawBezier($coralPen, $x + 18 * $scale, $y + 30 * $scale, $x + 42 * $scale, $y + 30 * $scale, $x + 58 * $scale, $y + 70 * $scale, $x + 82 * $scale, $y + 70 * $scale)

    $nodeSize = 11 * $scale
    $whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $points = @(
        [System.Drawing.PointF]::new($x + 18 * $scale, $y + 70 * $scale),
        [System.Drawing.PointF]::new($x + 82 * $scale, $y + 30 * $scale),
        [System.Drawing.PointF]::new($x + 18 * $scale, $y + 30 * $scale),
        [System.Drawing.PointF]::new($x + 82 * $scale, $y + 70 * $scale)
    )
    foreach ($point in $points) {
        $Graphics.FillEllipse($whiteBrush, $point.X - $nodeSize / 2, $point.Y - $nodeSize / 2, $nodeSize, $nodeSize)
    }

    $whiteBrush.Dispose()
    $tealPen.Dispose()
    $coralPen.Dispose()
    $backgroundBrush.Dispose()
    $backgroundPath.Dispose()
}

function New-AssetBitmap {
    param(
        [int]$Width,
        [int]$Height,
        [bool]$IncludeWordmark = $false
    )

    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    if ($IncludeWordmark) {
        $graphics.Clear([System.Drawing.Color]::FromArgb(255, 32, 33, 36))
        $markSize = [Math]::Min($Height * 0.62, $Width * 0.28)
        $markLeft = ($Width - ($markSize + $Width * 0.42)) / 2
        $markTop = ($Height - $markSize) / 2
        Draw-BrandMark -Graphics $graphics -Bounds ([System.Drawing.RectangleF]::new($markLeft, $markTop, $markSize, $markSize))

        $fontSize = [Math]::Max(18, $Height * 0.14)
        $font = [System.Drawing.Font]::new('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $textX = $markLeft + $markSize + $Width * 0.055
        $textY = ($Height - $font.GetHeight($graphics)) / 2
        $graphics.DrawString('Zashboard', $font, $brush, $textX, $textY)
        $brush.Dispose()
        $font.Dispose()
    }
    else {
        $markSize = [Math]::Min($Width, $Height) * 0.82
        $markLeft = ($Width - $markSize) / 2
        $markTop = ($Height - $markSize) / 2
        Draw-BrandMark -Graphics $graphics -Bounds ([System.Drawing.RectangleF]::new($markLeft, $markTop, $markSize, $markSize))
    }

    $graphics.Dispose()
    return $bitmap
}

function Save-PngAsset {
    param(
        [string]$Name,
        [int]$Width,
        [int]$Height,
        [bool]$IncludeWordmark = $false
    )

    $bitmap = New-AssetBitmap -Width $Width -Height $Height -IncludeWordmark $IncludeWordmark
    try {
        $bitmap.Save((Join-Path $OutputDirectory $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
Save-PngAsset -Name 'StoreLogo.png' -Width 50 -Height 50
Save-PngAsset -Name 'Square44x44Logo.png' -Width 44 -Height 44
Save-PngAsset -Name 'Square150x150Logo.png' -Width 150 -Height 150
Save-PngAsset -Name 'Wide310x150Logo.png' -Width 310 -Height 150 -IncludeWordmark $true
Save-PngAsset -Name 'SplashScreen.png' -Width 620 -Height 300 -IncludeWordmark $true

$iconBitmap = New-AssetBitmap -Width 256 -Height 256
$iconHandle = $iconBitmap.GetHicon()
try {
    $icon = [System.Drawing.Icon]::FromHandle($iconHandle)
    $stream = [System.IO.File]::Create((Join-Path $OutputDirectory 'AppIcon.ico'))
    try {
        $icon.Save($stream)
    }
    finally {
        $stream.Dispose()
        $icon.Dispose()
    }
}
finally {
    [NativeIconMethods]::DestroyIcon($iconHandle) | Out-Null
    $iconBitmap.Dispose()
}
