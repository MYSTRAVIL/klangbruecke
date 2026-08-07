<#
.SYNOPSIS
    Renders the Klangbruecke icon set (app + tray states) from the brand motif.

.DESCRIPTION
    Draws the "Klangbrücke" mark - a rainbow-arc band with two piers beneath its ends, which
    reads as both a bridge and a pair of headphones - parametrically with GDI+, so every size is
    rendered crisp at its own resolution rather than downscaled from one master. The geometry and
    the two brand colours were reverse-engineered from packaging/Images/Square150x150Logo.png (arc
    outer radius 0.353 of the canvas, band thickness 0.073, piers at ±0.317 from centre; foreground
    #7AC4FF on background #202A3A).

    Produces four .ico files in src/Klangbruecke/Assets:

      app.ico          the brand tile (navy rounded square + blue mark) - the .exe's Win32 icon,
                       shown in the taskbar, Alt+Tab and Explorer. Static.
      tray-active.ico  blue mark on transparent  - Connected
      tray-busy.ico    amber mark on transparent - Connecting / Discovering / Degraded / RetryBackoff
      tray-idle.ico    grey mark on transparent  - Idle / Suppressed

    The tray glyphs are transparent so they read on any taskbar colour; the mapping from
    ConnectionState to which of the three is shown lives in src/Klangbruecke/TrayIconPolicy.cs.

    Frames are stored as 32bpp BMP/DIB, NOT PNG. NotifyIcon.Icon is a System.Drawing.Icon, whose
    loader decodes only BMP-encoded frames - a PNG-compressed .ico throws "must be a picture that
    can be used as a Icon" the moment the app tries to load it. DIB frames cost more bytes and are
    read by everything (the app, the shell, Explorer).

    Build-time generator, checked in so the icons are reproducible. Re-run after any change here; it
    overwrites the four files in place. Runs under pwsh 7 or Windows PowerShell 5.1.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path $PSScriptRoot -Parent
$outDir   = Join-Path $repoRoot 'src\Klangbruecke\Assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# --- brand palette (sampled from Square150x150Logo.png) ---
$navy  = [System.Drawing.Color]::FromArgb(255, 32, 42, 58)    # #202A3A tile background
$blue  = [System.Drawing.Color]::FromArgb(255, 122, 196, 255) # #7AC4FF  Active
$amber = [System.Drawing.Color]::FromArgb(255, 242, 184, 75)  # #F2B84B  Busy
$grey  = [System.Drawing.Color]::FromArgb(255, 124, 135, 151) # #7C8797  Idle

# --- motif geometry, as fractions of the canvas edge (shifted up 0.05 from the source so the
#     transparent glyph sits centred rather than low, with no background box to anchor it) ---
$cx      = 0.5     # arc centre x
$cy      = 0.57    # arc centre y
$rOuter  = 0.353
$rInner  = 0.28
$rMid    = ($rOuter + $rInner) / 2.0   # 0.3165 - the radius the pen stroke is centred on
$thick   = $rOuter - $rInner           # 0.073 - band thickness (also the pier width)
$pierOff = 0.317   # pier centre offset from cx, aligned under the arc legs
$pierTop = 0.655
$pierBot = 0.795

function New-RoundedRectPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2.0
    $path.AddArc($x,           $y,           $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y,           $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $path.AddArc($x,           $y + $h - $d, $d, $d,  90, 90)
    $path.CloseFigure()
    return $path
}

# Renders one frame (size S) as a 32bpp ARGB Bitmap. $tile draws the navy brand tile behind the mark.
function New-Frame([int]$S, [System.Drawing.Color]$fg, [bool]$tile) {
    $bmp = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        if ($tile) {
            $corner = [single]($S * 0.14)
            $tilePath = New-RoundedRectPath 0 0 ([single]$S) ([single]$S) $corner
            $navyBrush = New-Object System.Drawing.SolidBrush $navy
            try { $g.FillPath($navyBrush, $tilePath) } finally { $navyBrush.Dispose(); $tilePath.Dispose() }
        }

        # Stroke width floors at 2px so the 16px tray glyph does not thin to nothing.
        $penW = [single][Math]::Max($thick * $S, 2.0)

        # Arc: top semicircle. GDI+ angles run clockwise with y-down, so the crown is 270deg and a
        # 180deg sweep from 180deg (west) passes through the top to 360deg (east). Flat caps give the
        # horizontal cut ends the source art has at the diameter.
        $rectX = [single](($cx - $rMid) * $S)
        $rectY = [single](($cy - $rMid) * $S)
        $rectS = [single]($rMid * 2.0 * $S)
        $pen = New-Object System.Drawing.Pen $fg, $penW
        try {
            $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
            $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Flat
            $g.DrawArc($pen, $rectX, $rectY, $rectS, $rectS, 180, 180)
        } finally { $pen.Dispose() }

        # Two piers, corners softened a touch to match the mark's rounded feel.
        $pw = [single][Math]::Max($thick * $S, 2.0)
        $py = [single]($pierTop * $S)
        $ph = [single](($pierBot - $pierTop) * $S)
        $pr = [single]([Math]::Min($pw * 0.35, $ph * 0.5))
        $brush = New-Object System.Drawing.SolidBrush $fg
        try {
            foreach ($sign in -1, 1) {
                $pxCenter = ($cx + $sign * $pierOff) * $S
                $px = [single]($pxCenter - $pw / 2.0)
                $pierPath = New-RoundedRectPath $px $py $pw $ph $pr
                try { $g.FillPath($brush, $pierPath) } finally { $pierPath.Dispose() }
            }
        } finally { $brush.Dispose() }

        return $bmp
    } finally {
        $g.Dispose()
    }
}

