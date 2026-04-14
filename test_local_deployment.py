#!/usr/bin/env python3
"""
Local deployment test script

This script manually tests the deployment server locally by:
1. Starting the deployment server
2. Creating and sending test payloads
3. Verifying successful deployment

Usage:
    python3 test_local_deployment.py
"""

import os
import sys
import time
import tempfile
import shutil
import subprocess
import zipfile
from io import BytesIO
from pathlib import Path

try:
    import requests
except ImportError:
    print("Error: requests module required. Install with: pip install requests")
    sys.exit(1)


def create_test_app_zip():
    """Create a minimal test app zip"""
    zip_buffer = BytesIO()
    
    with zipfile.ZipFile(zip_buffer, 'w') as zf:
        zf.writestr("index.html", """
<html>
<head>
    <title>Deployment Test</title>
</head>
<body>
    <h1>✓ Deployment Successful</h1>
    <p>This file was deployed via Citadel Deployment System</p>
    <p>Deployment time: %s</p>
</body>
</html>
""" % time.strftime("%Y-%m-%d %H:%M:%S"))
        
        zf.writestr("app.js", f"""
console.log('App initialized');
console.log('Deployed at: {time.strftime("%Y-%m-%d %H:%M:%S")}');
console.log('Version: 1.0.0');
""")
    
    zip_buffer.seek(0)
    return zip_buffer.getvalue()


def test_deployment(server_url, token):
    """Test deployment to the server"""
    print(f"\n📤 Testing deployment to {server_url}")
    
    # Create test payload
    print("  Creating test payload...")
    payload = create_test_app_zip()
    print(f"  Payload size: {len(payload) / 1024:.2f} KB")
    
    # Send deployment request
    print("  Sending deployment request...")
    try:
        files = {"file": ("test-app.zip", payload)}
        headers = {
            "Authorization": f"Bearer {token}",
            "X-Service": "test-app",
            "X-Deploy-Dir": "/tmp/citadel-test-deploy"
        }
        
        response = requests.post(
            f"{server_url}/deploy",
            files=files,
            headers=headers,
            timeout=10
        )
        
        print(f"  Response: HTTP {response.status_code}")
        print(f"  Message: {response.text}")
        
        if response.status_code == 200:
            print("  ✓ Deployment successful!")
            return True
        else:
            print(f"  ✗ Deployment failed!")
            return False
            
    except requests.exceptions.ConnectionError:
        print(f"  ✗ Connection refused. Is the server running on {server_url}?")
        return False
    except Exception as e:
        print(f"  ✗ Error: {e}")
        return False


def test_authentication(server_url, token):
    """Test authentication"""
    print(f"\n🔐 Testing authentication...")
    
    # Test with correct token
    print("  Testing with correct token...")
    try:
        headers = {"Authorization": f"Bearer {token}"}
        response = requests.post(
            f"{server_url}/deploy",
            headers=headers,
            data=b"test",
            timeout=5
        )
        
        # 400 (bad zip) is expected, not 401 (unauthorized)
        if response.status_code != 401:
            print(f"    ✓ Authentication passed (HTTP {response.status_code})")
            auth_works = True
        else:
            print(f"    ✗ Authentication failed (HTTP {response.status_code})")
            auth_works = False
    except Exception as e:
        print(f"    ✗ Error: {e}")
        auth_works = False
    
    # Test with wrong token
    print("  Testing with wrong token...")
    try:
        headers = {"Authorization": f"Bearer wrong-token"}
        response = requests.post(
            f"{server_url}/deploy",
            headers=headers,
            data=b"test",
            timeout=5
        )
        
        if response.status_code == 401:
            print(f"    ✓ Correctly rejected (HTTP 401)")
        else:
            print(f"    ✗ Should have been rejected (HTTP {response.status_code})")
            auth_works = False
    except Exception as e:
        print(f"    ✗ Error: {e}")
        auth_works = False
    
    return auth_works


def check_server_running(server_url):
    """Check if server is running"""
    try:
        response = requests.get(f"{server_url}/", timeout=2)
        return True
    except:
        return False


def main():
    # Configuration
    server_url = "http://localhost:9090"
    config_file = "config.txt"
    
    # Read token from config
    if not os.path.exists(config_file):
        print("Error: config.txt not found")
        print("Please run deploy-server.py to generate config.txt")
        sys.exit(1)
    
    token = None
    with open(config_file) as f:
        for line in f:
            if line.startswith("token="):
                token = line.split("=", 1)[1].strip()
                break
    
    if not token:
        print("Error: token not set in config.txt")
        sys.exit(1)
    
    print("=" * 50)
    print("Citadel Deployment - Local Test")
    print("=" * 50)
    print(f"Server URL: {server_url}")
    print(f"Token: {token[:10]}...{token[-10:]}")
    
    # Check if server is running
    print(f"\n⏳ Checking if server is running...")
    if not check_server_running(server_url):
        print(f"Error: Server not running on {server_url}")
        print(f"\nStart the server with:")
        print(f"  python3 deploy-server.py")
        sys.exit(1)
    
    print(f"✓ Server is running")
    
    # Run tests
    results = {}
    results["auth"] = test_authentication(server_url, token)
    results["deploy"] = test_deployment(server_url, token)
    
    # Summary
    print("\n" + "=" * 50)
    print("Test Summary")
    print("=" * 50)
    
    for test_name, passed in results.items():
        status = "✓ PASSED" if passed else "✗ FAILED"
        print(f"{test_name.capitalize():12} {status}")
    
    all_passed = all(results.values())
    print("=" * 50)
    
    if all_passed:
        print("✓ All tests passed!")
        return 0
    else:
        print("✗ Some tests failed")
        return 1


if __name__ == "__main__":
    sys.exit(main())
