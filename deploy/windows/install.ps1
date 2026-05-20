# GameServerControl — Windows installer / updater (one-liner friendly).
#
# Run from an elevated PowerShell:
#   iwr https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/windows/install.ps1 | iex
#
# What it does (idempotent — re-run to update):
#   1. Ensures .NET 8 Desktop Runtime is present (downloads + installs if missing).
#   2. Hits the GitHub Releases API for the *latest* release.
#   3. Downloads GameServerControl-Agent-windows.zip + GameServerControl-Client-windows.zip.
#   4. Stops the GameServerControlAgent service if it's running.
#   5. Extracts agent to C:\GameServerControl\Agent, client to C:\GameServerControl\Client.
#   6. Preserves existing appsettings.json + servers.json + tokens.json (treats them as user data).
#   7. Registers + starts the Windows service.
#   8. Creates / refreshes a desktop shortcut pointing at the new client exe.
#   9. Tails the agent's stdout long enough to grab the auto-generated API token.
#
# Override variables (set before piping into iex):
#   $env:GSC_REPO       — owner/name (default: tyrus2244/GameServerControl)
#   $env:GSC_INSTALL    — install root (default: C:\GameServerControl)
#   $env:GSC_VERSION    — pin a specific tag like v1.0.0 (default: latest)

#Requires -RunAsAdministrator
# NOTE: #Requires only blocks .ps1 files run directly. When this script is piped through `iex`
# (the recommended install flow), the directive is treated as a comment and ignored. We do an
# explicit elevation check below to handle that case correctly.

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'   # quiet IWR progress bars

# ---- Elevation check (must run before anything that needs Admin) ----
$isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated) {
    Write-Host ""
    Write-Host "===============================================================" -ForegroundColor Red
    Write-Host "  This installer must run as Administrator." -ForegroundColor Red
    Write-Host ""
    Write-Host "  Right-click Start -> 'Terminal (Admin)' or 'PowerShell (Admin)'," -ForegroundColor Yellow
    Write-Host "  then re-run the install command:" -ForegroundColor Yellow
    Write-Host ""
    Write-Host '    iwr https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/windows/install.ps1 | iex' -ForegroundColor Gray
    Write-Host ""
    Write-Host "  Why: registering a Windows service (sc.exe create) requires elevation." -ForegroundColor Yellow
    Write-Host "===============================================================" -ForegroundColor Red
    return
}

# ---- Configuration ----
$repo       = if ($env:GSC_REPO) { $env:GSC_REPO } else { 'tyrus2244/GameServerControl' }
$installRt  = if ($env:GSC_INSTALL) { $env:GSC_INSTALL } else { 'C:\GameServerControl' }
$agentDir   = Join-Path $installRt 'Agent'
$clientDir  = Join-Path $installRt 'Client'
$pinnedTag  = $env:GSC_VERSION
$svcName    = 'GameServerControlAgent'

