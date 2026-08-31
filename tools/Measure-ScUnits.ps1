<#
.SYNOPSIS
    Extract every FA unit's mass cost and footprint into docs\unit-wrecks.csv.

.DESCRIPTION
    The wreckage import sizes and filters Supreme Commander wrecks by the unit
    they were: mass decides whether a wreck is worth placing at all (walls cost
    2 mass; Sanctuary's wreck blueprints are all worth 100 alloys, so a wall
    wreck would be a goldmine), and the hitbox area picks which of the six
    shipped wreck meshes stands in.

    The numbers are data about the game, not art, and the CSV ships with the
    repo so CC0 conversions work without a Forged Alliance install - the same
    arrangement as texture-map.csv.
#>
[CmdletBinding()]
param(
    [string]$ScdPath = 'F:\SteamLibrary\steamapps\common\Supreme Commander Forged Alliance\gamedata\units.scd',
    [string]$OutCsv = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\unit-wrecks.csv')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [IO.Compression.ZipFile]::OpenRead($ScdPath)
$rows = @()
$n = 0
try {
    foreach ($e in $zip.Entries | Where-Object { $_.FullName -match '_unit\.bp$' }) {
        $id = ([IO.Path]::GetFileName($e.FullName) -replace '_unit\.bp$', '').ToLowerInvariant()
        $sr = New-Object IO.StreamReader($e.Open())
        $t = $sr.ReadToEnd(); $sr.Close()
        $mass = [regex]::Match($t, 'BuildCostMass\s*=\s*([\d.]+)').Groups[1].Value
        $sx = [regex]::Match($t, 'SizeX\s*=\s*([\d.]+)').Groups[1].Value
        $sz = [regex]::Match($t, 'SizeZ\s*=\s*([\d.]+)').Groups[1].Value
        if (-not $mass) { $mass = '0' }
        if (-not $sx) { $sx = '1' }
        if (-not $sz) { $sz = '1' }
        $rows += "$id,$mass,$sx,$sz"
        $n++
    }
}
finally { $zip.Dispose() }

@('id,mass,sizex,sizez') + ($rows | Sort-Object) | Set-Content $OutCsv -Encoding utf8
"wrote $n units -> $OutCsv"
