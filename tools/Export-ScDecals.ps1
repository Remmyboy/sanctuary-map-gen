<#
.SYNOPSIS
    Turn a Supreme Commander map's decal textures into map-local Sanctuary
    decal blueprints.

.DESCRIPTION
    A Sanctuary decal is three files: a .sandecal naming a .sanmaterial naming
    the textures. All of it is JSON, and a map's own folder can carry all three
    the same way it carries stratum textures - so unlike props, decals need no
    mapping table at all. The author's mud splat IS the content; extract it and
    author the two blueprint files around it.

    Supreme Commander types map onto the material keywords directly:

      type 1 (albedo)  -> _MATERIAL_AFFECTS_ALBEDO + _COLORMAP, texture in
                          _BaseColorMap, alpha is the decal's shape
      type 2 (normals) -> _MATERIAL_AFFECTS_NORMAL + _NORMALMAP, texture in
                          _NormalMap

    The _DecalColorMask floats are inferred, not copied: the shipped material
    affects albedo+normal+mask and carries 15/15/11/8, which reads as one
    write-mask per decal buffer (0 albedo, 1 normal, 2 mask, 3 resolve). An
    albedo-only decal therefore gets 15/0/0/0 and a normal-only one 0/15/0/0.
    If decals render but bleed into channels they should not touch, these
    masks are the first suspect.
