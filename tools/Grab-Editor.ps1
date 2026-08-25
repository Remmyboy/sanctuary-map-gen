<#
.SYNOPSIS
    Screenshot the Sanctuary map editor window.

.DESCRIPTION
    The preview renders we generate are our own arithmetic drawn with our own
    assumptions. They cannot show a texturing fault, because they use a table of
    approximate layer colours rather than the game's shaders. The only honest
    check on how a map looks is the game looking at it, so grab the editor
    window itself.
#>
[CmdletBinding()]
param(
    [string]$Out = 'C:\code\sanctuary-map-gen\maps\editor.png',
    [string]$ProcessName = 'SanctuaryMapEditor'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@

$p = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw "no $ProcessName window found - is the map editor running?" }

$h = $p.MainWindowHandle
[void][Win]::ShowWindow($h, 9)          # SW_RESTORE, in case it is minimised
[void][Win]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 700           # let it repaint after coming forward

$r = New-Object Win+RECT
[void][Win]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $ht = $r.B - $r.T
if ($w -le 0 -or $ht -le 0) { throw "window rect is empty" }

$bmp = New-Object Drawing.Bitmap $w, $ht
$g = [Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object Drawing.Size $w, $ht))
$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { $null = New-Item -ItemType Directory -Path $dir -Force }
$bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

"{0}  ({1}x{2})" -f $Out, $w, $ht | Write-Host
