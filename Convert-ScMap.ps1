<#
.SYNOPSIS
    Convert a Supreme Commander: Forged Alliance map into a Sanctuary .sanmap.

.DESCRIPTION
    Terrain and markers are translated; textures and props are not. SupCom's
    stratum masks and prop blueprints reference SupCom assets, so nothing about
    them survives the trip - Sanctuary's own stratum weights and prop sets are
    generated over the imported heightfield instead.

    What does transfer:
      * the heightmap, without resampling (see ScMap.cs for why the two
        fixed-point encodings coincide),
      * water level,
      * Mass and Hydrocarbon markers  -> alloy spots,
      * ARMY_n markers                -> spawns, one army each.

    The result is checked for pathability before it is written, because SupCom's
    slope tolerance is its own and a ramp that works there is not guaranteed to
    work under Sanctuary's 30-degree land limit.

.EXAMPLE
    .\Convert-ScMap.ps1 -Source 'F:\...\maps\SCMP_009' -Force
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Source,
    [string]$MapsRoot = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Maps',
    [string]$Name,
    [ValidateSet('Highlands', 'Tropical', 'Winter', 'Evergreen', 'Arid')]
    [string]$Biome = 'Tropical',
    [ValidateSet('.santp', '.sanprop')]
    [string]$PropExtension = '.santp',
    # SupCom altitudes are metres on the same scale as Sanctuary's, so 1.0 is a
    # straight copy. Raise it if the terrain reads as too flat in game.
    [double]$VerticalScale = 1.0,
    # Repaint with a Sanctuary biome instead of carrying the source textures.
    [switch]$NoSourceTextures,
    # Substitute CC0 ground for the source textures, keeping the same splat and
    # the same rendered colour. Gives every layer its own normal map, avoids the
    # DXT3 that Unity cannot load, and produces a map that is ours to share.
    [switch]$Cc0Textures,
    # The CC0 photos cover a metre or two of real ground with centimetre
    # detail, where the hand-painted originals draw their features for a 4-10m
    # repeat - so at the source tile size the photos read as a dense crinkle.
    # Stretch their repeat and soften their photogrammetry normals; both are
    # judged-by-eye constants, adjustable per conversion.
    [double]$Cc0TileMult = 2.5,
    [double]$Cc0NormalScale = 0.45,
    [string]$ScdPath = 'F:\SteamLibrary\steamapps\common\Supreme Commander Forged Alliance\gamedata\env.scd',
    [switch]$NoProps,
    # Cap on imported props. Source maps carry 6,716 on average and up to
    # 31,042; every one lands in the .sanmap as a position, a quaternion and a
    # scale, so the biggest maps would run to megabytes of JSON. Thinning takes
    # every n-th prop so the spread survives, and the converter says what it
    # dropped - a silent cap reads as "everything came across".
    [int]$MaxProps = 20000,
    # Decal import is opt-in while it stays experimental. The whole chain is
    # proven except the last step: the source decals scan (290 of 299 maps),
    # the transforms land in bounds, the map-local blueprints load through the
    # SanctuaryHud FilesCache fallback, and the game reports no errors - but
    # the RTS/Decals/Default shader renders the result invisibly, and with no
    # shader source and no diagnostics, iterating on material guesses is not
    # worth the time. Revisit when the developers ship maps that carry their
    # own decals and there is a working example to copy.
    [switch]$Decals,
    # Decal cap, same reasoning as -MaxProps. The corpus averages 981 decals a
    # map; the largest carry over ten thousand.
    [int]$MaxDecals = 8000,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'src' 'Import-MapGen.ps1')
. (Join-Path $here 'src' 'Biomes.ps1')

# ------------------------------------------------------------- source ----

$scmapFile = Get-ChildItem $Source -Filter *.scmap | Select-Object -First 1
if (-not $scmapFile) { throw "no .scmap in '$Source'" }
$saveFile     = Get-ChildItem $Source -Filter *_save.lua     | Select-Object -First 1
$scenarioFile = Get-ChildItem $Source -Filter *_scenario.lua | Select-Object -First 1
if (-not $saveFile) { throw "no _save.lua in '$Source' - markers come from there" }

$sc = [MapGen]::ReadScMap($scmapFile.FullName)
$markers = [MapGen]::ReadScMarkers($saveFile.FullName)

# The scenario file carries the human-readable name.
$srcName = $scmapFile.BaseName
if ($scenarioFile) {
    # Lua takes either quote, and mappers use both: Seton's Clutch needs double
    # quotes for its apostrophe, Loki is written with single ones. Matching only
    # double quotes left Loki named after its folder, loki_-_faf_version.
    # Anchored to ScenarioInfo's own indentation. A scenario file has a name per
    # team as well as the map's, and the team's often comes first - 22 of the
    # 300 in the corpus were converting as "FFA". The map's name sits at the top
    # level of the table, the team's is nested several levels deeper.
    $mm = [regex]::Match((Get-Content $scenarioFile.FullName -Raw),
                         '(?m)^\s{0,6}name\s*=\s*(["''])(.*?)\1')
    if ($mm.Success -and $mm.Groups[2].Value.Trim()) { $srcName = $mm.Groups[2].Value.Trim() }
}

$eN = 0.0; $eS = 0.0
$sc.RowZeroIsNorth = [MapGen]::ResolveScRowOrder($sc, $markers, [ref]$eN, [ref]$eS)

