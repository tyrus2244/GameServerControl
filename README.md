# GameServerControl

> Self-hosted control panel for Steam dedicated game servers running on a Windows host. Manage Windrose, Satisfactory, Valheim, Palworld, ARK, Rust, and more from a native dark-themed WPF UI — start, stop, backup, update, edit config, and apply live changes over [Tailscale](https://tailscale.com) from anywhere.

```
┌────────── your laptop / phone (over Tailscale) ──────────┐
│                                                          │
│   GameServerController.exe   (native WPF client)         │
│             │                                            │
│   HTTPS + Bearer token + SignalR (live status push)      │
│             │                                            │
└─────────────┼────────────────────────────────────────────┘
              │
   ┌──────────▼──────────── your gaming server ───────────┐
   │   GameServerControl.Agent   (ASP.NET Core service)   │
   │     │                                                │
   │     │ PowerShell SDK in-process                      │
   │     ▼                                                │
   │   Hyper-V cmdlets    PowerShell Direct    schtasks   │
   │     │ │                       │              │       │
   │     ▼ ▼                       ▼              ▼       │
   │   [Hyper-V VM]   [Hyper-V VM]      [bare-metal]      │
   │   game servers   game servers      game servers      │
   └──────────────────────────────────────────────────────┘
```

## Features

- **Bare-metal *and* Hyper-V VM hosting** — single UI manages both. Bare-metal servers run via Windows Task Scheduler with auto-restart-on-crash wrappers; VM servers use Hyper-V cmdlets + PowerShell Direct.
- **🔍 Auto-discover installed servers** — one click scans Steam libraries (via `libraryfolders.vdf` + `appmanifest_*.acf`) and common SteamCMD paths, matches against 11+ known dedicated-server presets, marks already-configured installs.
- **🧩 Server-side mod management** — browse + one-click install mods right in the dashboard. **Filters to server-side-only by default** so players don't need to install anything on their end. Per-game sources:
  - **Valheim** → Thunderstore (`valheim.thunderstore.io`)
  - **Satisfactory** → ficsit.app (GraphQL)
  - **Palworld** → curated catalog (Palguard etc.), fetched live from GitHub Releases so versions stay current
  - **ARK SE/SA** → Steam Workshop, install-by-ID via SteamCMD (paste a workshop URL or ID)
  - **Windrose** → no community marketplace yet — surface a clean explanation
- **Per-game config editor** — curated schemas (with units, sliders, dropdowns, tooltips) for Valheim · Palworld · Windrose · Satisfactory · ARK · Rust · 7DTD · Terraria · DST · Project Zomboid · Minecraft.
- **Auto-discovered settings** — when a game ships a defaults file or admin API (Palworld's `DefaultPalWorldSettings.ini`, Satisfactory's `GetAdvancedGameSettings`), the editor surfaces *every* setting beyond the curated ones. Palworld jumps from 53 curated fields to **109 total**.
- **Live Satisfactory Admin API integration** — claim server, rename, set client password, toggle all 13 Advanced Game Settings (NoPower, GodMode, FlightMode, StartingTier, …) without restarting. Applies instantly. Also surfaces the **current session name** as a read-only "Session ID" (analogous to Windrose's invite code).
- **📦 Backups + restore** — Hyper-V checkpoints for VMs, zipped save-dir archives for bare-metal. Per-server **Backups window** lists every archive, lets you restore (with auto safety-snapshot) or delete with two clicks.
- **⏰ Scheduled maintenance** — daily restart / weekly SteamCMD update / hourly backup, wired into Windows Task Scheduler from the **Schedule window** per server. Linux agents get the same API surface (use systemd timers).
- **🔔 Discord webhook alerts** — set a webhook URL per server; the agent posts colored embeds on start / stop / **crash detection** (Running→NotRunning without a Stop request) / backup completed.
- **🔄 Mod auto-update + dependencies** — Thunderstore `manifest.json` deps are resolved + installed recursively. The Mods tab has **Check for updates** and **Update all** buttons that walk every installed mod back to its source URL and re-installs the latest.
- **👥 Live player management** — RCON-driven player list (auto-refresh every 10s while open) with kick/ban/broadcast/shutdown built in.
- **📊 Resource Monitor** — per-server live CPU + RAM polyline charts with 5 minutes of rolling history.
- **🔑 Per-user tokens + roles** — multi-token store in `tokens.json` with `Admin` / `ReadOnly` roles. State-mutating verbs (POST/PUT/DELETE) require Admin; GETs accept any authenticated token. Manage from the **Tokens window** in the client.
- **🛰 Multi-host federation** — connect to multiple agents at once. All their servers show up in one unified dashboard with an "agent" badge on each card; actions automatically route to the right agent.
- **📱 Real responsive web UI** — drawer-based mobile-friendly UI at `/` of the agent. Player kick/ban, mod install/uninstall/update, backup restore — all accessible from a browser without the desktop app.
- **SteamCMD updates** — one button validates + updates any Steam-app-ID server.
- **Live status + log tail** via SignalR.
- **RCON** — Source-engine RCON for Palworld; pluggable `IGameRcon` for adding more.
- **Audit log** — every authenticated mutation lands in `Logs/audit/audit-YYYY-MM-DD.jsonl`.
- **First-run API token auto-generation** — no shipped default credentials.
- **Tailscale-friendly** — designed to bind to a tailnet IP, exposing nothing to the public internet.

## Supported games

| Game | Steam App ID | Curated schema | Auto-discovery |
|---|---|---|---|
| Windrose | `4129620` | ✅ 16 fields (server + world rules) | — |
| Valheim | `896660` | ✅ 27 fields (modifiers, world rules) | — |
| Satisfactory | `1690800` | ✅ 21 fields (incl. live API + AGS) | ✅ via Admin API |
| Palworld | `2394010` | ✅ 53 fields (game mode, pals, players, base, items) | ✅ +56 from `DefaultPalWorldSettings.ini` |
| ARK: Survival Ascended | `2430930` | basic preset (Discover finds it) | — |
| ARK: Survival Evolved | `376030` | basic preset | — |
| Rust | `258550` | basic preset | — |
| Project Zomboid | `380870` | basic preset | — |
| 7 Days to Die | `294420` | basic preset | — |
| Terraria | `105600` | basic preset | — |
| Don't Starve Together | `343050` | basic preset | — |
| Minecraft (Java) | n/a | basic preset | — |

Adding a new game with a curated schema is ~30 lines in `Shared/ConfigSchema.cs` plus an optional `IGameConfig` implementation to read/write its config file.

## Repo layout

```
src/
  GameServerControl.Shared/   DTOs, ConfigSchema, RconModels, DiscoveryModels
  GameServerControl.Agent/    ASP.NET Core service (Windows). Talks to Hyper-V and game-server processes.
    Auth/                     TokenAuthHandler, FirstRunTokenGenerator, AuditLogger
    Admin/                    SatisfactoryAdminClient (HTTPS Admin API)
    Config/                   IGameConfig + per-game read/write + dynamic schema providers
    Discovery/                Steam library scan + appmanifest parser
    Hyperv/                   VM control + PowerShell Direct + local process service
    Rcon/                     Source-engine RCON + Palworld glue
    Servers/                  Registry, store, orchestrator, status tracker
  GameServerControl.Client/   WPF dashboard (the "GameServerController")
.github/workflows/build.yml   CI (windows-latest, dotnet 8)
SECURITY.md                   Threat model + deployment checklist
LICENSE                       MIT
```

## Install

### Supported hosts — feature parity

|                                         | Windows | Linux |
|-----------------------------------------|---------|-------|
| **Bare-metal hosting** (start/stop/restart/backup/update) | ✅ | ✅ |
| **Hyper-V VM hosting**                  | ✅ | ❌ (Hyper-V doesn't exist on Linux) |
| **Auto-restart of agent on crash**      | ✅ (Windows Service) | ✅ (systemd `Restart=on-failure`) |
| **Auto-start of game servers on boot**  | ✅ (built-in Task Scheduler wrapper) | ✅ (user-written systemd unit — recipe in README) |
| **Live status + log tail** (SignalR)    | ✅ | ✅ |
| **RCON** (Source-engine + Palworld)     | ✅ | ✅ |
| **Satisfactory Admin API** (AGS, server identity) | ✅ | ✅ |
| **Per-game curated config editors**     | ✅ (4 games × dozens of fields) | ✅ (identical) |
| **Auto-discovered config fields**       | ✅ (Palworld + Satisfactory) | ✅ (identical) |
| **🔍 Auto-discover installed servers**  | ✅ (registry + Steam library + C:\GameServers) | ✅ (~/.steam, ~/.local/share/Steam, Flatpak, ~/gameservers, /srv, /opt) |
| **File-zip backups**                    | ✅ | ✅ |
| **Hyper-V checkpoint backups**          | ✅ | ❌ (Hyper-V only) |
| **SteamCMD updates** (one-click)        | ✅ (uses `steamcmd.exe` on PATH) | ✅ (uses `steamcmd` on PATH or `Agent:SteamCmdPath`) |
| **First-run API token auto-generation** | ✅ | ✅ |
| **Audit log of mutations**              | ✅ | ✅ |
| **HTTPS with self-signed cert**         | ✅ | ✅ |
| **Web UI**                              | ✅ | ✅ |
| **Native WPF client**                   | ✅ | ❌ (WPF is Windows-only — use web UI or remote Windows client over Tailscale) |
| **Scheduled maintenance** (daily restart / weekly update / hourly backup) | ✅ (Windows Task Scheduler) | ⚠️ (manual — recipe with systemd timers below) |

---

### Windows install

#### Requirements
- Windows 10 / 11 / Server 2019+
- **.NET 8 SDK** to build (or grab artifacts from [Actions](../../actions))
- **Tailscale** recommended
- **Hyper-V role** only needed if hosting servers inside VMs

#### Build, configure, deploy

```powershell
git clone https://github.com/tyrus2244/GameServerControl.git
cd GameServerControl
dotnet build -c Release

copy src\GameServerControl.Agent\appsettings.json.example src\GameServerControl.Agent\appsettings.json
copy src\GameServerControl.Agent\servers.json.example     src\GameServerControl.Agent\servers.json

dotnet publish src\GameServerControl.Agent -c Release -o C:\GameServerControl\Agent
sc.exe create GameServerControlAgent binPath= "C:\GameServerControl\Agent\GameServerControl.Agent.exe" start= auto
sc.exe start  GameServerControlAgent

dotnet publish src\GameServerControl.Client -c Release -o C:\GameServerControl\Client
C:\GameServerControl\Client\GameServerControl.exe
```

On first agent start, the API token is printed once to the console / Event Log — paste it into the client's **Settings**.

---

### Linux install

#### Requirements
- Any modern Linux (Debian/Ubuntu, Fedora/RHEL, Arch)
- **.NET 8 SDK** ([install guide](https://learn.microsoft.com/dotnet/core/install/linux))
- `systemd` (standard on all the above)

#### One-shot install with the included script

```bash
git clone https://github.com/tyrus2244/GameServerControl.git
cd GameServerControl
sudo bash deploy/linux/install.sh
```

That script:
1. Builds + publishes the agent for `linux-x64` (framework-dependent)
2. Installs it to `/opt/gameservercontrol/agent`
3. Creates a system user `gsc` with no login shell
4. Installs the systemd unit `gameservercontrol-agent.service` and enables/starts it
5. Tails journalctl long enough to capture the auto-generated API token and prints it for you

After install:
- Service status: `systemctl status gameservercontrol-agent`
- Live logs:      `journalctl -u gameservercontrol-agent -f`
- Web UI:         `http://<this-host>:5099/`
- Config:         `/opt/gameservercontrol/agent/appsettings.json`
- Server defs:    `/opt/gameservercontrol/agent/servers.json`

#### Manual build (no installer)

```bash
git clone https://github.com/tyrus2244/GameServerControl.git
cd GameServerControl
dotnet publish src/GameServerControl.Agent -c Release -r linux-x64 --self-contained false -o ~/gsc-agent
cd ~/gsc-agent
cp appsettings.json.example appsettings.json
./GameServerControl.Agent
```

The agent prints its generated API token on first run. Use that with the web UI at `http://localhost:5099/` (or a Windows client over Tailscale).

#### Linux notes

- **VM hosting mode is unsupported.** Use `"HostingMode": "BareMetal"` only.
- **Autostart-on-boot** for game servers should be done with your own systemd unit per server, e.g.:
  ```ini
  [Unit]
  Description=Satisfactory dedicated server
  After=network-online.target

  [Service]
  User=gsc
  WorkingDirectory=/srv/gameservers/satisfactory
  ExecStart=/srv/gameservers/satisfactory/FactoryServer.sh -log -unattended
  Restart=on-failure
  RestartSec=15s

  [Install]
  WantedBy=multi-user.target
  ```
  The agent's `ScheduledTaskName` field is ignored on Linux (it's Task-Scheduler-only).
- **Auto-discover** picks up Steam libraries under `~/.steam/steam`, `~/.local/share/Steam`, and Flatpak Steam installs, plus bare-metal installs under `~/gameservers`, `/srv/gameservers`, `/opt/gameservers`, `/var/lib/gameservers`.
- **Scheduled maintenance** (the `MaintenanceScheduler` API) is Windows-only because it drives Windows Task Scheduler. To get the same effect on Linux, write systemd timers that POST to the agent's own API. Example for a nightly restart at 04:00:
  ```ini
  # /etc/systemd/system/gsc-windrose-restart.service
  [Unit]
  Description=Nightly restart of Windrose via GameServerControl
  [Service]
  Type=oneshot
  ExecStart=/usr/bin/curl -fsS -X POST -H "Authorization: Bearer <YOUR-TOKEN>" http://127.0.0.1:5099/api/servers/windrose-main/restart
  ```
  ```ini
  # /etc/systemd/system/gsc-windrose-restart.timer
  [Unit]
  Description=Nightly restart of Windrose
  [Timer]
  OnCalendar=*-*-* 04:00:00
  Persistent=true
  [Install]
  WantedBy=timers.target
  ```
  Then `sudo systemctl enable --now gsc-windrose-restart.timer`. Same pattern for hourly backups (`/backup`) and weekly updates (`/update`).

### Run the client (Windows only)

The WPF client only runs on Windows. Linux users use the web UI. On Windows:

```powershell
dotnet publish src\GameServerControl.Client -c Release -o C:\GameServerControl\Client
C:\GameServerControl\Client\GameServerControl.exe
```

Open **Settings**, paste the agent URL (`http://100.x.y.z:5099`) and the API token, save. Click **🔍 Discover** to auto-detect installed servers.

## Usage

### Add a server
- **Auto-detect:** click 🔍 Discover → click Add on the row you want → review pre-filled fields → Save.
- **Manual:** click **+ Add server**, pick a preset for defaults, fill in your install paths, Save.

### Edit world / game settings
- Click the **Config** button on a server's card. The editor shows curated sections at the top (polished labels, dropdowns, sliders) and an **"All settings (auto-discovered)"** section at the bottom for everything the game exposes that we haven't hand-curated.
- For **Satisfactory**, the *Server identity (live via Admin API)* and *Advanced Game Settings* sections apply instantly — no restart. INI-backed fields require a restart.

### Manage server-side mods
- Click the **🧩 Mods** button on a server's card.
- **Browse tab** — search Thunderstore (Valheim today) right in the app. **Defaults to server-side-only mods** so anything you install runs entirely on the server and players don't need to do anything. Each result shows a green **✓ server-side** badge.
- **"Also show mods that require client install" checkbox** — toggle to see the full catalog. Mods that need client install get a yellow **⚠ needs client install** badge so you spot them immediately.
- One-click **Install** downloads the zip, extracts it into `<install>/BepInEx/plugins/`, and remembers what it installed in a sidecar (`BepInEx/.gsc-mods.json`) so uninstall is precise.
- **Installed tab** — list with per-row Uninstall + an "Advanced: install from URL" box for mods not on Thunderstore.
- Restart the server after install/uninstall for changes to take effect.

### Backup / Update / Restart
- **Backup** — Hyper-V checkpoint (VM) or zip of `SaveDirs` (bare-metal).
- **Update** — runs SteamCMD `+app_update <SteamAppId> validate`.
- **Restart** — graceful stop then start.

## API

Every endpoint under `/api/*` requires `Authorization: Bearer <token>`. JSON bodies, JSON responses.

| Method | Path | Notes |
|---|---|---|
| GET    | `/api/health` | Liveness check (no body). |
| GET    | `/api/discover` | Scan host for installed servers. |
| GET    | `/api/servers` | List configured servers. |
| POST   | `/api/servers` | Add a server. |
| PUT    | `/api/servers/{id}` | Update. |
| DELETE | `/api/servers/{id}` | Remove. |
| POST   | `/api/servers/reload` | Re-read `servers.json` without restarting the agent. |
| GET    | `/api/servers/{id}/status` | Live status (Hyper-V state + process state). |
| POST   | `/api/servers/{id}/start` · `/stop` · `/restart` · `/backup` · `/update` · `/apply` | Actions. |
| GET    | `/api/servers/{id}/config` | Returns merged curated + dynamic schema and current values. |
| PUT    | `/api/servers/{id}/config` | Save edits. Body: `{key: value, ...}`. |
| GET/POST | `/api/servers/{id}/autostart` | Read or toggle the scheduled-task autostart flag. |
| GET    | `/api/servers/{id}/rcon/players` | List players via RCON. |
| POST   | `/api/servers/{id}/rcon/command` | Run an RCON command. |
| GET    | `/api/download/client` | Download `GameServerController.zip` (publishes the client zipped). Linux agents return 404 + a pointer to the web UI. |
| GET    | `/api/servers/{id}/mods` | List installed server-side mods. |
| GET    | `/api/servers/{id}/mods/search?q=&limit=&serverSideOnly=` | Search the game's mod marketplace. Defaults `serverSideOnly=true`. |
| POST   | `/api/servers/{id}/mods/install` | Body: `{Url, DisplayName?}`. Downloads + extracts into the mods folder. |
| DELETE | `/api/servers/{id}/mods/{modId}` | Removes the files the agent recorded for that mod. |

SignalR hub at `/hubs/status` emits `statusChanged` + `logLine` events.

## Security

Read **[SECURITY.md](SECURITY.md)** before deploying anywhere your friends will touch. Short version:

- No default credentials — token is generated on first boot
- Every mutation is audit-logged
- Bind to Tailscale, not 0.0.0.0
- `servers.json` contains plaintext game-server passwords — protect file permissions
- HTTPS is supported but optional; required if exposed outside Tailscale/LAN

## Contributing

PRs welcome. Particularly interested in:

- New game presets (`GamePresets.cs` + `ConfigSchema.cs` + optional `IGameConfig` impl)
- New `IGameRcon` implementations
- New `IDynamicSchemaExtension` providers (auto-discover settings for more games)
- New `IModManager` implementations — Satisfactory (ficsit.app or Thunderstore community), ARK (Steam Workshop via SteamCMD `+workshop_download_item`), Palworld, etc. The Valheim BepInEx flow is the reference impl.
- Hardening items called out as TODO in `SECURITY.md` (DPAPI encryption, rate limiting, role-based tokens)
- More cross-platform polish — the agent already runs on Linux via systemd; a KVM/libvirt provider would close the Hyper-V gap on Linux hosts

## License

MIT — see [LICENSE](LICENSE).
