#!/usr/bin/env bash
# GameServerControl — macOS installer / updater that downloads pre-built release tarballs.
#
# Run with:
#   curl -fsSL https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/macos/install-from-release.sh | bash
#
# What it does (idempotent — re-run to update):
#   1. Detects Apple Silicon vs Intel and picks the correct release asset.
#   2. Ensures .NET 8 ASP.NET runtime is present (uses Microsoft's install script if missing).
#   3. Downloads GameServerControl-Agent-macos-{arm64|x64}.tar.gz from GitHub Releases.
#   4. Unloads the LaunchAgent if it's already running.
#   5. Extracts to ~/Library/Application Support/GameServerControl/Agent.
#   6. Preserves appsettings.json / servers.json / tokens.json across upgrades.
#   7. Installs the LaunchAgent plist into ~/Library/LaunchAgents and loads it.
#   8. Prints the auto-generated API token once the agent has started.
#
# Why user-scoped (LaunchAgent) instead of root-scoped (LaunchDaemon):
#   - No sudo. Friendlier first install.
#   - Auto-login Macs (typical for headless dedicated-server boxes) still get autostart.
#   - Logs land in the user's Library/Logs where Console.app already looks.
#
# Override env vars (set on the same line as curl|bash):
#   GSC_REPO       — owner/name (default: tyrus2244/GameServerControl)
#   GSC_INSTALL    — install dir (default: ~/Library/Application Support/GameServerControl/Agent)
#   GSC_VERSION    — pin a specific tag like v1.0.1 (default: latest)

set -euo pipefail

REPO="${GSC_REPO:-tyrus2244/GameServerControl}"
DEFAULT_INSTALL="$HOME/Library/Application Support/GameServerControl/Agent"
INSTALL_DIR="${GSC_INSTALL:-$DEFAULT_INSTALL}"
LOG_DIR="$HOME/Library/Logs/GameServerControl"
LAUNCH_AGENT_DIR="$HOME/Library/LaunchAgents"
PLIST_NAME="com.tk-eclipse.gameservercontrol-agent.plist"
PLIST_DST="$LAUNCH_AGENT_DIR/$PLIST_NAME"
VERSION="${GSC_VERSION:-}"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; GRAY='\033[0;90m'; NC='\033[0m'
step()  { echo -e "\n${CYAN}==>${NC} $*"; }
ok()    { echo -e "    ${GREEN}OK:${NC} $*"; }
skip()  { echo -e "    ${GRAY}-- $*${NC}"; }
warn()  { echo -e "    ${YELLOW}!! $*${NC}"; }
fail()  { echo -e "\n${RED}ERROR:${NC} $*\n"; exit 1; }

echo -e "${RED}=================================================================${NC}"
echo -e "${RED}  TK-ECLIPSE  ·  GameServerControl  ·  macOS installer           ${NC}"
echo -e "${RED}=================================================================${NC}"

# ---- Platform detection ----
# uname -m returns arm64 on Apple Silicon, x86_64 on Intel. The release pipeline produces
# two tarballs (macos-arm64 + macos-x64); we pick whichever matches.
arch_raw="$(uname -m)"
case "$arch_raw" in
    arm64)  arch="arm64"  ;;
    x86_64) arch="x64"    ;;
    *)      fail "Unsupported architecture: $arch_raw (expected arm64 or x86_64)." ;;
esac
ok "Architecture: $arch"

# ---- Prereqs ----
step "Checking prerequisites"
for tool in curl tar launchctl; do
    command -v "$tool" >/dev/null 2>&1 || fail "$tool not found in PATH (this is unusual on macOS)."
done
ok "curl, tar, launchctl all present."

