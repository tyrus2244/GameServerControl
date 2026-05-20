# Promotion playbook — TK-ECLIPSE / GameServerControl

Copy-paste these wherever you want to land them. Each draft is ready to go — screenshots
are the only thing missing, see [SCREENSHOTS.md](SCREENSHOTS.md) for the capture list.

**Recommended posting order** (highest signal → lowest, so feedback shapes the next post):

1. r/selfhosted on Reddit
2. Show HN on Hacker News
3. r/homelab on Reddit
4. Per-game subreddits (Valheim / Satisfactory / Palworld / ARK)
5. Awesome-selfhosted PR

Don't post all in the same hour — space them out over a week so each gets its own attention.

---

## 1) Show HN (Hacker News)

**Where:** https://news.ycombinator.com/submit

**Best time:** Tue/Wed 7–10am Pacific Time. Avoid Friday.

**Title** (80 char max, current is 78):

```
Show HN: GameServerControl – self-hosted dashboard for Steam dedicated servers
```

**URL field:** `https://github.com/tyrus2244/GameServerControl`

**Text field** (paste below, HN renders limited markdown):

```
I built GameServerControl — a self-hosted dashboard for managing Steam dedicated
game servers (Valheim, Satisfactory, Palworld, ARK, Rust, 7DTD, Terraria, etc.)
from a single pane of glass.

The thing I always wanted: instead of SSH-ing into a box, running steamcmd
manually, hand-rolling systemd units, and fumbling with RCON via netcat, I click
"Create Server", pick a game, and it installs, configures, registers, and starts
the server. SteamCMD gets auto-downloaded if it's missing.

What it does:
• 🚀 One-click Create Server (10 games preset, anonymous SteamCMD install)
• 🧩 Server-side mod management — Thunderstore (Valheim), ficsit.app (Satisfactory),
   curated GitHub Releases (Palworld), Steam Workshop (ARK). Auto-update + dep resolution.
• 📦 Backups + restore (zip-based or Hyper-V checkpoint, scheduled cadence)
• 👥 Live RCON player management (kick/ban/broadcast, auto-refresh)
• 📊 Live CPU/RAM charts per server, 5-min rolling history
• 🔔 Discord webhook alerts on crash detection (Running → NotRunning without a stop)
• 🛰 Multi-host federation (one dashboard, N agents on N machines)
• 🔑 Per-user tokens with Admin/ReadOnly roles
• ⏰ Scheduled maintenance (daily restart / weekly steamcmd update / hourly backup)

Stack: .NET 8 — ASP.NET Core agent (Windows service, Linux systemd, macOS LaunchAgent)
+ WPF dashboard (Windows) + browser web UI (works on any device). MIT license.
Self-signed HTTPS by default. Tailscale-friendly — designed to bind to a tailnet IP.

One-line install:
  Windows:  iwr https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/windows/install.ps1 | iex
  Linux:    curl -fsSL https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/linux/install-from-release.sh | sudo bash
  macOS:    curl -fsSL https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/macos/install-from-release.sh | bash

Each script auto-installs the .NET runtime if missing, preserves config on upgrade,
and registers the right service flavor (Windows service / systemd / LaunchAgent).

Feedback I'd love:
• What games should be next in the Create Server preset list?
• Anyone else wishing for a "headless mini-PC running game servers" experience
  that's not a $5/mo SaaS box?
• Sanity-check the architecture: agent broadcasts via SignalR, client is reactive WPF,
  web UI is plain HTML+JS (no framework). Anything dumb?

Repo: https://github.com/tyrus2244/GameServerControl
```

---

## 2) r/selfhosted

**Where:** https://reddit.com/r/selfhosted/submit

**Title:**

```
GameServerControl – self-hosted dashboard for Steam dedicated game servers (Valheim, Satisfactory, Palworld, ARK, Rust, …) with one-click install
```

**Tag:** Software → Software Development or Self Promotion (the sub enforces a flair).

**Body:**

