<#
.SYNOPSIS
    Validates a .sanmap by deserialising it with the game's own EM.Map.SanMap.

.DESCRIPTION
    The only reliable check on a generated map is the one the game performs:
    Newtonsoft's JsonConvert.PopulateObject against the real SanMap type. Field
    types are strict in one direction - an int field will not accept "128.0" -
    and a failure there throws inside SanMap.LoadJson before the first progress
    event, which is why a bad map leaves the editor sitting at 0% forever.

    This loads UnityEngine.CoreModule, Newtonsoft.Json and EM.Map into a
    collectible load context and replays that exact call. No game process, and
    nothing is written.

.EXAMPLE
    .\Test-Sanmap.ps1 -Path ...\Maps\Serpent_Crossing\Serpent_Crossing.sanmap
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline)]
    [string[]]$Path,

    [string]$Managed = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\map-editor\SanctuaryMapEditor_Data\Managed',

    # Also confirm the sibling Textures\ files exist and are the right size.
    [switch]$CheckTextures,

    # Confirm every gamedata path the map references (stratum textures, decal
    # and prop blueprints, skybox) actually exists in that build's .sanpacks.
    # The engine build ships NO .sanprop at all, and a missing prop blueprint
    # aborts RunMapSetup before the alloy spots are created - so an unchecked
    # reference costs the map every resource point, silently.
    [string]$GamedataDir,

    # Also decode with the game's Lua json parser (needs KeraLua from the
    # shipped map generator).
    [switch]$LuaCheck
)

begin {
    $ErrorActionPreference = 'Stop'
    $global:SanmapManaged = $Managed
    $failures = 0

    $packIndex = $null
    if ($GamedataDir) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $packIndex = [System.Collections.Generic.HashSet[string]]::new()
        foreach ($pack in Get-ChildItem (Join-Path $GamedataDir '*.sanpack')) {
            $zip = [IO.Compression.ZipFile]::OpenRead($pack.FullName)
            foreach ($e in $zip.Entries) { $null = $packIndex.Add($e.FullName) }
            $zip.Dispose()
        }
        Write-Host ("Indexed {0} gamedata entries from {1}" -f $packIndex.Count, $GamedataDir)
    }

    # Texture lookups are extension-agnostic - Load.cs strips the extension and
    # probes .dds first, so a map saying .tga resolves to the packed .dds.
    # Blueprints are NOT: Engine.GetFileContent takes the path verbatim, so a
    # .sanprop/.sandecal has to match exactly or the map breaks at runtime.
    # A path beginning "map/" is not a gamedata reference at all. Data.PathToID
    # rewrites it to "Maps/<current map>/..." and Data.InitMapFiles registers
    # everything in the map folder, so it resolves against the map's own files.
    # That is how a converted map carries its source textures.
    function Test-Asset([string]$p, [bool]$Exact, [string]$MapDir) {
        if ($p -match '^(?i)map/') {
            if (-not $MapDir) { return $false }
            $full = Join-Path $MapDir ($p -replace '^(?i)map/', '')
            if (Test-Path $full) { return $true }
            if ($Exact) { return $false }
            $stem = [IO.Path]::ChangeExtension($full, $null).TrimEnd('.')
            foreach ($ext in '.dds', '.png', '.tga', '.jpg', '.bmp', '.exr') {
                if (Test-Path ($stem + $ext)) { return $true }
            }
            return $false
        }
        if ($packIndex.Contains($p)) { return $true }
        if ($Exact) { return $false }
        $stem = [IO.Path]::ChangeExtension($p, $null).TrimEnd('.')
        foreach ($ext in '.dds', '.png', '.tga', '.jpg', '.bmp', '.exr') {
            if ($packIndex.Contains($stem + $ext)) { return $true }
        }
        return $false
    }
}

