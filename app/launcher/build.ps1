# Builds "Elite HUD Studio.exe" with the C# compiler that ships with Windows (.NET Framework 4.x). Nothing to install.
# Run:  powershell -ExecutionPolicy Bypass -File app\launcher\build.ps1
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent (Split-Path -Parent $here)
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }
$wv2 = Join-Path (Split-Path -Parent $here) 'webview2'
$ico = Join-Path $here 'studio.ico'

# app icon (HUD Studio crosshair) as a multi-size .ico with PNG images
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
  $bmp = New-Object Drawing.Bitmap $s, $s
  $g = [Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
  $k = $s / 64.0
  $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 11, 15, 23))), 0, 0, $s - 1, $s - 1)
  $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255, 255, 113, 0)), ([Math]::Max(1.5, 5 * $k))
  $g.DrawEllipse($pen, 14 * $k, 14 * $k, 36 * $k, 36 * $k)
  foreach ($l in @(@(32, 4, 32, 18), @(32, 46, 32, 60), @(4, 32, 18, 32), @(46, 32, 60, 32))) { $g.DrawLine($pen, $l[0] * $k, $l[1] * $k, $l[2] * $k, $l[3] * $k) }
  $g.Dispose()
  $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  , $ms.ToArray()
}
$fs = [IO.File]::Create($ico); $bw = New-Object IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $b = [byte]$(if ($s -ge 256) { 0 } else { $s })
  $bw.Write($b); $bw.Write($b); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset); $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()

$out = Join-Path $root 'Elite HUD Studio.exe'
& $csc /nologo /target:winexe /optimize+ /platform:x64 "/out:$out" "/win32icon:$ico" "/win32manifest:$(Join-Path $here 'app.manifest')" `
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Xml.dll /reference:Microsoft.VisualBasic.dll `
  "/reference:$(Join-Path $wv2 'Microsoft.Web.WebView2.Core.dll')" "/reference:$(Join-Path $wv2 'Microsoft.Web.WebView2.WinForms.dll')" `
  (Join-Path $here 'StudioApp.cs') (Join-Path $here 'Helper.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed (compiler exit code $LASTEXITCODE)" }
"Built: $out ($([Math]::Round((Get-Item $out).Length / 1KB)) KB)"