function Write-Step($msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    OK: $msg" -ForegroundColor Green }
function Write-Skip($msg) { Write-Host "    -- $msg" -ForegroundColor DarkGray }
function Write-Warn2($msg){ Write-Host "    !! $msg" -ForegroundColor Yellow }

Write-Host ""
Write-Host "GameServerControl installer (Windows)"
Write-Host "-------------------------------------"

# ---- .NET 8 Desktop Runtime check ----
Write-Step ".NET 8 Desktop Runtime"
$hasDesktop = & dotnet --list-runtimes 2>$null | Select-String 'Microsoft.WindowsDesktop.App 8\.'
if (-not $hasDesktop) {
    Write-Warn2 "Missing — downloading Microsoft's bootstrapper…"
    $dotnetUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe'
    $dotnetExe = Join-Path $env:TEMP 'gsc-dotnet8-desktop.exe'
    Invoke-WebRequest -Uri $dotnetUrl -OutFile $dotnetExe
    Write-Step "Installing .NET 8 Desktop Runtime (quiet)"
    $p = Start-Process -FilePath $dotnetExe -ArgumentList '/install', '/quiet', '/norestart' -PassThru -Wait
    if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) {
        throw ".NET installer exited $($p.ExitCode). Aborting."
    }
    Write-Ok ".NET 8 Desktop Runtime installed."
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

function Get-AssetUrl($name) {
    $a = $release.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if (-not $a) { throw "Release asset not found: $name" }
    return $a.browser_download_url
}

$agentZipUrl  = Get-AssetUrl 'GameServerControl-Agent-windows.zip'
$clientZipUrl = Get-AssetUrl 'GameServerControl-Client-windows.zip'

# ---- Download to temp ----
Write-Step "Downloading release assets"
$tmpRoot = Join-Path $env:TEMP "gsc-install-$(Get-Random -Maximum 99999)"
New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null
$agentZip  = Join-Path $tmpRoot 'agent.zip'
$clientZip = Join-Path $tmpRoot 'client.zip'
Invoke-WebRequest -Uri $agentZipUrl  -OutFile $agentZip;  Write-Ok "Agent zip"
Invoke-WebRequest -Uri $clientZipUrl -OutFile $clientZip; Write-Ok "Client zip"

# ---- Stop existing service if any ----
$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') {
    Write-Step "Stopping existing $svcName service"
    Stop-Service -Name $svcName -Force
    # Give it a moment to release file handles.
    Start-Sleep -Seconds 2
    Write-Ok "Stopped."
}

# ---- Extract (preserving user data files) ----
Write-Step "Installing agent to $agentDir"
$preserve = @('appsettings.json', 'servers.json', 'tokens.json')
$backupDir = Join-Path $tmpRoot 'preserved'
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
foreach ($f in $preserve) {
    $src = Join-Path $agentDir $f
    if (Test-Path $src) { Copy-Item $src $backupDir -Force; Write-Skip "Preserving $f" }
}
if (Test-Path $agentDir) { Remove-Item $agentDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $agentDir | Out-Null
Expand-Archive -Path $agentZip -DestinationPath $agentDir -Force
# Restore user data on top of fresh install
foreach ($f in $preserve) {
    $src = Join-Path $backupDir $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $agentDir $f) -Force }
}
Write-Ok "Agent unpacked."

Write-Step "Installing client to $clientDir"
if (Test-Path $clientDir) { Remove-Item $clientDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $clientDir | Out-Null
Expand-Archive -Path $clientZip -DestinationPath $clientDir -Force
Write-Ok "Client unpacked."

# ---- Register Windows service ----
# We invoke sc.exe and capture both stdout and exit code. On any failure we surface the actual
# sc.exe error message rather than letting Get-Service blow up downstream with a useless trace.
$svcExePath = Join-Path $agentDir 'GameServerControl.Agent.exe'
$existing   = Get-Service -Name $svcName -ErrorAction SilentlyContinue

# NOTE: parameter name is intentionally NOT `$Args` — that shadows PowerShell's automatic
# variable and breaks splatting (the function sees an empty array, sc.exe prints help, we throw).
function Invoke-Sc {
    param([string[]]$ScArgs, [string]$Label)
    $out = & sc.exe @ScArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed (sc.exe exit $LASTEXITCODE): $($out -join ' | ')"
    }
    return $out
}

if ($existing) {
    Write-Step "Refreshing $svcName service"
    Invoke-Sc -ScArgs @('config', $svcName, 'binPath=', "`"$svcExePath`"") -Label 'sc.exe config' | Out-Null
    Write-Ok "Service config refreshed."
} else {
    Write-Step "Registering $svcName Windows service"
    Invoke-Sc -ScArgs @('create', $svcName,
        'binPath=', "`"$svcExePath`"",
        'start=', 'auto',
        'DisplayName=', 'GameServerControl Agent') -Label 'sc.exe create' | Out-Null
    # description is non-critical — log a warning instead of failing the whole install.
    try { Invoke-Sc -ScArgs @('description', $svcName, "Self-hosted dashboard for game-server processes (start/stop/backup/update/RCON). github.com/$repo") -Label 'sc.exe description' | Out-Null }
    catch { Write-Warn2 "Couldn't set service description: $_" }
    Write-Ok "Service registered."
}

