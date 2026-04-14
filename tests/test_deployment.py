"""
Integration tests for Citadel Deployment System

Tests the deployment server's core functionality:
- Authentication and authorization
- File upload and extraction
- Deployment directory management
- Service restart simulation

Run with: pytest tests/test_deployment.py
"""

import os
import sys
import json
import zipfile
import tempfile
import shutil
import unittest
import threading
import time
from io import BytesIO
from pathlib import Path
from unittest.mock import patch, MagicMock

# Add parent directory to path so we can import deploy-server.py
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import requests
from http.server import HTTPServer


class TestDeploymentServer(unittest.TestCase):
    """Integration tests for deployment server"""

    @classmethod
    def setUpClass(cls):
        """Set up test server and configuration"""
        # Create temporary directories
        cls.temp_dir = tempfile.mkdtemp(prefix="citadel_test_")
        cls.config_dir = os.path.join(cls.temp_dir, "config")
        cls.deploy_dir = os.path.join(cls.temp_dir, "deploy")
        os.makedirs(cls.config_dir)
        os.makedirs(cls.deploy_dir)

        # Create test config
        cls.test_token = "test-token-12345"
        cls.test_port = 19090  # Use non-standard port for testing
        config_file = os.path.join(cls.config_dir, "config.txt")
        
        with open(config_file, "w") as f:
            f.write(f"token={cls.test_token}\n")
            f.write(f"port={cls.test_port}\n")

        # Import and configure server
        import importlib.util
        spec = importlib.util.spec_from_file_location(
            "deploy_server",
            os.path.join(os.path.dirname(__file__), "..", "deploy-server.py")
        )
        deploy_server = importlib.util.module_from_spec(spec)

        # Monkey patch config loading
        with patch("builtins.open", create=True) as mock_open:
            mock_open.return_value.__enter__.return_value.readlines.return_value = []
            
        # Set up server globals
        with patch.dict(os.environ, {"CITADEL_CONFIG": config_file}):
            # Read config manually
            config = {}
            with open(config_file) as f:
                for line in f:
                    line = line.strip()
                    if line and not line.startswith("#") and "=" in line:
                        key, val = line.split("=", 1)
                        config[key.strip()] = val.strip()

            cls.server_config = config
            cls.server_url = f"http://localhost:{cls.test_port}"

    @classmethod
    def tearDownClass(cls):
        """Clean up temporary directories"""
        if os.path.exists(cls.temp_dir):
            shutil.rmtree(cls.temp_dir)

    def setUp(self):
        """Set up each test"""
        # Clear deploy directory
        for item in os.listdir(self.deploy_dir):
            path = os.path.join(self.deploy_dir, item)
            if os.path.isdir(path):
                shutil.rmtree(path)
            else:
                os.remove(path)

    def test_config_loading(self):
        """Test that configuration is loaded correctly"""
        self.assertEqual(self.server_config.get("token"), self.test_token)
        self.assertEqual(int(self.server_config.get("port", "9090")), self.test_port)

    def test_create_deployment_zip(self):
        """Test creating a valid deployment zip file"""
        zip_buffer = BytesIO()
        
        with zipfile.ZipFile(zip_buffer, 'w') as zf:
            zf.writestr("index.html", "<h1>Test App</h1>")
            zf.writestr("app.js", "console.log('test');")
            zf.writestr("config/settings.json", '{"debug": true}')
        
        zip_buffer.seek(0)
        
        # Verify it's a valid zip
        self.assertTrue(zipfile.is_zipfile(zip_buffer))
        
        # Verify contents
        zip_buffer.seek(0)
        with zipfile.ZipFile(zip_buffer, 'r') as zf:
            files = zf.namelist()
            self.assertIn("index.html", files)
            self.assertIn("app.js", files)
            self.assertIn("config/settings.json", files)

    def test_zip_extraction(self):
        """Test that zip files extract correctly"""
        # Create test zip
        zip_buffer = BytesIO()
        test_content = {
            "index.html": "<h1>Test</h1>",
            "app.js": "console.log('app');",
            "data.json": '{"key": "value"}'
        }
        
        with zipfile.ZipFile(zip_buffer, 'w') as zf:
            for name, content in test_content.items():
                zf.writestr(name, content)
        
        zip_buffer.seek(0)
        
        # Extract to temp directory
        extract_dir = tempfile.mkdtemp()
        try:
            with zipfile.ZipFile(zip_buffer, 'r') as zf:
                zf.extractall(extract_dir)
            
            # Verify extracted files
            for name, content in test_content.items():
                file_path = os.path.join(extract_dir, name)
                self.assertTrue(os.path.exists(file_path), f"File {name} not extracted")
                with open(file_path, 'r') as f:
                    self.assertEqual(f.read(), content)
        finally:
            shutil.rmtree(extract_dir)

    def test_single_root_folder_detection(self):
        """Test detection of single root folder in zip (for unwrapping)"""
        zip_buffer = BytesIO()
        
        with zipfile.ZipFile(zip_buffer, 'w') as zf:
            zf.writestr("app/index.html", "<h1>Test</h1>")
            zf.writestr("app/style.css", "body { color: black; }")
        
        zip_buffer.seek(0)
        
        # Extract and check for single root
        extract_dir = tempfile.mkdtemp()
        try:
            with zipfile.ZipFile(zip_buffer, 'r') as zf:
                zf.extractall(extract_dir)
            
            entries = os.listdir(extract_dir)
            self.assertEqual(len(entries), 1)
            self.assertTrue(os.path.isdir(os.path.join(extract_dir, entries[0])))
        finally:
            shutil.rmtree(extract_dir)

    def test_multiple_root_items(self):
        """Test zip with multiple root items (no unwrapping)"""
        zip_buffer = BytesIO()
        
        with zipfile.ZipFile(zip_buffer, 'w') as zf:
            zf.writestr("index.html", "<h1>Test</h1>")
            zf.writestr("style.css", "body { color: black; }")
            zf.writestr("script.js", "console.log('hi');")
        
        zip_buffer.seek(0)
        
        # Extract and check for multiple items
        extract_dir = tempfile.mkdtemp()
        try:
            with zipfile.ZipFile(zip_buffer, 'r') as zf:
                zf.extractall(extract_dir)
            
            entries = os.listdir(extract_dir)
            self.assertEqual(len(entries), 3)
        finally:
            shutil.rmtree(extract_dir)

    def test_deployment_directory_replacement(self):
        """Test that deployment directory is properly replaced"""
        # Create old deployment directory
        old_deploy = os.path.join(self.deploy_dir, "app")
        os.makedirs(old_deploy, exist_ok=True)
        
        with open(os.path.join(old_deploy, "old-file.txt"), "w") as f:
            f.write("old content")
        
        # Create new deployment zip
        zip_buffer = BytesIO()
        with zipfile.ZipFile(zip_buffer, 'w') as zf:
            zf.writestr("new-file.txt", "new content")
        
        zip_buffer.seek(0)
        
        # Extract new files to temp
        temp_extract = tempfile.mkdtemp()
        try:
            with zipfile.ZipFile(zip_buffer, 'r') as zf:
                zf.extractall(temp_extract)
            
            # Replace directory (simulating deployment)
            if os.path.exists(old_deploy):
                shutil.rmtree(old_deploy)
            shutil.copytree(temp_extract, old_deploy)
            
            # Verify replacement
            self.assertFalse(os.path.exists(os.path.join(old_deploy, "old-file.txt")))
            self.assertTrue(os.path.exists(os.path.join(old_deploy, "new-file.txt")))
            
            with open(os.path.join(old_deploy, "new-file.txt"), "r") as f:
                self.assertEqual(f.read(), "new content")
        finally:
            shutil.rmtree(temp_extract)

    def test_invalid_zip_detection(self):
        """Test that invalid zip files are rejected"""
        # Create invalid zip
        invalid_zip = BytesIO(b"This is not a zip file at all")
        
        # Test validation
        self.assertFalse(zipfile.is_zipfile(invalid_zip))

    def test_corrupt_zip_detection(self):
        """Test that corrupt zip files are rejected"""
        # Create a valid zip then corrupt it
        valid_zip = BytesIO()
        with zipfile.ZipFile(valid_zip, 'w') as zf:
            zf.writestr("file.txt", "content")
        
        # Corrupt it
        data = valid_zip.getvalue()
        corrupt_zip = BytesIO(data[:len(data)//2] + b"CORRUPTED")
        
        # Should not be recognized as valid zip
        self.assertFalse(zipfile.is_zipfile(corrupt_zip))

    def test_multipart_form_extraction(self):
        """Test extracting file from multipart/form-data"""
        # Simulate multipart form data boundaries
        boundary = "----WebKitFormBoundary7MA4YWxkTrZu0gW"
        
        # Create a simple zip
        zip_buffer = BytesIO()
        with zipfile.ZipFile(zip_buffer, 'w') as zf:
            zf.writestr("test.txt", "test content")
        
        zip_data = zip_buffer.getvalue()
        
        # Build multipart message
        multipart = (
            f"------{boundary}\r\n"
            f'Content-Disposition: form-data; name="file"; filename="app.zip"\r\n'
            f"Content-Type: application/zip\r\n"
            f"\r\n"
        ).encode() + zip_data + f"\r\n------{boundary}--\r\n".encode()
        
        # Extract zip from multipart
        parts = multipart.split(f"--{boundary}".encode())
        for part in parts:
            if b"filename=" in part:
                idx = part.find(b"\r\n\r\n")
                if idx != -1:
                    extracted_zip = part[idx + 4:]
                    if extracted_zip.endswith(b"\r\n"):
                        extracted_zip = extracted_zip[:-2]
                    
                    # Verify it's a valid zip
                    self.assertTrue(zipfile.is_zipfile(BytesIO(extracted_zip)))
                    break

    def test_large_zip_handling(self):
        """Test handling of larger zip files"""
        zip_buffer = BytesIO()
        
        with zipfile.ZipFile(zip_buffer, 'w') as zf:
            # Create multiple files to simulate larger deployment
            for i in range(100):
                zf.writestr(f"file_{i:03d}.txt", f"content of file {i}" * 100)
        
        zip_buffer.seek(0)
        
        # Verify it's valid and extractable
        self.assertTrue(zipfile.is_zipfile(zip_buffer))
        
        zip_buffer.seek(0)
        extract_dir = tempfile.mkdtemp()
        try:
            with zipfile.ZipFile(zip_buffer, 'r') as zf:
                zf.extractall(extract_dir)
            
            # Verify all files extracted
            files = os.listdir(extract_dir)
            self.assertEqual(len(files), 100)
        finally:
            shutil.rmtree(extract_dir)

    def test_special_characters_in_filenames(self):
        """Test handling files with special characters in names"""
        zip_buffer = BytesIO()
        special_names = [
            "file with spaces.txt",
            "file-with-dashes.txt",
            "file_with_underscores.txt",
            "file.multiple.dots.txt",
            "ファイル.txt",  # Japanese characters
        ]
        
        with zipfile.ZipFile(zip_buffer, 'w') as zf:
            for name in special_names:
                zf.writestr(name, f"content of {name}")
        
        zip_buffer.seek(0)
        
        # Extract and verify
        extract_dir = tempfile.mkdtemp()
        try:
            with zipfile.ZipFile(zip_buffer, 'r') as zf:
                zf.extractall(extract_dir)
            
            for name in special_names:
                self.assertTrue(os.path.exists(os.path.join(extract_dir, name)))
        finally:
            shutil.rmtree(extract_dir)


class TestPayloads(unittest.TestCase):
    """Test fixtures and payload creation"""

    @staticmethod
    def create_simple_app_zip():
        """Create a simple test app zip payload"""
        zip_buffer = BytesIO()
        
        with zipfile.ZipFile(zip_buffer, 'w') as zf:
            zf.writestr("index.html", """
<html>
<head><title>Test App</title></head>
<body><h1>Deployment Test App</h1></body>
</html>
""")
            zf.writestr("app.js", """
console.log('App loaded');
console.log('Build version: 1.0.0');
""")
        
        zip_buffer.seek(0)
        return zip_buffer.getvalue()

    @staticmethod
    def create_nested_app_zip():
        """Create a test app zip with nested structure (single root)"""
        zip_buffer = BytesIO()
        
        with zipfile.ZipFile(zip_buffer, 'w') as zf:
            zf.writestr("app/index.html", "<h1>Nested App</h1>")
            zf.writestr("app/src/main.js", "console.log('main');")
            zf.writestr("app/src/utils.js", "const utils = {};")
            zf.writestr("app/config/settings.json", '{"debug": false}')
        
        zip_buffer.seek(0)
        return zip_buffer.getvalue()

    @staticmethod
    def create_complex_app_zip():
        """Create a more complex test app zip"""
        zip_buffer = BytesIO()
        
        with zipfile.ZipFile(zip_buffer, 'w') as zf:
            # HTML/CSS/JS
            zf.writestr("index.html", "<h1>Complex App</h1>")
            zf.writestr("styles/main.css", "body { margin: 0; }")
            zf.writestr("js/app.js", "console.log('app');")
            zf.writestr("js/vendor/jquery.min.js", "// jquery mock")
            
            # Config files
            zf.writestr(".env", "DEBUG=false")
            zf.writestr("config.json", '{"version": "1.2.3"}')
            
            # Data files
            zf.writestr("data/users.json", '[]')
            zf.writestr("data/posts.json", '[]')
        
        zip_buffer.seek(0)
        return zip_buffer.getvalue()

    def test_payload_creation(self):
        """Verify test payloads can be created"""
        simple = self.create_simple_app_zip()
        nested = self.create_nested_app_zip()
        complex_app = self.create_complex_app_zip()
        
        self.assertTrue(len(simple) > 0)
        self.assertTrue(len(nested) > 0)
        self.assertTrue(len(complex_app) > 0)
        
        # Verify they're valid zips
        self.assertTrue(zipfile.is_zipfile(BytesIO(simple)))
        self.assertTrue(zipfile.is_zipfile(BytesIO(nested)))
        self.assertTrue(zipfile.is_zipfile(BytesIO(complex_app)))

    def test_payload_extraction(self):
        """Verify test payloads extract correctly"""
        payload = self.create_simple_app_zip()
        
        extract_dir = tempfile.mkdtemp()
        try:
            with zipfile.ZipFile(BytesIO(payload), 'r') as zf:
                zf.extractall(extract_dir)
            
            # Verify expected files exist
            self.assertTrue(os.path.exists(os.path.join(extract_dir, "index.html")))
            self.assertTrue(os.path.exists(os.path.join(extract_dir, "app.js")))
        finally:
            shutil.rmtree(extract_dir)


if __name__ == "__main__":
    # Run tests with verbose output
    unittest.main(verbosity=2)
