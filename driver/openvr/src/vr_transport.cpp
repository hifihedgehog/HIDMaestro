#include "vr_transport.h"

#include "driver_log.h"

namespace
{
    /* Bootstrap poll interval while the SDK hasn't created the section
     * yet. Same 200 ms tick as driver.c SharedInputWorkerProc phase 1. */
    constexpr DWORD kBootstrapPollMs = 200;

    /* Steady-state guard wait. Same 500 ms as driver.c phase 2: any
     * non-signal wait result recycles the handles, bounding recovery
     * from every stale-handle class to half a second. */
    constexpr DWORD kSteadyWaitMs = 500;

    /* Event signaled but the input seqno never moves: the SDK is
     * signaling an event object we share by name while writing a view
     * we are no longer mapped to (consumer restarted). At 90-250 Hz VR
     * submit rates, 125 stale wakeups is roughly a second. driver.c
     * uses 250 against ~250 Hz HID rates; re-derived per the spec. */
    constexpr ULONG kStaleWakeupsBeforeRecycle = 125;
}

void HmVrTransport::Start()
{
    if (m_running.exchange(true))
        return;

    m_stopEvent = CreateEventW(nullptr, TRUE /*manual*/, FALSE, nullptr);
    if (m_stopEvent == nullptr)
    {
        /* Without a stop event the worker could neither be woken nor
         * terminated (WaitForSingleObject(nullptr) returns WAIT_FAILED,
         * which would hot-spin phase 1, and Stop()'s join would hang
         * vrserver shutdown). driver.c:1110 creates its worker only
         * when the stop event exists; same rule here. */
        m_running.store(false);
        return;
    }
    m_worker = std::thread(&HmVrTransport::WorkerProc, this);
}

void HmVrTransport::Stop()
{
    if (!m_running.exchange(false))
        return;

    if (m_stopEvent != nullptr)
        SetEvent(m_stopEvent);
    if (m_worker.joinable())
        m_worker.join();

    /* Best-effort: tell the SDK we are gone before unmapping. */
    if (m_view != nullptr)
        m_view->DriverPid = 0;

    CloseSection();

    if (m_stopEvent != nullptr)
    {
        CloseHandle(m_stopEvent);
        m_stopEvent = nullptr;
    }
}

bool HmVrTransport::TryOpenSection()
{
    /* The SDK (elevated) creates the section + both events; we only ever
     * open. FILE_MAP_WRITE is required for the haptic ring + DriverPid;
     * the section SDDL carries an Interactive-Users ACE for exactly this
     * (vrserver runs as the interactive user). */
    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE,
                                      HMVR_SECTION_NAME);
    if (mapping == nullptr)
        return false;

    void *view = MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE,
                               0, 0, HMVR_SECTION_SIZE);
    if (view == nullptr)
    {
        CloseHandle(mapping);
        return false;
    }

    volatile HMVR_SHARED_SECTION *sec = (volatile HMVR_SHARED_SECTION *)view;
    if (sec->Magic != HMVR_MAGIC || sec->AbiVersion != HMVR_ABI_VERSION)
    {
        HmVrLog("[hidmaestro] VR section magic/ABI mismatch (magic=0x%08X abi=%u, want 0x%08X/%u); ignoring section",
                sec->Magic, sec->AbiVersion, HMVR_MAGIC, HMVR_ABI_VERSION);
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        return false;
    }

    HANDLE inputEv = OpenEventW(SYNCHRONIZE, FALSE, HMVR_INPUT_EVENT_NAME);
    HANDLE hapticEv = OpenEventW(EVENT_MODIFY_STATE, FALSE, HMVR_HAPTIC_EVENT_NAME);
    if (inputEv == nullptr || hapticEv == nullptr)
    {
        if (inputEv != nullptr) CloseHandle(inputEv);
        if (hapticEv != nullptr) CloseHandle(hapticEv);
        UnmapViewOfFile(view);
        CloseHandle(mapping);
        return false;
    }

    m_mapping = mapping;
    m_view = sec;
    m_inputEvent = inputEv;
    m_hapticEvent = hapticEv;

    /* Resume the haptic ring where the section left it so a vrserver
     * restart against a live consumer doesn't rewind SeqNos (the SDK's
     * reader cursor is monotonic). */
    m_hapticSeqNo = sec->HapticHead;

    sec->DriverPid = (uint64_t)GetCurrentProcessId();
    m_sectionAttached.store(true, std::memory_order_release);
    HmVrLog("[hidmaestro] VR section attached (consumerPid=%llu)",
            (unsigned long long)sec->ConsumerPid);
    return true;
}

void HmVrTransport::CloseSection()
{
    m_sectionAttached.store(false, std::memory_order_release);
    if (m_view != nullptr)
    {
        UnmapViewOfFile((LPCVOID)m_view);
        m_view = nullptr;
    }
    if (m_mapping != nullptr) { CloseHandle(m_mapping); m_mapping = nullptr; }
    if (m_inputEvent != nullptr) { CloseHandle(m_inputEvent); m_inputEvent = nullptr; }
    if (m_hapticEvent != nullptr) { CloseHandle(m_hapticEvent); m_hapticEvent = nullptr; }
}

