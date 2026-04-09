#!/usr/bin/env bash
# Empirical XInput readback at known parked left-stick positions for the
# Xbox Series X|S BT profile (the one Cemu's XInput visualizer reportedly
# renders incorrectly). Pumps the test app's 'park' command through a
# coproc, then calls XInputGetState directly via the python ctypes reader.
#
# Goal: identify whether parked positions read back wrong (encoding bug)
# or whether only TIME-VARYING patterns look wrong (timing/tearing bug).
set +u

REPO=$(cd "$(dirname "$0")/.." && pwd)
EXE="$REPO/test/bin/Debug/net10.0-windows10.0.26100.0/win-x64/HIDMaestroTest.exe"
PY="$REPO/scripts/xinput_park_readback.py"

echo "[1/4] cleanup..."
sudo --inline "$EXE" cleanup 2>&1 | sed 's/^/  > /'

echo "[2/4] launching emulate xbox-series-xs-bt in coproc..."
LOG=$(mktemp)
coproc TESTAPP { sudo --inline "$EXE" emulate xbox-series-xs-bt 2>&1 | tee "$LOG"; }
TESTAPP_STDIN_FD=${TESTAPP[1]}

echo "[3/4] waiting for ready (max 60s)..."
DEADLINE=$(( $(date +%s) + 60 ))
READY=0
while [[ $(date +%s) -lt $DEADLINE ]]; do
    if grep -q "controller(s) ready" "$LOG" 2>/dev/null; then
        READY=1; break
    fi
    if ! kill -0 $TESTAPP_PID 2>/dev/null; then
        echo "  !! test app exited" >&2
        cat "$LOG"; rm -f "$LOG"; exit 3
    fi
    sleep 0.5
done
if [[ $READY -eq 0 ]]; then
    echo "  TIMED OUT" >&2
    tail -20 "$LOG"
    echo quit >&${TESTAPP_STDIN_FD}
    wait $TESTAPP_PID; rm -f "$LOG"; exit 3
fi
echo "  READY"

echo "[4/4] parked-position XInput readback..."
echo "  initial scan of all 4 slots (non-elevated):"
python "$REPO/scripts/xinput_scan.py" 2>&1 | sed 's/^/    /'
echo "  initial scan of all 4 slots (elevated):"
sudo --inline python "$REPO/scripts/xinput_scan.py" 2>&1 | sed 's/^/    /'
echo
# Parking expectations for the SDK's Map(v) = (v+1)/2 convention:
#   LeftStickY = +1   ->  HID raw 65535  ->  XInput thumbLY ≈ -32768  (down)
#   LeftStickY =  0   ->  HID raw 32767  ->  XInput thumbLY ≈      0  (center)
#   LeftStickY = -1   ->  HID raw     0  ->  XInput thumbLY ≈ +32767  (up)
# (xinputhid scales HID 0..65535 to XInput +32767..-32768, hence the inversion.)
#
# We sweep five positions: -1, -0.5, 0, +0.5, +1, then a couple of OFF-AXIS
# combinations (X non-zero) to confirm there's no cross-axis bleed.
declare -a PARKS=(
    "0  0.0 -1.0"
    "0  0.0 -0.5"
    "0  0.0  0.0"
    "0  0.0  0.5"
    "0  0.0  1.0"
    "0  1.0  0.0"
    "0 -1.0  0.0"
    "0  0.7  0.7"
    "0 -0.7 -0.7"
)

for park_args in "${PARKS[@]}"; do
    echo "park $park_args"  >&${TESTAPP_STDIN_FD}
    # Settle: park message → pattern thread picks it up next loop iter (8ms),
    # plus xinputhid + XInput service propagation. 200 ms is plenty.
    sleep 0.2
    printf "  park (%s) -> " "$park_args"
    python "$PY" 0 --once 2>&1 | tail -1
done

echo
echo "park off + quit..."
echo "park off" >&${TESTAPP_STDIN_FD}
sleep 0.5
echo quit      >&${TESTAPP_STDIN_FD}
WAIT_DEADLINE=$(( $(date +%s) + 60 ))
while kill -0 $TESTAPP_PID 2>/dev/null; do
    if [[ $(date +%s) -gt $WAIT_DEADLINE ]]; then
        kill -9 $TESTAPP_PID 2>/dev/null || true; break
    fi
    sleep 1
done
wait $TESTAPP_PID 2>/dev/null
rm -f "$LOG"

echo "[final] cleanup..."
sudo --inline "$EXE" cleanup 2>&1 | sed 's/^/  > /'
