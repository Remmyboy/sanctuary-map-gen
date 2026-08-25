<#
.SYNOPSIS
    Check that every texture a biome table names has all three variants.

.DESCRIPTION
    Sanctuary's stratum layers each reference an _albedo, a _normal and a _mask.
    Not every texture in the game has all three: the Winter set in particular
    ships albedos with no matching normal or mask, so a biome table built from
    it produces a map with unresolvable references.

    This is exactly the class of fault the deployed-map validator catches, but
    it catches it after a map has been built. Run this after editing a biome
    table and the answer arrives in a second instead.
#>
[CmdletBinding()]
param(
    [string]$Sanpack = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Gamedata\Environment.sanpack',
    [string[]]$Biomes = @('Highlands', 'Tropical', 'Winter', 'Evergreen', 'Arid')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot '..' 'src' 'Biomes.ps1')

$zip = [IO.Compression.ZipFile]::OpenRead($Sanpack)
$have = @{}
try { foreach ($e in $zip.Entries) { $have[($e.FullName -replace '\.[^.]+$', '')] = $true } }
finally { $zip.Dispose() }

$bad = 0
foreach ($b in $Biomes) {
    $layers = (Get-Biome $b).Layers
    $issues = @()
    for ($i = 0; $i -lt $layers.Count; $i++) {
        $p = Resolve-LayerPath $layers[$i]
        $missing = @()
        foreach ($v in '_albedo', '_normal', '_mask') {
            if (-not $have.ContainsKey("$p$v")) { $missing += $v }
        }
        if ($missing.Count) { $issues += ("slot {0} {1} has no {2}" -f $i, $layers[$i], ($missing -join ', ')) }
    }
    if ($issues.Count) {
        $bad++
        "FAIL {0}" -f $b | Write-Host -ForegroundColor Red
        $issues | ForEach-Object { "       $_" | Write-Host -ForegroundColor Red }
    }
    else { "ok   {0}" -f $b | Write-Host }
}

""
if ($bad) { "$bad biome(s) reference textures that do not exist" | Write-Host -ForegroundColor Red; exit 1 }
'every biome resolves' | Write-Host -ForegroundColor Green
