#!/usr/bin/env bash
# Browser-order diagnostic for HIDMaestro virtual controllers.
#
# Runs the test app elevated (via sudo --inline) in a bash coproc so we can
# pipe commands into its stdin from a non-elevated foreground that owns the
# user's interactive desktop. The browser check needs the user's desktop
# (focus + click synthesis), so it must NOT run elevated. The test app /
# driver install / device cleanup all NEED elevation.
#
# Sequence:
#   1. cleanup (elevated)
#   2. coproc-spawn 'emulate' with 4 distinct controllers (elevated)
#   3. wait for setup to finish (poll stderr or sleep)
#   4. send 'mark' over coproc stdin
#   5. run check_browser_order.py NON-elevated → reads navigator.getGamepads
#   6. send 'quit' over coproc stdin
#   7. wait for the test app to exit
#   8. final cleanup (elevated)
set +u   # coproc PID variable can briefly become unset across test app exit

REPO=$(cd "$(dirname "$0")/.." && pwd)
EXE="$REPO/test/HIDMaestroTest/bin/Debug/net10.0-windows10.0.26100.0/win-x64/HIDMaestroTest.exe"
PY="$REPO/scripts/check_browser_order.py"

if [[ ! -x "$EXE" ]]; then
    echo "ERROR: test exe not found at $EXE" >&2
    exit 2
fi

echo "[1/7] cleanup..."
sudo --inline "$EXE" cleanup 2>&1 | sed 's/^/  > /'

echo "[2/7] launching emulate (4 controllers) in coproc..."
# Homogeneous 4x DualSense — all reach Chromium via the same gamepad source
# (HID), so browser ordering is determined entirely by HID enumeration order.
# Heterogeneous mixes interleave HID/XInput/WGI sources and the resulting
# array order is implementation-defined inside Chromium, not a HIDMaestro
# bug we could fix even if we wanted to.
LOG=$(mktemp)
coproc TESTAPP { sudo --inline "$EXE" emulate dualsense dualsense dualsense dualsense 2>&1 | tee "$LOG"; }
TESTAPP_STDIN_FD=${TESTAPP[1]}

echo "[3/7] waiting for 'controller(s) ready' in log (max 90s)..."
DEADLINE=$(( $(date +%s) + 90 ))
READY=0
while [[ $(date +%s) -lt $DEADLINE ]]; do
    if grep -q "controller(s) ready" "$LOG" 2>/dev/null; then
        READY=1; break
    fi
    if ! kill -0 $TESTAPP_PID 2>/dev/null; then
        echo "  !! test app exited during setup" >&2
        cat "$LOG"
        rm -f "$LOG"
        exit 3
    fi
    sleep 0.5
done
if [[ $READY -eq 0 ]]; then
    echo "  !! TIMED OUT waiting for ready" >&2
    tail -20 "$LOG"
    echo quit >&${TESTAPP_STDIN_FD}
    wait $TESTAPP_PID
    rm -f "$LOG"
    exit 3
fi
echo "  READY"

echo "[4/7] sending mark..."
echo mark >&${TESTAPP_STDIN_FD}
sleep 4

echo "[5/7] running headless browser check + order analysis (non-elevated)..."
python "$PY"
PY_EXIT=$?

echo
echo "[6/7] sending quit..."
echo quit >&${TESTAPP_STDIN_FD}

echo "[7/7] waiting for test app exit (max 120s)..."
WAIT_DEADLINE=$(( $(date +%s) + 120 ))
while kill -0 $TESTAPP_PID 2>/dev/null; do
    if [[ $(date +%s) -gt $WAIT_DEADLINE ]]; then
        echo "  test app didn't exit, killing..."
        kill -9 $TESTAPP_PID 2>/dev/null || true
        break
    fi
    sleep 1
done
wait $TESTAPP_PID 2>/dev/null
rm -f "$LOG"

echo "[final] cleanup..."
sudo --inline "$EXE" cleanup 2>&1 | sed 's/^/  > /'

exit $PY_EXIT
