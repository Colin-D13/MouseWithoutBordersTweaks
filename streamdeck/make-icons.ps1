# Draws the Stream Deck key images in the same style as the rest of the deck (dark key, white line icon, label).
param([Parameter(Mandatory)][string]$OutDir)

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-Canvas([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.PixelOffsetMode = 'HighQuality'
    return $bmp, $g
}

function Add-RoundRect($path, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
}

# Monitor with a padlock on the screen, drawn in a 144 px key: scale/offset place it elsewhere.
function Draw-Glyph($g, [Drawing.Color]$color, [bool]$locked, [float]$scale, [float]$dx, [float]$dy) {
    $state = $g.Save()
    $g.TranslateTransform($dx, $dy)
    $g.ScaleTransform($scale, $scale)
    $pen = New-Object Drawing.Pen $color, 4.5
    $pen.LineJoin = 'Round'
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $brush = New-Object Drawing.SolidBrush $color

    $screen = New-Object Drawing.Drawing2D.GraphicsPath
    Add-RoundRect $screen 40 27 64 42 5
    $g.DrawPath($pen, $screen)
    $g.DrawLine($pen, 72, 69, 72, 79)
    $g.DrawLine($pen, 58, 79, 86, 79)

    # Padlock: filled body, shackle open (lifted and swung right) when unlocked.
    $body = New-Object Drawing.Drawing2D.GraphicsPath
    Add-RoundRect $body 62 47 20 15 2.5
    $g.FillPath($brush, $body)
    $shackle = New-Object Drawing.Pen $color, 3.5
    $shackle.StartCap = 'Round'
    $shackle.EndCap = 'Round'
    if ($locked) {
        $g.DrawArc($shackle, 65.5, 35, 13, 13, 180, 180)
        $g.DrawLine($shackle, 65.5, 41.5, 65.5, 47)
        $g.DrawLine($shackle, 78.5, 41.5, 78.5, 47)
    }
    else {
        $g.DrawArc($shackle, 72.5, 32, 13, 13, 180, 180)
        $g.DrawLine($shackle, 85.5, 38.5, 85.5, 44)
        $g.DrawLine($shackle, 72.5, 38.5, 72.5, 41)
    }

    $g.Restore($state)
}

# Two monitors with a two-way arrow under them; struck through when switching is off.
function Draw-SwitchGlyph($g, [Drawing.Color]$color, [bool]$enabled) {
    $pen = New-Object Drawing.Pen $color, 4
    $pen.LineJoin = 'Round'
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'

    foreach ($left in 36, 76) {
        $screen = New-Object Drawing.Drawing2D.GraphicsPath
        Add-RoundRect $screen $left 30 32 22 3.5
        $g.DrawPath($pen, $screen)
        $g.DrawLine($pen, $left + 16, 52, $left + 16, 58)
        $g.DrawLine($pen, $left + 9, 58, $left + 23, 58)
    }

    $g.DrawLine($pen, 50, 72, 94, 72)
    $g.DrawLine($pen, 50, 72, 56, 66)
    $g.DrawLine($pen, 50, 72, 56, 78)
    $g.DrawLine($pen, 94, 72, 88, 66)
    $g.DrawLine($pen, 94, 72, 88, 78)

    if (-not $enabled) {
        $gap = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(26, 26, 26)), 11
        $gap.StartCap = 'Round'
        $gap.EndCap = 'Round'
        $g.DrawLine($gap, 40, 82, 104, 24)
        $pen.Width = 4.5
        $g.DrawLine($pen, 40, 82, 104, 24)
    }
}

function Save-Key([string]$name, [string]$label, [string]$kind, [Drawing.Color]$color) {
    foreach ($size in 72, 144) {
        $bmp, $g = New-Canvas $size
        $s = $size / 144.0
        $g.ScaleTransform($s, $s)
        $key = New-Object Drawing.Drawing2D.GraphicsPath
        Add-RoundRect $key 0 0 144 144 25
        $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(58, 58, 58))), $key)
        $key = New-Object Drawing.Drawing2D.GraphicsPath
        Add-RoundRect $key 2 2 140 140 23
        $bg = New-Object Drawing.Drawing2D.LinearGradientBrush ([Drawing.PointF]::new(0, 0)), ([Drawing.PointF]::new(144, 144)), ([Drawing.Color]::FromArgb(42, 42, 42)), ([Drawing.Color]::FromArgb(8, 8, 8))
        $g.FillPath($bg, $key)
        switch ($kind) {
            'lock' { Draw-Glyph $g $color $true 1 0 0 }
            'unlocked' { Draw-Glyph $g $color $false 1 0 0 }
            'switch' { Draw-SwitchGlyph $g $color $true }
            'noswitch' { Draw-SwitchGlyph $g $color $false }
        }

        $font = New-Object Drawing.Font 'Segoe UI Semibold', 18.5, ([Drawing.FontStyle]::Regular), ([Drawing.GraphicsUnit]::Pixel)
        $format = New-Object Drawing.StringFormat
        $format.Alignment = 'Center'
        $g.DrawString($label, $font, (New-Object Drawing.SolidBrush $color), ([Drawing.RectangleF]::new(0, 94, 144, 30)), $format)

        $file = if ($size -eq 144) { "$name@2x.png" } else { "$name.png" }
        $bmp.Save((Join-Path $OutDir $file), [Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose()
        $bmp.Dispose()
    }
}

# Small icons for the action list and category: the glyph alone on a transparent background.
function Save-Glyph([string]$name, [int]$base) {
    foreach ($size in $base, ($base * 2)) {
        $bmp, $g = New-Canvas $size
        $s = $size / 76.0
        Draw-Glyph $g ([Drawing.Color]::White) $true $s (-34 * $s) (-15 * $s)
        $file = if ($size -eq $base * 2) { "$name@2x.png" } else { "$name.png" }
        $bmp.Save((Join-Path $OutDir $file), [Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose()
        $bmp.Dispose()
    }
}

Save-Key 'on' 'Game Lock' 'lock' ([Drawing.Color]::White)
Save-Key 'off' 'Lock Off' 'unlocked' ([Drawing.Color]::FromArgb(140, 140, 140))
Save-Key 'switch-on' 'PC Switch' 'switch' ([Drawing.Color]::White)
Save-Key 'switch-off' 'Switch Off' 'noswitch' ([Drawing.Color]::FromArgb(140, 140, 140))
Save-Key 'plugin' 'Game Lock' 'lock' ([Drawing.Color]::White)
Save-Glyph 'action' 20
Save-Glyph 'category' 28
