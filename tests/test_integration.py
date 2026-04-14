"""
Live-server integration tests for Citadel Deployment System.

Spins up the real Handler from deploy-server.py in a background thread,
then fires actual HTTP requests — including malicious payloads — to verify
the server's behaviour end-to-end.

Run with:
    pytest tests/test_integration.py -v
"""

import importlib.util
import io
import os
import shutil
import sys
import tempfile
import threading
import time
import unittest
import zipfile
from http.server import HTTPServer

import requests


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _load_server_module(config_path: str):
    """
    Load deploy-server.py as a module, injecting a minimal config.txt so
    the module-level startup code (which calls sys.exit on bad config) can
    complete cleanly.  Returns the loaded module object.
    """
    server_script = os.path.join(os.path.dirname(__file__), "..", "deploy-server.py")
    server_dir = os.path.dirname(os.path.abspath(server_script))
    real_config = os.path.join(server_dir, "config.txt")

    # We need a valid config.txt in the server's own directory before import
    # because deploy-server.py reads it at module level.
    created = False
    backed_up = False
    backup_path = real_config + ".bak"

    if os.path.exists(real_config):
        shutil.copy2(real_config, backup_path)
        backed_up = True
    else:
        created = True

    try:
        # Write our test config so the import completes without sys.exit
        shutil.copy2(config_path, real_config)

        spec = importlib.util.spec_from_file_location("deploy_server", server_script)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
    finally:
        if backed_up:
            shutil.move(backup_path, real_config)
        elif created:
            os.unlink(real_config)

    return module


def _make_zip(files: dict) -> bytes:
    """Return zip bytes from {name: content} dict."""
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        for name, content in files.items():
            if isinstance(name, zipfile.ZipInfo):
                zf.writestr(name, content)
            else:
                zf.writestr(name, content)
    return buf.getvalue()


def _make_zip_raw(entries: list) -> bytes:
    """Return zip bytes from [(ZipInfo_or_name, content), ...] list."""
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        for name, content in entries:
            zf.writestr(name, content)
    return buf.getvalue()


# ---------------------------------------------------------------------------
# Test class
# ---------------------------------------------------------------------------

