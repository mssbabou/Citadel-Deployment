# Deployment Server Tests

Integration and unit tests for the Citadel Deployment System.

## Setup

### Create Virtual Environment

On systems with externally-managed Python (like Arch Linux):

```bash
# Create virtual environment
python3 -m venv .venv

# Activate it
source .venv/bin/activate  # Linux/macOS
# or
.venv\Scripts\activate     # Windows
```

Or use the setup script:

```bash
chmod +x setup.sh
./setup.sh
```

### Install Dependencies

```bash
pip install -r requirements-test.txt
```

## Running Tests

### Run all tests
```bash
pytest tests/ -v
```

### Run specific test file
```bash
pytest tests/test_deployment.py -v
```

### Run specific test class
```bash
pytest tests/test_deployment.py::TestDeploymentServer -v
```

### Run specific test
```bash
pytest tests/test_deployment.py::TestDeploymentServer::test_zip_extraction -v
```

### Run with coverage report
```bash
pytest tests/ --cov=. --cov-report=html
# Open htmlcov/index.html to view coverage
```

## Test Structure

### TestDeploymentServer (test_deployment.py)
Unit tests for server internals (no live server):
- Configuration loading
- Zip file creation and validation
- File extraction and handling
- Directory replacement (deployment workflow)
- Error handling (invalid/corrupt zips)
- Multipart form data parsing
- Large file handling
- Special character support

### TestPayloads (test_deployment.py)
Test fixture and sample payload creation:
- Simple app zip (basic HTML/JS)
- Nested app zip (single root folder)
- Complex app zip (realistic structure)

### TestLiveServer (test_integration.py)
**Integration tests** — spins up the real `Handler` in an `HTTPServer` thread on port 19092, then sends actual HTTP requests:
- Valid deployment (flat zip → files land in deploy dir)
- Nested zip unwrapping (single-root dir → contents deployed, wrapper removed)
- Auth bypass: no token, wrong token, malformed `Authorization` header → 401
- Invalid zip body → 400
- **Zip slip attack** (`../../evil.txt` entry) → 400
- Absolute path in zip (`/etc/passwd` entry) → 400
- Unknown endpoint → 404
- Replacement: second deploy fully replaces first

## Test Payloads

The `TestPayloads` class provides reusable test data:

```python
from tests.test_deployment import TestPayloads

# Create test zip payload
payload = TestPayloads.create_simple_app_zip()

# Use in deployment test
response = requests.post(
    "http://localhost:9090/deploy",
    headers={"Authorization": f"Bearer {token}"},
    files={"file": ("app.zip", payload)}
)
```

## CI/CD Integration

These tests are designed to run in GitHub Actions:

```yaml
- name: Run deployment tests
  run: |
    pip install -r requirements-test.txt
    pytest tests/ -v --cov
```

## Notes

- Tests use temporary directories that are cleaned up automatically
- No actual systemd services are required for testing
- Tests are isolated and can run in any order
- Port 19090 is used for unit test config fixtures; port 19092 is used by the live integration server
- The integration test loader temporarily swaps `config.txt` in the server directory during `setUpClass` to allow module-level startup code to run, then restores it
