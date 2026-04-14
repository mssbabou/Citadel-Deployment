# Citadel Deployment

Deploy applications over HTTP from Linux or Windows to a Linux server running systemd services.

```
Client (deploy.sh / deploy.bat)
  → POST /deploy  (zip + Bearer token + X-Profile header)
.NET 10 server (/opt/citadel/deploy-server, port 9090)
  → look up profile → stop services → replace files → start services
```

## Server Setup

```bash
curl -fsSL https://github.com/mssbabou/Citadel-Deployment/releases/latest/download/install-server.sh | sudo bash
```

Downloads the binary to `/opt/citadel/`, writes a systemd service, and creates `/opt/citadel/config.toml` on first install. Safe to re-run for updates — config is preserved.

After install, edit the config:

```bash
sudo nano /opt/citadel/config.toml
sudo systemctl start deploy-server.service
journalctl -u deploy-server.service -f
```

### config.toml

```toml
[server]
token = "your-secret-token-here"
port = 9090

[profiles.myapp]
deploy_dir = "/opt/myapp"
services = ["myapp.service"]

[profiles.frontend]
deploy_dir = "/var/www/html"
services = ["nginx.service"]
```

Each profile maps a name to a deploy directory and a list of systemd services to stop before deploy and start after.

## Client Setup

```bash
# Linux/macOS
curl -LO https://github.com/mssbabou/Citadel-Deployment/releases/latest/download/deploy.sh
curl -LO https://github.com/mssbabou/Citadel-Deployment/releases/latest/download/.env.example
chmod +x deploy.sh && cp .env.example .env && nano .env
```

```powershell
# Windows
Invoke-WebRequest https://github.com/mssbabou/Citadel-Deployment/releases/latest/download/deploy.bat -OutFile deploy.bat
Invoke-WebRequest https://github.com/mssbabou/Citadel-Deployment/releases/latest/download/.env.example -OutFile .env.example
Copy-Item .env.example .env; notepad .env
```

`.env` fields: `AUTH_TOKEN`, `DEPLOY_URL`, `PROFILE`, `SOURCE` (optional).

Then run `./deploy.sh` or `deploy.bat`.

## Troubleshooting

| Problem | Fix |
|---|---|
| `unauthorized` | Token in `.env` doesn't match `config.toml` |
| `unknown profile` | Profile name in `.env` not defined in `config.toml` |
| Connection refused | `systemctl status deploy-server.service` / check port 9090 |
| Service won't start | `journalctl -u yourapp.service -n 50` / check `deploy_dir` permissions |

## Security Notes

- Use HTTPS via a reverse proxy (nginx) — the server speaks plain HTTP
- Firewall port 9090 from the internet, allow only deploy clients
- `chmod 600 /opt/citadel/config.toml` and client `.env` files
- Generate a strong token: `openssl rand -base64 32`

## Building

```bash
dotnet publish server/CitadelServer -r linux-x64 --self-contained -p:PublishSingleFile=true -c Release
# Output: server/CitadelServer/bin/Release/net10.0/linux-x64/publish/deploy-server
```

## Testing

```bash
dotnet test server/CitadelServer.sln
```
