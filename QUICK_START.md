# Citadel Deployment - Quick Setup Guide

## 5-Minute Server Setup

### Step 1: Install the deploy server

```bash
curl -fsSL https://github.com/mssbabou/Citadel-Deployment/releases/latest/download/install-server.sh | sudo bash
```

This will:
- Download the latest `deploy-server` binary to `/opt/citadel/`
- Create a systemd service and enable it on boot
- Write a default `config.txt` and tell you to set your token

Safe to re-run — running it again updates to the latest version without touching your config.

### Step 2: Set your token

```bash
# Generate a strong token
TOKEN=$(openssl rand -base64 32)
sudo nano /opt/citadel/config.txt
# Set: token=<your generated token>

sudo systemctl start deploy-server.service
```

View logs:
```bash
journalctl -u deploy-server.service -f
```

### Step 3: Create your app's systemd service

```bash
sudo nano /etc/systemd/system/app.service
```

Paste this template:
```ini
[Unit]
Description=My Application
After=network.target

[Service]
Type=simple
User=www-data
WorkingDirectory=/opt/app
ExecStart=/opt/app/start.sh
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
```

Enable service:
```bash
sudo systemctl daemon-reload
sudo systemctl enable app.service
sudo systemctl start app.service
```

---

## Testing the Server

### Run Automated Tests

```bash
dotnet test server/CitadelServer.sln
```

### Test Connection
```bash
curl -s -o /dev/null -w "%{http_code}" -X POST -H "Authorization: Bearer wrong-token" https://deploy.example.com/deploy
# Should return: 401 (server is reachable and auth is working)
```

---

## Troubleshooting

### "Connection refused"
```bash
# Check if server is running
systemctl status deploy-server.service

# Check if port is listening
sudo netstat -tlnp | grep 9090

# Start if not running
sudo systemctl start deploy-server.service
```

### "unauthorized"
Check that client token matches the server config:
```bash
sudo cat /opt/citadel/config.txt
```

### Service not restarting after deploy
```bash
# Check service logs
journalctl -u app.service -n 50

# Try manual restart
sudo systemctl restart app.service
```

---

## Updating the Server

Re-run the installer — it fetches the latest binary and restarts the service, config is preserved:

```bash
curl -fsSL https://github.com/mssbabou/Citadel-Deployment/releases/latest/download/install-server.sh | sudo bash
```

---

## Production Checklist

- [ ] Changed default token to strong random value
- [ ] Service runs as least-privileged user (not root)
- [ ] Firewall blocks port 9090 from internet
- [ ] Using HTTPS (reverse proxy with nginx/Apache)
- [ ] Automated backups of deployment directory
- [ ] Log rotation configured
- [ ] Monitoring/alerting set up for service failures
- [ ] Token rotated regularly
- [ ] Tested rollback procedure
- [ ] config.txt file has restricted permissions (`sudo chmod 600 /opt/citadel/config.txt`)
