/* Case 5, the drivers' timed loops (review F1): the shared-input worker's
 * Phase 1 wait of 200 ms and Phase 2 wait of 500 ms with nothing signaled,
 * its recycle after eight idle timeouts and after 250 stale wakeups
 * (driver.c:2099-2304, with the !SwitchProtocol unmap guard at 2286 on a
 * Switch Pro and on a profile without the responder), and the companion's
 * input pump waits of 500 ms and 2000 ms (companion.c:780-819). Each
 * timeout is answered at the deadline the driver's own wait set, and every
 * wait and answer is recorded (hm_win32.c RecordWait). */
#include "hm_scenario.h"

static void Timeout(Ctl *c, int thread, int times)
{
    for (int i = 0; i < times; i++) ThreadResume(CtlThread(c, thread));
}

/* One frame through a parked read, then the polled report: what the
 * worker serves once it holds the section again. */
static void Probe(Ctl *c, int sw, int n)
{
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(n * 16 + i);
    uint32_t len = c->p->readLength ? c->p->readLength : 64;
    HidRead(c, len);
    Frame(c, data, sw ? 48 : 63, NULL, NULL, 0);
    if (sw) SwitchTick(c);
    HidGetInput(c, sw ? 0x30 : 0x01, len);
    HidGetInput(c, 0x00, len);
}

/* Phase 1: the device starts before the SDK made its objects, so the
 * worker's OpenEventW fails and it waits 200 ms on the stop event at a
 * time (driver.c:2103-2152). Then the SDK creates them. */
static void StreamPhase1(PlanProfile *p, int sw)
{
    Ctl c;
    StreamBegin("threads/%s/worker-phase1", p->id);
    if (!CtlStart(&c, p, 0, SDK_NONE, 0)) return;
    Timeout(&c, 0, 3);
    if (sw) SwitchTick(&c);
    SdkCreate(&c.sdk, 0, SDK_ALL);
    Timeout(&c, 0, 1);
    RecordSections(&c.sdk);
    Probe(&c, sw, 1);
    CtlStop(&c);
}

/* Phase 2: eight 500 ms timeouts with nothing signaled recycle the
 * worker's handles (driver.c:2245-2303). The unmap runs only without the
 * Switch responder (2286). */
