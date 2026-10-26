/* The --wake mode: a read that parks beside a frame published while it
 * parks, checked rather than recorded, at each order the harness's hooks
 * can place on real driver code.
 *
 * IOCTL_HID_READ_REPORT looks at the cache under InputLock, and parks in the
 * manual queue when nothing new is there. ProcessSharedInput takes a parked
 * read from the queue and caches its frame under InputLock. A read whose
 * look comes before the cache write and whose park comes after the worker's
 * look at the queue waits beside the frame until the next one (driver.c,
 * v2.0.3 and b0fb7dc9 alike). The same holds for the release frames
 * OwnerGone queues, and in the XUSB companion for a WAIT_FOR_INPUT that
 * parks after the pump took a frame's doorbell with nothing parked.
 *
 * Each scenario places one order with a hook (hm_wdf.c g_forwardHook,
 * g_retrieveHook and g_requeueHook, hm_win32.c g_lockHook and
 * g_lockReleaseHook) and checks:
 *   - the read waiting when a frame came carries it, or the newest frame
 *     cached when it was taken,
 *   - no frame goes to two reads, or to two WAIT_FOR_INPUT completions,
 *   - and, as the positive control in the same scenario, a read parked
 *     before a frame carries it, and the next frame reaches the read left
 *     waiting.
 * The companion's 8 ms timer never runs here: it serves whatever is
 * current to whatever is parked, by design, and would hide a lost doorbell.
 * Exit 1 when a check fails, with every scenario run and the output file
 * complete, as --lifecycle does. */
#include "hm_scenario.h"

#define IOCTL_XUSB_WAIT_FOR_INPUT   0x8000E3AC

static int g_failed;
static int g_checks;

static void Check(const char *label, const char *what, int ok)
{
    g_checks++;
    printf("[%s] %s: %s\n", ok ? "PASS" : "FAIL", label, what);
    if (!ok) {
        g_failed++;
        fprintf(stderr, "[FAIL] %s: %s\n", label, what);
    }
    uint8_t rec = (uint8_t)(ok ? 1 : 0);
    RecOut("wake.check", &rec, 1);
}

/* ------------------------------------------------------------------ */
/*  The HID driver's read path                                         */
/* ------------------------------------------------------------------ */

static Ctl *g_w;
static int g_wHooks;
static uint8_t g_wBase;
static HmRequest *g_reads[16];
static int g_readCount;

static uint32_t ReadLen(Ctl *c) { return c->p->readLength ? c->p->readLength : 64; }

static HmRequest *Read(Ctl *c)
{
    HmRequest *r = HidRead(c, ReadLen(c));
    if (g_readCount < (int)ARRAYSIZE(g_reads)) g_reads[g_readCount++] = r;
    return r;
}

static void Write(Ctl *c, uint8_t base)
{
    uint8_t d[64];
    for (int i = 0; i < 64; i++) d[i] = (uint8_t)(base + i);
    SdkWriteFrame(&c->sdk, d, 63, NULL, NULL, 0);
}

/* A read completed with the frame whose data starts at base: the driver
 * puts the report id in front of the data on a descriptor with ids. */
static int Carries(Ctl *c, const HmRequest *r, uint8_t base)
{
    uint32_t at = c->p->usesReportIds ? 1u : 0u;
    return r && r->completed && !r->refused && r->info > at + 2
        && r->out[at] == base && r->out[at + 1] == (uint8_t)(base + 1) && r->out[at + 2] == (uint8_t)(base + 2);
}

static int CarriedBy(Ctl *c, uint8_t base)
{
    int n = 0;
    for (int i = 0; i < g_readCount; i++) n += Carries(c, g_reads[i], base);
    return n;
}

/* The release frame of gate-release-last: report 0x01, the rest zero. */
static const uint32_t k_releaseSize = 78;
static int CarriesRelease(const HmRequest *r)
{
    if (!r || !r->completed || r->info != k_releaseSize || r->out[0] != 0x01) return 0;
    for (uint32_t i = 1; i < k_releaseSize; i++) if (r->out[i]) return 0;
    return 1;
}

static void QueueRelease(Ctl *c)
{
    uint8_t *a = c->sdk.ack->buf;
    int32_t seq = 2, cnt = 1, sz = (int32_t)k_releaseSize;
    memcpy(a + 8, &seq, 4);
    memcpy(a + 12, &cnt, 4);
    memcpy(a + 16, &sz, 4);
    memset(a + 20, 0, 80);
    a[20] = 0x01;
}