void HmVrTransport::ReadConfigLocked()
{
    /* Config publish order on the SDK side is fields -> barrier ->
     * ConfigSeqNo, so reading SeqNo first and fields second can only
     * miss a brand-new update (picked up next tick), never see torn
     * fields for the SeqNo it reports. */
    HmVrConfigSnapshot cfg;
    cfg.ConfigSeqNo = m_view->ConfigSeqNo;
    MemoryBarrier();
    cfg.ConsumerPid = m_view->ConsumerPid;
    cfg.ActiveHandsMask = m_view->ActiveHandsMask;
    for (int h = 0; h < HMVR_HAND_COUNT; h++)
    {
        cfg.Profile[h] = m_view->Profile[h];
        cfg.Priority[h] = m_view->Priority[h];
    }

    uint32_t seq = m_snapSeq.load(std::memory_order_relaxed) + 1;
    m_snapSeq.store(seq, std::memory_order_release);          /* odd */
    m_config = cfg;
    m_snapSeq.store(seq + 1, std::memory_order_release);       /* even */
    m_haveConfig.store(true, std::memory_order_release);
}

bool HmVrTransport::ReadInputFrame()
{
    /* Cross-process seqlock read, the 4-retry loop from driver.c
     * ReadSharedInput verbatim: stable when the before/after seqnos
     * match and are even. */
    HmVrInputSnapshot in;
    uint32_t seq1 = 0, seq2 = 0;
    int retries = 4;
    do
    {
        seq1 = m_view->InputSeqNo;
        MemoryBarrier();
        in.TimestampQpc = m_view->TimestampQpc;
        for (int h = 0; h < HMVR_HAND_COUNT; h++)
        {
            in.Ctrl[h].ButtonBits = m_view->Ctrl[h].ButtonBits;
            for (int s = 0; s < HMVR_SCALAR_COUNT; s++)
                in.Ctrl[h].Scalars[s] = m_view->Ctrl[h].Scalars[s];
        }
        MemoryBarrier();
        seq2 = m_view->InputSeqNo;
    } while ((seq1 != seq2 || (seq1 & 1) != 0) && --retries > 0);

    if (seq1 != seq2 || (seq1 & 1) != 0)
        return false;

    uint32_t snapSeq = m_snapSeq.load(std::memory_order_relaxed) + 1;
    m_snapSeq.store(snapSeq, std::memory_order_release);       /* odd */
    m_input = in;
    m_snapSeq.store(snapSeq + 1, std::memory_order_release);   /* even */
    m_haveInput.store(true, std::memory_order_release);
    return true;
}

void HmVrTransport::WorkerProc()
{
    uint64_t lastConfigSeq = 0;
    uint32_t lastInputSeq = 0;

    for (;;)
    {
        /* Phase 1: bootstrap. Poll for the section until the SDK creates
         * it; StopEvent checked on every tick (driver.c:793-802 shape,
         * section + events opened together since the SDK creates all
         * three before flipping ConsumerPid). */
        while (m_view == nullptr)
        {
            if (TryOpenSection())
                break;
            if (WaitForSingleObject(m_stopEvent, kBootstrapPollMs) == WAIT_OBJECT_0)
                return;
        }

        /* Re-entry after an events-only recycle: the section stays
         * attached, only the wait handle is re-opened. */
        while (m_inputEvent == nullptr)
        {
            m_inputEvent = OpenEventW(SYNCHRONIZE, FALSE, HMVR_INPUT_EVENT_NAME);
            if (m_inputEvent != nullptr)
                break;
            if (WaitForSingleObject(m_stopEvent, kBootstrapPollMs) == WAIT_OBJECT_0)
                return;
        }

        /* Prime config immediately so RunFrame can act without waiting
         * for the first input signal. */
        ReadConfigLocked();
        lastConfigSeq = m_config.ConfigSeqNo;
        lastInputSeq = m_view->InputSeqNo;

        /* Phase 2: steady state (driver.c:816-841 shape). */
        ULONG staleWakeups = 0;
        HANDLE waits[2] = { m_stopEvent, m_inputEvent };
        for (;;)
        {
            DWORD rc = WaitForMultipleObjects(2, waits, FALSE, kSteadyWaitMs);

            if (rc == WAIT_OBJECT_0)
                return; /* StopEvent, the only legitimate exit */

            /* Config changes ride the same event as input frames (the
             * SDK signals after bumping ConfigSeqNo), and the timeout
             * path re-checks too so a lost signal only delays a hand
             * add/drop by half a second. */
            uint64_t cfgSeq = m_view->ConfigSeqNo;
            if (cfgSeq != lastConfigSeq)
            {
                ReadConfigLocked();
                lastConfigSeq = cfgSeq;
            }

            if (rc == WAIT_OBJECT_0 + 1)
            {
                uint32_t seqBefore = lastInputSeq;
                if (ReadInputFrame())
                    lastInputSeq = m_view->InputSeqNo;
                if (lastInputSeq == seqBefore && cfgSeq == lastConfigSeq)
                {
                    if (++staleWakeups > kStaleWakeupsBeforeRecycle)
                        break; /* recycle */
                }
                else
                {
                    staleWakeups = 0;
                }
                continue;
            }

            if (rc == WAIT_TIMEOUT)
            {
                /* Timeouts are normal when the consumer idles between
                 * frames. Nothing to recycle on consumer death: the
                 * SDK-side events and section are named objects our own
                 * handles keep alive, so a relaunched consumer opens
                 * the same objects. RunFrame handles the disconnect
                 * flips via IsConsumerAlive. */
                continue;
            }

            /* WAIT_FAILED / WAIT_ABANDONED / anything unexpected:
             * recycle the wait handle (driver.c:833-840 discipline). */
            break;
        }

        /* Recycle the INPUT EVENT ONLY, never the view. The RunFrame
         * thread publishes haptics through m_view concurrently with this
         * worker; in driver.c the worker's recycle likewise closes only
         * the mapping that the worker alone dereferences (driver.c:849,
         * SharedMemPtr) while the ring mapping PublishOutput writes is
         * unmapped exclusively after the worker join in
         * EvtDeviceContextCleanup. Here the shared view is unmapped
         * exclusively in Stop(), after the join, and vrserver serializes
         * Cleanup after the last RunFrame. */
        if (m_inputEvent != nullptr)
        {
            CloseHandle(m_inputEvent);
            m_inputEvent = nullptr;
        }
    }
}

