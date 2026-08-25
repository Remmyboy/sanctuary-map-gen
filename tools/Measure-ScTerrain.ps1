<#
.SYNOPSIS
    Measure the terrain structure of a Supreme Commander map library.

.DESCRIPTION
    Companion to Measure-ScCorpus.ps1, which counts markers. This one loads the
    heightmaps and asks what the terrain is actually shaped like: how wide the
    route between two spawns is, how often it pinches, how much of it is
    overlooked by high ground, and how much of the map is raised or flooded.

    Every map is judged by Sanctuary's Land nav rule - 30 degrees with a 3x3
    dilation - because the question is what this terrain would play like in
    Sanctuary, not how it behaved in its own engine.

    Slow: the heightmaps are decoded in full. Expect a couple of minutes for a
    few hundred maps. -Sample takes every Nth map for a quicker read.
#>
[CmdletBinding()]
param(
    [string[]]$MapsRoot = @(
        'F:\SteamLibrary\steamapps\common\Supreme Commander Forged Alliance\maps',
        "$env:USERPROFILE\Documents\My Games\Gas Powered Games\Supreme Commander Forged Alliance\Maps"
    ),
    [int]$Sample = 1,
    [int]$MaxSize = 2048,
    [switch]$PerMap,
    [string]$Csv
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')

$rows = @()
$seen = 0
foreach ($root in $MapsRoot) {
    if (-not (Test-Path $root)) { continue }
    foreach ($dir in Get-ChildItem $root -Directory | Sort-Object Name) {
        $scmap = Get-ChildItem $dir.FullName -Filter *.scmap -EA SilentlyContinue | Select-Object -First 1
        $save = Get-ChildItem $dir.FullName -Filter *_save.lua -EA SilentlyContinue | Select-Object -First 1
        if (-not $scmap -or -not $save) { continue }
        $seen++
        if (($seen - 1) % $Sample -ne 0) { continue }

        try {
            $sc = [MapGen]::ReadScMap($scmap.FullName)
            if ($sc.Size -gt $MaxSize) { continue }
            $mk = [MapGen]::ReadScMarkers($save.FullName)
            $spawns = @($mk | Where-Object { $_.Name -match '^ARMY_\d+$' })
            if ($spawns.Count -lt 2) { continue }

            $eN = 0.0; $eS = 0.0
            $sc.RowZeroIsNorth = [MapGen]::ResolveScRowOrder($sc, $mk, [ref]$eN, [ref]$eS)
            [MapGen]::MaxHeight = 512.0
            [MapGen]::AdoptScMap($sc, 1.0)

            $sx = New-Object 'System.Collections.Generic.List[float]'
            $sz = New-Object 'System.Collections.Generic.List[float]'
            foreach ($s in $spawns) {
                $sx.Add([float]$s.X); $sz.Add([float][MapGen]::ScMarkerZ($sc, $s.Z))
            }
            [MapGen]::BaseX = $sx.ToArray(); [MapGen]::BaseZ = $sz.ToArray()

            # The route between the two spawns furthest apart: on a team map
            # that is an actual attack lane rather than the walk to an ally.
            $bi = 0; $bk = 1; $bd = -1.0
            for ($i = 0; $i -lt $sx.Count; $i++) {
                for ($k = $i + 1; $k -lt $sx.Count; $k++) {
                    $d = [Math]::Sqrt(($sx[$i] - $sx[$k]) * ($sx[$i] - $sx[$k]) + ($sz[$i] - $sz[$k]) * ($sz[$i] - $sz[$k]))
                    if ($d -gt $bd) { $bd = $d; $bi = $i; $bk = $k }
                }
            }
            $rs = [MapGen]::RouteStats($sx[$bi], $sz[$bi], $sx[$bk], $sz[$bk])
            if ($rs[0] -le 0) { continue }        # no overland route: naval map

            $ts = [MapGen]::TerrainStats()
            $og = [MapGen]::OpenGroundStats(6.0)
            $land = [Math]::Max(1, $ts[2])

            $rows += , [pscustomobject]@{
                Name        = $dir.Name
                Size        = $sc.Size
                Spawns      = $spawns.Count
                Water       = [Math]::Round([MapGen]::WaterFraction(), 2)
                Flat        = [Math]::Round($ts[3] / $land, 2)
                Cliff       = [Math]::Round($ts[6] / $land, 2)
                Open        = [Math]::Round($og[0] / [Math]::Max(1, $og[1]), 2)
                Plateau     = [Math]::Round([MapGen]::PlateauFraction(6.0), 2)
                # Route metrics, the point of the exercise
                LaneMedian  = [Math]::Round($rs[2], 1)
                LaneMin     = [Math]::Round($rs[3], 1)
                LaneMedFrac = [Math]::Round($rs[2] / $sc.Size, 3)
                Directness  = [Math]::Round($rs[1], 2)
                Chokes      = [int]$rs[4]
                ChokePer1k  = [Math]::Round($rs[4] / ($rs[0] / 1000.0), 1)
                HighGround  = [Math]::Round($rs[5], 2)
            }
        }
        catch { continue }
    }
}

if ($PerMap) { $rows | Sort-Object Size, Name | Format-Table -AutoSize | Out-String -Width 240 | Write-Host }
if ($Csv) { $rows | Export-Csv -NoTypeInformation -Path $Csv; "wrote $Csv" | Write-Host }

function Stat($name, $vals) {
    $s = @($vals) | Sort-Object
    if ($s.Count -eq 0) { return }
    '  {0,-34} p10 {1,7}  p25 {2,7}  median {3,7}  p75 {4,7}  p90 {5,7}' -f $name,
    $s[[int]($s.Count * 0.10)], $s[[int]($s.Count * 0.25)], $s[[int]($s.Count * 0.5)],
    $s[[int]($s.Count * 0.75)], $s[[int]($s.Count * 0.90)] | Write-Host
}

"{0} maps measured" -f $rows.Count | Write-Host
""
'Terrain composition' | Write-Host
Stat 'water fraction of map'        $rows.Water
Stat 'flat land (< 6 deg)'          $rows.Flat
Stat 'cliff land (> 34 deg)'        $rows.Cliff
Stat 'largest open area / land'     $rows.Open
Stat 'raised ground (> 6 m up)'     $rows.Plateau
""
'The lane between the two furthest spawns' | Write-Host
Stat 'median clearance, m'          $rows.LaneMedian
Stat 'median clearance / map size'  $rows.LaneMedFrac
Stat 'narrowest point, m'           $rows.LaneMin
Stat 'directness (1 = straight)'    $rows.Directness
Stat 'chokepoints on the route'     $rows.Chokes
Stat 'chokepoints per 1000 m'       $rows.ChokePer1k
Stat 'route overlooked by high grd' $rows.HighGround
