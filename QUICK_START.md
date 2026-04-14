# Citadel Deployment - Quick Setup Guide

## 5-Minute Server Setup

### Step 1: Download Server Script

```bash
curl -LO https://github.com/mssbabou/citadel-deployment/releases/latest/download/deploy-server.py
```

### Step 2: Configure Token

```bash
# First run creates config.txt then exits
python3 deploy-server.py

# Generate a strong token and set it
TOKEN=$(openssl rand -base64 32)
echo "token=$TOKEN" > config.txt
echo "port=9090" >> config.txt
chmod 600 config.txt
echo "Your token: $TOKEN"   # save this for your .env files
```

### Step 3: Create App Service
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

### Step 4: Start Deploy Server
```bash
# Run directly
python3 deploy-server.py

# Or install as service
sudo python3 deploy-server.py --install
```

View logs:
```bash
journalctl -u deploy-server.service -f
```



---

## Testing the Server

### Setup Environment

First, create a virtual environment (required on Arch Linux and other systems with externally-managed Python):

```bash
# Create virtual environment
python3 -m venv .venv

# Activate it
source .venv/bin/activate
```

Or use the setup script:

```bash
chmod +x setup.sh
./setup.sh
```

### Quick Manual Test

In a new terminal, run:

```bash
# Make sure venv is activated
source .venv/bin/activate

python3 test_local_deployment.py
```

This tests:
- Server connectivity
- Authentication (correct and incorrect tokens)
- Deployment with test payload

### Automated Tests

Run comprehensive test suite:

```bash
pip install -r requirements-test.txt
pytest tests/ -v
```

---

### View Server Logs
```bash
journalctl -u deploy-server.service -f           # Real-time
journalctl -u deploy-server.service -n 100       # Last 100 lines
```

### Check Service Status
```bash
systemctl status app.service
systemctl restart app.service
systemctl stop app.service
systemctl start app.service
```

### Generate Strong Token
```bash
# Linux
openssl rand -base64 32

# macOS
openssl rand -base64 32

# Windows PowerShell
[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Random -Maximum 1000000000).ToString()))
```

### Test Connection
```bash
curl -H "Authorization: Bearer your-token" https://deploy.example.com/deploy
# Should return: "405 Method Not Allowed" (POST required)
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
Check that client token matches `config.txt`:
```bash
cat config.txt | grep token
```

### Service not restarting
```bash
# Check service file syntax
sudo systemctl status app.service

# Check service logs
journalctl -u app.service -n 50

# Try manual restart
sudo systemctl restart app.service
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
- [ ] config.txt file has restricted permissions (chmod 600)