class TestLiveServer(unittest.TestCase):
    """Integration tests against a real HTTPServer running in a thread."""

    TOKEN = "integ-test-token-citadel-xyz"
    PORT = 19092  # distinct from unit-test port (19090)

    @classmethod
    def setUpClass(cls):
        cls.temp_dir = tempfile.mkdtemp(prefix="citadel_integ_")
        cls.deploy_dir = os.path.join(cls.temp_dir, "deploy")
        os.makedirs(cls.deploy_dir)

        # Write a temp config file that the module import will consume
        cls.config_path = os.path.join(cls.temp_dir, "config.txt")
        with open(cls.config_path, "w") as f:
            f.write(f"token={cls.TOKEN}\nport={cls.PORT}\n")

        # Load the real server module, then override the module-level TOKEN
        cls.server_module = _load_server_module(cls.config_path)
        cls.server_module.TOKEN = cls.TOKEN

        # Start the server in a daemon thread
        cls.server = HTTPServer(("127.0.0.1", cls.PORT), cls.server_module.Handler)
        cls.thread = threading.Thread(target=cls.server.serve_forever)
        cls.thread.daemon = True
        cls.thread.start()
        time.sleep(0.05)  # give server a moment to bind

        cls.base_url = f"http://127.0.0.1:{cls.PORT}"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.thread.join(timeout=5)
        shutil.rmtree(cls.temp_dir, ignore_errors=True)

    def setUp(self):
        # Clear deploy dir before each test
        for item in os.listdir(self.deploy_dir):
            path = os.path.join(self.deploy_dir, item)
            shutil.rmtree(path) if os.path.isdir(path) else os.remove(path)

    def _deploy(self, zip_bytes, token=None, deploy_dir=None, service="test-svc"):
        """POST a zip to /deploy, return the Response."""
        headers = {
            "Authorization": f"Bearer {token or self.TOKEN}",
            "X-Service": service,
            "X-Deploy-Dir": deploy_dir or self.deploy_dir,
        }
        return requests.post(
            f"{self.base_url}/deploy",
            headers=headers,
            files={"file": ("app.zip", zip_bytes)},
            timeout=10,
        )

    # ------------------------------------------------------------------
    # Authentication
    # ------------------------------------------------------------------

    def test_no_token_returns_401(self):
        r = requests.post(
            f"{self.base_url}/deploy",
            files={"file": ("app.zip", b"PK\x05\x06" + b"\x00" * 18)},
            timeout=5,
        )
        self.assertEqual(r.status_code, 401)

    def test_wrong_token_returns_401(self):
        r = self._deploy(_make_zip({"f.txt": "x"}), token="wrong-token")
        self.assertEqual(r.status_code, 401)

    def test_malformed_auth_header_returns_401(self):
        """Header without 'Bearer ' prefix should be rejected."""
        r = requests.post(
            f"{self.base_url}/deploy",
            headers={
                "Authorization": self.TOKEN,  # missing "Bearer " prefix
                "X-Deploy-Dir": self.deploy_dir,
                "X-Service": "test-svc",
            },
            files={"file": ("app.zip", _make_zip({"f.txt": "x"}))},
            timeout=5,
        )
        self.assertEqual(r.status_code, 401)

    # ------------------------------------------------------------------
    # Invalid inputs
    # ------------------------------------------------------------------

    def test_invalid_zip_returns_400(self):
        r = self._deploy(b"this is not a zip file at all")
        self.assertEqual(r.status_code, 400)
        self.assertIn("zip", r.text.lower())

    def test_unknown_endpoint_returns_404(self):
        r = requests.post(
            f"{self.base_url}/admin",
            headers={"Authorization": f"Bearer {self.TOKEN}"},
            timeout=5,
        )
        self.assertEqual(r.status_code, 404)

    def test_get_method_not_allowed(self):
        """Server only handles POST; GET should be rejected (501 from stdlib)."""
        r = requests.get(f"{self.base_url}/deploy", timeout=5)
        self.assertNotEqual(r.status_code, 200)

    # ------------------------------------------------------------------
    # Zip slip attack
    # ------------------------------------------------------------------

    def test_zip_slip_path_traversal_rejected(self):
        """Zip entries that escape the temp dir must be rejected with 400."""
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as zf:
            # Craft a malicious entry that would walk up two directories
            info = zipfile.ZipInfo("../../evil.txt")
            zf.writestr(info, "I should not exist outside the temp dir")
        zip_bytes = buf.getvalue()

        r = self._deploy(zip_bytes)
        self.assertEqual(r.status_code, 400)
        self.assertIn("unsafe", r.text.lower())

        # Confirm the file did NOT escape to the temp_dir parent
        escaped = os.path.join(os.path.dirname(self.temp_dir), "evil.txt")
        self.assertFalse(os.path.exists(escaped), "zip slip escaped!")

    def test_absolute_path_in_zip_rejected(self):
        """Zip entries with absolute paths must also be rejected."""
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as zf:
            info = zipfile.ZipInfo("/etc/passwd")
            zf.writestr(info, "fake passwd")
        zip_bytes = buf.getvalue()

        r = self._deploy(zip_bytes)
        self.assertEqual(r.status_code, 400)

    # ------------------------------------------------------------------
    # Valid deployments
    # ------------------------------------------------------------------

    def test_valid_flat_zip_deploys_files(self):
        """A flat zip (no wrapper dir) should deploy all files directly."""
        zip_bytes = _make_zip({
            "index.html": "<h1>Hello</h1>",
            "app.js": "console.log('hi');",
        })
        r = self._deploy(zip_bytes)
        self.assertEqual(r.status_code, 200)
        self.assertEqual(r.text, "deployed")

        self.assertTrue(os.path.exists(os.path.join(self.deploy_dir, "index.html")))
        self.assertTrue(os.path.exists(os.path.join(self.deploy_dir, "app.js")))

    def test_nested_zip_unwraps_single_root_dir(self):
        """
        A zip with a single top-level directory should deploy the *contents*
        of that directory, not the directory itself.
        """
        zip_bytes = _make_zip({
            "myapp/index.html": "<h1>Nested</h1>",
            "myapp/src/main.js": "const x = 1;",
        })
        r = self._deploy(zip_bytes)
        self.assertEqual(r.status_code, 200)

        # Files should land at deploy_dir/index.html, not deploy_dir/myapp/index.html
        self.assertTrue(os.path.exists(os.path.join(self.deploy_dir, "index.html")))
        self.assertTrue(os.path.exists(os.path.join(self.deploy_dir, "src", "main.js")))
        self.assertFalse(os.path.exists(os.path.join(self.deploy_dir, "myapp")))

    def test_deploy_replaces_existing_files(self):
        """A second deployment should fully replace the previous one."""
        # First deployment
        self._deploy(_make_zip({"old.txt": "old content"}))
        self.assertTrue(os.path.exists(os.path.join(self.deploy_dir, "old.txt")))

        # Second deployment
        r = self._deploy(_make_zip({"new.txt": "new content"}))
        self.assertEqual(r.status_code, 200)

        self.assertFalse(os.path.exists(os.path.join(self.deploy_dir, "old.txt")))
        self.assertTrue(os.path.exists(os.path.join(self.deploy_dir, "new.txt")))

    def test_multipart_form_data_upload(self):
        """Verify the server correctly extracts the zip from multipart form data."""
        zip_bytes = _make_zip({"hello.txt": "world"})
        r = self._deploy(zip_bytes)
        self.assertEqual(r.status_code, 200)
        self.assertTrue(os.path.exists(os.path.join(self.deploy_dir, "hello.txt")))


if __name__ == "__main__":
    unittest.main(verbosity=2)