static int Signaled(const HmThread *t)
{
    for (DWORD i = 0; i < t->waitCount; i++) {
        HmObj *o = ObjFromHandle(t->waitHandles[i]);
        if (o && o->kind == HK_EVENT && ((HmEvent *)o)->signaled) return 1;
    }
    return 0;
}

/* Runs a thread while one of its wait's events is set, so the scenario
 * checks a state where nothing the thread waits for is pending. A timeout
 * is never answered. */
static void Settle(HmThread *t)
{
    for (int i = 0; i < 16 && !t->exited && Signaled(t); i++) ThreadResume(t);
}

static int StartHid(Ctl *c, PlanProfile *p, const char *name, const char *label)
{
    StreamBegin("wake/%s/%s", p->id, name);
    g_w = c;
    g_wHooks = 0;
    g_readCount = 0;
    if (!CtlStart(c, p, 0, SDK_ALL, 0)) { Check(label, "the device add succeeds", 0); return 0; }
    /* Frame A through a read parked before it: the path every check below
     * is set against. */
    Read(c);
    Write(c, 0x10);
    RunWorker(c);
    Check(label, "control: a read parked before frame A carries A", Carries(c, g_reads[0], 0x10));
    return 1;
}

/* The next frame reaches the read left waiting, or one sent now. */
static void ControlFrame(Ctl *c, const char *label, uint8_t base)
{
    if (Parked(c) == 0) Read(c);
    Write(c, base);
    RunWorker(c);
    Settle(CtlThread(c, 0));
    Check(label, "control: the next frame reaches the read waiting", CarriedBy(c, base) == 1);
}

static void HookWriteB(void)
{
    g_wHooks++;
    Write(g_w, g_wBase);
    RunWorker(g_w);
}

/* read-parks-after-publish: the read finds nothing new, and before it
 * parks the SDK writes frame B and the worker runs its whole frame path:
 * no read in the queue yet, B cached. Then the read parks. */
