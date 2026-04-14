# Citadel Deployment System

A cross-platform deployment system for managing service updates via HTTP. Deploy applications from Windows or Linux to a central deployment server that manages systemd services.

## Overview

The Citadel Deployment System consists of three components:

- **Deployment Server** (`deploy-server.py`) - Python HTTP server running on your deployment target (Linux)
- **Linux Client** (`deploy.sh`) - Bash script for deploying from Linux systems
- **Windows Client** (`deploy.bat`) - Batch script for deploying from Windows systems

### Architecture

```
Client (Linux/Windows)
    ↓ (POST zip file + token)
Deployment Server (HTTP on port 9090)
    ↓ (extract, stop service, deploy, start)
Systemd Service on target system
```

## Quick Start

### 1. Server Setup (on target Linux server)

Download the server script from the latest release:

```bash
curl -LO https://github.com/mssbabou/citadel-deployment/releases/latest/download/deploy-server.py
```

First run will create a `config.txt` in the same directory — edit it with your token:

```bash
python3 deploy-server.py   # creates config.txt then exits
nano config.txt
```

**Option A: Run as background service**

```bash
sudo python3 deploy-server.py --install
```

This will:
- Create a systemd service file
- Enable auto-start on boot
- Start the service immediately

View logs:

```bash
journalctl -u deploy-server.service -f
```

**Option B: Run directly**

```bash
python3 deploy-server.py
```

Server will listen on `http://0.0.0.0:9090`

### 2. Configure Your Service

The deployment server uses systemd to manage services. Create a systemd service file for your application:

```bash
sudo nano /etc/systemd/system/app.service
```

Example service file:

```ini
[Unit]
Description=My Application
After=network.target

[Service]
Type=simple
User=www-data
WorkingDirectory=/opt/app
ExecStart=/opt/app/run.sh
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
```

Enable the service:

```bash
sudo systemctl daemon-reload
sudo systemctl enable app.service
sudo systemctl start app.service
```



## Client Scripts

Download the client script and config template for your platform from the latest release:

```bash
# Linux/macOS
curl -LO https://github.com/mssbabou/citadel-deployment/releases/latest/download/deploy.sh
curl -LO https://github.com/mssbabou/citadel-deployment/releases/latest/download/.env.example
chmod +x deploy.sh
cp .env.example .env
nano .env   # fill in AUTH_TOKEN, DEPLOY_URL, SERVICE, DEPLOY_DIR
```

```powershell
# Windows (PowerShell)
Invoke-WebRequest https://github.com/mssbabou/citadel-deployment/releases/latest/download/deploy.bat -OutFile deploy.bat
Invoke-WebRequest https://github.com/mssbabou/citadel-deployment/releases/latest/download/.env.example -OutFile .env.example
Copy-Item .env.example .env
notepad .env   # fill in AUTH_TOKEN, DEPLOY_URL, SERVICE, DEPLOY_DIR
```

## Usage Guide

### How Deployment Works

1. **Client creates zip** - Compresses your source directory into a zip file
2. **Client sends to server** - Posts the zip with authentication token to server
3. **Server processes** - Extracts the zip to temporary directory
4. **Service stop** - Stops the systemd service
5. **Files replace** - Replaces deployment directory with new files
6. **Service start** - Restarts the systemd service
7. **Cleanup** - Removes temporary files

### Error Handling

If deployment fails, the system will:
- Print error message and HTTP status code
- Attempt to restart the service (even if deployment failed)
- Exit with error code for scripting

Check server logs for detailed errors:

```bash
journalctl -u deploy-server.service -n 50
```

## Security

### Important

- **Change the default token** in `config.txt` to a strong, random value
- **Use HTTPS** in production by running the server behind a reverse proxy (nginx/Apache)
- **Firewall access** - only allow deployment clients to reach the server
- **Protect `.env` files** - use `chmod 600` on Linux to restrict permissions
- **Validate tokens** - make token changes regularly