Write-Step "Starting $svcName"
try { Invoke-Sc -ScArgs @('start', $svcName) -Label 'sc.exe start' | Out-Null }
catch {
    # Service might already be Running (sc.exe returns 1056 for that), or might fail to start
    # because of a config error in appsettings.json. Tolerate "already running"; surface anything
    # else as a warning rather than crashing the install (the agent might already be up).
    Write-Warn2 "$_"
}
Start-Sleep -Seconds 3

# Re-check existence + status. SilentlyContinue here so we can give a clean error message ourselves.
$svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if (-not $svc) {
    throw "Service '$svcName' is still not registered. The sc.exe create above probably failed. Check that you're running as Administrator (Get-Process powershell shows your PID; right-click PowerShell -> Run as administrator)."
}
if ($svc.Status -ne 'Running') {
    Write-Warn2 "Service registered but not running (status: $($svc.Status)). Check the Application Event Log under source 'GameServerControl' for the boot error."
} else {
    Write-Ok "Service running."
}

# ---- Desktop shortcut ----
Write-Step "Refreshing desktop shortcut"
$userDesktop   = [Environment]::GetFolderPath('DesktopDirectory')
$publicDesktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
$lnkPaths = @(
    (Join-Path $userDesktop  'GameServerControl.lnk'),
    (Join-Path $publicDesktop 'GameServerControl.lnk')
)
$sh = New-Object -ComObject WScript.Shell
$clientExe = Join-Path $clientDir 'GameServerControl.exe'
# Drop the shortcut on the user's desktop. If the public one already exists we refresh it too.
foreach ($lp in $lnkPaths) {
    if ($lp -like "*\Public\*" -and -not (Test-Path $lp)) { continue }   # don't create public unless it already exists
    $lnk = $sh.CreateShortcut($lp)
    $lnk.TargetPath       = $clientExe
    $lnk.WorkingDirectory = $clientDir
    $lnk.IconLocation     = "$clientExe,0"
    $lnk.Description      = 'GameServerControl dashboard'
    $lnk.Save()
    Write-Ok "Shortcut: $lp"
}
# Force-create the user desktop shortcut if it didn't exist
$userLnk = Join-Path $userDesktop 'GameServerControl.lnk'
if (-not (Test-Path $userLnk)) {
    $lnk = $sh.CreateShortcut($userLnk)
    $lnk.TargetPath       = $clientExe
    $lnk.WorkingDirectory = $clientDir
    $lnk.IconLocation     = "$clientExe,0"
    $lnk.Description      = 'GameServerControl dashboard'
    $lnk.Save()
    Write-Ok "Shortcut: $userLnk"
}

# Bust icon cache so the new logo shows up right away
Get-Item "$env:LOCALAPPDATA\IconCache.db" -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# ---- Try to surface the API token from the service's recent log ----
Write-Step "Looking for the API token in recent Event Log entries"
try {
    $entries = Get-WinEvent -LogName Application -MaxEvents 50 -ErrorAction Stop |
        Where-Object { $_.ProviderName -like '*GameServerControl*' -or $_.Message -match 'GENERATED NEW AGENT API TOKEN' }
    $tokenEntry = $entries | Where-Object { $_.Message -match 'GENERATED NEW AGENT API TOKEN' } | Select-Object -First 1
    if ($tokenEntry) {
        Write-Host ""
        Write-Host "   ----- AGENT API TOKEN (copy into the client/web UI) -----" -ForegroundColor Yellow
        Write-Host $tokenEntry.Message
        Write-Host "   ----------------------------------------------------------" -ForegroundColor Yellow
    } else {
        Write-Skip "No token in Event Log. The agent reuses any existing appsettings.json token."
        Write-Skip "If this is a fresh install, check: $(Join-Path $agentDir 'appsettings.json')"
    }
} catch {
    Write-Skip "Event Log not readable; check appsettings.json directly."
}

Write-Host ""
Write-Host "Installed $($release.tag_name)."
Write-Host ""
Write-Host "  Service: $svcName"
Write-Host "  Web UI:  https://localhost:5099/"
Write-Host "  Client:  desktop shortcut"
Write-Host "  Config:  $agentDir\appsettings.json"
Write-Host ""
