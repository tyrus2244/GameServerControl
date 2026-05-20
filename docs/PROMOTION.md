# Promotion drafts

Plain copy/paste for the places I want to post the project. Personal notes only - not part of the shipped product.

---

## Show HN

URL field: `https://github.com/tyrus2244/GameServerControl`

Title (under 80 chars):

```
Show HN: GameServerControl, a self-hosted dashboard for Steam dedicated servers
```

Body:

```
I got tired of SSH'ing into my box every time I wanted to spin up a new Valheim or Satisfactory server, fight with steamcmd, hand-write a systemd unit, then come back later to find it had crashed two hours ago. So I built a control panel.

GameServerControl runs as a service on the host machine and exposes a browser UI plus a native Windows desktop client. Pick a game from a list, click install. The agent runs steamcmd in the background and registers the new server. Same UI handles backups, mods, RCON, scheduled restarts, crash detection with Discord webhooks, and live CPU/RAM charts.

Currently supports: Valheim, Satisfactory, Palworld, ARK ASA, ARK SE, Rust, 7DTD, Terraria, Don't Starve Together, Project Zomboid, Minecraft. Mod browsers wired to Thunderstore (Valheim), ficsit.app (Satisfactory), Steam Workshop (ARK), and curated GitHub Releases lists (Palworld).

Agent runs on Windows, Linux, and macOS. Native client is Windows-only; everyone else uses the browser UI. .NET 8. MIT. Designed to bind to a Tailscale IP so it stays off the public internet by default.

One-line install per platform:

  Windows:  iwr https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/windows/install.ps1 | iex
  Linux:    curl -fsSL https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/linux/install-from-release.sh | sudo bash
  macOS:    curl -fsSL https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/macos/install-from-release.sh | bash

Each script handles the .NET runtime, downloads the latest release asset, registers the right service flavor, and preserves config across upgrades. Re-running the same command upgrades.

Looking for feedback on the create-server UX and which games should be added next. Repo has screenshots.

https://github.com/tyrus2244/GameServerControl
```

---

## r/selfhosted

Title:

```
GameServerControl: self-hosted dashboard for Steam dedicated servers (Valheim, Satisfactory, Palworld, ARK, Rust, ...)
```

Body:

```
Tired of SSH and steamcmd, built a control panel.

One agent runs as a service on your host machine. Browser UI plus a native Windows desktop client. Click Create Server, pick a game, choose where it installs. The agent runs steamcmd and registers the server. From there it handles backups, mod browsers, RCON player management, scheduled restarts, crash detection and Discord alerts, multi-host federation, per-user tokens with roles.

10 game presets with anonymous SteamCMD installs (Valheim, Satisfactory, Palworld, ARK ASA + SE, Rust, 7DTD, Terraria, Don't Starve Together, Project Zomboid). Plus Minecraft Java and Windrose without SteamCMD.

Mod browsers built in: Thunderstore for Valheim, ficsit.app for Satisfactory, Steam Workshop for ARK, curated GitHub Releases for Palworld. Filters to server-side mods by default so your players don't need to install anything.

.NET 8, MIT, agent runs on Windows + Linux + macOS. Tailscale-friendly by design.

Install in one line:

    iwr https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/windows/install.ps1 | iex

(curl one-liner for Linux/macOS in the README.)

Screenshots and full docs: https://github.com/tyrus2244/GameServerControl

Happy to answer questions. Particularly want to know what games to add next.
```

---

## r/homelab

Same body as r/selfhosted, different title:

```
Built a self-hosted control panel for game servers in my homelab. Cross-platform, MIT licensed.
```

---

## Per-game subreddits

Lead with the game-specific value, not the project meta.

### r/valheim

```
Title: Browser dashboard for managing a Valheim dedicated server

Made this because I was tired of SSH'ing into the box every time I wanted to install a mod or restart the server. Runs as a service on the host machine, gives you a browser UI.

For Valheim specifically:

- One-click install. Pick "Valheim", give it a path, the agent runs steamcmd and registers it.
- Built-in Thunderstore browser. Search valheim.thunderstore.io, filter to server-side mods, click Install. BepInEx dependencies resolve automatically. Auto-update on installed mods.
- Backups + restore of worlds_local + characters_local. Scheduled hourly if you want.
- Scheduled daily restarts so the server doesn't hit the memory leak.
- Discord webhook if the server crashes.

Cross-platform agent, native Windows client, browser UI works on any device.

https://github.com/tyrus2244/GameServerControl
```

### r/SatisfactoryGame

```
Title: Dedicated server dashboard with one-click install, ficsit.app mod browser, live AGS toggles

Tool for Satisfactory dedicated server admins. Runs on the host machine, browser UI from anywhere.

- One-click install via SteamCMD. Pick "Satisfactory", give it a path, done.
- ficsit.app mod browser built in (the GraphQL API). Filter to server-side mods, install with one click.
- Talks to Satisfactory's Admin API live. Claim server, rename, set passwords, toggle all 13 Advanced Game Settings (NoPower, GodMode, FlightMode, StartingTier...) without restarting.
- Session ID surfaced in the UI like Windrose's invite code.
- Backups of FactoryGame/Saved, scheduled if you want.
- Crash detection with Discord webhook.

https://github.com/tyrus2244/GameServerControl
```

### r/Palworld

```
Title: Dashboard for managing a Palworld dedicated server

Same pattern as a real-game dedicated-server admin tool: one click install via SteamCMD, live RCON (kick/ban/broadcast), backups, scheduled restarts.

Auto-discovers every setting in DefaultPalWorldSettings.ini, so you can tweak all 109 fields from the dashboard, not just the curated subset most tools expose.

Mod browser supports the PalSchema curated list (more entries coming, the Palworld mod ecosystem is smaller than Valheim's).

https://github.com/tyrus2244/GameServerControl
```

### r/ARK

```
Title: One-click ARK ASA / ARK SE dedicated server install + Workshop mod browser

For anyone running ARK dedicated.

- Pick ARK ASA (app 2430930) or ARK SE (app 376030), point at an install path, done. SteamCMD runs in the background with a real progress bar (the % parsed from steamcmd's output, not a spinner).
- ARK ASA installs are ~50GB so the live progress matters.
- Paste a Workshop URL or ID, agent runs +workshop_download_item, updates your ?GameModIds= launch arg automatically.
- RCON kick/ban/broadcast.
- Backups + restore of ShooterGame/Saved.

https://github.com/tyrus2244/GameServerControl
```

---

## awesome-selfhosted PR

Section: Games (look for `## Games` in their README).

Entry to add (alphabetical position):

```
- [GameServerControl](https://github.com/tyrus2244/GameServerControl) - Control panel for Steam dedicated game servers (Valheim, Satisfactory, Palworld, ARK, Rust, 7 Days to Die, Terraria, Don't Starve Together, Project Zomboid). One-click install via SteamCMD, mod browsers (Thunderstore, ficsit.app, Workshop), RCON player management, scheduled backups, Discord webhooks, multi-host federation. Browser UI + native Windows client. `MIT` `C#`
```

PR title: `Add GameServerControl`

PR body:

```
- Open source, MIT.
- Self-hosted (Windows service / systemd unit / macOS LaunchAgent), no SaaS dependency.
- Documented install on Windows, Linux, and macOS.
- Active development.

Category: Games. Fits alongside other game-server admin tools in that section.
```
