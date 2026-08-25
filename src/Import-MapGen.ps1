<#
    Compiles the C# core into the current session. Dot-source it:

        . (Join-Path $PSScriptRoot 'src\Import-MapGen.ps1')

    The generator, the mask library, the validators and the Supreme Commander
    reader are all `public static partial class MapGen`, so they have to be
    compiled together as one type. The `using` lines are stripped because
    Add-Type takes a single compilation unit and they would end up interleaved
    between class bodies.

    Add-Type refuses to define a type twice in one session, so a script that
    dot-sources this must not also be dot-sourced by another that already has.
#>
$ErrorActionPreference = 'Stop'

if (-not ('MapGen' -as [type])) {
    $sources = 'MapGen.cs', 'PathedMesas.cs', 'Generator.cs', 'Resources.cs', 'Terrain.cs', 'ScMap.cs', 'ScMapEnvironment.cs', 'ScMapProps.cs', 'ScMapTextures.cs', 'ScMapPropScan.cs', 'ScPropImport.cs', 'ScMapDecalScan.cs', 'ScMapSplat.cs', 'Bc7.cs', 'Dxt.cs', 'DdsMean.cs', 'DdsWrite.cs', 'Bc3.cs', 'DdsDecode.cs'
    $body = foreach ($cs in $sources) {
        $p = Join-Path $PSScriptRoot $cs
        if (-not (Test-Path $p)) { throw "missing source '$p'" }
        (Get-Content $p) | Where-Object { $_ -notmatch '^\s*using\s+[\w.]+\s*;' }
    }
    Add-Type -Language CSharp -TypeDefinition (
        "using System;`nusing System.IO;`nusing System.Collections.Generic;`n" + ($body -join "`n"))
}

# Repo root and the tools directory, for scripts that shell out to the validators.
$MapGenRoot  = Split-Path -Parent $PSScriptRoot
$MapGenTools = Join-Path $MapGenRoot 'tools'