# Converts a 32bpp ARGB Bitmap into the DIB payload of one icon frame: a BITMAPINFOHEADER with
# double height (colour bitmap + AND mask), then the BGRA colour rows bottom-up, then a 1bpp AND
# mask. Modern loaders key transparency off the alpha channel, but the mask must still be present,
# so it is set from fully-transparent pixels.
function ConvertTo-IconDib([System.Drawing.Bitmap]$bmp) {
    $W = $bmp.Width; $H = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $W, $H
    $locked = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $locked.Stride
        $pixels = New-Object byte[] ($stride * $H)
        [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    } finally {
        $bmp.UnlockBits($locked)
    }

    $maskStride = [int](([Math]::Floor(($W + 31) / 32)) * 4)   # 1bpp rows padded to 32 bits
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    try {
        # BITMAPINFOHEADER
        $bw.Write([uint32]40)
        $bw.Write([int32]$W)
        $bw.Write([int32]($H * 2))
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]0)                       # BI_RGB
        $bw.Write([uint32]($W * $H * 4 + $maskStride * $H))
        $bw.Write([int32]0); $bw.Write([int32]0)   # resolution
        $bw.Write([uint32]0); $bw.Write([uint32]0) # palette

        # XOR colour bitmap, bottom-up. Format32bppArgb memory order is already B,G,R,A.
        for ($y = $H - 1; $y -ge 0; $y--) {
            $bw.Write($pixels, $y * $stride, $W * 4)
        }

        # AND mask, bottom-up: bit = 1 (transparent) where alpha is zero.
        $maskRow = New-Object byte[] $maskStride
        for ($y = $H - 1; $y -ge 0; $y--) {
            [Array]::Clear($maskRow, 0, $maskRow.Length)
            for ($x = 0; $x -lt $W; $x++) {
                $a = $pixels[$y * $stride + $x * 4 + 3]
                # -shr/-band, not [int]($x/8): [int] rounds (0.875 -> 1), which overruns the mask row.
                if ($a -eq 0) {
                    $byteIndex = $x -shr 3
                    $maskRow[$byteIndex] = $maskRow[$byteIndex] -bor (0x80 -shr ($x -band 7))
                }
            }
            $bw.Write($maskRow, 0, $maskRow.Length)
        }

        $bw.Flush()
        # Leading comma: without it the pipeline unrolls the byte[] into loose bytes and the caller
        # gets an object[], which BinaryWriter.Write cannot serialise (the frames vanish, leaving a
        # header-only .ico).
        return , $ms.ToArray()
    } finally {
        $bw.Dispose(); $ms.Dispose()
    }
}

# Assembles DIB frames into a single .ico. $frames is size -> DIB bytes.
function Write-Ico([hashtable]$frames, [string]$path) {
    $sizes = $frames.Keys | Sort-Object
    $count = $sizes.Count

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    try {
        # ICONDIR
        $bw.Write([uint16]0)      # reserved
        $bw.Write([uint16]1)      # type = icon
        $bw.Write([uint16]$count)

        # ICONDIRENTRY table; image data follows the whole table.
        $offset = 6 + 16 * $count
        foreach ($s in $sizes) {
            $data = $frames[$s]
            $dim  = if ($s -ge 256) { 0 } else { $s }   # 0 encodes 256
            $bw.Write([byte]$dim)   # width
            $bw.Write([byte]$dim)   # height
            $bw.Write([byte]0)      # colour count (0 = truecolour)
            $bw.Write([byte]0)      # reserved
            $bw.Write([uint16]1)    # colour planes
            $bw.Write([uint16]32)   # bits per pixel
            $bw.Write([uint32]$data.Length)
            $bw.Write([uint32]$offset)
            $offset += $data.Length
        }
        foreach ($s in $sizes) { $bw.Write($frames[$s]) }

        $bw.Flush()
        [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    } finally {
        $bw.Dispose(); $ms.Dispose()
    }
    Write-Host "  wrote $path ($count sizes: $($sizes -join ', '))"
}

function Build-Ico([string]$name, [System.Drawing.Color]$fg, [bool]$tile, [int[]]$sizes) {
    $frames = @{}
    foreach ($s in $sizes) {
        $bmp = New-Frame $s $fg $tile
        try { $frames[$s] = ConvertTo-IconDib $bmp } finally { $bmp.Dispose() }
    }
    Write-Ico $frames (Join-Path $outDir $name)
}

Write-Host "Rendering icons into $outDir"

# App tile carries a 256 for Explorer's large view; tray sizes stop at 48 (the largest the shell
# asks for at 200% DPI).
Build-Ico 'app.ico'         $blue  $true  @(16, 24, 32, 48, 256)
Build-Ico 'tray-active.ico' $blue  $false @(16, 20, 24, 32, 48)
Build-Ico 'tray-busy.ico'   $amber $false @(16, 20, 24, 32, 48)
Build-Ico 'tray-idle.ico'   $grey  $false @(16, 20, 24, 32, 48)

Write-Host 'Done.'
