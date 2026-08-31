<#
    The one biome table. Dot-source it:

        . (Join-Path $PSScriptRoot 'src' 'Biomes.ps1')

    There used to be two copies of this, one in New-RandomMap.ps1 and one in
    Convert-ScMap.ps1, and fixing a fault in one left it live in the other.

    BuildLayers gives every slot a fixed meaning, and a biome table that does
    not respect it produces exactly the sort of nonsense that sent us looking
    for a shader bug:

        0   base            flat ground, shows wherever nothing is painted
        1   cliff face      slope above 36 degrees
        2   mid slope       5 to 15 degrees   - VEGETATION OR SOIL, NEVER ROCK
        3   upper slope     13 to 27 degrees  - coarse ground, may be stony
        4   variation       flat-ground noise
        5   mud             at the waterline
        6   sand            shore, or wind drift on a dry map
        7   rock            slope above 26 degrees
        8   gravel          roads and crossings

    Slot 2 is the one to watch. It covers ground a player reads as flat, so a
    rock texture there paints grey contour rings across open desert - which is
    precisely what "cliffs on perfectly flat ground" turned out to be. Measured
    on a converted map: rock_sandstone01 sitting in slot 2 covered 12.7% of the
    map at a mean slope of 7.6 degrees.

    For comparison, the slope ramp on a map the developers shipped runs
    grass 5.9 -> heather 11.7 -> grass02 21.3 -> mud/cliff/gravel 28-29. Nothing
    rocky appears below the high twenties.

    Tone matters as much as slot. tools\Show-TextureTones.ps1 reports the mean
    brightness of every stratum: sand05 comes out at 183 where its neighbours
    sit near 130, so it painted blazing pale blotches wherever it landed. Keep
    consecutive steps of a ramp close in tone - a jump reads as a line drawn on
    the ground, whatever slope it is on.
#>

<#
    Measured mean luminance of each stratum albedo, from
    tools\Show-TextureTones.ps1.

    diffuseRemap is a per-texture colour correction, not a per-slot constant.
    Copying the shipped map's slot values wholesale was a mistake: its slot 6
    carried sand05, whose albedo averages luminance 183, so 0.26/0.20/0.11 was
    there to pull a very bright texture down. Our slot 6 carries sand02 at 130,
    and the same multiplier crushed 53% of the map to near-black.

    So compute the remap instead: target tone divided by the texture's own tone.
    A bright texture gets pulled down hard, a dark one barely at all, and both
    land at the same effective brightness.

    Only the DXT1 entries are measured reliably - the endpoint-averaging trick
    the tone tool uses does not apply to BC7, and those all come back near 122,
    which happens to be close to the true middle of the set. Anything unlisted
    falls back to 125, giving a remap near 0.33, which is about the shipped
    average anyway.
#>
$script:TextureLum = @{
    'highlands_100m_marsh01'          = 49
    'highlands_100m_grass01'          = 61
    'highlands_50m_heather01'         = 68
    'highlands_100m_rock_cliff02'     = 75
    'highlands_60m_rock_basalt01'     = 75
    'highlands_100m_grass03'          = 83
    'highlands_100m_moss01'           = 87
    'highlands_100m_mud01'            = 93
    'highlands_100m_mud02'            = 99
    'highlands_100m_grass02'          = 103
    'highlands_100m_rock_cliff01'     = 103
    'highlands_100m_rock_sandstone02' = 106
    'highlands_100m_heather03'        = 110
    'highlands_100m_grass07'          = 118
    'highlands_100m_groundrock_02'    = 122
    'highlands_100m_gravel02'         = 127
    'highlands_100m_sand02'           = 130
    'highlands_60m_gravel01'          = 140
    'highlands_100m_snow01'           = 171
    'highlands_100m_sand01'           = 177
    'highlands_100m_rock_sandstone01' = 179
    'highlands_100m_sand05'           = 183
    '10_WhiteDesert/desert_100m_ground_foliage_04' = 69
    '10_WhiteDesert/desert_100m_rock_01'          = 89
    '10_WhiteDesert/desert_100m_sand_03'          = 106
    '10_WhiteDesert/desert_100m_sandstone_01'     = 120
    '10_WhiteDesert/desert_100m_sand_02'          = 124
    '10_WhiteDesert/desert_100m_sand_01'          = 130
    'Winter/rock'                     = 65
    'Winter/moss_dry'                 = 78
    'Winter/dirt_c'                   = 112
    'Winter/dirt_a'                   = 152
    'Winter/snow_plain_darker'        = 192
    'Winter/dirt_d'                   = 193
    'Winter/dirt_b'                   = 224
    'Winter/snow_plain'               = 226
    '02_Evergreen/grass'              = 57
    '02_Evergreen/rock'               = 68
}