"Source: {0}  ({1})" -f $srcName, $scmapFile.Name | Write-Host
"  {0}x{0} cells, height scale 1/{1:N0}, water {2}, shader {3}, version {4}" -f `
    $sc.Size, (1.0 / $sc.HeightScale),
    $(if ($sc.HasWater) { '{0:N1} m' -f $sc.WaterElevation } else { 'none' }),
    $sc.TerrainShader, $sc.VersionMinor | Write-Host
"  row order {0} (mean marker error {1:N2} m vs {2:N2} m the other way)" -f `
    $(if ($sc.RowZeroIsNorth) { 'north-first' } else { 'south-first' }), $eN, $eS | Write-Host

# ---------------------------------------------------------- adoption ----

# 512 makes Sanctuary's raw encoding identical to SupCom's: 65535/512 = 128,
# and SupCom's height scale is 1/128 on every stock map.
$MapHeight = 512
[MapGen]::MaxHeight = [float]$MapHeight
[MapGen]::AdoptScMap($sc, [float]$VerticalScale)
[MapGen]::LandBase = [float][MapGen]::WaterLevel + 5.0

$Size = [int][MapGen]::MapSize
# Splat resolution follows the map, as it does everywhere else.
[MapGen]::SRes = [MapGen]::HRes
"  imported {0} m square, relief {1:N1} m, water level {2:N1} m" -f `
    $Size, [MapGen]::HeightMax(), [MapGen]::WaterLevel | Write-Host

# ----------------------------------------------------------- markers ----

# SupCom z runs the opposite way round from Sanctuary's, so every marker needs
# the same flip the heightmap got.
$mexX = New-Object 'System.Collections.Generic.List[double]'
$mexZ = New-Object 'System.Collections.Generic.List[double]'
$spawns = @()

foreach ($k in $markers) {
    $x = [double]$k.X
    $z = [double][MapGen]::ScMarkerZ($sc, [float]$k.Z)
    if ($x -lt 0 -or $x -gt $Size -or $z -lt 0 -or $z -gt $Size) { continue }

    if ($k.Name -match '^ARMY_\d+$') {
        $spawns += ,@{ Name = $k.Name; X = $x; Z = $z }
    }
    elseif ($k.Type -eq 'Mass' -or $k.Type -eq 'Hydrocarbon') {
        $mexX.Add($x); $mexZ.Add($z)
    }
}
$spawns = $spawns | Sort-Object { [int]($_.Name -replace '\D', '') }

"  markers: {0} spawns, {1} resource spots" -f $spawns.Count, $mexX.Count | Write-Host
if ($spawns.Count -lt 2) { throw "only $($spawns.Count) spawn marker(s); not a skirmish map" }

# Do the terrain fold and the marker mapping agree? A mirrored import is
# self-consistent everywhere else - reachability, slope, spawn placement all
# look healthy - so this is the only cheap way to catch it.
$fit = [MapGen]::ScMarkerFit($sc, $markers)
"  marker fit against imported terrain: {0:N2} m mean error" -f $fit | Write-Host
# 6.0, not 3.0. The census showed nine maps refused at 3.2-5.2 m, and the list
# gave the cause away: Seton's Clutch FAF version fails at 3.9 m while the
# stock map passes at 0.47 m on the same terrain. Community-edited maps carry
# stale marker heights - mappers move markers in 2D and the Y is never
# refreshed - so a few metres of error is noise, not a wrong fold. An actual
# mirrored import measures 12 m and up (Seton's the wrong way round is 12.2),
# which 6.0 still catches with margin.
if ($fit -gt 6.0) {
    throw ("markers sit {0:N1} m off the terrain on average - the heightmap fold and the marker mapping disagree" -f $fit)
}

# -------------------------------------------------------- validation ----

# This is the part that cannot be assumed. SupCom's own slope rules are not
# Sanctuary's, so measure rather than hope.
Write-Host 'Checking pathability against the 30-degree land limit...'
[MapGen]::BuildWalkable()
$walk = [MapGen]::WalkableCount()
$reach = [MapGen]::Reachable([float]$spawns[0].X, [float]$spawns[0].Z)
$rc = [MapGen]::CountTrue($reach)
$ts = [MapGen]::TerrainStats()      # { lo, hi, land, flat, gentle, steep, cliff }
$steep = ($ts[5] + $ts[6]) / [Math]::Max(1, $ts[2])
$og = [MapGen]::OpenGroundStats(6.0)

# Group the spawns by landmass, so an archipelago reads as what it is. The
# old single number - spawn 1's component as a share of all walkable ground -
# could not tell a six-island naval map from a broken import: SCMP_024 sat at
# "9% reachable" for weeks and turned out to be six spawn islands of 9% each,
# working exactly as its author intended.
$hres = [int][MapGen]::HRes
$cellStep = [double]$Size / ($hres - 1)
$groups = @()      # each: @{ Count = component cell count; Spawns = [list] }
foreach ($s in $spawns) {
    $placed = $false
    foreach ($g in $groups) {
        $r = [int][math]::Round(($Size - [double]$s.Z) / $cellStep)
        $cc = [int][math]::Round([double]$s.X / $cellStep)
        $r = [math]::Max(0, [math]::Min($hres - 1, $r)); $cc = [math]::Max(0, [math]::Min($hres - 1, $cc))
        if ($g.Mask[$r, $cc]) { $g.Spawns += $s.Name; $placed = $true; break }
    }
    if (-not $placed) {
        $m = [MapGen]::Reachable([float]$s.X, [float]$s.Z)
        $groups += @{ Mask = $m; Count = [MapGen]::CountTrue($m); Spawns = @($s.Name) }
    }
}
$landFrac = $ts[2] / [double]($hres * $hres)

"  land {0:P0} of map;  over the slope limit {1:P0} of land;  open ground {2:P0}" -f `
    $landFrac, $steep, ($og[0] / [Math]::Max(1, $og[1])) | Write-Host
