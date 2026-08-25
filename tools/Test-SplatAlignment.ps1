<#
.SYNOPSIS
    Is the splat texture registered to the terrain it is painted on?

.DESCRIPTION
    "The rock is not centred on the rock" is a registration claim, and it is
    testable: the rock layer is a function of slope, so the weight image and the
    slope field should correlate best at zero offset. If some other offset - or
    a flip - scores better, the splat is being written in a different
    orientation from the heightmap.

    Worth checking explicitly because the two files use different conventions.
    heightmap.raw is read back through Load.ReadRaw with flipVertically set, so
    its row 0 is world z max. A TGA whose descriptor byte has bit 5 clear is
    bottom-up, so its row 0 is the BOTTOM of the image. Nothing in our writer
    accounts for that difference.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MapDir,
    [int]$Layer = 7,          # rock: the layer most tightly tied to slope
    [int]$MaxShift = 6
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')

$f = Get-ChildItem $MapDir -Filter *.sanmap | Select-Object -First 1
$j = Get-Content $f.FullName -Raw | ConvertFrom-Json
$tex = Join-Path $MapDir 'Textures'
$water = if ($j.hasWater) { [float]$j.waterLevel } else { 0.0 }
[MapGen]::LoadHeightFromFile((Join-Path $tex 'heightmap.raw'), [int]$j.heightmapResolution,
    [float]$j.width, [float]$j.height, $water)

$file = if ($Layer -le 4) { 'stratums_1_4.tga' } else { 'stratums_5_8.tga' }
$off = @{ 1 = 2; 2 = 1; 3 = 0; 4 = 3; 5 = 2; 6 = 1; 7 = 0; 8 = 3 }[$Layer]
$b = [IO.File]::ReadAllBytes((Join-Path $tex $file))
$SRes = [int]$b[12] -bor ([int]$b[13] -shl 8)
$descriptor = $b[17]

"{0}" -f (Split-Path -Leaf $MapDir) | Write-Host
"  splat {0}x{0}, TGA descriptor 0x{1:x2} -> origin {2}" -f $SRes, $descriptor,
$(if ($descriptor -band 0x20) { 'top-left' } else { 'bottom-left (rows run upward)' }) | Write-Host
"  correlating layer {0} against slope" -f $Layer | Write-Host
""

function Corr([int]$dr, [bool]$flip) {
    $n = 0; $sx = 0.0; $sy = 0.0; $sxx = 0.0; $syy = 0.0; $sxy = 0.0
    $step = [double]$j.width / $SRes
    for ($r = 8; $r -lt $SRes - 8; $r += 3) {
        for ($c = 8; $c -lt $SRes - 8; $c += 3) {
            $sr = if ($flip) { $SRes - 1 - $r } else { $r }
            $sr = $sr + $dr
            if ($sr -lt 0 -or $sr -ge $SRes) { continue }
            $v = [int]$b[18 + (($sr * $SRes) + $c) * 4 + $off]
            $x = ($c + 0.5) * $step
            $z = [double]$j.width - ($r + 0.5) * $step
            $sl = [MapGen]::SlopeAtWorld([float]$x, [float]$z)
            $n++; $sx += $v; $sy += $sl; $sxx += $v * $v; $syy += $sl * $sl; $sxy += $v * $sl
        }
    }
    if ($n -lt 2) { return 0.0 }
    $num = $n * $sxy - $sx * $sy
    $den = [Math]::Sqrt(($n * $sxx - $sx * $sx) * ($n * $syy - $sy * $sy))
    if ($den -eq 0) { return 0.0 }
    $num / $den
}

'  row offset   as written   rows flipped' | Write-Host
$best = $null
foreach ($dr in - $MaxShift..$MaxShift) {
    $a = Corr $dr $false; $c = Corr $dr $true
    '   {0,4}         {1,8:N4}      {2,8:N4}' -f $dr, $a, $c | Write-Host
    if (-not $best -or $a -gt $best.C) { $best = @{ C = $a; DR = $dr; F = $false } }
    if ($c -gt $best.C) { $best = @{ C = $c; DR = $dr; F = $true } }
}
""
'  best {0:N4} at row offset {1}, rows {2}' -f $best.C, $best.DR,
$(if ($best.F) { 'FLIPPED' } else { 'as written' }) | Write-Host