static void StreamIdleRecycle(PlanProfile *p, int sw)
{
    Ctl c;
    StreamBegin("threads/%s/worker-idle-recycle", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    Probe(&c, sw, 1);
    Timeout(&c, 0, 7);
    Probe(&c, sw, 2);
    Timeout(&c, 0, 8);
    RecordSections(&c.sdk);
    Probe(&c, sw, 3);
    Timeout(&c, 0, 8);
    Timeout(&c, 0, 8);
    Probe(&c, sw, 4);
    CtlStop(&c);
}

/* 250 wakeups on the input event with no new SeqNo recycle the handles too
 * (driver.c:2207-2215). */
static void StreamStaleRecycle(PlanProfile *p, int sw)
{
    Ctl c;
    StreamBegin("threads/%s/worker-stale-recycle", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    Probe(&c, sw, 1);
    for (int i = 0; i < 251; i++) {
        EventSet(c.sdk.inputEv);
        RunWorker(&c);
    }
    RecordSections(&c.sdk);
    Probe(&c, sw, 2);
    CtlStop(&c);
}

/* The companion's pump: 500 ms waits on its stop event while the SDK's
 * companion input event does not exist, then 2000 ms waits on both, after
 * each of which it reopens the event (companion.c:786-818). */
static void StreamPump(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/companion-pump", p->id);
    if (!CtlStart(&c, p, 0, SDK_NONE, 1)) return;
    Timeout(&c, 0, 2);
    SdkCreate(&c.sdk, 0, SDK_ALL);
    Timeout(&c, 0, 1);
    Timeout(&c, 0, 2);
    uint8_t gip[14] = { 0xFF, 0x7F, 0xFF, 0x7F, 0xFF, 0x7F, 0xFF, 0x7F, 0, 0, 0, 0, 0x01, 0 };
    Ioctl(c.dev, "xusb.waitforinput", 0x8000E3AC, NULL, 0, 29, NULL, 0);
    Frame(&c, NULL, 0, gip, NULL, 0);
    ThreadResume(CtlThread(&c, 0));
    Ioctl(c.dev, "xusb.waitforinput", 0x8000E3AC, NULL, 0, 29, NULL, 0);
    Timeout(&c, 0, 1);
    gip[12] = 0x02;
    Frame(&c, NULL, 0, gip, NULL, 0);
    ThreadResume(CtlThread(&c, 0));
    CtlStop(&c);
}

/* 2.1.0's signal gate never sends a frame twice (docs/design/signal-gate.md,
 * "Which frame is new"). One frame delivered, a read parked, and nothing
 * more from the SDK through two recycles: no completion of that read may
 * come, since the section still holds the frame HID class already has. */
static void StreamGateIdle(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/gate-idle-no-repeat", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    Probe(&c, 0, 1);
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    HidRead(&c, len);
    Timeout(&c, 0, 8);
    Timeout(&c, 0, 8);
    Timeout(&c, 0, 2);
    CtlStop(&c);
}

/* One frame delivered, two reads parked, then the owner pipe closes (the
 * program ended) and the worker runs: OwnerGone sends nothing again. */
static void StreamGateOwnerGone(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/gate-owner-gone-no-repeat", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    Probe(&c, 0, 1);
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    HidRead(&c, len);
    HidRead(&c, len);
    PipeServerClose(c.sdk.pipe);
    ThreadResume(CtlThread(&c, 0));
    Timeout(&c, 0, 1);
    Timeout(&c, 0, 1);
    CtlStop(&c);
}

/* Issue #67 with the gate: one frame delivered (a key held), the SDK's
 * release frame in the acknowledgement section, two reads parked, then the
 * owner pipe closes and the worker runs. The release frame is the last
 * report HID class gets. */
static void StreamGateRelease(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/gate-release-last", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    Probe(&c, 0, 1);
    uint8_t *a = c.sdk.ack->buf;
    int32_t seq = 2, cnt = 1, sz = 78;
    memcpy(a + 8, &seq, 4);
    memcpy(a + 12, &cnt, 4);
    memcpy(a + 16, &sz, 4);
    memset(a + 20, 0, 80);
    a[20] = 0x01;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    HidRead(&c, len);
    HidRead(&c, len);
    PipeServerClose(c.sdk.pipe);
    ThreadResume(CtlThread(&c, 0));
    Timeout(&c, 0, 1);
    Timeout(&c, 0, 1);
    CtlStop(&c);
}

/* The SeqNo wrap: frames across 0xFFFFFFFF, each read by a parked read. A
 * 2.1.0 SDK steps from 0xFFFFFFFF to 2, and the frame after the wrap goes
 * out as every other does. */
static void StreamGateSeqWrap(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/gate-seq-wrap", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    c.sdk.seq = 0xFFFFFFFCu;
    Probe(&c, 0, 1);
    Probe(&c, 0, 2);
    Probe(&c, 0, 3);
    RecordSections(&c.sdk);
    CtlStop(&c);
}

/* The same wrap under a program before 2.1.0, which stores SeqNo 0 with its
 * frame and signals it: that frame goes out as every other does. */
static void StreamLegacySeqWrap(PlanProfile *p)
{
    Ctl c;
    int saved = g_sdkSignal;
    g_sdkSignal = 0;
    StreamBegin("threads/%s/legacy-seq-wrap", p->id);
    if (CtlStart(&c, p, 0, SDK_ALL, 0)) {
        c.sdk.seq = 0xFFFFFFFCu;
        Probe(&c, 0, 1);
        Probe(&c, 0, 2);
        Probe(&c, 0, 3);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

/* The frame after the wrap with no read parked, then a read: the read takes
 * that frame from the cache at once, under the plan's SDK and under one
 * before 2.1.0 (legacy), whose frame after 0xFFFFFFFE is SeqNo 0. */
static void StreamLateReadWrap(PlanProfile *p, int legacy)
{
    Ctl c;
    int saved = g_sdkSignal;
    if (legacy) g_sdkSignal = 0;
    StreamBegin(legacy ? "threads/%s/legacy-late-read-wrap" : "threads/%s/gate-late-read-wrap", p->id);
    if (CtlStart(&c, p, 0, SDK_ALL, 0)) {
        c.sdk.seq = 0xFFFFFFFCu;
        Probe(&c, 0, 1);
        uint8_t data[64];
        for (int i = 0; i < 64; i++) data[i] = (uint8_t)((legacy ? 0x60 : 0x50) + i);
        Frame(&c, data, 63, NULL, NULL, 0);
        uint32_t len = c.p->readLength ? c.p->readLength : 64;
        HidRead(&c, len);
        Timeout(&c, 0, 1);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

/* The next program starts on the index the moment the driver lets go of
 * the last program's pipe (CloseOwnerWatch in OwnerGone): it zeroes the
 * input section and the acknowledgement, makes its pipe and writes its
 * first frame, with no read parked. A program before 2.1.0 counts from 0, a
 * 2.1.0 one from above the SeqNo left. After the worker has run, a read
 * comes, or one was parked all along. v2.0.3 reads that frame after
 * OwnerGone and serves it to the read, from the cache when it comes late. */
static Ctl *g_nextCtl;
static int g_nextLegacy;
/* The next program's start up to its first frame: it zeroes the input
 * section and the acknowledgement and makes its pipe. */
static void MapNextProgram(Ctl *c, int legacy)
{
    WCHAR n[96];
    uint32_t left;
    memcpy(&left, c->sdk.input->buf, 4);
    memset(c->sdk.input->buf, 0, c->sdk.input->size);
    memset(c->sdk.ack->buf, 0, c->sdk.ack->size);
    swprintf_s(n, ARRAYSIZE(n), L"\\\\.\\pipe\\HIDMaestroOwner%d", c->index);
    c->sdk.pipe = PipeServerNew(n, 0x2F44);
    if (legacy) {
        c->sdk.signal = NULL;
        c->sdk.seq = 0;
    } else {
        if (c->sdk.signal) memset(c->sdk.signal->buf + 68, 0, 4);
        c->sdk.seq = (left & 1) ? left + 1 : left;
    }
}

static void StartNextProgram(void)
{
    Ctl *c = g_nextCtl;
    MapNextProgram(c, g_nextLegacy);
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x70 + i);
    SdkWriteFrame(&c->sdk, data, 63, NULL, NULL, 0);
}

static void StreamNextProgramInOwnerGone(PlanProfile *p, int legacy, int parked)
{
    Ctl c;
    StreamBegin("threads/%s/next-%s-in-ownergone-%s", p->id, legacy ? "legacy" : "gated", parked ? "parked" : "late");
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    Probe(&c, 0, 1);
    Probe(&c, 0, 2);
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    if (parked) HidRead(&c, len);
    g_nextCtl = &c;
    g_nextLegacy = legacy;
    g_pipeClientCloseHook = StartNextProgram;
    PipeServerClose(c.sdk.pipe);
    ThreadResume(CtlThread(&c, 0));
    g_pipeClientCloseHook = NULL;
    RecordSections(&c.sdk);
    ThreadResume(CtlThread(&c, 0));
    if (!parked) HidRead(&c, len);
    HidGetInput(&c, 0x01, len);
    Timeout(&c, 0, 1);
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x90 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    CtlStop(&c);
}

/* The next program starts the moment the worker's wait answers the last
 * program's pipe close, before OwnerGone runs, with no read parked or one
 * parked all along. Under 2.1.0 the worker has left its counted wait by
 * then, so the first frame is handed over final with no signal. OwnerGone
 * leaves the handoff alone, and the take at the loop top after it reads the
 * frame. A program before 2.1.0 signals it. v2.0.3 reads that signal after
 * OwnerGone and serves it to the read, from the cache when it comes late. */
static int NextAtOwnerAnswer(HmThread *t, DWORD result)
{
    Ctl *c = g_nextCtl;
    if (t != CtlThread(c, 0) || result != WAIT_OBJECT_0 + 2) return 0;
    StartNextProgram();
    int32_t caps = 0;
    if (c->sdk.signal) memcpy(&caps, c->sdk.signal->buf + 4, 4);
    if (caps & 1) {
        /* The SDK ran after the worker's decrement: the count was zero,
         * so it made the frame final and signaled nothing. */
        c->sdk.inputEv->signaled = FALSE;
        memcpy(c->sdk.signal->buf + 68, &c->sdk.seq, 4);
    }
    return 1;
}

static void StreamNextProgramBeforeTake(PlanProfile *p, int legacy, int parked)
{
    Ctl c;
    StreamBegin("threads/%s/next-%s-before-ownergone-take-%s", p->id, legacy ? "legacy" : "gated", parked ? "parked" : "late");
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    Probe(&c, 0, 1);
    Probe(&c, 0, 2);
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    if (parked) HidRead(&c, len);
    g_nextCtl = &c;
    g_nextLegacy = legacy;
    g_waitAnswerHook = NextAtOwnerAnswer;
    PipeServerClose(c.sdk.pipe);
    ThreadResume(CtlThread(&c, 0));
    if (g_waitAnswerHook) HmFail("next-before-take: the wait never answered the pipe's close");
    RecordSections(&c.sdk);
    ThreadResume(CtlThread(&c, 0));
    if (!parked) HidRead(&c, len);
    HidGetInput(&c, 0x01, len);
    Timeout(&c, 0, 1);
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x90 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    CtlStop(&c);
}

/* A frame handed over while the worker is outside its counted wait, here
 * as it completes a parked read, and then a stop event before the worker's
 * next poll: mode 1 a foreign stop (another program's sweep, or a
 * same-index sibling's cleanup), mode 2 the device's own teardown
 * (TearingDown set, as ReleaseIndexObjects sets it from another thread)
 * and an add of the device again while the program runs. Mode 0 is the
 * control with no stop. Under 2.1.0 the worker takes the frame and leaves
 * its loop without reading it. v2.0.3 leaves the input event set for that
 * frame, and the recycled worker or the next device context reads it. Each
 * runs under a program before 2.1.0 too (legacy). */
static Ctl *g_x5Ctl;
static int g_x5Mode;
static void X5Hook(void)
{
    Ctl *c = g_x5Ctl;
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x70 + i);
    SdkWriteFrame(&c->sdk, data, 63, NULL, NULL, 0);
    if (g_x5Mode) {
        /* The worker's Phase 2 wait puts the stop event first. */
        HmObj *o = ObjFromHandle(c->dev->threads[0]->waitHandles[0]);
        if (!o || o->kind != HK_EVENT || !((HmEvent *)o)->manual) HmFail("x5: no stop event");
        if (g_x5Mode == 2) *(volatile LONG *)((uint8_t *)c->dev->context + HmTearingDownOffset) = 1;
        EventSet((HmEvent *)o);
    }
}

static void StreamX5(PlanProfile *p, int mode, int legacy)
{
    Ctl c;
    int saved = g_sdkSignal;
    if (legacy) g_sdkSignal = 0;
    StreamBegin("threads/%s/x5-take-then-%s%s", p->id,
                mode == 0 ? "nothing" : mode == 1 ? "foreign-stop" : "teardown-readd", legacy ? "-legacy" : "");
    if (CtlStart(&c, p, 0, SDK_ALL, 0)) {
        uint32_t len = c.p->readLength ? c.p->readLength : 64;
        Probe(&c, 0, 1);
        HidRead(&c, len);
        g_x5Ctl = &c;
        g_x5Mode = mode;
        g_completeHook = X5Hook;
        uint8_t data[64];
        for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x50 + i);
        Frame(&c, data, 63, NULL, NULL, 0);
        if (g_completeHook) HmFail("x5: the hook never ran");
        if (mode == 2) {
            DeviceRemove(c.dev);
            ConfigWrite(p, 0);
            c.dev = DeviceAdd(0);
            if (!c.dev) HmFail("x5: re-add failed");
        }
        HidRead(&c, len);
        RunWorker(&c);
        RunWorker(&c);
        RunWorker(&c);
        for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x90 + i);
        Frame(&c, data, 63, NULL, NULL, 0);
        HidRead(&c, len);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

/* The stress probe's gate-off mode (perf_budget_check signal-stress): the
 * SDK made the signal section and the driver maps it and counts, but the
 * capability is cleared under it once, so frames signal until the driver's
 * AnnounceGate sets it again at its 256th frame. Reads parked and late,
 * through 300 frames, and the capability word every 50. */
static void StreamX5GateOff(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/x5-gate-off-then-announce", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    Probe(&c, 0, 1);
    if (c.sdk.signal) memset(c.sdk.signal->buf + 4, 0, 4);
    uint8_t data[64];
    for (int f = 0; f < 300; f++) {
        for (int i = 0; i < 64; i++) data[i] = (uint8_t)(f + i);
        if (f % 3 == 0) HidRead(&c, len);
        Frame(&c, data, 63, NULL, NULL, 0);
        if (f % 3 == 1) HidRead(&c, len);
        if ((f % 50 == 0 || f == 299) && c.sdk.signal) { int32_t caps; memcpy(&caps, c.sdk.signal->buf + 4, 4); RecOut("x5.caps", &caps, 4); }
    }
    CtlStop(&c);
}

/* A program killed inside WriteInputFrame after its exchange put frame X
 * in the handoff and before it read the count and signaled. A worker sits
 * in its counted wait, so nothing wakes it for X. Then the pipe closes.
 * With release frames (issue #67, a keyboard's key held) and without (a
 * gamepad). v2.0.3, killed after the store and before its SetEvent, never
 * reads X: the release frame, or the frame before X, is the last report. */
static void StreamX5Kill(PlanProfile *p, int release)
{
    Ctl c;
    StreamBegin("threads/%s/x5-kill-after-exchange%s", p->id, release ? "-release" : "");
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    Probe(&c, 0, 1);
    if (release) {
        uint8_t *a = c.sdk.ack->buf;
        int32_t seq = 2, cnt = 1, sz = 78;
        memcpy(a + 8, &seq, 4);
        memcpy(a + 12, &cnt, 4);
        memcpy(a + 16, &sz, 4);
        memset(a + 20, 0, 80);
        a[20] = 0x01;
    }
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    HidRead(&c, len);
    HidRead(&c, len);
    int32_t caps = 0;
    if (c.sdk.signal) memcpy(&caps, c.sdk.signal->buf + 4, 4);
    RecOut("x5.caps", &caps, 4);
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0xA0 + i);
    SdkWriteFrame(&c.sdk, data, 63, NULL, NULL, 0);
    c.sdk.inputEv->signaled = FALSE;
    if (c.sdk.signal && (caps & 1)) { uint32_t prov = c.sdk.seq | 1; memcpy(c.sdk.signal->buf + 68, &prov, 4); }
    PipeServerClose(c.sdk.pipe);
    ThreadResume(CtlThread(&c, 0));
    Timeout(&c, 0, 1);
    Timeout(&c, 0, 1);
    CtlStop(&c);
}

/* HID class keeps one READ_REPORT outstanding: it sends the next once the
 * last completed. */
static void KeepOneRead(Ctl *c)
{
    uint32_t len = c->p->readLength ? c->p->readLength : 64;
    if (Parked(c) == 0) HidRead(c, len);
}

static void FrameData(uint8_t data[64], uint8_t base)
{
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(base + i);
}

/* Two programs before 2.1.0 on the index, the second starting early: it
 * zeroes the input section before the worker has handled the first one's
 * pipe close. Each step a wait answer the stream picks runs at that
 * answer, before the worker reads (g_waitAnswerHook).
 *
 * legacy-next-after-zero-read: A1 (SeqNo 2) is read and delivered. A2's
 * signal wakes the worker, and before it reads, A ends and B zeroes the
 * section. The worker reads the zeroed section. B1, SeqNo 2 again, is
 * signaled and read before OwnerGone. v2.0.3 forgot SeenSeqNo at the zero
 * read and sends B1. */
static Ctl *g_zCtl;
static int ZeroReadAnswer(HmThread *t, DWORD result)
{
    Ctl *c = g_zCtl;
    if (t != CtlThread(c, 0) || result != WAIT_OBJECT_0 + 1) return 0;
    PipeServerClose(c->sdk.pipe);
    MapNextProgram(c, 1);
    return 1;
}

static void StreamZeroReadNext(PlanProfile *p)
{
    Ctl c;
    int saved = g_sdkSignal;
    g_sdkSignal = 0;
    StreamBegin("threads/%s/legacy-next-after-zero-read", p->id);
    if (CtlStart(&c, p, 0, SDK_ALL, 0)) {
        uint8_t data[64];
        KeepOneRead(&c);
        FrameData(data, 0x10);
        Frame(&c, data, 63, NULL, NULL, 0);
        KeepOneRead(&c);
        FrameData(data, 0x20);
        SdkWriteFrame(&c.sdk, data, 63, NULL, NULL, 0);
        g_zCtl = &c;
        g_waitAnswerHook = ZeroReadAnswer;
        RunWorker(&c);
        if (g_waitAnswerHook) HmFail("zero-read: the wait never answered the input event");
        KeepOneRead(&c);
        FrameData(data, 0x70);
        SdkWriteFrame(&c.sdk, data, 63, NULL, NULL, 0);
        RunWorker(&c);
        KeepOneRead(&c);
        RunWorker(&c);
        KeepOneRead(&c);
        FrameData(data, 0x90);
        Frame(&c, data, 63, NULL, NULL, 0);
        KeepOneRead(&c);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

/* legacy-next-zero-read-late-after-recycle: A1's signal wakes the worker,
 * and A2 (SeqNo 4) is stored and signaled before it reads, so it reads and
 * delivers A2 and A2's signal stays set. A foreign stop recycles the
 * worker, which forgets the SeqNos, and A2's signal has it read A2 again
 * into the cache with no read parked. A3's signal wakes it, and before it
 * reads, A ends and B zeroes the section. Then a read comes. v2.0.3 put
 * its all-zero report at SeqNo 0 in the cache, and the read parks.
 *
 * legacy-next-late-below-last-delivered: A2 is delivered as above. A2's
 * signal wakes the worker, and before it reads, A ends, B zeroes the
 * section and stores B1 (SeqNo 2) without signaling yet. The worker reads
 * B1 with no read parked, and then a read comes: below the SeqNo last
 * delivered, it parks in v2.0.3. OwnerGone runs, then B1's signal comes. */
static int g_zMode;
static int SecondFrameAnswer(HmThread *t, DWORD result)
{
    Ctl *c = g_zCtl;
    if (t != CtlThread(c, 0) || result != WAIT_OBJECT_0 + 1) return 0;
    uint8_t data[64];
    FrameData(data, 0x20);
    SdkWriteFrame(&c->sdk, data, 63, NULL, NULL, 0);
    return 1;
}

static int NextStartsAnswer(HmThread *t, DWORD result)
{
    Ctl *c = g_zCtl;
    if (t != CtlThread(c, 0) || result != WAIT_OBJECT_0 + 1) return 0;
    PipeServerClose(c->sdk.pipe);
    MapNextProgram(c, 1);
    if (g_zMode == 1) {
        uint8_t data[64];
        FrameData(data, 0x70);
        SdkWriteFrame(&c->sdk, data, 63, NULL, NULL, 0);
        c->sdk.inputEv->signaled = FALSE;
    }
    return 1;
}

static void StreamTwoProgramLate(PlanProfile *p, int mode)
{
    Ctl c;
    int saved = g_sdkSignal;
    g_sdkSignal = 0;
    StreamBegin(mode == 0 ? "threads/%s/legacy-next-zero-read-late-after-recycle" : "threads/%s/legacy-next-late-below-last-delivered", p->id);
    if (CtlStart(&c, p, 0, SDK_ALL, 0)) {
        uint8_t data[64];
        uint32_t len = c.p->readLength ? c.p->readLength : 64;
        g_zCtl = &c;
        g_zMode = mode;
        KeepOneRead(&c);
        FrameData(data, 0x10);
        SdkWriteFrame(&c.sdk, data, 63, NULL, NULL, 0);
        g_waitAnswerHook = SecondFrameAnswer;
        RunWorker(&c);
        if (g_waitAnswerHook) HmFail("two-program: the first wait never answered the input event");
        if (mode == 0) {
            HmObj *o = ObjFromHandle(c.dev->threads[0]->waitHandles[0]);
            if (!o || o->kind != HK_EVENT || !((HmEvent *)o)->manual) HmFail("two-program: no stop event");
            EventSet((HmEvent *)o);
            RunWorker(&c);
            RunWorker(&c);
            FrameData(data, 0x30);
            SdkWriteFrame(&c.sdk, data, 63, NULL, NULL, 0);
        }
        g_waitAnswerHook = NextStartsAnswer;
        RunWorker(&c);
        if (g_waitAnswerHook) HmFail("two-program: the next program's wait never answered the input event");
        HidRead(&c, len);
        RunWorker(&c);
        KeepOneRead(&c);
        if (mode == 1) {
            EventSet(c.sdk.inputEv);
            RunWorker(&c);
            KeepOneRead(&c);
        }
        FrameData(data, 0x90);
        Frame(&c, data, 63, NULL, NULL, 0);
        KeepOneRead(&c);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}
/* SdkWriteFrame in two parts, as a program preempted or killed between
 * them: the frame's store (the section's bytes and its even SeqNo), and the
 * gate's part (exchange, recheck, take-back, SetEvent). */
static void X6Store(Sdk *s, uint8_t base)
{
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(base + i);
    uint8_t *b = s->input->buf;
    uint32_t pending = s->seq + 1, len = 63, zero = 0;
    memcpy(b, &pending, 4);
    memcpy(b + 4, &len, 4);
    memcpy(b + 8, data, 63);
    memcpy(b + 278, &zero, 4);
    s->seq = pending + 1;
    if (s->signal && s->seq == 0) s->seq = 2;
    memcpy(b, &s->seq, 4);
}

static void X6Signal(Sdk *s)
{
    int input = 1;
    if (s->signal) {
        int32_t caps, waiters, handoff;
        memcpy(&caps, s->signal->buf + 4, 4);
        if (caps & 1) {
            int32_t fin = (int32_t)s->seq, prov = fin | 1;
            memcpy(&handoff, s->signal->buf + 68, 4);
            if (handoff != 0 && (handoff & 1) == 0) {
                memcpy(s->signal->buf + 68, &fin, 4);
                input = 0;
            } else {
                memcpy(s->signal->buf + 68, &prov, 4);
                memcpy(&caps, s->signal->buf + 4, 4);
                memcpy(&waiters, s->signal->buf + 64, 4);
                memcpy(&handoff, s->signal->buf + 68, 4);
                if ((caps & 1) && waiters == 0) {
                    if (handoff == prov) { memcpy(s->signal->buf + 68, &fin, 4); input = 0; }
                } else if (handoff == prov) {
                    memset(s->signal->buf + 68, 0, 4);
                }
            }
        }
    }
    if (input) EventSet(s->inputEv);
}

/* The exchange done and the program killed before its recheck: the frame
 * sits in the handoff, provisional (2.1.0 on a gated driver), with no
 * SetEvent. */
static void X6ExchangeOnly(Sdk *s)
{
    int32_t caps = 0;
    if (s->signal) memcpy(&caps, s->signal->buf + 4, 4);
    if (s->signal && (caps & 1)) { uint32_t prov = s->seq | 1; memcpy(s->signal->buf + 68, &prov, 4); }
}

/* A take of a frame already sent. P is signaled. At the wait's answer,
 * before the worker reads, X is stored with its gate part still to come,
 * so the read finds X and sends it. At that read's completion X's gate
 * part runs: 2.1.0 hands it over with the count at 0, and 533ca4a4 gets
 * its SetEvent, a left signal for a frame already read. Then Y is stored.
 * live: Y's gate part runs later. kill: the program dies before it, after
 * the worker's next turn. kill-early: Y is stored at X's completion, and
 * the program dies before Y's gate part. 533ca4a4 reads Y on X's left
 * signal. */
static Ctl *g_x6Ctl;
static int g_x6Early;
static void X6Complete(void)
{
    X6Signal(&g_x6Ctl->sdk);
    if (g_x6Early) X6Store(&g_x6Ctl->sdk, 0x50);
}

static int X6Answer(HmThread *t, DWORD result)
{
    Ctl *c = g_x6Ctl;
    if (t != CtlThread(c, 0) || result != WAIT_OBJECT_0 + 1) return 0;
    X6Store(&c->sdk, 0x30);
    g_completeHook = X6Complete;
    return 1;
}

static void StreamTakeSentThenNewer(PlanProfile *p, int kill)
{
    Ctl c;
    g_x6Early = kill == 2;
    StreamBegin("threads/%s/x6-take-sent-then-newer-%s", p->id, kill == 2 ? "kill-early" : kill ? "kill" : "live");
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    Probe(&c, 0, 1);
    HidRead(&c, len);
    g_x6Ctl = &c;
    g_waitAnswerHook = X6Answer;
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x20 + i);
    SdkWriteFrame(&c.sdk, data, 63, NULL, NULL, 0);
    RunWorker(&c);
    if (g_waitAnswerHook || g_completeHook) HmFail("x6: a hook never ran");
    HidRead(&c, len);
    if (!g_x6Early) X6Store(&c.sdk, 0x50);
    if (kill) {
        PipeServerClose(c.sdk.pipe);
        ThreadResume(CtlThread(&c, 0));
        ThreadResume(CtlThread(&c, 0));
        ThreadResume(CtlThread(&c, 0));
    } else {
        RunWorker(&c);
        X6Signal(&c.sdk);
        RunWorker(&c);
        RunWorker(&c);
        for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x90 + i);
        HidRead(&c, len);
        Frame(&c, data, 63, NULL, NULL, 0);
    }
    CtlStop(&c);
}

/* OwnerGone's release-bit rule. Program A (2.1.0) is killed with frame X in
 * the handoff, provisional, its exchange done and no SetEvent. The next
 * program B starts inside OwnerGone: at the first SRW lock a driver thread
 * takes after the wait answers the pipe's close, or at the pipe client's
 * close, whichever comes first. B zeroes the input section and the
 * acknowledgement, and under 2.1.0 clears the handoff, then writes B1,
 * handed or signaled. sameseq: X is A's first frame, SeqNo 2, which a B1
 * before 2.1.0 repeats. The record x6.bstart says where B started: 1 at
 * the SRW lock, 2 at the pipe client's close. */
static int g_x6Legacy, g_x6Done;
static void X6StartNext(void)
{
    if (g_x6Done) return;
    g_x6Done = 1;
    {
        uint32_t where = g_srwHook == NULL && g_pipeClientCloseHook != NULL ? 1u : 2u;
        RecOut("x6.bstart", &where, 4);
    }
    Ctl *c = g_x6Ctl;
    MapNextProgram(c, g_x6Legacy);
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x70 + i);
    SdkWriteFrame(&c->sdk, data, 63, NULL, NULL, 0);
}

static int X6OwnerAnswer(HmThread *t, DWORD result)
{
    if (t != CtlThread(g_x6Ctl, 0) || result != WAIT_OBJECT_0 + 2) return 0;
    g_srwHook = X6StartNext;
    g_pipeClientCloseHook = X6StartNext;
    return 1;
}

static void StreamKillNextInOwnerGone(PlanProfile *p, int legacy, int sameseq, int parked)
{
    Ctl c;
    StreamBegin("threads/%s/x6-kill-next-%s-in-ownergone%s-%s", p->id, legacy ? "legacy" : "gated",
                sameseq ? "-sameseq" : "", parked ? "parked" : "late");
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    if (!sameseq) Probe(&c, 0, 1);
    if (parked) HidRead(&c, len);
    X6Store(&c.sdk, 0xA0);
    X6ExchangeOnly(&c.sdk);
    g_x6Ctl = &c;
    g_x6Legacy = legacy;
    g_x6Done = 0;
    g_waitAnswerHook = X6OwnerAnswer;
    PipeServerClose(c.sdk.pipe);
    ThreadResume(CtlThread(&c, 0));
    g_srwHook = NULL;
    g_pipeClientCloseHook = NULL;
    if (!g_x6Done) HmFail("x6: the next program never started");
    ThreadResume(CtlThread(&c, 0));
    if (!parked) HidRead(&c, len);
    ThreadResume(CtlThread(&c, 0));
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x90 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    HidRead(&c, len);
    CtlStop(&c);
}

/* legacy-next-zero-read-polled: a program before 2.1.0 starts early as in
 * legacy-next-after-zero-read, with no read parked when the worker reads
 * the zeroed section. Then the polled input report, before and after the
 * next program's first frame. 533ca4a4 cached its all-zero report there,
 * and a polling caller stopped seeing the last program's report. */
static void StreamZeroReadPolled(PlanProfile *p)
{
    Ctl c;
    int saved = g_sdkSignal;
    g_sdkSignal = 0;
    StreamBegin("threads/%s/legacy-next-zero-read-polled", p->id);
    if (CtlStart(&c, p, 0, SDK_ALL, 0)) {
        uint8_t data[64];
        uint32_t len = c.p->readLength ? c.p->readLength : 64;
        KeepOneRead(&c);
        FrameData(data, 0x10);
        Frame(&c, data, 63, NULL, NULL, 0);
        HidGetInput(&c, 0x01, len);
        FrameData(data, 0x20);
        SdkWriteFrame(&c.sdk, data, 63, NULL, NULL, 0);
        g_zCtl = &c;
        g_waitAnswerHook = ZeroReadAnswer;
        RunWorker(&c);
        if (g_waitAnswerHook) HmFail("zero-read-polled: the wait never answered the input event");
        HidGetInput(&c, 0x01, len);
        HidGetInput(&c, 0x00, len);
        RunWorker(&c);
        HidGetInput(&c, 0x01, len);
        KeepOneRead(&c);
        FrameData(data, 0x70);
        Frame(&c, data, 63, NULL, NULL, 0);
        HidGetInput(&c, 0x01, len);
        KeepOneRead(&c);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}
/* x7: the take of a frame already sent against a newer frame, and a frame a killed program left in the handoff, taken at a loop top. */
static Ctl *g_x7Ctl;
static int g_x7Done, g_x7Mode, g_x7Legacy;

/* One action, run once, at the first of: this driver's zero-timeout poll
 * (g_pollHook), or a wait answer of the input event (g_waitAnswerHook), the
 * point after the worker's wake decision and before its read on 533ca4a4. */
static void X7Act(void)
{
    if (g_x7Done) return;
    g_x7Done = 1;
    Ctl *c = g_x7Ctl;
    if (g_x7Mode == 0) {
        X6Store(&c->sdk, 0x50);
    } else if (g_x7Mode == 1) {
        X6Store(&c->sdk, 0x50);
        X6Signal(&c->sdk);
    } else if (g_x7Mode == 2) {
        PipeServerClose(c->sdk.pipe);
        MapNextProgram(c, g_x7Legacy);
        X6Store(&c->sdk, 0x70);
    } else {
        /* A v1.11.0 program: no owner pipe and no acknowledgement. It
         * zeroes the input section and counts from 0. */
        PipeServerClose(c->sdk.pipe);
        memset(c->sdk.input->buf, 0, c->sdk.input->size);
        c->sdk.signal = NULL;
        c->sdk.seq = 0;
        X6Store(&c->sdk, 0x70);
    }
}
static int X7Answer(HmThread *t, DWORD result)
{
    if (t != CtlThread(g_x7Ctl, 0) || result != WAIT_OBJECT_0 + 1) return 0;
    X7Act();
    return 1;
}
static void X7Arm(int mode)
{
    g_x7Done = 0;
    g_x7Mode = mode;
    g_pollHook = X7Act;
    g_waitAnswerHook = X7Answer;
}
static void X7Disarm(void) { g_pollHook = NULL; g_waitAnswerHook = NULL; }

/* X read before its gate part: P signaled, X stored at the wait's answer,
 * X's gate part at the read's completion (signalX) or left for later. A
 * stop event can be set there too (stopAfter). */
static int g_x7SignalX, g_x7StopAfter;
static void X7Complete(void)
{
    Ctl *c = g_x7Ctl;
    if (g_x7SignalX) X6Signal(&c->sdk);
    if (g_x7StopAfter) {
        HmObj *o = ObjFromHandle(c->dev->threads[0]->waitHandles[0]);
        if (!o || o->kind != HK_EVENT || !((HmEvent *)o)->manual) HmFail("x7: no stop event");
        EventSet((HmEvent *)o);
    }
}
static int g_x7Arm1AfterX, g_x7ArmMode = 1;
static int X7AnswerX(HmThread *t, DWORD result)
{
    Ctl *c = g_x7Ctl;
    if (t != CtlThread(c, 0) || result != WAIT_OBJECT_0 + 1) return 0;
    X6Store(&c->sdk, 0x30);
    g_completeHook = X7Complete;
    return 1;
}
static void X7ReadXEarly(Ctl *c, int signalX)
{
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x20 + i);
    g_x7SignalX = signalX;
    g_waitAnswerHook = X7AnswerX;
    SdkWriteFrame(&c->sdk, data, 63, NULL, NULL, 0);
    RunWorker(c);
    if (g_waitAnswerHook || g_completeHook) HmFail("x7: a hook never ran");
}

/* x6-take-sent-then-newer-kill with Y stored at the same point on both
 * drivers: after the wake decision (this driver's take of the sent X, 533ca4a4's
 * left signal) and before the read (before-read), or after that read
 * (after-read). Then the program dies before Y's gate part. */
static int X7AnswerVariant(HmThread *t, DWORD result);
static void StreamX7KillOrder(PlanProfile *p, int afterRead)
{
    Ctl c;
    StreamBegin("threads/%s/x7-take-sent-newer-%s-kill", p->id, afterRead ? "after-read" : "before-read");
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    uint8_t data[64];
    Probe(&c, 0, 1);
    HidRead(&c, len);
    g_x7Ctl = &c;
    g_x7StopAfter = 0;
    if (afterRead) {
        X7ReadXEarly(&c, 1);
        HidRead(&c, len);
        ThreadResume(CtlThread(&c, 0));
        X6Store(&c.sdk, 0x50);
    } else {
        for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x20 + i);
        g_x7Arm1AfterX = 1;
        g_x7ArmMode = 0;
        g_waitAnswerHook = X7AnswerVariant;
        SdkWriteFrame(&c.sdk, data, 63, NULL, NULL, 0);
        RunWorker(&c);
        g_x7ArmMode = 1;
        HidRead(&c, len);
        ThreadResume(CtlThread(&c, 0));
        if (!g_x7Done) HmFail("x7: Y never stored");
    }
    X7Disarm();
    PipeServerClose(c.sdk.pipe);
    ThreadResume(CtlThread(&c, 0));
    ThreadResume(CtlThread(&c, 0));
    CtlStop(&c);
}

/* a take of a sent frame X racing the newer frame Y's own gate part,
 * with the program alive. Y is stored and its gate part run: before the
 * worker's next take (at X's completion), between the take and the read
 * (533ca4a4: between its wake and read), or after the read. Gated (2.1.0)
 * and ungated (a program before 2.1.0, which signals every frame). */
static void X7CompleteBefore(void)
{
    Ctl *c = g_x7Ctl;
    X6Signal(&c->sdk);
    X6Store(&c->sdk, 0x50);
    X6Signal(&c->sdk);
}
static void X7CompleteThenArm(void)
{
    X6Signal(&g_x7Ctl->sdk);
    X7Arm(g_x7ArmMode);
}
static int X7AnswerVariant(HmThread *t, DWORD result)
{
    Ctl *c = g_x7Ctl;
    if (t != CtlThread(c, 0) || result != WAIT_OBJECT_0 + 1) return 0;
    X6Store(&c->sdk, 0x30);
    g_completeHook = g_x7Arm1AfterX ? X7CompleteThenArm : X7CompleteBefore;
    return 1;
}
static void StreamX7Race(PlanProfile *p, int when, int legacy)
{
    Ctl c;
    int saved = g_sdkSignal;
    if (legacy) g_sdkSignal = 0;
    static const char *names[] = { "before-take", "in-take-read", "after-read" };
    StreamBegin("threads/%s/x7-take-sent-race-%s%s", p->id, names[when], legacy ? "-legacy" : "");
    if (CtlStart(&c, p, 0, SDK_ALL, 0)) {
        uint32_t len = c.p->readLength ? c.p->readLength : 64;
        uint8_t data[64];
        Probe(&c, 0, 1);
        HidRead(&c, len);
        g_x7Ctl = &c;
        g_x7StopAfter = 0;
        if (when == 2) {
            X7ReadXEarly(&c, 1);
        } else {
            for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x20 + i);
            g_x7Arm1AfterX = when == 1;
            g_waitAnswerHook = X7AnswerVariant;
            SdkWriteFrame(&c.sdk, data, 63, NULL, NULL, 0);
            RunWorker(&c);
            if (g_completeHook) HmFail("x7: the completion hook never ran");
        }
        HidRead(&c, len);
        for (int i = 0; i < 2; i++) ThreadResume(CtlThread(&c, 0));
        if (when == 1 && !g_x7Done) X7Act();
        if (when == 2) { X6Store(&c.sdk, 0x50); X6Signal(&c.sdk); }
        X7Disarm();
        for (int i = 0; i < 3; i++) ThreadResume(CtlThread(&c, 0));
        HidRead(&c, len);
        for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x90 + i);
        Frame(&c, data, 63, NULL, NULL, 0);
        HidRead(&c, len);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

/* Recycle: X read early and its gate part run, then a foreign stop
 * before the worker's next take. This driver takes X, signals it back and
 * recycles. 533ca4a4 returns the stop with X's left signal set and recycles.
 * Both forget SeenSeqNo there. */
static void StreamX7Recycle(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/x7-take-sent-then-foreign-stop", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    Probe(&c, 0, 1);
    HidRead(&c, len);
    g_x7Ctl = &c;
    g_x7StopAfter = 1;
    X7ReadXEarly(&c, 1);
    g_x7StopAfter = 0;
    HidRead(&c, len);
    for (int i = 0; i < 3; i++) ThreadResume(CtlThread(&c, 0));
    HidRead(&c, len);
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x90 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    CtlStop(&c);
}

/* A frame left in the handoff by a program killed between its exchange and
 * its compare-exchange, with the worker counted, then taken at a loop top
 * (here after a timeout) before the pipe's close is seen. The take drops
 * the provisional SeqNo. The program's pipe then closes and the next
 * program B starts and stores B1 (on 533ca4a4, after its timeout wake,
 * before its next wait's answer). B1's SetEvent comes after OwnerGone.
 * sent: X was read before its exchange. v111: the next program is a
 * v1.11.0 one, with no owner pipe and no acknowledgement. */
static void StreamX7KillStuck(PlanProfile *p, int sent, int legacy)
{
    Ctl c;
    StreamBegin("threads/%s/x7-kill-stuck-%s-next-%s-at-take", p->id, sent ? "sent" : "unsent", legacy == 2 ? "v111" : legacy ? "legacy" : "gated");
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    Probe(&c, 0, 1);
    HidRead(&c, len);
    g_x7Ctl = &c;
    g_x7StopAfter = 0;
    if (sent) {
        X7ReadXEarly(&c, 0);
        HidRead(&c, len);
    } else {
        X6Store(&c.sdk, 0x30);
    }
    X6ExchangeOnly(&c.sdk);
    g_x7Legacy = legacy != 0;
    X7Arm(legacy == 2 ? 3 : 2);
    g_waitAnswerHook = NULL;
    ThreadResume(CtlThread(&c, 0));
    X7Act();
    X7Disarm();
    ThreadResume(CtlThread(&c, 0));
    HidRead(&c, len);
    X6Signal(&c.sdk);
    RunWorker(&c);
    RunWorker(&c);
    HidRead(&c, len);
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x90 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    HidRead(&c, len);
    CtlStop(&c);
}

/* x7-kill-stuck-unsent-alone-at-take: as x7-kill-stuck-unsent-*, with no
 * next program. The frame a program left in the handoff as it died is
 * taken at a loop top before the pipe's close is seen. The take drops the
 * provisional SeqNo, so the frame is never sent, as on v2.0.3, whose
 * program died before its SetEvent in the same order. */
static void StreamX7KillStuckAlone(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/x7-kill-stuck-unsent-alone-at-take", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    Probe(&c, 0, 1);
    HidRead(&c, len);
    X6Store(&c.sdk, 0x30);
    X6ExchangeOnly(&c.sdk);
    ThreadResume(CtlThread(&c, 0));
    PipeServerClose(c.sdk.pipe);
    ThreadResume(CtlThread(&c, 0));
    ThreadResume(CtlThread(&c, 0));
    HidRead(&c, len);
    ThreadResume(CtlThread(&c, 0));
    CtlStop(&c);
}

/* x8: a frame left in the handoff before its program's recheck, and the next program's own handoff at the switch between programs. */
/* The program, stopped between its exchange and its recheck, resumes: the
 * recheck and the compare-exchange. A take dropped the provisional SeqNo,
 * so the compare-exchange fails and the frame is signaled. On a driver
 * without the capability this is the SetEvent after the store. */
static void X6Resume(Sdk *s)
{
    int input = 1;
    if (s->signal) {
        int32_t caps, waiters, handoff, fin = (int32_t)s->seq, prov = fin | 1;
        memcpy(&caps, s->signal->buf + 4, 4);
        memcpy(&handoff, s->signal->buf + 68, 4);
        if ((caps & 1) || handoff == prov) {
            memcpy(&waiters, s->signal->buf + 64, 4);
            if ((caps & 1) && waiters == 0) {
                if (handoff == prov) { memcpy(s->signal->buf + 68, &fin, 4); input = 0; }
            } else if (handoff == prov) {
                memset(s->signal->buf + 68, 0, 4);
            }
        }
    }
    if (input) EventSet(s->inputEv);
}
static Ctl *g_x8Ctl;
static int g_x8Mode, g_x8Done;

/* What another process does at the worker's zero-timeout poll (this
 * driver) or, on a driver that never polls, from the scenario at the same
 * point after the worker's timeout wake. */
static void X8Act(void)
{
    if (g_x8Done) return;
    g_x8Done = 1;
    Ctl *c = g_x8Ctl;
    uint32_t left;
    switch (g_x8Mode) {
    case 0:
        /* A's pipe closes and a 2.1.0 B starts: input zeroed, handoff
         * cleared, B1 stored at left + 2. B's acknowledgement zeroing is
         * left for later: the worker's load of the release bit came before
         * it, its load of the section after B1's store. */
        PipeServerClose(c->sdk.pipe);
        memcpy(&left, c->sdk.input->buf, 4);
        memset(c->sdk.input->buf, 0, c->sdk.input->size);
        if (c->sdk.signal) memset(c->sdk.signal->buf + 68, 0, 4);
        c->sdk.seq = (left & 1) ? left + 1 : left;
        X6Store(&c->sdk, 0x70);
        break;
    case 1:
        /* A's pipe closes and a v1.11.0 B starts: input zeroed, no
         * acknowledgement and no pipe, B1 stored at SeqNo 2. */
        PipeServerClose(c->sdk.pipe);
        memset(c->sdk.input->buf, 0, c->sdk.input->size);
        c->sdk.signal = NULL;
        c->sdk.seq = 0;
        X6Store(&c->sdk, 0x70);
        break;
    case 2:
        /* The live program, preempted between X's exchange and its
         * recheck, resumes: X's compare-exchange finds the provisional
         * SeqNo taken and signals X, and X+2 and X+4 run whole. */
        X6Resume(&c->sdk);
        X6Store(&c->sdk, 0x50);
        X6Signal(&c->sdk);
        X6Store(&c->sdk, 0x60);
        X6Signal(&c->sdk);
        break;
    case 3:
        /* As 2, killed between X+4's store and its exchange. */
        X6Resume(&c->sdk);
        X6Store(&c->sdk, 0x50);
        X6Signal(&c->sdk);
        X6Store(&c->sdk, 0x60);
        break;
    }
}
static void X8Arm(int mode) { g_x8Done = 0; g_x8Mode = mode; g_pollHook = X8Act; }

/* A frame X in the handoff with the worker counted and no SetEvent
 * (killed, or preempted, between its exchange and its recheck), the worker
 * at a loop top before it sees anything else (a timeout here), and
 * something happening between its take and its read. */
static void StreamX8(PlanProfile *p, int mode, int probe)
{
    static const char *names[] = { "next-gated-between-bit-and-section", "next-v1110-repeats-taken",
                                   "live-overwrite-skips", "kill-overwrite-skips" };
    Ctl c;
    StreamBegin("threads/%s/x8-%s%s", p->id, names[mode], probe ? "" : "-first");
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    if (probe) Probe(&c, 0, 1);
    HidRead(&c, len);
    g_x8Ctl = &c;
    X6Store(&c.sdk, 0x30);
    X6ExchangeOnly(&c.sdk);
    X8Arm(mode);
    ThreadResume(CtlThread(&c, 0));
    X8Act();
    g_pollHook = NULL;
    if (mode == 0) memset(c.sdk.ack->buf, 0, c.sdk.ack->size);
    ThreadResume(CtlThread(&c, 0));
    HidRead(&c, len);
    if (mode <= 1) X6Signal(&c.sdk);
    RunWorker(&c);
    RunWorker(&c);
    HidRead(&c, len);
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x90 + i);
    if (mode != 3) Frame(&c, data, 63, NULL, NULL, 0);
    HidRead(&c, len);
    CtlStop(&c);
}

/* Two contexts, or a removal that did not take: the sweep that removed the
 * index's other device clears the acknowledgement's release and delivered
 * bits and the gate's capabilities for the index the program still holds
 * (SharedMemoryIO.ForgetDriverLocked), and this device context lives on
 * with its watch connected. AnnounceGate sets the gate's capability again
 * at the 256th frame, the release bit stays clear. Then a frame handed over
 * while the worker completes a parked read, and nothing after it: the take
 * at the next loop top reads it for the read that follows. */
static void X8HandY(void)
{
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0xB0 + i);
    SdkWriteFrame(&g_x8Ctl->sdk, data, 63, NULL, NULL, 0);
}
static void StreamX8Forgotten(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/x8-forget-driver-then-handed-frame", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    Probe(&c, 0, 1);
    int32_t caps;
    memcpy(&caps, c.sdk.ack->buf + 4, 4);
    caps &= ~3;
    memcpy(c.sdk.ack->buf + 4, &caps, 4);
    if (c.sdk.signal) { memset(c.sdk.signal->buf + 4, 0, 4); memset(c.sdk.signal->buf + 8, 0, 4); }
    uint8_t data[64];
    for (int f = 0; f < 260; f++) {
        for (int i = 0; i < 64; i++) data[i] = (uint8_t)(f + i);
        Frame(&c, data, 63, NULL, NULL, 0);
    }
    int32_t dc = 0;
    if (c.sdk.signal) memcpy(&dc, c.sdk.signal->buf + 4, 4);
    RecOut("x8.drivercaps", &dc, 4);
    memcpy(&caps, c.sdk.ack->buf + 4, 4);
    RecOut("x8.ackcaps", &caps, 4);
    HidRead(&c, len);
    HidRead(&c, len);
    g_x8Ctl = &c;
    g_completeHook = X8HandY;
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0xA0 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    if (g_completeHook) HmFail("x8: the completion hook never ran");
    HidRead(&c, len);
    for (int i = 0; i < 3; i++) ThreadResume(CtlThread(&c, 0));
    CtlStop(&c);
}

