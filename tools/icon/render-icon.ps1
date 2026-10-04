# render-icon.ps1 - app icon concepts and the multi-size .ico (WPF vector rendering).
#
#   pwsh -NoProfile -STA -File tools\icon\render-icon.ps1 -Preview          # concept sheet
#   pwsh -NoProfile -STA -File tools\icon\render-icon.ps1 -Ico src\Shiyu.App\Assets\app.ico
#   pwsh -NoProfile -STA -File tools\icon\render-icon.ps1 -Xaml src\Shiyu.App\Assets\BrandMark.xaml
#
# The chosen concept is "stack" (2026-10-04); the other two stay for the preview sheet.
# -Xaml writes the same drawing as vector DrawingImages for the in-app marks:
# Brand.Mark (small master, for marks shown at 24 DIP or less) and
# Brand.Mark.Large (large master, with the glyph).
#
# Sizes follow what Windows actually asks for: tray 16/20/24/32 (100-200% scaling),
# taskbar 24/32/40/48, Explorer 64/128/256. Small sizes (<= 24) use their own,
# simplified master: a nine-stroke CJK glyph cannot survive 16 px.
# ASCII only: the glyph is built from its code point.

param(
    [switch]$Preview,
    [ValidateSet('bubble', 'stack', 'tile')][string]$Concept = 'stack',
    [string]$Ico = '',
    [string]$Xaml = '',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\docs\design\app-icon')
)

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$ErrorActionPreference = 'Stop'

$Glyph = [string][char]0x62FE   # the character "shi" (pick up)
function C([string]$hex) { [Windows.Media.ColorConverter]::ConvertFromString($hex) }
function Brush([string]$hex) { $b = [Windows.Media.SolidColorBrush]::new((C $hex)); $b.Freeze(); $b }
function Grad([string]$top, [string]$bottom) {
    $g = [Windows.Media.LinearGradientBrush]::new((C $top), (C $bottom), [Windows.Point]::new(0, 0), [Windows.Point]::new(0, 1))
    $g.Freeze(); $g
}

$Accent    = Grad '#3F8AF7' '#1652C9'     # brand blue (app accent #1A66DB), lit from the top
$AccentLow = Grad '#9DBDF8' '#6F9CEE'     # the card behind: lighter than the front, still visible on a light taskbar
$White     = Brush '#FFFFFF'
$Rim       = [Windows.Media.Pen]::new((Brush '#33FFFFFF'), 1)   # faint top light on big sizes

function GlyphGeometry([double]$emSize, [Windows.Point]$center) {
    $face = [Windows.Media.Typeface]::new([Windows.Media.FontFamily]::new('Microsoft YaHei UI'),
        [Windows.FontStyles]::Normal, [Windows.FontWeights]::Bold, [Windows.FontStretches]::Normal)
    $ft = [Windows.Media.FormattedText]::new($Glyph, [Globalization.CultureInfo]::GetCultureInfo('zh-CN'),
        [Windows.FlowDirection]::LeftToRight, $face, $emSize, $White, 1.0)
    $geo = $ft.BuildGeometry([Windows.Point]::new(0, 0))
    $b = $geo.Bounds
    $geo.Transform = [Windows.Media.TranslateTransform]::new($center.X - ($b.X + $b.Width / 2), $center.Y - ($b.Y + $b.Height / 2))
    $geo
}

function Bubble([double]$x, [double]$y, [double]$w, [double]$h, [double]$r, [double[]]$tail) {
    $body = [Windows.Media.RectangleGeometry]::new([Windows.Rect]::new($x, $y, $w, $h), $r, $r)
    $fig = [Windows.Media.PathFigure]::new(); $fig.StartPoint = [Windows.Point]::new($tail[0], $tail[1]); $fig.IsClosed = $true
    $fig.Segments.Add([Windows.Media.LineSegment]::new([Windows.Point]::new($tail[2], $tail[3]), $true))
    $fig.Segments.Add([Windows.Media.LineSegment]::new([Windows.Point]::new($tail[4], $tail[5]), $true))
    $tg = [Windows.Media.PathGeometry]::new(); $tg.Figures.Add($fig)
    [Windows.Media.CombinedGeometry]::new([Windows.Media.GeometryCombineMode]::Union, $body, $tg)
}

