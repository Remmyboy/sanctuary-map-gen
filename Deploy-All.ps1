<#
.SYNOPSIS
    Rebuild the named maps and mirror everything into both game trees.

.DESCRIPTION
    The engine wants .santp prop blueprints and the map editor wants .sanprop,
    so each named map is built twice rather than copied. Generated and converted
    maps already exist under the engine tree, so those are copied across with
    the extension rewritten in place.

    Restart the map editor afterwards: it indexes a map folder the first time
    that map is opened and will not notice files added to a folder it has
    already scanned.
#>
[CmdletBinding()]
param(
    [string]$EngineMaps = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Maps',
    [string]$EditorMaps = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\map-editor\SanctuaryMapEditor_Data\Maps',
    [switch]$SkipRebuild
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$named = @(
    @{ Script = 'New-RiverMap.ps1';      Name = 'Serpent Crossing' },
    @{ Script = 'New-RiverbreakMap.ps1'; Name = 'Riverbreak' },
    @{ Script = 'New-CleftMap.ps1';      Name = 'Cleftwater' },
    @{ Script = 'New-OrganicMap.ps1';    Name = 'Broken Mesa' }
)

if (-not $SkipRebuild) {
    foreach ($m in $named) {
        foreach ($t in @(@{ Root = $EngineMaps; Ext = '.santp' }, @{ Root = $EditorMaps; Ext = '.sanprop' })) {
            Write-Host ("Building {0} -> {1}" -f $m.Name, (Split-Path -Leaf (Split-Path -Parent $t.Root)))
            & (Join-Path $here $m.Script) -MapsRoot $t.Root -PropExtension $t.Ext -Force |
                Select-String -Pattern '^(PASS|FAIL)' | ForEach-Object { "    $_" | Write-Host }
        }
    }
}

# Everything else that only lives under the engine tree: generated batches and
# Supreme Commander conversions.
foreach ($dir in Get-ChildItem $EngineMaps -Directory | Where-Object { $_.Name -like '~GEN-*' -or $_.Name -like '~SC-*' }) {
    $dest = Join-Path $EditorMaps $dir.Name
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    Copy-Item $dir.FullName $dest -Recurse
    $f = Get-ChildItem $dest -Filter *.sanmap | Select-Object -First 1
    $t = [IO.File]::ReadAllText($f.FullName).Replace('.santp"', '.sanprop"')
    [IO.File]::WriteAllText($f.FullName, $t, (New-Object Text.UTF8Encoding $false))
    Write-Host ("Copied {0}" -f $dir.Name)
}

""
'In the map editor:' | Write-Host
Get-ChildItem $EditorMaps -Directory | Sort-Object Name | ForEach-Object {
    $f = Get-ChildItem $_.FullName -Filter *.sanmap | Select-Object -First 1
    if (-not $f) { return }
    $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
    '  {0,-42} {1,5} m  {2} spawns  {3,3} alloys  {4}' -f $_.Name, $j.width,
    @($j.markers.Spawn.transforms.PSObject.Properties).Count,
    @($j.markers.Alloys.transforms.PSObject.Properties).Count,
    $(if ($j.hasWater) { 'water {0:N0}' -f $j.waterLevel } else { 'dry' }) | Write-Host
}
""
'Restart the map editor before opening these.' | Write-Host

# Validate before declaring victory. Every fault this project shipped was
# something absent or in the wrong format, and each was a two-second check away:
# prop blueprints with the editor extension in the engine tree (no extractors at
# all), .sanmap fields left to SanMap's defaults (black fog in every hollow), a
# splat written in a format the shipped maps do not use.
& (Join-Path $here 'tools\Test-Deployed.ps1') -EngineMaps $EngineMaps -EditorMaps $EditorMaps
