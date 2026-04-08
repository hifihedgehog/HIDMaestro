"""
HIDMaestro browser gamepad verification — invoked by scripts/verify.py.

Launches a real browser (Edge or Chrome) in --app= mode pointed at index.html.
The page polls navigator.getGamepads() across 30 samples and POSTs results
back to a local HTTP server. We capture the result, kill the browser, and
return a structured dict.

This is the only way to verify that the browser path actually works, because:
  - Chromium's gamepad source selection (XInput vs WGI vs RawInput vs HID
    backend) is internal and can't be inferred from a single OS API
  - Different controllers prefer different paths (Xbox -> XInput, DualSense
    -> WGI, generic HID -> RawInput)
  - The "is the controller present" question and the "does the browser see
    live data" question are independent

Usage from verify.py:
    result = run_browser_check(timeout_s=10)
    # result = {'available': True, 'pads': 1, 'live': 1, 'snapshot': [...]}
    # or {'available': False, 'error': '...'}
"""

import http.server
import json
import os
import shutil
import socket
import socketserver
import subprocess
import sys
import threading
import time
from pathlib import Path

PORT = 8765
RESULT_PATH = "/result"
DONE_TITLE = "HIDMAESTRO_BROWSER_CHECK_DONE"
INDEX_HTML = Path(__file__).parent / "index.html"


class _ResultHandler(http.server.BaseHTTPRequestHandler):
    """Tiny HTTP server that captures the JSON result POSTed by index.html."""
    server_version = "HIDMaestroVerify/1.0"

    def do_POST(self):  # noqa: N802 (stdlib API)
        if self.path != RESULT_PATH:
            self.send_error(404); return
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        try:
            self.server.result = json.loads(body.decode())  # type: ignore[attr-defined]
        except Exception as e:
            self.server.result = {"error": f"bad json: {e}"}  # type: ignore[attr-defined]
        # Response headers (CORS allows file:// origin)
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()

    def do_OPTIONS(self):  # noqa: N802
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Methods", "POST")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.end_headers()

    def log_message(self, format, *args):  # noqa: A002 (stdlib API)
        pass  # silence default request logging


def _find_browser() -> str | None:
    """Locate Edge or Chrome. Edge is preferred (always present on Win11)."""
    candidates = [
        os.path.expandvars(r"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
        os.path.expandvars(r"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"),
        os.path.expandvars(r"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
        os.path.expandvars(r"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"),
    ]
    for c in candidates:
        if os.path.isfile(c):
            return c
    # Fall back to PATH lookup
    for name in ("msedge", "chrome"):
        p = shutil.which(name)
        if p:
            return p
    return None


def _port_free(port: int) -> bool:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        try:
            s.bind(("127.0.0.1", port)); return True
        except OSError:
            return False


def run_browser_check(timeout_s: float = 12.0) -> dict:
    """Launch a browser, collect gamepad readings, return structured result."""
    if not INDEX_HTML.exists():
        return {"available": False, "error": f"index.html not found at {INDEX_HTML}"}

    browser = _find_browser()
    if not browser:
        return {"available": False, "error": "no browser found (Edge/Chrome)"}

    if not _port_free(PORT):
        return {"available": False, "error": f"port {PORT} in use (another verify.py running?)"}

    # Start HTTP server
    httpd = socketserver.TCPServer(("127.0.0.1", PORT), _ResultHandler)
    httpd.result = None  # type: ignore[attr-defined]
    httpd.timeout = 0.2
    server_thread = threading.Thread(target=httpd.serve_forever, daemon=True)
    server_thread.start()

    # Use a temp profile directory so we don't pollute the user's main profile
    import tempfile
    profile_dir = tempfile.mkdtemp(prefix="hidmaestro_browsercheck_")

    # --app= launches a frameless single-page window
    # --user-data-dir= isolates the temporary profile
    # --no-first-run skips welcome dialogs
    # --window-size keeps the window small
    cmd = [
        browser,
        f"--app=file:///{INDEX_HTML.as_posix().lstrip('/')}",
        f"--user-data-dir={profile_dir}",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-features=Translate,OptimizationHints",
        "--window-size=400,300",
    ]
    proc = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    # Wait for the page to POST a result, or timeout
    start = time.time()
    try:
        while time.time() - start < timeout_s:
            if httpd.result is not None:  # type: ignore[attr-defined]
                break
            time.sleep(0.1)
    finally:
        # Kill browser
        try:
            proc.terminate()
            proc.wait(timeout=3)
        except Exception:
            try: proc.kill()
            except Exception: pass

        httpd.shutdown()
        httpd.server_close()

        # Clean temp profile
        try:
            shutil.rmtree(profile_dir, ignore_errors=True)
        except Exception:
            pass

    result = httpd.result  # type: ignore[attr-defined]
    if result is None:
        return {"available": False, "error": "browser did not POST results within timeout",
                "browser": browser}

    return {
        "available": True,
        "browser": os.path.basename(browser),
        "pads": result.get("padCount", 0),
        "live": result.get("liveCount", 0),
        "snapshot": result.get("finalSnapshot", []),
        "error": result.get("error"),
    }


if __name__ == "__main__":
    # Standalone debug mode: print full result
    r = run_browser_check()
    print(json.dumps(r, indent=2))
    sys.exit(0 if r.get("live", 0) > 0 else 1)