# ---- .NET 8 ASP.NET runtime ----
step ".NET 8 ASP.NET runtime"
DOTNET_ROOT_DEFAULT="$HOME/.dotnet"
DOTNET_ROOT_SYS="/usr/local/share/dotnet"
DOTNET_ROOT=""
if command -v dotnet >/dev/null 2>&1 && dotnet --list-runtimes 2>/dev/null | grep -q 'Microsoft.AspNetCore.App 8\.'; then
    # Already installed somewhere. Find where so we can put DOTNET_ROOT into the plist env block.
    DOTNET_ROOT="$(dirname "$(command -v dotnet)")"
    # If dotnet is /usr/local/bin/dotnet (Homebrew/Microsoft installer), DOTNET_ROOT is the .dotnet dir, not bin.
    if [[ -d "$DOTNET_ROOT_SYS" ]]; then DOTNET_ROOT="$DOTNET_ROOT_SYS"; fi
    if [[ -d "$DOTNET_ROOT_DEFAULT" ]]; then DOTNET_ROOT="$DOTNET_ROOT_DEFAULT"; fi
    ok "Already present at $DOTNET_ROOT."
else
    warn "Missing — installing via Microsoft's dotnet-install.sh script (user-scoped, no sudo)."
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --runtime aspnetcore --channel 8.0 --install-dir "$DOTNET_ROOT_DEFAULT"
    DOTNET_ROOT="$DOTNET_ROOT_DEFAULT"
    # Add to ~/.zshrc so future shells also see it (idempotent — skip if line already exists).
    if ! grep -q 'DOTNET_ROOT="$HOME/.dotnet"' "$HOME/.zshrc" 2>/dev/null; then
        {
            echo ''
            echo '# GameServerControl: .NET 8 runtime'
            echo 'export DOTNET_ROOT="$HOME/.dotnet"'
            echo 'export PATH="$DOTNET_ROOT:$PATH"'
        } >> "$HOME/.zshrc"
        ok "Added DOTNET_ROOT to ~/.zshrc (new shells will see dotnet on PATH)."
    fi
    ok ".NET 8 runtime installed at $DOTNET_ROOT."
fi

# ---- Resolve release ----
step "Resolving release from GitHub"
if [[ -n "$VERSION" ]]; then
    api_url="https://api.github.com/repos/$REPO/releases/tags/$VERSION"
else
    api_url="https://api.github.com/repos/$REPO/releases/latest"
fi
release_json="$(curl -fsSL -H "User-Agent: gsc-installer" "$api_url")"
tag="$(echo "$release_json" | grep -oE '"tag_name":[[:space:]]*"[^"]+"' | head -1 | sed -E 's/.*"([^"]+)"$/\1/')"
[[ -n "$tag" ]] || fail "Could not resolve release tag."
ok "Tag: $tag"

asset_name="GameServerControl-Agent-macos-${arch}.tar.gz"
tar_url="$(echo "$release_json" | grep -oE '"browser_download_url":[[:space:]]*"https://[^"]+'"$asset_name"'"' | head -1 | sed -E 's/.*"(https[^"]+)"$/\1/')"
[[ -n "$tar_url" ]] || fail "Release $tag is missing $asset_name. Older releases may not have macOS support."

# ---- Download ----
step "Downloading $asset_name"
tmp_dir="$(mktemp -d -t gsc-install)"
trap "rm -rf $tmp_dir" EXIT
curl -fsSL "$tar_url" -o "$tmp_dir/agent.tar.gz"
ok "Downloaded ($(du -h "$tmp_dir/agent.tar.gz" | cut -f1))"

# ---- Stop existing LaunchAgent ----
if launchctl list | grep -q 'com.tk-eclipse.gameservercontrol-agent'; then
    step "Unloading existing LaunchAgent"
    launchctl unload "$PLIST_DST" 2>/dev/null || true
    sleep 1
    ok "Stopped."
fi

# ---- Preserve user data, then unpack ----
step "Installing into $INSTALL_DIR"
preserve_dir="$tmp_dir/preserved"
mkdir -p "$preserve_dir"
for f in appsettings.json servers.json tokens.json; do
    if [[ -f "$INSTALL_DIR/$f" ]]; then
        cp "$INSTALL_DIR/$f" "$preserve_dir/$f"
        skip "Preserving $f"
    fi
