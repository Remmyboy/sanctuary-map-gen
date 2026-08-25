<#
.SYNOPSIS
    Mine a folder of Supreme Commander maps for map-design statistics.

.DESCRIPTION
    299 maps between the stock set and a community collection is enough to stop
    guessing at generator parameters and read them off instead. Reports the
    distributions that actually drive a generator: resource density against map
    size and player count, how mass clusters around a spawn, how far spawns sit
    from each other, and how much of the map is water.

    Uses the header-only reader, so the heightmaps are skipped - this is about
    marker layout, which lives in _save.lua.
#>
[CmdletBinding()]
param(
    [string[]]$MapsRoot = @(
        'F:\SteamLibrary\steamapps\common\Supreme Commander Forged Alliance\maps',
        "$env:USERPROFILE\Documents\My Games\Gas Powered Games\Supreme Commander Forged Alliance\Maps"
    ),
    [switch]$PerMap
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')

$rows = @()
foreach ($root in $MapsRoot) {
    if (-not (Test-Path $root)) { continue }
    foreach ($dir in Get-ChildItem $root -Directory | Sort-Object Name) {
        $scmap = Get-ChildItem $dir.FullName -Filter *.scmap -EA SilentlyContinue | Select-Object -First 1
        $save = Get-ChildItem $dir.FullName -Filter *_save.lua -EA SilentlyContinue | Select-Object -First 1
        if (-not $scmap -or -not $save) { continue }

        try { $h = [MapGen]::ReadScMapHeader($scmap.FullName) } catch { continue }
        $mk = [MapGen]::ReadScMarkers($save.FullName)

        $spawns = @($mk | Where-Object { $_.Name -match '^ARMY_\d+$' })
        $mass = @($mk | Where-Object { $_.Type -eq 'Mass' })
        $hydro = @($mk | Where-Object { $_.Type -eq 'Hydrocarbon' })
        if ($spawns.Count -lt 2) { continue }

        # Mass in rings around each spawn. FA and Sanctuary both use metres, so
        # these radii carry over directly.
        $r16 = @(); $r24 = @(); $r48 = @(); $nearest = @()
        foreach ($s in $spawns) {
            $a = 0; $b = 0; $c = 0; $best = [double]::MaxValue
            foreach ($m in $mass) {
                $dx = $m.X - $s.X; $dz = $m.Z - $s.Z
                $d = [Math]::Sqrt($dx * $dx + $dz * $dz)
                if ($d -lt 16) { $a++ }
                if ($d -lt 24) { $b++ }
                if ($d -lt 48) { $c++ }
                if ($d -lt $best) { $best = $d }
            }
            $r16 += $a; $r24 += $b; $r48 += $c; $nearest += $best
        }

        # Closest pair of spawns, as a fraction of map size.
        $sep = [double]::MaxValue
        for ($i = 0; $i -lt $spawns.Count; $i++) {
            for ($k = $i + 1; $k -lt $spawns.Count; $k++) {
                $dx = $spawns[$i].X - $spawns[$k].X; $dz = $spawns[$i].Z - $spawns[$k].Z
                $d = [Math]::Sqrt($dx * $dx + $dz * $dz)
                if ($d -lt $sep) { $sep = $d }
            }
        }

        $rows += , [pscustomobject]@{
            Name       = $dir.Name
            Size       = $h.Size
            Spawns     = $spawns.Count
            Mass       = $mass.Count
            Hydro      = $hydro.Count
            MassPer    = [Math]::Round($mass.Count / $spawns.Count, 1)
            # Density is the size-independent number: mass per player per
            # square kilometre of map.
            Density    = [Math]::Round($mass.Count / $spawns.Count / (($h.Size / 1000.0) * ($h.Size / 1000.0)), 1)
            WorstR16   = ($r16 | Measure-Object -Minimum).Minimum
            MedR16     = ([int](($r16 | Sort-Object)[[int]($r16.Count / 2)]))
            MedR24     = ([int](($r24 | Sort-Object)[[int]($r24.Count / 2)]))
            MedR48     = ([int](($r48 | Sort-Object)[[int]($r48.Count / 2)]))
            NearestMax = [Math]::Round((($nearest | Measure-Object -Maximum).Maximum), 0)
            SepFrac    = [Math]::Round($sep / $h.Size, 2)
        }
    }
}

if ($PerMap) { $rows | Sort-Object Size, Name | Format-Table -AutoSize | Out-String -Width 220 | Write-Host }

function Stat($name, $vals) {
    $s = @($vals) | Sort-Object
    if ($s.Count -eq 0) { return }
    '  {0,-30} min {1,7}  p10 {2,7}  median {3,7}  p90 {4,7}  max {5,7}' -f $name,
    $s[0], $s[[int]($s.Count * 0.10)], $s[[int]($s.Count * 0.5)], $s[[int]($s.Count * 0.90)], $s[-1] | Write-Host
}

"{0} maps with 2+ spawns" -f $rows.Count | Write-Host
""
'Resources' | Write-Host
Stat 'mass per player'            $rows.MassPer
Stat 'mass per player per km2'    $rows.Density
Stat 'hydrocarbon per map'        $rows.Hydro
""
'Mass around the spawn (worst and median spawn on each map)' | Write-Host
Stat 'worst spawn, r < 16 m'      $rows.WorstR16
Stat 'median spawn, r < 16 m'     $rows.MedR16
Stat 'median spawn, r < 24 m'     $rows.MedR24
Stat 'median spawn, r < 48 m'     $rows.MedR48
Stat 'furthest nearest mass'      $rows.NearestMax
""
'Layout' | Write-Host
Stat 'closest spawn pair / size'  $rows.SepFrac
""
'Mass per player by map size' | Write-Host
$rows | Group-Object Size | Sort-Object { [int]$_.Name } | ForEach-Object {
    $m = @($_.Group.MassPer) | Sort-Object
    '  {0,5} m  n={1,4}   median {2,5}   spawns {3}' -f $_.Name, $_.Count, $m[[int]($m.Count / 2)],
    (($_.Group.Spawns | Sort-Object -Unique) -join ',') | Write-Host
}