<#
    Effective brightness each slot should land on, read off the shipped map:
    texture luminance times its remap gives roughly 30 for ground cover, 45 for
    rock and cliff, and a little more for the upper-slope layers that want to
    stand out from the ground.
#>
$script:SlotTargetTone = @(38, 45, 32, 44, 40, 34, 36, 45, 44)

function Get-DiffuseRemap([string]$texture, [int]$slot) {
    $lum = if ($script:TextureLum.ContainsKey($texture)) { $script:TextureLum[$texture] } else { 125 }
    $k = [Math]::Max(0.15, [Math]::Min(0.90, $script:SlotTargetTone[$slot] / $lum))
    # A touch warmer on red, cooler on blue, so ground reads as earth rather
    # than as flat grey. Nothing like the strong tints that turned desert
    # gravel violet.
    @{ r = [Math]::Round($k * 1.06, 3); g = [Math]::Round($k, 3); b = [Math]::Round($k * 0.90, 3); a = 1.0 }
}

<#
    A layer name may name its stratum set: "Winter/rock" resolves into the
    Winter set, a bare name into 01_Highlands.

    Worth having, because the game ships five sets and the biome tables were
    built almost entirely out of 01_Highlands. Arid was assembled from highland
    sand and grass while a 10_WhiteDesert set sat unused - which is why it kept
    fighting the palette. highlands_50m_heather01 measures R79 G58 B88: green
    well below both red and blue, so it paints magenta on a desert.
#>
function Resolve-LayerPath([string]$t) {
    if ($t -match '^([^/]+)/(.+)$') { return "Environment/$($Matches[1])/Stratum/$($Matches[2])" }
    "Environment/01_Highlands/Stratum/$t"
}

function Get-Biome([string]$key) {
    switch ($key) {
        'Highlands' { @{
                Layers = @('highlands_100m_grass07', 'highlands_60m_rock_basalt01', 'highlands_100m_heather03',
                    'highlands_100m_grass03', 'highlands_100m_grass02', 'highlands_100m_mud02',
                    'highlands_100m_sand02', 'highlands_100m_rock_cliff01', 'highlands_60m_gravel01')
                Sun    = 96.2; SunTemp = 9200; Sky = 12000; Exposure = 11.5; Fog = 330
            } }
        'Tropical' { @{
                Layers = @('highlands_100m_grass07', 'highlands_60m_rock_basalt01', 'highlands_100m_moss01',
                    'highlands_100m_grass02', 'highlands_100m_marsh01', 'highlands_100m_mud02',
                    'highlands_100m_sand02', 'highlands_100m_rock_cliff01', 'highlands_60m_gravel01')
                Sun    = 88.0; SunTemp = 8200; Sky = 13000; Exposure = 11.3; Fog = 280
            } }
        'Winter' { @{
                # Highland textures, not the dedicated Winter set.
                #
                # The Winter set looks like the obvious choice and cannot be used
                # as it stands: it ships albedos with no matching _normal, and
                # three of its textures have no _mask either. A table built from
                # it produces a map referencing nine assets that do not exist.
                # tools\Test-BiomeTextures.ps1 checks for this now.
                #
                # Using it properly means letting a layer take its normal and
                # mask from a different texture than its albedo, which
                # New-StratumLayers cannot currently express.
                Layers = @('highlands_100m_snow01', 'highlands_60m_rock_basalt01', 'highlands_100m_heather03',
                    'highlands_100m_groundrock_02', 'highlands_100m_grass01', 'highlands_100m_mud01',
                    'highlands_100m_sand01', 'highlands_100m_rock_cliff01', 'highlands_60m_gravel01')
                Sun    = 140.0; SunTemp = 11500; Sky = 14000; Exposure = 12.2; Fog = 220
            } }
        'Evergreen' { @{
                Layers = @('02_Evergreen/grass', 'highlands_60m_rock_basalt01', 'highlands_100m_moss01',
                    'highlands_100m_grass01', 'highlands_100m_heather03', 'highlands_100m_mud01',
                    'highlands_100m_sand02', '02_Evergreen/rock', 'highlands_60m_gravel01')
                Sun    = 110.0; SunTemp = 7600; Sky = 11000; Exposure = 11.6; Fog = 300
            } }
        'Arid' { @{
                # The game ships a desert stratum set; this table used to be
                # assembled out of highland grass and sand instead, which is why
                # it never settled. Six textures, all warm (R minus B from 12 to
                # 70), so nothing in the ramp fights the sand.
                Layers = @('10_WhiteDesert/desert_100m_sand_01', '10_WhiteDesert/desert_100m_rock_01',
                    '10_WhiteDesert/desert_100m_sand_03', '10_WhiteDesert/desert_100m_sandstone_01',
                    '10_WhiteDesert/desert_100m_sand_02', '10_WhiteDesert/desert_100m_ground_foliage_04',
                    '10_WhiteDesert/desert_100m_sand_01', '10_WhiteDesert/desert_100m_rock_01',
                    '10_WhiteDesert/desert_100m_sandstone_01')
                Sun    = 118.0; SunTemp = 7000; Sky = 10500; Exposure = 11.9; Fog = 460
            } }
        default { throw "unknown biome '$key'" }
    }
}