if ($groups.Count -eq 1) {
    "  all {0} spawns share one landmass ({1:P0} of walkable ground)" -f `
        $spawns.Count, ($groups[0].Count / [math]::Max(1, $walk)) | Write-Host
}
else {
    "  archipelago: {0} spawns across {1} landmasses ({2})" -f `
        $spawns.Count, $groups.Count, `
        (($groups | ForEach-Object { '{0:p0}' -f ($_.Count / [math]::Max(1, $walk)) }) -join ', ') | Write-Host
}


$cutSpawn = 0
foreach ($s in $spawns) { if (-not [MapGen]::IsReachable($reach, [float]$s.X, [float]$s.Z)) { $cutSpawn++ } }
$cutMex = 0
for ($i = 0; $i -lt $mexX.Count; $i++) {
    if (-not [MapGen]::IsReachable($reach, [float]$mexX[$i], [float]$mexZ[$i])) { $cutMex++ }
}
if ($cutSpawn -gt 0) { Write-Host ("  WARNING: {0} spawn(s) not reachable from spawn 1" -f $cutSpawn) -ForegroundColor Yellow }
if ($cutMex -gt 0) { Write-Host ("  note: {0} of {1} resource spots sit off the main landmass (islands or water)" -f $cutMex, $mexX.Count) -ForegroundColor Yellow }

# --------------------------------------------------------- write out ----

$folder = if ($Name) { $Name } else { '~SC-' + ($srcName -replace "[^\w]+", '_') }
# -Name picks the folder, not the map's identity. Overriding the display name
# with it produced "~SC-Badlands CC0" where the same map converted the ordinary
# way is "8 - Badlands_v4", which is the only structural difference between a
# CC0 build and a working source build of the same map.
$display = $srcName

$mapDir = Join-Path $MapsRoot $folder
$texDir = Join-Path $mapDir 'Textures'
if (Test-Path $mapDir) {
    if (-not $Force) { throw "'$mapDir' exists. Pass -Force." }
    Remove-Item $mapDir -Recurse -Force
}
$null = New-Item -ItemType Directory -Path $texDir -Force

# ---- textures ----
#
# Carry the map's own texture set across rather than repainting it with one of
# our five biomes. A map can reference assets in its own folder - Data.PathToID
# rewrites "map/..." to "Maps/<this map>/..." - and the texture lookup strips
# the extension and probes .dds first, so Supreme Commander's .dds files drop in
# behind .tga paths unchanged.
#
# Falls back to the generated biome if anything is unavailable: a map that looks
# generic beats one that fails to convert.
$srcTextures = $null
if (-not $NoSourceTextures) {
    $scBytes = [IO.File]::ReadAllBytes($scmapFile.FullName)
    $texSet = [MapGen]::ScanScTextures($scBytes, $sc.Size)
    if ($texSet -and [MapGen]::AdoptScSplat($scBytes, $texSet)) {
        $exp = if ($Cc0Textures) {
            & (Join-Path $here 'tools\Export-Cc0Textures.ps1') `
                -TexturePaths $texSet.Paths -DestDir $texDir -Quiet
        }
        else {
            & (Join-Path $here 'tools\Export-ScTextures.ps1') -ScdPath $ScdPath `
                -TexturePaths $texSet.Paths -NormalPaths $texSet.NormalPaths -DestDir $texDir -Quiet `
                -MapsRoot (Split-Path -Parent $scmapFile.Directory.FullName)
        }
        if ($exp.Copied -gt 0) {
            $srcTextures = [pscustomobject]@{ Set = $texSet; Export = $exp }
            "  textures: {0} source layers, {1} files copied, splat {2} -> {3}{4}" -f `
                $texSet.UsedLayers, $exp.Copied, $texSet.MaskSize, [MapGen]::SRes, `
                $(if ([MapGen]::DroppedLayers) { ", {0} unassigned layer(s) zeroed" -f [MapGen]::DroppedLayers }) + $(if ($exp.Transcoded) { ", {0} DXT3 -> DXT5" -f $exp.Transcoded }) + $(if ($Cc0Textures) { ", CC0 substitutes ({0} inexact role)" -f $exp.Inexact }) | Write-Host
            $cov = [MapGen]::SplatCoverage()
            for ($i = 1; $i -le 8; $i++) {
                if ($cov[$i] -le 0.0) { continue }
                "    L{0} {1,-34} {2,5:p1}" -f $i, [IO.Path]::GetFileName($texSet.Paths[$i]), $cov[$i] | Write-Host
            }
        }
    }
    if (-not $srcTextures) { "  textures: source set unavailable, falling back to the {0} biome" -f $Biome | Write-Host }
}
if (-not $srcTextures) { [MapGen]::BuildLayers() }

[MapGen]::WriteHeightmap((Join-Path $texDir 'heightmap.raw'))
[MapGen]::WriteStratums($texDir)
[MapGen]::WriteTints($texDir, 2048)
[MapGen]::WritePreview((Join-Path $texDir 'preview.png'), 512, $false, $null, $null, $null)
Copy-Item (Join-Path $texDir 'preview.png') (Join-Path $mapDir 'preview.png')

