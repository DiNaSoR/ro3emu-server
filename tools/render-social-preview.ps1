# Rebuild the project's text/vector social image on Windows. No game assets are used.
Add-Type -AssemblyName System.Drawing
$assetDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../docs/site'))
$bitmap = [System.Drawing.Bitmap]::new(1280, 640)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$disposables = [System.Collections.Generic.List[System.IDisposable]]::new()
function New-Brush([string]$hex) {
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($hex))
    $disposables.Add($brush)
    return $brush
}
function Draw-Text([string]$text, [int]$size, [string]$family, [System.Drawing.FontStyle]$style, [string]$color, [int]$x, [int]$y) {
    $font = [System.Drawing.Font]::new($family, $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
    $disposables.Add($font)
    $graphics.DrawString($text, $font, (New-Brush $color), $x, $y)
}
try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#111a2a'))
    $gridPen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#1b2a40'), 1)
    $disposables.Add($gridPen)
    for ($x = 850; $x -lt 1280; $x += 36) { $graphics.DrawLine($gridPen, $x, 0, $x, 640) }
    for ($y = 0; $y -lt 640; $y += 36) { $graphics.DrawLine($gridPen, 850, $y, 1280, $y) }
    $graphics.FillRectangle((New-Brush '#e5bd77'), 0, 0, 1280, 7)
    $graphics.FillRectangle((New-Brush '#172237'), 0, 503, 1280, 137)
    Draw-Text 'RO3Emu' 78 'Segoe UI' Bold '#e5bd77' 68 58
    Draw-Text 'Ragnarok Online 3' 61 'Segoe UI' Bold '#f3f1ec' 68 223
    Draw-Text 'Open-source server emulator' 33 'Segoe UI' Regular '#bcc9db' 72 308
    Draw-Text 'C#  /  .NET 10  /  GNU GPL v3.0' 21 'Consolas' Regular '#e2c999' 74 408
    Draw-Text 'github.com/aethai/ro3emu-server' 28 'Consolas' Regular '#e8edf5' 74 550
    $left = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(1088, 130), [System.Drawing.PointF]::new(1008, 276), [System.Drawing.PointF]::new(1088, 441))
    $right = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(1088, 130), [System.Drawing.PointF]::new(1168, 276), [System.Drawing.PointF]::new(1088, 441))
    $top = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(1088, 130), [System.Drawing.PointF]::new(1008, 276), [System.Drawing.PointF]::new(1088, 297), [System.Drawing.PointF]::new(1168, 276))
    $graphics.FillPolygon((New-Brush '#e5bd77'), $left)
    $graphics.FillPolygon((New-Brush '#9d743c'), $right)
    $graphics.FillPolygon((New-Brush '#f7dea6'), $top)
    $bitmap.Save((Join-Path $assetDirectory 'social-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    foreach ($resource in $disposables) { $resource.Dispose() }
    $graphics.Dispose()
    $bitmap.Dispose()
}
Write-Output 'Rendered docs/site/social-preview.png (1280x640).'
