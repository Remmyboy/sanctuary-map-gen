<#
.SYNOPSIS
    Point-and-click front end for Convert-ScMap.ps1.

.DESCRIPTION
    Browse a folder of Supreme Commander maps, pick one or several, convert them
    into Sanctuary maps. Intended for community maps rather than the stock set -
    it defaults to the user Maps folder under My Games, which is where anything
    downloaded ends up.

    Rows are read with the header-only reader rather than decoding every
    heightmap. Conversion shells out to Convert-ScMap.ps1 in a child pwsh, one
    map at a time: Add-Type cannot redefine MapGen in a session that already
    has it, and this window compiled it to build the list.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'src' 'Import-MapGen.ps1')

# ---------------------------------------------------------------- paths ----

# Downloaded and community maps live under My Games; the stock set ships in the
# install directory. Offer whichever exists.
$candidateSources = @(
    (Join-Path $env:USERPROFILE 'Documents\My Games\Gas Powered Games\Supreme Commander Forged Alliance\Maps'),
    'F:\SteamLibrary\steamapps\common\Supreme Commander Forged Alliance\maps'
) | Where-Object { Test-Path $_ }

$candidateTargets = @(
    'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\engine\Sanctuary_Data\Maps'
) | Where-Object { Test-Path $_ }

$editorMaps = 'F:\SteamLibrary\steamapps\common\Sanctuary Shattered Sun Demo\map-editor\SanctuaryMapEditor_Data\Maps'

# ----------------------------------------------------------------- form ----

$form = New-Object Windows.Forms.Form
$form.Text = 'Supreme Commander -> Sanctuary map converter'
$form.Size = New-Object Drawing.Size(1000, 680)
$form.StartPosition = 'CenterScreen'
$form.MinimumSize = New-Object Drawing.Size(820, 560)

function New-Label($text, $x, $y, $w) {
    $l = New-Object Windows.Forms.Label
    $l.Text = $text; $l.Left = $x; $l.Top = $y; $l.Width = $w; $l.Height = 18
    $form.Controls.Add($l); $l
}

$null = New-Label 'Supreme Commander maps folder' 12 10 260

$txtSource = New-Object Windows.Forms.TextBox
$txtSource.Left = 12; $txtSource.Top = 30; $txtSource.Width = 860
$txtSource.Anchor = 'Top,Left,Right'
$txtSource.Text = if ($candidateSources) { $candidateSources[0] } else { '' }
$form.Controls.Add($txtSource)

$btnSource = New-Object Windows.Forms.Button
$btnSource.Text = 'Browse'; $btnSource.Left = 880; $btnSource.Top = 28; $btnSource.Width = 90
$btnSource.Anchor = 'Top,Right'
$form.Controls.Add($btnSource)

$list = New-Object Windows.Forms.ListView
$list.Left = 12; $list.Top = 60; $list.Width = 958; $list.Height = 300
$list.Anchor = 'Top,Left,Right,Bottom'
$list.View = 'Details'; $list.FullRowSelect = $true; $list.MultiSelect = $true
$list.HideSelection = $false
$null = $list.Columns.Add('Map', 300)
$null = $list.Columns.Add('Size', 70)
$null = $list.Columns.Add('Spawns', 60)
$null = $list.Columns.Add('Mass', 60)
$null = $list.Columns.Add('Water', 70)
$null = $list.Columns.Add('Ver', 45)
$null = $list.Columns.Add('Note', 320)
$form.Controls.Add($list)

$null = New-Label 'Sanctuary Maps folder' 12 372 200
$txtTarget = New-Object Windows.Forms.TextBox
$txtTarget.Left = 12; $txtTarget.Top = 392; $txtTarget.Width = 860
$txtTarget.Anchor = 'Top,Left,Right'
$txtTarget.Text = if ($candidateTargets) { $candidateTargets[0] } else { '' }
$form.Controls.Add($txtTarget)

$btnTarget = New-Object Windows.Forms.Button
$btnTarget.Text = 'Browse'; $btnTarget.Left = 880; $btnTarget.Top = 390; $btnTarget.Width = 90
$btnTarget.Anchor = 'Top,Right'
$form.Controls.Add($btnTarget)

