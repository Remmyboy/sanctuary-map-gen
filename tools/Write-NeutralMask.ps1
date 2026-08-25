<#
.SYNOPSIS
    Write the shared neutral stratum mask, matching what the game's own masks
    average to.

.DESCRIPTION
    Sanctuary gives every stratum layer a _mask alongside its albedo and normal.
    Supreme Commander has no equivalent, so a converted map has to supply one,
    and it used to be flat mid-grey on the reasoning that the middle of a range
    is the safe place to sit when you do not know what the channels mean.

    It is not. Measured across all 127 masks the game ships:

        R  mean   7.5   (113 of 127 are 2 or below)
        G  mean 218.5   (min 92)
        B  mean 149.5
        A  mean  36.4   (110 DXT5 masks, min 0, max 229)

    Mid-grey is wrong in every channel and badly wrong in three. Alpha is the
    worst of them and was the last to be found, because the first pass measured
    only RGB - the masks are DXT5 and BC7, so the alpha is a whole channel
    hiding behind a decoder that did not read it. Opaque against a shipped mean
    of 36 is seven times too much of whatever alpha drives, and it looked it:
    polished wet stone on every cliff.

    So the neutral mask is the average shipped mask rather than the midpoint of
    an unknown range. That is still an assumption about what the channels do,
    but it is anchored to what the game actually ships instead of to nothing.

    A better answer for the CC0 path is a real per-material mask built from the
    roughness map ambientCG provides - but that needs the channel semantics
    confirmed, not inferred, so it is deliberately not done here.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Path,
    # Channel means of the 127 masks in Environment.sanpack.
    [int]$R = 0,
    [int]$G = 219,
    [int]$B = 150,
    [int]$A = 36,
    [int]$Resolution = 4,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if ((Test-Path $Path) -and -not $Force) { return }

$hdr = New-Object byte[] 18
$hdr[2]  = 2                                    # uncompressed true-colour
$hdr[12] = $Resolution -band 0xff; $hdr[13] = $Resolution -shr 8
$hdr[14] = $Resolution -band 0xff; $hdr[15] = $Resolution -shr 8
$hdr[16] = 32                                   # bits per pixel
$hdr[17] = 0x28                                 # 8 alpha bits, top-left origin

$px = New-Object byte[] ($Resolution * $Resolution * 4)
for ($i = 0; $i -lt $px.Length; $i += 4) {
    $px[$i] = [byte]$B; $px[$i + 1] = [byte]$G; $px[$i + 2] = [byte]$R; $px[$i + 3] = [byte]$A
}

$dir = Split-Path -Parent $Path
if ($dir -and -not (Test-Path $dir)) { $null = New-Item -ItemType Directory -Path $dir -Force }
$fs = [IO.File]::Create($Path)
$fs.Write($hdr, 0, 18); $fs.Write($px, 0, $px.Length); $fs.Close()