### Production Checklist

- [ ] Generate strong authentication token (32+ characters)
- [ ] Set up firewall rules for port 9090
- [ ] Configure reverse proxy with HTTPS/TLS
- [ ] Set appropriate file permissions on `.env` files
- [ ] Monitor systemd logs regularly
- [ ] Backup `config.txt` securely

## Troubleshooting

### "unauthorized" Response

- Verify `AUTH_TOKEN` in `.env` matches token in server's `config.txt`
- Check server logs: `journalctl -u deploy-server.service -f`

### "not a valid zip" Error

- Ensure the source is a valid directory or zip file
- Try zipping manually: `zip -r app.zip app-directory/`

### Service Won't Start After Deployment

- Check service logs: `journalctl -u app.service -n 50`
- Verify `DEPLOY_DIR` permissions match service `User`
- Manually test service: `sudo systemctl start app.service`

### Permission Denied on Server

- Ensure Python script has execute permissions: `chmod +x deploy-server.py`
- For `--install`, use sudo: `sudo python3 deploy-server.py --install`

### Connection Refused

- Verify server is running: `systemctl status deploy-server.service`
- Check port: `netstat -tlnp | grep 9090`
- Verify firewall rules allow connection

## Configuration Reference

### config.txt (Server)

```ini
# Authentication token for client requests
# Generate strong random token
token=your-secret-token-here

# HTTP port for deployment server
# Default: 9090
port=9090
```

## Development

### Project Structure

```
citadel-deployment/
├── deploy-server.py         # Python HTTP server (deployment target)
├── deploy.sh                # Bash client template
├── deploy.bat               # Batch client template
├── test_local_deployment.py # Manual local test script
├── setup.sh                 # Development environment setup script
├── config.txt               # Server configuration (generated on first run)
├── config.txt.example       # Server configuration template
├── .env.example             # Client authentication template
├── .gitignore               # Git ignore rules
├── requirements-test.txt    # Test dependencies
├── README.md                # Full documentation
├── QUICK_START.md           # Quick setup guide
└── tests/                   # Integration and unit tests
    ├── test_deployment.py   # Deployment tests
    ├── conftest.py          # Pytest fixtures
    └── README.md            # Test documentation
```

## Testing

The project includes comprehensive integration tests for the deployment server functionality. There are two ways to test:

### Setup (Virtual Environment)

First-time setup - create and activate virtual environment:

```bash
# Option 1: Automated setup
chmod +x setup.sh
./setup.sh

# Option 2: Manual setup
python3 -m venv .venv
source .venv/bin/activate  # Linux/macOS
# or .venv\Scripts\activate on Windows
pip install -r requirements-test.txt
```

For future sessions, just activate the environment:

```bash
source .venv/bin/activate
```

### Quick Local Test (Manual)

Test the server quickly with real HTTP requests:

```bash
# Terminal 1: Start the server
python3 deploy-server.py

# Terminal 2: Run local test (with venv activated)
python3 test_local_deployment.py
```

This script:
- Verifies the server is running
- Tests authentication (correct and incorrect tokens)
- Creates a test app zip and sends it to the server
- Reports success/failure

### Automated Tests (Pytest)

Run all tests:

```bash
# Run all tests
pytest tests/ -v

# Run with coverage report
pytest tests/ --cov=. --cov-report=html
```

Tests cover:
- Configuration loading
- Zip file validation and extraction
- File handling and special characters
- Directory replacement (deployment workflow)
- Multipart form data parsing
- Error handling for invalid/corrupt files
- Large file handling

See [tests/README.md](tests/README.md) for detailed information.

## License

[Add your license here]

## Support

For issues or questions:
1. Check logs: `journalctl -u deploy-server.service -f`
2. Review configuration files
3. Verify network connectivity
4. Check firewall rules

## Changelog

### Version 1.0
- Initial release
- HTTP server with token authentication
- Systemd service management
- Multi-platform client support (Linux/Windows templates)