/* Issue #67 with a frame left provisional. A keyboard-style program
 * holds a key, its release frame queued. It is killed between X's exchange
 * (key down) and its recheck, with the worker counted. The worker reaches
 * its take before it sees the pipe close (a timeout here) and drops the
 * provisional SeqNo, so X is never sent, as on 533ca4a4, where the program
 * died before its SetEvent. The release frame goes out last. */
static void StreamX8StuckAloneRelease(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/x8-stuck-alone-release-last", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    Probe(&c, 0, 1);
    uint8_t *a = c.sdk.ack->buf;
    int32_t seq = 2, cnt = 1, sz = 78;
    memcpy(a + 8, &seq, 4);
    memcpy(a + 12, &cnt, 4);
    memcpy(a + 16, &sz, 4);
    memset(a + 20, 0, 80);
    a[20] = 0x01;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    HidRead(&c, len);
    HidRead(&c, len);
    X6Store(&c.sdk, 0xA0);
    X6ExchangeOnly(&c.sdk);
    ThreadResume(CtlThread(&c, 0));
    PipeServerClose(c.sdk.pipe);
    ThreadResume(CtlThread(&c, 0));
    ThreadResume(CtlThread(&c, 0));
    HidRead(&c, len);
    ThreadResume(CtlThread(&c, 0));
    CtlStop(&c);
}

