# Design note: the signal gate

Release 2.1.0 skips kernel transitions a frame does not need. Before it, every frame cost the SDK a `SetEvent` on the driver's input event, every Xbox 360 frame a second one on the XUSB companion's doorbell, and every delivered frame cost the driver a `SetEvent` on the delivery acknowledgement's event. The HID driver also took its locks through UMDF's wait lock, which is an auto-reset event (Windows-Driver-Frameworks `mxeventum.h`, `MxEvent::WaitFor` and `MxEvent::Set`): two system calls for every acquire and release, twice a frame. This note gives the protocol that drops what nobody waits for, the argument that it loses no wakeup, and the checks that back the argument.

## What changes

| Cost | Who pays | After |
|---|---|---|
| The HID driver's three locks | the driver host, twice a frame | slim reader/writer locks, user mode while uncontended |
| `SetEvent` on the input event | the SDK, every frame | only while a worker waits on it |
| `SetEvent` on the companion doorbell | the SDK, every frame with GIP bytes | only while a WAIT_FOR_INPUT is parked |
| `SetEvent` on the acknowledgement's event | the driver host, every delivered frame | only for a controller whose SDK waits on it |

No existing section layout, named object or profile id changes. The new words live in a new section, and each skip waits on a capability the driver host announces, so a program before 2.1.0 on the new driver, and a 2.1.0 program on an older driver, signal and acknowledge exactly as before.

## The section

`Global\HIDMaestroSignal<N>`, 256 bytes, `HIDMAESTRO_SIGNAL` in `driver/hm_contract.h`:

| Offset | Field | Written by | Read by |
|---|---|---|---|
| 0 | layout version, 1 | the SDK, once, when it creates the section | both drivers, at map |
| 4 | the HID driver's capabilities: 0x1, it counts its workers | HID driver, `InterlockedOr` at map, `InterlockedAnd` to clear | SDK, every frame |
| 8 | the companion's capabilities: 0x1, it counts its parked requests | companion, `InterlockedOr` at add | SDK, every Xbox 360 frame |
| 64 | `InputWaiters`, the HID workers inside a counted wait | HID driver, interlocked increment and decrement | SDK, every frame |
| 128 | `CompanionParked`, the WAIT_FOR_INPUT requests the companion holds | companion, interlocked increment and decrement | SDK, every Xbox 360 frame |

Each count has a cache line of its own, since two driver hosts write them. The SDK creates the section in `SharedMemoryIO.EnsureInputMapping`, before the input event, with the SDDL of its other sections, and the transport allocator skips an index whose section another program owns (`IndexSections`). After it creates the section the SDK writes nothing in it but one thing: a sweep that removes the device of an index the program still holds clears both capability bits (`SharedMemoryIO.ForgetDriverLocked`), so a device the next create makes there gets every signal until its own driver host counts itself and sets its bit again. The HID driver sets its bit at each map of the input section, the companion at add. The SDK never zeroes the section: one that already exists is held by a driver host of a device on the index, which may be counting in it, and zeroing it would hide a waiter. The SDK uses an existing section only when its layout is 1 and Administrators or SYSTEM own it. Otherwise it lets go of it and signals every frame. A driver host maps the section once and keeps it until its device goes, as it keeps the acknowledgement, so the name stays bound to the same object across programs on the index.

## The input event

The worker (`SharedInputWorkerProc` in `driver/driver.c`), before each wait on the input event:

1. `InterlockedIncrement(&InputWaiters)`. An increment that leaves the count below one shows it was negative, which no correct run produces, and the worker clears its capability bit, which sends the SDK back to a `SetEvent` on every frame.
2. Read the input section's `SeqNo`. When it is even and not the last frame the worker read, a frame is waiting: `InterlockedDecrement`, and the wait polls the other events with a zero timeout, which stands for the input event, so the stop, owner and remove events keep their order on every pass.
3. Otherwise wait, with the 500 ms timeout as before.
4. `InterlockedDecrement(&InputWaiters)` as the wait returns, before anything else.

The SDK (`SharedMemoryIO.WriteInputFrame`), after the frame:

1. Store the even `SeqNo` with release order (`Volatile.Write`).
2. A full fence (`Thread.MemoryBarrier`).
3. Read the HID driver's capabilities and `InputWaiters`. With the capability set and the count zero, skip the `SetEvent`. Otherwise signal, as before.

An odd `SeqNo` at step 2 of the worker is a write in progress: its writer stores the even number, fences, finds the worker counted and signals. So the worker never spins on a write.

### Why no wakeup is lost

Call the worker's increment I and its read of `SeqNo` R, and the SDK's store of frame F's even number S and its read of the count L. Both sides put a full fence between their store and their load: `InterlockedIncrement` "generates a full memory barrier (or fence) to ensure that memory operations are completed in order" (Microsoft Learn, InterlockedIncrement function), and `Thread.MemoryBarrier` compiles to `lock or dword ptr [rsp], 0` on x64 (the tier-1 listing of `WriteInputFrame`) and `dmb ish` on ARM64. The .NET memory model gives `Thread.MemoryBarrier` and the `Interlocked` methods full-fence semantics ("Full-fence operations", docs/design/specs/Memory-model.md in dotnet/runtime). Two stores and two loads on opposite sides of full fences cannot both miss each other: either R sees S, or L sees I. This is the store-buffering pattern, and full fences forbid its relaxed outcome on both architectures.

- R sees F. The worker reads F without waiting.
- L sees the worker's increment. The worker does not decrement until its wait returns, so L reads at least one, and the SDK signals. The input event is auto-reset: a wait that has not started yet finds it set, and one already blocked returns.

