#!/usr/bin/env python3
"""
Citadel Deployment Server
HTTP server that handles service deployment via systemd.
"""
import os
import sys
import hmac
import shutil
import tempfile
import zipfile
import subprocess
from http.server import HTTPServer, BaseHTTPRequestHandler

# Load config from config.txt
script_dir = os.path.dirname(os.path.abspath(__file__))
config_path = os.path.join(script_dir, "config.txt")

# Create default config if it doesn't exist
if not os.path.exists(config_path):
    with open(config_path, "w") as f:
        f.write("""# Configuration file for deploy-server.py
token=your-secret-token-here
port=9090
""")
    print(f"Created default config.txt at {config_path}")
    print("Please edit it with your token and run again.")
    sys.exit(1)

config = {}
with open(config_path) as f:
    for line in f:
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            key, val = line.split("=", 1)
            config[key.strip()] = val.strip()

TOKEN = config.get("token")
PORT = int(config.get("port", "9090"))

if not TOKEN:
    print("Error: token not set in config.txt")
    sys.exit(1)


def install_service():
    """Create and enable a systemd service for this deploy script."""
    script_path = os.path.abspath(__file__)
    script_dir = os.path.dirname(script_path)
    service_name = "deploy-server.service"
    service_path = f"/etc/systemd/system/{service_name}"
    
    service_content = f"""[Unit]
Description=Deploy Server
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory={script_dir}
ExecStart={script_path}
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
"""
    
    try:
        with open(service_path, "w") as f:
            f.write(service_content)
        subprocess.run(["systemctl", "daemon-reload"], check=True)
        subprocess.run(["systemctl", "enable", service_name], check=True)
        subprocess.run(["systemctl", "start", service_name], check=True)
        print(f"✓ Service installed and started: {service_name}")
        print(f"  View logs: journalctl -u {service_name} -f")
    except subprocess.CalledProcessError as e:
        print(f"✗ Failed to install service: {e}")
        sys.exit(1)
    except PermissionError:
        print("✗ Permission denied. Run with sudo: sudo python3 deploy.py --install")
        sys.exit(1)


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/deploy":
            self.respond(404, "not found")
            return

        if not hmac.compare_digest(self.headers.get("Authorization", ""), f"Bearer {TOKEN}"):
            self.respond(401, "unauthorized")
            return

        # Get config from headers (sent by deploy.sh)
        service = self.headers.get("X-Service", "")
        deploy_dir = self.headers.get("X-Deploy-Dir", "/var/www")

        try:
            # read body
            body = self.rfile.read(int(self.headers.get("Content-Length", 0)))

            # extract file from multipart if needed
            content_type = self.headers.get("Content-Type", "")
            if "multipart/form-data" in content_type:
                boundary = content_type.split("boundary=")[1].strip()
                parts = body.split(f"--{boundary}".encode())
                for part in parts:
                    if b"filename=" in part:
                        idx = part.find(b"\r\n\r\n")
                        if idx != -1:
                            body = part[idx + 4:]
                            if body.endswith(b"\r\n"):
                                body = body[:-2]
                            break

            # write to temp zip
            tmp = tempfile.NamedTemporaryFile(suffix=".zip", delete=False)
            tmp.write(body)
            tmp.close()

            if not zipfile.is_zipfile(tmp.name):
                os.unlink(tmp.name)
                self.respond(400, "not a valid zip")
                return

            # extract to temp dir (validate paths first to prevent zip slip)
            tmp_dir = tempfile.mkdtemp()
            real_tmp_dir = os.path.realpath(tmp_dir)
            unsafe_member = None
            with zipfile.ZipFile(tmp.name) as zf:
                for member in zf.namelist():
                    member_path = os.path.realpath(os.path.join(tmp_dir, member))
                    if not (member_path.startswith(real_tmp_dir + os.sep) or member_path == real_tmp_dir):
                        unsafe_member = member
                        break
                if unsafe_member is None:
                    zf.extractall(tmp_dir)
            os.unlink(tmp.name)  # safe to unlink — zip is now closed

            if unsafe_member is not None:
                shutil.rmtree(tmp_dir, ignore_errors=True)
                self.respond(400, "unsafe path in zip")
                return

            # if zip has single root folder, use its contents
            entries = os.listdir(tmp_dir)
            source = tmp_dir
            if len(entries) == 1 and os.path.isdir(os.path.join(tmp_dir, entries[0])):
                source = os.path.join(tmp_dir, entries[0])

            # stop service if specified (ignore errors if no permissions)
            if service:
                try:
                    subprocess.run(["systemctl", "stop", service], capture_output=True, timeout=15)
                except Exception:
                    pass  # Ignore failures (no systemd, no permissions, etc.)

            # replace files
            if os.path.exists(deploy_dir):
                shutil.rmtree(deploy_dir)
            shutil.copytree(source, deploy_dir)

            # start service if specified (ignore errors if no permissions)
            if service:
                try:
                    subprocess.run(["systemctl", "start", service], capture_output=True, timeout=15)
                except Exception:
                    pass  # Ignore failures (no systemd, no permissions, etc.)

            # cleanup
            shutil.rmtree(tmp_dir, ignore_errors=True)

            self.respond(200, "deployed")

        except Exception as e:
            # attempt to restart service on failure (ignore errors)
            if service:
                try:
                    subprocess.run(["systemctl", "start", service], capture_output=True, timeout=15)
                except Exception:
                    pass
            self.respond(500, str(e))

    def respond(self, code, msg):
        self.send_response(code)
        self.send_header("Content-Type", "text/plain")
        self.end_headers()
        self.wfile.write(msg.encode())

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    if "--install" in sys.argv:
        install_service()
    else:
        print(f"deploy server listening on :{PORT}")
        HTTPServer(("0.0.0.0", PORT), Handler).serve_forever()
