"""Exercise real dotnet-run launch profiles and browser-style asset requests.

Run after building Web and AdminConsole and creating a dotnet HTTPS dev certificate.
No database is needed: only anonymous login/static endpoints are exercised.
"""

import gzip
import os
from pathlib import Path
import shutil
import signal
import socket
import ssl
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
DOTNET = os.environ.get("DOTNET_BIN", "dotnet")


def check_host(host, assets, password_hash):
    # Avoid collisions with an already-running demo. Closing the socket leaves a
    # small bind race; a collision fails with the host's startup log below.
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    address = f"https://localhost:{port}"
    env = os.environ.copy()
    for name in ("ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "ASPNETCORE_URLS"):
        env.pop(name, None)
    env.update({
        "EVENT_STORE_PROVIDER": "Postgres",
        "EVENT_STORE_CONNECTION_STRING": "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1",
        "READ_MODEL_CONNECTION_STRING": "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1",
        "API_BASE_URL": "http://127.0.0.1:1",
        "FORWARDED_IDENTITY_SIGNING_SECRET": "asset-smoke-test-only-forwarded-identity-secret",
        "BootstrapAdministrator__AdministratorUserId": "11111111-1111-1111-1111-111111111111",
        "OperatorAuthentication__PasswordHash": password_hash,
    })
    # Certificate trust is bypassed only in this loopback test client. The hosts
    # still serve HTTPS and keep their secure-cookie policies.
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    client = urllib.request.build_opener(
        urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))
    with tempfile.TemporaryFile(mode="w+", encoding="utf-8") as log:
        process = subprocess.Popen(
            [DOTNET, "run", "--no-build", "--project", f"src/Hosts/{host}",
             "--", "--urls", address],
            cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT,
            start_new_session=os.name != "nt")
        try:
            deadline = time.monotonic() + 60
            while True:
                if process.poll() is not None:
                    raise AssertionError(f"{host} exited before listening")
                try:
                    with client.open(address + "/login", timeout=2) as response:
                        assert response.status == 200
                    break
                except (urllib.error.URLError, TimeoutError):
                    if time.monotonic() >= deadline:
                        raise AssertionError(f"{host} did not start within 60 seconds")
                    time.sleep(0.2)
            for path, media_type in assets:
                bodies = []
                for encoding in ("identity", "gzip"):
                    request = urllib.request.Request(
                        address + path, headers={"Accept-Encoding": encoding})
                    with client.open(request, timeout=10) as response:
                        assert response.status == 200, (host, path, response.status)
                        body = response.read()
                        if response.headers.get("Content-Encoding") == "gzip":
                            body = gzip.decompress(body)
                        assert len(body) > 100, f"{host} {path} {encoding}: empty or truncated asset"
                        assert response.headers.get_content_type() == media_type, (host, path, response.headers.get("Content-Type"))
                        bodies.append(body)
                assert bodies[0] == bodies[1], f"{host} {path}: gzip response differs"
                print(f"PASS {host} {path}: identity and gzip requests ({len(bodies[0])} bytes)", flush=True)
        except Exception:
            log.seek(0)
            print(log.read(), flush=True)
            raise
        finally:
            # dotnet run owns a child host process; stop the entire process tree.
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                               capture_output=True, check=False)
            else:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            process.wait(timeout=10)


def main():
    if not shutil.which(DOTNET):
        raise SystemExit("Install the .NET 10 SDK or set DOTNET_BIN to its executable.")
    result = subprocess.run(
        [DOTNET, "run", "--no-build", "--no-launch-profile", "--project", "src/Hosts/Web",
         "--", "--hash-operator-password"], cwd=ROOT, input="asset-test-" + os.urandom(24).hex() + "\n",
        text=True, capture_output=True, check=True)
    password_hash = result.stdout.strip()
    check_host("Web", [("/workspace.css", "text/css"), ("/operations.css", "text/css"),
                       ("/order-story.css", "text/css"), ("/_framework/blazor.web.js", "text/javascript")], password_hash)
    # Admin's Blazor runtime is behind its role gate. Its login stylesheet is
    # public and exercises the same build/compressed-asset mapping without a DB.
    check_host("AdminConsole", [("/console.css", "text/css")], password_hash)


if __name__ == "__main__":
    main()
