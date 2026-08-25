<#
.SYNOPSIS
    Generate test maps that isolate what each stratum mask channel does.

.DESCRIPTION
    Every rendering fault in this project came from putting a plausible number
    into a field whose meaning was unknown. The stratum _mask is the last of
    those: we know what the shipped masks average to per channel, because that
    is measurable, but not what any channel drives.

    So measure it the other way round - paint stripes that differ only in the
    mask and look at them.

    The first attempt at this was unreadable, for three reasons worth writing
    down because they are the whole design:

      * Fog. The template map has fogAttenuationDistance 251 on a 256 m map and
        a fog layer 132 m deep, so the terrain sits inside it and every stripe
        washes out to the same pale blue. Fog is pushed out of the way here.

      * Flat ground has one surface normal, so a specular difference has
        nowhere to show. Each stripe now carries a half-cylinder ridge running
        north-south, sweeping the surface normal through roughly +/-45 degrees.
        Gloss shows up as a bright band on the ridge; matte does not.

      * Four gentle levels of one channel is a subtle test. The primary probe
        now puts 0 against 255 for all four channels side by side, which is the
        most contrast the format allows.

    Three maps:

        ~PROBE-Mask_Extremes   R0 R255 G0 G255 B0 B255 A0 A255
        ~PROBE-Mask_RG         R and G swept 0/85/170/255
        ~PROBE-Mask_BA         B and A swept 0/85/170/255

    Read the extremes map first: it says which channel matters. Then read that
    channel's sweep map to see whether the response is linear.

    Albedo, normal, tiling and diffuseRemap are identical across all nine
    layers, and maskRemapMin/Max pass the raw value through, so the mask is the
    only variable. Everything else is copied from a known-good map.

    The stripes run vertically on purpose. Splat TGAs are written bottom-up and
    row order has bitten this project before; a pattern that varies only in x
    is identical either way up, so an orientation mistake cannot corrupt the
    result.

.EXAMPLE
    .\New-MaskProbe.ps1 -Force