# Draws one concept into a 256-unit design space; $small selects the simplified master.
function Draw($dc, [string]$concept, [bool]$small) {
    switch ($concept) {
        'bubble' {
            if ($small) {
                $dc.DrawGeometry($Accent, $null, (Bubble 8 14 240 176 56 @(52, 180, 30, 246, 118, 184)))
                $dc.DrawRoundedRectangle($White, $null, [Windows.Rect]::new(56, 64, 144, 30), 15, 15)
                $dc.DrawRoundedRectangle($White, $null, [Windows.Rect]::new(56, 116, 96, 30), 15, 15)
            } else {
                $dc.DrawGeometry($Accent, $Rim, (Bubble 14 18 228 180 54 @(60, 186, 40, 242, 116, 192)))
                $dc.DrawGeometry($White, $null, (GlyphGeometry 132 ([Windows.Point]::new(128, 108))))
            }
        }
        'stack' {
            if ($small) {
                $dc.DrawRoundedRectangle($AccentLow, $null, [Windows.Rect]::new(72, 8, 176, 176), 44, 44)
                $dc.DrawRoundedRectangle($Accent, $null, [Windows.Rect]::new(8, 72, 176, 176), 44, 44)
                # two text lines, not one bar: a single bar reads as a "minus" button at 16 px
                $dc.DrawRoundedRectangle($White, $null, [Windows.Rect]::new(40, 118, 112, 26), 13, 13)
                $dc.DrawRoundedRectangle($White, $null, [Windows.Rect]::new(40, 168, 72, 26), 13, 13)
            } else {
                $dc.DrawRoundedRectangle($AccentLow, $null, [Windows.Rect]::new(64, 12, 180, 180), 46, 46)
                $dc.DrawRoundedRectangle($Accent, $Rim, [Windows.Rect]::new(12, 64, 180, 180), 46, 46)
                $dc.DrawGeometry($White, $null, (GlyphGeometry 122 ([Windows.Point]::new(102, 154))))
            }
        }
        'tile' {
            $dc.DrawRoundedRectangle($Accent, $(if ($small) { $null } else { $Rim }), [Windows.Rect]::new(10, 10, 236, 236), 60, 60)
            $dc.DrawGeometry($White, $null, (GlyphGeometry $(if ($small) { 208 } else { 158 }) ([Windows.Point]::new(128, 128))))
        }
    }
}

function Render([string]$concept, [int]$size) {
    $dv = [Windows.Media.DrawingVisual]::new()
    $dc = $dv.RenderOpen()
    $dc.PushTransform([Windows.Media.ScaleTransform]::new($size / 256.0, $size / 256.0))
    Draw $dc $concept ($size -le 24)
    $dc.Pop(); $dc.Close()
    $rtb = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv); $rtb.Freeze(); $rtb
}

function SavePng($bitmap, [string]$path) {
    $enc = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $fs = [IO.File]::Create($path); try { $enc.Save($fs) } finally { $fs.Dispose() }
}

function PngBytes($bitmap) {
    $enc = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = [IO.MemoryStream]::new(); $enc.Save($ms); $ms.ToArray()
}

$IcoSizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

