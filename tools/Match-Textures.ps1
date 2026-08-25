<#
.SYNOPSIS
    Map every Supreme Commander stratum texture onto a CC0 substitute, with the
    per-channel correction that makes it land on the original's colour.

.DESCRIPTION
    The corpus references 329 distinct textures and the CC0 library has 23, so
    the mapping does the reducing. Two things make that ratio work.

    Role. A texture's material is legible from its name across all twelve
    environments - des_rock01, evgrass005, tund_snow - so each one picks a
    substitute of the same material.

    Colour. Within a role the FA variants differ mostly by tone: sand runs from
    luma 31 to 197 across 29 textures. diffuseRemap is a per-channel multiply
    the shader already applies, so a substitute can be pushed onto the
    original's exact average colour. Solving

        cc0_channel * remap = fa_channel * base

    for remap gives a substitute that renders the colour the FA texture renders
    today, which is what makes a converted map still look like itself.

    base is the flat 0.37/0.35/0.32 the FA path uses now, so this preserves the
    appearance we already have rather than introducing a second change at the
    same time.

    Where a role has several candidates the closest in hue wins, so the remap
    stays a gentle correction rather than dragging a grey rock to green.

.NOTES
    crystal, lava-adjacent and unclassified materials have no honest CC0
    equivalent. They are mapped onto the nearest plausible role and listed
    separately at the end - those are the ones that will not look the same.
#>
[CmdletBinding()]
param(
    [string]$ScCsv       = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\sc-textures.csv'),
    [string]$PackDir     = (Join-Path (Split-Path -Parent $PSScriptRoot) 'texturepack'),
    [string]$Out         = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\texture-map.csv'),
    # The multiply the source-texture path applies today. Matching it means the
    # substitution is the only change.
    [double[]]$BaseRemap = @(0.37, 0.35, 0.32)
)

$ErrorActionPreference = 'Stop'

$sc   = Import-Csv $ScCsv
$pack = Import-Csv (Join-Path $PackDir 'manifest.csv')

# Roles the library does not carry, and the nearest thing it does. Recorded
# rather than silently folded in, so the report can say which maps are affected.
$fallback = @{ }

function Chroma([double]$r, [double]$g, [double]$b) {
    $s = $r + $g + $b
    if ($s -le 0) { return @(0.333, 0.333, 0.333) }
    # The inner parentheses are load-bearing: PowerShell's comma binds tighter
    # than division, so @($r / $s, $g / $s, ...) divides by an array.
    return @(($r / $s), ($g / $s), ($b / $s))
}

# Pairs judged by eye, overriding the scored pick. The metrics measure mean,
# contrast and feature size; they cannot measure "soft". Canis proved it in
# the field: its two "gravel" layers cover 80% of the map each and render in
# FA as gentle warm sand-mottle, and every scored candidate - real gravel,
# then fine-but-confetti dirt - read busier than the original. Keep this
# list short and only for pairs actually compared in the field.
$eyeOverrides = @{
    '/env/desert/layers/des_gravel01_albedo.dds' = 'cc0_ground080'
    '/env/desert/layers/des_gravel_albedo.dds'   = 'cc0_ground054'
}

