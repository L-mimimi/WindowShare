# ============================================================
# 生成应用图标 assets/windowshare.ico（多尺寸 PNG 打包 ICO）
#
# 用法：powershell -ExecutionPolicy Bypass -File scripts\make-icon.ps1
# 设计：蓝色渐变圆角底 + 白色屏幕框 + 底部无线波纹（屏幕向下发射 = 共享）
# ============================================================
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size)
{
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $k = $size / 256.0   # 全部按 256 设计稿坐标绘制，k 为缩放系数

    function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r)
    {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $r * 2
        $p.AddArc($x, $y, $d, $d, 180, 90)
        $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
        $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure()
        return $p
    }

    # 背景：垂直渐变圆角方块
    $bgPath = New-RoundRect (8 * $k) (8 * $k) (240 * $k) (240 * $k) (52 * $k)
    $bgRect = New-Object System.Drawing.RectangleF(0, 0, (256 * $k), (256 * $k))
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bgRect,
        [System.Drawing.Color]::FromArgb(0x42, 0xA5, 0xF5),
        [System.Drawing.Color]::FromArgb(0x15, 0x65, 0xC0),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillPath($brush, $bgPath)

    $white = [System.Drawing.Brushes]::White

    # 屏幕：白色圆角描边（无填充）
    $screen = New-RoundRect (62 * $k) (52 * $k) (132 * $k) (92 * $k) (10 * $k)
    $pen = New-Object System.Drawing.Pen($white, (13 * $k))
    $g.DrawPath($pen, $screen)

    # 底座：屏幕下方短横线
    $basePen = New-Object System.Drawing.Pen($white, (11 * $k))
    $basePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $basePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($basePen, (102 * $k), (160 * $k), (154 * $k), (160 * $k))

    # 无线波纹：圆点 + 两道向下的弧（信号从屏幕发出）
    $cx = 128 * $k; $cy = 196 * $k
    $g.FillEllipse($white, ($cx - 9 * $k), ($cy - 9 * $k), (18 * $k), (18 * $k))
    $arcPen = New-Object System.Drawing.Pen($white, (10 * $k))
    $arcPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arcPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($r in 30, 50)
    {
        $d = $r * 2 * $k
        $g.DrawArc($arcPen, ($cx - $r * $k), ($cy - $r * $k), $d, $d, 45, 90)
    }

    $g.Dispose()

    # 小尺寸下细节糊成一团时缩得更狠：16px 仅保留底 + 波点
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$entries = @(foreach ($s in 256, 64, 48, 32, 16) {
    @{ Size = $s; Data = New-IconPng $s }
})

function Pack-Ico([string]$outPath, [object[]]$list)
{
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$list.Count)
    $offset = 6 + 16 * $list.Count
    foreach ($e in $list)
    {
        $dim = $(if ($e.Size -ge 256) { 0 } else { $e.Size })
        $bw.Write([byte]$dim); $bw.Write([byte]$dim)
        $bw.Write([byte]0); $bw.Write([byte]0)          # 调色板/保留
        $bw.Write([uint16]1); $bw.Write([uint16]32)     # 类型=PNG 色, 位深
        $bw.Write([uint32]$e.Data.Length)
        $bw.Write([uint32]$offset)
        $offset += $e.Data.Length
    }
    foreach ($e in $list) { $bw.Write($e.Data) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
}

$iconDir = Join-Path $root "assets"
New-Item -ItemType Directory -Force -Path $iconDir | Out-Null
$icoPath = Join-Path $iconDir "windowshare.ico"
Pack-Ico $icoPath $entries
# 顺手输出 256px 预览图，便于人工确认
[System.IO.File]::WriteAllBytes((Join-Path $iconDir "icon-preview-256.png"), $entries[0].Data)
Write-Host "已生成 $icoPath ($((Get-Item $icoPath).Length) bytes)"