$null = New-Label 'Biome' 12 426 50
$cboBiome = New-Object Windows.Forms.ComboBox
$cboBiome.Left = 60; $cboBiome.Top = 422; $cboBiome.Width = 130
$cboBiome.DropDownStyle = 'DropDownList'
$null = $cboBiome.Items.AddRange(@('Tropical', 'Highlands', 'Evergreen', 'Winter', 'Arid'))
$cboBiome.SelectedIndex = 0
$form.Controls.Add($cboBiome)

$chkEditor = New-Object Windows.Forms.CheckBox
$chkEditor.Text = 'Also install for the map editor'
$chkEditor.Left = 210; $chkEditor.Top = 424; $chkEditor.Width = 210
$chkEditor.Checked = (Test-Path $editorMaps)
$chkEditor.Enabled = (Test-Path $editorMaps)
$form.Controls.Add($chkEditor)

$chkProps = New-Object Windows.Forms.CheckBox
$chkProps.Text = 'Scatter trees and rocks'
$chkProps.Left = 430; $chkProps.Top = 424; $chkProps.Width = 180
$chkProps.Checked = $true
$form.Controls.Add($chkProps)

$btnConvert = New-Object Windows.Forms.Button
$btnConvert.Text = 'Convert selected'; $btnConvert.Left = 800; $btnConvert.Top = 420
$btnConvert.Width = 170; $btnConvert.Height = 30
$btnConvert.Anchor = 'Top,Right'
$form.Controls.Add($btnConvert)

$log = New-Object Windows.Forms.TextBox
$log.Left = 12; $log.Top = 460; $log.Width = 958; $log.Height = 170
$log.Anchor = 'Top,Left,Right,Bottom'
$log.Multiline = $true; $log.ScrollBars = 'Vertical'; $log.ReadOnly = $true
$log.Font = New-Object Drawing.Font('Consolas', 9)
$form.Controls.Add($log)

$lblStatus = New-Object Windows.Forms.Label
$lblStatus.Left = 12; $lblStatus.Top = 636; $lblStatus.Width = 700; $lblStatus.Height = 18
$lblStatus.Anchor = "Bottom,Left"
$form.Controls.Add($lblStatus)

function Write-Log($text) {
    $log.AppendText($text + [Environment]::NewLine)
    $log.SelectionStart = $log.TextLength
    $log.ScrollToCaret()
    [Windows.Forms.Application]::DoEvents()
}

# ----------------------------------------------------------- scan folder ----

function Get-MapRows([string]$root) {
    $rows = @()
    if (-not (Test-Path $root)) { return $rows }
    $dirs = @(Get-ChildItem $root -Directory | Sort-Object Name)
    $i = 0
    foreach ($dir in $dirs) {
        $i++
        # Reading a 4096 map header still means pulling the file off disk to reach
        # the water block behind the heightmap, so a big folder takes a while.
        if ($i % 5 -eq 0 -or $i -eq $dirs.Count) {
            $lblStatus.Text = "Scanning $i of $($dirs.Count): $($dir.Name)"
            [Windows.Forms.Application]::DoEvents()
        }
        $scmap = Get-ChildItem $dir.FullName -Filter *.scmap -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $scmap) { continue }
        $save = Get-ChildItem $dir.FullName -Filter *_save.lua -ErrorAction SilentlyContinue | Select-Object -First 1

        $row = [ordered]@{
            Name = $dir.Name; Path = $dir.FullName
            Size = ''; Spawns = 0; Mass = 0; Water = ''; Ver = ''; Note = ''
        }
        try {
            $h = [MapGen]::ReadScMapHeader($scmap.FullName)
            $row.Size = '{0}' -f $h.Size
            $row.Ver = '{0}' -f $h.VersionMinor
            $row.Water = if ($h.HasWater) { '{0:N1} m' -f $h.WaterElevation } else { 'dry' }
            if ($save) {
                $mk = [MapGen]::ReadScMarkers($save.FullName)
                $row.Spawns = @($mk | Where-Object { $_.Name -match '^ARMY_\d+$' }).Count
                $row.Mass = @($mk | Where-Object { $_.Type -eq 'Mass' -or $_.Type -eq 'Hydrocarbon' }).Count
            }
            else { $row.Note = 'no _save.lua - no markers' }
            if ($row.Spawns -lt 2 -and -not $row.Note) { $row.Note = 'campaign map (no spawn markers)' }
        }
        catch {
            $row.Note = $_.Exception.Message -replace '^.*?": "', '' -replace '"$', ''
        }
        $rows += , [pscustomobject]$row
    }
    $rows
}