static void ParksAfterPublish(PlanProfile *p, int legacy)
{
    Ctl c;
    char label[160];
    int saved = g_sdkSignal;
    if (legacy) g_sdkSignal = 0;
    snprintf(label, sizeof(label), "%s read-parks-after-publish%s", p->id, legacy ? "-legacy" : "");
    if (StartHid(&c, p, legacy ? "read-parks-after-publish-legacy" : "read-parks-after-publish", label)) {
        g_wBase = 0x40;
        g_forwardHook = HookWriteB;
        HmRequest *r = Read(&c);
        g_forwardHook = NULL;
        Check(label, "the worker cached frame B between the read's look and its park", g_wHooks == 1);
        Settle(CtlThread(&c, 0));
        Check(label, "the read waiting when frame B came carries B", Carries(&c, r, 0x40));
        Check(label, "frame B goes to exactly one read", CarriedBy(&c, 0x40) == 1);
        ControlFrame(&c, label, 0x70);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

static void HookRead(void)
{
    g_wHooks++;
    Read(g_w);
}

/* read-at-worker-lock: frame B is written, and the read comes at the
 * worker's first lock in its frame path: after the worker looked at the
 * queue and before it cached B in b0fb7dc9 and 533ca4a4, before the cache
 * write and the queue in this driver. */
static void ReadAtWorkerLock(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s read-at-worker-lock", p->id);
    if (!StartHid(&c, p, "read-at-worker-lock", label)) return;
    Write(&c, 0x40);
    g_lockHook = HookRead;
    RunWorker(&c);
    g_lockHook = NULL;
    Check(label, "the read came inside the worker's frame path", g_wHooks == 1 && g_readCount == 2);
    Settle(CtlThread(&c, 0));
    Check(label, "the read waiting when frame B was cached carries B", Carries(&c, g_reads[1], 0x40));
    Check(label, "frame B goes to exactly one read", CarriedBy(&c, 0x40) == 1);
    ControlFrame(&c, label, 0x70);
    CtlStop(&c);
}

static void HookCloseOwner(void)
{
    g_wHooks++;
    PipeServerClose(g_w->sdk.pipe);
    RunWorker(g_w);
}

/* read-parks-after-ownergone: a keyboard's release frame is in the
 * acknowledgement. The read finds nothing queued, and before it parks the
 * owner pipe closes and the worker runs OwnerGone, which queues the release
 * frame and finds no read parked. Then the read parks. The key the
 * program held stays down until the read gets the release frame. */
static void ParksAfterOwnerGone(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s read-parks-after-ownergone", p->id);
    if (!StartHid(&c, p, "read-parks-after-ownergone", label)) return;
    QueueRelease(&c);
    g_forwardHook = HookCloseOwner;
    HmRequest *r = Read(&c);
    g_forwardHook = NULL;
    Check(label, "the worker ran OwnerGone between the read's look and its park", g_wHooks == 1);
    Settle(CtlThread(&c, 0));
    Check(label, "the read waiting when the release frame was queued carries it", CarriesRelease(r));
    /* Control: the release frame was queued, so the next read takes it
     * when the first did not, and never a second time. */
    HmRequest *next = Read(&c);
    Check(label, "control: the release frame goes to exactly one read", CarriesRelease(r) + CarriesRelease(next) == 1);
    CtlStop(&c);
}

/* late-read-before-retrieve: one read parked, frame B written, and a
 * second read comes at the worker's first SRW release in its frame path.
 * In this driver that is after the cache write and before the worker looks
 * at the queue, so the late read takes B from the cache and the worker,
 * finding B taken, puts the read it took back. In b0fb7dc9 it is after the
 * worker gave B to the parked read. Either way B goes to exactly one read. */
static void LateReadBeforeRetrieve(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s late-read-before-retrieve", p->id);
    if (!StartHid(&c, p, "late-read-before-retrieve", label)) return;
    Read(&c);
    Write(&c, 0x40);
    g_lockReleaseHook = HookRead;
    RunWorker(&c);
    g_lockReleaseHook = NULL;
    Check(label, "the second read came inside the worker's frame path", g_wHooks == 1 && g_readCount == 3);
    Settle(CtlThread(&c, 0));
    Check(label, "frame B goes to exactly one read", CarriedBy(&c, 0x40) == 1);
    Check(label, "one read is left waiting", Parked(&c) == 1);
    ControlFrame(&c, label, 0x70);
    CtlStop(&c);
}

static void HookLateRead(void) { g_wHooks++; Read(g_w); }
static void HookWriteBThenArm(void)
{
    HookWriteB();
    g_retrieveHook = HookLateRead;
}

/* parked-read-loses-to-late-read: the read finds nothing new, B is cached
 * before it parks, and in this driver its look after parking finds B and
 * goes to the queue for a read: a second read comes at that moment and
 * takes B from the cache, and the read taken from the queue goes back.
 * b0fb7dc9 parks without a second look, and the second read comes right
 * after the park. B goes to the second read either way, once. */
static void ParkedReadLosesToLateRead(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s parked-read-loses-to-late-read", p->id);
    if (!StartHid(&c, p, "parked-read-loses-to-late-read", label)) return;
    g_wBase = 0x40;
    g_forwardHook = HookWriteBThenArm;
    HmRequest *r = Read(&c);
    g_forwardHook = NULL;
    int inside = g_retrieveHook == NULL;
    if (!inside) { g_retrieveHook = NULL; HookLateRead(); }
    RecOut("wake.inside", &(uint8_t){ (uint8_t)inside }, 1);
    Check(label, "B was cached and the second read came", g_wHooks == 2 && g_readCount == 3);
    Settle(CtlThread(&c, 0));
    /* Inside the first read's dispatch the second is listed before it. */
    HmRequest *late = g_reads[1] == r ? g_reads[2] : g_reads[1];
    Check(label, "frame B goes to exactly one read, the second", CarriedBy(&c, 0x40) == 1 && Carries(&c, late, 0x40));
    Check(label, "the first read is left waiting", !r->completed && Parked(&c) == 1);
    ControlFrame(&c, label, 0x70);
    CtlStop(&c);
}

static void HookWriteC(void)
{
    g_wHooks++;
    Write(g_w, 0x70);
    RunWorker(g_w);
}
static void HookWriteBThenArmTwo(void)
{
    HookWriteB();
    g_retrieveHook = HookLateRead;
    g_requeueHook = HookWriteC;
}

/* read-put-back-beside-frame: as parked-read-loses-to-late-read, the read
 * that parks finds B on its second look, goes to the queue and takes
 * itself, and the second read takes B meanwhile. While the first read's
 * thread holds it, finding nothing to take, the SDK writes C and the worker
 * caches C and finds the queue empty. Then the read goes back. A put back
 * is a park, and this driver looks again after it. b0fb7dc9 has no second
 * look, so the second read and C come right after the park, and the worker
 * takes the parked read for C. C reaches the first read either way. */
static void ReadPutBackBesideFrame(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s read-put-back-beside-frame", p->id);
    if (!StartHid(&c, p, "read-put-back-beside-frame", label)) return;
    g_wBase = 0x40;
    g_forwardHook = HookWriteBThenArmTwo;
    HmRequest *r = Read(&c);
    g_forwardHook = NULL;
    int inside = g_retrieveHook == NULL && g_requeueHook == NULL;
    if (g_retrieveHook) { g_retrieveHook = NULL; HookLateRead(); }
    if (g_requeueHook) { g_requeueHook = NULL; HookWriteC(); }
    RecOut("wake.inside", &(uint8_t){ (uint8_t)inside }, 1);
    Check(label, "B was cached, the second read came and C was cached", g_wHooks == 3 && g_readCount == 3);
    Settle(CtlThread(&c, 0));
    Check(label, "frame B goes to exactly one read", CarriedBy(&c, 0x40) == 1);
    Check(label, "the read put back carries C, cached while it was held", Carries(&c, r, 0x70));
    Check(label, "frame C goes to exactly one read", CarriedBy(&c, 0x70) == 1);
    ControlFrame(&c, label, 0xA0);
    CtlStop(&c);
}

static void HookWriteTwo(void)
{
    g_wHooks++;
    Write(g_w, 0x40);
    RunWorker(g_w);
    Write(g_w, 0x70);
    RunWorker(g_w);
}

/* two-frames-before-park: B and then C are cached between the read's look
 * and its park. The read carries C, the newest frame cached when it was
 * taken, and B, which C replaced before any read was taken, goes to none. */
static void TwoFramesBeforePark(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s two-frames-before-park", p->id);
    if (!StartHid(&c, p, "two-frames-before-park", label)) return;
    g_forwardHook = HookWriteTwo;
    HmRequest *r = Read(&c);
    g_forwardHook = NULL;
    Check(label, "B and C were cached between the read's look and its park", g_wHooks == 1);
    Settle(CtlThread(&c, 0));
    Check(label, "the read waiting carries C, the newest frame", Carries(&c, r, 0x70));
    Check(label, "no read carries B, which C replaced before any read took it", CarriedBy(&c, 0x40) == 0);
    ControlFrame(&c, label, 0xA0);
    CtlStop(&c);
}

/* wrap-before-park: frame A carries SeqNo 0xFFFFFFFE, and B, the frame
 * past the wrap, is cached between the read's look and its park. B's SeqNo
 * is not above A's, so v2.0.3's greater-than test, which a read coming
 * after a frame keeps, would not give it to this read. This read was
 * waiting when B came, as a read parked a moment earlier was, and that
 * read gets B from the worker in every driver. -late is the control: the
 * read comes after B, and parks until the next frame, as at 533ca4a4. */
static void WrapBeforePark(PlanProfile *p, int late)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s wrap-%s", p->id, late ? "late-read" : "before-park");
    StreamBegin("wake/%s/wrap-%s", p->id, late ? "late-read" : "before-park");
    g_w = &c;
    g_wHooks = 0;
    g_readCount = 0;
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) { Check(label, "the device add succeeds", 0); return; }
    c.sdk.seq = 0xFFFFFFFCu;
    Read(&c);
    Write(&c, 0x10);
    RunWorker(&c);
    Check(label, "control: a read parked before frame A carries A", Carries(&c, g_reads[0], 0x10));
    HmRequest *r;
    if (late) {
        Write(&c, 0x40);
        RunWorker(&c);
        r = Read(&c);
        Settle(CtlThread(&c, 0));
        Check(label, "the read that comes after frame B, past the wrap, waits for the next frame", !r->completed && Parked(&c) == 1);
    } else {
        g_wBase = 0x40;
        g_forwardHook = HookWriteB;
        r = Read(&c);
        g_forwardHook = NULL;
        Check(label, "the worker cached frame B between the read's look and its park", g_wHooks == 1);
        Settle(CtlThread(&c, 0));
        Check(label, "the read waiting when frame B came carries B", Carries(&c, r, 0x40));
    }
    ControlFrame(&c, label, 0x70);
    CtlStop(&c);
}