bool HmVrTransport::GetConfig(HmVrConfigSnapshot *out) const
{
    if (!m_haveConfig.load(std::memory_order_acquire))
        return false;
    uint32_t s1, s2;
    int retries = 4;
    do
    {
        s1 = m_snapSeq.load(std::memory_order_acquire);
        *out = m_config;
        s2 = m_snapSeq.load(std::memory_order_acquire);
    } while ((s1 != s2 || (s1 & 1) != 0) && --retries > 0);
    return s1 == s2 && (s1 & 1) == 0;
}

bool HmVrTransport::GetInput(HmVrInputSnapshot *out) const
{
    if (!m_haveInput.load(std::memory_order_acquire))
        return false;
    uint32_t s1, s2;
    int retries = 4;
    do
    {
        s1 = m_snapSeq.load(std::memory_order_acquire);
        *out = m_input;
        s2 = m_snapSeq.load(std::memory_order_acquire);
    } while ((s1 != s2 || (s1 & 1) != 0) && --retries > 0);
    return s1 == s2 && (s1 & 1) == 0;
}

bool HmVrTransport::IsConsumerAlive()
{
    HmVrConfigSnapshot cfg;
    if (!GetConfig(&cfg) || cfg.ConsumerPid == 0)
        return false;

    ULONGLONG now = GetTickCount64();
    if (cfg.ConsumerPid == m_livenessCheckedPid
        && now - m_livenessCheckedTick < 1000)
        return m_livenessLastResult;

    bool alive = false;
    HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE,
                           (DWORD)cfg.ConsumerPid);
    if (h != nullptr)
    {
        DWORD exitCode = 0;
        alive = GetExitCodeProcess(h, &exitCode) && exitCode == STILL_ACTIVE;
        CloseHandle(h);
    }

    m_livenessCheckedPid = cfg.ConsumerPid;
    m_livenessCheckedTick = now;
    m_livenessLastResult = alive;
    return alive;
}

void HmVrTransport::PublishHaptic(uint32_t controllerIdx, float amplitude,
                                  float durationSeconds, float frequency)
{
    if (!m_sectionAttached.load(std::memory_order_acquire) || m_view == nullptr)
        return;

    /* Single-producer ring write, the driver.c PublishOutput protocol
     * (:550-574) with the WdfWaitLock dropped. That lock served two
     * roles in the reference: producer-vs-producer exclusion (not needed
     * here, RunFrame is the only producer) and publish-vs-unmap
     * exclusion (covered structurally: the worker's recycle never
     * unmaps m_view, and Stop() unmaps only after the join, with
     * vrserver serializing Cleanup after the last RunFrame). */
    m_hapticSeqNo++;
    uint64_t newSeq = m_hapticSeqNo;
    volatile HMVR_HAPTIC_SLOT *slot =
        &m_view->Haptic[(newSeq - 1) % HMVR_HAPTIC_RING_SLOTS];

    LARGE_INTEGER qpc;
    QueryPerformanceCounter(&qpc);

    slot->ControllerIdx = controllerIdx;
    slot->ComponentIdx = 0;
    slot->Amplitude = amplitude;
    slot->DurationSeconds = durationSeconds;
    slot->Frequency = frequency;
    slot->Reserved = 0;
    slot->TimestampQpc = (uint64_t)qpc.QuadPart;
    MemoryBarrier();
    slot->SeqNo = newSeq;
    MemoryBarrier();
    m_view->HapticHead = newSeq;

    if (m_hapticEvent != nullptr)
        SetEvent(m_hapticEvent);
}