# Round-trip check: with MaxHeight 512 and a vertical scale of 1 the raw we
# write should be the raw SupCom stored, give or take a rounding unit. If it is
# not, the encoding assumption is wrong and everything downstream is suspect.
if ($VerticalScale -eq 1.0) {
    $back = [IO.File]::ReadAllBytes((Join-Path $texDir 'heightmap.raw'))
    $n = $sc.Size + 1
    $worst = 0; $sum = 0.0
    for ($r = 0; $r -lt $n; $r += 7) {
        for ($c = 0; $c -lt $n; $c += 7) {
            $srcRow = $(if ($sc.RowZeroIsNorth) { $r } else { $n - 1 - $r })
            $src = $sc.Raw[$srcRow, $c]
            $i = ($r * $n + $c) * 2
            $got = [int]$back[$i] -bor ([int]$back[$i + 1] -shl 8)
            $d = [Math]::Abs($got - $src)
            if ($d -gt $worst) { $worst = $d }
            $sum += $d
        }
    }
    "  heightmap round trip: worst {0} raw unit(s) ({1:N3} m), mean {2:N3}" -f `
        $worst, ($worst * $sc.HeightScale), ($sum / [Math]::Max(1, [Math]::Pow([Math]::Ceiling($n / 7.0), 2))) | Write-Host
}

# ---- props ----

$propGroups = @()
# The Sanctuary prop palette, classified by eye from rendered silhouettes of
# every model the game ships (see docs\san-prop-kinds notes). Only props whose
# blueprint exists in BOTH builds are usable - the editor pack has no .sanprop
# for the whole WhiteDesert set, so desert maps use the dead snag and the
# gnarly small trees instead, which reads right for FA deserts anyway.
$SanTreesLarge   = @('edbm0121','edbm0122','edbm0123','edbm0141','edbm0143','edbm0144','edbm0145','edbm0146','edbm0201')
$SanTreesSmall   = @('edbm0101','edbm0103','edbm0104','edbm0105','edbm0106','edbm0124','edbm0125','edbm0147','edbm0150')
$SanTreesMixed   = $SanTreesLarge + $SanTreesSmall
$SanTreesConifer = @('edbm0401','edbm0402')
$SanTreesDry     = @('edbm0161','edbm0148','edbm0149','edbm0150')
$SanRocksMed     = @('edmm0101','edmm0102','edmm0103','edmm0104','edmm0105','edmm0106','edmm0107','edmm0108')
$SanRocksDark    = @('edmm0110','edmm0111','edmm0112','edmm0113','edml0111')
$SanRocksOlive   = @('edmm0201','edmm0202','edmm0203','edmm0204','edml0201')
$SanRocksSmall   = @('edms0101','edms0102','edms0103','edms0104','edms0105','edms0110','edms0111','edms0112')
$SanLogs         = @('edbs0112','edbs0113','edbs0115','edbs0116')

# Most props live under Highlands; the exceptions are few enough to list.
$SanPropEnv = @{}
foreach ($n in 'edbm0201','edml0201','edmm0201','edmm0202','edmm0203','edmm0204') { $SanPropEnv[$n] = 'Environment/02_Evergreen' }
foreach ($n in 'edbm0401','edbm0402') { $SanPropEnv[$n] = 'Environment/04_Baikal' }
function Get-SanPropEnv([string]$n) { if ($SanPropEnv.ContainsKey($n)) { $SanPropEnv[$n] } else { 'Environment/01_Highlands' } }

# Pick a Sanctuary prop for one source prop: environment family from the
# blueprint path, kind from the classifier, size hints from the name. Each
# list round-robins so neighbouring props vary.
$script:SanRR = @{}
function Get-SanProp($p) {
    $bp = $p.Blueprint.ToLowerInvariant()
    $fam = ''
    $seg = $bp -split '/'
    if ($seg.Count -gt 2) { $fam = $seg[2] }

    if ($p.Kind -eq 2) {
        $list = if ($bp -match '/logs/') { $SanLogs }
                elseif ($bp -match 'sm\d|small|pebble|fieldstone') { $SanRocksSmall }
                elseif ($fam -in 'desert','red barrens','redrocks','lava','geothermal') { $SanRocksDark }
                elseif ($fam -in 'tropical','swamp','paradise') { $SanRocksOlive }
                else { $SanRocksMed }
    }
    elseif ($fam -in 'tundra','crystalline','crystalline-alt') { $list = $SanTreesConifer }
    elseif ($fam -in 'desert','red barrens','redrocks','lava','geothermal') { $list = $SanTreesDry }
    elseif ($p.Kind -eq 1) { $list = $SanTreesLarge }
    else { $list = $SanTreesMixed }

    $key = [string]$list.Count + $list[0]
    if (-not $script:SanRR.ContainsKey($key)) { $script:SanRR[$key] = 0 }
    $i = $script:SanRR[$key]; $script:SanRR[$key] = $i + 1
    return $list[$i % $list.Count]
}

# The generated-scatter fallback keeps a fixed temperate set.
$treeBps = @('edbm0121', 'edbm0122', 'edbm0123', 'edbm0124', 'edbm0125')
$rockBps = @('edmm0104', 'edmm0106', 'edms0110')

# Carry the source map's own props if they can be read. The author placed them;
# a scatter of our own is a different map wearing the same terrain. See
# src\ScPropImport.cs for how blueprints classify.
$importedProps = $null
if (-not $NoProps) {
    $scProps = [MapGen]::ScanScProps([IO.File]::ReadAllBytes($scmapFile.FullName))
    if ($null -ne $scProps -and $scProps.Count -gt 0) {
        $conv  = [MapGen]::ConvertScProps($scProps, $sc, [float]$VerticalScale)
        $found = $conv.Count
        $kept  = [MapGen]::ThinProps($conv, $MaxProps)
        $buckets = @{}; $skipped = 0; $groups = 0

        foreach ($p in $kept) {
            if ($p.Kind -eq 3) { $skipped++; continue }
            $bp = Get-SanProp $p
            if (-not $buckets.ContainsKey($bp)) { $buckets[$bp] = @() }

            # A Supreme Commander "group" prop is one placed object whose mesh
            # holds several trees. Sanctuary has no equivalent, and inventing
            # extra positions would be guessing, so it becomes one tree scaled
            # up - fewer trunks than the original, in the right places.
            $gs = 1.0
            if ($p.Kind -eq 1) { $gs = 1.35; $groups++ }

            # Source scale is relative to a model we are not using, so treat it
            # as variation rather than truth and keep it inside a sane band.
            $sx = [math]::Max(0.5, [math]::Min(2.0, [double]$p.ScaleX)) * $gs
            $sy = [math]::Max(0.5, [math]::Min(2.0, [double]$p.ScaleY)) * $gs
            $sz = [math]::Max(0.5, [math]::Min(2.0, [double]$p.ScaleZ)) * $gs

            $buckets[$bp] += , [ordered]@{
                position = @{ x = [math]::Round([double]$p.X, 3); y = [math]::Round([double]$p.Y, 3); z = [math]::Round([double]$p.Z, 3) }
                rotation = @{ x = 0.0; y = [math]::Round([math]::Sin($p.Yaw / 2), 7); z = 0.0; w = [math]::Round([math]::Cos($p.Yaw / 2), 7) }
                scale    = @{ x = [math]::Round($sx, 4); y = [math]::Round($sy, 4); z = [math]::Round($sz, 4) }
            }
        }

        $placed = ($buckets.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
        if ($placed -gt 0) {
            foreach ($b in ($buckets.Keys | Sort-Object)) {
                if ($buckets[$b].Count -eq 0) { continue }
                $propGroups += , [ordered]@{
                    blueprintPath = "$(Get-SanPropEnv $b)/Props/$b/$b$PropExtension"
                    transforms    = $buckets[$b]
                }
            }
            $importedProps = $placed
            "  props: {0:n0} of {1:n0} source props placed{2}{3}{4}" -f `
                $placed, $found, `
                $(if ($kept.Count -lt $found) { ", thinned from {0:n0} by -MaxProps" -f $found }), `
                $(if ($skipped) { ", {0:n0} unclassified skipped" -f $skipped }), `
                $(if ($groups) { ", {0:n0} tree groups became single trees" -f $groups }) | Write-Host
        }
    }
    if ($null -eq $importedProps) {
        "  props: source prop table unreadable, scattering our own" | Write-Host
    }
}

if (-not $NoProps -and $null -eq $importedProps) {
    # The imported terrain has whatever symmetry the original map had, and we do
    # not mirror props the way the generated maps do, so scatter over the whole
    # map rather than the half Scatter defaults to.
    [MapGen]::ScatterHalfOnly = $false
    $buckets = @{}; foreach ($b in ($treeBps + $rockBps)) { $buckets[$b] = @() }
    $per = [int]($Size * $Size / 620)
    foreach ($set in @(
            @{ Bps = $treeBps; Rocks = $false; Count = $per; Seed = 4111 },
            @{ Bps = $rockBps; Rocks = $true; Count = [int]($per * 0.4); Seed = 8221 })) {
        $sct = [MapGen]::Scatter($set.Seed, $set.Count, $set.Rocks, $mexX.ToArray(), $mexZ.ToArray(), [float]($Size * 0.02))
        $cnt = $sct.Length / 5
        for ($k = 0; $k -lt $cnt; $k++) {
            $bp = $set.Bps[$k % $set.Bps.Count]
            $yaw = [double]$sct[$k * 5 + 3]; $s = [double]$sct[$k * 5 + 4]
            $buckets[$bp] += , [ordered]@{
                position = @{ x = [math]::Round([double]$sct[$k * 5], 3); y = [math]::Round([double]$sct[$k * 5 + 1], 3); z = [math]::Round([double]$sct[$k * 5 + 2], 3) }
                rotation = @{ x = 0.0; y = [math]::Round([math]::Sin($yaw / 2), 7); z = 0.0; w = [math]::Round([math]::Cos($yaw / 2), 7) }
                scale    = @{ x = [math]::Round($s, 4); y = [math]::Round($s, 4); z = [math]::Round($s, 4) }
            }
        }
    }
    foreach ($b in ($treeBps + $rockBps)) {
        if ($buckets[$b].Count -eq 0) { continue }
        $propGroups += , [ordered]@{
            blueprintPath = "Environment/01_Highlands/Props/$b/$b$PropExtension"
            transforms    = $buckets[$b]
        }
    }
}

# ---- decals ----

# The source map's decals - roads, craters, mud, erosion - carried as
# map-local blueprints. All three Sanctuary decal files are JSON, so the
# author's own textures come across exactly; no substitution table exists or
# is needed. See tools\Export-ScDecals.ps1.
$decalGroups = @()
if ($Decals -and -not $Cc0Textures) {
    $scDecals = [MapGen]::ScanScDecals([IO.File]::ReadAllBytes($scmapFile.FullName), $texSet)
    if ($null -ne $scDecals -and $scDecals.Count -gt 0) {
        # Types 1 and 2 are albedo and normals; 0 and 4 are rare and unknown
        # (186 of 284,626 across the corpus), so they are dropped and counted.
        $usable = @($scDecals | Where-Object { ($_.Type -eq 1 -or $_.Type -eq 2) -and $_.Texture })
        $dropped = $scDecals.Count - $usable.Count

        if ($usable.Count -gt $MaxDecals) {
            $step = $usable.Count / [double]$MaxDecals
            $thinned = New-Object System.Collections.Generic.List[object]
            for ($di = 0.0; $di -lt $usable.Count -and $thinned.Count -lt $MaxDecals; $di += $step) {
                $thinned.Add($usable[[int]$di])
            }
            $usable = @($thinned)
        }

        $distinct = @{}
        foreach ($d in $usable) {
            $k = $d.Texture.ToLowerInvariant()
            if (-not $distinct.ContainsKey($k)) { $distinct[$k] = @{ Path = $d.Texture; Type = $d.Type } }
        }
        $dexp = & (Join-Path $here 'tools\Export-ScDecals.ps1') `
            -Entries @($distinct.Values) -DestDir (Join-Path $mapDir 'Decals') -ScdPath $ScdPath `
            -MapsRoot (Split-Path -Parent $scmapFile.Directory.FullName) -Quiet

        $byBp = @{}
        $h = 0.7071067811865476
        foreach ($d in $usable) {
            $bp = $dexp.Blueprints[$d.Texture]
            if (-not $bp) { continue }
            $x = [double]$d.X
            $z = [double][MapGen]::ScMarkerZ($sc, [float]$d.Z)
            if ($x -lt 0 -or $x -gt $Size -or $z -lt 0 -or $z -gt $Size) { continue }

            # A Sanctuary decal is a downward projector: the shipped rotation
            # decodes as Ry(yaw) * Rx(90deg), quaternion (h*cos, h*sin, -h*sin,
            # h*cos). The z flip negates yaw, as it does for props.
            $yaw = -[double]$d.RotY
            $cy = [math]::Cos($yaw / 2); $sy = [math]::Sin($yaw / 2)

            # After the 90-degree pitch the projector's local x/y span the
            # ground and local z is the projection depth. Depth uses the
            # footprint's larger side so the projection always reaches the
            # terrain across its own extent.
            $sx = [math]::Abs([double]$d.ScaleX); $sz = [math]::Abs([double]$d.ScaleZ)
            if ($sx -le 0.01) { $sx = 1.0 }
            if ($sz -le 0.01) { $sz = $sx }

            if (-not $byBp.ContainsKey($bp)) { $byBp[$bp] = New-Object System.Collections.Generic.List[object] }
            $byBp[$bp].Add([ordered]@{
                position = @{ x = [math]::Round($x, 3); y = [math]::Round([double]$d.Y * $VerticalScale, 3); z = [math]::Round($z, 3) }
                rotation = @{ x = [math]::Round($h * $cy, 7); y = [math]::Round($h * $sy, 7); z = [math]::Round(-$h * $sy, 7); w = [math]::Round($h * $cy, 7) }
                scale    = @{ x = [math]::Round($sx, 3); y = [math]::Round($sz, 3); z = [math]::Round([math]::Max($sx, $sz), 3) }
            })
        }
        foreach ($bp in $byBp.Keys | Sort-Object) {
            # .ToArray, not @( ): wrapping a generic List in @( ) inside a
            # hashtable literal throws "Argument types do not match" - a
            # PowerShell binder quirk that the same expression outside a
            # literal does not hit.
            $decalGroups += , [ordered]@{ blueprintPath = $bp; transforms = $byBp[$bp].ToArray() }
        }
        $placedD = ($byBp.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
        "  decals: {0:n0} of {1:n0} placed across {2} textures{3}{4}" -f `
            $placedD, $scDecals.Count, $byBp.Count, `
            $(if ($dropped) { ", {0} of unknown type dropped" -f $dropped }), `
            $(if ($dexp.Missing.Count) { ", {0} textures unresolved" -f $dexp.Missing.Count }) | Write-Host
    }
    elseif ($null -eq $scDecals) { "  decals: table unreadable, none imported" | Write-Host }
}
elseif ($Decals -and $Cc0Textures) {
    "  decals: skipped in CC0 mode (source decal art has no substitutes)" | Write-Host
}

