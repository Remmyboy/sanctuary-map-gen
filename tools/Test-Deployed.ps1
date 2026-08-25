<#
.SYNOPSIS
    Validate every deployed map: parse, assets, and the fields the game expects.

.DESCRIPTION
    Three faults this project shipped were all of one kind - something the game
    needs was absent or wrong, and nothing said so until it was seen in game:

      * prop blueprints with the map-editor extension in the engine tree, which
        aborts RunMapSetup before alloy spots are created and leaves the map
        with no extractors at all;
      * .sanmap fields the shipped maps set and we omitted, so SanMap's C#
        defaults applied - dense black height fog in every hollow;
      * splat textures written in a format the shipped maps do not use.

    Each was a two-second check away. This runs all of them over both trees so a
    broken map cannot reach the game unnoticed.
#>
[CmdletBinding()]
param(
    [string]$EngineMaps = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Maps',
    [string]$EditorMaps = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\map-editor\SanctuaryMapEditor_Data\Maps',
    [string]$Gamedata = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Gamedata',
    # A map the developers shipped, used as the reference for which fields a
    # .sanmap is expected to carry.
    [string]$Reference = '~TEAM-1v1_Tropical_256_47940',
    [string]$Filter = '*'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $PSScriptRoot

$refPath = Get-ChildItem (Join-Path $EngineMaps $Reference) -Filter *.sanmap | Select-Object -First 1
$refFields = if ($refPath) { (Get-Content $refPath.FullName -Raw | ConvertFrom-Json).PSObject.Properties.Name } else { @() }

$problems = 0
foreach ($tree in @(
        @{ Name = 'engine'; Root = $EngineMaps; Ext = '.santp' },
        @{ Name = 'editor'; Root = $EditorMaps; Ext = '.sanprop' })) {

    if (-not (Test-Path $tree.Root)) { continue }
    "== $($tree.Name) tree" | Write-Host

    foreach ($dir in Get-ChildItem $tree.Root -Directory -Filter $Filter | Sort-Object Name) {
        # Only the maps we produce; leave the shipped ones alone.
        if ($dir.Name -notmatch '^(~GEN-|~SC-)' -and
            $dir.Name -notin 'Riverbreak', 'Cleftwater', 'Broken_Mesa', 'Serpent_Crossing') { continue }
        $f = Get-ChildItem $dir.FullName -Filter *.sanmap | Select-Object -First 1
        if (-not $f) { continue }

        $issues = @()
        $raw = Get-Content $f.FullName -Raw
        $j = $raw | ConvertFrom-Json

        # 1. prop blueprints must carry this tree's extension
        $wrong = if ($tree.Ext -eq '.santp') { '.sanprop' } else { '.santp' }
        $nWrong = ([regex]::Matches($raw, [regex]::Escape($wrong) + '"')).Count
        if ($nWrong -gt 0) { $issues += "$nWrong blueprint(s) use $wrong, this tree wants $($tree.Ext)" }

        # 2. every field the reference map sets
        if ($refFields.Count) {
            $missing = $refFields | Where-Object { $_ -notin $j.PSObject.Properties.Name }
            if ($missing) { $issues += "missing field(s): $($missing -join ', ')" }
        }

        # 3. heightmap resolution must be a power of two plus one
        $n = [int]$j.heightmapResolution - 1
        if ($n -le 0 -or ($n -band ($n - 1)) -ne 0) { $issues += "heightmapResolution $($j.heightmapResolution) is not 2^n+1" }

        # 4. splat must match the heightmap grid, and use the shipped TGA header
        $t = Join-Path $dir.FullName 'Textures\stratums_1_4.tga'
        if (Test-Path $t) {
            $b = [byte[]]::new(18)
            $fs = [IO.File]::OpenRead($t); $null = $fs.Read($b, 0, 18); $fs.Close()
            $sw = [int]$b[12] -bor ([int]$b[13] -shl 8)
            if ($sw -ne [int]$j.heightmapResolution) { $issues += "splat $sw does not match heightmapResolution $($j.heightmapResolution)" }
            if ($b[17] -ne 0x28) { $issues += ("TGA descriptor 0x{0:x2}, shipped maps use 0x28" -f $b[17]) }
        }
        else { $issues += 'stratums_1_4.tga missing' }

        if ($issues) {
            $problems++
            "  FAIL {0}" -f $dir.Name | Write-Host -ForegroundColor Red
            $issues | ForEach-Object { "         $_" | Write-Host -ForegroundColor Red }
        }
        else { "  ok   {0}" -f $dir.Name | Write-Host }
    }
}

# Asset resolution is the expensive check, so it runs once over the engine tree
# where a missing blueprint actually costs you the map.
""
'Asset resolution (engine tree)' | Write-Host
foreach ($dir in Get-ChildItem $EngineMaps -Directory -Filter $Filter | Sort-Object Name) {
    if ($dir.Name -notmatch '^(~GEN-|~SC-)' -and
        $dir.Name -notin 'Riverbreak', 'Cleftwater', 'Broken_Mesa', 'Serpent_Crossing') { continue }
    $f = Get-ChildItem $dir.FullName -Filter *.sanmap | Select-Object -First 1
    if (-not $f) { continue }
    $out = & (Join-Path $here 'tools\Test-Sanmap.ps1') -Path $f.FullName -CheckTextures -GamedataDir $Gamedata 2>&1
    $bad = $out | Select-String -Pattern 'missing from this build'
    if ($bad) {
        $problems++
        "  FAIL {0}" -f $dir.Name | Write-Host -ForegroundColor Red
        $out | Select-String -Pattern '^\s+Environment/' | ForEach-Object { "       $_" | Write-Host -ForegroundColor Red }
    }
    else { "  ok   {0}" -f $dir.Name | Write-Host }
}

""
if ($problems) { "$problems problem(s)" | Write-Host -ForegroundColor Red; exit 1 }
'all deployed maps pass' | Write-Host -ForegroundColor Green
