<#
.SYNOPSIS
    Seeded random map generator for Sanctuary: Shattered Sun.

.DESCRIPTION
    Give it a seed, a size, a player count, a style and a biome and it produces
    a complete, validated map folder. Same seed and settings always produce the
    same map.

    Every roll is put through the game's own rules before it is accepted:
    walkability uses navigationLayers.lua's 30-degree Land limit with the 3x3
    dilation from NavmapUtils.IsSteepTerrain, and the map is rejected and
    re-rolled unless every spawn and every resource spot is reachable on foot
    and enough of the map is contiguous open ground to manoeuvre an army on.

    Symmetry is 180 or 90 degrees - the only rotations that map a square onto
    itself. Higher player counts use several spawns per sector.

.EXAMPLE
    .\New-RandomMap.ps1 -Seed 4242 -Size 512 -Players 4 -Style Mesas -Biome Winter

.EXAMPLE
    .\New-RandomMap.ps1 -Count 5 -Size 512 -Players 2
#>
[CmdletBinding()]
param(
    [int]$Seed = -1,
    # Powers of two only: Unity rounds TerrainData.heightmapResolution to 2^n+1, and a
    # size like 384 leaves three quarters of the terrain unwritten. All 100 shipped
    # maps obey this.
    [ValidateSet(256, 512, 1024, 2048)]
    [int]$Size = 512,
    [ValidateSet(2, 3, 4, 6, 8)]
    [int]$Players = 2,
    [ValidateSet('Random', 'RiverCrossing', 'Mesas', 'Plateaus', 'Basin', 'Open')]
    [string]$Style = 'Random',
    [ValidateSet('Random', 'Highlands', 'Tropical', 'Winter', 'Evergreen', 'Arid')]
    [string]$Biome = 'Random',
    [string]$Name,
    [string]$MapsRoot = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Maps',
    [ValidateSet('.santp', '.sanprop')]
    [string]$PropExtension = '.santp',
    [int]$Count = 1,
    [int]$MaxAttempts = 6,
    [switch]$NoProps,
    [string]$DebugDir,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

. (Join-Path $here 'src' 'Import-MapGen.ps1')
. (Join-Path $here 'src' 'Biomes.ps1')

# ============================================================== biomes ====
# Every path is verified present in BOTH the engine and map-editor packs.
# Layer 0 is the ground everything else paints over; 1 is cliff faces, 5-8 are
# the rule-driven layers (wet, shore, slope, roads) the splat builder expects.

# Winter has no second heather; reuse a present texture rather than ship a path
# that does not resolve.


# =============================================================== styles ===
function Get-Style([string]$key, [int]$size, [int]$players) {
    $s = @{
        UseRiver = $false; OrganicRiver = $false; PathedMesas = $true
        SymOrder = $(if ($players % 4 -eq 0) { 4 } else { 2 })
        WaterLevel = 0.0; LandBase = 22.0; HasWater = $false
        BowlDepth = 0.0
        Tier1 = 13.0; Tier2 = 8.0
        PathCount = 6; Inflate = 0.013; BlurR = 0.011; MinSep = 0.20
        SpawnRadius = 0.40; Phase = 45.0; MinDirect = 0.82
        NearMex = 4; MidMex = 3; FarMex = 2
    }
    switch ($key) {
        'RiverCrossing' {
            # A diagonal channel is only symmetric under 180 degrees.
            $s.SymOrder = 2; $s.UseRiver = $true; $s.OrganicRiver = $true
            $s.HasWater = $true; $s.WaterLevel = 16.0; $s.LandBase = 21.0
            $s.SpawnRadius = 0.40; $s.Phase = 135.0; $s.MinDirect = 0.68
            $s.Tier1 = 12.0; $s.Tier2 = 8.0; $s.PathCount = 6
        }
        'Mesas'    { $s.Tier1 = 13.0; $s.Tier2 = 8.0;  $s.PathCount = 7; $s.MinSep = 0.18 }
        'Plateaus' { $s.Tier1 = 17.0; $s.Tier2 = 10.0; $s.PathCount = 4; $s.MinSep = 0.26; $s.Inflate = 0.019 }
        'Open'     { $s.Tier1 = 8.0;  $s.Tier2 = 4.0;  $s.PathCount = 3; $s.MinSep = 0.28; $s.NearMex = 5; $s.MidMex = 3 }
        'Basin'    {
            $s.HasWater = $true; $s.WaterLevel = 16.0; $s.LandBase = 26.0
            $s.BowlDepth = 26.0; $s.Tier1 = 12.0; $s.Tier2 = 7.0
            $s.PathCount = 6; $s.SpawnRadius = 0.42; $s.MinDirect = 0.68
        }
    }
    $s
}

# ============================================================ generate ====
$styleChoices = @('RiverCrossing', 'Mesas', 'Plateaus', 'Basin', 'Open')
$biomeChoices = @('Highlands', 'Tropical', 'Winter', 'Evergreen', 'Arid')

if ($Seed -lt 0) { $Seed = Get-Random -Minimum 1 -Maximum 999999 }

# .NET's Random correlates strongly across nearby seeds - seeding it with
# Seed, Seed+7919, Seed+15838 and asking for Next(5) each time returns almost
# the same value, which is why a -Count 6 batch first came out as
# Basin, Basin, Open, Open, Open, RiverCrossing. Mix the seed first.
#
# The mix lives in C#: PowerShell promotes an integer product that exceeds
# Int64 to Double, which silently destroys the avalanche.
function Get-MixedSeed([int]$v) { [MapGen]::MixSeed($v) }

for ($run = 0; $run -lt $Count; $run++) {
    $runSeed = Get-MixedSeed ($Seed + $run * 7919)
    $pick = [Random]::new($runSeed)

    $useStyle = if ($Style -eq 'Random') { $styleChoices[$pick.Next($styleChoices.Count)] } else { $Style }
    $useBiome = if ($Biome -eq 'Random') { $biomeChoices[$pick.Next($biomeChoices.Count)] } else { $Biome }

    $accepted = $false
    for ($attempt = 0; $attempt -lt $MaxAttempts -and -not $accepted; $attempt++) {
        $seedN = Get-MixedSeed ($runSeed + $attempt * 104729)
        $cfg = Get-Style $useStyle $Size $Players
        if ($Players % $cfg.SymOrder -ne 0) { $cfg.SymOrder = 2 }
        $perSector = [int]($Players / $cfg.SymOrder)

        Write-Host ""
        Write-Host ("=== {0}  {1}P  {2}  {3}  {4} m  seed {5}{6}" -f `
            $useStyle, $Players, $useBiome, "sym$($cfg.SymOrder)", $Size, $seedN,
            $(if ($attempt) { "  (attempt $($attempt+1))" } else { "" })) -ForegroundColor Cyan

        [MapGen]::Configure([float]$Size, $(if ($Size -ge 512) { 1024 } else { 512 }))
        [MapGen]::SymOrder     = $cfg.SymOrder
        [MapGen]::UseRiver     = $cfg.UseRiver
        [MapGen]::OrganicRiver = $cfg.OrganicRiver
        [MapGen]::Organic      = $false
        [MapGen]::OrganicHills = $false
        [MapGen]::PathedMesas  = $cfg.PathedMesas
        [MapGen]::HillStrength = 1.0
        [MapGen]::WaterLevel   = [float]$cfg.WaterLevel
        [MapGen]::LandBase     = [float]$cfg.LandBase
        [MapGen]::BowlDepth    = [float]$cfg.BowlDepth
        [MapGen]::CurveAmp     = [float]($Size * 0.115)
        # Landform count has to scale with area, and the separation has to
        # shrink as symmetry rises - a quarter sector of a big map still needs
        # filling, and at a fixed 20% separation the sampler only ever fits two
        # starts in it, which leaves most of the map bare.
        $areaScale = [Math]::Max(1.0, ($Size / 320.0) * ($Size / 320.0))
        [MapGen]::MesaPathCount  = [Math]::Max(3, [int]($cfg.PathCount * $areaScale / [Math]::Sqrt($cfg.SymOrder)))
        [MapGen]::MesaMinSep     = [float]($cfg.MinSep / [Math]::Sqrt($cfg.SymOrder / 2.0))
        # Four rotations converge on the centre far more aggressively than two,
        # so the no-start zone has to be much larger at 90-degree symmetry.
        [MapGen]::MesaCentreClear = [float]$(if ($cfg.SymOrder -eq 4) { 0.30 } else { 0.16 })
        [MapGen]::MesaInflate    = [float]$cfg.Inflate
        [MapGen]::MesaBlurRadius = [float]$cfg.BlurR

        if ($cfg.UseRiver) {
            [MapGen]::ComputeBridgePositions()
            [MapGen]::PlaceBasesAlongRiver($perSector, [float]($Size * 0.16))
        } else {
            [MapGen]::PlaceSpawnsRadial($perSector, [float]$cfg.SpawnRadius, [float]$cfg.Phase)
        }

        # Aim each spawn's lane. Water styles cannot use the map centre: on
        # RiverCrossing it is the channel and on Basin the flooded bowl, so a
        # corridor to the centre is a corridor into water. RiverCrossing aims at
        # its own crossing; Basin aims at the rim, short of the shoreline.
        $ltx = New-Object 'System.Collections.Generic.List[float]'
        $ltz = New-Object 'System.Collections.Generic.List[float]'
        for ($i = 0; $i -lt [MapGen]::BaseX.Length; $i++) {
            $sx = [double][MapGen]::BaseX[$i]; $sz = [double][MapGen]::BaseZ[$i]
            if ($cfg.UseRiver) {
                $nb = [MapGen]::NearestBridge([float]$sx, [float]$sz)
                $ltx.Add([MapGen]::BridgeX[$nb]); $ltz.Add([MapGen]::BridgeZ[$nb])
            }
            elseif ($cfg.BowlDepth -gt 0) {
                $vx = ($Size / 2.0) - $sx; $vz = ($Size / 2.0) - $sz
                $len = [Math]::Sqrt($vx * $vx + $vz * $vz)
                $stop = [Math]::Max(0.0, $len - $Size * [double]$cfg.BowlRadiusFrac * 1.05)
                $ltx.Add([float]($sx + $vx / $len * $stop)); $ltz.Add([float]($sz + $vz / $len * $stop))
            }
            else { $ltx.Add([float]($Size / 2.0)); $ltz.Add([float]($Size / 2.0)) }
        }
        [MapGen]::LaneTargetX = $ltx.ToArray()
        [MapGen]::LaneTargetZ = $ltz.ToArray()

        [MapGen]::BuildMesaField($seedN, [float]$cfg.Tier1, [float]$cfg.Tier2)
        [MapGen]::BuildHeight()

        $bx0 = [MapGen]::BaseX[0]; $bz0 = [MapGen]::BaseZ[0]
        $carved = [MapGen]::CarveRamps($bx0, $bz0, 40, 11.0, 9.0, 120)
        # Sand off invisible one-cell obstacles before anything is measured.
        $despeckled = [MapGen]::SmoothPathingSpecks(60, 8)
        [MapGen]::BuildWalkable()
        $reach = [MapGen]::Reachable($bx0, $bz0)

        # Resources for one sector, then rotated into the rest. Base rings
        # first so nothing else can take the ground around a commander - see
        # src\Resources.cs for where the numbers come from.
        $minRiver = if ($cfg.UseRiver) { [float]($Size * 0.09) } else { 0.0 }
        $budget = [MapGen]::AlloyBudget($Players, $Size)
        $flat = [MapGen]::PlaceResourcesV2($seedN, $reach, $perSector, $budget, 12.0, $minRiver)
        $sectorCount = $flat.Length / 2

        $mexX = New-Object 'System.Collections.Generic.List[float]'
        $mexZ = New-Object 'System.Collections.Generic.List[float]'
        for ($i = 0; $i -lt $sectorCount; $i++) {
            for ($k = 0; $k -lt $cfg.SymOrder; $k++) {
                $ox = 0.0; $oz = 0.0
                [MapGen]::RotateWorld($flat[$i*2], $flat[$i*2+1], $k, [ref]$ox, [ref]$oz)
                $mexX.Add([float]$ox); $mexZ.Add([float]$oz)
            }
        }

        $ev = [MapGen]::Evaluate($reach, $mexX.ToArray(), $mexZ.ToArray())
        $ts = [MapGen]::TerrainStats()
        "  ramps carved {0};  specks smoothed {6};  reachable {1:P0};  open ground {2:P0};  flat {3:P0};  cliff {4:P0};  {5} resource spots" -f `
            $carved, $ev[0], $ev[1], $ev[2], $ev[3], $mexX.Count, $despeckled | Write-Host
        "  relief {0:N1} m .. {1:N1} m;  closest two spawns {2:N0} m apart" -f $ts[0], $ts[1], $ev[6] | Write-Host

        # ---- the gate ----
        $fail = @()
        if ($ev[4] -lt 1)              { $fail += 'a spawn is unreachable overland' }
        if ($ev[5] -lt 1)              { $fail += 'a resource spot is cut off' }
        if ($ev[0] -lt 0.92)           { $fail += ("only {0:P0} of walkable ground connected" -f $ev[0]) }
        if ($ev[1] -lt 0.14)           { $fail += ("largest open area only {0:P0}" -f $ev[1]) }
        if ($ev[2] -lt 0.45)           { $fail += ("only {0:P0} level ground" -f $ev[2]) }
        if ($ev[3] -gt 0.22)           { $fail += ("{0:P0} cliff" -f $ev[3]) }
        # The check that would have caught bare bases. Measured against the
        # shipped maps: worst spawn has a median of 4 alloys inside 20 m, and
        # never fewer than 1. Three is the floor worth playing.
        $nearBase = [MapGen]::MinAlloysNearSpawn($mexX.ToArray(), $mexZ.ToArray(), 20.0)
        if ($nearBase -lt 3)           { $fail += "only $nearBase alloys within 20 m of the barest spawn" }
        if ($mexX.Count -lt $Players * 8) { $fail += "only $($mexX.Count) resource spots placed" }
        $sepTarget = [MapGen]::SpawnSeparationTarget($Players)
        if ($ev[6] -lt $Size * $sepTarget * 0.8) {
            $fail += ("spawns {0:N0} m apart, corpus median for {1}P is {2:N0} m" -f $ev[6], $Players, ($Size * $sepTarget))
        }
        # Lane structure between the two furthest spawns. This is the check the
        # old gate was missing: "largest open area" can score 0.67 while the
        # route between bases is a 4 m corridor, because the open blob is off to
        # one side. Corpus medians: clearance 0.030 of map size in Supreme
        # Commander, 0.038 in the shipped Sanctuary maps; directness 0.93/0.94.
        $li = 0; $lk = 1; $ld = -1.0
        for ($i = 0; $i -lt [MapGen]::BaseX.Length; $i++) {
            for ($k = $i + 1; $k -lt [MapGen]::BaseX.Length; $k++) {
                $dx = [MapGen]::BaseX[$i] - [MapGen]::BaseX[$k]; $dz = [MapGen]::BaseZ[$i] - [MapGen]::BaseZ[$k]
                $dd = [Math]::Sqrt($dx * $dx + $dz * $dz)
                if ($dd -gt $ld) { $ld = $dd; $li = $i; $lk = $k }
            }
        }
        $rs = [MapGen]::RouteStats([MapGen]::BaseX[$li], [MapGen]::BaseZ[$li],
                                   [MapGen]::BaseX[$lk], [MapGen]::BaseZ[$lk])
        "  lane: {0:N1} m wide (median), {1:N0}% direct, {2} chokepoints, {3:P0} overlooked" -f `
            $rs[2], ($rs[1] * 100), [int]$rs[4], $rs[5] | Write-Host

        if ($rs[0] -le 0)                  { $fail += 'no overland route between spawns' }
        elseif ($rs[2] -lt $Size * 0.022)  { $fail += ("lane only {0:N1} m wide, corpus median is {1:N0} m" -f $rs[2], ($Size * 0.030)) }
        # Water styles detour to a crossing by design, so they get their own floor.
        if ($rs[1] -gt 0 -and $rs[1] -lt $cfg.MinDirect) { $fail += ("route only {0:P0} direct, floor is {1:P0}" -f $rs[1], $cfg.MinDirect) }
        if ($rs[5] -gt 0.55)               { $fail += ("{0:P0} of the route is overlooked - a canyon, not a map" -f $rs[5]) }

        $leftover = [MapGen]::PathingSpecks(60)
        if ($leftover[0] -gt 6)       { $fail += ("{0:N0} isolated obstacles in open ground" -f $leftover[0]) }

        if ($fail.Count) {
            Write-Host ("  REJECTED: {0}" -f ($fail -join '; ')) -ForegroundColor Yellow
            continue
        }
        Write-Host "  accepted" -ForegroundColor Green
        $accepted = $true

        # ---------------------------------------------------------- write ----
        $folder = if ($Name -and $Count -eq 1) { $Name -replace '[^\w\-]', '_' }
                  else { "~GEN-{0}P_{1}_{2}_{3}_{4}" -f $Players, $useStyle, $useBiome, $Size, $seedN }
        $display = $folder -replace '^~GEN-', '' -replace '_', ' '

        $mapDir = Join-Path $MapsRoot $folder
        $texDir = Join-Path $mapDir 'Textures'
        if (Test-Path $mapDir) {
            if (-not $Force) { throw "'$mapDir' exists. Pass -Force." }
            Remove-Item $mapDir -Recurse -Force
        }
        $null = New-Item -ItemType Directory -Path $texDir -Force

        [MapGen]::BuildLayers()
        [MapGen]::WriteHeightmap((Join-Path $texDir 'heightmap.raw'))
        [MapGen]::WriteStratums($texDir)
        [MapGen]::WriteTints($texDir, 2048)
        [MapGen]::WritePreview((Join-Path $texDir 'preview.png'), 512, $false, $null, $null, $null)
        Copy-Item (Join-Path $texDir 'preview.png') (Join-Path $mapDir 'preview.png')

        if ($DebugDir) {
            $null = New-Item -ItemType Directory -Path $DebugDir -Force -ErrorAction SilentlyContinue
            $mx = @(); $mz = @(); $mk = @()
            for ($i = 0; $i -lt $mexX.Count; $i++) { $mx += $mexX[$i]; $mz += $mexZ[$i]; $mk += 1 }
            foreach ($i in 0..([MapGen]::BaseX.Length - 1)) { $mx += [MapGen]::BaseX[$i]; $mz += [MapGen]::BaseZ[$i]; $mk += 0 }
            [MapGen]::WritePreview((Join-Path $DebugDir "$folder.png"), 900, $true, [float[]]$mx, [float[]]$mz, [int[]]$mk)
            [MapGen]::WriteHeightPreview((Join-Path $DebugDir "${folder}_elevation.png"), 900)
            [MapGen]::WriteWalkPreview((Join-Path $DebugDir "${folder}_walk.png"), 900, $reach)
        }

        # ---- props ----
        $propGroups = @()
        if (-not $NoProps) {
            $treeBps = @('edbm0121','edbm0122','edbm0123','edbm0124','edbm0125')
            $rockBps = @('edmm0104','edmm0106','edms0110')
            $buckets = @{}; foreach ($b in ($treeBps + $rockBps)) { $buckets[$b] = @() }
            $per = [int]($Size * $Size / 620)
            foreach ($set in @(
                    @{ Bps = $treeBps; Rocks = $false; Count = $per;              Seed = $seedN + 11 },
                    @{ Bps = $rockBps; Rocks = $true;  Count = [int]($per * 0.4); Seed = $seedN + 29 })) {
                $sc = [MapGen]::Scatter($set.Seed, $set.Count, $set.Rocks, $mexX.ToArray(), $mexZ.ToArray(), [float]($Size * 0.035))
                $n = $sc.Length / 5
                for ($k = 0; $k -lt $n; $k++) {
                    $x = [double]$sc[$k*5]; $y = [double]$sc[$k*5+1]; $z = [double]$sc[$k*5+2]
                    $yaw = [double]$sc[$k*5+3]; $s = [double]$sc[$k*5+4]
                    $bp = $set.Bps[$k % $set.Bps.Count]
                    for ($r = 0; $r -lt $cfg.SymOrder; $r++) {
                        $ox = 0.0; $oz = 0.0
                        [MapGen]::RotateWorld([float]$x, [float]$z, $r, [ref]$ox, [ref]$oz)
                        $ry = $yaw + $r * (2 * [math]::PI / $cfg.SymOrder)
                        $buckets[$bp] += ,[ordered]@{
                            position = @{ x = [math]::Round($ox,3); y = [math]::Round($y,3); z = [math]::Round($oz,3) }
                            rotation = @{ x = 0.0; y = [math]::Round([math]::Sin($ry/2),7); z = 0.0; w = [math]::Round([math]::Cos($ry/2),7) }
                            scale    = @{ x = [math]::Round($s,4); y = [math]::Round($s,4); z = [math]::Round($s,4) }
                        }
                    }
                }
            }
            foreach ($b in ($treeBps + $rockBps)) {
                if ($buckets[$b].Count -eq 0) { continue }
                $propGroups += ,[ordered]@{
                    blueprintPath = "Environment/01_Highlands/Props/$b/$b$PropExtension"
                    transforms    = $buckets[$b]
                }
            }
        }

        # ---- json ----
        $bio = Get-Biome $useBiome
        $stratums = New-StratumLayers $useBiome

        function T([double]$X, [double]$Y, [double]$Z) {
            [ordered]@{
                position = @{ x = $X;  y = $Y;  z = $Z }
                rotation = @{ x = 0.0; y = 0.0; z = 0.0; w = 1.0 }
                scale    = @{ x = 0.0; y = 0.0; z = 0.0 }
            }
        }

        $spawnT = [ordered]@{}
        $armies = [ordered]@{}
        for ($i = 0; $i -lt [MapGen]::BaseX.Length; $i++) {
            $ax = [double][MapGen]::SnapBuild([MapGen]::BaseX[$i]); $az = [double][MapGen]::SnapBuild([MapGen]::BaseZ[$i])
            $key = "ARMY_{0}" -f ($i + 1)
            $spawnT[$key] = T $ax ([math]::Round([MapGen]::HeightAtWorld($ax, $az), 2)) $az
            $armies[$key] = [ordered]@{ faction = 0; alloys = 500.0; energy = 500.0; groups = @{} }
        }

        $alloyT = [ordered]@{}
        for ($i = 0; $i -lt $mexX.Count; $i++) {
            $px = [double][MapGen]::SnapBuild([float]$mexX[$i]); $pz = [double][MapGen]::SnapBuild([float]$mexZ[$i])
            $py = [math]::Round([MapGen]::HeightAtWorld($px, $pz), 2)
            $alloyT["Alloys_{0:D3}" -f ($i+1)] = T $px $py $pz
        }

        $map = [ordered]@{
            fileVersion = 3; mapVersion = 1
            name = $display
            credits = "Generated: $useStyle / $useBiome / seed $seedN"
            width = $Size; length = $Size
            height = 128                       # SanMap.height is an int
            heightmapResolution = [MapGen]::HRes
            hasWater = [bool]$cfg.HasWater
            waterLevel = [double]$cfg.WaterLevel
            waterDepth = 2.0; waterWindSpeed = 0.06; waterWindDirection = 100.0
            waterShoreDepthOffset = 8.0; waterShoreDepthStrength = 0.7
            waterShoreDistanceOffset = 0.0; waterShoreDistanceStrength = 2.0
            waveGeneratorBlueprint = ''
            shader = 'RTS/TerrainLit'
            heightTransition = 2.0; fadeDistance = 55.0; fadeStartDistance = 32.0
            stratumLayers = $stratums
            sunRA = [double]$bio.Sun; sunDA = 34.0; sunIntensity = 60000.0
            sunTint = @{ r = 1.0; g = 1.0; b = 1.0; a = 1.0 }
            sunTemperature = [double]$bio.SunTemp
            sunAngularDiameter = 0.5; sunVolumetricsMultiplier = 6.7; sunVolumetricsShadowDimer = 0.5
            skylightIntensity = 0.0
            skylightTint = @{ r = 1.0; g = 1.0; b = 1.0; a = 1.0 }
            skylightTemperature = [double]$bio.Sky
            exposure = [double]$bio.Exposure; exposureCompensation = 0.0; skyboxExposure = 12.0
            fogAttenuationDistance = [double]$bio.Fog
            fogBaseHeight = 6.0; fogMaximumHeight = 140.0; fogMaximumDistance = 1800.0; fogAnisotropy = 0.0
            skybox = @{ path = 'Environment/Skybox/kloofendal_48d_partly_cloudy_puresky_4k.exr' }
            areas = @{ Playable = @{ x = 0.0; y = 0.0; width = [double]$Size; height = [double]$Size } }
            armies = $armies
            chains = @{}
            markers = [ordered]@{
                Spawn  = [ordered]@{ resource = $false; transforms = $spawnT }
                Alloys = [ordered]@{ resource = $true;  transforms = $alloyT }
            }
            decals = @()
            windSpeed = 0.25; windDirection = 160.0
            props = $propGroups
        }


        # Fields the shipped maps set that SanMap would otherwise default badly
        # - most importantly the height fog. See src\Biomes.ps1.
        foreach ($kv in (New-MapEnvironment $useBiome ([double]$cfg.WaterLevel)).GetEnumerator()) {
            $map[$kv.Key] = $kv.Value
        }
        $sanmap = Join-Path $mapDir "$folder.sanmap"
        [IO.File]::WriteAllText($sanmap, ($map | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding $false))
        & (Join-Path $MapGenTools 'Test-Sanmap.ps1') -Path $sanmap -CheckTextures -LuaCheck
        Write-Host ("  -> {0}" -f $mapDir)
    }

    if (-not $accepted) {
        Write-Warning ("no acceptable {0} map after {1} attempts - try another seed or a gentler style" -f $useStyle, $MaxAttempts)
    }
}