done

mkdir -p "$INSTALL_DIR" "$LOG_DIR" "$LAUNCH_AGENT_DIR"
find "$INSTALL_DIR" -mindepth 1 -delete
tar -xzf "$tmp_dir/agent.tar.gz" -C "$INSTALL_DIR"

# Restore user data on top
for f in appsettings.json servers.json tokens.json; do
    if [[ -f "$preserve_dir/$f" ]]; then
        cp "$preserve_dir/$f" "$INSTALL_DIR/$f"
    fi
done

# First-run defaults
if [[ ! -f "$INSTALL_DIR/appsettings.json" ]] && [[ -f "$INSTALL_DIR/appsettings.json.example" ]]; then
    cp "$INSTALL_DIR/appsettings.json.example" "$INSTALL_DIR/appsettings.json"
    ok "Wrote default appsettings.json (token generates on first boot)."
fi
[[ -f "$INSTALL_DIR/servers.json" ]] || echo '{"Servers":[]}' > "$INSTALL_DIR/servers.json"

chmod +x "$INSTALL_DIR/GameServerControl.Agent" 2>/dev/null || true
ok "Unpacked."

# ---- Install LaunchAgent plist ----
step "Installing LaunchAgent plist"
plist_template="$INSTALL_DIR/$PLIST_NAME"
[[ -f "$plist_template" ]] || fail "Tarball missing $PLIST_NAME — bad release artifact."

# Substitute the runtime paths. Using | as sed delimiter so / in paths is safe.
sed -e "s|{{INSTALL_DIR}}|$INSTALL_DIR|g" \
    -e "s|{{LOG_DIR}}|$LOG_DIR|g" \
    -e "s|{{DOTNET_ROOT}}|$DOTNET_ROOT|g" \
    "$plist_template" > "$PLIST_DST"

launchctl load -w "$PLIST_DST"
sleep 3
if launchctl list | grep -q 'com.tk-eclipse.gameservercontrol-agent'; then
    ok "LaunchAgent loaded + running."
else
    warn "LaunchAgent did not start. Check $LOG_DIR/agent.err.log"
fi

# ---- Try to surface the API token from logs ----
step "Looking for the API token in recent agent logs"
token_block=""
if [[ -f "$LOG_DIR/agent.out.log" ]]; then
    token_block="$(grep -A 3 'GENERATED NEW AGENT API TOKEN' "$LOG_DIR/agent.out.log" | tail -10 || true)"
fi
if [[ -n "$token_block" ]]; then
    echo ""
    echo -e "${YELLOW}   ----- AGENT API TOKEN (copy into the client/web UI) -----${NC}"
    echo "$token_block"
    echo -e "${YELLOW}   ----------------------------------------------------------${NC}"
else
    skip "No new token in log — agent is reusing the existing one in appsettings.json."
fi

echo ""
echo -e "${RED}=================================================================${NC}"
echo -e "${GREEN}  Done!  ($tag)${NC}"
echo ""
echo -e "${GRAY}  Status:    launchctl list | grep gameservercontrol${NC}"
echo -e "${GRAY}  Logs:      tail -f $LOG_DIR/agent.out.log${NC}"
echo -e "${GRAY}  Errors:    tail -f $LOG_DIR/agent.err.log${NC}"
echo -e "${GRAY}  Web UI:    http://<this-mac>:5099/${NC}"
echo -e "${GRAY}  Config:    $INSTALL_DIR/appsettings.json${NC}"
echo -e "${GRAY}  Unload:    launchctl unload $PLIST_DST${NC}"
echo ""
echo -e "${RED}  ❤ Support: https://paypal.me/TKECLIPSE${NC}"
echo -e "${RED}=================================================================${NC}"
