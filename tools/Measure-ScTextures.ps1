<#
.SYNOPSIS
    Measure every Supreme Commander stratum texture the map corpus actually
    uses, and classify it by material role.

.DESCRIPTION
    Replacing Supreme Commander's textures with our own needs three facts about
    each one: what material it is, how bright it is, and how much it matters.

    Role comes from the file name, which is reliable here because the naming is
    consistent across all twelve environments - des_rock01, evgrass005,
    tund_snow. Tone comes from decoding the surface, because a substitute only
    reads as the same ground if it lands on the same tone, and diffuseRemap is
    targetTone / measuredLuminance. Weight is how many maps reference it, so
    effort goes where it shows.

    Only textures the corpus references are measured. env.scd holds 402 stratum
    textures and the 288 readable maps touch 249 of them; measuring the rest
    would be work spent on ground nobody has painted.

    Map-local textures - a mapper's own art shipped inside their map folder -
    are counted separately. They are not Gas Powered Games' to replace and not
    ours to substitute.

.EXAMPLE
    .\Measure-ScTextures.ps1 -Csv ..\docs\sc-textures.csv
#>
[CmdletBinding()]
param(
    [string[]]$MapsRoot = @(
        'F:\SteamLibrary\steamapps\common\Supreme Commander Forged Alliance\maps',
        "$env:USERPROFILE\Documents\My Games\Gas Powered Games\Supreme Commander Forged Alliance\Maps"
    ),
    [string]$ScdPath = 'F:\SteamLibrary\steamapps\common\Supreme Commander Forged Alliance\gamedata\env.scd',
    [string]$Csv
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem

# First match wins, so the specific materials come before the generic ones:
# "sandstone" is rock, and "lavarock" is lava.
$roleRules = [ordered]@{
    snow    = 'snow|ice|frost|glacier|melt'
    lava    = 'lava|magma|molten|ribbon|wiers'
    crystal = 'crystal|cryst|^cr_|^cru_'
    grass   = 'grass|moss|turf|heather|foliage|sphagnum|creeper|hostas|fern|jungle'
    gravel  = 'gravel|pebble|shingle|scree|gravil'
    sand    = 'sand|dune|beach'
    crack   = 'crack|barren|waste|dry'
    rock    = 'rock|stone|cliff|boulder|slate|granit|ash|coral|reef|masonry'
    dirt    = 'dirt|soil|mud|earth|ground|clay|dust|silt'
}

# Contrast: the standard deviation of luma across the surface. Mean colour
# says what a texture is on average; this says how loudly it varies, which is
# what makes a fine sand and a bold pebble field read differently at the same
# mean. 45 is the corpus-typical value, used where the format cannot decode.

function Get-LumaStdCoarse([byte[]]$bytes) {
    $w = 0; $h = 0
    $px = [MapGen]::DecodeDdsToBgra($bytes, [ref]$w, [ref]$h)
    if ($null -eq $px) { return 20.0 }
    $cw = [math]::Max(4, [int]($w / 8)); $ch = [math]::Max(4, [int]($h / 8))
    $n = 0; [double]$s = 0; [double]$s2 = 0
    for ($cy = 0; $cy -lt $ch; $cy++) {
        $y = [int]($cy * $h / $ch)
        for ($cx = 0; $cx -lt $cw; $cx++) {
            $x = [int]($cx * $w / $cw)
            # Mean of a small neighbourhood stands in for the box average.
            [double]$acc = 0; $cnt = 0
            for ($dy = 0; $dy -lt 8; $dy += 3) {
                for ($dx = 0; $dx -lt 8; $dx += 3) {
                    $xx = [math]::Min($w - 1, $x + $dx); $yy = [math]::Min($h - 1, $y + $dy)
                    $o = ($yy * $w + $xx) * 4
                    $acc += 0.299 * $px[$o+2] + 0.587 * $px[$o+1] + 0.114 * $px[$o]
                    $cnt++
                }
            }
            $l = $acc / $cnt
            $s += $l; $s2 += $l * $l; $n++
        }
    }
    if ($n -lt 2) { return 20.0 }
    return [math]::Round([math]::Sqrt([math]::Max(0.0, $s2 / $n - ($s / $n) * ($s / $n))), 1)
}
function Get-LumaStd([byte[]]$bytes) {
    $w = 0; $h = 0
    $px = [MapGen]::DecodeDdsToBgra($bytes, [ref]$w, [ref]$h)
    if ($null -eq $px) { return 45.0 }
    $n = 0; [double]$s = 0; [double]$s2 = 0
    $step = [math]::Max(1, [int]($w * $h / 65536)) * 4
    for ($k = 0; $k -lt $px.Length; $k += $step) {
        $l = 0.299 * $px[$k+2] + 0.587 * $px[$k+1] + 0.114 * $px[$k]
        $s += $l; $s2 += $l * $l; $n++
    }
    if ($n -lt 2) { return 45.0 }
    return [math]::Round([math]::Sqrt([math]::Max(0.0, $s2 / $n - ($s / $n) * ($s / $n))), 1)
}
function Get-Role([string]$leaf) {
    foreach ($r in $roleRules.GetEnumerator()) { if ($leaf -match $r.Value) { return $r.Key } }
    return 'other'
}

Write-Host 'Scanning the corpus for referenced textures...'
$refs = @{}
$scanned = 0
foreach ($root in $MapsRoot) {
    if (-not (Test-Path $root)) { continue }
    foreach ($f in Get-ChildItem $root -Recurse -Filter *.scmap -EA SilentlyContinue) {
        try {
            $b = [IO.File]::ReadAllBytes($f.FullName)
            $sc = [MapGen]::ReadScMap($f.FullName, $true)
            $set = [MapGen]::ScanScTextures($b, $sc.Size)
            if (-not $set) { continue }
            $scanned++
            foreach ($p in ($set.Paths | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique)) {
                $refs[$p] = 1 + $refs[$p]
            }
        } catch { }
    }
}
Write-Host ("  {0} maps readable, {1} distinct textures referenced" -f $scanned, $refs.Count)

Write-Host 'Measuring...'
$zip = [IO.Compression.ZipFile]::OpenRead($ScdPath)
$index = @{}
foreach ($e in $zip.Entries) { $index[$e.FullName.ToLowerInvariant().TrimStart('/')] = $e }

$rows = @()
$failed = @()
foreach ($kv in ($refs.GetEnumerator() | Sort-Object Value -Descending)) {
    $path = $kv.Key
    $leaf = ($path -split '/')[-1]
    $stem = $leaf -replace '_albedo\.dds$|\.dds$', ''
    $family = if ($path -match '^/maps/') { 'MAP-LOCAL' } else { ($path -split '/')[2] }

    $bytes = $null
    $entry = $index[$path.TrimStart('/')]
    if ($entry) {
        $ms = New-Object IO.MemoryStream; $s = $entry.Open(); $s.CopyTo($ms); $s.Close()
        $bytes = $ms.ToArray(); $ms.Dispose()
    }
    elseif ($family -eq 'MAP-LOCAL') {
        foreach ($root in $MapsRoot) {
            $cand = Join-Path $root ($path -replace '^/maps/', '')
            if (Test-Path $cand) { $bytes = [IO.File]::ReadAllBytes($cand); break }
        }
    }
    if (-not $bytes) { $failed += $path; continue }

    $i = [MapGen]::ReadDdsInfo($bytes)
    if (-not $i.Ok) { $failed += ("{0} ({1})" -f $path, $i.Format); continue }

    $role = Get-Role $stem
    if ($role -eq 'other') {
        # Colour settles the stragglers: transition blends, coral, map-local
        # customs. Rough bands, but each lands on a plausible material where
        # the old path landed everything on rock.
        $gx = $i.G - ($i.R + $i.B) / 2
        $spread = [math]::Max($i.R, [math]::Max($i.G, $i.B)) - [math]::Min($i.R, [math]::Min($i.G, $i.B))
        $role = if ($gx -gt 8) { 'grass' }
                elseif ($i.Luma -gt 150 -and $spread -lt 40) { 'snow' }
                elseif ($i.R - $i.G -gt 30 -and $i.Luma -lt 95) { 'lava' }
                elseif ($i.Luma -lt 50) { 'rock' }
                elseif ($i.R -gt $i.G -and $i.G -gt $i.B -and $i.R - $i.B -gt 35) { 'dirt' }
                else { 'rock' }
    }
    $rows += [pscustomobject]@{
        Stem    = $stem
        Role    = $role
        Family  = $family
        Maps    = $kv.Value
        Luma    = [math]::Round($i.Luma, 1)
        Std     = (Get-LumaStd $bytes)
        StdC    = (Get-LumaStdCoarse $bytes)
        R       = [math]::Round($i.R, 1)
        G       = [math]::Round($i.G, 1)
        B       = [math]::Round($i.B, 1)
        Format  = $i.Format
        Size    = '{0}x{1}' -f $i.Width, $i.Height
        Path    = $path
    }
}
$zip.Dispose()

Write-Host ("  measured {0}, failed {1}" -f $rows.Count, $failed.Count)
if ($failed.Count) { $failed | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" } }

''
'By role (stock environments only):' | Write-Host
$rows | Where-Object { $_.Family -ne 'MAP-LOCAL' } | Group-Object Role |
    Sort-Object { ($_.Group | Measure-Object Maps -Sum).Sum } -Descending | ForEach-Object {
        $l = $_.Group | Measure-Object Luma -Average -Minimum -Maximum
        '  {0,-8} {1,3} textures  {2,4} map-refs   luma {3,3:n0} ({4,3:n0}-{5,3:n0})' -f `
            $_.Name, $_.Count, ($_.Group | Measure-Object Maps -Sum).Sum, $l.Average, $l.Minimum, $l.Maximum
    }

if ($Csv) {
    $rows | Sort-Object Role, @{E = 'Luma'} | Export-Csv -Path $Csv -NoTypeInformation -Encoding UTF8
    '' ; "wrote $Csv" | Write-Host
}
$rows
