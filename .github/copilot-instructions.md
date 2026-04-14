# Citadel Deployment — Copilot Instructions

## Architecture

Three-component system for deploying applications to a Linux server via HTTP:

```
Client (deploy.sh / deploy.bat)
  → POST /deploy  (zip payload + Bearer token + X-Service + X-Deploy-Dir headers)
Deployment Server (deploy-server.py, port 9090)
  → stop systemd service → replace deploy dir → start systemd service
```

- **`deploy-server.py`** — Python `http.server`-based server. Reads `config.txt` from its own directory at startup (not environment variables). Exits with error if `config.txt` is missing or has no token.
- **`deploy.sh` / `deploy.bat`** — Client templates. Must be customized with `DEPLOY_URL`, `SERVICE`, and `DEPLOY_DIR`. Read `AUTH_TOKEN` from a sibling `.env` file.
- **`tests/`** — Pytest integration tests. Use `unittest.TestCase` classes (not bare pytest functions). The server is not actually started; systemd calls are silently swallowed.

## Key Conventions

### Configuration split
- **Server** config lives in `config.txt` (INI-like `key=value`, `#` comments). Only `token` and `port` are supported.
- **Client** credentials and all deployment parameters live in `.env` (sourced by the shell scripts). Required keys: `AUTH_TOKEN`, `DEPLOY_URL`, `SERVICE`, `DEPLOY_DIR`. Optional: `SOURCE` (default source path when no CLI arg is given).
- Neither file is committed; see `config.txt.example` and `.env.example` for templates.
- Both `deploy.sh` and `deploy.bat` are **stateless templates** — no hardcoded values. Distribute the script + a filled-in `.env` file.

### Deployment headers
Service name and deploy directory are passed to the server as HTTP headers, not in the request body:
```
X-Service: <systemd service name>
X-Deploy-Dir: <absolute path on server>
```
Defaults are `nginx` and `/var/www` if headers are absent.

### Security notes
- **Zip slip**: the server validates all zip member paths before extraction; entries that escape the temp dir get a 400 response.
- **Token comparison**: uses `hmac.compare_digest` (constant-time) to prevent timing attacks.


If the uploaded zip contains exactly one top-level directory, the server deploys that directory's *contents* (not the directory itself) to `X-Deploy-Dir`. Flat zips are deployed as-is.

### Multipart handling
`deploy.sh` sends the zip via `curl -F` (multipart/form-data). The server manually splits on the boundary to extract the raw zip bytes — there is no multipart library dependency.

### Server module import in tests
`deploy-server.py` contains a hyphen, so tests load it with `importlib.util.spec_from_file_location` rather than a normal import.

## Testing

```bash
# First-time setup
python3 -m venv .venv
source .venv/bin/activate          # Windows: .venv\Scripts\activate
pip install -r requirements-test.txt

# Run full suite
pytest tests/ -v

# Run a single test
pytest tests/test_deployment.py::TestDeploymentServer::test_zip_extraction -v

# With coverage
pytest tests/ --cov=. --cov-report=html
```

Tests use port **19090** (unit) and **19092** (integration) to avoid conflicts with a running server on 9090. Systemd subprocess calls (`systemctl`) are not mocked — they are wrapped in `try/except` in the server and silently ignored when unavailable.

### Integration tests (`tests/test_integration.py`)
Starts the real `Handler` in an `HTTPServer` thread. Covers auth bypass attempts, zip slip attacks, invalid payloads, valid deployments, and nested-zip unwrapping. The server module is loaded via `importlib.util.spec_from_file_location` and its `TOKEN` global is patched to the test token after import.

### Manual smoke test
```bash
# Terminal 1
python3 deploy-server.py

# Terminal 2 (venv activated)
python3 test_local_deployment.py
```
`test_local_deployment.py` sends real HTTP requests and validates auth + deployment round-trip.