process {
    foreach ($p in $Path) {
        $name = Split-Path -Leaf $p
        try {
            $ctx = [System.Runtime.Loader.AssemblyLoadContext]::new([guid]::NewGuid().ToString(), $true)
            $ctx.add_Resolving({
                param($c, $n)
                $dll = Join-Path $global:SanmapManaged ($n.Name + '.dll')
                if (Test-Path $dll) { return $c.LoadFromAssemblyPath($dll) }
                return $null
            })
            $null = $ctx.LoadFromAssemblyPath((Join-Path $Managed 'UnityEngine.CoreModule.dll'))
            $nj   = $ctx.LoadFromAssemblyPath((Join-Path $Managed 'Newtonsoft.Json.dll'))
            $t    = $ctx.LoadFromAssemblyPath((Join-Path $Managed 'EM.Map.dll')).GetType('EM.Map.SanMap')

            # SanMap's only constructor takes a path and would start loading.
            $obj = [Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($t)
            $mi  = $nj.GetType('Newtonsoft.Json.JsonConvert').GetMethods() |
                   Where-Object { $_.Name -eq 'PopulateObject' -and $_.GetParameters().Count -eq 2 }

            $a = [object[]]::new(2)
            $a[0] = [IO.File]::ReadAllText($p)
            $a[1] = $obj
            $null = $mi.Invoke($null, $a)

            $w    = $t.GetField('width').GetValue($obj)
            $hmr  = $t.GetField('heightmapResolution').GetValue($obj)
            if ($hmr -eq 0) { $hmr = $w + 1 }

            $props  = ($t.GetField('props').GetValue($obj)  | ForEach-Object { $_.transforms.Length } | Measure-Object -Sum).Sum
            $decals = ($t.GetField('decals').GetValue($obj) | ForEach-Object { $_.transforms.Length } | Measure-Object -Sum).Sum

            # Buffered: nothing is reported as passing until every check has run.
            $detail = [System.Collections.Generic.List[string]]::new()
            $detail.Add(("      {0}x{1} m, height {2}, hmRes {3}, water {4}, {5} stratums, {6} props, {7} decals" -f `
                $w, $t.GetField('length').GetValue($obj), $t.GetField('height').GetValue($obj), $hmr,
                $(if ($t.GetField('hasWater').GetValue($obj)) { $t.GetField('waterLevel').GetValue($obj) } else { 'none' }),
                $t.GetField('stratumLayers').GetValue($obj).Length, $props, $decals))

            if ($CheckTextures) {
                $tex = Join-Path (Split-Path -Parent $p) 'Textures'
                $raw = Join-Path $tex 'heightmap.raw'
                if (-not (Test-Path $raw)) { throw "missing Textures\heightmap.raw" }
                $want = $hmr * $hmr * 2
                $got  = (Get-Item $raw).Length
                if ($got -ne $want) { throw "heightmap.raw is $got bytes, heightmapResolution $hmr needs $want" }
                foreach ($f in 'stratums_1_4', 'stratums_5_8', 'tint_colors', 'tint_geometry') {
                    $tp = Join-Path $tex "$f.tga"
                    if (-not (Test-Path $tp)) { throw "missing Textures\$f.tga" }
                    $fs = [IO.File]::OpenRead($tp); $h = [byte[]]::new(18); $null = $fs.Read($h, 0, 18); $fs.Close()
                    $tw = [BitConverter]::ToUInt16($h, 12); $th = [BitConverter]::ToUInt16($h, 14)
                    if ($h[2] -ne 2 -or $h[16] -ne 32) { throw "$f.tga is not uncompressed 32bpp true-colour" }
                    $expect = 18 + $tw * $th * 4
                    if ((Get-Item $tp).Length -ne $expect) { throw "$f.tga header says ${tw}x${th} but the file is $((Get-Item $tp).Length) bytes, not $expect" }
                    $detail.Add(("      {0,-14} {1}x{2}" -f $f, $tw, $th))
                }

                # A stratum slot that carries splat weight has to be painted by
                # something that can actually be ground. This check exists
                # because a converted map pointed its unused slots at a 4x4
                # placeholder and then handed them the weights of the used ones:
                # the map came out a smooth featureless wash, and everything
                # else here passed it, because every file was present and every
                # path resolved. Weight on a placeholder is the fault.
                # Sanctuary ships no DXT3 at all - 470 textures, none of them -
                # because Unity has TextureFormat.DXT1 and DXT5 and nothing for
                # BC2. A DXT3 texture is not an error, it is a blank white
                # surface, which is how Seton's Clutch spent two rounds looking
                # like snow. Supreme Commander has 221 of them.
                foreach ($dd in Get-ChildItem $tex -Filter *.dds -EA SilentlyContinue) {
                    $hb = [byte[]]::new(88)
                    $hs = [IO.File]::OpenRead($dd.FullName); $null = $hs.Read($hb, 0, 88); $hs.Close()
                    if ([Text.Encoding]::ASCII.GetString($hb, 0, 4) -ne 'DDS ') { continue }
                    if ([Text.Encoding]::ASCII.GetString($hb, 84, 4) -eq 'DXT3') {
                        throw ("{0} is DXT3 - Unity has no format for BC2, so it loads as a blank white surface" -f $dd.Name)
                    }
                }

                $w8 = New-Object double[] 9
                foreach ($pair in @(@{ F = 'stratums_1_4'; L = 1 }, @{ F = 'stratums_5_8'; L = 5 })) {
                    $sb = [IO.File]::ReadAllBytes((Join-Path $tex ('{0}.tga' -f $pair.F)))
                    $n = 0
                    # BGRA on disk is [L3,L2,L1,L4] relative to the pair base.
                    for ($k = 18; $k + 3 -lt $sb.Length; $k += 4) {
                        $w8[$pair.L + 2] += $sb[$k];     $w8[$pair.L + 1] += $sb[$k + 1]
                        $w8[$pair.L]     += $sb[$k + 2]; $w8[$pair.L + 3] += $sb[$k + 3]
                        $n++
                    }
                    if ($n) { $pair.L..($pair.L + 3) | ForEach-Object { $w8[$_] = $w8[$_] / $n } }
                }
                $layers = $t.GetField('stratumLayers').GetValue($obj)
                for ($li = 1; $li -le 8 -and $li -lt $layers.Length; $li++) {
                    if ($w8[$li] -lt 1.0) { continue }
                    $ap = $layers[$li].albedo.path
                    if ($ap -notmatch '^(?i)map/') { continue }
                    $af = Join-Path (Split-Path -Parent $p) ($ap -replace '^(?i)map/', '')
                    if (-not (Test-Path $af)) {
                        $stem = [IO.Path]::ChangeExtension($af, $null).TrimEnd('.')
                        $af = @('.dds', '.tga', '.png') | ForEach-Object { $stem + $_ } |
                              Where-Object { Test-Path $_ } | Select-Object -First 1
                        if (-not $af) { continue }
                    }
                    $ab = [byte[]]::new(32)
                    $afs = [IO.File]::OpenRead($af); $null = $afs.Read($ab, 0, 32); $afs.Close()
                    $aw = if ([Text.Encoding]::ASCII.GetString($ab, 0, 4) -eq 'DDS ') {
                              [BitConverter]::ToInt32($ab, 16)
                          } else { [BitConverter]::ToUInt16($ab, 12) }
                    if ($aw -lt 64) {
                        throw ('stratum layer {0} carries splat weight (mean {1:n0}/255) but its albedo {2} is only {3}px - that is a placeholder, not a ground texture' -f `
                               $li, $w8[$li], (Split-Path -Leaf $af), $aw)
                    }
                }
            }

            if ($packIndex) {
                $tex = [System.Collections.Generic.List[string]]::new()   # extension-agnostic
                $bp  = [System.Collections.Generic.List[string]]::new()   # must match exactly
                foreach ($s in $t.GetField('stratumLayers').GetValue($obj)) {
                    foreach ($m in $s.albedo, $s.normal, $s.mask) { if ($m -and $m.path) { $tex.Add($m.path) } }
                }
                foreach ($d in $t.GetField('decals').GetValue($obj)) { if ($d.blueprintPath) { $bp.Add($d.blueprintPath) } }
                foreach ($pr in $t.GetField('props').GetValue($obj))  { if ($pr.blueprintPath) { $bp.Add($pr.blueprintPath) } }
                $sky = $t.GetField('skybox').GetValue($obj)
                if ($sky -and $sky.path -and $sky.path -ne 'empty') { $tex.Add($sky.path) }

                $missing = @()
                $mapDir = Split-Path -Parent $p
                $missing += @($tex | Sort-Object -Unique | Where-Object { -not (Test-Asset $_ $false $mapDir) })
                $missing += @($bp  | Sort-Object -Unique | Where-Object { -not (Test-Asset $_ $true  $mapDir) })
                if ($missing.Count) {
                    throw ("{0} referenced asset(s) missing from this build's gamedata:`n        {1}" -f `
                           $missing.Count, ($missing -join "`n        "))
                }
                $detail.Add(("      assets        {0} texture + {1} blueprint references, all resolve" -f ($tex | Sort-Object -Unique).Count, ($bp | Sort-Object -Unique).Count))
            }

            # Newtonsoft is not the only parser the map must satisfy: mapUtils.lua
            # decodes it with the game's own json.lua, and that one is stricter.
            if ($LuaCheck) {
                & (Join-Path (Split-Path -Parent $PSCommandPath) 'Test-LuaJson.ps1') -Path $p | Out-Null
            }
            Write-Host ("PASS  {0}" -f $name) -ForegroundColor Green
            $detail | ForEach-Object { Write-Host $_ }
        }
        catch {
            $failures++
            Write-Host ("FAIL  {0}" -f $name) -ForegroundColor Red
            Write-Host ("      {0}" -f $_.Exception.GetBaseException().Message) -ForegroundColor Red
        }
    }
}

end {
    if ($failures) { Write-Error "$failures map(s) failed validation." }
}
