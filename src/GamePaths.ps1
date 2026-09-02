<#
.SYNOPSIS
    Find the two game installs, wherever this machine keeps them.

.DESCRIPTION
    The scripts used to default their paths to this machine's F:\ install,
    which works for exactly one person. Anyone else got a converter that
    listed their maps happily and then failed every one of them on a missing
    env.scd - the path in the error being a drive they do not have.

    So: detect. Forged Alliance is found through the registry keys the retail
    installer and Steam's uninstall entry write, then every Steam library
    (steamapps\libraryfolders.vdf lists them all), then the obvious folders on
    each fixed drive. Sanctuary the same way, minus the registry.

    Every function returns $null rather than throwing, so a caller can decide
    whether a missing install is fatal - the CC0 texture mode does not need
    Forged Alliance at all.

    Dot-source this, then use the defaults in the body of a script rather than
    in its param block: PowerShell binds parameter defaults before the script
    body runs, so a function called there does not exist yet.
#>

function Get-SteamLibraryRoot {
    $roots = @()
    try {
        $steam = (Get-ItemProperty 'HKCU:\SOFTWARE\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
        if ($steam) {
            $steam = $steam -replace '/', '\'
            $roots += $steam
            $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
            if (Test-Path $vdf) {
                foreach ($m in ([regex]'"path"\s+"([^"]+)"').Matches((Get-Content $vdf -Raw))) {
                    # The vdf escapes its separators, so unescape literally -
                    # -replace would read both sides as regex.
                    $roots += $m.Groups[1].Value.Replace('\\', '\')
                }
            }
        }
    }
    catch { }
    # Non-Steam-registry fallback: the conventional library folder name on
    # every fixed drive.
    foreach ($d in [IO.DriveInfo]::GetDrives()) {
        if ($d.DriveType -ne 'Fixed' -or -not $d.IsReady) { continue }
        $roots += (Join-Path $d.RootDirectory.FullName 'SteamLibrary')
    }
    $roots | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
}

# The names this game installs under: Steam, the retail/GOG installer, and
# Forged Alliance Forever, which is what most community mappers run.
$script:FaFolderNames = @(
    'Supreme Commander Forged Alliance',
    'Supreme Commander - Forged Alliance',
    'SupremeCommanderForgedAlliance',
    'Forged Alliance Forever',
    'ForgedAllianceForever'
)

function Find-FaInstall {
    $cands = @()
    foreach ($k in @(
        @{ Key = 'HKLM:\SOFTWARE\WOW6432Node\THQ\Gas Powered Games\Supreme Commander Forged Alliance'; Name = 'InstallPath' },
        @{ Key = 'HKLM:\SOFTWARE\THQ\Gas Powered Games\Supreme Commander Forged Alliance'; Name = 'InstallPath' },
        @{ Key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 9420'; Name = 'InstallLocation' },
        @{ Key = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 9420'; Name = 'InstallLocation' })) {
        try {
            $v = (Get-ItemProperty $k.Key -ErrorAction SilentlyContinue).($k.Name)
            if ($v) { $cands += ([string]$v).Trim().Trim('"') }
        }
        catch { }
    }
    foreach ($lib in Get-SteamLibraryRoot) {
        foreach ($n in $script:FaFolderNames) { $cands += (Join-Path $lib "steamapps\common\$n") }
    }
    foreach ($d in [IO.DriveInfo]::GetDrives()) {
        if ($d.DriveType -ne 'Fixed' -or -not $d.IsReady) { continue }
        $r = $d.RootDirectory.FullName
        foreach ($mid in @('', 'Games', 'Program Files (x86)', 'Program Files', 'Program Files (x86)\THQ', 'Games\THQ')) {
            foreach ($n in $script:FaFolderNames) {
                $cands += (Join-Path $r $(if ($mid) { Join-Path $mid $n } else { $n }))
            }
        }
    }
    foreach ($c in $cands) {
        if ($c -and (Test-Path (Join-Path $c 'gamedata\env.scd'))) { return $c }
    }
    $null
}

function Find-ScdPath {
    $fa = Find-FaInstall
    if ($fa) { return (Join-Path $fa 'gamedata\env.scd') }
    $null
}

function Find-SanctuaryInstall {
    foreach ($lib in Get-SteamLibraryRoot) {
        foreach ($n in @('Sanctuary Shattered Sun Playtest', 'Sanctuary Shattered Sun Demo', 'Sanctuary Shattered Sun')) {
            $p = Join-Path $lib "steamapps\common\$n"
            if (Test-Path (Join-Path $p 'engine\Sanctuary_Data\Maps')) { return $p }
        }
    }
    $null
}

function Get-SanctuaryEngineMaps {
    $s = Find-SanctuaryInstall
    if ($s) { return (Join-Path $s 'engine\Sanctuary_Data\Maps') }
    $null
}

<#
    The map editor tree, or $null. The Playtest build dropped it, so a caller
    must treat its absence as normal rather than as a broken install.
#>
function Get-SanctuaryEditorMaps {
    $s = Find-SanctuaryInstall
    if (-not $s) { return $null }
    $p = Join-Path $s 'map-editor\SanctuaryMapEditor_Data\Maps'
    if (Test-Path $p) { return $p }
    $null
}

<#
    Every folder that holds Supreme Commander maps: the community/downloaded
    vault under My Games first, since that is where a mapper's own work lands,
    then the stock set inside the install.
#>
function Find-FaMapRoots {
    $roots = @(Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Gas Powered Games\Supreme Commander Forged Alliance\Maps')
    $fa = Find-FaInstall
    if ($fa) { $roots += (Join-Path $fa 'maps') }
    $roots | Where-Object { Test-Path $_ } | Select-Object -Unique
}
