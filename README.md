# Citadel Deployment

Deploy applications over HTTP from Linux or Windows to a Linux server running systemd services.

```
Client (deploy.sh / deploy.ps1)
  → POST /deploy  (zip + HMAC-SHA256 signature + X-Profile header)
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
max_upload_mb = 500     # optional, default 500
keep_backups = 3        # optional, default 3
audit_log_path = ""     # optional, default audit.jsonl next to the binary

[profiles.myapp]
deploy_dir = "/opt/myapp"
services = ["myapp.service"]
# Optional: runs via /bin/sh -c with 60s timeout and deploy_dir as CWD
post_update_command = "npm install --omit=dev"

[profiles.frontend]
deploy_dir = "/var/www/html"
services = ["nginx.service"]
```

Each profile maps a name to a deploy directory and a list of systemd services to stop before deploy and start after. Optional `post_update_command` runs between file-replace and service-start.

## Client Setup

```bash
# Linux/macOS
curl -LO https://github.com/mssbabou/Citadel-Deployment/releases/latest/download/deploy.sh
curl -LO https://github.com/mssbabou/Citadel-Deployment/releases/latest/download/.env.example
chmod +x deploy.sh && cp .env.example .env && chmod 600 .env && nano .env
```

```powershell
# Windows
Invoke-WebRequest https://github.com/mssbabou/Citadel-Deployment/releases/latest/download/deploy.ps1 -OutFile deploy.ps1
Invoke-WebRequest https://github.com/mssbabou/Citadel-Deployment/releases/latest/download/.env.example -OutFile .env.example
Copy-Item .env.example .env; notepad .env
# Run:
powershell -ExecutionPolicy Bypass -File .\deploy.ps1
```

`.env` fields: `AUTH_TOKEN`, `DEPLOY_URL`, `PROFILE`, `SOURCE` (optional).

Then run `./deploy.sh` or `.\deploy.ps1`.

### Useful flags

```
./deploy.sh --list         # list profiles on the server
./deploy.sh --dry-run      # upload + validate, no side effects
./deploy.sh --help
./deploy.sh --version
```

## Endpoints

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `GET`  | `/health`   | none | liveness probe (JSON: status, version, uptime_s) |
| `GET`  | `/profiles` | HMAC | list configured profiles |
| `GET`  | `/deploys?profile=X` | HMAC | last 50 audit entries for a profile |
| `POST` | `/deploy`   | HMAC | deploy a zip (supports `X-DryRun: true`) |
| `POST` | `/rollback` | HMAC | restore a named backup (body: `{"profile":"X","backup":"previous"\|"<name>"}`) |

## Auth protocol (v2)

Every authenticated request sends three headers: `X-Protocol: v2`, `X-Timestamp: <unix-seconds>`, `X-Signature: <hex>`. The server rejects timestamps more than 300 seconds from its clock.

The signature is:

```
HMAC-SHA256(token, timestamp || "\n" || context || "\n" || body)
```

Hex-lowercase. `context` is the profile name for `/deploy`, or `"METHOD path"` for other endpoints (e.g. `GET /profiles`). `body` is the raw zip bytes for `/deploy`, the request body for `/rollback`, and empty for GETs.

Reproduce a signature offline (useful for debugging):

```bash
TOKEN=your-token
TS=$(date +%s)
PROFILE=myapp
printf '%s\n%s\n' "$TS" "$PROFILE" | cat - payload.zip \
  | openssl dgst -sha256 -hmac "$TOKEN" -binary | xxd -p -c 256
```

## Troubleshooting

| Problem | Fix |
|---|---|
| `unauthorized` / signature mismatch | Token in `.env` doesn't match `config.toml`, or client and server clocks differ by more than 5 min |
| `unsupported protocol version` | Upgrade the client — server requires protocol v2 |
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