<#
    Build the nine stratumLayers entries for a biome.

    Every layer used to get the same tile size, the same far-tile blend and the
    same diffuseRemap of 0.45/0.45/0.42. The maps the developers ship vary all
    three per layer, and diffuseRemap is the important one: it is a colour
    correction applied to the texture, and it is how they make textures of very
    different raw tone sit together.

    The clearest example is sand05, whose albedo averages luminance 183 where
    its neighbours are near 130. Left alone it paints blazing pale blotches. The
    shipped map multiplies it by 0.26/0.20/0.11 - darkened and warmed hard - and
    it reads as ordinary desert.

    The values below follow the roles on ~TEAM-1v1_Tropical_256_47940. Ground
    cover is darkened, rock is left brighter, and the far-tile blend rises for
    the layers that want visible detail up close.
#>
<#
    Point the preview at a generated map's actual ground.

    A converted map carries its textures in its own folder; a generated map
    references the game's, which live inside Environment.sanpack - a plain zip
    of .dds, behind the .tga paths the stratum layers name (the loader is
    extension-agnostic). Without this a Winter map previews in Highlands green,
    the same fault converted maps had.

    Silently leaves the built-in table alone when the pack is not there:
    previews must not depend on the game being installed.
#>
function Set-BiomePreviewColors([string]$Biome, [string]$GamedataDir) {
    if (-not $GamedataDir) {
        $GamedataDir = 'C:\Program Files (x86)\Steam\steamapps\common\Sanctuary Shattered Sun Playtest\engine\Sanctuary_Data\Gamedata'
    }
    $pack = Join-Path $GamedataDir 'Environment.sanpack'
    if (-not (Test-Path $pack)) { return }

    $b = Get-Biome $Biome
    $albedos = New-Object 'byte[][]' 9
    $remaps = New-Object 'double[][]' 9
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($pack)
        try {
            $index = @{}
            foreach ($e in $zip.Entries) { $index[$e.FullName.ToLowerInvariant().TrimStart('/')] = $e }
            for ($i = 0; $i -lt 9; $i++) {
                $key = ((Resolve-LayerPath $b.Layers[$i]) + '_albedo.dds').ToLowerInvariant()
                $entry = $index[$key]
                if (-not $entry) { continue }
                $ms = New-Object IO.MemoryStream
                $st = $entry.Open(); $st.CopyTo($ms); $st.Dispose()
                $albedos[$i] = $ms.ToArray(); $ms.Dispose()
                $rm = Get-DiffuseRemap $b.Layers[$i] $i
                $remaps[$i] = [double[]]@($rm.r, $rm.g, $rm.b)
            }
        }
        finally { $zip.Dispose() }
    }
    catch { return }

    [MapGen]::SetPreviewLayerColorsFromBytes($albedos, $remaps)
}