```
I got tired of SSH-ing into my mini-PC every time I wanted to spin up a new game
server, restart Valheim after it crashed, or check who was online. So I built
**GameServerControl** — a self-hosted dashboard that runs as a service on the
host machine and exposes a slick browser UI (and a native Windows client).

[Screenshot: dashboard overview]

**The marquee feature:** click "🚀 Create Server", pick a game, choose where to
install it. The agent auto-downloads SteamCMD if missing, runs `+app_update`
with live progress, registers the server. From zero to "running Valheim/ARK/
Palworld" in one workflow.

[Screenshot: create wizard]

**What's in the box**

* 🚀 One-click Create Server — 10 games preset (Valheim, Palworld, Satisfactory,
  ARK ASA+SE, Rust, 7DTD, Terraria, Don't Starve Together, Project Zomboid)
* 🧩 Server-side mod management — Thunderstore + ficsit.app + Steam Workshop +
  GitHub Releases. Filters to server-side-only by default (your buddies don't
  need to install anything client-side). Auto-update + dep resolution.
* 📦 Backups + restore (zip-based, Hyper-V checkpoints if you have a VM,
  scheduled cadence with retention)
* 👥 Live player management via RCON — kick / ban / broadcast / shutdown
* 📊 Live CPU/RAM charts per server, 5-minute rolling history
* 🔔 Discord webhook alerts (start / stop / crash auto-detect / backup done)
* 🛰 Multi-host federation — control N agents from one dashboard
* 🔑 Per-user tokens with Admin / ReadOnly roles
* ⏰ Scheduled maintenance (daily restart, weekly update, hourly backup)

**Stack**

.NET 8, MIT license. Agent runs on Windows / Linux / macOS (Apple Silicon + Intel).
Web UI works in any modern browser. Native Windows dashboard for the desktop UX.
HTTPS with self-signed cert. Tailscale-friendly — bind to a tailnet IP and you're
private by default.

**Install — one line per OS**

Windows (PowerShell, Admin):
```
iwr https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/windows/install.ps1 | iex
```

Linux (systemd):
```
curl -fsSL https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/linux/install-from-release.sh | sudo bash
```

macOS (LaunchAgent, no sudo needed):
```
curl -fsSL https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/macos/install-from-release.sh | bash
```

Re-run the same command to update. User data (servers, tokens, configs) is
preserved across upgrades.

**Repo:** github.com/tyrus2244/GameServerControl

Built by [TK-ECLIPSE](https://paypal.me/TKECLIPSE). Open to ideas for what to
build next — what games would you want preset? What integrations are missing?
```

---

## 3) r/homelab

Same body as r/selfhosted but with the title tweaked:

```
[Project] One-click self-hosted control panel for game servers in your homelab (Valheim, Satisfactory, Palworld, ARK, Rust, ...)
```

---

## 4) Per-game subreddits

Lead with the game-specific value, not the project meta. r/selfhosted folks like
"my homelab project" framing; gamers want "the tool that makes running MY server
easier".

### r/valheim

**Title:**

```
[Tool] Browser dashboard for managing a Valheim dedicated server — one-click install, mod browser, backups, RCON, scheduled restarts
```

**Body:**

```
If you're running a Valheim dedicated server, I made a thing that takes the pain
out of it. **GameServerControl** is a self-hosted dashboard you run on the same
machine (or any Windows/Linux/macOS box on your network).

[Screenshot: Valheim server card in dashboard]

What it does for Valheim specifically:

* **One-click install** — pick "Valheim" → enter a name → done. SteamCMD runs
  in the background, server registers itself.
* **Mod browser** — searches valheim.thunderstore.io directly. Filter to
  server-side-only mods (your friends don't need to install anything on their
  end). Click "Install", it pulls the zip + dependencies + drops the DLL into
  BepInEx/plugins.
* **Auto-update mods** — "Check for updates" button compares installed versions
  against Thunderstore and lets you bulk-upgrade.
* **Live player list** via RCON — kick/ban/broadcast.
* **Backups + restore** — zips your `worlds_local` + `characters_local` on a
  schedule. Restore puts them back with one click.
* **Discord webhook** — get pinged when the server crashes, or when a scheduled
  backup completes.
* **Scheduled restarts** — daily 4am restart so memory leaks don't take you down.

One-line Windows install (PowerShell as Admin):
```
iwr https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/windows/install.ps1 | iex
```

Linux & macOS install commands + screenshots + full feature list:
github.com/tyrus2244/GameServerControl

MIT-licensed, no SaaS, your server stays your server. Built by TK-ECLIPSE.
```

### r/SatisfactoryGame

**Title:**

```
[Tool] One-click Satisfactory dedicated server install + browser dashboard (mod browser, backups, live AGS toggles)
```

**Body:**

