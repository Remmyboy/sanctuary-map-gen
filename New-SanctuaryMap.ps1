<#
.SYNOPSIS
    Creates a new, perfectly flat Sanctuary: Shattered Sun map from scratch.

.DESCRIPTION
    The demo's map editor has no working "New Map" command (the button exists in
    the scene but has no onClick handler, and MapEditorMenu has no New() method).
    This script writes a complete, loadable map folder directly:

        <MapsRoot>\<Folder>\
            <Folder>.sanmap          JSON, fileVersion 3
            Textures\heightmap.raw   (Size+1)^2 uint16 LE, constant
            Textures\stratums_1_4.tga  Size*2 square, 32bpp BGRA, all zero
            Textures\stratums_5_8.tga  ditto
            Textures\tint_colors.tga   max(2048,Size*2) square, BGRA 128/128/128/0
            Textures\tint_geometry.tga ditto, BGRA 255/128/128/255 (flat normal)

    Formats were derived from EM.Map.SanMap / EM.Gamedata.Load in the shipped
    assemblies and cross-checked against the four bundled maps.

.EXAMPLE
    .\New-SanctuaryMap.ps1 -Name "Flat Test"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Name,

    [string]$MapsRoot = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\map-editor\SanctuaryMapEditor_Data\Maps',

    # Playable extent in metres. Bundled maps use 512 / 1024 / 2048.
    [ValidateSet(256, 512, 1024, 2048)]
    [int]$Size = 512,

    # Full vertical range of the heightmap in metres (raw 65535 == this height).
    # SanMap.height is an int field, so this must stay whole.
    [int]$MaxHeight = 128,

    # Where the flat plane sits, in metres. Default = 25% of MaxHeight, which
    # leaves headroom to both raise and lower terrain later.
    [double]$FlatHeight = -1,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if ($FlatHeight -lt 0) { $FlatHeight = $MaxHeight * 0.25 }
if ($FlatHeight -gt $MaxHeight) { throw "FlatHeight ($FlatHeight) exceeds MaxHeight ($MaxHeight)." }

$folder = ($Name -replace '[^\w\- ]', '') -replace '\s+', '_'
if (-not $folder) { throw "Name '$Name' produced an empty folder name." }

$mapDir = Join-Path $MapsRoot $folder
$texDir = Join-Path $mapDir 'Textures'

if (Test-Path $mapDir) {
    if (-not $Force) { throw "'$mapDir' already exists. Pass -Force to overwrite." }
    Remove-Item $mapDir -Recurse -Force
}
$null = New-Item -ItemType Directory -Path $texDir -Force

# ---------------------------------------------------------------- writers ---

function Write-Tga {
    param([string]$Path, [int]$Width, [int]$Height,
          [byte]$B, [byte]$G, [byte]$R, [byte]$A)

    # 18-byte header, image type 2 (uncompressed true-colour), 32bpp, no footer.
    # Matches the bundled maps byte for byte, including descriptor 0.
    $hdr = [byte[]]::new(18)
    $hdr[2] = 2
    [BitConverter]::GetBytes([uint16]$Width ).CopyTo($hdr, 12)
    [BitConverter]::GetBytes([uint16]$Height).CopyTo($hdr, 14)
    $hdr[16] = 32

    $row = [byte[]]::new($Width * 4)          # TGA stores BGRA
    for ($x = 0; $x -lt $Width; $x++) {
        $o = $x * 4
        $row[$o] = $B; $row[$o+1] = $G; $row[$o+2] = $R; $row[$o+3] = $A
    }

    $fs = [IO.File]::Create($Path)
    try {
        $fs.Write($hdr, 0, $hdr.Length)
        for ($y = 0; $y -lt $Height; $y++) { $fs.Write($row, 0, $row.Length) }
    } finally { $fs.Dispose() }
}

function Write-HeightmapRaw {
    param([string]$Path, [int]$Resolution, [uint16]$Value)

    # Load.ReadRaw: Resolution^2 uint16, little-endian, value/65535 * map.height.
    $row = [byte[]]::new($Resolution * 2)
    $pair = [BitConverter]::GetBytes($Value)
    for ($x = 0; $x -lt $Resolution; $x++) {
        $row[$x*2] = $pair[0]; $row[$x*2+1] = $pair[1]
    }

    $fs = [IO.File]::Create($Path)
    try { for ($y = 0; $y -lt $Resolution; $y++) { $fs.Write($row, 0, $row.Length) } }
    finally { $fs.Dispose() }
}

# --------------------------------------------------------------- textures ---

$hmRes    = $Size + 1
$splatRes = $Size * 2
$tintRes  = [Math]::Max(2048, $Size * 2)
$rawValue = [uint16][Math]::Round(($FlatHeight / $MaxHeight) * 65535)

Write-Host "Generating '$Name' -> $mapDir"
Write-Host "  terrain      ${Size}x${Size} m, vertical range 0..$MaxHeight m"
Write-Host "  flat plane   $FlatHeight m  (raw $rawValue / 65535)"

