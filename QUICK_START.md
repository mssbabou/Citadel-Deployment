# Citadel Deployment - Quick Setup Guide

## 5-Minute Server Setup

### Step 1: Download Server Binary

```bash
curl -LO https://github.com/mssbabou/citadel-deployment/releases/latest/download/deploy-server
chmod +x deploy-server
```

### Step 2: Configure Token

```bash
# First run creates config.txt then exits
./deploy-server

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
./deploy-server

# Or install as service
sudo ./deploy-server --install
```

View logs:
```bash
journalctl -u deploy-server.service -f
```



---

## Testing the Server

### Run Automated Tests

```bash
dotnet test server/CitadelServer.sln
```

### Test Connection
```bash
curl -X GET -H "Authorization: Bearer your-token" https://deploy.example.com/deploy
# Should return: 405 Method Not Allowed (POST required)
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
