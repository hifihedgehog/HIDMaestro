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
INDEX_PATH = "/"
DONE_TITLE = "HIDMAESTRO_BROWSER_CHECK_DONE"
INDEX_HTML = Path(__file__).parent / "index.html"


class _Handler(http.server.BaseHTTPRequestHandler):
    """Serves index.html on GET / and captures result on POST /result.

    Serving the page from the same HTTP server (instead of file://) keeps
    everything same-origin, avoiding fetch() CORS issues that broke previous
    file:// + cross-origin attempts."""
    server_version = "HIDMaestroVerify/1.0"

    def do_GET(self):  # noqa: N802 (stdlib API)
        if self.path != INDEX_PATH and self.path != "/index.html":
            self.send_error(404); return
        try:
            with open(INDEX_HTML, "rb") as f:
                body = f.read()
        except OSError as e:
            self.send_error(500, str(e)); return
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):  # noqa: N802
        if self.path != RESULT_PATH:
            self.send_error(404); return
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        try:
            self.server.result = json.loads(body.decode())  # type: ignore[attr-defined]
        except Exception as e:
            self.server.result = {"error": f"bad json: {e}"}  # type: ignore[attr-defined]
        self.send_response(204)
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


def run_browser_check(timeout_s: float = 20.0) -> dict:
    """Launch a browser, collect gamepad readings, return structured result."""
    if not INDEX_HTML.exists():
        return {"available": False, "error": f"index.html not found at {INDEX_HTML}"}

    browser = _find_browser()
    if not browser:
        return {"available": False, "error": "no browser found (Edge/Chrome)"}

    if not _port_free(PORT):
        return {"available": False, "error": f"port {PORT} in use (another verify.py running?)"}

    # Start HTTP server (also serves index.html on GET /)
    httpd = socketserver.TCPServer(("127.0.0.1", PORT), _Handler)
    httpd.result = None  # type: ignore[attr-defined]
    httpd.timeout = 0.2
    server_thread = threading.Thread(target=httpd.serve_forever, daemon=True)
    server_thread.start()

    # Use a temp profile directory so we don't pollute the user's main profile
    import tempfile
    profile_dir = tempfile.mkdtemp(prefix="hidmaestro_browsercheck_")

    # --app= launches a frameless single-page window pointing at our HTTP server.
    # Serving from http://127.0.0.1 (not file://) keeps everything same-origin
    # so fetch() to /result works without CORS preflight problems.
    #
    # Background-throttling flags are CRITICAL: Chromium throttles setTimeout
    # to 1Hz when the window isn't focused, which makes our 30-sample/100ms
    # poll loop take 30 seconds instead of 3.
    #
    # Window focus is also critical for the Gamepad API on Chromium: gamepad
    # data is only delivered when the window owning the page has focus AND
    # there's been a user gesture in that window. We force-focus the window
    # below via SetForegroundWindow + AttachThreadInput, then synthesize a
    # mouse click to satisfy the user-gesture requirement.
    cmd = [
        browser,
        f"--app=http://127.0.0.1:{PORT}/",
        f"--user-data-dir={profile_dir}",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-features=Translate,OptimizationHints,CalculateNativeWinOcclusion",
        "--disable-background-timer-throttling",
        "--disable-renderer-backgrounding",
        "--disable-backgrounding-occluded-windows",
        "--autoplay-policy=no-user-gesture-required",
        "--window-size=400,300",
        "--window-position=100,100",
    ]
    # Capture Edge's stderr so we can debug renderer crashes / sandbox failures.
    edge_log = Path(profile_dir) / "edge.log"
    proc = subprocess.Popen(
        cmd,
        stdout=subprocess.DEVNULL,
        stderr=open(str(edge_log), "wb"),
    )

    # Force-focus the launched browser window after a brief startup delay so
    # Chromium delivers gamepad data and our setTimeout polling isn't throttled.
    def _focus_browser_window():
        import ctypes
        time.sleep(2.5)
        try:
            user32 = ctypes.windll.user32
            kernel32 = ctypes.windll.kernel32
            EnumWindows = user32.EnumWindows
            GetWindowThreadProcessId = user32.GetWindowThreadProcessId
            SetForegroundWindow = user32.SetForegroundWindow
            BringWindowToTop = user32.BringWindowToTop
            ShowWindow = user32.ShowWindow
            AllowSetForegroundWindow = user32.AllowSetForegroundWindow
            AllowSetForegroundWindow(proc.pid)
            EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
            target = []
            def cb(hwnd, lparam):
                pid = ctypes.c_ulong(0)
                GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
                if pid.value == proc.pid:
                    target.append(hwnd)
                return True
            EnumWindows(EnumProc(cb), 0)
            for hwnd in target:
                ShowWindow(hwnd, 5)         # SW_SHOW
                BringWindowToTop(hwnd)
                SetForegroundWindow(hwnd)
            # Synthesize a mouse click into the window for the user-gesture requirement.
            # SendInput at the screen position of our forced --window-position.
            time.sleep(0.5)
            INPUT_MOUSE = 0
            MOUSEEVENTF_LEFTDOWN = 0x0002
            MOUSEEVENTF_LEFTUP = 0x0004
            MOUSEEVENTF_ABSOLUTE = 0x8000
            MOUSEEVENTF_MOVE = 0x0001
            class MOUSEINPUT(ctypes.Structure):
                _fields_ = [("dx", ctypes.c_long), ("dy", ctypes.c_long),
                            ("mouseData", ctypes.c_ulong), ("dwFlags", ctypes.c_ulong),
                            ("time", ctypes.c_ulong), ("dwExtraInfo", ctypes.c_void_p)]
            class INPUTUNION(ctypes.Union):
                _fields_ = [("mi", MOUSEINPUT)]
            class INPUT(ctypes.Structure):
                _fields_ = [("type", ctypes.c_ulong), ("u", INPUTUNION)]
            screen_w = user32.GetSystemMetrics(0)
            screen_h = user32.GetSystemMetrics(1)
            # Click at (200, 200) — well inside our 400x300 window at (100,100)
            cx = int(200 * 65535 / max(screen_w, 1))
            cy = int(200 * 65535 / max(screen_h, 1))
            for flags in (
                MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                MOUSEEVENTF_LEFTDOWN,
                MOUSEEVENTF_LEFTUP,
            ):
                inp = INPUT(0, INPUTUNION(MOUSEINPUT(cx, cy, 0, flags, 0, None)))
                user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))
                time.sleep(0.05)
        except Exception:
            pass

    threading.Thread(target=_focus_browser_window, daemon=True).start()

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