Write-HeightmapRaw (Join-Path $texDir 'heightmap.raw') $hmRes $rawValue
Write-Host "  heightmap.raw       ${hmRes}x${hmRes} uint16"

# All splat weights zero -> only stratum layer 0 (the base) is visible.
Write-Tga (Join-Path $texDir 'stratums_1_4.tga') $splatRes $splatRes 0 0 0 0
Write-Tga (Join-Path $texDir 'stratums_5_8.tga') $splatRes $splatRes 0 0 0 0
Write-Host "  stratums_*.tga      ${splatRes}x${splatRes} (blank)"

# tint_colors: RGB 128 grey is the neutral point (Two_Step_Shuffle, the least
# art-directed bundled map, averages RGB 131/123/110). Alpha is the hole mask;
# two of the three other maps are alpha 0 everywhere, so 0 == no holes.
Write-Tga (Join-Path $texDir 'tint_colors.tga') $tintRes $tintRes 128 128 128 0

# tint_geometry: flat tangent-space normal, RGB 128/128/255, alpha 255.
Write-Tga (Join-Path $texDir 'tint_geometry.tga') $tintRes $tintRes 255 128 128 255
Write-Host "  tint_*.tga          ${tintRes}x${tintRes} (neutral)"

# ------------------------------------------------------------ stratum set ---

function New-StratumLayer {
    param([string]$Base, [double[]]$Tile, [double[]]$TileFar,
          [double]$NrmScale, [double]$NrmScaleFar, [double[]]$Diffuse,
          [double[]]$FarRemap, [double[]]$MaskMin, [double[]]$MaskMax)

    $p = if ($Base) { "Environment/01_Highlands/Stratum/$Base" } else { '' }
    [ordered]@{
        name   = $null
        albedo = @{ path = if ($p) { "${p}_albedo.tga" } else { '' } }
        normal = @{ path = if ($p) { "${p}_normal.tga" } else { '' } }
        mask   = @{ path = if ($p) { "${p}_mask.tga"   } else { '' } }
        tileSize             = @{ x = $Tile[0];    y = $Tile[1] }
        tileSizeFar          = @{ x = $TileFar[0]; y = $TileFar[1] }
        tileSizeTriplanar    = 12.0
        tileSizeFarTriplanar = 36.0
        normalScale          = $NrmScale
        normalScaleFar       = $NrmScaleFar
        normalFarNearBlend   = 0.5
        heightFarNearBlend   = 0.5
        diffuseRemap  = @{ r = $Diffuse[0];  g = $Diffuse[1];  b = $Diffuse[2];  a = $Diffuse[3]  }
        farColorRemap = @{ r = $FarRemap[0]; g = $FarRemap[1]; b = $FarRemap[2]; a = $FarRemap[3] }
        maskRemapMin  = @{ x = $MaskMin[0]; y = $MaskMin[1]; z = $MaskMin[2]; w = $MaskMin[3] }
        maskRemapMax  = @{ x = $MaskMax[0]; y = $MaskMax[1]; z = $MaskMax[2]; w = $MaskMax[3] }
    }
}

# Layer 0 is the base (visible everywhere while the splatmaps are blank);
# 1-4 give the texture-paint tab something to work with; 5-8 are free slots.
$stratums = @(
    New-StratumLayer 'highlands_100m_sand01'            @(8,8)   @(50,50)   1.5 1.0 @(0.13,0.121939994,0.1144,1.0)               @(0,0,0,0)       @(0,0,0,0)   @(1,1,1.5,1)
    New-StratumLayer 'highlands_100m_rock_sandstone02'  @(10,10) @(64,64)   1.0 0.2 @(0.19,0.16669333,0.1596,1.0)                @(0,0,0,0)       @(0,0,0.1,0) @(1,1,0.9,1)
    New-StratumLayer 'highlands_100m_grass02'           @(8,8)   @(110,110) 1.5 0.5 @(0.5399167,0.55,0.495,1.0)                  @(0,0,0,0)       @(0,0,0,0)   @(1,1,1,1)
    New-StratumLayer 'highlands_100m_mud01'             @(10,10) @(64,64)   0.5 0.5 @(0.0899999961,0.0872999951,0.0872999951,1.0) @(0.3584906,0.3584906,0.3584906,0) @(0,0,0,0) @(1,1,1,1)
    New-StratumLayer 'highlands_100m_rock_sandstone02'  @(12,12) @(52,52)   1.0 0.0 @(0.5,0.5,0.5,1.0)                           @(1,1,1,0)       @(0,0,0,0)   @(1,1,1,1)
)
foreach ($i in 5..8) {
    $stratums += New-StratumLayer '' @(1,1) @(1,1) 1.0 1.0 @(0.5,0.5,0.5,1.0) @(1,1,1,1) @(0,0,0,0) @(1,1,1,1)
}

# ------------------------------------------------------- markers & armies ---

function New-Transform {
    param([double]$X, [double]$Y, [double]$Z)
    [ordered]@{
        position = @{ x = $X;   y = $Y; z = $Z }
        rotation = @{ x = 0.0;  y = 0.0; z = 0.0; w = 1.0 }
        scale    = @{ x = 1.0;  y = 1.0; z = 1.0 }
    }
}

