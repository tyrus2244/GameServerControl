# What's left for you to do (~15 min total)

Everything that doesn't need your GitHub login or a screen capture is already done and
pushed to `main`. Here's the action list.

## 1. Set GitHub topic tags (~30 sec)

Topics drive GitHub search discovery. Without them the repo is invisible.

### Option A — Web UI (fastest)

1. Go to https://github.com/tyrus2244/GameServerControl
2. Click the ⚙ gear icon next to "About" (top-right of the description box)
3. Paste this list into the **Topics** field:

```
game-server  dedicated-server  self-hosted  homelab  dotnet  csharp  wpf  aspnet-core  tailscale  valheim  satisfactory  palworld  ark-survival  rust-game  steamcmd  rcon  dashboard  control-panel
```

4. Click **Save changes**.

### Option B — gh CLI (if you've kept it auth'd)

```powershell
gh repo edit tyrus2244/GameServerControl `
    --add-topic game-server,dedicated-server,self-hosted,homelab,dotnet,csharp,wpf,aspnet-core,tailscale,valheim,satisfactory,palworld,ark-survival,rust-game,steamcmd,rcon,dashboard,control-panel
```

If gh isn't auth'd: `gh auth login` once, then run the above.

## 2. Take screenshots (~5 min)

Follow [SCREENSHOTS.md](SCREENSHOTS.md). Capture 5 PNGs from the running app, drop them
into `docs/screenshots/`, then:

```powershell
cd C:\Users\tyrus\Claude\GameServerControl
git add docs/screenshots/*.png
git commit -m "Add README screenshots"
git push origin main
```

The README gallery + every promotion post will instantly show the images — they reference
the same `docs/screenshots/*.png` paths.

## 3. Post promotion (~10 min)

Copy-paste from [PROMOTION.md](PROMOTION.md). Recommended order:

1. **r/selfhosted** — best signal-to-noise for tools like this. Highest priority.
2. **Hacker News (Show HN)** — slot it for Tue/Wed 7-10am PT. Highest reach if it lands.
3. **r/homelab** — same body as r/selfhosted with a tweaked title.
4. **Per-game subreddits** (r/valheim, r/SatisfactoryGame, r/Palworld, r/ARK) — space these out a day or two each.
5. **Awesome-Selfhosted PR** — formality. Won't drive immediate traffic but cements long-tail discoverability.

## 4. (Optional) Make a short demo GIF

A 3-5 second GIF of the Create Server wizard is gold for Reddit. See SCREENSHOTS.md for
the recipe — use [ScreenToGif](https://www.screentogif.com/), save as
`docs/screenshots/create-wizard.gif`, reference it in the r/selfhosted post.

---

## What I'm already on the hook for

When you post and people respond, I'll handle:

* Drafting replies to feedback / questions
* Iterating on the README based on what readers ask about
* Building any feature requested that fits the project
* Tweaks to the awesome-selfhosted PR if the maintainers want changes

Just paste what someone said and I'll write the response.

---

## Why these specific topic tags

* `game-server`, `dedicated-server` — the core discoverability bucket
* `self-hosted`, `homelab` — pulls in the awesome-selfhosted / r/selfhosted crowd
* `dotnet`, `csharp`, `wpf`, `aspnet-core` — language/framework filters
* `tailscale` — niche but high-signal; Tailscale's own community shares projects tagged this way
* `valheim`, `satisfactory`, `palworld`, `ark-survival`, `rust-game` — per-game discoverability for people searching for tools specific to one game
* `steamcmd`, `rcon` — capability tags
* `dashboard`, `control-panel` — UI category

GitHub allows up to 20 topics — we're at 18, comfortable buffer.

❤ https://paypal.me/TKECLIPSE