Either way the worker reads F. Several workers on one named event, the orphan case of issue #38, each count themselves, so the count is zero only when none of them is inside a counted wait. Each then reads the section before it waits again and finds anything newer than its own last frame. A `SetEvent` wakes one waiter, as before, so a frame the SDK signals reaches one of them, the guarantee the SDK's per-frame signal gave.

The 500 ms timeout wakes the worker as before and changes nothing here. A recycle through phase 1 closes the input event and opens it again. The worker waits uncounted there, on the stop event alone, and counts itself again from its first wait in phase 2, after it has read the section. Teardown returns from the wait and decrements before it checks `TearingDown`.

### What the argument assumes

Every waiter on the index's input event is a 2.1.0 worker that has the section mapped, or none has announced the capability. A USB/IP device's input pump also waits on the event and counts itself nowhere, so the SDK never gates a USB/IP controller (`HMController._signalView`). A device context of an older driver beside one of this driver on the same index would break the assumption: the newer one's capability would let the SDK skip signals the older one waits for. Two contexts share an index only while one of them tears down, and the SDK installs only a newer driver over an older one, so such a pair needs a downgrade with a device still tearing down. Even then the older worker reads the section on its 500 ms timeout.

## The companion doorbell

The companion (`driver/companion.c`) completes a parked `IOCTL_XUSB_WAIT_FOR_INPUT` from its pump thread when the doorbell rings, and from its 8 ms timer, Windows.Gaming.Input's cadence, which stays. With nothing parked, a ring only wakes the pump to find an empty queue.

- The IOCTL thread forwards the request to the manual queue, then `InterlockedIncrement(&CompanionParked)`, then reads the input section's `SeqNo` under `GipLock`. When the frame is newer than the one the last completion carried (`WaitServedSeq`, stored before each completion), it sets the pump's own kick event.
- The pump, or the timer, decrements once it takes a request (`CompanionPumpOnce`), and `EvtIoCanceledOnQueue` decrements for a request the framework cancels or purges, which it calls for every request it presented to the driver (Windows-Driver-Frameworks `fxioqueue.cpp`, `CancelForQueue`).
- The SDK rings the doorbell only when the companion's capability is clear or `CompanionParked` is not zero, after the same fence as the input event.

The pump can take a request before its increment, so the count can be -1 for a moment. The SDK rings on any count but zero.

### Why the last frame is served

The IOCTL thread's increment and its read of `SeqNo` face the SDK's store and its read of the count across full fences, as on the input event. Either the SDK sees the request counted and rings, and the pump wakes after the request is in the queue, or the IOCTL thread sees the frame and kicks the pump. In both cases the pump reads the section after the frame was stored and serves it.

This closes a gap the doorbell had before 2.1.0. The pump could take a frame's ring before Windows.Gaming.Input sent its next request, and that request then waited for the 8 ms timer. With the kick it waits for nothing. The kick needs the section, so a program before 2.1.0 on this driver keeps the old behavior, gap included: in `signal-stress` its Xbox 360 pass reads 235 frames over 2 ms of 122,571 where the 2.1.0 SDK reads none (SIG-stress-3).

## The acknowledgement event

`HMController` waits on `Global\HIDMaestroInputAckEvent<N>` only to deliver every report of a keyboard or mouse (`DeliversEveryReport`) and for a protocol host's replies (`ProtocolHost`). At create the SDK writes `Controller<N>\AckSignal`, 0 when neither applies (`DeviceOrchestrator.WaitsOnDelivery`). The driver reads it at add, under the contract, and with 0 stores `DeliveredSeqNo` on every delivery as before and skips only the `SetEvent`. A value it cannot read, or none, which is every program before 2.1.0, and it signals every delivery.

## The locks

`InputLock`, `OutputLock` and `FeatureLock` are `SRWLOCK`s in the zeroed device context, taken exclusive only. `SRWLOCK_INIT` is `{0}` (winnt.h, `RTL_SRWLOCK_INIT`), so the context's zeroing initializes them, and an SRW lock needs no deletion. Acquire and release order the data they guard on x64 and ARM64. WinUHid guards its HID request state the same way (`WinUHid.cpp`). The served input report is copied out under `InputLock` and into the request after it, so the worker never waits on the framework's buffer calls.

## Checks

- `test/probes/signal_gate_model` explores every interleaving of the SDK and one or two workers, and of the SDK, the companion's pump and a caller that keeps one WAIT_FOR_INPUT outstanding and sends the next the moment the last completes, each request's IOCTL dispatch on a thread of its own, so one dispatch can still be between its steps while the next runs. The SDK's stores sit in a store buffer that drains at any later step. With both fences no final state leaves a frame unread or the last frame unserved, with the companion's count raised after the park or before it. Without the SDK's fence, with a plain increment in the worker, or without the companion's kick, it finds the lost frame, which shows it would find one. The doorbell on every frame with no kick, before 2.1.0, also leaves the last frame unserved in the model, the gap the timer covered.
- `perf_budget_check signal-stress` sends frames after random pauses of 0 to 2 ms, in bursts of 50 to 200 and after pauses of 600 to 1,200 ms, for minutes, and fails when any frame is not read within 250 ms: through a host's ReadFile on a DualShock 4, and through Windows.Gaming.Input, which reads the companion's WAIT_FOR_INPUT completions, on an Xbox 360 pad. A missed signal that the worker's timeout or the companion's 8 ms timer recovers is only late, so the probe also switches the gate itself by writing the capability words, in blocks of 1 s that take each mode in turn (off, and on for each part and both), and fails a mode whose frames over 4 ms exceed twice the gate-off mode's plus five. Blocks that short spread a burst of load on the machine over every mode. It runs on the new pair, on a v2.0.3 build of the probe against the new driver (`--any-driver`), and with the capabilities cleared for the whole pass (`--gate-off`).
