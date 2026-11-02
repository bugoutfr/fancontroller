$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\src\FanController.App\Assets'
[void][System.IO.Directory]::CreateDirectory($assets)
$bitmap = [System.Drawing.Bitmap]::new(1024, 1024)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)
$graphics.ScaleTransform(4, 4)
$background = [System.Drawing.Drawing2D.GraphicsPath]::new()
$background.AddArc(8, 8, 64, 64, 180, 90)
$background.AddArc(184, 8, 64, 64, 270, 90)
$background.AddArc(184, 184, 64, 64, 0, 90)
$background.AddArc(8, 184, 64, 64, 90, 90)
$background.CloseFigure()
$backgroundBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Point]::new(0, 0), [System.Drawing.Point]::new(256, 256),
    [System.Drawing.ColorTranslator]::FromHtml('#24374D'), [System.Drawing.ColorTranslator]::FromHtml('#172638'))
$graphics.FillPath($backgroundBrush, $background)
$ring = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#41596D'), 3)
$graphics.DrawEllipse($ring, 37, 37, 182, 182)
$blade = [System.Drawing.Drawing2D.GraphicsPath]::new()
$blade.AddBezier(119, 106, 96, 86, 101, 48, 132, 47)
$blade.AddBezier(132, 47, 159, 45, 181, 65, 171, 82)
$blade.AddBezier(171, 82, 164, 94, 148, 105, 137, 111)
$blade.AddBezier(137, 111, 130, 107, 124, 106, 119, 106)
$blade.CloseFigure()
$bladeBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Point]::new(90, 45), [System.Drawing.Point]::new(170, 120),
    [System.Drawing.ColorTranslator]::FromHtml('#76E0CD'), [System.Drawing.ColorTranslator]::FromHtml('#4CC5B5'))
for ($i = 0; $i -lt 4; $i++) {
    $state = $graphics.Save()
    $graphics.TranslateTransform(128, 128)
    $graphics.RotateTransform($i * 90)
    $graphics.TranslateTransform(-128, -128)
    $graphics.FillPath($bladeBrush, $blade)
    $graphics.Restore($state)
}
$hubBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.Point]::new(110, 108), [System.Drawing.Point]::new(145, 150),
    [System.Drawing.ColorTranslator]::FromHtml('#E1F7F1'), [System.Drawing.ColorTranslator]::FromHtml('#BFDED7'))
$graphics.FillEllipse($backgroundBrush, 103, 103, 50, 50)
$graphics.FillEllipse($hubBrush, 110, 110, 36, 36)
$graphics.Dispose()
$background.Dispose()
$backgroundBrush.Dispose()
$blade.Dispose()
$bladeBrush.Dispose()
$hubBrush.Dispose()
$ring.Dispose()

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
try {
    foreach ($size in $sizes) {
        $frame = [System.Drawing.Bitmap]::new($size, $size)
        $g = [System.Drawing.Graphics]::FromImage($frame)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.DrawImage($bitmap, 0, 0, $size, $size)
        $g.Dispose()
        $stream = [System.IO.MemoryStream]::new()
        $frame.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += ,$stream.ToArray()
        if ($size -eq 256) { $frame.Save((Join-Path $assets 'FanController.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
        $stream.Dispose()
        $frame.Dispose()
    }
    $file = [System.IO.File]::Create((Join-Path $assets 'FanController.ico'))
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    }
    finally { $writer.Dispose() }
}
finally { $bitmap.Dispose() }
Write-Output "Generated FanController.ico (9 sizes) and FanController.png in $assets"
