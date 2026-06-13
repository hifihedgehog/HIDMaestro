# Input Latency Benchmark

End-to-end input latency for a HIDMaestro virtual controller, measured from the `HMController.SubmitState` call to the input surfacing through XInput.

## Methodology

- One emulated `xbox-360-wired` controller, created in-process.
- A single button (A) is toggled each iteration, for 10,000 iterations.
- One process, one `Stopwatch` (QPC) clock shared between the writer and the reader, so there is no cross-process clock skew to correct for.
- The reader busy-polls `XInputGetState` and stops the clock the moment the A-button bit reflects the submitted state.
- Detection is by button bit, not by `dwPacketNumber`. The XUSB companion increments the packet number on every `GET_STATE` poll (see [driver/companion.c](../../driver/companion.c)), so the packet number is useless as a change detector. The button bit reflects the actual `GipData` the SDK wrote, so it tracks real propagation.
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
