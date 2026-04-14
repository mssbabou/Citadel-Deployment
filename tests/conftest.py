"""
Pytest configuration and shared fixtures for deployment tests
"""

import pytest
import tempfile
import shutil
import os
from io import BytesIO
import zipfile


@pytest.fixture
def temp_deployment_dir():
    """Provide a temporary deployment directory that's cleaned up after test"""
    temp_dir = tempfile.mkdtemp(prefix="citadel_deploy_")
    yield temp_dir
    
    # Cleanup
    if os.path.exists(temp_dir):
        shutil.rmtree(temp_dir)


@pytest.fixture
def simple_zip_payload():
    """Provide a simple zip file payload for testing"""
    zip_buffer = BytesIO()
    
    with zipfile.ZipFile(zip_buffer, 'w') as zf:
        zf.writestr("index.html", "<h1>Test</h1>")
        zf.writestr("app.js", "console.log('test');")
    
    zip_buffer.seek(0)
    return zip_buffer.getvalue()


@pytest.fixture
def nested_zip_payload():
    """Provide a nested zip file with single root"""
    zip_buffer = BytesIO()
    
    with zipfile.ZipFile(zip_buffer, 'w') as zf:
        zf.writestr("app/index.html", "<h1>App</h1>")
        zf.writestr("app/src/main.js", "console.log('app');")
    
    zip_buffer.seek(0)
    return zip_buffer.getvalue()


@pytest.fixture
def test_config(temp_deployment_dir):
    """Provide test configuration"""
    return {
        "token": "test-secret-token-12345",
        "port": 19090,
        "deploy_dir": temp_deployment_dir
    }


def pytest_configure(config):
    """Configure pytest"""
    config.addinivalue_line(
        "markers", 
        "integration: mark test as an integration test"
    )
    config.addinivalue_line(
        "markers",
        "unit: mark test as a unit test"
    )
