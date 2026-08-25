<#
.SYNOPSIS
    The terrain analysis of Measure-ScTerrain.ps1, run against deployed
    Sanctuary maps instead of Supreme Commander ones.

.DESCRIPTION
    Same metrics, same nav rule, read from the shipped bytes on disk. Point it
    at the developers' own generated maps to get a target, then at ours to see
    how far off we are.
#>
[CmdletBinding()]
param(
    [string]$MapsRoot = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Maps',
    [string]$Filter = '*',
    [int]$MaxSize = 2048,
    [switch]$PerMap,
    [string]$Csv
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')

$rows = @()
foreach ($dir in Get-ChildItem $MapsRoot -Directory -Filter $Filter | Sort-Object Name) {
    $f = Get-ChildItem $dir.FullName -Filter *.sanmap | Select-Object -First 1
    $raw = Join-Path $dir.FullName 'Textures\heightmap.raw'
    if (-not $f -or -not (Test-Path $raw)) { continue }

    try {
        $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
        if ([int]$j.width -gt $MaxSize) { continue }
        $water = if ($j.hasWater) { [float]$j.waterLevel } else { 0.0 }
        [MapGen]::LoadHeightFromFile($raw, [int]$j.heightmapResolution, [float]$j.width,
            [float]$j.height, $water)

        $sx = New-Object 'System.Collections.Generic.List[float]'
        $sz = New-Object 'System.Collections.Generic.List[float]'
        foreach ($p in $j.markers.Spawn.transforms.PSObject.Properties) {
            $sx.Add([float]$p.Value.position.x); $sz.Add([float]$p.Value.position.z)
        }
        if ($sx.Count -lt 2) { continue }
        [MapGen]::BaseX = $sx.ToArray(); [MapGen]::BaseZ = $sz.ToArray()

        $bi = 0; $bk = 1; $bd = -1.0
        for ($i = 0; $i -lt $sx.Count; $i++) {
            for ($k = $i + 1; $k -lt $sx.Count; $k++) {
                $d = [Math]::Sqrt(($sx[$i] - $sx[$k]) * ($sx[$i] - $sx[$k]) + ($sz[$i] - $sz[$k]) * ($sz[$i] - $sz[$k]))
                if ($d -gt $bd) { $bd = $d; $bi = $i; $bk = $k }
            }
        }
        $rs = [MapGen]::RouteStats($sx[$bi], $sz[$bi], $sx[$bk], $sz[$bk])
        if ($rs[0] -le 0) { continue }

        $ts = [MapGen]::TerrainStats()
        $og = [MapGen]::OpenGroundStats(6.0)
        $land = [Math]::Max(1, $ts[2])

        $rows += , [pscustomobject]@{
            Name        = $dir.Name
            Size        = [int]$j.width
            Spawns      = $sx.Count
            Water       = [Math]::Round([MapGen]::WaterFraction(), 2)
            Flat        = [Math]::Round($ts[3] / $land, 2)
            Cliff       = [Math]::Round($ts[6] / $land, 2)
            Open        = [Math]::Round($og[0] / [Math]::Max(1, $og[1]), 2)
            Plateau     = [Math]::Round([MapGen]::PlateauFraction(6.0), 2)
            LaneMedian  = [Math]::Round($rs[2], 1)
            LaneMin     = [Math]::Round($rs[3], 1)
            LaneMedFrac = [Math]::Round($rs[2] / [int]$j.width, 3)
            Directness  = [Math]::Round($rs[1], 2)
            Chokes      = [int]$rs[4]
            ChokePer1k  = [Math]::Round($rs[4] / ($rs[0] / 1000.0), 1)
            HighGround  = [Math]::Round($rs[5], 2)
        }
    }
    catch { Write-Host ("  skip {0}: {1}" -f $dir.Name, $_.Exception.Message) }
}

if ($PerMap) { $rows | Sort-Object Size, Name | Format-Table -AutoSize | Out-String -Width 240 | Write-Host }
if ($Csv) { $rows | Export-Csv -NoTypeInformation -Path $Csv }

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