/* The switch between programs with the worker slow: while it completes a
 * read for A's frame, A ends, B (2.1.0) starts and writes B1, which is
 * handed over with no SetEvent since the worker is not counted. The worker
 * takes B1 before it handles A's pipe close. v2.0.3 reads B1 on its
 * SetEvent before the close and delivers it. */
static void X8SwitchHook(void)
{
    Ctl *c = g_x8Ctl;
    PipeServerClose(c->sdk.pipe);
    MapNextProgram(c, 0);
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x70 + i);
    SdkWriteFrame(&c->sdk, data, 63, NULL, NULL, 0);
}
static void StreamX8NextHandedAtTake(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/x8-next-handed-at-take-before-ownergone", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    Probe(&c, 0, 1);
    HidRead(&c, len);
    g_x8Ctl = &c;
    g_completeHook = X8SwitchHook;
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x30 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    if (g_completeHook) HmFail("x8: the switch hook never ran");
    HidRead(&c, len);
    for (int i = 0; i < 3; i++) ThreadResume(CtlThread(&c, 0));
    HidRead(&c, len);
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x90 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    HidRead(&c, len);
    CtlStop(&c);
}

/* x9: the first atomic step of a frame's handoff, then the program dies.
 * With a final SeqNo still waiting for its take, that step is the one
 * compare-exchange that puts this frame's final SeqNo in its place.
 * Otherwise it is the exchange of the provisional SeqNo. On a driver
 * without the capability nothing is written: the program died before its
 * SetEvent. */
