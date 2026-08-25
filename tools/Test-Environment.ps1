<#
.SYNOPSIS
    Compare a map's environment settings against the range the shipped maps use.

.DESCRIPTION
    A .sanmap carries about thirty lighting and atmosphere fields, and none of
    them can fail: an absurd value renders, it just renders wrong. That is how
    skylightIntensity sat at 6000 across every map we generate while all four
    shipped maps use exactly 0 - a full ambient sky wash that put a wet sheen
    over every surface, found by eye rather than by any check.

    So: read the shipped maps, take the range of each field, and flag ours when
    it falls outside. A field the shipped maps all agree on is a constant we
    should be matching, not a dial to invent a value for.

    This is a warning, not a failure. Deliberate variety is the point of these
    fields, and a map is allowed to be brighter than anything that shipped. The
    check exists so that choice is visible instead of accidental.

.EXAMPLE
    .\Test-Environment.ps1 -Path ...\Maps\~SC-Badlands_CC0\~SC-Badlands_CC0.sanmap
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromPipeline)][string[]]$Path,
    [string]$MapsRoot = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Maps',
    # The maps that came with the game. Ours are not evidence about ours.
    [string[]]$Reference = @('The_Forge', 'White_Desert', 'There_Is_Time', 'Two_Step_Shuffle'),
    # How far outside the shipped range is worth mentioning, as a fraction of
    # that range. Zero flags anything outside at all.
    [double]$Tolerance = 0.25
)

begin {
    $ErrorActionPreference = 'Stop'

    $ref = @{}
    foreach ($n in $Reference) {
        $f = Join-Path $MapsRoot "$n\$n.sanmap"
        if (-not (Test-Path $f)) { continue }
        $j = Get-Content $f -Raw | ConvertFrom-Json
        foreach ($p in $j.PSObject.Properties) {
            $v = $p.Value
            if ($v -isnot [double] -and $v -isnot [int] -and $v -isnot [long]) { continue }
            if (-not $ref.ContainsKey($p.Name)) { $ref[$p.Name] = @() }
            $ref[$p.Name] += [double]$v
        }
    }
    if (-not $ref.Count) { throw "no reference maps found under '$MapsRoot'" }
    Write-Host ("Reference range from {0} shipped maps, {1} numeric fields" -f $Reference.Count, $ref.Count)

    # Size and terrain fields legitimately differ per map; they are not style.
    $skip = 'width', 'length', 'height', 'heightmapResolution', 'mapVersion', 'waterLevel',
            'waterDepth', 'seed', 'maxPlayers'
    $flagged = 0
}

process {
    foreach ($p in $Path) {
        $j = Get-Content $p -Raw | ConvertFrom-Json
        $issues = @()
        foreach ($prop in $j.PSObject.Properties) {
            $k = $prop.Name
            if ($k -in $skip -or -not $ref.ContainsKey($k)) { continue }
            $v = $prop.Value
            if ($v -isnot [double] -and $v -isnot [int] -and $v -isnot [long]) { continue }
            $v = [double]$v

            $m = $ref[$k] | Measure-Object -Minimum -Maximum
            $lo = $m.Minimum; $hi = $m.Maximum
            $span = $hi - $lo
            # A field every shipped map agrees on has no range to be tolerant
            # about, so any deviation counts.
            $pad = if ($span -gt 0) { $span * $Tolerance } else { 0 }
            if ($v -ge $lo - $pad -and $v -le $hi + $pad) { continue }

            $issues += '{0,-30} {1,12:n2}   shipped {2:n2} .. {3:n2}' -f $k, $v, $lo, $hi
        }
        $name = Split-Path -Leaf $p
        if ($issues.Count) {
            $flagged++
            Write-Host ("WARN  {0}" -f $name) -ForegroundColor Yellow
            $issues | ForEach-Object { Write-Host ("      $_") -ForegroundColor Yellow }
        }
        else { Write-Host ("ok    {0}" -f $name) }
    }
}

end {
    ''
    if ($flagged) { "{0} map(s) have environment values outside the shipped range" -f $flagged | Write-Host -ForegroundColor Yellow }
    else { 'every map sits inside the shipped range' | Write-Host -ForegroundColor Green }
}
