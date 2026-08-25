<#
.SYNOPSIS
    Copy CC0 substitutes for a map's Supreme Commander textures into its folder.

.DESCRIPTION
    The counterpart to Export-ScTextures. That one extracts the map's own
    textures out of env.scd, which looks right but carries someone else's art
    and four format problems with it. This one looks each texture up in the
    substitution table and copies a CC0 material instead.

    Three things get better, beyond the licence:

      * Every layer gets its own normal map. Supreme Commander shares four
        across eight and does not record which belongs where, so the source
        path has been putting one normal on all nine layers.
      * Nothing is DXT3, because the library is encoded by us.
      * The tone correction is measured rather than guessed. The source path
        uses a flat 0.37/0.35/0.32 for every layer; here diffuseRemap is solved
        per channel so the substitute renders the colour the original renders.

    Layers with no entry in the table fall back to the nearest role, which
    Match-Textures has already resolved - so a path always returns something.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][AllowEmptyString()][string[]]$TexturePaths,
    [Parameter(Mandatory)][string]$DestDir,
    [string]$PackDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'texturepack'),
    [string]$MapCsv  = (Join-Path (Split-Path -Parent $PSScriptRoot) 'docs\texture-map.csv'),
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $MapCsv))  { throw "no substitution table at '$MapCsv' - run Match-Textures.ps1" }
if (-not (Test-Path $PackDir)) { throw "no texture pack at '$PackDir' - run Build-TexturePack.ps1" }
if (-not (Test-Path $DestDir)) { $null = New-Item -ItemType Directory -Path $DestDir -Force }

$table = @{}
foreach ($r in Import-Csv $MapCsv) { $table[$r.ScPath.ToLowerInvariant()] = $r }

$names    = @{}
$normals  = @{}
$masks    = @{}
$remaps   = @{}
$missing  = @()
$copied   = 0
$inexact  = 0

foreach ($p in $TexturePaths) {
    if ([string]::IsNullOrWhiteSpace($p)) { continue }
    $key = $p.ToLowerInvariant()
    $row = $table[$key]
    if (-not $row) { $missing += $key; continue }

    $albedoSrc = Join-Path $PackDir "$($row.Cc0)_albedo.dds"
    $normalSrc = Join-Path $PackDir "$($row.Cc0)_normal.dds"
    $maskSrc   = Join-Path $PackDir "$($row.Cc0)_mask.dds"
    if (-not (Test-Path $albedoSrc)) { $missing += $key; continue }

    foreach ($pair in @(@{ Src = $albedoSrc; Key = 'a' }, @{ Src = $normalSrc; Key = 'n' }, @{ Src = $maskSrc; Key = 'm' })) {
        if (-not (Test-Path $pair.Src)) { continue }
        $leaf = Split-Path -Leaf $pair.Src
        $out  = Join-Path $DestDir $leaf
        if (-not (Test-Path $out)) { Copy-Item $pair.Src $out; $copied++ }
        switch ($pair.Key) {
            'a' { $names[$p]   = $leaf }
            'n' { $normals[$p] = $leaf }
            'm' { $masks[$p]   = $leaf }
        }
    }
    $remaps[$p] = @([double]$row.RemapR, [double]$row.RemapG, [double]$row.RemapB)
    if ($row.Exact -ne 'True') { $inexact++ }
}

# Sanctuary wants a _mask per layer and Supreme Commander has no equivalent, so
# one neutral one is shared. See Write-NeutralMask.ps1 for what "neutral" turned
# out to mean - it is not mid-grey, and getting that wrong put a wet sheen over
# every converted map.
$maskName = 'sc_neutral_mask.tga'
$maskPath = Join-Path $DestDir $maskName
& (Join-Path $PSScriptRoot 'Write-NeutralMask.ps1') -Path $maskPath -Force

if (-not $Quiet) {
    "  CC0 substitutes: {0} files, {1} layers, {2} with a real mask, {3} inexact role" -f $copied, $names.Count, $masks.Count, $inexact | Write-Host
    if ($missing.Count) { "  no substitute for {0}" -f ($missing -join ', ') | Write-Host }
}

[pscustomobject]@{
    Copied = $copied; Missing = $missing; Names = $names
    Normals = $normals; Masks = $masks; Remaps = $remaps; MaskName = $maskName; Inexact = $inexact
}
