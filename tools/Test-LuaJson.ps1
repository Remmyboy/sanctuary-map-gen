<#
.SYNOPSIS
    Parses a .sanmap with the game's OWN Lua json decoder.

.DESCRIPTION
    Newtonsoft is not the only parser a map has to satisfy. mapUtils.lua does
    `local data = json.decode(file)` with engine/LJ/lua/common/systems/json.lua,
    and if that returns nil the map dies at LoadMapData with

        common/mapUtils.lua:22: attempt to index local 'data' (a nil value)

    which in game looks like "the map didn't load" - and, because GameInfo.MapData
    never gets populated, like "I spawned in the middle of the water", since with
    no Spawn markers the commander lands on the map default.

    Newtonsoft accepts things that decoder does not, so this runs the real thing
    via KeraLua + lua54.dll (both shipped with the map generator).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromPipeline)][string[]]$Path,
    [string]$GameRoot = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo'
)
begin {
    $ErrorActionPreference = 'Stop'
    $gen = Join-Path $GameRoot 'engine\Sanctuary-Map-Generation'
    $env:PATH = "$gen;$env:PATH"
    Add-Type -Path (Join-Path $gen 'KeraLua.dll')
    $jsonLua = Join-Path $GameRoot 'engine\LJ\lua\common\systems\json.lua'
    $failures = 0
}
process {
    foreach ($p in $Path) {
        $name = Split-Path -Leaf $p
        $L = [KeraLua.Lua]::new($true)
        try {
            # json.lua ends in `return json`, so load it as a chunk and call it.
            if ($L.DoString(("json = (function() " + [IO.File]::ReadAllText($jsonLua) + " end)()"))) {
                throw "could not load json.lua: " + $L.ToString(-1, $false)
            }
            $L.PushString([IO.File]::ReadAllText($p))
            $L.SetGlobal('MAPTEXT')
            if ($L.DoString('DECODED, ERRPOS, ERRMSG = json.decode(MAPTEXT)')) {
                throw "decode threw: " + $L.ToString(-1, $false)
            }
            $L.GetGlobal('DECODED')
            $isNil = $L.IsNil(-1)
            $L.Pop(1)
            if ($isNil) {
                $L.GetGlobal('ERRMSG'); $msg = $L.ToString(-1, $false); $L.Pop(1)
                $L.GetGlobal('ERRPOS'); $pos = $L.ToNumber(-1); $L.Pop(1)
                throw ("json.decode returned nil at byte {0}: {1}" -f $pos, $msg)
            }
            # spot-check the fields LoadMapData reads first
            $L.DoString('CHK = tostring(DECODED.mapVersion) .. "/" .. tostring(DECODED.name) .. "/" .. tostring(DECODED.width)') | Out-Null
            $L.GetGlobal('CHK'); $chk = $L.ToString(-1, $false); $L.Pop(1)
            Write-Host ("LUA-OK   {0}   (mapVersion/name/width = {1})" -f $name, $chk) -ForegroundColor Green
        }
        catch {
            $failures++
            Write-Host ("LUA-FAIL {0}" -f $name) -ForegroundColor Red
            Write-Host ("         {0}" -f $_.Exception.GetBaseException().Message) -ForegroundColor Red
        }
        finally { $L.Close() }
    }
}
end { if ($failures) { Write-Error "$failures map(s) rejected by the game's Lua json decoder." } }