```
Spent way too long manually setting up dedicated servers, so I built a tool that
does it in one click. GameServerControl runs as a service on your host machine,
gives you a browser dashboard (or native Windows app) for managing every server
in one place.

[Screenshot: Satisfactory server card]

Satisfactory-specific features:

* **One-click install** — pick "Satisfactory" → name + install path → SteamCMD
  fetches it in the background → server registers automatically.
* **Full ficsit.app mod browser** built into the dashboard. Search, filter to
  server-side-only mods, install with one click. Auto-update too.
* **Live Admin API integration** — claim server, rename, set client/admin
  passwords, toggle all 13 Advanced Game Settings (NoPower, GodMode, FlightMode,
  StartingTier, etc.) without restarting.
* **Session ID** displayed prominently like a Windrose invite code so you can
  share it.
* **Backups + restore** of FactoryGame/Saved.
* **Scheduled maintenance** — daily restart, weekly SteamCMD update, hourly backup.
* **Crash detection + Discord ping**.

One-line Windows install (PowerShell as Admin):
```
iwr https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/windows/install.ps1 | iex
```

Linux & macOS too. github.com/tyrus2244/GameServerControl — MIT license, no SaaS.
Built by TK-ECLIPSE.
```

### r/Palworld

**Title:**

```
[Tool] Self-hosted dashboard for managing a Palworld dedicated server — one-click install, RCON, backups, schemes
```

**Body:** (same shape as Valheim/Satisfactory, swap features:)

```
* Installs Palworld dedicated via SteamCMD with one click.
* Auto-discovered config — pulls every setting from DefaultPalWorldSettings.ini
  so you can tweak 109 fields, not just the handful most tools expose.
* RCON (port 25575 default) — kick/ban/broadcast/shutdown live.
* Mod browser — curated PalSchema + future entries.
* Backups + restore + scheduled cadence.
* Discord alerts on crash.

github.com/tyrus2244/GameServerControl
```

### r/ARK

**Title:**

```
[Tool] One-click ARK Survival Ascended / Survival Evolved dedicated server install + mod browser (browser dashboard)
```

**Body:**

```
* Installs ARK ASA (app 2430930) or ARK SE (app 376030) via SteamCMD in one click.
* Steam Workshop integration — paste a workshop URL or ID, agent runs
  `+workshop_download_item`, updates your `?GameModIds=` launch arg automatically.
* RCON live management.
* Backups + restore of ShooterGame/Saved.
* Discord crash alerts.

ARK ASA installs can take a while (~50 GB) — there's a real progress bar with %
parsed from SteamCMD's output, not just a spinner.

github.com/tyrus2244/GameServerControl
```

---

## 5) Awesome-Selfhosted PR

**Where:** https://github.com/awesome-selfhosted/awesome-selfhosted

**Steps:**
1. Fork the repo via the GitHub UI.
2. Edit `README.md` on your fork.
3. Find the "Games" section (Ctrl-F: `## Games`).
4. Add this line alphabetically into the section (it sits between "Foundry-VTT" and "Geoboard-Server" or whatever it lands next to):

```
- [GameServerControl](https://github.com/tyrus2244/GameServerControl) - Self-hosted control panel for Steam dedicated game servers (Valheim, Satisfactory, Palworld, ARK, Rust, 7 Days to Die, Terraria, Don't Starve Together, Project Zomboid). One-click install via SteamCMD, server-side mod management (Thunderstore + ficsit.app + Workshop), RCON player management, scheduled backups, Discord webhook alerts, multi-host federation. Browser UI + native Windows desktop client. `MIT` `C#`
```

5. Commit message: `Add GameServerControl to Games section`
6. PR title: `Add GameServerControl`
7. PR body — paste this:

```
**Project link:** https://github.com/tyrus2244/GameServerControl

**Self-hosted criteria:**
- ✅ Available under an open-source license (MIT)
- ✅ Self-hostable — runs as a service on the user's own machine (Windows service / systemd unit / macOS LaunchAgent)
- ✅ Active development — v1.0.3 released today, regular commits
- ✅ Documentation — README covers install, config, security, contribution
- ✅ No SaaS / no cloud dependency
- ✅ Tested deploy paths for Windows, Linux, and macOS (Apple Silicon + Intel)

**Category:** Games — it's a control panel for self-hosted game servers, fits alongside other game-server admin tools in the section.

**Tagline justification:** I included a longer feature list because game-server admin tools are typically single-game (e.g. just a Valheim panel, just a Minecraft panel) — calling out the multi-game support + the specific mod ecosystems matters for discoverability by the right users.

Built by TK-ECLIPSE. Open to feedback on category fit or formatting before merge.
```

---

## What I can't do for you (sorry)

I can't:
* Post these to HN or Reddit — your accounts, you have to log in.
* Open the awesome-selfhosted PR — needs your GitHub credentials (you revoked the PATs).
* Set repo topic tags — same auth issue (see below for the workaround).

What I CAN do:
* If you post any of these and someone replies, I can draft the response. Just paste what they said.
* If a maintainer asks for changes on the awesome-selfhosted PR, I can edit the entry.
* If HN / Reddit feedback identifies a missing feature, I can build it.

---

❤ https://paypal.me/TKECLIPSE
