<#
    Builds "Serpent Crossing" - a 1v1, 256 m, tropical map.

      * river runs corner to corner, top-left to bottom-right
      * bases sit back from the TL and BR corners on opposite banks
      * two causeways cross the river, one just outside each base
      * 180-degree rotational symmetry throughout

    Terrain, splatmaps and preview come from MapGen.cs; this script places the
    markers against the finished heightfield, validates them, and writes the
    .sanmap JSON.
#>
[CmdletBinding()]
param(
    [string]$MapsRoot = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\map-editor\SanctuaryMapEditor_Data\Maps',
    [string]$Folder   = 'Serpent_Crossing',
    [string]$MapName  = 'Serpent Crossing',
    [string]$DebugOut,

    # Prop blueprints ship under different extensions per build: the engine's
    # Environment.sanpack has 94 .santp, the map editor's has 76 .sanprop, and
    # the same maps are exported both ways. Getting this wrong is not a soft
    # failure - see the props section below.
    [ValidateSet('.santp', '.sanprop')]
    [string]$PropExtension = '.santp',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

. (Join-Path $here 'src' 'Import-MapGen.ps1')
. (Join-Path $here 'src' 'Biomes.ps1')

# Serpent Crossing predates the style config and relies on MapGen's defaults,
# including the hardcoded BridgeX/BridgeZ that were authored for this map. Now
# that a river is opt-in it has to say so, and vouch for those coordinates.
[MapGen]::UseRiver = $true
[MapGen]::BridgesPlaced = $true
# Serpent Crossing is 256 m and relied on MapGen's field defaults, so it never
# went through Configure and kept a 512 splat. The splat has to be vertex
# aligned to the heightmap grid like every shipped map.
[MapGen]::Configure(256.0, 0)

$mapDir = Join-Path $MapsRoot $Folder
$texDir = Join-Path $mapDir 'Textures'
if (Test-Path $mapDir) {
    if (-not $Force) { throw "'$mapDir' exists. Pass -Force." }
    Remove-Item $mapDir -Recurse -Force
}
$null = New-Item -ItemType Directory -Path $texDir -Force

Write-Host 'Building heightfield...'
[MapGen]::BuildHeight()
# Sand off invisible one-cell obstacles: they leave the map 100% reachable
# but litter it with pinch points units path around.
"  smoothed {0} isolated blocked patches" -f ([MapGen]::SmoothPathingSpecks(60, 8)) | Write-Host
Write-Host 'Building stratum weights...'
[MapGen]::BuildLayers()
# Slot badges on the preview, in spawn order.
[MapGen]::PreviewSpawnX = [float[]][MapGen]::BaseX.Clone()
[MapGen]::PreviewSpawnZ = [float[]][MapGen]::BaseZ.Clone()

# ------------------------------------------------------------- markers ---

# Nine resource spots on the top-left player's bank; the bottom-right set is
# the exact 180-degree rotation, so the map is mirror-fair by construction.
$alloySideA = @(
    @(86,232), @(116,234), @(88,206), @(120,202),
    @(146,238), @(176,220), @(58,238), @(196,250),
    @(156,156)          # contested, just north-east of the centre crossing
)

$M = 256.0
$spawns = @(
    @{ Army = 'ARMY_1'; X = [MapGen]::BaseX[0]; Z = [MapGen]::BaseZ[0] }
    @{ Army = 'ARMY_2'; X = [MapGen]::BaseX[1]; Z = [MapGen]::BaseZ[1] }
)

# A spot is good if it is dry, gentle, and clear of the channel.
function Test-Ok {
    param([double]$X, [double]$Z, [double]$MaxSlope, [double]$MinRiver)
    if ($X -lt 8 -or $X -gt 248 -or $Z -lt 8 -or $Z -gt 248) { return $false }
    ([MapGen]::HeightAtWorld($X, $Z) -gt [MapGen]::WaterLevel + 1.0) -and
    ([MapGen]::SlopeAtWorld($X, $Z) -le $MaxSlope) -and
    ([math]::Abs([MapGen]::RiverDist($X, $Z)) -ge $MinRiver)
}

# If a hand-placed spot lands somewhere awkward, walk outward in rings until a
# valid one turns up. Only the side-A point moves; side B is its mirror, so the
# map stays symmetric whatever the nudge does.
function Resolve-Spot {
    param([double]$X, [double]$Z, [double]$MaxSlope, [double]$MinRiver, [string]$Label)
    if (Test-Ok $X $Z $MaxSlope $MinRiver) { return ,@($X, $Z) }
    foreach ($rad in 4, 8, 12, 16, 20, 26, 32) {
        foreach ($deg in 0..23) {
            $a = $deg * 15 * [math]::PI / 180
            $nx = [math]::Round($X + $rad * [math]::Cos($a))
            $nz = [math]::Round($Z + $rad * [math]::Sin($a))
            if (Test-Ok $nx $nz $MaxSlope $MinRiver) {
                Write-Host ("  nudged {0}: ({1},{2}) -> ({3},{4})  [{5} m]" -f $Label, $X, $Z, $nx, $nz, $rad)
                return ,@([double]$nx, [double]$nz)
            }
        }
    }
    Write-Warning "could not place $Label near ($X,$Z)"
    return ,@($X, $Z)
}

Write-Host 'Placing markers...'
$resolvedA = @()
$i = 0
foreach ($p in $alloySideA) {
    $i++
    $resolvedA += ,(Resolve-Spot ([double]$p[0]) ([double]$p[1]) 12.0 22.0 ("alloy A{0}" -f $i))
}

$alloyPts = @()
foreach ($p in $resolvedA) { $alloyPts += ,@([double]$p[0], [double]$p[1]) }
foreach ($p in $resolvedA) {
    $rx = $M - [double]$p[0]
    $rz = $M - [double]$p[1]
    $alloyPts += ,@($rx, $rz)
}

# --------------------------------------------------------- validation ---

function Test-Spot {
    param([double]$X, [double]$Z, [double]$MaxSlope, [string]$Label)
    $h  = [MapGen]::HeightAtWorld($X, $Z)
    $sl = [MapGen]::SlopeAtWorld($X, $Z)
    $rd = [math]::Abs([MapGen]::RiverDist($X, $Z))
    $ok = ($h -gt [MapGen]::WaterLevel + 1.0) -and ($sl -le $MaxSlope)
    [pscustomobject]@{
        Label = $Label; X = $X; Z = $Z
        Height = [math]::Round($h, 2); Slope = [math]::Round($sl, 1)
        RiverDist = [math]::Round($rd, 1); OK = $ok
    }
}

$report = @()
$i = 0
foreach ($s in $spawns) { $i++; $report += Test-Spot $s.X $s.Z 6.0 "spawn $($s.Army)" }
$i = 0
foreach ($p in $alloyPts) { $i++; $report += Test-Spot $p[0] $p[1] 12.0 ("alloy {0:D3}" -f $i) }

$report | Format-Table -AutoSize | Out-String | Write-Host
$bad = $report | Where-Object { -not $_.OK }
if ($bad) {
    Write-Warning "$($bad.Count) marker(s) failed validation:"
    $bad | Format-Table -AutoSize | Out-String | Write-Host
}

# Prove the causeways are the only dry crossings.
Write-Host 'Crossings:'
for ($b = 0; $b -lt 2; $b++) {
    $prof = [MapGen]::CrossingProfile($b, 161)
    $min  = ($prof | Measure-Object -Minimum).Minimum
    $dry  = ($prof | Where-Object { $_ -gt [MapGen]::WaterLevel }).Count
    $state = if ($min -gt [MapGen]::WaterLevel) { 'CONTINUOUS dry land' } else { 'BROKEN - dips below water' }
    "  bridge {0}: lowest point {1:N2} m (water {2:N1} m), {3}/161 samples dry -> {4}" -f `
        ($b + 1), $min, [MapGen]::WaterLevel, $dry, $state | Write-Host
}
$riverMax = [MapGen]::RiverMaxHeightBetweenBridges()
$sealed = if ($riverMax -lt [MapGen]::WaterLevel) { 'sealed' } else { 'LEAKS - fordable somewhere' }
"  river elsewhere: highest bed point {0:N2} m -> {1}" -f $riverMax, $sealed | Write-Host

# Pathability, using the game's own Land-layer rule (30 deg, 3x3 dilated).
[MapGen]::BuildWalkable()
$reach = [MapGen]::Reachable([MapGen]::BaseX[0], [MapGen]::BaseZ[0])
$walk  = [MapGen]::WalkableCount()
$rc    = [MapGen]::CountTrue($reach)
Write-Host 'Pathability (Land layer, maxSlope 30 deg):'
"  walkable cells {0:N0}, reachable from ARMY_1 {1:N0} ({2:P0} of walkable)" -f $walk, $rc, ($rc/$walk) | Write-Host

$enemyOk = [MapGen]::IsReachable($reach, [MapGen]::BaseX[1], [MapGen]::BaseZ[1])
"  enemy spawn reachable overland: {0}" -f $(if($enemyOk){'YES'}else{'NO - the bridges do not connect!'}) | Write-Host

$unreachable = @()
$i = 0
foreach ($p in $alloyPts) {
    $i++
    if (-not [MapGen]::IsReachable($reach, $p[0], $p[1])) { $unreachable += ("Alloys_{0:D3} ({1},{2})" -f $i, $p[0], $p[1]) }
}
if ($unreachable.Count) {
    Write-Warning ("  {0}/18 alloy spots are cut off: {1}" -f $unreachable.Count, ($unreachable -join ', '))
} else {
    Write-Host "  all 18 alloy spots reachable on foot"
}

for ($b = 0; $b -lt 2; $b++) {
    $ok = [MapGen]::IsReachable($reach, [MapGen]::BridgeX[$b], [MapGen]::BridgeZ[$b])
    "  bridge {0} deck reachable: {1}" -f ($b+1), $(if($ok){'YES'}else{'NO'}) | Write-Host
}
Write-Host ''

$ts = [MapGen]::TerrainStats()
$land = $ts[2]
"Terrain: {0:N1} m .. {1:N2} m ({2:N1} m of relief)" -f $ts[0], $ts[1], ($ts[1] - $ts[0]) | Write-Host
"  dry land slopes: {0:P0} flat (<6 deg), {1:P0} gentle (6-15), {2:P0} steep (15-34), {3:P0} cliff (>34)" -f `
    ($ts[3]/$land), ($ts[4]/$land), ($ts[5]/$land), ($ts[6]/$land) | Write-Host
Write-Host ''

# ---------------------------------------------------------- textures ----

Write-Host 'Writing textures...'
[MapGen]::WriteHeightmap((Join-Path $texDir 'heightmap.raw'))
[MapGen]::WriteStratums($texDir)
[MapGen]::WriteTints($texDir, 2048)
[MapGen]::WritePreview((Join-Path $texDir 'preview.png'), 512, $false, $null, $null, $null)

# (the annotated layout render happens after the props exist, further down)

# -------------------------------------------------------------- json ----

# Palette lifted from ~TEAM-1v1_Tropical_256_47940, with the three textures the
# map-editor's Environment.sanpack lacks swapped for present equivalents:
#   sand05 -> sand02, rock_cliff02 -> rock_cliff01, gravel02 -> gravel01.
# Layer 1 is rock_basalt01 for cliff faces. Every path here is verified present
# in BOTH the engine and map-editor packs - rock_cliff03 is editor-only.
function S {
    param([string]$Tex, [double]$Tile, [double]$Far, [double]$Tri, [double]$TriFar,
          [double]$Nrm, [double]$NrmFar, [double]$NB, [double]$HB,
          [double[]]$Diff, [double[]]$FarRemap, [double[]]$MMin, [double[]]$MMax)
    $p = "Environment/01_Highlands/Stratum/$Tex"
    [ordered]@{
        name   = $null
        albedo = @{ path = "${p}_albedo.tga" }
        normal = @{ path = "${p}_normal.tga" }
        mask   = @{ path = "${p}_mask.tga" }
        tileSize             = @{ x = $Tile; y = $Tile }
        tileSizeFar          = @{ x = $Far;  y = $Far }
        tileSizeTriplanar    = $Tri
        tileSizeFarTriplanar = $TriFar
        normalScale          = $Nrm
        normalScaleFar       = $NrmFar
        normalFarNearBlend   = $NB
        heightFarNearBlend   = $HB
        diffuseRemap  = @{ r = $Diff[0];     g = $Diff[1];     b = $Diff[2];     a = $Diff[3] }
        farColorRemap = @{ r = $FarRemap[0]; g = $FarRemap[1]; b = $FarRemap[2]; a = $FarRemap[3] }
        maskRemapMin  = @{ x = $MMin[0]; y = $MMin[1]; z = $MMin[2]; w = $MMin[3] }
        maskRemapMax  = @{ x = $MMax[0]; y = $MMax[1]; z = $MMax[2]; w = $MMax[3] }
    }
}

$grass07 = @{ D = @(0.309759647,0.319,0.268598,1.0); F = @(0.0159015749,0.007912427,0.004240413,0.0) }

$stratums = @(
    (S 'highlands_100m_grass07'      10 64 12 36 1.0 1.22 0.0      0.5 $grass07.D $grass07.F @(0,0,0.1,0) @(1,1,0.9,1))
    (S 'highlands_60m_rock_basalt01' 10 44 10 36 1.0 1.0  0.2      0.5 @(0.58,0.556,0.54,1.0)                    @(1,1,1,0) @(0,0,0,0) @(1,1,1,1))
    (S 'highlands_100m_heather03'     8 32 12 36 1.0 1.0  0.5      0.5 @(0.298039228,0.286274523,0.192156866,1.0) @(0.0376494564,0.03465263,0.03465263,0.0) @(0,0,0,0) @(1,1,1,1))
    (S 'highlands_100m_grass02'       8 64 12 36 1.0 1.0  0.319    0.5 @(0.549019635,0.58431375,0.5411765,1.0)    @(0.235482112,0.2004364,0.148044586,0.0)  @(0,0,0,0) @(1,1,1,1))
    (S 'highlands_100m_grass03'       8 32 12 36 1.0 1.0  0.533783 0.5 @(0.5280531,0.615026534,0.701999962,1.0)   @(0.06586576,0.04347261,0.0224204231,0.0) @(0,0,0.1,0) @(1,1,0.8,1))
    (S 'highlands_100m_mud02'         8 40 12 36 1.0 1.0  0.56636  0.5 @(0.5280531,0.615026534,0.701999962,1.0)   @(1,1,1,0) @(0,0,0,0) @(1,1,1,1))
    (S 'highlands_100m_sand02'       10 32  8 36 0.8 0.8  0.062    0.5 @(0.262999982,0.204119369,0.109910429,1.0) @(0.250646025,0.2133727,0.138813585,0.0) @(0,0,0,0) @(1,1,1,1))
    (S 'highlands_100m_rock_cliff01' 12 52 10 36 1.0 1.0  0.164    0.5 @(0.6666667,0.5372549,0.5137255,1.0)       @(1,1,1,0) @(0,0,0,0) @(1,1,1,1))
    (S 'highlands_60m_gravel01'      12 52 10 36 1.0 1.0  0.164    0.5 @(0.6666667,0.5372549,0.5137255,1.0)       @(1,1,1,0) @(0,0,0,0) @(1,1,1,1))
)

function T {
    param([double]$X, [double]$Y, [double]$Z)
    [ordered]@{
        position = @{ x = $X;  y = $Y;  z = $Z }
        rotation = @{ x = 0.0; y = 0.0; z = 0.0; w = 1.0 }
        scale    = @{ x = 1.0; y = 1.0; z = 1.0 }
    }
}

$spawnT = [ordered]@{}
foreach ($s in $spawns) {
    $y = [math]::Round([MapGen]::HeightAtWorld($s.X, $s.Z), 2)
    $spawnT[$s.Army] = T $s.X $y $s.Z
}

$alloyT = [ordered]@{}
$i = 0
foreach ($p in $alloyPts) {
    $i++
    $y = [math]::Round([MapGen]::HeightAtWorld($p[0], $p[1]), 2)
    $alloyT["Alloys_{0:D3}" -f $i] = T $p[0] $y $p[1]

    # alloy_spot decals use the quaternion family (a, b, -b, a) with a^2+b^2=0.5:
    # a flat-to-ground rotation with a free spin about Y.
}

function Army { [ordered]@{ faction = 0; alloys = 500.0; energy = 500.0; groups = @{} } }

# ------------------------------------------------------------- props ----
# Every Highlands prop is tagged HARVESTABLE with harvest = { alloys, plasma },
# so these are early reclaim as well as scenery. edb* have tall colliders
# (bushes/trees), edm* are flat (ground rocks).
#
# The extension matters enormously. A blueprint the build can't find is not a
# soft failure: Engine.GetFileContent returns an empty chunk rather than nil,
# so mapUtils.lua's `if propFileString then` guard passes, pcall "succeeds"
# with propTemplateData = nil, and CreatePropPrefab(nil) throws on tp.visuals.
# That aborts RunMapSetup at mapUtils.lua:92 - and the alloy resource spots are
# created at line 113. One bad prop path silently costs the map every single
# one of its alloy points, with nothing on screen to say why.
$treeBps = @('edbm0121','edbm0122','edbm0123','edbm0124','edbm0125')
$rockBps = @('edmm0104','edmm0106','edms0110')

$avoidX = [float[]]($alloyPts | ForEach-Object { [float]$_[0] })
$avoidZ = [float[]]($alloyPts | ForEach-Object { [float]$_[1] })

Write-Host "Scattering props ($PropExtension)..."
$buckets = @{}
foreach ($b in ($treeBps + $rockBps)) { $buckets[$b] = @() }

foreach ($set in @(
        @{ Bps = $treeBps; Rocks = $false; Count = 260; Seed = 8081 },
        @{ Bps = $rockBps; Rocks = $true;  Count =  70; Seed = 4409 })) {

    $flat = [MapGen]::Scatter($set.Seed, $set.Count, $set.Rocks, $avoidX, $avoidZ, 12.0)
    $n = $flat.Length / 5
    for ($k = 0; $k -lt $n; $k++) {
        $x = [double]$flat[$k*5]; $y = [double]$flat[$k*5+1]; $z = [double]$flat[$k*5+2]
        $yaw = [double]$flat[$k*5+3]; $sc = [double]$flat[$k*5+4]
        $bp = $set.Bps[$k % $set.Bps.Count]

        # original, then its 180-degree mirror on the far bank
        foreach ($inst in @(@($x, $z, $yaw), @(($M - $x), ($M - $z), ($yaw + [math]::PI)))) {
            $buckets[$bp] += ,[ordered]@{
                position = @{ x = [math]::Round($inst[0],3); y = [math]::Round($y,3); z = [math]::Round($inst[1],3) }
                rotation = @{ x = 0.0
                              y = [math]::Round([math]::Sin($inst[2] / 2), 7)
                              z = 0.0
                              w = [math]::Round([math]::Cos($inst[2] / 2), 7) }
                scale    = @{ x = [math]::Round($sc,4); y = [math]::Round($sc,4); z = [math]::Round($sc,4) }
            }
        }
    }
    Write-Host ("  {0}: {1} per bank -> {2} total" -f $(if($set.Rocks){'rocks'}else{'trees'}), $n, ($n*2))
}

$propGroups = @()
foreach ($b in ($treeBps + $rockBps)) {
    if ($buckets[$b].Count -eq 0) { continue }
    $propGroups += ,[ordered]@{
        blueprintPath = "Environment/01_Highlands/Props/$b/$b$PropExtension"
        transforms    = $buckets[$b]
    }
}
Write-Host ("  {0} blueprint groups, {1} instances" -f $propGroups.Count, (($buckets.Values | ForEach-Object { $_.Count }) | Measure-Object -Sum).Sum)

if ($DebugOut) {
    $mx = @(); $mz = @(); $mk = @()
    foreach ($g in $propGroups) {
        foreach ($t in $g.transforms) { $mx += [float]$t.position.x; $mz += [float]$t.position.z; $mk += 2 }
    }
    foreach ($p in $alloyPts) { $mx += [float]$p[0]; $mz += [float]$p[1]; $mk += 1 }
    foreach ($s in $spawns)   { $mx += [float]$s.X;  $mz += [float]$s.Z;  $mk += 0 }
    [MapGen]::WritePreview($DebugOut, 768, $true, [float[]]$mx, [float[]]$mz, [int[]]$mk)
    Write-Host "  layout render -> $DebugOut"
    $stem = [IO.Path]::ChangeExtension($DebugOut, $null).TrimEnd('.')
    [MapGen]::WriteHeightPreview("${stem}_elevation.png", 768)
    Write-Host "  elevation render -> ${stem}_elevation.png"
    [MapGen]::WriteWalkPreview("${stem}_walk.png", 768, $reach)
    Write-Host "  walkability render -> ${stem}_walk.png"
}

$map = [ordered]@{
    fileVersion         = 3
    mapVersion          = 1
    name                = $MapName
    credits             = ''
    width               = 256
    length              = 256
    # SanMap.height is an int. Newtonsoft's ReadAsInt32 rejects "128.0" outright
    # and LoadJson dies before the first progress tick, so the editor sits at 0%.
    height              = 128
    heightmapResolution = [MapGen]::HRes
    # layerResolution is [JsonIgnore] on SanMap - it is always recomputed as
    # `width` on load, so writing it would be dead data. The splat TGAs can be
    # any square size; the loader takes their dimensions from the file itself,
    # and layerResolution only matters for MapEditorTextures.ImportMask().
    hasWater                   = $true
    waterLevel                 = [double][MapGen]::WaterLevel
    waterDepth                 = 2.0
    waterWindSpeed             = 0.06
    waterWindDirection         = 100.0
    waterShoreDepthOffset      = 8.0
    waterShoreDepthStrength    = 0.7
    waterShoreDistanceOffset   = 0.0
    waterShoreDistanceStrength = 2.0
    shader              = 'RTS/TerrainLit'
    heightTransition    = 2.0
    fadeDistance        = 50.0
    fadeStartDistance   = 30.0
    stratumLayers       = $stratums

    sunRA                     = 96.2
    sunDA                     = 30.0
    sunIntensity              = 60000.0
    sunTint                   = @{ r = 1.0; g = 1.0; b = 1.0; a = 1.0 }
    sunTemperature            = 9800.0
    sunAngularDiameter        = 0.5
    sunVolumetricsMultiplier  = 6.7
    sunVolumetricsShadowDimer = 0.5
    skylightIntensity         = 0.0
    skylightTint              = @{ r = 1.0; g = 1.0; b = 1.0; a = 1.0 }
    skylightTemperature       = 12000.0
    exposure                  = 11.5
    exposureCompensation      = 0.0
    skyboxExposure            = 12.0
    fogAttenuationDistance    = 251.0
    fogBaseHeight             = 5.41
    fogMaximumHeight          = 132.5
    fogMaximumDistance        = 1500.0
    fogAnisotropy             = 0.0
    skybox = @{ path = 'Environment/Skybox/kloofendal_48d_partly_cloudy_puresky_4k.exr' }

    areas   = @{ Playable = @{ x = 0.0; y = 0.0; width = 256.0; height = 256.0 } }
    armies  = [ordered]@{ ARMY_1 = (Army); ARMY_2 = (Army) }
    chains  = @{}
    markers = [ordered]@{
        Spawn  = [ordered]@{ resource = $false; transforms = $spawnT }
        Alloys = [ordered]@{ resource = $true;  transforms = $alloyT }
    }
    decals = @()
    windSpeed     = 0.25
    windDirection = 160.0
    props         = $propGroups
}

# Fields the shipped maps set that SanMap would otherwise default badly - most
# importantly the height fog. See src\Biomes.ps1.
foreach ($kv in (New-MapEnvironment 'Tropical' ([double][MapGen]::WaterLevel)).GetEnumerator()) {
    $map[$kv.Key] = $kv.Value
}
$sanmap = Join-Path $mapDir "$Folder.sanmap"
[IO.File]::WriteAllText($sanmap, ($map | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding $false))

Write-Host ''
Get-ChildItem $mapDir -Recurse -File | ForEach-Object { "  {0,-22} {1,12:N0}" -f $_.Name, $_.Length }
Write-Host ''

# Replay the game's own deserialisation before claiming this is loadable.
& (Join-Path $MapGenTools 'Test-Sanmap.ps1') -Path $sanmap -CheckTextures

Write-Host ''
Write-Host "Open: $sanmap"