static void X9FirstStep(Sdk *s)
{
    int32_t caps = 0, handoff;
    if (!s->signal) return;
    memcpy(&caps, s->signal->buf + 4, 4);
    if (!(caps & 1)) return;
    memcpy(&handoff, s->signal->buf + 68, 4);
    uint32_t v = handoff != 0 && (handoff & 1) == 0 ? s->seq : (s->seq | 1);
    memcpy(s->signal->buf + 68, &v, 4);
}

/* x9-newer-first-step-then-kill: X is read before its handoff lands, and
 * X's handoff completes while the worker is busy, so X is final and its
 * take still to come (v2.0.3: X's SetEvent came after the read, a left
 * signal). The program stores Y, takes the first step of Y's handoff and
 * dies. The final SeqNo Y replaced is the read X's take owed, and that
 * read sends Y, as 533ca4a4's read on X's left signal does. idle: the same
 * with X's handoff done and taken already, and the first step Y's
 * provisional exchange: nothing sends Y, as on 533ca4a4, where no signal
 * was left. */
static Ctl *g_x9Ctl;
static void X9Complete(void)
{
    Ctl *c = g_x9Ctl;
    X6Signal(&c->sdk);
    X6Store(&c->sdk, 0x50);
    X9FirstStep(&c->sdk);
}
static int X9Answer(HmThread *t, DWORD result)
{
    Ctl *c = g_x9Ctl;
    if (t != CtlThread(c, 0) || result != WAIT_OBJECT_0 + 1) return 0;
    X6Store(&c->sdk, 0x30);
    g_completeHook = X9Complete;
    return 1;
}
static void StreamX9FirstStepKill(PlanProfile *p, int idle)
{
    Ctl c;
    StreamBegin("threads/%s/x9-newer-first-step-then-kill%s", p->id, idle ? "-idle" : "");
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    uint8_t data[64];
    Probe(&c, 0, 1);
    HidRead(&c, len);
    g_x9Ctl = &c;
    if (idle) {
        for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x30 + i);
        Frame(&c, data, 63, NULL, NULL, 0);
        HidRead(&c, len);
        ThreadResume(CtlThread(&c, 0));
        X6Store(&c.sdk, 0x50);
        X9FirstStep(&c.sdk);
    } else {
        for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x20 + i);
        g_waitAnswerHook = X9Answer;
        SdkWriteFrame(&c.sdk, data, 63, NULL, NULL, 0);
        RunWorker(&c);
        if (g_waitAnswerHook || g_completeHook) HmFail("x9: a hook never ran");
        HidRead(&c, len);
    }
    PipeServerClose(c.sdk.pipe);
    for (int i = 0; i < 3; i++) ThreadResume(CtlThread(&c, 0));
    HidRead(&c, len);
    ThreadResume(CtlThread(&c, 0));
    CtlStop(&c);
}
/* x10-idle-window-next-late-read: A ends and OwnerGone runs. B starts
 * inside the idle window, before any recycle, so the worker keeps the
 * input event it holds and maps nothing. B1 is read with no read parked,
 * then B ends and a read comes. The watch is retried at a map or every
 * 256th frame, as on 533ca4a4, so B is not watched, no OwnerGone resets the
 * delivered gate, and the read takes B1 from the cache. */