# ---- stratums ----

# Lighting and fog still come from the biome; only the ground textures are the
# source map's.
$bio = Get-Biome $Biome

if ($srcTextures) {
    # Layer 0 is Sanctuary's base, showing wherever nothing is painted. Supreme
    # Commander's layer 1 plays the same role, so it goes in both slots: the
    # splat's own layer-1 channel paints it where the author put it, and it
    # backs the rest of the map besides.
    $set = $srcTextures.Set
    $names = $srcTextures.Export.Names
    $maskRef = "map/Textures/$($srcTextures.Export.MaskName)"

    # One shared normal for all layers. Supreme Commander keeps four across the
    # eight and does not record which belongs to which, so pick the first that
    # is actually a normal map - the block also carries the macro texture, and
    # taking entry zero blindly put macrotexture000_albedo on every layer.
    $normalRef = $maskRef
    foreach ($n in $set.NormalPaths) {
        if ($n -and $n -match '_normal' -and $names.ContainsKey($n)) { $normalRef = "map/Textures/$($names[$n])"; break }
    }
    # The CC0 path has a real normal per layer, so the shared one above is only
    # the fallback there.
    $perLayerNormals = $srcTextures.Export.Normals
    $perLayerRemaps  = $srcTextures.Export.Remaps
    $perLayerMasks   = $srcTextures.Export.Masks
    # Unused slots borrow the base layer's normal rather than the mask, so a
    # slot that somehow keeps a weight still shades like ground.
    if ($perLayerNormals -and $set.Paths[0] -and $perLayerNormals.ContainsKey($set.Paths[0])) {
        $normalRef = "map/Textures/$($perLayerNormals[$set.Paths[0]])"
    }

    # Slot 0, the base that shows wherever nothing is painted, doubles as the
    # stand-in for any layer the source left unassigned.
    $baseRef = $null
    if ($set.Paths[0] -and $names.ContainsKey($set.Paths[0])) { $baseRef = "map/Textures/$($names[$set.Paths[0]])" }

    $stratums = @()
    for ($li = 0; $li -lt 9; $li++) {
        # Both games index the same way: entry 0 is the base that shows where
        # nothing is painted, and entries 1..n are the masked layers, in the
        # order the two splat images pack them.
        #
        # Putting entry 0 in both slot 0 and slot 1 shifted every layer by one,
        # which showed up as the base texture covering 88% of the map and the
        # rock layer covering none. SCMP_016 confirms the alignment: five named
        # entries, one base plus four masked, and exactly the four channels of
        # the low mask in use.
        $srcIdx = $li
        # Paths now carries the file's true layout: 0 = LowerStratum (the
        # base), 1..8 the masked strata - so every Sanctuary slot has a source
        # entry and stratum 8 is no longer dropped.
        $p = if ($srcIdx -le 8) { $set.Paths[$srcIdx] } else { '' }
        if (-not $p -or -not $names.ContainsKey($p)) {
            # An unused layer still needs an entry. AdoptScSplat has zeroed its
            # weight, so what it points at is never drawn - but point it at the
            # base texture rather than the flat neutral mask, so that if a
            # weight ever does survive the result is a plausible ground rather
            # than the featureless grey wash that gave this away the first time.
            $albedoRef = if ($baseRef) { $baseRef } else { $maskRef }
            $tile = 10.0
        }
        else {
            $albedoRef = "map/Textures/$($names[$p])"
            # Supreme Commander's textureScale and Sanctuary's tileSize are both
            # metres per repeat, and the two games share a coordinate scale, so
            # carry it straight over. Untested - if the ground reads too coarse
            # or too fine this is the number to change.
            $tile = [double]$set.Scales[$srcIdx]
            if ($tile -lt 1.0) { $tile = 10.0 }
            if ($Cc0Textures) { $tile = $tile * $Cc0TileMult }
        }
        $layerNormal = $normalRef
        if ($perLayerNormals -and $p -and $perLayerNormals.ContainsKey($p)) {
            $layerNormal = "map/Textures/$($perLayerNormals[$p])"
        }
        # The corrected texture-block layout pairs normals[0..8] with layers
        # 0..8, so source-texture maps get the author's own normal per layer
        # instead of one shared guess. Only when it was actually exported.
        elseif ($srcIdx -le 8 -and $set.NormalPaths[$srcIdx] -and
                $set.NormalPaths[$srcIdx] -match '_normal' -and
                $names.ContainsKey($set.NormalPaths[$srcIdx])) {
            $layerNormal = "map/Textures/$($names[$set.NormalPaths[$srcIdx]])"
        }
        $layerMask = $maskRef
        if ($perLayerMasks -and $p -and $perLayerMasks.ContainsKey($p)) {
            $layerMask = "map/Textures/$($perLayerMasks[$p])"
        }
        $layerRemap = @(0.37, 0.35, 0.32)
        if ($perLayerRemaps -and $p -and $perLayerRemaps.ContainsKey($p)) {
            $layerRemap = $perLayerRemaps[$p]
        }
        elseif ($perLayerRemaps -and $set.Paths[0] -and $perLayerRemaps.ContainsKey($set.Paths[0])) {
            # An unused slot borrows the base layer's remap along with its
            # albedo, so if a weight ever survives it renders as more base
            # ground rather than the same texture at a different exposure.
            $layerRemap = $perLayerRemaps[$set.Paths[0]]
        }
        $stratums += , [ordered]@{
            name                 = $null
            albedo               = @{ path = $albedoRef }
            normal               = @{ path = $layerNormal }
            mask                 = @{ path = $layerMask }
            tileSize             = @{ x = $tile; y = $tile }
            tileSizeFar          = @{ x = [double]($tile * 6.0); y = [double]($tile * 6.0) }
            tileSizeTriplanar    = 12.0
            tileSizeFarTriplanar = 36.0
            normalScale          = $(if ($Cc0Textures) { $Cc0NormalScale } else { 1.0 })
            normalScaleFar       = $(if ($Cc0Textures) { $Cc0NormalScale } else { 1.0 })
            normalFarNearBlend   = 0.3; heightFarNearBlend = 0.5
            # These textures are not in the measured tone table, so start at the
            # shipped average rather than pretending to normalise them.
            diffuseRemap         = @{ r = $layerRemap[0]; g = $layerRemap[1]; b = $layerRemap[2]; a = 1.0 }
            farColorRemap        = @{ r = 1.0; g = 1.0; b = 1.0; a = 0.0 }
            maskRemapMin         = @{ x = 0.0; y = 0.0; z = 0.0; w = 0.0 }
            maskRemapMax         = @{ x = 1.0; y = 1.0; z = 1.0; w = 1.0 }
        }
    }
}
else {
    $stratums = New-StratumLayers $Biome
}

