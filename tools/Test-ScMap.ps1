<#
.SYNOPSIS
    Parse Supreme Commander .scmap files and report what the reader found.

    Run this across the whole stock map set before trusting the converter: the
    block after the heightmap is a walk over variable-length strings, so if the
    layout is wrong anywhere it desynchronises and the water elevations come out
    as garbage. Every map parsing cleanly is the evidence that the walk is right.
#>
[CmdletBinding()]
param(
    [string]$MapsRoot = 'F:\SteamLibrary\steamapps\common\Supreme Commander Forged Alliance\maps',
    [string]$Filter = '*'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')

$ok = 0; $bad = 0
foreach ($dir in Get-ChildItem $MapsRoot -Directory -Filter $Filter | Sort-Object Name) {
    $scmap = Get-ChildItem $dir.FullName -Filter *.scmap | Select-Object -First 1
    if (-not $scmap) { continue }
    $save = Get-ChildItem $dir.FullName -Filter *_save.lua | Select-Object -First 1

    try {
        $m = [MapGen]::ReadScMap($scmap.FullName)
        $markers = if ($save) { [MapGen]::ReadScMarkers($save.FullName) } else { New-Object 'System.Collections.Generic.List[MapGen+ScMarker]' }

        $mass  = @($markers | Where-Object { $_.Type -eq 'Mass' }).Count
        $hydro = @($markers | Where-Object { $_.Type -eq 'Hydrocarbon' }).Count
        $spawn = @($markers | Where-Object { $_.Name -match '^ARMY_\d+$' }).Count

        $eN = 0.0; $eS = 0.0
        $north = [MapGen]::ResolveScRowOrder($m, $markers, [ref]$eN, [ref]$eS)

        # Peak terrain, to see whether 1/128 really is the scale everywhere.
        $peak = 0
        for ($y = 0; $y -le $m.Size; $y += 8) {
            for ($x = 0; $x -le $m.Size; $x += 8) { if ($m.Raw[$y, $x] -gt $peak) { $peak = $m.Raw[$y, $x] } }
        }

        "{0,-28} {1,5}  hs 1/{2,-5:N0}  water {3,-6}  peak {4,6:N1} m   mass {5,3}  hydro {6,2}  spawn {7,2}   rows {8} (dN {9:N2} dS {10:N2})" -f `
            $dir.Name, $m.Size, (1.0 / $m.HeightScale),
            $(if ($m.HasWater) { '{0:N1}' -f $m.WaterElevation } else { 'none' }),
            ($peak * $m.HeightScale), $mass, $hydro, $spawn,
            $(if ($north) { 'N' } else { 'S' }), $eN, $eS | Write-Host
        $ok++
    }
    catch {
        Write-Host ("{0,-28} FAILED: {1}" -f $dir.Name, $_.Exception.Message) -ForegroundColor Red
        $bad++
    }
}
""
"{0} parsed, {1} failed" -f $ok, $bad | Write-Host