/* ------------------------------------------------------------------ */
/*  The XUSB companion's WAIT_FOR_INPUT                                */
/* ------------------------------------------------------------------ */

static HmRequest *g_waits[16];
static int g_waitCount;

static HmRequest *Wait(Ctl *c)
{
    HmRequest *r = Ioctl(c->dev, "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, NULL, 0, 29, NULL, 0);
    if (g_waitCount < (int)ARRAYSIZE(g_waits)) g_waits[g_waitCount++] = r;
    return r;
}

/* gip[12] bit k sets button bit 12 + k (companion.c DecodeGipToXInput), the
 * high byte of wButtons at state[12]. */
static void WriteGip(Ctl *c, uint8_t low)
{
    uint8_t gip[14] = { 0xFF, 0x7F, 0xFF, 0x7F, 0xFF, 0x7F, 0xFF, 0x7F, 0, 0, 0, 0, 0, 0 };
    gip[12] = low;
    SdkWriteFrame(&c->sdk, NULL, 0, gip, NULL, 0);
}

static int Served(const HmRequest *r, uint8_t low)
{
    return r && r->completed && r->info == 29 && r->out[12] == (uint8_t)(low << 4) && r->out[11] == 0;
}

static int ServedTimes(uint8_t low)
{
    int n = 0;
    for (int i = 0; i < g_waitCount; i++) n += Served(g_waits[i], low);
    return n;
}

static int ParkedWaits(void)
{
    int n = 0;
    for (int i = 0; i < g_waitCount; i++) n += g_waits[i]->inQueue;
    return n;
}

static int StartCompanion(Ctl *c, PlanProfile *p, const char *name, const char *label)
{
    StreamBegin("wake/%s/%s", p->id, name);
    g_w = c;
    g_wHooks = 0;
    g_waitCount = 0;
    if (!CtlStart(c, p, 0, SDK_ALL, 1)) { Check(label, "the device add succeeds", 0); return 0; }
    /* Frame 1 through a request parked before it. */
    Wait(c);
    WriteGip(c, 0x01);
    Settle(CtlThread(c, 0));
    Check(label, "control: a WAIT_FOR_INPUT parked before frame 1 carries it", Served(g_waits[0], 0x01));
    return 1;
}

/* The next frame reaches the request waiting, or one sent now. */
static void ControlGip(Ctl *c, const char *label, uint8_t low)
{
    if (ParkedWaits() == 0) Wait(c);
    WriteGip(c, low);
    Settle(CtlThread(c, 0));
    Check(label, "control: the next frame reaches the WAIT_FOR_INPUT waiting", ServedTimes(low) == 1);
}

/* doorbell-before-park: frame 2 rings the doorbell with nothing parked, and
 * the pump's turn finds the queue empty. Then Windows.Gaming.Input's next
 * WAIT_FOR_INPUT parks. Before a 2.1.0 SDK only the doorbell wakes the
 * pump (-legacy). With the signal section the SDK skips that ring and the
 * request's look kicks the pump. */
static void DoorbellBeforePark(PlanProfile *p, int legacy)
{
    Ctl c;
    char label[160];
    int saved = g_sdkSignal;
    if (legacy) g_sdkSignal = 0;
    snprintf(label, sizeof(label), "%s doorbell-before-park%s", p->id, legacy ? "-legacy" : "");
    if (StartCompanion(&c, p, legacy ? "doorbell-before-park-legacy" : "doorbell-before-park", label)) {
        WriteGip(&c, 0x02);
        Settle(CtlThread(&c, 0));
        HmRequest *r = Wait(&c);
        Settle(CtlThread(&c, 0));
        Check(label, "the WAIT_FOR_INPUT that came after frame 2's doorbell carries frame 2", Served(r, 0x02));
        Check(label, "frame 2 goes to exactly one WAIT_FOR_INPUT", ServedTimes(0x02) == 1);
        ControlGip(&c, label, 0x04);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

static void HookWriteGip2(void)
{
    g_wHooks++;
    WriteGip(g_w, 0x02);
}

/* doorbell-and-kick: frame 2 is written and rung while a WAIT_FOR_INPUT is
 * being forwarded, so the doorbell is set and the request's look kicks the
 * pump as well, both for frame 2. The pump serves the request on the
 * doorbell, and the next WAIT_FOR_INPUT, sent the moment that one
 * completes, finds the kick still set. -legacy: before a 2.1.0 SDK, one
 * request at a time, as Windows.Gaming.Input keeps it. -gated: under the
 * signal section with a second request parked and counted before, so the
 * SDK rings: the doorbell serves the first request and the kick the
 * second, both with frame 2, in b0fb7dc9. */
static void DoorbellAndKick(PlanProfile *p, int legacy)
{
    Ctl c;
    char label[160];
    int saved = g_sdkSignal;
    if (legacy) g_sdkSignal = 0;
    snprintf(label, sizeof(label), "%s doorbell-and-kick-%s", p->id, legacy ? "legacy" : "gated");
    if (StartCompanion(&c, p, legacy ? "doorbell-and-kick-legacy" : "doorbell-and-kick-gated", label)) {
        if (!legacy) Wait(&c);
        g_forwardHook = HookWriteGip2;
        Wait(&c);
        g_forwardHook = NULL;
        Check(label, "frame 2 came while the WAIT_FOR_INPUT was forwarded", g_wHooks == 1);
        if (legacy) {
            /* The doorbell's turn, then the next request at once. */
            ThreadResume(CtlThread(&c, 0));
            Wait(&c);
        }
        Settle(CtlThread(&c, 0));
        Check(label, "frame 2 goes to exactly one WAIT_FOR_INPUT", ServedTimes(0x02) == 1);
        Check(label, "one WAIT_FOR_INPUT is left waiting", ParkedWaits() == 1);
        ControlGip(&c, label, 0x04);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

/* The companion's 8 ms timer, run once: it serves whatever is current to
 * the request parked. */
static void TimerTick(Ctl *c)
{
    HmTimer *t = c->dev->timerCount ? c->dev->timers[0] : NULL;
    if (!t || !t->started) return;
    g_clockMs += t->period;
    t->fn((WDFTIMER)t);
}

/* kick-without-doorbell: nothing holds the SDK's companion doorbell, so the
 * pump never opens it and waits in its bootstrap: on its stop event alone
 * in b0fb7dc9, on the stop event and the kick in this driver. Frame 1 is
 * in the section, and a WAIT_FOR_INPUT parks and kicks. The control is the
 * 8 ms timer, which serves the next frame to the request waiting. */
static void KickWithoutDoorbell(PlanProfile *p, int legacy)
{
    Ctl c;
    char label[160];
    int saved = g_sdkSignal;
    if (legacy) g_sdkSignal = 0;
    snprintf(label, sizeof(label), "%s kick-without-doorbell%s", p->id, legacy ? "-legacy" : "");
    StreamBegin("wake/%s/kick-without-doorbell%s", p->id, legacy ? "-legacy" : "");
    g_w = &c;
    g_wHooks = 0;
    g_waitCount = 0;
    memset(&c, 0, sizeof(c));
    c.p = p;
    c.companion = 1;
    SdkCreate(&c.sdk, 0, SDK_ALL);
    ObjRelease(&c.sdk.companionEv->o);
    ConfigWrite(p, 0);
    c.dev = DeviceAdd(1);
    if (!c.dev) {
        Check(label, "the device add succeeds", 0);
    } else {
        WriteGip(&c, 0x01);
        HmRequest *w = Wait(&c);
        Settle(CtlThread(&c, 0));
        Check(label, "the WAIT_FOR_INPUT that parks beside frame 1 carries it", Served(w, 0x01));
        if (ParkedWaits() == 0) Wait(&c);
        WriteGip(&c, 0x02);
        Settle(CtlThread(&c, 0));
        TimerTick(&c);
        Check(label, "control: the timer serves frame 2 to the WAIT_FOR_INPUT waiting", ServedTimes(0x02) == 1);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

int CaseWake(void)
{
    PlanProfile *ds = NULL;
    for (int i = 0; i < g_plan.profileCount && !ds; i++)
        if (!strcmp(g_plan.profiles[i].group, "sony") && g_plan.profiles[i].bluetooth)
            ds = &g_plan.profiles[i];
    PlanProfile *xusb = PlanFind("xusb", "xbox-360-wired");
    if (!ds || !xusb) HmFail("the plan lacks a profile the wake checks need");
    ParksAfterPublish(ds, 0);
    ParksAfterPublish(ds, 1);
    ReadAtWorkerLock(ds);
    ParksAfterOwnerGone(ds);
    LateReadBeforeRetrieve(ds);
    ParkedReadLosesToLateRead(ds);
    ReadPutBackBesideFrame(ds);
    TwoFramesBeforePark(ds);
    WrapBeforePark(ds, 0);
    WrapBeforePark(ds, 1);
    DoorbellBeforePark(xusb, 1);
    DoorbellBeforePark(xusb, 0);
    DoorbellAndKick(xusb, 1);
    DoorbellAndKick(xusb, 0);
    KickWithoutDoorbell(xusb, 1);
    KickWithoutDoorbell(xusb, 0);
    printf("wake checks %d failed %d\n", g_checks, g_failed);
    return g_failed;
}
