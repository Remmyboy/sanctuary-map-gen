<#
.SYNOPSIS
    Mean colour and brightness of every stratum albedo in the game.

.DESCRIPTION
    A biome table is a slope ramp, and a ramp only reads as terrain if
    neighbouring steps are close in tone. Put a near-black rock next to pale
    sand and the boundary looks like a line drawn on the ground - which is what
    the grey rings on the converted desert maps turned out to be. The slope
    bands were where they should be; the textures either side of them were far
    apart in brightness.

    The shipped maps never do this. Their ramp is grass -> heather -> grass, all
    within a few points of each other, and rock only appears at the top of the
    slope range where a real cliff face justifies it.

    The albedos are DDS despite the .tga paths in the .sanmap - the game's
    texture lookup strips the extension and probes .dds first. Thirty-eight are
    DXT1 and fifteen are BC7.

    DXT1 keeps two RGB565 endpoints at a fixed offset in every block, so
    averaging them estimates the texture mean closely. BC7 does not: its
    endpoints are packed differently in each of eight block modes, and reading
    them at a fixed offset returns noise - which it did, giving every BC7
    texture roughly the same wrong luminance near 122. src\Bc7.cs decodes them
    for real.
#>
[CmdletBinding()]
param(
    [string]$Sanpack = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Gamedata\Environment.sanpack',
    [string]$Match = 'Stratum/.*_albedo'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')

function Get-BlockTone([byte[]]$b, [int]$dataStart, [int]$blockBytes, [int]$colourOffset) {
    $sr = 0.0; $sg = 0.0; $sbl = 0.0; $n = 0
    # Every block, up to a cap - these are 2048 square and we only need a mean.
    for ($i = $dataStart; $i + $blockBytes -le $b.Length -and $n -lt 40000; $i += $blockBytes) {
        $o = $i + $colourOffset
        foreach ($k in 0, 2) {
            $c = [int]$b[$o + $k] -bor ([int]$b[$o + $k + 1] -shl 8)
            $sr += (($c -shr 11) -band 0x1F) * 255.0 / 31.0
            $sg += (($c -shr 5) -band 0x3F) * 255.0 / 63.0
            $sbl += ($c -band 0x1F) * 255.0 / 31.0
            $n++
        }
    }
    if ($n -eq 0) { return $null }
    @{ R = $sr / $n; G = $sg / $n; B = $sbl / $n }
}

$zip = [IO.Compression.ZipFile]::OpenRead($Sanpack)
try {
    $rows = @()
    foreach ($e in $zip.Entries) {
        if ($e.FullName -notmatch $Match) { continue }
        $ms = New-Object IO.MemoryStream
        $s = $e.Open(); $s.CopyTo($ms); $s.Close()
        $b = $ms.ToArray(); $ms.Dispose()
        if ($b.Length -lt 148) { continue }
        if ([Text.Encoding]::ASCII.GetString($b, 0, 4) -ne 'DDS ') { continue }

        $w = [BitConverter]::ToInt32($b, 16)
        $h = [BitConverter]::ToInt32($b, 12)
        $fcc = [Text.Encoding]::ASCII.GetString($b, 84, 4)

        # DXT1: 8-byte blocks, endpoints first. DXT5 and the BC7/BC3 forms
        # behind a DX10 header: 16-byte blocks with the alpha block first, so
        # the colour endpoints sit at offset 8. BC7 does not store endpoints
        # this way, so those come out approximate - flagged in the output.
        switch ($fcc) {
            'DXT1' { $tone = Get-BlockTone $b 128 8 0;  $kind = 'DXT1' }
            'DXT5' { $tone = Get-BlockTone $b 128 16 8; $kind = 'DXT5' }
            'DX10' {
                # dxgiFormat 98 is BC7_UNORM. Endpoint averaging does not work
                # on BC7 - its endpoints are packed differently in each of the
                # eight block modes, so reading them at a fixed offset returned
                # noise and every BC7 texture scored about the same wrong value.
                # Decode them properly instead; see src\Bc7.cs.
                $dxgi = [BitConverter]::ToInt32($b, 128)
                if ($dxgi -eq 98) {
                    $rr = 0.0; $gg = 0.0; $bb = 0.0
                    if ([Bc7]::SurfaceMean($b, 148, 40000, [ref]$rr, [ref]$gg, [ref]$bb)) {
                        $tone = @{ R = $rr; G = $gg; B = $bb }; $kind = 'BC7'
                    }
                    else { continue }
                }
                else { $tone = Get-BlockTone $b 148 16 8; $kind = "DX10:$dxgi" }
            }
            default { continue }
        }
        if (-not $tone) { continue }

        $rows += , [pscustomobject]@{
            Name = ($e.FullName -replace '.*/', '') -replace '_albedo.*', ''
            Fmt  = $kind
            Size = "${w}x${h}"
            R    = [int]$tone.R; G = [int]$tone.G; B = [int]$tone.B
            Lum  = [int](0.299 * $tone.R + 0.587 * $tone.G + 0.114 * $tone.B)
            Warm = [int]($tone.R - $tone.B)
        }
    }
}
finally { $zip.Dispose() }

'  {0,-36} {1,-6} {2,4} {3,4} {4,4}  {5,4} {6,5}' -f 'texture', 'fmt', 'R', 'G', 'B', 'lum', 'warm' | Write-Host
'  {0,-36} {1,-6} {2,4} {3,4} {4,4}  {5,4} {6,5}' -f ('-' * 36), '------', '----', '----', '----', '----', '-----' | Write-Host
$rows | Sort-Object Lum | ForEach-Object {
    '  {0,-36} {1,-6} {2,4} {3,4} {4,4}  {5,4} {6,5}' -f $_.Name, $_.Fmt, $_.R, $_.G, $_.B, $_.Lum, $_.Warm | Write-Host
}