# ---- json ----

function T([double]$X, [double]$Y, [double]$Z) {
    [ordered]@{
        position = @{ x = $X; y = $Y; z = $Z }
        rotation = @{ x = 0.0; y = 0.0; z = 0.0; w = 1.0 }
        scale    = @{ x = 0.0; y = 0.0; z = 0.0 }
    }
}

$spawnT = [ordered]@{}
$armies = [ordered]@{}
for ($i = 0; $i -lt $spawns.Count; $i++) {
    $ax = [double][MapGen]::SnapBuild([float]$spawns[$i].X); $az = [double][MapGen]::SnapBuild([float]$spawns[$i].Z)
    $key = "ARMY_{0}" -f ($i + 1)
    $spawnT[$key] = T $ax ([math]::Round([MapGen]::HeightAtWorld($ax, $az), 2)) $az
    $armies[$key] = [ordered]@{ faction = 0; alloys = 500.0; energy = 500.0; groups = @{} }
}

# Alloy spots draw their own decal.
#
# resourceSpotTemplateLoader.lua attaches a DecalTemplate for
# Environment/Common/Decals/alloy_spot.sandecal to every resource spot the game
# creates, so a decal in the .sanmap is a second copy of the same texture at a
# different scale sitting on top of the first. That is what made the spots look
# smeared and off-centre. The developers' own generated maps carry no decals
# array at all.
$alloyT = [ordered]@{}
for ($i = 0; $i -lt $mexX.Count; $i++) {
    $px = [double][MapGen]::SnapBuild([float]$mexX[$i]); $pz = [double][MapGen]::SnapBuild([float]$mexZ[$i])
    $py = [math]::Round([MapGen]::HeightAtWorld($px, $pz), 2)
    $alloyT["Alloys_{0:D3}" -f ($i + 1)] = T $px $py $pz
}

