<#
.SYNOPSIS
    Render two deployed maps' stratum layers side by side, as configured.

.DESCRIPTION
    Draws each layer pair at the same world scale with each side's own
    diffuseRemap applied, so what lands in the image is what the shader is
    told to draw. This is the tool that settled the texture-substitution
    audit: tone metrics said every pair matched, and the pictures showed a
    mossy lichen rock and confetti gravel that plainly did not. Mean colour
    is necessary; character - contrast, feature size - is what the eye
    actually checks.

.EXAMPLE
    .\Compare-MapTextures.ps1 -MapA ~SC-Canis_River -MapB ~SC-Canis_CC0
#>
[CmdletBinding()]
param(
    [string]$MapA = '~SC-Canis_River',
    [string]$MapB = '~SC-Canis_CC0',
    [int]$Layers = 6,
    [string]$Out = "$env:TEMP\map-tex-compare.png"
)
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')
Add-Type -AssemblyName System.Drawing

$M = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Maps'
$fa  = Get-Content (Join-Path $M "$MapA\$MapA.sanmap") -Raw | ConvertFrom-Json
$cc  = Get-Content (Join-Path $M "$MapB\$MapB.sanmap") -Raw | ConvertFrom-Json

function Load-Tex([string]$mapDir, [string]$path) {
    $f = Join-Path $mapDir ($path -replace '^map/', '')
    if (-not (Test-Path $f)) { return $null }
    $b = [IO.File]::ReadAllBytes($f)
    $w = 0; $h = 0
    $px = [MapGen]::DecodeDdsToBgra($b, [ref]$w, [ref]$h)
    if ($null -eq $px) { return $null }
    @{ Px = $px; W = $w; H = $h }
}

# One swatch: `metres` of world drawn into `size` pixels, remap applied, a
# fixed display gain so the remap products are visible. Same gain both sides.
function Draw-Swatch($g, $tex, [double]$tile, $remap, [int]$ox, [int]$oy, [int]$size, [double]$metres) {
    $gain = 2.6
    $bmp2 = New-Object Drawing.Bitmap $size, $size
    for ($y = 0; $y -lt $size; $y++) {
        for ($x = 0; $x -lt $size; $x++) {
            $u = ($x / [double]$size) * $metres / $tile
            $v = ($y / [double]$size) * $metres / $tile
            $tx = [int](($u - [math]::Floor($u)) * $tex.W) % $tex.W
            $ty = [int](($v - [math]::Floor($v)) * $tex.H) % $tex.H
            $o = ($ty * $tex.W + $tx) * 4
            $bb = [math]::Min(255, $tex.Px[$o]   * $remap[2] * $gain)
            $gg = [math]::Min(255, $tex.Px[$o+1] * $remap[1] * $gain)
            $rr = [math]::Min(255, $tex.Px[$o+2] * $remap[0] * $gain)
            $bmp2.SetPixel($x, $y, [Drawing.Color]::FromArgb([int]$rr, [int]$gg, [int]$bb))
        }
    }
    $g.DrawImage($bmp2, $ox, $oy)
    $bmp2.Dispose()
}

$rows = @()
for ($i = 0; $i -lt $Layers; $i++) {
    $rows += @{ Fa = $fa.stratumLayers[$i]; Cc = $cc.stratumLayers[$i]; Idx = $i }
}

$sw = 190; $pad = 10; $rowH = $sw + 44
$bmp = New-Object Drawing.Bitmap (2 * $sw + 3 * $pad + 240), ($rows.Count * $rowH + 40)
$g = [Drawing.Graphics]::FromImage($bmp)
$g.Clear([Drawing.Color]::FromArgb(16, 20, 24))
$font = New-Object Drawing.Font('Consolas', 10)
$fontB = New-Object Drawing.Font('Consolas', 11, [Drawing.FontStyle]::Bold)
$white = [Drawing.Brushes]::White; $grey = [Drawing.Brushes]::Silver
$g.DrawString($MapA, $fontB, $white, $pad + 40, 8)
$g.DrawString($MapB, $fontB, $white, $sw + 2 * $pad + 30, 8)
$g.DrawString('8 m x 8 m of ground each', $font, $grey, 2 * $sw + 3 * $pad + 4, 8)

$y0 = 36
foreach ($r in $rows) {
    $faT = Load-Tex (Join-Path $M $MapA) $r.Fa.albedo.path
    $ccT = Load-Tex (Join-Path $M $MapB) $r.Cc.albedo.path
    $faRemap = @([double]$r.Fa.diffuseRemap.r, [double]$r.Fa.diffuseRemap.g, [double]$r.Fa.diffuseRemap.b)
    $ccRemap = @([double]$r.Cc.diffuseRemap.r, [double]$r.Cc.diffuseRemap.g, [double]$r.Cc.diffuseRemap.b)
    if ($faT) { Draw-Swatch $g $faT ([double]$r.Fa.tileSize.x) $faRemap $pad $y0 $sw 8.0 }
    if ($ccT) { Draw-Swatch $g $ccT ([double]$r.Cc.tileSize.x) $ccRemap ($sw + 2 * $pad) $y0 $sw 8.0 }
    $tx = 2 * $sw + 3 * $pad
    $g.DrawString(('L{0}' -f $r.Idx), $fontB, $white, $tx, $y0)
    $g.DrawString((Split-Path -Leaf $r.Fa.albedo.path), $font, $grey, $tx, $y0 + 20)
    $g.DrawString(('  tile {0}m  remap {1:n2}/{2:n2}/{3:n2}' -f $r.Fa.tileSize.x, $faRemap[0], $faRemap[1], $faRemap[2]), $font, $grey, $tx, $y0 + 36)
    $g.DrawString((Split-Path -Leaf $r.Cc.albedo.path), $font, $grey, $tx, $y0 + 58)
    $g.DrawString(('  tile {0}m  remap {1:n2}/{2:n2}/{3:n2}' -f $r.Cc.tileSize.x, $ccRemap[0], $ccRemap[1], $ccRemap[2]), $font, $grey, $tx, $y0 + 74)
    $y0 += $rowH
}
$g.Dispose()
$out = $Out
$bmp.Save($out, [Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"wrote $out"
