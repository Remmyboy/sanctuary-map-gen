<#
.SYNOPSIS
    Build a CC0 ground-material library that Sanctuary can load directly.

.DESCRIPTION
    Converted maps currently carry Supreme Commander's own textures, extracted
    from env.scd. That looks right, but it drags four problems along: one
    texture in eleven is DXT3, which Unity cannot load at all; there is no
    _mask, so every layer shares one flat grey placeholder; four normals are
    shared across eight layers with no record of which belongs where; and the
    art is 512x512 where Sanctuary ships 2048x2048. It also means a converted
    map folder contains someone else's art and cannot be shared.

    So: source the ground from ambientCG instead, which is CC0, and convert it
    to the format the game already reads - DXT1 with a full mip chain.

    The library is small on purpose. The corpus references 329 distinct
    textures, but they collapse to about ten material roles, and within a role
    the variants are mostly the same material at a different tone. Tone is
    something we can set per layer through diffuseRemap, so a handful of
    materials per role covers the lot. Match-Textures.ps1 does that mapping.

.NOTES
    Downloads roughly 9 MB per material from ambientCG and caches the zips, so
    a re-run costs nothing. Everything here is CC0: no attribution required,
    and the resulting maps are free to share.
#>
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'texturepack'),
    [string]$Variant = '1K-JPG',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..' 'src' 'Import-MapGen.ps1')
Add-Type -AssemblyName System.Drawing, System.IO.Compression.FileSystem

# Chosen for material, not for tone - tone is corrected per layer later. Where
# a role has several entries they differ in pattern and grain, so neighbouring
# layers on one map do not read as the same surface twice.
#

$materials = [ordered]@{
    # Rock029 is the red one. Rock009 was tried and cut by eye: its mean and
    # contrast both measure fine, but the surface is moss patches and white
    # lichen - a forest boulder - and no average-based metric can see that. Without them the red barrens family
    # - 16 textures across 124 map references - had to be faked by crushing the
    # blue channel to the clamp floor, which greys the red off rather than
    # saturating it. The hue matcher picks them up on its own.
    rock   = 'Rock030', 'Rock051', 'Rock058', 'Rock020', 'Rock029'
    grass  = 'Grass004', 'Grass001', 'Ground037'
    sand   = 'Ground054', 'Ground080', 'Ground078'
    gravel = 'Gravel023', 'Gravel040', 'Ground110', 'Gravel025'
    dirt   = 'Ground048', 'Ground103', 'Ground106', 'Ground107'
    snow   = 'Snow006', 'Snow010A', 'Snow002'
    crack  = 'Ground093C', 'Ground095A'
    # Crystalline is stylised sci-fi with no photographic equivalent, but ice
    # is its nearest honest neighbour - translucent, faceted, blue-white - and
    # Onyx carries the dark glassy end. Far closer than the old crystal->rock
    # fallback that greyed those maps out.
    crystal = 'Ice002', 'Ice003', 'Ice004', 'Onyx006'
    lava   = 'Lava004', 'Lava001'
}

