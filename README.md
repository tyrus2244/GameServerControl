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
- **Per-game config editor** — curated schemas (with units, sliders, dropdowns, tooltips) for Valheim · Palworld · Windrose · Satisfactory · ARK · Rust · 7DTD · Terraria · DST · Project Zomboid · Minecraft.
- **Auto-discovered settings** — when a game ships a defaults file or admin API (Palworld's `DefaultPalWorldSettings.ini`, Satisfactory's `GetAdvancedGameSettings`), the editor surfaces *every* setting beyond the curated ones. Palworld jumps from 53 curated fields to **109 total**.
- **Live Satisfactory Admin API integration** — claim server, rename, set client password, toggle all 13 Advanced Game Settings (NoPower, GodMode, FlightMode, StartingTier, …) without restarting. Applies instantly.
- **Backups** — Hyper-V checkpoints for VMs, zipped save-dir archives for bare-metal, with optional scheduled cadence.
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

### Requirements
- **Windows 10 / 11 / Server 2019+** on the host running the agent
- **.NET 8 SDK** for building (.NET 8 Runtime is enough at runtime once published)
- **Tailscale** strongly recommended for any remote use
- **Hyper-V role enabled** if you want VM-hosted servers (bare-metal hosting works without it)

### Build

```powershell
git clone https://github.com/tyrus2244/GameServerControl.git
cd GameServerControl
dotnet build -c Release
```

### Configure (one-time)

```powershell
# Copy example configs into place — the agent reads from these.
copy src\GameServerControl.Agent\appsettings.json.example src\GameServerControl.Agent\appsettings.json
copy src\GameServerControl.Agent\servers.json.example     src\GameServerControl.Agent\servers.json
```

Edit `appsettings.json` to set `Agent:Bind` to your Tailscale IP (or LAN address). Leave `ApiToken` as the placeholder — the agent generates a strong random token on first boot and writes it back.

### Deploy the agent as a Windows service

```powershell
dotnet publish src\GameServerControl.Agent -c Release -o C:\GameServerControl\Agent
sc.exe create GameServerControlAgent binPath= "C:\GameServerControl\Agent\GameServerControl.Agent.exe" start= auto
sc.exe start  GameServerControlAgent
```

On first start, watch the Windows Event Log (Application source `GameServerControlAgent`) or the console: it prints the generated API token once.

### Run the client

```powershell
dotnet publish src\GameServerControl.Client -c Release -o C:\GameServerControl\Client
C:\GameServerControl\Client\GameServerControl.exe
```

Open **Settings**, paste the agent URL (`http://100.x.y.z:5099`) and the API token, save. The dashboard connects and you'll see your existing servers (or a clean slate). Click **🔍 Discover** to auto-detect any installed dedicated servers — each found install gets an **Add** button that pre-fills the New Server wizard with real paths.

## Usage

### Add a server
- **Auto-detect:** click 🔍 Discover → click Add on the row you want → review pre-filled fields → Save.
- **Manual:** click **+ Add server**, pick a preset for defaults, fill in your install paths, Save.

### Edit world / game settings
- Click the **Config** button on a server's card. The editor shows curated sections at the top (polished labels, dropdowns, sliders) and an **"All settings (auto-discovered)"** section at the bottom for everything the game exposes that we haven't hand-curated.
- For **Satisfactory**, the *Server identity (live via Admin API)* and *Advanced Game Settings* sections apply instantly — no restart. INI-backed fields require a restart.

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
| GET    | `/api/download/client` | Download `GameServerController.zip` (publishes the client zipped). |

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
- Hardening items called out as TODO in `SECURITY.md` (DPAPI encryption, rate limiting, role-based tokens)
- Cross-platform support — agent is Windows-only by design (uses Hyper-V + Task Scheduler + DPAPI), but a Linux variant using systemd + Docker would be neat

## License

MIT — see [LICENSE](LICENSE).
