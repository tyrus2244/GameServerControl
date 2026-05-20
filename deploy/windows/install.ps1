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

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'   # quiet IWR progress bars

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
Write-Host "===============================================================" -ForegroundColor Red
Write-Host "  TK-ECLIPSE  ·  GameServerControl  ·  Windows installer       " -ForegroundColor Red
Write-Host "===============================================================" -ForegroundColor Red

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
$svcExePath = Join-Path $agentDir 'GameServerControl.Agent.exe'
$existing   = Get-Service -Name $svcName -ErrorAction SilentlyContinue
if ($existing) {
    # In case the binary path moved — re-point it.
    Write-Step "Refreshing $svcName service"
    & sc.exe config $svcName binPath= "`"$svcExePath`"" | Out-Null
} else {
    Write-Step "Registering $svcName Windows service"
    & sc.exe create $svcName binPath= "`"$svcExePath`"" start= auto DisplayName= "GameServerControl Agent (TK-ECLIPSE)" | Out-Null
    & sc.exe description $svcName "Self-hosted dashboard for game-server processes (start/stop/backup/update/RCON). github.com/$repo" | Out-Null
}
Write-Step "Starting $svcName"
& sc.exe start $svcName | Out-Null
Start-Sleep -Seconds 3
$status = (Get-Service -Name $svcName).Status
if ($status -ne 'Running') { Write-Warn2 "Service status: $status (check Windows Event Log)" } else { Write-Ok "Service running." }

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
    $lnk.Description      = 'GameServerControl dashboard — TK-ECLIPSE'
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
    $lnk.Description      = 'GameServerControl dashboard — TK-ECLIPSE'
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
Write-Host "===============================================================" -ForegroundColor Red
Write-Host "  Done!  ($($release.tag_name))" -ForegroundColor Green
Write-Host ""
Write-Host "  Agent service: $svcName" -ForegroundColor Gray
Write-Host "  Web UI:        https://localhost:5099/  (also tailnet IP)" -ForegroundColor Gray
Write-Host "  Client:        double-click the desktop shortcut" -ForegroundColor Gray
Write-Host "  Config:        $agentDir\appsettings.json" -ForegroundColor Gray
Write-Host ""
Write-Host "  ❤ Support: https://paypal.me/TKECLIPSE" -ForegroundColor Red
Write-Host "===============================================================" -ForegroundColor Red
