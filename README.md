# GameServerControl

A self-hosted control panel for Steam-based dedicated game servers. One agent runs as a service on the host machine; a Windows desktop client and a browser UI both talk to it over HTTPS. MIT licensed.

Supports Valheim, Satisfactory, Palworld, ARK (Ascended + Evolved), Rust, 7 Days to Die, Terraria, Don't Starve Together, Project Zomboid, Minecraft (Java), and Windrose. Agent runs on Windows, Linux, and macOS. The desktop client is Windows-only; everyone else uses the browser UI.

## Install

One line per platform. Each script downloads the matching release artifact, installs the .NET 8 runtime if missing, registers the host's native service (Windows service, systemd unit, or LaunchAgent), and preserves config across upgrades. Re-running the same command upgrades to the latest release.

Windows (elevated PowerShell):

```powershell
iwr https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/windows/install.ps1 | iex
```

Windows, client only (no admin, no service):

```powershell
iwr https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/windows/install-client.ps1 | iex
```

Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/linux/install-from-release.sh | sudo bash
```

macOS:

```bash
curl -fsSL https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/macos/install-from-release.sh | bash
```

On first run, the agent generates a random API token and writes it to `appsettings.json`. Paste that into the client's Settings (or the browser UI's login form). The agent also polls GitHub Releases once a day and surfaces an in-app banner when a newer release exists.

## Screenshots

<p align="center"><img src="docs/screenshots/dashboard.png"     alt="Dashboard"      width="800"/></p>
<p align="center"><img src="docs/screenshots/create-wizard.png" alt="Create Server"  width="800"/></p>
<p align="center"><img src="docs/screenshots/mods-window.png"   alt="Mod browser"    width="800"/></p>
<p align="center"><img src="docs/screenshots/players.png"       alt="Player list"    width="800"/></p>
<p align="center"><img src="docs/screenshots/web-ui.png"        alt="Browser UI"     width="800"/></p>

## What it does

**Hosting modes.** Bare-metal (the agent supervises processes directly) or Hyper-V VM (PowerShell Direct into the guest). Both modes are managed identically from the UI.

**Create Server.** Pick a game from the wizard, choose an install path, and the agent runs `steamcmd +login anonymous +force_install_dir … +app_update <appid> validate +quit`, streams progress, then registers the new server. SteamCMD is auto-downloaded if it isn't on the host. Works for any anonymous-installable Steam dedicated server.

**Auto-discover.** Scans Steam libraries (`libraryfolders.vdf` + `appmanifest_*.acf`) plus common install roots (`C:\GameServers`, `~/gameservers`, `/srv/gameservers`, `~/Library/Application Support/Steam` on macOS). Matches against a preset list and flags installs that aren't yet registered.

**Per-game config editors.** Curated schemas for Valheim, Palworld, Satisfactory, Windrose, ARK, Rust, 7DTD, Terraria, Don't Starve Together, Project Zomboid, and Minecraft. Where the game exposes a settings file or live admin API, every field is surfaced (Palworld grows from 53 curated to 109 total; Satisfactory adds all 13 Advanced Game Settings).

**Mod management.** Server-side mods only by default, so clients don't have to install anything. Sources:
- Valheim → Thunderstore
- Satisfactory → ficsit.app (GraphQL)
- Palworld → curated GitHub Releases list
- ARK ASA/SE → Steam Workshop by ID
- Windrose → no community marketplace yet

Thunderstore dependencies resolve recursively. The Mods window has Check-for-updates and Update-all buttons.

**RCON.** Source-engine RCON for Palworld; pluggable `IGameRcon` for others. The Players window auto-refreshes every 10 seconds with kick/ban/broadcast/shutdown. The Console window keeps the raw-command interface for power users.

**Backups + restore.** Zip-based for bare-metal (save-dir archives with configurable retention), Hyper-V checkpoint for VMs. The Backups window lists every archive with restore and delete actions; restore takes a safety snapshot of the current state first.

**Scheduled maintenance.** Daily restart, weekly SteamCMD update, hourly backup. On Windows this drives Task Scheduler; on Linux you wire up systemd timers that POST to the agent's API (recipe below); on macOS use LaunchAgent calendar intervals.

**Discord webhooks.** Per-server URL. Posts start, stop, crash detection, and backup events as colored embeds.

**Resource monitor.** Live CPU and RAM polylines per server with five minutes of rolling history.

**Federation.** Add multiple agents in Settings. The dashboard merges every server from every agent into one list with an agent badge on each card; actions route automatically.

**Tokens + roles.** Multi-user token store in `tokens.json` with Admin and ReadOnly roles. State-mutating verbs (POST/PUT/DELETE) require Admin; GETs accept any authenticated token. The legacy single-token setup still works as an Admin fallback.

**Other.** Live status + log tail over SignalR; first-run API token auto-generation; per-request audit log; HTTPS with self-signed cert; designed to bind to a Tailscale IP and stay off the public internet.

## Supported games

| Game | Steam App ID | Curated schema | Auto-discovery |
|---|---|---|---|
| Windrose | 4129620 | 16 fields | — |
| Valheim | 896660 | 27 fields | — |
| Satisfactory | 1690800 | 21 fields (incl. live AGS) | via Admin API |
| Palworld | 2394010 | 53 fields | +56 from `DefaultPalWorldSettings.ini` |
| ARK: Survival Ascended | 2430930 | preset | — |
| ARK: Survival Evolved | 376030 | preset | — |
| Rust | 258550 | preset | — |
| Project Zomboid | 380870 | preset | — |
| 7 Days to Die | 294420 | preset | — |
| Terraria | 105600 | preset | — |
| Don't Starve Together | 343050 | preset | — |
| Minecraft (Java) | — | preset | — |

Adding a new game with a curated schema is ~30 lines in `Shared/ConfigSchema.cs` plus an optional `IGameConfig` to read/write its config file.

## Platform support

| | Windows | Linux | macOS |
|---|---|---|---|
| Bare-metal hosting | yes | yes | yes |
| Hyper-V VM hosting | yes | no | no |
| Agent auto-restart on crash | Windows service | systemd `Restart=on-failure` | LaunchAgent `KeepAlive=true` |
| Game-server autostart on boot | built-in Task Scheduler wrapper | user systemd unit | user LaunchAgent |
| Scheduled maintenance API | Task Scheduler | manual systemd timer | manual LaunchAgent calendar |
| Native desktop client | yes (WPF) | no (use browser UI) | no (use browser UI) |
| Browser UI | yes | yes | yes |

## Repo layout

```
src/
  GameServerControl.Shared/   DTOs, ConfigSchema, RconModels
  GameServerControl.Agent/    ASP.NET Core service. Talks to Hyper-V, host processes, RCON.
    Auth/                     Token auth, audit log, role enforcement, first-run token generator
    Admin/                    Satisfactory HTTPS Admin API client
    Config/                   IGameConfig + per-game read/write + dynamic schema providers
    Discovery/                Steam library scan, appmanifest parser, server detection
    Hyperv/                   VM control, PowerShell Direct, local + guest process services
    Mods/                     IModManager + per-game implementations + Thunderstore/ficsit clients
    Notifications/            Discord webhook, GitHub release update checker
    Rcon/                     Source-engine RCON + per-game glue
    Servers/                  Registry, store, orchestrator, status tracker, SteamCMD installer
    WebUi/                    Single-file HTML/JS browser dashboard
  GameServerControl.Client/   WPF desktop dashboard
