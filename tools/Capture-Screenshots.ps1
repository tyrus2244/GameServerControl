# Captures the 5 README screenshots automatically.
#
# Usage:
#   1. Make sure all 5 windows are open (see "Required windows" below).
#   2. Run this script from PowerShell — it'll prompt before each capture so you can
#      bring the right window to focus / arrange anything that's covered.
#   3. PNGs land in docs\screenshots\ with the exact filenames the README expects.
#
# Required windows BEFORE running:
#   - GameServerControl WPF dashboard  ("Game Server Control")
#   - Players window                     (Click 👥 Players on Satisfactory in the dashboard)
#   - Create Server wizard               (Click 🚀 Create Server)
#   - Mods window                        (Click 🧩 Mods on Valheim)
#   - A browser tab pointing at http://100.90.15.50:5099 ("Game Server Control" + browser)
#
# All 5 can be open at once — the script brings each to foreground individually.

param([string]$OutDir = "$PSScriptRoot\..\docs\screenshots")

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CaptureWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Write-Host ""
Write-Host "Output folder: $OutDir" -ForegroundColor Cyan
Write-Host ""

function Snap {
    param(
        [string]$TitlePattern,        # regex to match against MainWindowTitle
        [string]$OutFile,
        [string]$ProcessNameFilter,   # optional — disambiguate when multiple windows have the same title (e.g. dashboard vs browser tab)
        [string]$Hint                 # human-readable for the user
    )

    Write-Host "==> $Hint" -ForegroundColor Cyan
    $proc = Get-Process | Where-Object {
        $_.MainWindowHandle -ne 0 -and
        $_.MainWindowTitle -match $TitlePattern -and
        (-not $ProcessNameFilter -or $_.ProcessName -match $ProcessNameFilter)
    } | Select-Object -First 1

    if (-not $proc) {
        Write-Host "    SKIPPED: no window matching title /$TitlePattern/ (process filter: $ProcessNameFilter)" -ForegroundColor Yellow
        return
    }

    Write-Host ("    found: '$($proc.MainWindowTitle)' (PID $($proc.Id), $($proc.ProcessName).exe)") -ForegroundColor DarkGray

    $h = $proc.MainWindowHandle
    if ([CaptureWin]::IsIconic($h)) { [CaptureWin]::ShowWindow($h, 9) | Out-Null }   # SW_RESTORE
    [CaptureWin]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 700   # let the window paint after focus change

    $r = New-Object CaptureWin+RECT
    [CaptureWin]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left
    $hh = $r.Bottom - $r.Top
    if ($w -le 0 -or $hh -le 0) {
        Write-Host "    SKIPPED: window has zero size (minimized?)" -ForegroundColor Yellow
        return
    }

    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    $dst = Join-Path $OutDir $OutFile
    $bmp.Save($dst, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "    saved $OutFile ($w x $hh)" -ForegroundColor Green
}

Write-Host "Press Enter to start. Make sure the 5 windows listed at the top of this script are open." -ForegroundColor Yellow
Read-Host | Out-Null

# 1) Dashboard. Process filter "GameServerControl" so we don't pick up the browser tab whose
#    page title is also "Game Server Control".
Snap -TitlePattern '^Game Server Control$' `
     -ProcessNameFilter 'GameServerControl' `
     -OutFile 'dashboard.png' `
     -Hint 'Capturing dashboard (WPF client)'

# 2) Players window
Snap -TitlePattern '^Players$' `
     -ProcessNameFilter 'GameServerControl' `
     -OutFile 'players.png' `
     -Hint 'Capturing Players window'

# 3) Create Server wizard
Snap -TitlePattern '^Create Server$' `
     -ProcessNameFilter 'GameServerControl' `
     -OutFile 'create-wizard.png' `
     -Hint 'Capturing Create Server wizard'

# 4) Mods window — title is "Mods — <ServerName>"
Snap -TitlePattern '^Mods' `
     -ProcessNameFilter 'GameServerControl' `
     -OutFile 'mods-window.png' `
     -Hint 'Capturing Mods window'

# 5) Web UI — Edge/Chrome/Firefox tab. Match the page-title prefix + any browser process name.
Snap -TitlePattern 'Game Server Control.*(Edge|Chrome|Firefox|Brave)' `
     -ProcessNameFilter 'msedge|chrome|firefox|brave' `
     -OutFile 'web-ui.png' `
     -Hint 'Capturing web UI (browser)'

Write-Host ""
Write-Host "Done. Files written to $OutDir" -ForegroundColor Green
Write-Host ""
Write-Host "Next step — commit + push:" -ForegroundColor Yellow
Write-Host '    cd C:\Users\tyrus\Claude\GameServerControl' -ForegroundColor Gray
Write-Host '    git add docs/screenshots/*.png' -ForegroundColor Gray
Write-Host '    git commit -m "Add README screenshots"' -ForegroundColor Gray
Write-Host '    git push origin main' -ForegroundColor Gray
