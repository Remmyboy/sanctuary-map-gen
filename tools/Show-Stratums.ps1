<#
.SYNOPSIS
    What each stratum layer is painted on, for a deployed map.

.DESCRIPTION
    For every one of the eight blended layers, reports the texture it uses, how
    much of the map it covers, and the mean slope and height of the ground it
    is painted on. Run it on a map the developers shipped to learn what a layer
    is *supposed* to mean, and on one of ours to see whether we agree.

    This is how the channel order was established in the first place: a layer
    whose texture is called "rock_cliff" should sit on steep ground, and if it
    does not, either the channel mapping is wrong or the weights are.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MapDir
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')

$f = Get-ChildItem $MapDir -Filter *.sanmap | Select-Object -First 1
$j = Get-Content $f.FullName -Raw | ConvertFrom-Json
$tex = Join-Path $MapDir 'Textures'
$water = if ($j.hasWater) { [float]$j.waterLevel } else { 0.0 }

[MapGen]::LoadHeightFromFile((Join-Path $tex 'heightmap.raw'), [int]$j.heightmapResolution,
    [float]$j.width, [float]$j.height, $water)

function Read-Tga([string]$p) {
    $b = [IO.File]::ReadAllBytes($p)
    $w = [int]$b[12] -bor ([int]$b[13] -shl 8)
    $h = [int]$b[14] -bor ([int]$b[15] -shl 8)
    @{ W = $w; H = $h; Data = $b }        # pixels start at byte 18, BGRA
}

$t14 = Read-Tga (Join-Path $tex 'stratums_1_4.tga')
$t58 = Read-Tga (Join-Path $tex 'stratums_5_8.tga')
$SRes = $t14.W

"{0}   {1}x{1} m, splat {2}x{2}, water {3}" -f (Split-Path -Leaf $MapDir), $j.width, $SRes,
$(if ($j.hasWater) { $j.waterLevel } else { 'none' }) | Write-Host
""

# BGRA in the file: byte0=B=layer3, byte1=G=layer2, byte2=R=layer1, byte3=A=layer4
$chan = @(
    @{ L = 1; Tga = $t14; Byte = 2 }, @{ L = 2; Tga = $t14; Byte = 1 }
    @{ L = 3; Tga = $t14; Byte = 0 }, @{ L = 4; Tga = $t14; Byte = 3 }
    @{ L = 5; Tga = $t58; Byte = 2 }, @{ L = 6; Tga = $t58; Byte = 1 }
    @{ L = 7; Tga = $t58; Byte = 0 }, @{ L = 8; Tga = $t58; Byte = 3 }
)

'  layer  texture                                cover   mean slope   mean height   slope where weight > 0.5' | Write-Host
'  -----  -------------------------------------  ------  ----------   -----------   ------------------------' | Write-Host

$step = [double]$j.width / ($SRes - 1)   # vertex-aligned, not texel-centred
foreach ($ch in $chan) {
    $d = $ch.Tga.Data; $off = $ch.Byte
    $sumW = 0.0; $sumSlope = 0.0; $sumH = 0.0; $strongN = 0; $strongSlope = 0.0
    for ($r = 0; $r -lt $SRes; $r += 2) {
        for ($c = 0; $c -lt $SRes; $c += 2) {
            $v = [int]$d[18 + (($r * $SRes) + $c) * 4 + $off]
            if ($v -eq 0) { continue }
            $w = $v / 255.0
            $x = $c * $step; $z = $r * $step        # file row 0 is world z min
            $sl = [MapGen]::SlopeAtWorld([float]$x, [float]$z)
            $hh = [MapGen]::HeightAtWorld([float]$x, [float]$z)
            $sumW += $w; $sumSlope += $w * $sl; $sumH += $w * $hh
            if ($w -gt 0.5) { $strongN++; $strongSlope += $sl }
        }
    }
    $total = [Math]::Pow([Math]::Ceiling($SRes / 2.0), 2)
    $path = ($j.stratumLayers[$ch.L].albedo.path -replace '.*/', '') -replace '_albedo\.tga$', ''
    if ($sumW -lt 1) {
        '  {0,5}  {1,-37}  {2,6}' -f $ch.L, $path, 'unused' | Write-Host
    }
    else {
        '  {0,5}  {1,-37}  {2,5:P1}  {3,8:N1} deg  {4,9:N1} m   {5,8:N1} deg  ({6:P0} of map)' -f `
            $ch.L, $path, ($sumW / $total), ($sumSlope / $sumW), ($sumH / $sumW),
        $(if ($strongN) { $strongSlope / $strongN } else { 0 }), ($strongN / $total) | Write-Host
    }
}
