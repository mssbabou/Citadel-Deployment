# Citadel Deployment System

A cross-platform deployment system for managing service updates via HTTP. Deploy applications from Windows or Linux to a central deployment server that manages systemd services.

## Overview

The Citadel Deployment System consists of three components:

- **Deployment Server** (`server/CitadelServer`) - .NET 8 HTTP server running on your deployment target (Linux)
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

Run the installer:

```bash
curl -fsSL https://github.com/mssbabou/Citadel-Deployment/releases/latest/download/install-server.sh | sudo bash
```

This will:
- Download the `deploy-server` binary to `/opt/citadel/`
- Create and enable a systemd service
- Write a default `config.txt` — edit it with your token before starting

```bash
sudo nano /opt/citadel/config.txt
sudo systemctl start deploy-server.service
```

View logs:

```bash
journalctl -u deploy-server.service -f
```

To update to the latest version at any time, just re-run the installer — your config is preserved.

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
nano .env   # fill in AUTH_TOKEN, DEPLOY_URL, DEPLOY_DIR (SERVICE is optional)
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

- The installer requires root: `curl ... | sudo bash`
- If the binary lost execute permissions: `sudo chmod +x /opt/citadel/deploy-server`

### Connection Refused

- Verify server is running: `systemctl status deploy-server.service`
- Check port: `netstat -tlnp | grep 9090`
- Verify firewall rules allow connection

## Building the Server

```bash
# Build self-contained single-file binary for Linux
dotnet publish server/CitadelServer -r linux-x64 --self-contained -p:PublishSingleFile=true -c Release
# Binary: server/CitadelServer/bin/Release/net8.0/linux-x64/publish/deploy-server
```

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
├── server/                      # .NET 8 deployment server
│   ├── CitadelServer.sln
│   ├── CitadelServer/           # Server project
│   │   ├── CitadelServer.csproj
│   │   ├── Program.cs           # Entry point, config loading, --install
│   │   ├── AppFactory.cs        # WebApplication builder (used by tests)
│   │   ├── DeployHandler.cs     # POST /deploy handler
│   │   └── ServerConfig.cs      # Config record + loader
│   └── CitadelServer.Tests/     # xUnit integration tests
│       ├── CitadelServer.Tests.csproj
│       └── DeployHandlerTests.cs
├── deploy.sh                    # Bash client template
├── deploy.bat                   # Batch client template
├── test_local_deployment.py     # Manual local test script
├── setup.sh                     # Development environment setup script
├── config.txt.example           # Server configuration template
├── .env.example                 # Client authentication template
├── .gitignore                   # Git ignore rules
├── README.md                    # Full documentation
└── QUICK_START.md               # Quick setup guide
```

## Testing

The project includes xUnit integration tests for the .NET deployment server. Tests start real server instances on dynamic ports — no mocking.

### Setup

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

### Run Tests

```bash
# Run all tests
dotnet test server/CitadelServer.sln

# With verbose output
dotnet test server/CitadelServer.sln --logger "console;verbosity=normal"
```

Tests cover:
- Authentication (no token, wrong token, malformed header)
- Unknown endpoint → 404
- Invalid zip → 400
- Zip slip path traversal → 400
- Absolute path in zip → 400
- Valid flat zip deployment
- Single root directory unwrapping
- Deployment replacing existing files
- Deployment without service header (no restart)

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