function Refresh-List {
    $list.BeginUpdate()
    $list.Items.Clear()
    Write-Log ("Scanning {0} ..." -f $txtSource.Text)
    $rows = Get-MapRows $txtSource.Text
    foreach ($r in $rows) {
        $it = New-Object Windows.Forms.ListViewItem($r.Name)
        $null = $it.SubItems.Add($r.Size)
        $null = $it.SubItems.Add([string]$r.Spawns)
        $null = $it.SubItems.Add([string]$r.Mass)
        $null = $it.SubItems.Add($r.Water)
        $null = $it.SubItems.Add($r.Ver)
        $null = $it.SubItems.Add($r.Note)
        $it.Tag = $r.Path
        if ($r.Note) { $it.ForeColor = [Drawing.Color]::FromArgb(150, 90, 0) }
        $null = $list.Items.Add($it)
    }
    $list.EndUpdate()
    $ok = @($rows | Where-Object { -not $_.Note }).Count
    $lblStatus.Text = "{0} maps found, {1} ready to convert" -f $rows.Count, $ok
    Write-Log $lblStatus.Text
}

# --------------------------------------------------------------- actions ----

$btnSource.Add_Click({
        $d = New-Object Windows.Forms.FolderBrowserDialog
        $d.SelectedPath = $txtSource.Text
        if ($d.ShowDialog() -eq 'OK') { $txtSource.Text = $d.SelectedPath; Refresh-List }
    })

$btnTarget.Add_Click({
        $d = New-Object Windows.Forms.FolderBrowserDialog
        $d.SelectedPath = $txtTarget.Text
        if ($d.ShowDialog() -eq 'OK') { $txtTarget.Text = $d.SelectedPath }
    })

$btnConvert.Add_Click({
        if ($list.SelectedItems.Count -eq 0) {
            [Windows.Forms.MessageBox]::Show('Pick at least one map from the list.', 'Nothing selected') | Out-Null
            return
        }
        if (-not (Test-Path $txtTarget.Text)) {
            [Windows.Forms.MessageBox]::Show('That Sanctuary Maps folder does not exist.', 'Bad destination') | Out-Null
            return
        }

        $btnConvert.Enabled = $false
        $script = Join-Path $here 'Convert-ScMap.ps1'
        $done = 0; $failed = 0

        foreach ($item in $list.SelectedItems) {
            $src = [string]$item.Tag
            Write-Log ''
            Write-Log ('=== ' + $item.Text)

            # Each conversion runs in its own pwsh, because Add-Type cannot
            # redefine MapGen in a session that already has it - and this window
            # loaded it to build the list.
            $targets = @(@{ Root = $txtTarget.Text; Ext = '.santp' })
            if ($chkEditor.Checked) { $targets += @{ Root = $editorMaps; Ext = '.sanprop' } }

            $bad = $false
            foreach ($t in $targets) {
                $argv = @(
                    '-NoProfile', '-File', $script,
                    '-Source', $src,
                    '-MapsRoot', $t.Root,
                    '-Biome', $cboBiome.SelectedItem,
                    '-PropExtension', $t.Ext,
                    '-Force'
                )
                if (-not $chkProps.Checked) { $argv += '-NoProps' }

                $out = & pwsh @argv 2>&1
                foreach ($line in $out) {
                    $s = [string]$line
                    if ($s.Trim()) { Write-Log ('  ' + $s.Trim()) }
                }
                if ($LASTEXITCODE -ne 0) { $bad = $true }
            }
            if ($bad) { $failed++ } else { $done++ }
        }

        Write-Log ''
        Write-Log ("Done. {0} converted, {1} failed." -f $done, $failed)
        Write-Log 'Restart Sanctuary to pick up new maps.'
        $btnConvert.Enabled = $true
    })

$list.Add_DoubleClick({ $btnConvert.PerformClick() })

$form.Add_Shown({ $form.Activate(); if ($txtSource.Text) { Refresh-List } })
[void]$form.ShowDialog()
