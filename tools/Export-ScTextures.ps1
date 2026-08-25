<#
.SYNOPSIS
    Copy the textures a Supreme Commander map uses into the converted map's own
    folder, and write the neutral mask Sanctuary expects alongside them.

.DESCRIPTION
    A converted map used to be repainted with one of five Sanctuary biomes, so
    it arrived looking like our preset with someone else's hills. Supreme
    Commander ships 402 stratum textures across twelve environments and the map
    already names the ones it wants, so carry those instead.

    Three facts make it work:

      * Data.PathToID rewrites a path beginning "map/" to
        "Maps/<current map>/...", and Data.InitMapFiles registers everything in
        the map folder recursively, so a map can reference its own assets.
      * Load.cs strips the extension and probes .dds first, so a .dds can sit
        behind a .tga path. That is already how the shipped maps work.
      * env.scd is a plain zip.

    Normals are passed in rather than derived. Supreme Commander does not keep
    one per layer - it shares four across the eight, listed separately in the
    .scmap - so turning "x_albedo.dds" into "x_normals.dds" finds nothing.

    Sanctuary also wants a _mask per layer, which Supreme Commander has no
    equivalent for, so a single neutral one is written and shared.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ScdPath,
    # Empty entries are normal - a map need not use all eight layers.
    [Parameter(Mandatory)][AllowEmptyString()][string[]]$TexturePaths,
    [AllowEmptyString()][string[]]$NormalPaths = @(),
    [Parameter(Mandatory)][string]$DestDir,

    # Root of the folder the source map sits in. About one community map in ten
    # ships its own textures rather than using the shipped set, and names them
    # "/maps/<map>.vNNNN/env/layers/x.dds" - a path env.scd knows nothing about.
    # Given the root, those resolve on disk.
    [string]$MapsRoot,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')

if (-not (Test-Path $DestDir)) { $null = New-Item -ItemType Directory -Path $DestDir -Force }

$zip = [IO.Compression.ZipFile]::OpenRead($ScdPath)
$copied = 0
$transcoded = 0
$missing = @()
$names = @{}
try {
    # Archive keys are case-sensitive and the map's paths are not, and leading
    # slashes differ, so index once in lower case without the slash.
    $index = @{}
    foreach ($e in $zip.Entries) { $index[$e.FullName.ToLowerInvariant().TrimStart('/')] = $e }

    # Leaf name -> the source path that claimed it. Two different textures can
    # share a file name once map-local ones are in play (env/layers/grass and
    # env/decals/grass), and silently copying one over the other would put the
    # wrong ground on a layer without anything failing.
    $claimed = @{}

    foreach ($p in ($TexturePaths + $NormalPaths)) {
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        $key = $p.ToLowerInvariant().TrimStart('/')

        $leaf = [IO.Path]::GetFileName($key)
        if ($claimed.ContainsKey($leaf) -and $claimed[$leaf] -ne $key) {
            $n = 2
            while ($claimed.ContainsKey(('{0}_{1}{2}' -f [IO.Path]::GetFileNameWithoutExtension($leaf), $n, [IO.Path]::GetExtension($leaf)))) { $n++ }
            $leaf = '{0}_{1}{2}' -f [IO.Path]::GetFileNameWithoutExtension($leaf), $n, [IO.Path]::GetExtension($leaf)
        }

        $out = Join-Path $DestDir $leaf
        $entry = $index[$key]
        $disk = $null
        if (-not $entry -and $MapsRoot -and $key -like 'maps/*') {
            $cand = Join-Path $MapsRoot ($key -replace '^maps/', '')
            if (Test-Path $cand) { $disk = $cand }
        }
        if (-not $entry -and -not $disk) { $missing += $key; continue }

        # Only recorded once the file is known to exist. A name here with no
        # file behind it becomes a stratum layer pointing at nothing.
        $names[$p] = $leaf
        $claimed[$leaf] = $key

        if (Test-Path $out) { continue }        # several layers share a texture

        if ($disk) { $bytes = [IO.File]::ReadAllBytes($disk) }
        else {
            $st = $entry.Open()
            $ms = New-Object IO.MemoryStream
            $st.CopyTo($ms); $st.Close()
            $bytes = $ms.ToArray(); $ms.Dispose()
        }

        # One Supreme Commander texture in eleven is DXT3, which Unity has no
        # format for and Sanctuary therefore draws as a blank white surface -
        # silently, because a texture that fails to load is not an error.
        # See src\Dxt.cs.
        if ([MapGen]::TranscodeDxt3ToDxt5($bytes)) { $transcoded++ }
        [IO.File]::WriteAllBytes($out, $bytes)
        $copied++
    }
}
finally { $zip.Dispose() }

# A neutral mask. Sanctuary's stratum layers each name one and Supreme Commander
# has nothing corresponding, so write one flat image and point every layer at
# it. This used to be mid-grey, on the reasoning that the middle of an unknown
# range is the safe place to sit. See Write-NeutralMask.ps1 for why that was
# wrong and what the shipped masks actually average to.
$maskName = 'sc_neutral_mask.tga'
$maskPath = Join-Path $DestDir $maskName
& (Join-Path $PSScriptRoot 'Write-NeutralMask.ps1') -Path $maskPath -Force

if (-not $Quiet) {
    "  textures copied: {0}" -f $copied | Write-Host
    if ($missing.Count) {
        "  not in the archive ({0}): {1}" -f $missing.Count, (($missing | Select-Object -Unique -First 3) -join ', ') | Write-Host
    }
}

# Leaf names keyed by original path, so the caller can build stratumLayers
# without re-deriving them.
[pscustomobject]@{ Copied = $copied; Missing = $missing; Names = $names; MaskName = $maskName; Transcoded = $transcoded }
