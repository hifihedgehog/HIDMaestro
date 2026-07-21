# Input Latency Benchmark

End-to-end input latency for a HIDMaestro virtual controller, measured from the `HMController.SubmitState` call to the input surfacing through XInput.

## Methodology

- One emulated `xbox-360-wired` controller, created in-process.
- A single button (A) is toggled each iteration, for 10,000 iterations.
- One process, one `Stopwatch` (QPC) clock shared between the writer and the reader, so there is no cross-process clock skew to correct for.
- The reader busy-polls `XInputGetState` and stops the clock the moment the A-button bit reflects the submitted state.
- Detection is by button bit, not by `dwPacketNumber`. Historically the XUSB companion incremented the packet number on every `GET_STATE` poll, which made it useless as a change detector. Since the 2026-07-21 audit it advances only on a real state change (matching physical xusb22), but the harness keeps detecting by button bit: it reflects the actual `GipData` the SDK wrote, so it tracks real propagation regardless of packet-number policy.
- Process priority High, reader thread priority Highest.

This matches VIIPER's published methodology closely enough for a like-for-like comparison: a single emulated controller, a single button transition per iteration, a tight reader loop, all on the same host. VIIPER reads via SDL3, which on Windows routes through XInput for an Xbox 360 device, so both numbers measure the same surface.

Reproduce:

```
HIDMaestroTest.exe latency xbox-360-wired
```

## Measurement integrity

The measured loop allocates nothing (the axes dictionary is built once and reused; `HMGamepadState` is a struct) and is marked `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`. Both matter:

- Allocating inside the loop drove gen0 garbage collections, and a collection landing in a timed window suspends the polling thread, adding broad jitter to the tail.
- Without forced optimization, the .NET JIT starts the loop in tier-0 and recompiles it on-stack at a fixed back-edge count, producing a single multi-millisecond stall at a deterministic iteration (~4895). That is the JIT optimizing the harness, not device latency.

With both controlled, GC count during the loop is zero and no sample exceeds 1 ms.

## Results

Windows 11 build 26200, AMD Ryzen-class desktop, 10,000 iterations per run, three runs. All figures in microseconds.

| Run | min | median | mean | p90 | p99 | max |
|-----|-----|--------|------|-----|-----|-----|
| 1 | 16.6 | 38.3 | 39.3 | 51.3 | 87.3 | 179 |
| 2 | 15.7 | 34.9 | 35.5 | 45.7 | 64.8 | 370 |
| 3 | 15.2 | 32.0 | 32.3 | 43.9 | 57.7 | 537 |

Median ~35 µs, p99 ~58-87 µs, worst case under 600 µs. Zero GC during the loop, zero samples above 1 ms. The distribution is continuous from ~15 µs up, with no floor at any millisecond boundary, which confirms `XInputGetState` issues a fresh IOCTL per call rather than reading a cached value on a timer. The number is real propagation latency, not poll quantization.

## Reference

VIIPER's published E2E single-press figures, from its [e2e_latency.md](https://github.com/Alia5/VIIPER/blob/main/docs/testing/e2e_latency.md):

| Platform | E2E single-press |
|----------|------------------|
| Windows / Ryzen 9 3900X | 168.3 µs |
| Steam Deck LCD | 89.1 µs |

VIIPER's figures are localhost-only by its own methodology note. The network path (the project's namesake) is excluded from those numbers because remote USBIP attachment adds network round-trip time and jitter. VIIPER also batches reports every millisecond, capping its update rate at 1000 Hz.

## Output direction: event-driven delivery (issue #34)

Input latency covers `SubmitState` to the game. The output direction
(game to consumer: rumble, FFB, LED) was poll-quantized until issue #34:
the SDK reader woke every 8 ms to check the output ring, so a rumble
packet waited up to a full poll interval before the consumer's
`OutputReceived` fired, and every idle controller cost 125 kernel waits
per second.

Since #34 the driver and the XUSB companion signal
`Global\HIDMaestroOutputEvent<N>` after each published packet and the
reader blocks on it. Measured with `test/probes/output_perf_bench`
(HidD_SetOutputReport to `OutputReceived` timestamp delta, 200 paced
sends; idle CPU via process CPU time over 30 s with 4 idle controllers),
same host and same day, before and after:

| Metric | 8 ms poll (pre-#34) | Event-driven (#34) |
|--------|--------------------:|-------------------:|
| Output RTT median | 9.41 ms | **0.182 ms** |
| Output RTT p95 | 18.70 ms | **0.244 ms** |
| Output RTT max | 23.04 ms | 0.630 ms |
| Idle CPU per controller | 0.651 ms/s (~0.07% core) | **0.000 ms/s** (below measurement resolution) |

Mutation-verified: forcing the reader onto its polling fallback (the
compatibility path for pre-#34 drivers) regressed the median to 15.5 ms
in the same harness, confirming the event is the load-bearing mechanism
and the fallback still delivers every packet.

Re-measured 2026-07-21 on the post-audit binary (adaptive reader,
multi-producer ring reservation, companion input doorbell, GIP read
serialization): three 200-send runs delivered 200/200 with medians
0.145 / 0.146 / 0.158 ms, p95 0.214-0.228 ms, max at or under 1.01 ms,
and idle CPU still 0.000 ms per controller per second. The same audit
round cut the launch and crash-recovery path: a same-version
`InstallDriver()` runs in ~40-60 ms (the full extract-sign-install
pipeline only runs when the embedded driver payload actually changed),
and recovery after a force-closed consumer reaches a live first
controller in ~2.3 s with an Xbox Series BT profile in the mix.

The same change trimmed the input submit path (construction-time trigger
resolution, compiled vendor-blob opcodes). Same-day before/after on the
input harness: median 53.6 to 48.9 µs, p99 98.5 to 85.4 µs, max 215.8 to
168.8 µs (host state ran warmer than the original three-run session
above; compare within the same day, not across sessions).