#>
[CmdletBinding()]
param(
    [string]$EngineMaps = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Maps',
    [string]$EditorMaps = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\map-editor\SanctuaryMapEditor_Data\Maps',
    [string]$Template   = 'Serpent_Crossing',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')

$tplDir = Join-Path $EngineMaps $Template
$tplMap = Get-ChildItem $tplDir -Filter *.sanmap | Select-Object -First 1
if (-not $tplMap) { throw "no template map at '$tplDir'" }
$tpl = Get-Content $tplMap.FullName -Raw | ConvertFrom-Json
$size   = [int]$tpl.width
$hmRes  = [int]$tpl.heightmapResolution
if ($hmRes -le 0) { $hmRes = $size + 1 }
$maxH   = [double]$tpl.height

# Shipped-mask channel means, held on the three channels a stripe is not testing.
$hold = @{ R = 0; G = 219; B = 150; A = 36 }

function Write-FlatTga([string]$Path, [int]$Res, [int]$B, [int]$G, [int]$R, [int]$A) {
    $hdr = [byte[]]::new(18)
    $hdr[2] = 2
    [BitConverter]::GetBytes([uint16]$Res).CopyTo($hdr, 12)
    [BitConverter]::GetBytes([uint16]$Res).CopyTo($hdr, 14)
    $hdr[16] = 32; $hdr[17] = 0x28
    $px = [byte[]]::new($Res * $Res * 4)
    for ($i = 0; $i -lt $px.Length; $i += 4) {
        $px[$i] = [byte]$B; $px[$i+1] = [byte]$G; $px[$i+2] = [byte]$R; $px[$i+3] = [byte]$A
    }
    $fs = [IO.File]::Create($Path)
    try { $fs.Write($hdr, 0, 18); $fs.Write($px, 0, $px.Length) } finally { $fs.Dispose() }
}

function Write-StripeSplat([string]$Path, [int]$Res, [int[]]$Stripes) {
    $hdr = [byte[]]::new(18)
    $hdr[2] = 2
    [BitConverter]::GetBytes([uint16]$Res).CopyTo($hdr, 12)
    [BitConverter]::GetBytes([uint16]$Res).CopyTo($hdr, 14)
    $hdr[16] = 32; $hdr[17] = 0x28
    # BGRA on disk is [L3,L2,L1,L4] low / [L7,L6,L5,L8] high, so the n-th layer
    # of a pair lives at this byte offset:
    $offsetFor = @{ 0 = 2; 1 = 1; 2 = 0; 3 = 3 }
    $row = [byte[]]::new($Res * 4)
    for ($x = 0; $x -lt $Res; $x++) {
        $stripe = [math]::Min(7, [math]::Floor($x * 8.0 / $Res))
        $n = $Stripes.IndexOf([int]$stripe)
        if ($n -ge 0) { $row[$x * 4 + $offsetFor[$n]] = 255 }
    }
    $fs = [IO.File]::Create($Path)
    try { $fs.Write($hdr, 0, 18); for ($y = 0; $y -lt $Res; $y++) { $fs.Write($row, 0, $row.Length) } }
    finally { $fs.Dispose() }
}

# One half-cylinder ridge per stripe, running north-south. Flat ground has a
# single surface normal, so a specular difference between stripes has nowhere
# to appear; a ridge sweeps the normal through the whole range and a glossy
# material answers with a bright band down its flank.
function Write-RidgeHeightmap([string]$Path, [int]$Res, [double]$BaseM, [double]$RidgeM, [double]$MaxM) {
    $raw = [byte[]]::new($Res * $Res * 2)
    $row = [byte[]]::new($Res * 2)
    for ($x = 0; $x -lt $Res; $x++) {
        $u = $x * 8.0 / $Res
        $stripe = [math]::Min(7, [math]::Floor($u))
        $t = $u - [math]::Floor($u)                              # 0..1 across the stripe
        # Ridge height ramps west to east, so the stripe order can be read off
        # the shape from any camera angle. Without it a screenshot taken from
        # the east reverses the order and a reading of which channel did what
        # is a coin flip - which is exactly what happened the first time.
        $amp = $RidgeM * (0.35 + 0.65 * ($stripe / 7.0))
        $h = $BaseM + $amp * [math]::Sin([math]::PI * $t)
        $v = [uint16][math]::Round(($h / $MaxM) * 65535)
        $p = [BitConverter]::GetBytes($v)
        $row[$x*2] = $p[0]; $row[$x*2+1] = $p[1]
    }
    for ($y = 0; $y -lt $Res; $y++) { [Array]::Copy($row, 0, $raw, $y * $Res * 2, $row.Length) }
    [IO.File]::WriteAllBytes($Path, $raw)
}

function New-Probe([string]$Name, [object[]]$Spec, [string]$Note) {
    $dir = Join-Path $EngineMaps $Name
    if (Test-Path $dir) {
        if (-not $Force) { throw "'$dir' exists; pass -Force" }
        Remove-Item $dir -Recurse -Force
    }
    $tex = Join-Path $dir 'Textures'
    $null = New-Item -ItemType Directory -Path $tex -Force

    foreach ($f in 'tint_colors.tga', 'tint_geometry.tga') {
        Copy-Item (Join-Path $tplDir "Textures\$f") (Join-Path $tex $f)
    }
    $prev = Join-Path $tplDir 'preview.png'
    if (Test-Path $prev) { Copy-Item $prev (Join-Path $dir 'preview.png'); Copy-Item $prev (Join-Path $tex 'preview.png') }

    Write-RidgeHeightmap (Join-Path $tex 'heightmap.raw') $hmRes ($maxH * 0.20) ($maxH * 0.09) $maxH
    Write-StripeSplat (Join-Path $tex 'stratums_1_4.tga') $hmRes @(0, 1, 2, 3)
    Write-StripeSplat (Join-Path $tex 'stratums_5_8.tga') $hmRes @(4, 5, 6, 7)

    $res = 64
    $grey = [byte[]]::new($res * $res * 4)
    for ($i = 0; $i -lt $grey.Length; $i += 4) { $grey[$i] = 128; $grey[$i+1] = 128; $grey[$i+2] = 128; $grey[$i+3] = 255 }
    [IO.File]::WriteAllBytes((Join-Path $tex 'probe_grey_albedo.dds'), [MapGen]::WriteDxt1Dds($grey, $res, $res))
    $flat = [byte[]]::new($res * $res * 4)
    for ($i = 0; $i -lt $flat.Length; $i += 4) { $flat[$i] = 255; $flat[$i+1] = 128; $flat[$i+2] = 128; $flat[$i+3] = 255 }
    [IO.File]::WriteAllBytes((Join-Path $tex 'probe_flat_normal.dds'), [MapGen]::WriteDxt1Dds($flat, $res, $res))

    $legend = @()
    $stratums = @()
    for ($li = 0; $li -lt 9; $li++) {
        $c = @{ R = $hold.R; G = $hold.G; B = $hold.B; A = $hold.A }
        $maskName = 'probe_mask_hold.tga'
        if ($li -ge 1) {
            $s = $Spec[$li - 1]
            $c[$s.Ch] = $s.Val
            $maskName = "probe_mask_$($s.Ch.ToLower())_$($s.Val).tga"
            $legend += '  stripe {0}  {1} = {2,3}' -f ($li - 1), $s.Ch, $s.Val
        }
        Write-FlatTga (Join-Path $tex $maskName) 4 $c.B $c.G $c.R $c.A

        $stratums += , [ordered]@{
            name                 = $null
            albedo               = @{ path = 'map/Textures/probe_grey_albedo.dds' }
            normal               = @{ path = 'map/Textures/probe_flat_normal.dds' }
            mask                 = @{ path = "map/Textures/$maskName" }
            tileSize             = @{ x = 10.0; y = 10.0 }
            tileSizeFar          = @{ x = 60.0; y = 60.0 }
            tileSizeTriplanar    = 12.0
            tileSizeFarTriplanar = 36.0
            normalScale          = 1.0; normalScaleFar = 1.0
            normalFarNearBlend   = 0.3; heightFarNearBlend = 0.5
            diffuseRemap         = @{ r = 0.5; g = 0.5; b = 0.5; a = 1.0 }
            farColorRemap        = @{ r = 1.0; g = 1.0; b = 1.0; a = 0.0 }
            maskRemapMin         = @{ x = 0.0; y = 0.0; z = 0.0; w = 0.0 }
            maskRemapMax         = @{ x = 1.0; y = 1.0; z = 1.0; w = 1.0 }
        }
    }

    $map = $tpl | ConvertTo-Json -Depth 20 | ConvertFrom-Json
    $map.name          = $Name
    $map.credits       = $Note
    $map.stratumLayers = $stratums
    $map.hasWater      = $false
    $map.props         = @()
    $map.decals        = @()

    # Fog off. It is the single biggest reason the first probe was unreadable:
    # the terrain sat inside a 132 m fog layer with attenuation shorter than the
    # map, so every stripe washed to the same pale blue.
    $map.fogAttenuationDistance = 100000.0
    $map.fogBaseHeight          = -1000.0
    $map.fogMaximumHeight       = -900.0
    $map.fogMaximumDistance     = 100000.0
    $map.fogAnisotropy          = 0.0
    # Low sun, so the ridges catch a grazing highlight.
    $map.sunDA = 18.0

    [IO.File]::WriteAllText((Join-Path $dir "$Name.sanmap"),
        ($map | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))

    if ($EditorMaps) {
        $ed = Join-Path $EditorMaps $Name
        if (Test-Path $ed) { Remove-Item $ed -Recurse -Force }
        Copy-Item $dir $ed -Recurse
    }

    Write-Host ("{0}  ({1} m, splat {2}, ridges)" -f $Name, $size, $hmRes) -ForegroundColor Cyan
    $legend | ForEach-Object { Write-Host $_ }
    Write-Host ("  other channels held at R={0} G={1} B={2} A={3}" -f $hold.R, $hold.G, $hold.B, $hold.A)
    ''
}

function Spec([string]$ch, [int]$v) { return @{ Ch = $ch; Val = $v } }

# One map per channel. Position within a map stopped being readable the moment
# perspective got involved - a black stripe fourth from the left is G=255 or
# B=0 depending on which end you are standing at, and those mean opposite
# things. Splitting by channel makes the first-order answer "which map changed",
# which needs no counting and no orientation at all. The level within a map is
# the second question, and the ridge ramp answers that once you know what you
# are looking for.
foreach ($ch in 'R', 'G', 'B', 'A') {
    $spec = 0, 36, 73, 109, 146, 182, 219, 255 | ForEach-Object { Spec $ch $_ }
    New-Probe "~PROBE-Ch_$ch" $spec `
        "Single channel $ch swept 0..255 west to east; every other channel held at the shipped mean."
}

New-Probe '~PROBE-Mask_Extremes' @(
    (Spec 'R' 0), (Spec 'R' 255), (Spec 'G' 0), (Spec 'G' 255),
    (Spec 'B' 0), (Spec 'B' 255), (Spec 'A' 0), (Spec 'A' 255)
) 'Mask channel extremes, west to east: R0 R255 G0 G255 B0 B255 A0 A255.'

New-Probe '~PROBE-Mask_RG' @(
    (Spec 'R' 0), (Spec 'R' 85), (Spec 'R' 170), (Spec 'R' 255),
    (Spec 'G' 0), (Spec 'G' 85), (Spec 'G' 170), (Spec 'G' 255)
) 'Mask sweep, west to east: R 0/85/170/255 then G 0/85/170/255.'

New-Probe '~PROBE-Mask_BA' @(
    (Spec 'B' 0), (Spec 'B' 85), (Spec 'B' 170), (Spec 'B' 255),
    (Spec 'A' 0), (Spec 'A' 85), (Spec 'A' 170), (Spec 'A' 255)
) 'Mask sweep, west to east: B 0/85/170/255 then A 0/85/170/255.'

'Read ~PROBE-Mask_Extremes first - it says which channel matters at all.' | Write-Host
'Each stripe carries a ridge, so gloss shows as a bright band down its flank.' | Write-Host
'Fog is off and the sun is low. View from a shallow angle, not from overhead.' | Write-Host