#>
[CmdletBinding()]
param(
    # One entry per distinct texture: @{ Path = '/env/...dds'; Type = 1 }.
    [Parameter(Mandatory)][object[]]$Entries,
    [Parameter(Mandatory)][string]$DestDir,
    [string]$ScdPath = 'F:\SteamLibrary\steamapps\common\Supreme Commander Forged Alliance\gamedata\env.scd',
    [string]$MapsRoot,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')

if (-not (Test-Path $DestDir)) { $null = New-Item -ItemType Directory -Path $DestDir -Force }

$zip = [IO.Compression.ZipFile]::OpenRead($ScdPath)
$index = @{}
foreach ($e in $zip.Entries) { $index[$e.FullName.ToLowerInvariant().TrimStart('/')] = $e }

# A flat tangent-space normal (128,128,255) with full alpha, shared by every
# albedo decal so its normal write is inert inside the shape.
$flatPath = Join-Path $DestDir 'sc_flat_normal.dds'
if (-not (Test-Path $flatPath)) {
    $res = 4
    $px = [byte[]]::new($res * $res * 4)
    for ($i = 0; $i -lt $px.Length; $i += 4) {
        $px[$i] = 255; $px[$i+1] = 128; $px[$i+2] = 128; $px[$i+3] = 255   # BGRA
    }
    [IO.File]::WriteAllBytes($flatPath, [MapGen]::WriteDxt5Dds($px, $res, $res))
}

$blueprints = @{}       # source path -> map/Decals/... blueprint path
$missing = @()
$copied = 0
$transcoded = 0

try {
    foreach ($en in $Entries) {
        $src = [string]$en.Path
        $type = [int]$en.Type
        if ([string]::IsNullOrWhiteSpace($src)) { continue }
        $key = $src.ToLowerInvariant().TrimStart('/')
        $leaf = [IO.Path]::GetFileName($key)
        $stem = [IO.Path]::GetFileNameWithoutExtension($leaf)

        # The texture itself: env.scd first, the map folder for /maps/ paths.
        $bytes = $null
        $entry = $index[$key]
        if ($entry) {
            $ms = New-Object IO.MemoryStream
            $s = $entry.Open(); $s.CopyTo($ms); $s.Close()
            $bytes = $ms.ToArray(); $ms.Dispose()
        }
        elseif ($MapsRoot -and $key -like 'maps/*') {
            $cand = Join-Path $MapsRoot ($key -replace '^maps/', '')
            if (Test-Path $cand) { $bytes = [IO.File]::ReadAllBytes($cand) }
        }
        if (-not $bytes) { $missing += $key; continue }
        if ([MapGen]::TranscodeDxt3ToDxt5($bytes)) { $transcoded++ }

        $ddsOut = Join-Path $DestDir "$leaf"
        if (-not (Test-Path $ddsOut)) { [IO.File]::WriteAllBytes($ddsOut, $bytes); $copied++ }

        # The material copies the shipped terrain-decal recipes verbatim
        # rather than a hand-derived subset. The first version declared only
        # the channel the decal touches and bound only one texture; the shader
        # samples all three slots unconditionally, an unbound sampler reads as
        # white, and white everywhere means fully opaque everywhere - every
        # decal rendered as a solid rectangle.
        #
        # Two shipped archetypes cover both source types exactly:
        #   type 1 -> "Detail1"/"Dunes1" (affects albedo + normal)
        #   type 2 -> "Dunes2"/"CliffRidge1" (affects normal only,
        #             _AffectAlbedo=0)
        # In both, every texture slot is bound and the base colour alpha is
        # the decal's shape. Type 2 binds its normals texture in every slot -
        # its alpha still shapes it and nothing else is written; type 1 binds
        # a shared flat normal so the normal write inside the shape is inert.
        if ($type -eq 2) {
            $keywords = '"_MATERIAL_AFFECTS_NORMAL", "_DISABLE_SSR_TRANSPARENT", "_NORMALMAP_TANGENT_SPACE"'
            $baseTex = $leaf; $normTex = $leaf; $maskTex = $leaf
            $affects = '{ "key": "_AffectAlbedo", "value": 0.0 }, { "key": "_AffectMetal", "value": 0.0 }, { "key": "_AffectSmoothness", "value": 0.0 }, { "key": "_DecalColorMask1", "value": 15.0 }'
        }
        else {
            $keywords = '"_MATERIAL_AFFECTS_ALBEDO", "_MATERIAL_AFFECTS_NORMAL", "_DISABLE_SSR_TRANSPARENT", "_NORMALMAP_TANGENT_SPACE"'
            $baseTex = $leaf; $normTex = 'sc_flat_normal.dds'; $maskTex = $leaf
            $affects = '{ "key": "_AffectMetal", "value": 0.0 }, { "key": "_AffectSmoothness", "value": 0.0 }, { "key": "_DecalColorMask0", "value": 15.0 }, { "key": "_DecalColorMask1", "value": 15.0 }'
        }

        $material = @"
{
  "name": "$stem",
  "shader": "RTS/Decals/Default",
  "renderQueue": 2000,
  "keywords": [ $keywords ],
  "textures": [
    {
      "key": "_NormalMap",
      "path": "map/Decals/$normTex",
      "linear": true,
      "tilingOffset": { "x": 1.0, "y": 1.0, "z": 0.0, "w": 0.0 }
    },
    {
      "key": "_BaseColorMap",
      "path": "map/Decals/$baseTex",
      "linear": false,
      "tilingOffset": { "x": 1.0, "y": 1.0, "z": 0.0, "w": 0.0 }
    },
    {
      "key": "_MaskMap",
      "path": "map/Decals/$maskTex",
      "linear": true,
      "tilingOffset": { "x": 1.0, "y": 1.0, "z": 0.0, "w": 0.0 }
    }
  ],
  "colors": [
    { "key": "_BaseColor", "value": { "r": 1.0, "g": 1.0, "b": 1.0, "a": 1.0 } }
  ],
  "vectors": [],
  "floats": [
    { "key": "_DrawOrder", "value": -3.0 },
    { "key": "_DecalMeshDepthBias", "value": 0.39 },
    { "key": "_DecalStencilWriteMask", "value": 16.0 },
    { "key": "_DecalStencilRef", "value": 16.0 },
    $affects
  ]
}
"@
        # Distinct stems on all three files. The map-file registry resolves by
        # extension-stripped stem with .dds probed first, so a .sandecal that
        # shares its stem with the texture hands the decal loader DDS bytes -
        # json.decode returns nil and RunMapSetup aborts at the decals.
        [IO.File]::WriteAllText((Join-Path $DestDir "$stem`_mat.sanmaterial"), $material, (New-Object Text.UTF8Encoding $false))

        $decal = @"
{
  "drawDistance": 1000.0,
  "fadeStart": 0.0,
  "fadeFactor": 0.9,
  "material": "map/Decals/$stem`_mat.sanmaterial"
}
"@
        [IO.File]::WriteAllText((Join-Path $DestDir "$stem`_decal.sandecal"), $decal, (New-Object Text.UTF8Encoding $false))
        $blueprints[$src] = "map/Decals/$stem`_decal.sandecal"
    }
}
finally { $zip.Dispose() }

if (-not $Quiet) {
    "  decal blueprints: {0} authored, {1} textures copied{2}" -f `
        $blueprints.Count, $copied, $(if ($transcoded) { ", {0} DXT3 -> DXT5" -f $transcoded }) | Write-Host
    if ($missing.Count) { "  decal textures not found: {0}" -f (($missing | Select-Object -First 3) -join ', ') | Write-Host }
}

[pscustomobject]@{ Blueprints = $blueprints; Missing = $missing; Copied = $copied; Transcoded = $transcoded }
