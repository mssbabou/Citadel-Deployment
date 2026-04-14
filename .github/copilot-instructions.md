# Citadel Deployment — Copilot Instructions

## Architecture

Three-component deploy-over-HTTP system:

```
Client (deploy.sh / deploy.bat)
  → POST /deploy  (multipart zip + Bearer token + X-Profile header)
.NET 10 server (server/CitadelServer, port 9090)
  → look up profile → stop services → replace deploy dir → start services
```

- **`server/CitadelServer`** — ASP.NET Core HTTP server. Reads `config.toml` from `AppContext.BaseDirectory` at startup. Exits with error if `config.toml` is missing or token is not set.
- **`deploy.sh` / `deploy.bat`** — Client templates. Must be customized with `DEPLOY_URL` and `PROFILE`. Read `AUTH_TOKEN` from a sibling `.env` file.
- **`install-server.sh`** — Installer/updater. Downloads the binary, writes the systemd service, creates `config.toml` on first run. Safe to re-run for updates.

## Key Conventions

### Configuration split
- **Server** config lives in `config.toml` (TOML format). `[server]` section has `token` and `port`. `[profiles.*]` sections each have `deploy_dir` and `services[]`.
- **Client** credentials live in `.env` (sourced by shell scripts). Required: `AUTH_TOKEN`, `DEPLOY_URL`, `PROFILE`. Optional: `SOURCE` (default deploy source).
- Neither file is committed; see `config.toml.example` and `.env.example` for templates.
- Both `deploy.sh` and `deploy.bat` are **stateless templates** — no hardcoded values. Distribute alongside a filled-in `.env`.

### Deployment profiles
Instead of clients specifying arbitrary paths and services, the server holds named profiles in `config.toml`. The client sends `X-Profile: myapp` and the server looks up the profile to get:
- `deploy_dir` — absolute path where files are deployed
- `services[]` — one or more systemd services to stop before deploy and start after

This removes arbitrary path/service access from authenticated clients.

Example `config.toml`:
```toml
[server]
token = "abc123"
port = 9090

[profiles.myapp]
deploy_dir = "/opt/myapp"
services = ["myapp.service", "nginx.service"]

[profiles.frontend]
deploy_dir = "/var/www/html"
services = ["nginx.service"]
```

### Security notes
- **Zip slip**: the server validates all zip member paths before extraction; entries that escape the temp dir get a 400 response.
- **Token comparison**: uses `CryptographicOperations.FixedTimeEquals` (constant-time) to prevent timing attacks.
- **Profile isolation**: clients can only deploy to paths and control services that are defined in config profiles.

### Multipart handling
`deploy.sh` sends the zip via `curl -F` (multipart/form-data). The server manually splits on the boundary to extract the raw zip bytes — there is no multipart library dependency.

### Single-root-dir unwrapping
If the uploaded zip contains exactly one top-level directory, the server deploys that directory's *contents* (not the directory itself) to `deploy_dir`. Flat zips are deployed as-is. This matches how clients zip: `(cd "$(dirname "$SOURCE")" && zip -r "$TEMP_ZIP" "$(basename "$SOURCE")")`.

## Testing

### Unit and integration tests
```bash
# Full suite
dotnet test server/CitadelServer.sln

# Single test
dotnet test server/CitadelServer.sln --filter "FullyQualifiedName~ValidFlatZip_DeploysFiles"

# With detailed output
dotnet test server/CitadelServer.sln --logger "console;verbosity=normal"
```

Tests (`server/CitadelServer.Tests/DeployHandlerTests.cs`) use `IAsyncLifetime` to start a real `WebApplication` on a free port per test instance via `AppFactory.Create`. No mocks — `systemctl` calls happen but fail silently (tests run on machines without systemd). Each test gets its own temp deploy dir, cleaned up in `DisposeAsync`.

Test coverage includes:
- Authentication (no token, wrong token, malformed header)
- Profile lookup (missing X-Profile header, unknown profile)
- Unknown endpoint → 404
- Invalid zip → 400
- Zip slip path traversal → 400
- Absolute paths in zip → 400
- Valid flat zip deployment
- Single root directory unwrapping
- Deployment replacing existing files

### Manual smoke test
```bash
# Terminal 1 — prepare config and start server
mkdir -p /tmp/citadel-test
cat > server/CitadelServer/bin/Debug/net10.0/config.toml << 'EOF'
[server]
token = "test-token"
port = 9090

[profiles.test]
deploy_dir = "/tmp/citadel-test"
services = []
EOF
dotnet run --project server/CitadelServer

# Terminal 2 — create .env and deploy
cat > .env << 'EOF'
AUTH_TOKEN=test-token
DEPLOY_URL=http://localhost:9090/deploy
PROFILE=test
SOURCE=./test-source
EOF
mkdir test-source && echo "hello" > test-source/hello.txt
./deploy.sh
# Verify: ls /tmp/citadel-test/hello.txt
```

## Code organization

- `Program.cs` — Entry point. Loads `config.toml`, validates token, starts the ASP.NET Core app.
- `AppFactory.cs` — Builds the `WebApplication` and registers `POST /deploy`. Extracted as a public factory so tests can boot a real server in-process.
- `DeployHandler.cs` — The entire deploy pipeline: auth, profile lookup, multipart parsing, zip validation, zip-slip checks, single-root-dir unwrapping, systemctl stop/start (multiple services), file replacement, cleanup. Attempts service recovery on failure.
- `ServerConfig.cs` — TOML config loader using `Tomlyn`. Parses `[server]` and `[profiles.*]` sections.