$map = [ordered]@{
    fileVersion              = 3; mapVersion = 1
    name                     = $display
    credits                  = "Converted from Supreme Commander: Forged Alliance - $($scmapFile.Name)"
    width                    = $Size; length = $Size
    height                   = $MapHeight
    heightmapResolution      = [MapGen]::HRes
    hasWater                 = [bool]$sc.HasWater
    waterLevel               = [double][MapGen]::WaterLevel
    # How far below the surface the water reads as fully deep. The source
    # records exactly this as its "deep" elevation; Seton's comes out at 2.5,
    # the shipped maps use 2 - same idea, same scale.
    waterDepth               = [math]::Round([math]::Max(1.0, [math]::Min(8.0, [double]$sc.WaterElevation - [double]$sc.WaterElevationDeep)), 2)
    waterWindSpeed = 0.06; waterWindDirection = 100.0
    waterShoreDepthOffset    = 8.0; waterShoreDepthStrength = 0.7
    waterShoreDistanceOffset = 0.0; waterShoreDistanceStrength = 2.0
    waveGeneratorBlueprint   = ''
    shader                   = 'RTS/TerrainLit'
    heightTransition         = 2.0; fadeDistance = 55.0; fadeStartDistance = 32.0
    stratumLayers            = $stratums
    sunRA                    = [double]$bio.Sun; sunDA = 34.0; sunIntensity = 60000.0
    sunTint                  = @{ r = 1.0; g = 1.0; b = 1.0; a = 1.0 }
    sunTemperature           = [double]$bio.SunTemp
    sunAngularDiameter       = 0.5; sunVolumetricsMultiplier = 6.7; sunVolumetricsShadowDimer = 0.5
    skylightIntensity        = 0.0
    skylightTint             = @{ r = 1.0; g = 1.0; b = 1.0; a = 1.0 }
    skylightTemperature      = [double]$bio.Sky
    exposure                 = [double]$bio.Exposure; exposureCompensation = 0.0; skyboxExposure = 12.0
    fogAttenuationDistance   = [double]$bio.Fog
    fogBaseHeight            = 6.0; fogMaximumHeight = 140.0; fogMaximumDistance = 1800.0; fogAnisotropy = 0.0
    skybox                   = @{ path = 'Environment/Skybox/kloofendal_48d_partly_cloudy_puresky_4k.exr' }
    areas                    = @{ Playable = @{ x = 0.0; y = 0.0; width = [double]$Size; height = [double]$Size } }
    armies                   = $armies
    chains                   = @{}
    markers                  = [ordered]@{
        Spawn  = [ordered]@{ resource = $false; transforms = $spawnT }
        Alloys = [ordered]@{ resource = $true; transforms = $alloyT }
    }
    decals                   = $decalGroups
    windSpeed                = 0.25; windDirection = 160.0
    props                    = $propGroups
}


# Fields the shipped maps set that SanMap would otherwise default badly - most
# importantly the height fog. See src\Biomes.ps1.
foreach ($kv in (New-MapEnvironment $Biome ([double][MapGen]::WaterLevel)).GetEnumerator()) {
    $map[$kv.Key] = $kv.Value
}
$sanmap = Join-Path $mapDir "$folder.sanmap"
[IO.File]::WriteAllText($sanmap, ($map | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding $false))
& (Join-Path $MapGenTools 'Test-Sanmap.ps1') -Path $sanmap -CheckTextures -LuaCheck
Write-Host ("  -> {0}" -f $mapDir)