static void StreamX10IdleWindowNext(PlanProfile *p, int legacy)
{
    Ctl c;
    StreamBegin("threads/%s/x10-idle-window-next-late-read%s", p->id, legacy ? "-legacy" : "");
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    uint8_t data[64];
    Probe(&c, 0, 1);
    PipeServerClose(c.sdk.pipe);
    ThreadResume(CtlThread(&c, 0));
    MapNextProgram(&c, legacy);
    Hold(&c.sdk.pipe->o);
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x70 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    PipeServerClose(c.sdk.pipe);
    ThreadResume(CtlThread(&c, 0));
    ThreadResume(CtlThread(&c, 0));
    HidRead(&c, len);
    HidGetInput(&c, 0x01, len);
    CtlStop(&c);
}

/* x10-take-owner-at-teardown: while the worker completes a read, A's frame
 * X is handed over final and A's pipe closes. The worker takes X, and its
 * device's teardown has set TearingDown, with the stop event still to come,
 * when it polls: the poll answers the pipe's close. The queues are purged by
 * then, so the worker signals X back instead of reading it, and the device
 * added again reads X for its read. 533ca4a4's worker, still before its
 * wait, leaves X's signal set for the next device the same way. */
static void X10TearAtPoll(void)
{
    Ctl *c = g_x9Ctl;
    *(volatile LONG *)((uint8_t *)c->dev->context + HmTearingDownOffset) = 1;
}
static void X10HandAndClose(void)
{
    Ctl *c = g_x9Ctl;
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x30 + i);
    SdkWriteFrame(&c->sdk, data, 63, NULL, NULL, 0);
    PipeServerClose(c->sdk.pipe);
    g_pollHook = X10TearAtPoll;
}
static void StreamX10TakeOwnerTeardown(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/x10-take-owner-at-teardown", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    uint8_t data[64];
    Probe(&c, 0, 1);
    HidRead(&c, len);
    g_x9Ctl = &c;
    g_completeHook = X10HandAndClose;
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x20 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    if (g_completeHook) HmFail("x10: the completion hook never ran");
    g_pollHook = NULL;
    DeviceRemove(c.dev);
    ConfigWrite(p, 0);
    c.dev = DeviceAdd(0);
    if (!c.dev) HmFail("x10: re-add failed");
    HidRead(&c, len);
    RunWorker(&c);
    RunWorker(&c);
    HidRead(&c, len);
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x90 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    CtlStop(&c);
}

