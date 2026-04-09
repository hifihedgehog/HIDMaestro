#!/usr/bin/env bash
# Launch the test app with one xbox-series-xs-bt virtual controller in its
# time-varying circle pattern, then trace XInput for several seconds and
# print the (LX, LY) trajectory. If the traced shape matches what Cemu's
# visualizer draws, the bug is at or above the XInput layer (not Cemu).
# If the trace is a clean circle, Cemu is doing something weird on its end.
set +u

REPO=$(cd "$(dirname "$0")/.." && pwd)
EXE="$REPO/test/bin/Debug/net10.0-windows10.0.26100.0/win-x64/HIDMaestroTest.exe"
PY="$REPO/scripts/xinput_trace.py"

echo "[1/5] cleanup..."
sudo --inline "$EXE" cleanup 2>&1 | sed 's/^/  > /'

echo "[2/5] launching emulate xbox-series-xs-bt in coproc..."
LOG=$(mktemp)
coproc TESTAPP { sudo --inline "$EXE" emulate xbox-series-xs-bt 2>&1 | tee "$LOG"; }
TESTAPP_STDIN_FD=${TESTAPP[1]}

echo "[3/5] waiting for ready (max 60s)..."
DEADLINE=$(( $(date +%s) + 60 ))
READY=0
while [[ $(date +%s) -lt $DEADLINE ]]; do
    grep -q "controller(s) ready" "$LOG" 2>/dev/null && { READY=1; break; }
    kill -0 $TESTAPP_PID 2>/dev/null || { echo "  !! app exited"; cat "$LOG"; rm -f "$LOG"; exit 3; }
    sleep 0.5
done
[[ $READY -eq 0 ]] && { echo "  TIMED OUT"; tail -20 "$LOG"; echo quit >&${TESTAPP_STDIN_FD}; wait; rm -f "$LOG"; exit 3; }
echo "  READY"

echo "[4/5] tracing XInput slot 0 for 3s (time-varying circle pattern running)..."
python "$PY" 0 3 --interval-ms 2

echo
echo "[5/5] quit..."
echo quit >&${TESTAPP_STDIN_FD}
WAIT_DEADLINE=$(( $(date +%s) + 60 ))
while kill -0 $TESTAPP_PID 2>/dev/null; do
    [[ $(date +%s) -gt $WAIT_DEADLINE ]] && { kill -9 $TESTAPP_PID 2>/dev/null; break; }
    sleep 1
done
wait $TESTAPP_PID 2>/dev/null
rm -f "$LOG"

echo "[final] cleanup..."
sudo --inline "$EXE" cleanup 2>&1 | sed 's/^/  > /'