function New-StratumLayers([string]$biome) {
    $b = Get-Biome $biome

    #            tile  far  triPlan  nScale  nFNB    diffuseRemap r/g/b
    $role = @(
        @(10.0, 64.0, 12.0, 1.00, 0.00, 0.31, 0.32, 0.27),   # 0 base
        @(12.0, 52.0, 10.0, 1.00, 0.16, 0.67, 0.54, 0.51),   # 1 cliff
        @( 8.0, 32.0, 12.0, 1.00, 0.50, 0.30, 0.29, 0.19),   # 2 mid slope
        @( 8.0, 64.0, 12.0, 1.00, 0.32, 0.55, 0.58, 0.54),   # 3 upper slope
        @( 8.0, 32.0, 12.0, 1.00, 0.53, 0.53, 0.62, 0.70),   # 4 variation
        @( 8.0, 40.0, 12.0, 1.00, 0.57, 0.53, 0.62, 0.70),   # 5 mud
        @(10.0, 32.0,  8.0, 0.80, 0.06, 0.26, 0.20, 0.11),   # 6 sand
        @(12.0, 52.0, 10.0, 1.00, 0.16, 0.67, 0.54, 0.51),   # 7 rock
        @(12.0, 52.0, 10.0, 1.00, 0.16, 0.67, 0.54, 0.51)    # 8 gravel
    )

    $out = @()
    for ($i = 0; $i -lt 9; $i++) {
        $p = Resolve-LayerPath $b.Layers[$i]
        $r = $role[$i]
        $out += , [ordered]@{
            name                 = $null
            albedo               = @{ path = "${p}_albedo.tga" }
            normal               = @{ path = "${p}_normal.tga" }
            mask                 = @{ path = "${p}_mask.tga" }
            tileSize             = @{ x = $r[0]; y = $r[0] }
            tileSizeFar          = @{ x = $r[1]; y = $r[1] }
            tileSizeTriplanar    = $r[2]
            tileSizeFarTriplanar = 36.0
            normalScale          = $r[3]; normalScaleFar = 1.0
            normalFarNearBlend   = $r[4]; heightFarNearBlend = 0.5
            diffuseRemap         = Get-DiffuseRemap $b.Layers[$i] $i
            farColorRemap        = @{ r = 1.0; g = 1.0; b = 1.0; a = 0.0 }
            maskRemapMin         = @{ x = 0.0; y = 0.0; z = 0.0; w = 0.0 }
            maskRemapMax         = @{ x = 1.0; y = 1.0; z = 1.0; w = 1.0 }
        }
    }
    $out
}

<#
    Environment fields every shipped map sets and we were leaving out.

    Omitting a field does not mean "engine default is fine" - it means SanMap's
    C# initialiser wins, and several of those are nothing like what the shipped
    maps use:

        heightFogIntensity      default 1.0          shipped 0.195
        heightFogRange          default (-10, 100)   shipped (16, 61)
        backgroundColor         default black        shipped near-white
        backgroundFogIntensity  default 1.0          shipped 0.425
        linearFogIntensity      default 0.24         shipped 0.167

    Dense height fog banded from below sea level, coloured by a black
    background, pools in every hollow on the map and reads as a violet haze
    ringing each pond. That was the "holes" - fog, not texture.

    heightFogRange is tied to the water level because that is what the shipped
    maps do: fog starts at the waterline and fades out about 45 m above it.
#>
function New-MapEnvironment([string]$biome, [double]$waterLevel) {
    $b = Get-Biome $biome
    $lo = if ($waterLevel -gt 0) { $waterLevel } else { 0.0 }
    [ordered]@{
        waterWindShoreWavesRemap     = 0.5
        waterShoreGeneratorBlueprint = ''

        backgroundFogIntensity       = 0.425
        backgroundFogRange           = 1024.0
        backgroundFogMinimum         = 0.1
        backgroundSkyColorIntensity  = 0.52
        backgroundColorIntensity     = 1.0
        backgroundColor              = @{ r = 1.319508; g = 1.319508; b = 1.319508; a = 1.0 }
        backgroundColorFadeoutRange  = 15000.0
        backgroundColorFadeoutPower  = 0.2

        heightFogIntensity           = 0.195
        heightFogRange               = @{ x = [double]$lo; y = [double]($lo + 45.0) }
        heightFogStart               = -10.0
        heightFogEnd                 = 500.0
        heightFogPower               = 6.0

        linearFogIntensity           = 0.167
        linearFogStart               = 100.0
        linearFogEnd                 = 5000.0
        linearFogPower               = 1.0
        linearFogCameraIntensity     = 0.0
        linearFogCameraStart         = 500.0
        linearFogCameraEnd           = 5000.0

        sunPosition                  = @{ x = 512.0; y = 512.0; z = -130.0 }
        sunCookie                    = @{ path = '' }
        sunCookieSize                = @{ x = 512.0; y = 512.0 }
        skyboxRotation               = 232.0
        skyboxIntensityMode          = 'Exposure'
        skyboxMultiplier             = 1.0
        skyboxLuxValue               = 30000.0
    }
}
