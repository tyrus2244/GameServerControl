# GameServerControl — Windows CLIENT-ONLY installer (no Admin needed).
#
# Run from any PowerShell:
#   iwr https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/windows/install-client.ps1 | iex
#
# What it does:
#   1. Ensures .NET 8 Desktop Runtime is present (user-scoped install if missing).
#   2. Downloads the latest GameServerControl-Client-windows.zip from GitHub Releases.
#   3. Extracts to %LOCALAPPDATA%\GameServerControl\Client (user-writable, no Admin needed).
#   4. Creates a desktop shortcut.
#
# No service registration, no firewall changes, no agent. Use this on the machine you'll
# point at a remote agent (your gaming desktop, laptop, etc.).
#
# To uninstall: delete %LOCALAPPDATA%\GameServerControl and the desktop shortcut.

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$repo       = if ($env:GSC_REPO)    { $env:GSC_REPO }    else { 'tyrus2244/GameServerControl' }
$installRt  = if ($env:GSC_INSTALL) { $env:GSC_INSTALL } else { Join-Path $env:LOCALAPPDATA 'GameServerControl' }
$clientDir  = Join-Path $installRt 'Client'
$pinnedTag  = $env:GSC_VERSION

function Write-Step($msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK: $msg" -ForegroundColor Green }
function Write-Skip($msg) { Write-Host "    -- $msg" -ForegroundColor DarkGray }
function Write-Warn2($msg){ Write-Host "    !! $msg" -ForegroundColor Yellow }

Write-Host ""
Write-Host "GameServerControl client installer (no admin required)"
Write-Host "------------------------------------------------------"

# ---- .NET 8 Desktop Runtime check (WPF needs the Desktop bundle, not just ASP.NET) ----
Write-Step ".NET 8 Desktop Runtime"
$hasDesktop = & dotnet --list-runtimes 2>$null | Select-String 'Microsoft.WindowsDesktop.App 8\.'
if (-not $hasDesktop) {
    Write-Warn2 "Missing — downloading Microsoft's bootstrapper…"
    $dotnetUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe'
    $dotnetExe = Join-Path $env:TEMP 'gsc-dotnet8-desktop.exe'
    Invoke-WebRequest -Uri $dotnetUrl -OutFile $dotnetExe

    # Microsoft's installer prompts for UAC. We try /install /quiet which works user-scoped
    # on most builds; if elevation IS needed, the UAC prompt appears once.
    Write-Step "Running .NET 8 Desktop Runtime installer (may show a UAC prompt)"
    $p = Start-Process -FilePath $dotnetExe -ArgumentList '/install', '/quiet', '/norestart' -PassThru -Wait
    if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) {
        Write-Warn2 ".NET installer exited $($p.ExitCode). The dashboard may not launch."
        Write-Warn2 "If clicking the shortcut does nothing, install .NET 8 Desktop Runtime manually:"
        Write-Warn2 "  https://dotnet.microsoft.com/download/dotnet/8.0"
    } else {
        Write-Ok ".NET 8 Desktop Runtime installed."
    }
} else {
    Write-Ok "Already present."
}

# ---- Resolve release ----
Write-Step "Resolving release from GitHub"
$apiUrl = if ($pinnedTag) {
    "https://api.github.com/repos/$repo/releases/tags/$pinnedTag"
} else {
    "https://api.github.com/repos/$repo/releases/latest"
}
$headers = @{ 'User-Agent' = 'gsc-installer' }
$release = Invoke-RestMethod -Uri $apiUrl -Headers $headers
Write-Ok "Tag: $($release.tag_name)  ·  Published: $($release.published_at)"

$clientAsset = $release.assets | Where-Object { $_.name -eq 'GameServerControl-Client-windows.zip' } | Select-Object -First 1
if (-not $clientAsset) { throw "Release $($release.tag_name) is missing GameServerControl-Client-windows.zip." }

# ---- Download ----
Write-Step "Downloading client"
$tmpZip = Join-Path $env:TEMP "gsc-client-$([Guid]::NewGuid().ToString('N')).zip"
Invoke-WebRequest -Uri $clientAsset.browser_download_url -OutFile $tmpZip
Write-Ok ("Downloaded (" + [Math]::Round((Get-Item $tmpZip).Length / 1MB, 2) + " MB)"
)

# ---- Extract ----
Write-Step "Installing client to $clientDir"
if (Test-Path $clientDir) { Remove-Item $clientDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $clientDir | Out-Null
Expand-Archive -Path $tmpZip -DestinationPath $clientDir -Force
Remove-Item $tmpZip -Force
Write-Ok "Client unpacked."

# ---- Desktop shortcut ----
Write-Step "Creating desktop shortcut"
$userDesktop = [Environment]::GetFolderPath('DesktopDirectory')
$lnkPath = Join-Path $userDesktop 'GameServerControl.lnk'
$clientExe = Join-Path $clientDir 'GameServerControl.exe'
$sh = New-Object -ComObject WScript.Shell
$lnk = $sh.CreateShortcut($lnkPath)
$lnk.TargetPath       = $clientExe
$lnk.WorkingDirectory = $clientDir
$lnk.IconLocation     = "$clientExe,0"
$lnk.Description      = 'GameServerControl dashboard'
$lnk.Save()
Write-Ok "Shortcut: $lnkPath"

# Bust icon cache so the new logo shows immediately rather than after a logoff/logon
Get-Item "$env:LOCALAPPDATA\IconCache.db" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Installed $($release.tag_name)."
Write-Host ""
Write-Host "  Install dir: $clientDir"
Write-Host "  Next:        open the client, Settings, paste your agent URL + API token."
Write-Host ""
