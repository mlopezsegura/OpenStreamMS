param([string]$Out = "$PSScriptRoot\app.ico")

Add-Type -Assembly System.Drawing

function New-IcoBytes([int[]]$Sizes) {
    $images = foreach ($s in $Sizes) {
        $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g   = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

        $g.Clear([System.Drawing.Color]::Transparent)

        # Purple circle background
        $bg = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(124,58,237))
        $g.FillEllipse($bg, 0, 0, $s-1, $s-1)

        # Camera body (white rectangle)
        $pw  = [float][Math]::Max(1, $s / 16.0)
        $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $pw)
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $bx = [int]($s * 0.10); $bw = [int]($s * 0.48)
        $bh = [int]($s * 0.34); $by = [int](($s - $bh) / 2.0)
        $g.DrawRectangle($pen, $bx, $by, $bw, $bh)

        # Play triangle (lens / viewfinder)
        $tx = $bx + $bw + [int]($s * 0.04)
        $th = [float]($bh * 0.80); $ty = [float](($s - $th) / 2.0)
        $pts = [System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new($tx,                        $ty),
            [System.Drawing.PointF]::new($tx + [float]($s * 0.19),  $ty + $th / 2.0),
            [System.Drawing.PointF]::new($tx,                        $ty + $th)
        )
        $wb = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $g.FillPolygon($wb, $pts)

        $g.Dispose(); $bg.Dispose(); $pen.Dispose(); $wb.Dispose()

        $ms = [System.IO.MemoryStream]::new()
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()   # comma forces array-of-arrays, not flat merge
    }

    # Build ICO binary (ICONDIR + ICONDIRENTRYs + PNG data)
    $count    = $Sizes.Count
    $hdrBytes = 6 + 16 * $count
    $offsets  = [int[]]::new($count)
    $off      = $hdrBytes
    for ($i = 0; $i -lt $count; $i++) { $offsets[$i] = $off; $off += $images[$i].Length }

    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)

    # ICONDIR
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$count)

    # ICONDIRENTRYs
    for ($i = 0; $i -lt $count; $i++) {
        $sz = $Sizes[$i]
        $bw.Write([byte]$(if ($sz -ge 256) { 0 } else { $sz }))
        $bw.Write([byte]$(if ($sz -ge 256) { 0 } else { $sz }))
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$images[$i].Length)
        $bw.Write([uint32]$offsets[$i])
    }

    # PNG payloads
    foreach ($img in $images) { $bw.Write($img) }
    $bw.Flush()
    $ms.ToArray()
}

$bytes = New-IcoBytes -Sizes @(16, 24, 32, 48, 256)
[System.IO.File]::WriteAllBytes($Out, $bytes)
Write-Host "[IconGen] app.ico generado en $Out ($($bytes.Length) bytes)"
