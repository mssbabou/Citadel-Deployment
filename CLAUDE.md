# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Branch note

This repository has two divergent branches:
- `master` — legacy Python server (`deploy-server.py`) with a `tests/` pytest suite.
- `dotnet-server` — current .NET 10 server under `server/`. This is the branch this file describes.

Before editing, confirm which branch is checked out; the server implementation and test commands differ completely.

## Architecture

Three-component deploy-over-HTTP system:

```
Client (deploy.sh / deploy.bat)
  → POST /deploy  (multipart zip + Bearer token + X-Service + X-Deploy-Dir headers)
.NET 10 server (server/CitadelServer, port 9090)
  → stop systemd service → replace deploy dir → start systemd service
```

Server-side code is tightly scoped across four files:

- `server/CitadelServer/Program.cs` — entry point. Loads `config.txt` from `AppContext.BaseDirectory`, validates token, starts the server. If `config.txt` is missing it prints an error pointing to `install-server.sh`.
- `server/CitadelServer/AppFactory.cs` — builds the `WebApplication` and registers `POST /deploy`. Extracted as a public factory so tests can boot a real server in-process.
- `server/CitadelServer/DeployHandler.cs` — the whole deploy pipeline: auth, multipart parsing, zip validation, zip-slip checks, single-root-dir unwrapping, systemctl stop/start, file replacement, cleanup.
- `server/CitadelServer/ServerConfig.cs` — key=value config loader (`#` comments, only `token` and `port` used).

Server setup is handled by `install-server.sh` in the repo root — it downloads the binary, writes the systemd unit, and writes `config.txt` on first install. Safe to re-run as an updater.

Config split: **server** reads `config.txt` next to the binary (at `/opt/citadel/config.txt` when installed). **Clients** read a sibling `.env` (`AUTH_TOKEN`, `DEPLOY_URL`, `DEPLOY_DIR`, optional `SERVICE` and `SOURCE`). Neither file is committed; see `config.txt.example` and `.env.example`. `deploy.sh` / `deploy.bat` are stateless templates — distribute alongside a filled-in `.env`.

## Key invariants (don't break these)

- **Auth is constant-time.** `DeployHandler.ConstantTimeEquals` pads both sides and uses `CryptographicOperations.FixedTimeEquals`, then verifies original lengths match. Preserve this shape when touching auth.
- **Zip-slip validation runs before extraction.** All entries are resolved against the realpath of the temp dir; any entry escaping it aborts the whole deploy with 400. Don't switch to streaming-extract without re-adding the check.
- **Single-root-dir unwrapping.** If the uploaded zip contains exactly one top-level directory, the server deploys that directory's *contents* to `X-Deploy-Dir`. Flat zips deploy as-is. `deploy.sh` produces a single-rooted zip via `(cd "$(dirname "$SOURCE")" && zip -r "$TEMP_ZIP" "$(basename "$SOURCE")")` — users expect their source directory's contents to land directly in `DEPLOY_DIR`.
- **Multipart parsing is hand-rolled.** `ExtractFromMultipart` splits on the boundary and grabs the first part with `filename=`. There is no multipart library dependency.
- **Headers, not body, carry deploy config.** `X-Service` (optional, default empty → no restart) and `X-Deploy-Dir` (default `/var/www`) come through HTTP headers. The zip body is only the payload.
- **Systemctl errors are silently swallowed.** `RunSystemctl` catches everything — tests run on machines without systemd or root. On any exception in the deploy pipeline, the server attempts `systemctl start <service>` as a best-effort recovery before responding 500.

## Commands

### Build and run the server

```bash
# Restore + build
dotnet build server/CitadelServer.sln

# Run from source (requires config.txt in the build output dir)
dotnet run --project server/CitadelServer

# Publish self-contained single-file binary for Linux
dotnet publish server/CitadelServer -r linux-x64 --self-contained -p:PublishSingleFile=true -c Release
# Output: server/CitadelServer/bin/Release/net10.0/linux-x64/publish/deploy-server
```

### Tests

```bash
# Full suite
dotnet test server/CitadelServer.sln

# Single test
dotnet test server/CitadelServer.sln --filter "FullyQualifiedName~ValidFlatZip_DeploysFiles"

# Verbose
dotnet test server/CitadelServer.sln --logger "console;verbosity=normal"
```

Tests (`server/CitadelServer.Tests/DeployHandlerTests.cs`) use `IAsyncLifetime` to start a real `WebApplication` on a free port per test instance via `AppFactory.Create`. No mocks — `systemctl` calls happen but fail silently. Each test gets its own temp deploy dir, cleaned up in `DisposeAsync`.

### Manual smoke test against a running server

```bash
# Terminal 1 — place a config.txt in the build output dir, then start the server
echo "token=test-token\nport=9090" > server/CitadelServer/bin/Debug/net10.0/config.txt
dotnet run --project server/CitadelServer

# Terminal 2 — send a real deploy
./deploy.sh ./some-source-dir
```
