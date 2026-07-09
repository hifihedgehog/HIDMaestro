/*
 * vr_transport.h: driver-side consumer of the Global\HIDMaestroVR section.
 *
 * Owns the worker thread that discovers the section + input event created
 * by the SDK, seqlock-reads input frames into a latest-wins snapshot, and
 * watches the config block for hand add / drop. RunFrame (vrserver's
 * thread) only ever touches the snapshot accessors. Nothing here blocks
 * RunFrame.
 *
 * The worker's two-phase shape (bootstrap poll -> steady-state wait ->
 * recycle stale handles) is the proven SharedInputWorkerProc pattern from
 * driver.c:779-857, with the wait constants re-derived for VR wall-clock:
 * the SDK signals per SubmitState (90-250 Hz typical), and config changes
 * are rare, so the 500 ms guard wait and 200 ms bootstrap poll carry over
 * unchanged while the stale-wakeup recycle threshold drops to 125
 * (~1 s of event-signaled-but-nothing-new before we re-open handles).
 */
#pragma once

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <thread>

#include "vr_shared_layout.h"

struct HmVrConfigSnapshot
{
    uint64_t ConfigSeqNo = 0;
    uint64_t ConsumerPid = 0;
    uint32_t ActiveHandsMask = 0;
    uint32_t Profile[HMVR_HAND_COUNT] = { 0, 0 };
    int32_t  Priority[HMVR_HAND_COUNT] = { 0, 0 };
};

struct HmVrInputSnapshot
{
    uint64_t TimestampQpc = 0;
    HMVR_CONTROLLER_STATE Ctrl[HMVR_HAND_COUNT] = {};
};

class HmVrTransport
{
public:
    HmVrTransport() = default;
    ~HmVrTransport() { Stop(); }

    HmVrTransport(const HmVrTransport &) = delete;
    HmVrTransport &operator=(const HmVrTransport &) = delete;

    void Start();
    void Stop();

    /* Latest-wins accessors for RunFrame. Copy out under the snapshot
     * seqlock so RunFrame never sees a torn struct. */
    bool GetConfig(HmVrConfigSnapshot *out) const;
    bool GetInput(HmVrInputSnapshot *out) const;

    /* True once the section has been mapped and validated this session. */
    bool IsSectionAttached() const { return m_sectionAttached.load(std::memory_order_acquire); }

    /* True while the consumer process that registered the config is
     * alive. Checked at most once per second (OpenProcess is not free);
     * RunFrame calls this every frame. */
    bool IsConsumerAlive();

    /* Haptic ring producer. Called from RunFrame only (single producer,
     * mirrors driver.c PublishOutput minus the writer lock, which the
     * single-thread contract makes unnecessary). Signals the haptic
     * event afterward. No-op until the section is attached. */
    void PublishHaptic(uint32_t controllerIdx, float amplitude,
                       float durationSeconds, float frequency);

private:
    void WorkerProc();
    bool TryOpenSection();
    void CloseSection();
    void ReadConfigLocked();
    bool ReadInputFrame();

    volatile HMVR_SHARED_SECTION *m_view = nullptr;
    HANDLE m_mapping = nullptr;
    HANDLE m_inputEvent = nullptr;
    HANDLE m_hapticEvent = nullptr;
    HANDLE m_stopEvent = nullptr;

    std::thread m_worker;
    std::atomic<bool> m_running{ false };
    std::atomic<bool> m_sectionAttached{ false };

    /* Snapshot seqlock (private to this process): the worker publishes,
     * RunFrame reads. Same odd/even discipline as the cross-process
     * section so the copy path is uniform. */
    mutable std::atomic<uint32_t> m_snapSeq{ 0 };
    HmVrConfigSnapshot m_config;
    HmVrInputSnapshot m_input;
    std::atomic<bool> m_haveConfig{ false };
    std::atomic<bool> m_haveInput{ false };

    uint64_t m_hapticSeqNo = 0;

    /* Consumer-liveness cache. */
    uint64_t m_livenessCheckedPid = 0;
    ULONGLONG m_livenessCheckedTick = 0;
    bool m_livenessLastResult = false;
};