deploy/
  windows/install.ps1, install-client.ps1
  linux/install-from-release.sh, gameservercontrol-agent.service
  macos/install-from-release.sh, com.tk-eclipse.gameservercontrol-agent.plist
.github/workflows/
  build.yml      builds on every push, uploads artifacts
  release.yml    builds on v* tags, attaches zips/tarballs to a GitHub Release
```

## Usage

After install, open the dashboard (client or browser).

To register an existing dedicated-server install: click Discover (it scans Steam libraries and common paths) or Add for a manual entry.

To install a new server from scratch: click Create Server, pick the game, choose a path, hit Install. The agent runs SteamCMD and registers the server when it finishes.

To start, stop, restart, back up, update via SteamCMD, edit config, view live logs, manage mods, kick players, or schedule maintenance: use the per-server card buttons.

To manage tokens: Tokens in the header (Admin only).

To connect a client to multiple hosts: Settings, then add a row per agent.

## API

`GET  /api/health` · `GET /api/version` · `GET /api/servers` · `POST /api/servers` (create row) · `POST /api/servers/install` (SteamCMD install) · `PUT /api/servers/{id}` · `DELETE /api/servers/{id}` · `POST /api/servers/{id}/{start|stop|restart|backup|update|apply}` · `GET /api/servers/{id}/status` · `GET /api/status` · `GET/PUT /api/servers/{id}/config` · `GET /api/servers/{id}/backups` · `POST /api/servers/{id}/backups/{name}/restore` · `DELETE /api/servers/{id}/backups/{name}` · `GET/POST/DELETE /api/servers/{id}/schedule` · `GET /api/servers/{id}/mods` · `GET /api/servers/{id}/mods/search` · `POST /api/servers/{id}/mods/install` · `POST /api/servers/{id}/mods/{modId}/update` · `DELETE /api/servers/{id}/mods/{modId}` · `GET /api/servers/{id}/mods/updates` · `GET /api/servers/{id}/rcon/players` · `POST /api/servers/{id}/rcon/command` · `GET /api/servers/{id}/autostart` · `POST /api/servers/{id}/autostart` · `GET /api/discover` · `GET/POST/DELETE /api/tokens` · `POST /api/discord/test`

SignalR hub at `/hubs/status` pushes `statusChanged`, `logLine`, and `installProgress`.

Bearer token in `Authorization` header for all `/api/*` (or `?access_token=` for SignalR).

## Security

- Token generated at first run; written to `appsettings.json`. Rotate by editing the file or by creating a new token via the Tokens window and revoking the old one.
- Role enforcement middleware rejects POST/PUT/DELETE/PATCH from ReadOnly tokens with 403.
- HTTPS with a self-signed cert generated on first run. Designed to live on a private network (Tailscale, LAN). Do not expose the agent's port to the public internet.
- Every authenticated mutation is logged to `Logs/audit/audit-YYYY-MM-DD.jsonl` with token id, IP, method, path, and outcome.

## Contributing

PRs welcome. The `Plan` agent in `.claude/agents/` is set up if you use Claude Code; otherwise standard `dotnet build` + `dotnet test` works on all three platforms.

Adding a new game: drop a preset in `Shared/GamePresets.cs`, optionally implement `IGameConfig` for its settings file, and (if it has an RCON variant) `IGameRcon` for the protocol.

## License

MIT. See `LICENSE`.

---

Maintained by TK-ECLIPSE. [paypal.me/TKECLIPSE](https://paypal.me/TKECLIPSE) if you find it useful.
