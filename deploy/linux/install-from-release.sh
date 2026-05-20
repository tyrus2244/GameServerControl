#!/usr/bin/env bash
# GameServerControl — Linux installer / updater that downloads pre-built release tarballs.
#
# Run as root (or via sudo):
#   curl -fsSL https://raw.githubusercontent.com/tyrus2244/GameServerControl/main/deploy/linux/install-from-release.sh | sudo bash
#
# What it does (idempotent — re-run to update):
#   1. Ensures .NET 8 ASP.NET runtime is present (uses Microsoft's install script if missing).
#   2. Hits the GitHub Releases API for the latest release.
#   3. Downloads GameServerControl-Agent-linux.tar.gz.
#   4. Stops the gameservercontrol-agent.service if it's running.
#   5. Extracts into /opt/gameservercontrol/agent.
#   6. Preserves existing appsettings.json + servers.json + tokens.json (user data).
#   7. Creates gsc system user if missing.
#   8. Installs + enables the systemd unit.
#   9. Tails journalctl long enough to surface the auto-generated API token.
#
# Override env vars (set on the same line as curl|bash, e.g. GSC_VERSION=v1.0.0 sudo bash):
#   GSC_REPO       — owner/name (default: tyrus2244/GameServerControl)
#   GSC_INSTALL    — install dir (default: /opt/gameservercontrol/agent)
#   GSC_USER       — service user (default: gsc)
#   GSC_VERSION    — pin a specific tag like v1.0.0 (default: latest)

set -euo pipefail

REPO="${GSC_REPO:-tyrus2244/GameServerControl}"
INSTALL_DIR="${GSC_INSTALL:-/opt/gameservercontrol/agent}"
SERVICE_USER="${GSC_USER:-gsc}"
VERSION="${GSC_VERSION:-}"
UNIT_NAME="gameservercontrol-agent.service"

# ---- Logging helpers ----
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; GRAY='\033[0;90m'; NC='\033[0m'
step()  { echo -e "\n${CYAN}==>${NC} $*"; }
ok()    { echo -e "    ${GREEN}OK:${NC} $*"; }
skip()  { echo -e "    ${GRAY}-- $*${NC}"; }
warn()  { echo -e "    ${YELLOW}!! $*${NC}"; }
fail()  { echo -e "\n${RED}ERROR:${NC} $*\n"; exit 1; }

echo -e "${RED}=================================================================${NC}"
echo -e "${RED}  TK-ECLIPSE  ·  GameServerControl  ·  Linux installer           ${NC}"
echo -e "${RED}=================================================================${NC}"

if [[ $EUID -ne 0 ]]; then
    fail "Run as root (sudo)."
fi

# ---- Tool prerequisites ----
step "Checking prerequisites"
for tool in curl tar systemctl; do
    command -v "$tool" >/dev/null 2>&1 || fail "$tool not found in PATH."
done
ok "curl, tar, systemctl all present."

# ---- .NET 8 ASP.NET runtime ----
step ".NET 8 ASP.NET runtime"
if command -v dotnet >/dev/null 2>&1 && dotnet --list-runtimes 2>/dev/null | grep -q 'Microsoft.AspNetCore.App 8\.'; then
    ok "Already present."
else
    warn "Missing — installing via Microsoft's dotnet-install.sh script."
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --runtime aspnetcore --channel 8.0 --install-dir /usr/share/dotnet
    ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
    ok ".NET 8 runtime installed at /usr/share/dotnet."
fi

# ---- Resolve release ----
step "Resolving release from GitHub"
if [[ -n "$VERSION" ]]; then
    api_url="https://api.github.com/repos/$REPO/releases/tags/$VERSION"
else
    api_url="https://api.github.com/repos/$REPO/releases/latest"
