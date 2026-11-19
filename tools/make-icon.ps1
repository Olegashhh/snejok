# Генерация Resources/app.ico из векторного описания снежинки.
# Оригинальная графика: рисуется кодом, сторонних ассетов нет.
# Запуск: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Фон — скруглённый квадрат с зимним градиентом (только для крупных размеров).
    if ($s -ge 24) {
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $r = [float]($s * 0.22)
        $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
        $d = $r * 2
        $path.AddArc(0, 0, $d, $d, 180, 90)
        $path.AddArc($s - $d, 0, $d, $d, 270, 90)
        $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90)
        $path.AddArc(0, $s - $d, $d, $d, 90, 90)
        $path.CloseFigure()
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect,
            [System.Drawing.Color]::FromArgb(255, 24, 48, 86),
            [System.Drawing.Color]::FromArgb(255, 9, 18, 34), 90.0)
        $g.FillPath($brush, $path)
        $brush.Dispose(); $path.Dispose()
    }

    # Снежинка: 6 лучей с ответвлениями.
    $cx = $s / 2.0
    $cy = $s / 2.0
    $radius = $s * 0.36
    $penWidth = [Math]::Max(1.0, $s * 0.055)
    $color = if ($s -ge 24) { [System.Drawing.Color]::FromArgb(255, 226, 244, 255) } else { [System.Drawing.Color]::White }
    $pen = New-Object System.Drawing.Pen($color, $penWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $round = New-Object System.Drawing.SolidBrush($color)

    for ($i = 0; $i -lt 6; $i++) {
        $a = [Math]::PI * $i / 3.0
        $x2 = $cx + [Math]::Cos($a) * $radius
        $y2 = $cy + [Math]::Sin($a) * $radius
        $g.DrawLine($pen, [float]$cx, [float]$cy, [float]$x2, [float]$y2)

        # Ответвления на 2/3 длины луча.
        if ($s -ge 24) {
            $bx = $cx + [Math]::Cos($a) * $radius * 0.62
            $by = $cy + [Math]::Sin($a) * $radius * 0.62
            $bl = $radius * 0.26
            foreach ($off in @(-0.7, 0.7)) {
                $ba = $a + $off
                $ex = $bx + [Math]::Cos($ba) * $bl
                $ey = $by + [Math]::Sin($ba) * $bl
                $g.DrawLine($pen, [float]$bx, [float]$by, [float]$ex, [float]$ey)
            }
        }
    }

    $c = [float]($radius * 0.13)
    $g.FillEllipse($round, [float]($cx - $c), [float]($cy - $c), [float]($c * 2), [float]($c * 2))

    $pen.Dispose(); $round.Dispose(); $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @($s, $ms.ToArray())
    $bmp.Dispose(); $ms.Dispose()
}

# Сборка ICO (PNG-кадры).
$outPath = Join-Path $PSScriptRoot '..\src\WinterDesktop.App\Resources\app.ico'
New-Item -ItemType Directory -Force -Path (Split-Path $outPath) | Out-Null
$fs = [System.IO.File]::Create($outPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = [int]$p[0]; $data = $p[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
    $bw.Write([Byte]0); $bw.Write([Byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
$bw.Flush(); $bw.Dispose()
Write-Host "ICO создан: $outPath ($((Get-Item $outPath).Length) bytes)"
