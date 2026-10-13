# Builds Aetheris.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src  = Join-Path $root 'src'
$out  = if ($env:AETHERIS_OUT) { $env:AETHERIS_OUT } else { Join-Path $root 'dist' }
New-Item -ItemType Directory -Force $out | Out-Null

# ---- icon (drawn with System.Drawing, written as a multi-size .ico) ----
$ico = Join-Path $src 'app.ico'
if (-not (Test-Path $ico)) {
  Add-Type -AssemblyName System.Drawing
  function Draw([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $r = [single]($s * 0.23); $d = $r * 2; $w = [single]($s - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(124, 58, 237)), ([System.Drawing.Color]::FromArgb(6, 182, 212))
    $g.FillPath($br, $path)
    $P = { param($x, $y) New-Object System.Drawing.PointF ([single]($x * $s / 64)), ([single]($y * $s / 64)) }
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPolygon($white, [System.Drawing.PointF[]]@((& $P 32 10), (& $P 50 27), (& $P 32 54), (& $P 14 27)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(223, 232, 255))), [System.Drawing.PointF[]]@((& $P 32 10), (& $P 50 27), (& $P 32 54)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(140, 255, 255, 255))), [System.Drawing.PointF[]]@((& $P 14 27), (& $P 50 27), (& $P 32 54)))
    $g.Dispose(); return $bmp
  }
  $sizes = 16, 24, 32, 48, 256
  $images = foreach ($s in $sizes) {
    $bmp = Draw $s
    if ($s -eq 256) { $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); ,$ms.ToArray() }
    else {
      # 32-bit BGRA DIB, bottom-up, followed by an (empty) AND mask
      $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
      $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
      $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
      for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
      $maskRow = [int]([math]::Ceiling($s / 32.0) * 4); $bw.Write((New-Object byte[] ($maskRow * $s)))
      $bw.Flush(); ,$ms.ToArray()
    }
    $bmp.Dispose()
  }
  $fs = [IO.File]::Create($ico); $w = New-Object IO.BinaryWriter $fs
  $w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
  $offset = 6 + 16 * $sizes.Count
  for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
  }
  foreach ($img in $images) { $w.Write($img) }
  $w.Close()
  Write-Host "Generated icon"
}

# ---- compile ----
$fw  = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319"
$csc = Join-Path $fw 'csc.exe'
$exe = Join-Path $out 'Aetheris.exe'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /debug- `
  "/out:$exe" "/win32icon:$ico" `
  "/resource:$(Join-Path $src 'index.html'),index.html" "/resource:$ico,app.ico" `
  /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll `
  (Join-Path $src 'Aetheris.cs')
if ($LASTEXITCODE -ne 0) { throw "Compile failed" }

# Runtime config: workstation non-concurrent GC = fewer threads and less RAM
@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.5"/></startup>
  <runtime><gcConcurrent enabled="false"/><gcServer enabled="false"/></runtime>
</configuration>
'@ | Set-Content -Encoding UTF8 (Join-Path $out 'Aetheris.exe.config')

Write-Host "Built $exe ($([math]::Round((Get-Item $exe).Length / 1KB)) KB)"