fi
release_json="$(curl -fsSL -H "User-Agent: gsc-installer" "$api_url")"
tag="$(echo "$release_json" | grep -oP '"tag_name":\s*"\K[^"]+' | head -1)"
[[ -n "$tag" ]] || fail "Could not resolve release tag. Is the repo public + does a release exist?"
ok "Tag: $tag"

# Extract download URL for the agent tarball
tar_url="$(echo "$release_json" | grep -oP '"browser_download_url":\s*"\Khttps://[^"]+GameServerControl-Agent-linux\.tar\.gz' | head -1)"
[[ -n "$tar_url" ]] || fail "Release $tag is missing GameServerControl-Agent-linux.tar.gz asset."

# ---- Download tarball ----
step "Downloading agent tarball"
tmp_dir="$(mktemp -d)"
trap "rm -rf $tmp_dir" EXIT
curl -fsSL "$tar_url" -o "$tmp_dir/agent.tar.gz"
ok "Downloaded to $tmp_dir/agent.tar.gz"

# ---- Stop service if running ----
if systemctl is-active --quiet "$UNIT_NAME"; then
    step "Stopping existing $UNIT_NAME"
    systemctl stop "$UNIT_NAME"
    sleep 1
    ok "Stopped."
fi

# ---- Preserve user data, then overwrite ----
step "Installing into $INSTALL_DIR"
preserve_dir="$tmp_dir/preserved"
mkdir -p "$preserve_dir"
for f in appsettings.json servers.json tokens.json; do
    if [[ -f "$INSTALL_DIR/$f" ]]; then
        cp "$INSTALL_DIR/$f" "$preserve_dir/$f"
        skip "Preserving $f"
    fi
done

mkdir -p "$INSTALL_DIR"
# Wipe but keep the directory itself so any bind mounts / capabilities remain.
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

# ---- Service user ----
step "Service user '$SERVICE_USER'"
if ! id "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --create-home --shell /usr/sbin/nologin "$SERVICE_USER"
    ok "Created."
else
    skip "Already exists."
fi
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"

# ---- systemd unit ----
step "Installing systemd unit"
unit_src="$INSTALL_DIR/$UNIT_NAME"
if [[ -f "$unit_src" ]]; then
    cp "$unit_src" "/etc/systemd/system/$UNIT_NAME"
else
    fail "Tarball missing $UNIT_NAME — bad release artifact."
fi
systemctl daemon-reload
systemctl enable "$UNIT_NAME" >/dev/null 2>&1 || true
systemctl start "$UNIT_NAME"
sleep 2
if systemctl is-active --quiet "$UNIT_NAME"; then
    ok "$UNIT_NAME running."
else
    warn "Service did not become active. Check: journalctl -u $UNIT_NAME -n 50"
fi

# ---- Try to surface the API token ----
step "Looking for the API token in recent journal entries"
token_block="$(journalctl -u "$UNIT_NAME" --no-pager --since '2 minutes ago' 2>/dev/null | grep -A 3 'GENERATED NEW AGENT API TOKEN' || true)"
if [[ -n "$token_block" ]]; then
    echo ""
    echo -e "${YELLOW}   ----- AGENT API TOKEN (copy into the client/web UI) -----${NC}"
    echo "$token_block"
    echo -e "${YELLOW}   ----------------------------------------------------------${NC}"
else
    skip "No new token printed — the agent is reusing the existing one in appsettings.json."
fi

echo ""
echo -e "${RED}=================================================================${NC}"
echo -e "${GREEN}  Done!  ($tag)${NC}"
echo ""
echo -e "${GRAY}  Service:    systemctl status $UNIT_NAME${NC}"
echo -e "${GRAY}  Logs:       journalctl -u $UNIT_NAME -f${NC}"
echo -e "${GRAY}  Web UI:     http://<this-host>:5099/${NC}"
echo -e "${GRAY}  Config:     $INSTALL_DIR/appsettings.json${NC}"
echo ""
echo -e "${RED}  ❤ Support: https://paypal.me/TKECLIPSE${NC}"
echo -e "${RED}=================================================================${NC}"
