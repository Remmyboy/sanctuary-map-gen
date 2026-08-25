<#
.SYNOPSIS
    Renders a deployed map from the bytes on disk, not from the generator.

.DESCRIPTION
    Reads Textures\heightmap.raw the way Load.ReadRaw does and draws it, with
    anything over the 30-degree Land nav limit painted red and the Spawn and
    Alloys markers overlaid. Deliberately independent of the generator: if the
    two ever disagree, this shows what the game will load.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline)][string[]]$MapDir,
    [string]$OutDir,
    [int]$Res = 700
)
begin {
    $ErrorActionPreference = 'Stop'
    $here = Split-Path -Parent $PSCommandPath
    . (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')
    if (-not $OutDir) { $OutDir = $here }
    $null = New-Item -ItemType Directory -Path $OutDir -Force -ErrorAction SilentlyContinue
}
process {
    foreach ($d in $MapDir) {
        $name = Split-Path -Leaf $d
        $sanmap = Get-ChildItem (Join-Path $d '*.sanmap') | Select-Object -First 1
        if (-not $sanmap) { Write-Warning "no .sanmap in $d"; continue }
        $j = Get-Content $sanmap.FullName -Raw | ConvertFrom-Json

        $hmRes = [int]$(if ($j.heightmapResolution) { $j.heightmapResolution } else { $j.width + 1 })
        $size  = [float]$j.width
        $maxH  = [float]$j.height
        $water = [float]$(if ($j.hasWater) { $j.waterLevel } else { -9999 })
        $raw   = Join-Path $d 'Textures\heightmap.raw'

        # flat marker arrays: PowerShell flattens nested arrays on +=, so keep
        # the coordinates in parallel typed lists instead
        $mx = New-Object 'System.Collections.Generic.List[float]'
        $mz = New-Object 'System.Collections.Generic.List[float]'
        $mk = New-Object 'System.Collections.Generic.List[int]'
        foreach ($p in $j.markers.Alloys.transforms.PSObject.Properties) {
            $mx.Add([float]$p.Value.position.x); $mz.Add([float]$p.Value.position.z); $mk.Add(1)
        }
        foreach ($p in $j.markers.Spawn.transforms.PSObject.Properties) {
            $mx.Add([float]$p.Value.position.x); $mz.Add([float]$p.Value.position.z); $mk.Add(0)
        }

        $out = Join-Path $OutDir "$($name)_ondisk.png"
        [MapGen]::RenderHeightmapFile($raw, $hmRes, $size, $maxH, $water,
                                      $mx.ToArray(), $mz.ToArray(), $mk.ToArray(), $out, $Res)
        $steep = [MapGen]::SteepFractionOnDisk($raw, $hmRes, $size, $maxH, $water)

        # Pathability against the shipped bytes, not the generator's memory.
        [MapGen]::LoadHeightFromFile($raw, $hmRes, $size, $maxH, $water)
        $spawnX = New-Object 'System.Collections.Generic.List[float]'
        $spawnZ = New-Object 'System.Collections.Generic.List[float]'
        foreach ($p in $j.markers.Spawn.transforms.PSObject.Properties) {
            $spawnX.Add([float]$p.Value.position.x); $spawnZ.Add([float]$p.Value.position.z)
        }
        [MapGen]::BaseX = $spawnX.ToArray()
        [MapGen]::BaseZ = $spawnZ.ToArray()
        $reach = [MapGen]::Reachable($spawnX[0], $spawnZ[0])
        $walk  = [MapGen]::WalkableCount()
        $rc    = [MapGen]::CountTrue($reach)

        $badSpawn = 0
        for ($i = 1; $i -lt $spawnX.Count; $i++) {
            if (-not [MapGen]::IsReachable($reach, $spawnX[$i], $spawnZ[$i])) { $badSpawn++ }
        }
        $badMex = 0
        for ($i = 0; $i -lt $mx.Count; $i++) {
            if ($mk[$i] -eq 1 -and -not [MapGen]::IsReachable($reach, $mx[$i], $mz[$i])) { $badMex++ }
        }
        $og = [MapGen]::OpenGroundStats(6.0)
        $sp = [MapGen]::PathingSpecks(60)

        "{0,-40} {1}x{1}m  water {2,-4}  over-limit {3,4:P0}  reachable {4,4:P0}  open {5,4:P0}  cut off {6}/{7}  specks {8:N0}" -f `
            $name, $j.width,
            $(if ($j.hasWater) { $j.waterLevel } else { 'none' }),
            $steep, ($rc / [Math]::Max(1,$walk)), ($og[0] / [Math]::Max(1,$og[1])),
            $badSpawn, $badMex, $sp[0] | Write-Host

        # Unity rounds heightmapResolution to a power of two plus one. Anything else and
        # SetHeights fills only a corner of the terrain; the remainder stays at height 0.
        $n = $hmRes - 1
        if ($n -le 0 -or ($n -band ($n - 1)) -ne 0) {
            Write-Host ("    *** heightmapResolution {0} is not 2^n+1 - Unity will resize the terrain and leave part of it unwritten" -f $hmRes) -ForegroundColor Red
        }
    }
}
