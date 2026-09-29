param([string]$Out = "$PSScriptRoot\app.ico")

Add-Type -Assembly System.Drawing

function New-IcoBytes([int[]]$Sizes) {
    $images = foreach ($s in $Sizes) {
        $bmp = [System.Drawing.Bitmap]::new($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g   = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

        $g.Clear([System.Drawing.Color]::Transparent)

        # Cuadrado redondeado morado + triangulo play blanco. Misma geometria que
        # Core/Helpers/AppIcon.cs y wwwroot/favicon.svg (lienzo 32, radio 7).
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $k = $s / 32.0
        $d = [float](14 * $k)
        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $path.AddArc(0, 0, $d, $d, 180, 90)
        $path.AddArc([float]($s - $d), 0, $d, $d, 270, 90)
        $path.AddArc([float]($s - $d), [float]($s - $d), $d, $d, 0, 90)
        $path.AddArc(0, [float]($s - $d), $d, $d, 90, 90)
        $path.CloseFigure()
        $bg = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(124,58,237))
        $g.FillPath($bg, $path)

        $pts = [System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new([float](12 * $k), [float]( 9 * $k)),
            [System.Drawing.PointF]::new([float](23 * $k), [float](16 * $k)),
            [System.Drawing.PointF]::new([float](12 * $k), [float](23 * $k))
        )
        $wb = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $g.FillPolygon($wb, $pts)

        $g.Dispose(); $bg.Dispose(); $path.Dispose(); $wb.Dispose()

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
