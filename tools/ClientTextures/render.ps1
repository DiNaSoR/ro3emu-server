# Renders English replacements for client textures that bake Chinese text.
# Each replacement keeps the original's size and look: the letters are filled with the original texture's own
# pixels (its colour and brush hatching, spread over the transparent gaps), and a soft glow takes the original's
# halo colour. Run with Windows PowerShell 5.1 (System.Drawing):
#   powershell -File tools\ClientTextures\render.ps1 -Dump <client>\ro3emu\dump
# Originals come from the client hook's "!client dumptex <filter>"; output goes to src\Ro3Emu.ClientHooks\Textures,
# embedded into the hooks DLL (the game folder gets no loose image files).
param(
  [Parameter(Mandatory)][string]$Dump,
  [string]$Out = (Join-Path $PSScriptRoot '..\..\src\Ro3Emu.ClientHooks\Textures')
)
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null

# texture name -> English text (Localization: 精炼成功 "Refinement Successful", 精炼失败 "Refinement Failed")
$jobs = @(
  @{ Name = 'txtimg_cg_success_strengthen_01_n'; Text = 'SUCCESS' },
  @{ Name = 'txtimg_sb_fail_strengthen_01_n'; Text = 'FAILED' }
)

function Get-Px($bmp) {
  $rect = New-Object Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
  $data = $bmp.LockBits($rect, 'ReadOnly', 'Format32bppArgb')
  $bytes = New-Object byte[] ($data.Stride * $bmp.Height)
  [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
  $bmp.UnlockBits($data)
  return $bytes
}

function Set-Px($bmp, [byte[]]$bytes) {
  $rect = New-Object Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
  $data = $bmp.LockBits($rect, 'WriteOnly', 'Format32bppArgb')
  [Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, $bytes.Length)
  $bmp.UnlockBits($data)
}

# Fill texture: the original's solid pixels, each transparent pixel taking the nearest solid one on its row/column.
function New-Fill($src) {
  $w = $src.Width; $h = $src.Height
  $p = Get-Px $src
  $solid = New-Object bool[] ($w * $h)
  for ($i = 0; $i -lt $w * $h; $i++) { $solid[$i] = $p[$i * 4 + 3] -ge 200 }
  $q = $p.Clone()
  for ($pass = 0; $pass -lt 2; $pass++) {
    for ($y = 0; $y -lt $h; $y++) {
      $last = -1
      for ($x = 0; $x -lt $w; $x++) {
        $i = $y * $w + $x
        if ($solid[$i]) { $last = $i } elseif ($last -ge 0) { for ($k = 0; $k -lt 3; $k++) { $q[$i * 4 + $k] = $q[$last * 4 + $k] }; $solid[$i] = $true }
      }
    }
    for ($x = 0; $x -lt $w; $x++) {
      $last = -1
      for ($y = $h - 1; $y -ge 0; $y--) {
        $i = $y * $w + $x
        if ($solid[$i]) { $last = $i } elseif ($last -ge 0) { for ($k = 0; $k -lt 3; $k++) { $q[$i * 4 + $k] = $q[$last * 4 + $k] }; $solid[$i] = $true }
      }
    }
  }
  for ($i = 0; $i -lt $w * $h; $i++) { $q[$i * 4 + 3] = 255 }
  $fill = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  Set-Px $fill $q
  return $fill
}

# The halo colour: average of the half-transparent pixels around the letters.
function Get-Halo($src) {
  $p = Get-Px $src
  [double]$r = 0; [double]$g = 0; [double]$b = 0; $n = 0
  for ($i = 0; $i -lt $p.Length; $i += 4) {
    if ($p[$i + 3] -ge 20 -and $p[$i + 3] -le 120) { $b += $p[$i]; $g += $p[$i + 1]; $r += $p[$i + 2]; $n++ }
  }
  if ($n -eq 0) { return [Drawing.Color]::White }
  # The dump's half-transparent pixels read darker than they show (premultiplied edges): lift towards white.
  $lift = { param($v) [int](($v / $n) * 0.35 + 255 * 0.65) }
  return [Drawing.Color]::FromArgb(255, (& $lift $r), (& $lift $g), (& $lift $b))
}

foreach ($job in $jobs) {
  $src = [Drawing.Bitmap]::FromFile((Join-Path $Dump ($job.Name + '.png')))
  $w = $src.Width; $h = $src.Height
  $fill = New-Fill $src
  $halo = Get-Halo $src

  # Text path fitted to ~88% of the width and ~62% of the height, centred.
  $family = New-Object Drawing.FontFamily 'Georgia'
  $path = New-Object Drawing.Drawing2D.GraphicsPath
  $fmt = [Drawing.StringFormat]::GenericTypographic
  $path.AddString($job.Text, $family, [int][Drawing.FontStyle]::Bold, 100, (New-Object Drawing.PointF 0, 0), $fmt)
  $bounds = $path.GetBounds()
  $scale = [Math]::Min(($w * 0.88) / $bounds.Width, ($h * 0.62) / $bounds.Height)
  $m = New-Object Drawing.Drawing2D.Matrix
  $m.Translate(($w - $bounds.Width * $scale) / 2 - $bounds.X * $scale, ($h - $bounds.Height * $scale) / 2 - $bounds.Y * $scale)
  $m.Scale($scale, $scale)
  $path.Transform($m)

  # Glow: the letters drawn wide in the halo colour at low resolution, scaled back up (a cheap blur).
  $small = New-Object Drawing.Bitmap ([int]($w / 6)), ([int]($h / 6)), ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $gs = [Drawing.Graphics]::FromImage($small)
  $gs.SmoothingMode = 'AntiAlias'
  $gs.ScaleTransform(1 / 6, 1 / 6)
  $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(95, $halo)), 22
  $pen.LineJoin = 'Round'
  $gs.DrawPath($pen, $path)
  $gs.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(110, $halo))), $path)
  $gs.Dispose()

  $outBmp = New-Object Drawing.Bitmap $w, $h, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($outBmp)
  $g.Clear([Drawing.Color]::Transparent)
  $g.InterpolationMode = 'HighQualityBicubic'
  $g.DrawImage($small, (New-Object Drawing.Rectangle -6, -6, ($w + 12), ($h + 12)))
  $g.SmoothingMode = 'AntiAlias'
  # A thin dark rim keeps the letters readable over bright effects, as the original's brush edges do.
  $g.DrawPath((New-Object Drawing.Pen ([Drawing.Color]::FromArgb(70, 40, 30, 20)), 2), $path)
  $g.FillPath((New-Object Drawing.TextureBrush $fill), $path)
  $g.Dispose()

  $file = Join-Path $Out ($job.Name + '.png')
  $outBmp.Save($file, [Drawing.Imaging.ImageFormat]::Png)
  "$($job.Name) -> $file ($w x $h, halo $halo)"
  $src.Dispose(); $fill.Dispose(); $outBmp.Dispose(); $small.Dispose()
}