$cache = Join-Path $OutDir 'cache'
foreach ($d in $OutDir, $cache) { if (-not (Test-Path $d)) { $null = New-Item -ItemType Directory -Path $d -Force } }


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
function Get-Bgra([IO.Stream]$stream, [ref]$w, [ref]$h) {
    $bmp = [Drawing.Bitmap]::FromStream($stream)
    try {
        $w.Value = $bmp.Width; $h.Value = $bmp.Height
        $rect = New-Object Drawing.Rectangle 0, 0, $bmp.Width, $bmp.Height
        $d = $bmp.LockBits($rect, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $buf = New-Object byte[] ($bmp.Width * $bmp.Height * 4)
        [Runtime.InteropServices.Marshal]::Copy($d.Scan0, $buf, 0, $buf.Length)
        $bmp.UnlockBits($d)
        return $buf
    } finally { $bmp.Dispose() }
}

$rows = @()
$total = ($materials.Values | ForEach-Object { $_ }).Count
$i = 0
foreach ($role in $materials.Keys) {
    foreach ($id in $materials[$role]) {
        $i++
        $zipPath = Join-Path $cache "$id`_$Variant.zip"
        if (-not (Test-Path $zipPath)) {
            $url = "https://ambientcg.com/get?file=$id`_$Variant.zip"
            Write-Host ("[{0,2}/{1}] downloading {2}" -f $i, $total, $id)
            try {
                Invoke-WebRequest -Uri $url -OutFile $zipPath -TimeoutSec 600 -UserAgent 'sanctuary-map-gen/1.0'
            } catch {
                Write-Host ("        FAILED: {0}" -f $_.Exception.Message) -ForegroundColor Red
                continue
            }
        }

        $albedoOut = Join-Path $OutDir "cc0_$($id.ToLower())_albedo.dds"
        $normalOut = Join-Path $OutDir "cc0_$($id.ToLower())_normal.dds"
        $maskOut   = Join-Path $OutDir "cc0_$($id.ToLower())_mask.dds"
        $z = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            foreach ($job in @(
                    @{ Match = '_Color\.jpg$';    Out = $albedoOut },
                    @{ Match = '_NormalGL\.jpg$'; Out = $normalOut })) {
                if ((Test-Path $job.Out) -and -not $Force) { continue }
                $e = $z.Entries | Where-Object { $_.FullName -match $job.Match } | Select-Object -First 1
                if (-not $e) { Write-Host ("        no {0} in {1}" -f $job.Match, $id) -ForegroundColor Yellow; continue }
                $ms = New-Object IO.MemoryStream
                $st = $e.Open(); $st.CopyTo($ms); $st.Close(); $ms.Position = 0
                $w = 0; $h = 0
                $bgra = Get-Bgra $ms ([ref]$w) ([ref]$h)
                $ms.Dispose()
                [IO.File]::WriteAllBytes($job.Out, [MapGen]::WriteDxt1Dds($bgra, $w, $h))
            }
            # The mask. Sanctuary's stratum mask is Unity HDRP's mask map - the
            # engine binary names the channels itself, _MaskmapMetal /
            # _MaskmapAO / _MaskmapSmoothness - so the layout is fixed:
            #
            #     R metallic   G ambient occlusion   B detail   A smoothness
            #
            # ambientCG ships an AO map and a roughness map, which is exactly
            # G and A. Ground is not metal and there is no detail map, so R and
            # B are zero. Smoothness is the inverse of roughness.
            #
            # This replaces the single flat placeholder every layer used to
            # share, which is the reason every surface had the same material
            # response no matter what it was made of.
            if (-not (Test-Path $maskOut) -or $Force) {
                $aoE = $z.Entries | Where-Object { $_.FullName -match '_AmbientOcclusion\.jpg$' } | Select-Object -First 1
                $roE = $z.Entries | Where-Object { $_.FullName -match '_Roughness\.jpg$' }        | Select-Object -First 1
                # Four of the packs ship roughness but no AO. Smoothness is the
                # channel that actually matters here, so build the mask anyway
                # with G at 255, meaning no occlusion - still far better than
                # falling back to one flat placeholder for the whole map.
                if ($roE) {
                    $rw = 0; $rh = 0
                    $ms2 = New-Object IO.MemoryStream; $s2 = $roE.Open(); $s2.CopyTo($ms2); $s2.Close(); $ms2.Position = 0
                    $ro = Get-Bgra $ms2 ([ref]$rw) ([ref]$rh); $ms2.Dispose()

                    $ao = $null; $aw = $rw; $ah = $rh
                    if ($aoE) {
                        $ms1 = New-Object IO.MemoryStream; $s1 = $aoE.Open(); $s1.CopyTo($ms1); $s1.Close(); $ms1.Position = 0
                        $ao = Get-Bgra $ms1 ([ref]$aw) ([ref]$ah); $ms1.Dispose()
                        if ($aw -ne $rw -or $ah -ne $rh) { $ao = $null }
                    }

                    $mask = New-Object byte[] ($rw * $rh * 4)
                    for ($k = 0; $k -lt $mask.Length; $k += 4) {
                        $mask[$k]     = 0                                      # B - detail, unused
                        $mask[$k + 1] = if ($ao) { $ao[$k + 1] } else { 255 }  # G - ambient occlusion
                        $mask[$k + 2] = 0                                      # R - metallic; ground is not metal
                        $mask[$k + 3] = [byte](255 - $ro[$k + 1])              # A - smoothness = 1 - roughness
                    }
                    [IO.File]::WriteAllBytes($maskOut, [MapGen]::WriteDxt5Dds($mask, $rw, $rh))
                }
            }
        } finally { $z.Dispose() }

        if (-not (Test-Path $albedoOut)) { continue }
        $info = [MapGen]::ReadDdsInfo([IO.File]::ReadAllBytes($albedoOut))
        $rows += [pscustomobject]@{
            Name   = "cc0_$($id.ToLower())"
            Role   = $role
            Source = $id
            Luma   = [math]::Round($info.Luma, 1)
            Std    = (Get-LumaStd ([IO.File]::ReadAllBytes($albedoOut)))
            StdC   = (Get-LumaStdCoarse ([IO.File]::ReadAllBytes($albedoOut)))
            R      = [math]::Round($info.R, 1)
            G      = [math]::Round($info.G, 1)
            B      = [math]::Round($info.B, 1)
            Size   = '{0}x{1}' -f $info.Width, $info.Height
            Normal = (Test-Path $normalOut)
            Mask   = (Test-Path $maskOut)
        }
        Write-Host ("[{0,2}/{1}] {2,-22} {3,-7} luma {4,5:n1}  rgb {5,3:n0},{6,3:n0},{7,3:n0}" -f `
                $i, $total, $id, $role, $info.Luma, $info.R, $info.G, $info.B)
    }
}

$manifest = Join-Path $OutDir 'manifest.csv'
$rows | Export-Csv -Path $manifest -NoTypeInformation -Encoding UTF8
''
'{0} materials built into {1}' -f $rows.Count, $OutDir | Write-Host
'  {0} with a normal map, {1} with a real mask' -f @($rows | Where-Object Normal).Count, @($rows | Where-Object Mask).Count | Write-Host
'  manifest: {0}' -f $manifest | Write-Host
$rows
