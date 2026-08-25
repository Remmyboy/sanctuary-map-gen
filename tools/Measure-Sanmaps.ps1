<#
.SYNOPSIS
    Mine deployed Sanctuary maps for spawn and alloy geometry.

.DESCRIPTION
    The generator's thresholds were invented. The maps that ship with the game
    are evidence, so measure those instead and let them set the targets. Reports
    per map, then the distribution across whatever set is scanned, so a
    generated batch can be compared against the shipped one directly.
#>
[CmdletBinding()]
param(
    [string]$MapsRoot = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Maps',
    [string]$Filter = '*',
    [switch]$PerMap
)

$ErrorActionPreference = 'Stop'

$rows = @()
foreach ($dir in Get-ChildItem $MapsRoot -Directory -Filter $Filter | Sort-Object Name) {
    $f = Get-ChildItem $dir.FullName -Filter *.sanmap | Select-Object -First 1
    if (-not $f) { continue }
    $j = Get-Content $f.FullName -Raw | ConvertFrom-Json

    $spawns = @()
    if ($j.markers.Spawn.transforms) {
        foreach ($p in $j.markers.Spawn.transforms.PSObject.Properties) {
            $spawns += , @([double]$p.Value.position.x, [double]$p.Value.position.z)
        }
    }
    $alloys = @()
    if ($j.markers.Alloys.transforms) {
        foreach ($p in $j.markers.Alloys.transforms.PSObject.Properties) {
            $alloys += , @([double]$p.Value.position.x, [double]$p.Value.position.z)
        }
    }
    if ($spawns.Count -eq 0) { continue }

    # Alloys within each ring of each spawn, and the walk to the nearest one.
    $near20 = @(); $near40 = @(); $near80 = @(); $nearest = @()
    foreach ($s in $spawns) {
        $n20 = 0; $n40 = 0; $n80 = 0; $best = [double]::MaxValue
        foreach ($a in $alloys) {
            $d = [Math]::Sqrt(($a[0] - $s[0]) * ($a[0] - $s[0]) + ($a[1] - $s[1]) * ($a[1] - $s[1]))
            if ($d -lt 20) { $n20++ }
            if ($d -lt 40) { $n40++ }
            if ($d -lt 80) { $n80++ }
            if ($d -lt $best) { $best = $d }
        }
        $near20 += $n20; $near40 += $n40; $near80 += $n80
        $nearest += $best
    }

    $rows += , [pscustomobject]@{
        Name       = $dir.Name
        Size       = [int]$j.width
        Spawns     = $spawns.Count
        Alloys     = $alloys.Count
        PerSpawn   = [Math]::Round($alloys.Count / $spawns.Count, 1)
        Min20      = ($near20 | Measure-Object -Minimum).Minimum
        Min40      = ($near40 | Measure-Object -Minimum).Minimum
        Min80      = ($near80 | Measure-Object -Minimum).Minimum
        Med40      = ([int](($near40 | Sort-Object)[[int]($near40.Count / 2)]))
        NearestMax = [Math]::Round((($nearest | Measure-Object -Maximum).Maximum), 0)
    }
}

if ($PerMap) {
    $rows | Format-Table Name, Size, Spawns, Alloys, PerSpawn, Min20, Min40, Min80, NearestMax -AutoSize | Out-String -Width 200 | Write-Host
}

function Stat($name, $vals) {
    if (-not $vals -or $vals.Count -eq 0) { return }
    $s = $vals | Sort-Object
    '{0,-24} min {1,6}   p25 {2,6}   median {3,6}   p75 {4,6}   max {5,6}' -f $name,
    $s[0], $s[[int]($s.Count * 0.25)], $s[[int]($s.Count * 0.5)], $s[[int]($s.Count * 0.75)], $s[-1] | Write-Host
}

"{0} maps" -f $rows.Count | Write-Host
Stat 'alloys per spawn'      ($rows.PerSpawn)
Stat 'worst spawn, r<20 m'   ($rows.Min20)
Stat 'worst spawn, r<40 m'   ($rows.Min40)
Stat 'worst spawn, r<80 m'   ($rows.Min80)
Stat 'furthest nearest alloy' ($rows.NearestMax)
