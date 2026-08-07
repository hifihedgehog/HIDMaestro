// Driver-side IPC consumer for the HIDMaestroVR section (issue #32).
//
// Mirrors the two-phase worker shape of driver.c SharedInputWorkerProc:
// phase 1 polls OpenEvent/OpenFileMapping until the consumer has created
// the kernel objects, gated by a stop event; phase 2 waits on the input
// event and snapshots the latest frame under the 4-retry seqlock read of
// driver.c ReadSharedInput. Wait/recycle constants are re-derived for the
// SteamVR tick (90-144 Hz) rather than copied from the 250 Hz HID values.

#pragma once

#include <windows.h>

#include <atomic>
#include <thread>

#include "vr_protocol.h"

class HmVrTransport
{
public:
    ~HmVrTransport();

    void Start();
    void Stop();

    // True when a consumer process is live: consumerPid is set, the
    // process still exists, and the newest input frame is fresher than
    // the staleness window.
    bool ConsumerActive();

    // Latest-wins snapshot of the input frame; false until the first
    // stable read.
    bool TryGetSnapshot( HMVR_InputFrame &out );

    // Current config (hands to expose). Valid once ConsumerActive.
    HMVR_Config GetConfig();

    // Raw mapped section for the haptic-ring producer and status writer;
    // nullptr until the section is open.
    HMVR_Section *Section() { return section_; }

    // Consumer-created haptic event, signalled after each ring publish;
    // nullptr until opened.
    HANDLE HapticEvent() { return haptic_event_; }

private:
    void WorkerProc();
    bool TryOpenObjects();
    void CloseObjects();

    HANDLE stop_event_ = nullptr;      // unnamed, private to the driver
    HANDLE input_event_ = nullptr;     // Global\HIDMaestroVR_InputEvent
    HANDLE haptic_event_ = nullptr;    // Global\HIDMaestroVR_HapticEvent
    HANDLE mapping_ = nullptr;
    HMVR_Section *section_ = nullptr;

    std::thread worker_;
    std::atomic<bool> running_{ false };

    // Snapshot double-buffer: worker writes, RunFrame reads.
    std::atomic<uint32_t> snap_seq_{ 0 };  // odd = writing
    HMVR_InputFrame snap_{};
    std::atomic<bool> have_snapshot_{ false };
    std::atomic<uint64_t> last_frame_qpc_{ 0 };
};
