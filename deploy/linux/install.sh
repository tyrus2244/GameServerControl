#!/usr/bin/env bash
# GameServerControl Agent — Linux installer.
#
# Builds + installs the agent under /opt/gameservercontrol/agent and registers
# a systemd unit. Designed to run on Debian/Ubuntu and Fedora/RHEL-likes.
# Requires sudo for systemd integration + user creation.

set -euo pipefail

INSTALL_DIR="${INSTALL_DIR:-/opt/gameservercontrol/agent}"
SERVICE_USER="${SERVICE_USER:-gsc}"
UNIT_NAME="gameservercontrol-agent.service"
REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

echo "=== GameServerControl Agent — Linux install ==="
echo "Install dir : $INSTALL_DIR"
echo "Service user: $SERVICE_USER"
echo

if [[ $EUID -ne 0 ]]; then
  echo "This script needs root (it creates a system user + installs a systemd unit)."
  echo "Re-run with: sudo $0"
  exit 1
fi

# 1) .NET 8 SDK present?
if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: dotnet CLI not found. Install .NET 8 SDK first:"
  echo "  Debian/Ubuntu: https://learn.microsoft.com/dotnet/core/install/linux-ubuntu"
  echo "  Fedora/RHEL:   https://learn.microsoft.com/dotnet/core/install/linux-rhel"
  exit 1
fi

# 2) Build + publish
echo "=== Building agent ==="
cd "$REPO_ROOT"
dotnet publish src/GameServerControl.Agent -c Release -r linux-x64 --self-contained false -o "$INSTALL_DIR.tmp"

# 3) Create service user if missing
if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  echo "=== Creating system user $SERVICE_USER ==="
  useradd --system --create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

# 4) Stop existing service if running (we're about to overwrite the binaries)
if systemctl is-active --quiet "$UNIT_NAME"; then
  echo "=== Stopping existing $UNIT_NAME ==="
  systemctl stop "$UNIT_NAME"
fi

# 5) Move into place
echo "=== Installing into $INSTALL_DIR ==="
mkdir -p "$(dirname "$INSTALL_DIR")"
rm -rf "$INSTALL_DIR"
mv "$INSTALL_DIR.tmp" "$INSTALL_DIR"

# Make the .NET entry-point executable.
chmod +x "$INSTALL_DIR/GameServerControl.Agent" 2>/dev/null || true

# 6) Default config if absent
if [[ ! -f "$INSTALL_DIR/appsettings.json" ]]; then
  cp "$INSTALL_DIR/appsettings.json.example" "$INSTALL_DIR/appsettings.json"
  echo "Wrote default appsettings.json (agent will generate API token on first boot)."
fi
if [[ ! -f "$INSTALL_DIR/servers.json" ]]; then
  echo '{"Servers":[]}' > "$INSTALL_DIR/servers.json"
fi

chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR"

# 7) Install systemd unit
echo "=== Installing systemd unit ==="
cp "$REPO_ROOT/deploy/linux/$UNIT_NAME" "/etc/systemd/system/$UNIT_NAME"
systemctl daemon-reload
systemctl enable "$UNIT_NAME"
systemctl start "$UNIT_NAME"

# 8) Show the generated token (from journalctl)
sleep 2
echo
echo "=== Generated API token (copy this into your client/web UI) ==="
journalctl -u "$UNIT_NAME" --no-pager --since "1 minute ago" 2>/dev/null \
  | grep -A 2 "GENERATED NEW AGENT API TOKEN" \
  || echo "(no token printed — likely already set in appsettings.json)"

echo
echo "=== Done ==="
echo "Service status: systemctl status $UNIT_NAME"
echo "Logs:           journalctl -u $UNIT_NAME -f"
echo "Web UI:         http://<this-host>:5099/"
echo "Edit config:    $INSTALL_DIR/appsettings.json"
