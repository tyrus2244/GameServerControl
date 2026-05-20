# Screenshots capture guide

The README and promotion posts reference 5 screenshots in `docs/screenshots/`. Capture each
one as a PNG, save with the **exact filename listed**, drop them in this folder, and commit.
Once they're in, the README gallery + every Reddit/HN post will show them automatically.

**Tools:** Windows Snip & Sketch (`Win + Shift + S`) or Greenshot. PNG, no JPG.
**Aim:** ~1600x1000 pixels per shot, keep aspect roughly 16:10. Don't bother retouching —
real screenshots > polished mockups for HN/Reddit readers.

## 1) `dashboard.png` — the hero shot

What to show:
- The WPF dashboard with **all 4 servers** visible (Windrose, Valheim, Satisfactory, Palworld)
- At least one server with **GAME UP** (green pill) and at least one with **GAME DOWN** (grey)
- The Windrose invite-code badge visible
- The 🚀 Create Server button in the header
- The ❤ PayPal · TK-ECLIPSE link bottom-right of the Activity pane

How: open the WPF client → make sure Windrose is running so the invite code shows up → press
`Win + Shift + S` → drag a rectangle around the whole window.

## 2) `create-wizard.png` — the marquee feature

What to show:
- The Create Server window on **step 2** (name + install location)
- A game selected (Valheim is nicest because of the ⚔ icon and short name)
- The EXE/ARGS preview box visible at the bottom

How: from the dashboard, click **🚀 Create Server**. The wizard opens on step 1. Click on
Valheim. You're now on step 2 with the form pre-populated. Snip the window.

## 3) `mods-window.png` — the mod browser

What to show:
- The Mods window for Valheim
- The **Browse** tab active, with a few mods returned in the list
- The "server-side" green pills visible on at least one result
- Cards showing icon + author + download count

How: from the dashboard, click a Valheim server's **🧩 Mods** button. Tab to Browse. Type
"world" or similar to get a populated list. Snip.

## 4) `players.png` — live RCON

What to show:
- The RCON Console window for Palworld (or any server with RCON configured)
- The Players list on the left (even if empty, the Kick/Ban buttons should be visible)
- Some output in the right-side log pane (run "Save World" once to populate it)
- Auto-refresh checkbox visible

How: click a server's **💬 Console** button. Click "💾 Save World" once so the log has
content. Snip.

## 5) `web-ui.png` — the responsive browser UI

What to show:
- `http://100.90.15.50:5099/` open in a browser (or `localhost:5099` if you've moved on)
- The new neutral dark palette
- The server cards as they appear in the web UI
- The bottom-right ❤ PayPal · TK-ECLIPSE link
- The 🚀 Create Server button up top

How: open the URL in Edge/Chrome/Firefox. Snip the **full visible area** including the
floating donate link.

---

## Optional but high-impact: animated GIF

For Reddit's r/selfhosted post, a short GIF of the Create Server wizard is gold. ~3-5 seconds:

1. Click 🚀 Create Server
2. Pick Valheim
3. Show step 2 fields auto-populated
4. (Stop here — don't actually run the install in the GIF)

Tool: [ScreenToGif](https://www.screentogif.com/) (free, MIT) or Giphy Capture. Save as
`docs/screenshots/create-wizard.gif`. Reference it in the README + r/selfhosted post.

---

## Once you have them

```bash
cd C:\Users\tyrus\Claude\GameServerControl
# Drop PNG files into docs\screenshots\
git add docs/screenshots/*.png
git commit -m "Add README screenshots"
git push origin main
```

The README gallery and all promotion posts (which reference raw.githubusercontent.com URLs)
will start showing the images immediately — no new release needed.