# 180-degree rotational symmetry about the map centre. The editor has no marker
# UI in this build, so these are the starting layout you edit in JSON.
$q = $Size / 4.0                                # quarter, e.g. 128 on a 512 map
$c = $Size / 2.0                                # centre

$spawn = [ordered]@{
    Army_1 = New-Transform $q             $FlatHeight $q
    Army_2 = New-Transform ($Size - $q)   $FlatHeight ($Size - $q)
}

$alloyOffsets = @(@(-16,-16), @(16,-16), @(-16,16), @(16,16))
$alloys = [ordered]@{}
$n = 0
foreach ($o in $alloyOffsets) {
    $n++; $alloys["Alloys_{0:D3}" -f $n] = New-Transform ($q + $o[0])           $FlatHeight ($q + $o[1])
}
foreach ($o in $alloyOffsets) {
    $n++; $alloys["Alloys_{0:D3}" -f $n] = New-Transform ($Size - $q - $o[0])   $FlatHeight ($Size - $q - $o[1])
}
$n++; $alloys["Alloys_{0:D3}" -f $n] = New-Transform ($c - 48) $FlatHeight ($c - 48)
$n++; $alloys["Alloys_{0:D3}" -f $n] = New-Transform ($c + 48) $FlatHeight ($c + 48)

function New-Army {
    param([int]$Faction, [string]$Tpid, [string]$UnitKey)
    [ordered]@{
        faction = $Faction
        alloys  = 100.0
        energy  = 1000.0
        groups  = @{
            Initial = [ordered]@{
                units = @{
                    $UnitKey = [ordered]@{
                        type     = 'Unit'
                        tpid     = $Tpid
                        position = @{ x = 0.0; y = 0.0; z = 0.0 }
                        rotation = @{ x = 0.0; y = 0.0; z = 0.0; w = 0.0 }
                        scale    = @{ x = 1.0; y = 1.0; z = 1.0 }
                    }
                }
                groups = @{}
            }
        }
    }
}

# ------------------------------------------------------------------- json ---

$map = [ordered]@{
    fileVersion         = 3
    mapVersion          = 1
    name                = $Name
    credits             = ''
    width               = $Size
    length              = $Size
    height              = [int]$MaxHeight
    heightmapResolution = $hmRes
    hasWater            = $false
    waterLevel                 = 0.0
    waterDepth                 = 0.0
    waterWindSpeed             = 0.25
    waterWindDirection         = 160.0
    waterShoreDepthOffset      = 8.0
    waterShoreDepthStrength    = 0.7
    waterShoreDistanceOffset   = 0.0
    waterShoreDistanceStrength = 2.0
    shader              = 'RTS/TerrainLit'
    heightTransition    = 2.0
    fadeDistance        = 128.0
    fadeStartDistance   = 1.0
    stratumLayers       = $stratums

    sunRA                      = 128.0
    sunDA                      = 42.0
    sunIntensity               = 60000.0
    sunTint                    = @{ r = 1.0; g = 1.0; b = 1.0; a = 1.0 }
    sunTemperature             = 5800.0
    sunAngularDiameter         = 0.5
    sunVolumetricsMultiplier   = 6.7
    sunVolumetricsShadowDimer  = 0.5
    skylightIntensity          = 0.0
    skylightTint               = @{ r = 1.0; g = 1.0; b = 1.0; a = 1.0 }
    skylightTemperature        = 10000.0
    exposure                   = 12.0
    exposureCompensation       = 0.0
    skyboxExposure             = 12.0
    fogAttenuationDistance     = 350.0
    fogBaseHeight              = 10.0
    fogMaximumHeight           = 100.0
    fogMaximumDistance         = 1500.0
    fogAnisotropy              = 0.58
    skybox                     = @{ path = 'empty' }

    areas   = @{ Playable = @{ x = 0.0; y = 0.0; width = [double]$Size; height = [double]$Size } }
    armies  = [ordered]@{
        Army_1 = New-Army 1 'ues1601' 'Unit_001'
        Army_2 = New-Army 2 'ucl3001' 'Unit_002'
    }
    chains  = @{}
    markers = [ordered]@{
        Spawn  = [ordered]@{ resource = $false; transforms = $spawn  }
        Alloys = [ordered]@{ resource = $true;  transforms = $alloys }
    }
    decals        = @()
    windSpeed     = 0.1
    windDirection = 160.0
    props         = @()
}

$json = $map | ConvertTo-Json -Depth 12
$sanmap = Join-Path $mapDir "$folder.sanmap"
[IO.File]::WriteAllText($sanmap, $json, (New-Object Text.UTF8Encoding $false))

Write-Host "  $folder.sanmap      $((Get-Item $sanmap).Length) bytes"
Write-Host ''

# Replay the game's own deserialisation before claiming this is loadable.
$validator = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Test-Sanmap.ps1'
if (Test-Path $validator) { & $validator -Path $sanmap -CheckTextures }

Write-Host ''
Write-Host "Done. In the editor: File > Open >" -NoNewline
Write-Host " $sanmap"