if ($Preview) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $concepts = 'bubble', 'stack', 'tile'
    $show = 16, 20, 24, 32, 40, 48, 64, 256
    $rowH = 300; $sheetW = 1500
    $dv = [Windows.Media.DrawingVisual]::new(); $dc = $dv.RenderOpen()
    $dc.DrawRectangle((Brush '#FFFFFF'), $null, [Windows.Rect]::new(0, 0, $sheetW, $rowH * 3 + 40))
    $label = [Windows.Media.Typeface]::new('Segoe UI')
    $row = 0
    foreach ($c in $concepts) {
        $y0 = 20 + $row * $rowH
        $dc.DrawText([Windows.Media.FormattedText]::new($c, [Globalization.CultureInfo]::InvariantCulture, 'LeftToRight', $label, 18, (Brush '#444444'), 1.0), [Windows.Point]::new(20, $y0))
        # a light and a dark "taskbar" strip with the icon at real size, plus a 4x zoom of 16/24
        foreach ($band in @(@{ bg = '#F3F3F3'; y = $y0 + 30 }, @{ bg = '#202020'; y = $y0 + 160 })) {
            $dc.DrawRectangle((Brush $band.bg), $null, [Windows.Rect]::new(20, $band.y, $sheetW - 40, 120))
            $x = 40
            foreach ($s in $show) {
                $img = Render $c $s
                $d = [Math]::Min($s, 96)
                $dc.DrawImage($img, [Windows.Rect]::new($x, $band.y + (120 - $d) / 2, $d, $d)); $x += $d + 24
            }
            foreach ($s in 16, 24) {
                # pixel-exact zoom: the scaling mode has to sit on a drawing group, not the visual
                $zoom = [Windows.Media.DrawingGroup]::new()
                [Windows.Media.RenderOptions]::SetBitmapScalingMode($zoom, [Windows.Media.BitmapScalingMode]::NearestNeighbor)
                $zoom.Children.Add([Windows.Media.ImageDrawing]::new((Render $c $s), [Windows.Rect]::new($x, $band.y + 12, 96, 96)))
                $dc.DrawDrawing($zoom); $x += 120
            }
        }
        $row++
    }
    $dc.Close()
    $sheet = [Windows.Media.Imaging.RenderTargetBitmap]::new($sheetW, $rowH * 3 + 40, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $sheet.Render($dv)
    $path = Join-Path $OutDir 'concepts.png'; SavePng $sheet $path
    foreach ($c in $concepts) { SavePng (Render $c 256) (Join-Path $OutDir "$c-256.png") }
    "preview: $([IO.Path]::GetFullPath($path))"
}

if ($Xaml -ne '') {
    # The in-app marks as vector DrawingImages: crisp at any DPI, one source of truth
    # with the .ico. A transparent 256 box keeps the icon's padding when stretched.
    function Mark([bool]$small) {
        $group = [Windows.Media.DrawingGroup]::new()
        $dc = $group.Open()
        $dc.DrawRectangle([Windows.Media.Brushes]::Transparent, $null, [Windows.Rect]::new(0, 0, 256, 256))
        Draw $dc $Concept $small
        $dc.Close()
        $image = [Windows.Media.DrawingImage]::new($group); $image.Freeze(); $image
    }
    $dict = [Windows.ResourceDictionary]::new()
    $dict.Add('Brand.Mark', (Mark $true))
    $dict.Add('Brand.Mark.Large', (Mark $false))
    $header = "<!-- Generated by tools/icon/render-icon.ps1 -Concept $Concept -Xaml; do not edit by hand. -->"
    $settings = [Xml.XmlWriterSettings]::new(); $settings.Indent = $true; $settings.OmitXmlDeclaration = $true
    $sb = [Text.StringBuilder]::new(); $writer = [Xml.XmlWriter]::Create($sb, $settings)
    [Windows.Markup.XamlWriter]::Save($dict, $writer); $writer.Dispose()
    [IO.File]::WriteAllText($Xaml, $header + [Environment]::NewLine + $sb.ToString() + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    "xaml: $Xaml ($((Get-Item $Xaml).Length) bytes)"
}

if ($Ico -ne '') {
    # ICO with PNG-compressed frames (Vista+): header, directory, then each frame's PNG.
    $frames = foreach ($s in $IcoSizes) { , @($s, (PngBytes (Render $Concept $s))) }
    $ms = [IO.MemoryStream]::new(); $w = [IO.BinaryWriter]::new($ms)
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $s = $f[0]; $data = $f[1]
        $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
        $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$data.Length); $w.Write([uint32]$offset); $offset += $data.Length
    }
    foreach ($f in $frames) { $w.Write([byte[]]$f[1]) }
    $w.Flush(); [IO.File]::WriteAllBytes($Ico, $ms.ToArray())
    "ico: $Ico ($($frames.Count) frames, $($ms.Length) bytes)"
}
