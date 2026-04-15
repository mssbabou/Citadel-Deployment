#!/bin/bash
# Citadel Deployment Server — installer / updater
# Run as root. Safe to run multiple times — always installs the latest release.
#
# Usage:
#   sudo bash install-server.sh            # install or update
#   sudo bash install-server.sh --uninstall  # remove everything

set -e

REPO="mssbabou/Citadel-Deployment"
INSTALL_DIR="/opt/citadel"
BINARY="$INSTALL_DIR/deploy-server"
CONFIG="$INSTALL_DIR/config.toml"
SERVICE_NAME="deploy-server.service"
SERVICE_PATH="/etc/systemd/system/$SERVICE_NAME"

if [ "$(id -u)" -ne 0 ]; then
    echo "Error: run with sudo" >&2
    exit 1
fi

if [ "${1:-}" = "--uninstall" ]; then
    echo "==> Stopping and disabling service"
    systemctl stop "$SERVICE_NAME" 2>/dev/null || true
    systemctl disable "$SERVICE_NAME" 2>/dev/null || true

    echo "==> Removing systemd unit"
    rm -f "$SERVICE_PATH"
    systemctl daemon-reload

    echo "==> Removing $INSTALL_DIR"
    rm -rf "$INSTALL_DIR"

    echo "Done. Citadel deploy server has been removed."
    exit 0
fi

echo "==> Creating install directory: $INSTALL_DIR"
mkdir -p "$INSTALL_DIR"

# Ensure unzip is available
if ! command -v unzip &>/dev/null; then
    apt-get install -y unzip 2>/dev/null || yum install -y unzip 2>/dev/null || true
fi

# Ensure .NET 10 runtime is available
if ! command -v dotnet &>/dev/null || ! dotnet --list-runtimes 2>/dev/null | grep -q "^Microsoft.NETCore.App 10\."; then
    echo "==> Installing .NET 10 runtime"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    bash /tmp/dotnet-install.sh --channel 10.0 --runtime dotnet --install-dir /usr/local/dotnet
    rm /tmp/dotnet-install.sh
    ln -sf /usr/local/dotnet/dotnet /usr/local/bin/dotnet 2>/dev/null || true
fi

# Stop existing service if running (ignore errors if not installed yet)
if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    echo "==> Stopping existing service"
    systemctl stop "$SERVICE_NAME"
fi

echo "==> Downloading latest deploy-server package"
curl -fsSL "https://github.com/$REPO/releases/latest/download/deploy-server.zip" -o /tmp/deploy-server.zip
unzip -o /tmp/deploy-server.zip -d "$INSTALL_DIR"
chmod +x "$BINARY"
rm /tmp/deploy-server.zip

echo "==> Writing systemd service"
cat > "$SERVICE_PATH" << EOF
[Unit]
Description=Citadel Deploy Server
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=$INSTALL_DIR
ExecStart=$BINARY
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE_NAME"

# Write config.toml only on first install — preserve existing config on updates
if [ ! -f "$CONFIG" ]; then
    echo "==> Writing default config.toml (edit before starting)"
    cat > "$CONFIG" << EOF
[server]
token = "your-secret-token-here"
port = 9090

# Define one or more deployment profiles.
# Clients send X-Profile: <name> to target a profile.
# All listed services are stopped before deploy and started after.

[profiles.myapp]
deploy_dir = "/opt/myapp"
services = ["myapp.service"]
EOF
    YELLOW='\033[0;33m'
    WHITE='\033[1;37m'
    RESET='\033[0m'
    echo ""
    echo "Installation complete. To finish setup:"
    echo ""
    echo -e "${YELLOW}============================================================${RESET}"
    echo ""
    echo -e "${YELLOW}  1. Edit the config file:${RESET}"
    echo ""
    echo -e "${WHITE}       nano $CONFIG${RESET}"
    echo ""
    echo -e "${YELLOW}  2. Start the server:${RESET}"
    echo ""
    echo -e "${WHITE}       sudo systemctl start $SERVICE_NAME${RESET}"
    echo ""
    echo -e "${YELLOW}============================================================${RESET}"
else
    echo "==> Existing config.toml preserved"
    systemctl restart "$SERVICE_NAME"
    echo "==> Service restarted with latest binary"
    echo ""
    echo "Done. View logs with:"
    echo "  journalctl -u $SERVICE_NAME -f"
fi
