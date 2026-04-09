"""Capture a dense trace of XInput left-stick (LX, LY) samples for a
specified slot and write ASCII-art + CSV. Used to investigate whether the
shape Cemu draws is a consumer-side problem or something visible at the
XInput API layer itself (which is the same place Cemu reads from).

Usage:
  python scripts/xinput_trace.py <slot> <seconds> [--csv out.csv]
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import time
from pathlib import Path


class XINPUT_GAMEPAD(ctypes.Structure):
    _fields_ = [
        ("wButtons", wt.WORD),
        ("bLeftTrigger", wt.BYTE),
        ("bRightTrigger", wt.BYTE),
        ("sThumbLX", ctypes.c_short),
        ("sThumbLY", ctypes.c_short),
        ("sThumbRX", ctypes.c_short),
        ("sThumbRY", ctypes.c_short),
    ]


class XINPUT_STATE(ctypes.Structure):
    _fields_ = [("dwPacketNumber", wt.DWORD), ("Gamepad", XINPUT_GAMEPAD)]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("slot", type=int)
    ap.add_argument("seconds", type=float)
    ap.add_argument("--csv", type=str, default=None)
    ap.add_argument("--interval-ms", type=float, default=2.0,
                    help="poll interval (ms), default 2 = 500 Hz")
    args = ap.parse_args()

    dll = ctypes.WinDLL("XInput1_4.dll")
    dll.XInputGetState.argtypes = [wt.DWORD, ctypes.POINTER(XINPUT_STATE)]
    dll.XInputGetState.restype = wt.DWORD

    samples: list[tuple[float, int, int, int, int]] = []  # (t, pkt, LX, LY, dLpkt)
    deadline = time.perf_counter() + args.seconds
    interval = args.interval_ms / 1000.0
    last_pkt = -1
    t0 = time.perf_counter()
    while time.perf_counter() < deadline:
        st = XINPUT_STATE()
        rc = dll.XInputGetState(args.slot, ctypes.byref(st))
        if rc != 0:
            time.sleep(0.05)
            continue
        pkt = st.dwPacketNumber
        if pkt != last_pkt:
            dpkt = 1 if last_pkt < 0 else pkt - last_pkt
            last_pkt = pkt
            samples.append((
                time.perf_counter() - t0,
                pkt,
                st.Gamepad.sThumbLX,
                st.Gamepad.sThumbLY,
                dpkt,
            ))
        time.sleep(interval)

    if not samples:
        print("no samples — is the controller connected on that slot?")
        return 1

    # Summary
    lxs = [s[2] for s in samples]
    lys = [s[3] for s in samples]
    print(f"captured {len(samples)} unique-packet samples over {args.seconds}s "
          f"({len(samples)/args.seconds:.0f} Hz unique-packet rate)")
    print(f"  LX range: [{min(lxs):+6d}, {max(lxs):+6d}]")
    print(f"  LY range: [{min(lys):+6d}, {max(lys):+6d}]")
    print(f"  packet number range: {samples[0][1]}..{samples[-1][1]} (span={samples[-1][1]-samples[0][1]})")
    print()

    # ASCII plot — 60x30 grid, axis range [-32768, +32767].
    W, H = 60, 30
    grid = [[' '] * W for _ in range(H)]
    # draw axes
    for r in range(H):
        grid[r][W // 2] = '|'
    for c in range(W):
        grid[H // 2][c] = '-'
    grid[H // 2][W // 2] = '+'
    for _, _, lx, ly in ((s[0], s[1], s[2], s[3]) for s in samples):
        # LX: -32768..32767 -> 0..W-1
        # LY in XInput is +Y up; ASCII rows grow downward so invert.
        cx = int((lx + 32768) * (W - 1) / 65535)
        cy = int((32767 - ly) * (H - 1) / 65535)
        if 0 <= cx < W and 0 <= cy < H:
            grid[cy][cx] = '*'
    print("ASCII trajectory (60x30, +Y up, +X right):")
    print('  +' + '-' * W + '+')
    for row in grid:
        print('  |' + ''.join(row) + '|')
    print('  +' + '-' * W + '+')

    if args.csv:
        p = Path(args.csv)
        p.write_text(
            "t_s,pkt,LX,LY,dpkt\n"
            + "\n".join(f"{t:.6f},{pkt},{lx},{ly},{dp}" for t, pkt, lx, ly, dp in samples)
        )
        print(f"wrote {len(samples)} rows to {p}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
