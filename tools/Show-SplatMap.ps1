<#
.SYNOPSIS
    Top-down false-colour image of which stratum layer wins at each texel.

.DESCRIPTION
    Show-Stratums gives per-layer averages, which tells you a layer is painted
    on the wrong slope but not what the result looks like. This draws the
    layout, so a pattern - rings, bands, blotches - is visible and can be
    attributed to a specific layer instead of guessed at from a screenshot.

    Each layer gets a flat, distinct colour with no shading, deliberately: this
    is a diagram of the splat weights, not an attempt to preview the map.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MapDir,
    [string]$Out,
    [int]$Res = 900
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$f = Get-ChildItem $MapDir -Filter *.sanmap | Select-Object -First 1
$j = Get-Content $f.FullName -Raw | ConvertFrom-Json
$tex = Join-Path $MapDir 'Textures'
if (-not $Out) { $Out = Join-Path (Split-Path -Parent $PSScriptRoot) ("maps\" + (Split-Path -Leaf $MapDir) + "_splat.png") }

function Read-Tga([string]$p) {
    $b = [IO.File]::ReadAllBytes($p)
    @{ W = ([int]$b[12] -bor ([int]$b[13] -shl 8)); Data = $b }
}
$t14 = Read-Tga (Join-Path $tex 'stratums_1_4.tga')
$t58 = Read-Tga (Join-Path $tex 'stratums_5_8.tga')
$SRes = $t14.W

# BGRA in the file: byte0=B=layer3, byte1=G=layer2, byte2=R=layer1, byte3=A=layer4
$chan = @(
    @{ L = 1; T = $t14; O = 2 }, @{ L = 2; T = $t14; O = 1 }, @{ L = 3; T = $t14; O = 0 }, @{ L = 4; T = $t14; O = 3 }
    @{ L = 5; T = $t58; O = 2 }, @{ L = 6; T = $t58; O = 1 }, @{ L = 7; T = $t58; O = 0 }, @{ L = 8; T = $t58; O = 3 }
)
# 0 = base showing through, then one colour per layer
$cols = @(
    [Drawing.Color]::FromArgb(40, 40, 46), [Drawing.Color]::FromArgb(220, 60, 60),
    [Drawing.Color]::FromArgb(70, 200, 90), [Drawing.Color]::FromArgb(60, 130, 240),
    [Drawing.Color]::FromArgb(240, 210, 60), [Drawing.Color]::FromArgb(180, 90, 220),
    [Drawing.Color]::FromArgb(240, 150, 50), [Drawing.Color]::FromArgb(80, 220, 220),
    [Drawing.Color]::FromArgb(250, 250, 250)
)

$bmp = New-Object Drawing.Bitmap $Res, $Res
$counts = @(0) * 9
# The splat is stored bottom-up, so flip while drawing to get a picture that
# matches a top-down view of the map.
for ($y = 0; $y -lt $Res; $y++) {
    $sy = $SRes - 1 - [int]($y * $SRes / $Res)
    for ($x = 0; $x -lt $Res; $x++) {
        $sx = [int]($x * $SRes / $Res)
        $best = 0; $bestV = 40        # base wins unless a layer beats this
        foreach ($c in $chan) {
            $v = [int]$c.T.Data[18 + (($sy * $SRes) + $sx) * 4 + $c.O]
            if ($v -gt $bestV) { $bestV = $v; $best = $c.L }
        }
        $counts[$best]++
        $bmp.SetPixel($x, $y, $cols[$best])
    }
}
$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { $null = New-Item -ItemType Directory -Path $dir -Force }
$bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$names = @('(base)') + (0..7 | ForEach-Object { ($j.stratumLayers[$_ + 1].albedo.path -replace '.*/', '') -replace '_albedo\.tga$', '' })
$tot = $Res * $Res
"{0}   splat {1}x{1}" -f (Split-Path -Leaf $MapDir), $SRes | Write-Host
for ($i = 0; $i -lt 9; $i++) {
    if ($counts[$i] -eq 0) { continue }
    '  {0}  {1,-34} {2,6:P1}  rgb({3},{4},{5})' -f $i, $names[$i], ($counts[$i] / $tot),
    $cols[$i].R, $cols[$i].G, $cols[$i].B | Write-Host
}
$Out | Write-Host