$rows = @()
$substituted = @{}
foreach ($t in $sc) {
    $role = $t.Role
    $used = $role
    if ($fallback.ContainsKey($role)) { $used = $fallback[$role] }
    # Loose ground is a continuum, not a taxonomy. Supreme Commander's desert
    # "gravel" textures are sand with a few pebbles - they cover 80% of Canis
    # and render as gentle warm mottle - while its evergreen gravels really
    # are stone chips. A candidate pool locked to the name's role forced the
    # sandy ones onto real grey gravel and turned whole deserts busy. Adjacent
    # roles join the pool and the measured stats pick within it; a texture
    # that is genuinely coarse still lands on coarse.
    $pool = switch ($used) {
        'sand'   { @('sand', 'gravel') }
        'gravel' { @('gravel', 'sand', 'dirt') }
        'dirt'   { @('dirt', 'gravel') }
        'crack'  { @('crack', 'dirt') }
        default  { @($used) }
    }
    $cands = @($pack | Where-Object { $pool -contains $_.Role })
    if (-not $cands.Count) { $cands = @($pack | Where-Object { $_.Role -eq 'rock' }) }
    if (-not $cands.Count) { continue }

    $ct = Chroma ([double]$t.R) ([double]$t.G) ([double]$t.B)
    $tStd = if ($t.PSObject.Properties['Std'] -and $t.Std) { [double]$t.Std } else { 45.0 }
    $best = $null; $bestD = [double]::MaxValue
    foreach ($c in $cands) {
        $cc = Chroma ([double]$c.R) ([double]$c.G) ([double]$c.B)
        $d = 0.0
        for ($i = 0; $i -lt 3; $i++) { $d += [math]::Pow($ct[$i] - $cc[$i], 2) }
        # Contrast term. Mean-colour matching alone picked a mossy lichen rock
        # for a smooth desert one and a bold pebble field for fine gravel -
        # the means matched, the character did not. Chroma distances here run
        # about 0.000..0.01, luma-std differences 0..40, so 0.001 per unit
        # prices character above hue - the per-channel remap corrects hue
        # exactly, but nothing downstream can fix a texture that varies twice
        # as loudly as the original.
        $cStd = if ($c.PSObject.Properties['Std'] -and $c.Std) { [double]$c.Std } else { 45.0 }
        $d += [math]::Abs($tStd - $cStd) * 0.001
        # Feature size, separately from amplitude: coarse-scale contrast
        # survives downsampling only when the features are large. Without this
        # term a fine confetti gravel and a bold pebble field score the same,
        # and six eg_gravel textures landed on coarse pebbles.
        $tC = if ($t.PSObject.Properties['StdC'] -and $t.StdC) { [double]$t.StdC } else { 20.0 }
        $cC = if ($c.PSObject.Properties['StdC'] -and $c.StdC) { [double]$c.StdC } else { 20.0 }
        $d += [math]::Abs($tC - $cC) * 0.001
        if ($d -lt $bestD) { $bestD = $d; $best = $c }
    }

    $ovr = $eyeOverrides[$t.Path.ToLowerInvariant()]
    if ($ovr) {
        $o = $pack | Where-Object { $_.Name -eq $ovr } | Select-Object -First 1
        if ($o) { $best = $o }
    }

    # Per channel: what multiply puts the substitute on the original's colour?
    # The floor is low on purpose. Saturation lives in the gap between channels,
    # so a red ground legitimately needs its blue multiplied by very little; a
    # floor set to protect luminance instead greys the colour off.
    $remap = @(0.0, 0.0, 0.0)
    $srcCh  = @([double]$t.R, [double]$t.G, [double]$t.B)
    $dstCh  = @([double]$best.R, [double]$best.G, [double]$best.B)
    $clamped = $false
    for ($i = 0; $i -lt 3; $i++) {
        $v = if ($dstCh[$i] -gt 1) { $BaseRemap[$i] * $srcCh[$i] / $dstCh[$i] } else { $BaseRemap[$i] }
        $c = [math]::Max(0.03, [math]::Min(0.95, $v))
        if ([math]::Abs($c - $v) -gt 1e-6) { $clamped = $true }
        $remap[$i] = [math]::Round($c, 4)
    }

    $substituted[$best.Name] = 1 + $substituted[$best.Name]
    $rows += [pscustomobject]@{
        ScPath   = $t.Path
        ScStem   = $t.Stem
        ScRole   = $role
        UsedRole = $used
        Exact    = ($role -eq $used)
        Maps     = [int]$t.Maps
        Cc0      = $best.Name
        RemapR   = $remap[0]
        RemapG   = $remap[1]
        RemapB   = $remap[2]
        Clamped  = $clamped
        # What the substitute will actually render at, against what the
        # original renders at now. This is the number that says whether the
        # mapping worked.
        WantLuma = [math]::Round(0.299 * $srcCh[0] * $BaseRemap[0] + 0.587 * $srcCh[1] * $BaseRemap[1] + 0.114 * $srcCh[2] * $BaseRemap[2], 1)
        GotLuma  = [math]::Round(0.299 * $dstCh[0] * $remap[0] + 0.587 * $dstCh[1] * $remap[1] + 0.114 * $dstCh[2] * $remap[2], 1)
    }
}

$rows | Export-Csv -Path $Out -NoTypeInformation -Encoding UTF8

$err = $rows | ForEach-Object { [math]::Abs($_.GotLuma - $_.WantLuma) }
'{0} textures mapped onto {1} CC0 materials' -f $rows.Count, $substituted.Count | Write-Host
'  rendered-tone error: mean {0:n2}, worst {1:n1} (out of 255)' -f `
    (($err | Measure-Object -Average).Average), (($err | Measure-Object -Maximum).Maximum) | Write-Host
'  {0} needed clamping' -f @($rows | Where-Object Clamped).Count | Write-Host
''
'Role substitutions that are not like-for-like:' | Write-Host
$rows | Where-Object { -not $_.Exact } | Group-Object ScRole | ForEach-Object {
    '  {0,-8} -> {1,-6} {2,3} textures, {3,3} map-refs' -f `
        $_.Name, $_.Group[0].UsedRole, $_.Count, (($_.Group | Measure-Object Maps -Sum).Sum) | Write-Host
}
''
'Most-used substitutes:' | Write-Host
$rows | Group-Object Cc0 | Sort-Object { ($_.Group | Measure-Object Maps -Sum).Sum } -Descending |
    Select-Object -First 8 | ForEach-Object {
        '  {0,-18} {1,3} textures, {2,4} map-refs' -f $_.Name, $_.Count, (($_.Group | Measure-Object Maps -Sum).Sum) | Write-Host
    }
''
"wrote $Out" | Write-Host
