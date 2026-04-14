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
CONFIG="$INSTALL_DIR/config.txt"
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

# Stop existing service if running (ignore errors if not installed yet)
if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
    echo "==> Stopping existing service"
    systemctl stop "$SERVICE_NAME"
fi

echo "==> Downloading latest deploy-server binary"
curl -fsSL "https://github.com/$REPO/releases/latest/download/deploy-server" -o "$BINARY"
chmod +x "$BINARY"

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

# Write config.txt only on first install — preserve existing token on updates
if [ ! -f "$CONFIG" ]; then
    echo "==> Writing default config.txt (edit before starting)"
    cat > "$CONFIG" << EOF
# Authentication token — change this before starting the service
# Generate one with: openssl rand -base64 32
token=your-secret-token-here

# Port the server listens on
port=9090
EOF
    echo ""
    echo "  !! Edit $CONFIG and set a strong token before continuing !!"
    echo "     Then run: sudo systemctl start $SERVICE_NAME"
    echo ""
else
    echo "==> Existing config.txt preserved"
    systemctl restart "$SERVICE_NAME"
    echo "==> Service restarted with latest binary"
fi

echo ""
echo "Done. View logs with:"
echo "  journalctl -u $SERVICE_NAME -f"