/* x10-next-handed-at-take-stop: x8-next-handed-at-take-before-ownergone
 * with a foreign stop set beside the pipe's close. The worker takes B1,
 * its poll answers the stop, and it signals B1 back and recycles: Phase 1
 * runs OwnerGone, then reads B1 once. */
static void X10SwitchStop(void)
{
    Ctl *c = g_x8Ctl;
    X8SwitchHook();
    HmObj *o = ObjFromHandle(c->dev->threads[0]->waitHandles[0]);
    if (!o || o->kind != HK_EVENT || !((HmEvent *)o)->manual) HmFail("x10: no stop event");
    EventSet((HmEvent *)o);
}
static void StreamX10NextHandedStop(PlanProfile *p)
{
    Ctl c;
    StreamBegin("threads/%s/x10-next-handed-at-take-stop", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t len = c.p->readLength ? c.p->readLength : 64;
    Probe(&c, 0, 1);
    HidRead(&c, len);
    g_x8Ctl = &c;
    g_completeHook = X10SwitchStop;
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x30 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    if (g_completeHook) HmFail("x10: the switch hook never ran");
    HidRead(&c, len);
    for (int i = 0; i < 3; i++) ThreadResume(CtlThread(&c, 0));
    HidRead(&c, len);
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(0x90 + i);
    Frame(&c, data, 63, NULL, NULL, 0);
    HidRead(&c, len);
    CtlStop(&c);
}
void CaseThreads(void)
{
    PlanProfile *sw = PlanFind("switch", NULL);
    PlanProfile *ds = NULL;
    for (int i = 0; i < g_plan.profileCount && !ds; i++)
        if (!strcmp(g_plan.profiles[i].group, "sony") && g_plan.profiles[i].bluetooth)
            ds = &g_plan.profiles[i];
    PlanProfile *xusb = PlanFind("xusb", NULL);
    if (!sw || !ds || !xusb) HmFail("the plan lacks a profile the threads case needs");
    StreamPhase1(sw, 1);
    StreamPhase1(ds, 0);
    StreamIdleRecycle(sw, 1);
    StreamIdleRecycle(ds, 0);
    StreamStaleRecycle(sw, 1);
    StreamStaleRecycle(ds, 0);
    StreamPump(xusb);
    StreamGateIdle(ds);
    StreamGateOwnerGone(ds);
    StreamGateRelease(ds);
    StreamGateSeqWrap(ds);
    StreamLegacySeqWrap(ds);
    StreamLateReadWrap(ds, 0);
    StreamLateReadWrap(ds, 1);
    StreamNextProgramInOwnerGone(ds, 1, 0);
    StreamNextProgramInOwnerGone(ds, 0, 0);
    StreamNextProgramInOwnerGone(ds, 1, 1);
    StreamNextProgramInOwnerGone(ds, 0, 1);
    for (int legacy = 1; legacy >= 0; legacy--)
        for (int parked = 0; parked < 2; parked++)
            StreamNextProgramBeforeTake(ds, legacy, parked);
    for (int m = 0; m < 3; m++) { StreamX5(ds, m, 0); StreamX5(ds, m, 1); }
    StreamX5GateOff(ds);
    StreamX5Kill(ds, 1);
    StreamX5Kill(ds, 0);
    StreamZeroReadNext(ds);
    StreamTwoProgramLate(ds, 0);
    StreamTwoProgramLate(ds, 1);
    StreamZeroReadPolled(ds);
    for (int kill = 0; kill < 3; kill++) StreamTakeSentThenNewer(ds, kill);
    for (int lg = 0; lg < 2; lg++)
        for (int ss = 0; ss < 2; ss++)
            for (int pk = 0; pk < 2; pk++) StreamKillNextInOwnerGone(ds, lg, ss, pk);
    StreamX7KillOrder(ds, 0);
    StreamX7KillOrder(ds, 1);
    for (int w = 0; w < 3; w++) { StreamX7Race(ds, w, 0); StreamX7Race(ds, w, 1); }
    StreamX7Recycle(ds);
    for (int st = 0; st < 2; st++) { StreamX7KillStuck(ds, st, 1); StreamX7KillStuck(ds, st, 0); }
    StreamX7KillStuckAlone(ds);
    for (int st = 0; st < 2; st++) StreamX7KillStuck(ds, st, 2);
    StreamX8(ds, 0, 1);
    StreamX8(ds, 1, 1);
    StreamX8(ds, 1, 0);
    StreamX8(ds, 2, 1);
    StreamX8(ds, 3, 1);
    StreamX8Forgotten(ds);
    StreamX8StuckAloneRelease(ds);
    StreamX8NextHandedAtTake(ds);
    StreamX9FirstStepKill(ds, 0);
    StreamX9FirstStepKill(ds, 1);
    StreamX10IdleWindowNext(ds, 0);
    StreamX10IdleWindowNext(ds, 1);
    StreamX10TakeOwnerTeardown(ds);
    StreamX10NextHandedStop(ds);
}
