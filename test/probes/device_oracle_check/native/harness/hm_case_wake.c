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
 * OwnerGone queues.
 *
 * Each scenario places one order with a hook (hm_wdf.c g_forwardHook,
 * g_retrieveHook and g_requeueHook, hm_win32.c g_lockHook,
 * g_lockReleaseHook, g_csLeaveHook, g_mapHook and g_barrierHook), where a
 * thread beside the scenario's can send a request (RunBeside), and checks:
 *   - the read waiting when a frame came carries it, or the newest frame
 *     cached when it was taken,
 *   - no frame goes to two reads,
 *   - no completion carries an older frame than one completed before it,
 *   - and, as the positive control in the same scenario, a read parked
 *     before a frame carries it, and the next frame reaches the read left
 *     waiting.
 * The XUSB companion's scenarios hold its WAIT_FOR_INPUT to xusb22's rule,
 * through several handles: a report is a change of the XInput state, it
 * completes every request parked before it, in order, all with the same
 * bytes, a request parked after it waits for the next, and the dispatch
 * fails what xusb22 fails. Others race SET_STATE at the output mapping's
 * remap and lap an output-ring reader. The companion's 8 ms timer runs
 * only where a scenario checks it: a tick before a check would hide a
 * lost doorbell.
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

/* Completions reach HID class in the order the frames were taken: its
 * completion routine runs on the completing thread, so a thread that took
 * a frame and completes it later puts it in HID class's buffer after a
 * newer one another thread completed meanwhile. */
static HmRequest *g_other;
static int g_releaseCountdown;

/* Runs where the dispatching thread released InputLock with a frame taken:
 * HID class's other read parks, frame C comes, and the worker serves C to
 * that read. */
static void HookNewerFirst(void)
{
    g_wHooks++;
    g_other = Read(g_w);
    Write(g_w, 0x70);
    RunWorker(g_w);
}

/* older-after-newer: frame B is cached with no read parked, and a late
 * READ_REPORT takes B. Before its handler completes it, HID class's other
 * read parks and the worker serves it frame C. Through 81e1fe40, as in
 * v2.0.3, the late read completed after its lock, so C reached HID class
 * before B: 99,670 then 99,669 on the Celeron. */
static void OlderAfterNewer(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s older-after-newer", p->id);
    if (!StartHid(&c, p, "older-after-newer", label)) return;
    g_other = NULL;
    Write(&c, 0x40);
    RunWorker(&c);
    g_dispatchReleaseHook = HookNewerFirst;
    HmRequest *r = Read(&c);
    g_dispatchReleaseHook = NULL;
    Check(label, "frame C came while the late read's handler ran", g_wHooks == 1 && g_other != NULL);
    Check(label, "control: the late read carries B", Carries(&c, r, 0x40));
    Check(label, "control: HID class's other read carries C", g_other && Carries(&c, g_other, 0x70));
    Check(label, "no completion carries an older frame than one completed before it",
          g_other && r->completed && g_other->completed && r->doneNo < g_other->doneNo);
    ControlFrame(&c, label, 0xA0);
    CtlStop(&c);
}

/* The second release on the dispatching fiber after this arms it: the one
 * in DeliverParked after its take. */
static void HookNewerFirstAtSecond(void)
{
    if (--g_releaseCountdown > 0) {
        g_dispatchReleaseHook = HookNewerFirstAtSecond;
        return;
    }
    HookNewerFirst();
}
static void HookWriteBThenArmOrder(void)
{
    HookWriteB();
    g_releaseCountdown = 2;
    g_dispatchReleaseHook = HookNewerFirstAtSecond;
}

/* second-look-older-after-newer: frame B is cached between a read's look
 * and its park, so the read's second look takes B from the queue itself
 * (DeliverParked on the dispatching thread). Before that thread completes
 * it, HID class's other read parks and the worker serves it frame C. */
static void SecondLookOlderAfterNewer(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s second-look-older-after-newer", p->id);
    if (!StartHid(&c, p, "second-look-older-after-newer", label)) return;
    g_other = NULL;
    g_wBase = 0x40;
    g_forwardHook = HookWriteBThenArmOrder;
    HmRequest *r = Read(&c);
    g_forwardHook = NULL;
    g_dispatchReleaseHook = NULL;
    Settle(CtlThread(&c, 0));
    /* The order needs the park's second look: b0fb7dc9 and 533ca4a4 make
     * none, so these three fail there beside the order check, and
     * older-after-newer holds their completion order. */
    Check(label, "precondition: the read that parked beside B carries B", Carries(&c, r, 0x40));
    Check(label, "precondition: frame C came while the dispatching thread held B", g_wHooks == 2 && g_other != NULL);
    Check(label, "precondition: HID class's other read carries C", g_other && Carries(&c, g_other, 0x70));
    Check(label, "no completion carries an older frame than one completed before it",
          g_other && r->completed && g_other->completed && r->doneNo < g_other->doneNo);
    ControlFrame(&c, label, 0xA0);
    CtlStop(&c);
}

/* ------------------------------------------------------------------ */
/*  The XUSB companion's WAIT_FOR_INPUT, by xusb22's rule              */
/* ------------------------------------------------------------------ */

/* xusb22.sys 10.0.26100.9549 keeps one list of the waits parked through
 * every handle of a controller. The pad sends a report when its state
 * changes, and each report completes every wait in that list, in the
 * order they were parked, all with the same 29 bytes, while a wait parked
 * after a report waits for the next one (XInputControllerDevice::
 * HandleWaitOrUnfilteredRequest and ProcessWaitRequests). A wait whose
 * process lacks focus goes back in order. No focus information reaches the
 * companion, and every wait passes. Its report is a frame whose XInput
 * state differs from the last report's, found by the first turn that
 * reads it: the pump's on the doorbell or a kick, GET_STATE's, or the
 * 8 ms timer's under an SDK that makes no doorbell. A request is parked
 * after a frame when it looked at the section after the frame was written.
 * Each handle is a reader of its own, as Windows.Gaming.Input, explorer
 * and GameInputSvc each open the XUSB interface. */

#define IOCTL_XUSB_GET_STATE        0x8000E00C
#define MAX_HANDLES 4
static HmFile *g_h[MAX_HANDLES];
static int g_hCount;
static HmRequest *g_ws[64];
static int g_wsHandle[64];
static int g_wsCount;

static HmRequest *WaitOn(Ctl *c, int h)
{
    HmRequest *r = XusbWait(c->dev, g_h[h], "xusb.waitforinput", 29);
    if (g_wsCount < (int)ARRAYSIZE(g_ws)) {
        g_ws[g_wsCount] = r;
        g_wsHandle[g_wsCount] = h;
        g_wsCount++;
    }
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
    return r && r->completed && r->status == STATUS_SUCCESS && r->info == 29
        && r->out[12] == (uint8_t)(low << 4) && r->out[11] == 0;
}

static uint32_t Packet(const HmRequest *r)
{
    uint32_t v;
    memcpy(&v, r->out + 5, 4);
    return v;
}

/* Requests of handle h that carry frame low. */
static int HandleServed(int h, uint8_t low)
{
    int n = 0;
    for (int i = 0; i < g_wsCount; i++)
        if (g_wsHandle[i] == h) n += Served(g_ws[i], low);
    return n;
}

static int HandleParked(int h)
{
    int n = 0;
    for (int i = 0; i < g_wsCount; i++)
        if (g_wsHandle[i] == h) n += g_ws[i]->inQueue;
    return n;
}

static int WaitsParked(void)
{
    int n = 0;
    for (int i = 0; i < g_wsCount; i++) n += g_ws[i]->inQueue;
    return n;
}

/* Every request sent from first on carries frame low, all with one packet
 * number, and they completed in the order they were sent. */
static int AllCarry(int first, uint8_t low)
{
    if (first >= g_wsCount) return 0;
    for (int i = first; i < g_wsCount; i++) {
        if (!Served(g_ws[i], low) || Packet(g_ws[i]) != Packet(g_ws[first])) return 0;
        if (i > first && g_ws[i]->doneNo < g_ws[i - 1]->doneNo) return 0;
    }
    return 1;
}

/* GET_STATE, as XInputGetState sends it. */
static HmRequest *GetState(Ctl *c)
{
    return Ioctl(c->dev, "xusb.state", IOCTL_XUSB_GET_STATE, NULL, 0, 29, NULL, 0);
}

static uint32_t StatePacket(Ctl *c)
{
    HmRequest *r = GetState(c);
    return r->completed && r->info == 29 ? Packet(r) : 0xFFFFFFFFu;
}

/* The companion's 8 ms timer, run once. */
static void TimerTick(Ctl *c)
{
    HmTimer *t = c->dev->timerCount ? c->dev->timers[0] : NULL;
    if (!t || !t->started) return;
    g_clockMs += t->period;
    t->fn((WDFTIMER)t);
}

static void BeginPad(Ctl *c, PlanProfile *p, const char *name, int handles)
{
    StreamBegin("wake/%s/%s", p->id, name);
    g_w = c;
    g_wHooks = 0;
    g_wsCount = 0;
    g_hCount = handles;
}

static void OpenHandles(Ctl *c)
{
    for (int h = 0; h < g_hCount; h++) g_h[h] = FileOpen(c->dev);
}

/* A pad whose SDK makes the doorbell, with its handles open. */
static int StartPad(Ctl *c, PlanProfile *p, const char *name, const char *label, int handles)
{
    BeginPad(c, p, name, handles);
    if (!CtlStart(c, p, 0, SDK_ALL, 1)) { Check(label, "the device add succeeds", 0); return 0; }
    OpenHandles(c);
    return 1;
}

/* A pad whose SDK makes no companion doorbell: nothing holds it, so the
 * pump never opens it and waits on its stop event and the kick. */
static int StartPadNoDoorbell(Ctl *c, PlanProfile *p, const char *name, const char *label, int handles)
{
    BeginPad(c, p, name, handles);
    memset(c, 0, sizeof(*c));
    c->p = p;
    c->companion = 1;
    SdkCreate(&c->sdk, 0, SDK_ALL);
    ObjRelease(&c->sdk.companionEv->o);
    ConfigWrite(p, 0);
    c->dev = DeviceAdd(1);
    if (!c->dev) { Check(label, "the device add succeeds", 0); return 0; }
    OpenHandles(c);
    return 1;
}

/* report-every-request: four requests parked through three handles, the
 * first handle's twice, then frame 1. Its report completes all four in the
 * order they were sent, with one packet number and xusb22's bytes: version
 * 0x0103, state[2] 1, slot 0, 0xFF for no headset, then 0x00 and the
 * report's length 0x14 before the gamepad. Through e9a6e92d a turn
 * completed one request a handle, so the first handle's second waited, and
 * every completion carried state[2] 3 and state[4] 0. */
static void ReportEveryRequest(PlanProfile *p, int legacy)
{
    Ctl c;
    char label[160];
    int saved = g_sdkSignal;
    if (legacy) g_sdkSignal = 0;
    snprintf(label, sizeof(label), "%s report-every-request%s", p->id, legacy ? "-legacy" : "");
    if (StartPad(&c, p, legacy ? "report-every-request-legacy" : "report-every-request", label, 3)) {
        WaitOn(&c, 0);
        WaitOn(&c, 1);
        WaitOn(&c, 0);
        WaitOn(&c, 2);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        Check(label, "control: the first request carries frame 1", Served(g_ws[0], 0x01));
        Check(label, "frame 1's report completes all four requests, in the order sent, with one packet number", AllCarry(0, 0x01));
        const uint8_t *o = g_ws[0]->out;
        Check(label, "the completion's bytes are xusb22's",
              o[0] == 0x03 && o[1] == 0x01 && o[2] == 0x01 && o[3] == 0x00 && o[4] == 0xFF && o[9] == 0x00 && o[10] == 0x14);
        int next = g_wsCount;
        for (int h = 0; h < 3; h++) WaitOn(&c, h);
        WriteGip(&c, 0x02);
        Settle(CtlThread(&c, 0));
        Check(label, "frame 2's report completes the three requests sent after it", AllCarry(next, 0x02));
        Check(label, "with the next packet number", Packet(g_ws[next]) == Packet(g_ws[0]) + 1);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

/* parked-after-report: handle A's request carries frame 1. Handle B's
 * request comes after frame 1's report and waits for the next one, as a
 * wait xusb22 parks after a report does, and frame 2 reaches it beside
 * handle A's next request. Through e9a6e92d B's look kicked the pump, and
 * B got frame 1. */
static void ParkedAfterReport(PlanProfile *p, int legacy)
{
    Ctl c;
    char label[160];
    int saved = g_sdkSignal;
    if (legacy) g_sdkSignal = 0;
    snprintf(label, sizeof(label), "%s parked-after-report%s", p->id, legacy ? "-legacy" : "");
    if (StartPad(&c, p, legacy ? "parked-after-report-legacy" : "parked-after-report", label, 2)) {
        WaitOn(&c, 0);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        Check(label, "control: handle A's request carries frame 1", HandleServed(0, 0x01) == 1);
        HmRequest *b = WaitOn(&c, 1);
        Settle(CtlThread(&c, 0));
        Check(label, "handle B's request, sent after frame 1's report, waits for the next report", b->inQueue && !b->completed);
        WaitOn(&c, 0);
        WriteGip(&c, 0x02);
        Settle(CtlThread(&c, 0));
        Check(label, "control: frame 2 reaches a request", HandleServed(0, 0x02) + HandleServed(1, 0x02) > 0);
        Check(label, "frame 2 reaches handle A's next request and handle B's request", HandleServed(0, 0x02) == 1 && Served(b, 0x02));
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

/* same-state: handle A keeps two requests parked, and frame 1 completes
 * both. Then the SDK writes frame 1's state again, and a frame whose GIP
 * bytes differ only below XInput's resolution (a trigger of 1 in 1023
 * reads 0 of 255). Neither is a report: no completion, and GET_STATE's
 * packet number holds. Frame 2 completes both requests with the next
 * packet number. Through e9a6e92d frame 1 completed one request, each new
 * SeqNo reached the handle, and GET_STATE's packet number went up with
 * the GIP bytes. */
static void SameState(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s same-state", p->id);
    if (StartPad(&c, p, "same-state", label, 1)) {
        WaitOn(&c, 0);
        WaitOn(&c, 0);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        Check(label, "control: frame 1 completes the first request", Served(g_ws[0], 0x01));
        Check(label, "and the second, with the same packet number", AllCarry(0, 0x01));
        uint32_t pk = Packet(g_ws[0]);
        Check(label, "control: GET_STATE reads frame 1's packet number", StatePacket(&c) == pk);
        int next = g_wsCount;
        WaitOn(&c, 0);
        WaitOn(&c, 0);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        uint8_t gip[14] = { 0xFF, 0x7F, 0xFF, 0x7F, 0xFF, 0x7F, 0xFF, 0x7F, 0x01, 0, 0, 0, 0x01, 0 };
        SdkWriteFrame(&c.sdk, NULL, 0, gip, NULL, 0);
        Settle(CtlThread(&c, 0));
        Check(label, "frames with frame 1's XInput state complete nothing", WaitsParked() == 2);
        Check(label, "and GET_STATE's packet number holds", StatePacket(&c) == pk);
        WriteGip(&c, 0x02);
        Settle(CtlThread(&c, 0));
        Check(label, "frame 2 completes both requests with the next packet number",
              AllCarry(next, 0x02) && Packet(g_ws[next]) == pk + 1);
        Check(label, "and GET_STATE reads it", StatePacket(&c) == pk + 1);
        CtlStop(&c);
    }
}

/* get-state-reports: before any frame GET_STATE answers packet 0 and a
 * zero state, as xusb22 does before the pad's first report. A request
 * parks, and frame 1 rings the doorbell, but GET_STATE reads the frame
 * before the pump runs: that look is frame 1's report, and it completes
 * the request with GET_STATE's packet number. The doorbell's turn then
 * finds nothing new. Through e9a6e92d GET_STATE decoded the zeroed
 * section as a state at packet 1, and left the request to the doorbell. */
static void GetStateReports(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s get-state-reports", p->id);
    if (StartPad(&c, p, "get-state-reports", label, 1)) {
        HmRequest *g0 = GetState(&c);
        int zero = g0->completed && g0->info == 29 && Packet(g0) == 0;
        for (int i = 9; i < 29 && zero; i++) zero = g0->out[i] == 0;
        Check(label, "before any frame GET_STATE answers packet 0 and a zero state", zero);
        HmRequest *w = WaitOn(&c, 0);
        WriteGip(&c, 0x01);
        HmRequest *g1 = GetState(&c);
        Check(label, "control: GET_STATE reads frame 1", g1->completed && g1->info == 29 && g1->out[12] == 0x10);
        Check(label, "and its report completes the request parked, with the same packet number",
              Served(w, 0x01) && Packet(w) == Packet(g1) && Packet(g1) == 1);
        Settle(CtlThread(&c, 0));
        HmRequest *w2 = WaitOn(&c, 0);
        Settle(CtlThread(&c, 0));
        Check(label, "the doorbell's turn after it reports nothing more", w2->inQueue && StatePacket(&c) == 1);
        CtlStop(&c);
    }
}

/* timer-without-doorbell: an SDK that makes no doorbell. Two handles'
 * requests park before frame 1, and one tick of the 8 ms timer reports it
 * to both. A tick with nothing new, and one after frame 1's state written
 * again, report nothing. Through e9a6e92d the requests' kicks served the
 * section as it was, and the tick after the repeat served it again. */
static void TimerWithoutDoorbell(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s timer-without-doorbell", p->id);
    if (StartPadNoDoorbell(&c, p, "timer-without-doorbell", label, 2)) {
        WaitOn(&c, 0);
        WaitOn(&c, 1);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        TimerTick(&c);
        Check(label, "control: one tick reports frame 1 to the first handle", Served(g_ws[0], 0x01));
        Check(label, "and to the second, with the same packet number", AllCarry(0, 0x01));
        WaitOn(&c, 0);
        WaitOn(&c, 1);
        TimerTick(&c);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        TimerTick(&c);
        Check(label, "ticks with no new state report nothing", WaitsParked() == 2);
        HmRequest *a = WaitOn(&c, 0);
        WriteGip(&c, 0x02);
        TimerTick(&c);
        Check(label, "control: the tick after frame 2 reports it", HandleServed(0, 0x02) + HandleServed(1, 0x02) > 0);
        Check(label, "to the two that waited and the one parked just before it, in the order sent", AllCarry(2, 0x02) && Served(a, 0x02));
        CtlStop(&c);
    }
}

/* timer-beside-doorbell: the SDK makes the doorbell, and frame 1 rings it
 * before the pump has run. A tick of the 8 ms timer reports nothing: the
 * timer is the fallback for an SDK with no doorbell. The pump's turn then
 * reports frame 1. Through e9a6e92d the tick served it. */
static void TimerBesideDoorbell(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s timer-beside-doorbell", p->id);
    if (StartPad(&c, p, "timer-beside-doorbell", label, 1)) {
        WaitOn(&c, 0);
        WriteGip(&c, 0x01);
        TimerTick(&c);
        Check(label, "a tick with the doorbell open reports nothing", WaitsParked() == 1);
        Settle(CtlThread(&c, 0));
        Check(label, "control: the pump's turn reports frame 1", Served(g_ws[0], 0x01));
        CtlStop(&c);
    }
}

static void ParkHandleB(void) { WaitOn(g_w, 1); }

/* Runs at the first take from the queue in the report's turn: handle B's
 * request is sent then, by another thread, after frame 2 was written. */
static void HookParkHandleB(void)
{
    g_wHooks++;
    RunBeside(ParkHandleB);
}

/* parks-during-report: handle A's request carries frame 1. A's next
 * request waits, and frame 2 rings. While the pump's turn takes the
 * requests to report frame 2, handle B's request parks. B looked at the
 * section after frame 2 was written, so the report puts it back, and B
 * waits for frame 3, which reaches it beside A's next request. Through
 * e9a6e92d B's request, sent beside the turn, waited for ReadersLock to
 * join its reader, and its look then kicked the pump for frame 2. */
static void ParksDuringReport(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s parks-during-report", p->id);
    if (StartPad(&c, p, "parks-during-report", label, 2)) {
        WaitOn(&c, 0);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        Check(label, "control: handle A's request carries frame 1", HandleServed(0, 0x01) == 1);
        WaitOn(&c, 0);
        WriteGip(&c, 0x02);
        g_retrieveHook = HookParkHandleB;
        Settle(CtlThread(&c, 0));
        g_retrieveHook = NULL;
        Check(label, "handle B's request was sent inside the report's turn", g_wHooks == 1 && !BesideWaiting());
        Check(label, "control: handle A's next request carries frame 2", HandleServed(0, 0x02) == 1);
        Check(label, "handle B's request, parked after frame 2, waits", HandleParked(1) == 1 && HandleServed(1, 0x02) == 0);
        WaitOn(&c, 0);
        WriteGip(&c, 0x04);
        Settle(CtlThread(&c, 0));
        Check(label, "control: frame 3 reaches a request", HandleServed(0, 0x04) + HandleServed(1, 0x04) > 0);
        Check(label, "frame 3 reaches handle A's next request and handle B's request", HandleServed(0, 0x04) == 1 && HandleServed(1, 0x04) == 1);
        CtlStop(&c);
    }
}

/* Runs at the next forward: frame 1 is written and its doorbell's turn
 * reports it, while the request being forwarded has already looked. */
static void HookReportFrameOne(void)
{
    g_wHooks++;
    WriteGip(g_w, 0x01);
    Settle(CtlThread(g_w, 0));
}

/* late-after-report: handle A's second request looks at the section before
 * frame 1 is written, and enters the queue after frame 1's report took the
 * first one. xusb22 holds a wait in its list from its dispatch, so the
 * report owes it frame 1: its kick completes it with that report, with the
 * first request's packet number. Through e9a6e92d the handle had frame 1,
 * and the request waited for frame 2. */
static void LateAfterReport(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s late-after-report", p->id);
    if (StartPad(&c, p, "late-after-report", label, 1)) {
        HmRequest *first = WaitOn(&c, 0);
        g_forwardHook = HookReportFrameOne;
        HmRequest *late = WaitOn(&c, 0);
        g_forwardHook = NULL;
        Check(label, "control: frame 1's report came while the second request was forwarded",
              g_wHooks == 1 && Served(first, 0x01));
        Settle(CtlThread(&c, 0));
        Check(label, "the second request carries frame 1's report, with its packet number",
              Served(late, 0x01) && Packet(late) == Packet(first));
        CtlStop(&c);
    }
}

/* doorbell-and-kick: frame 2 is written while a WAIT_FOR_INPUT is being
 * forwarded, after its look at the section, so the request was parked
 * before the frame, as xusb22's list would hold it. -gated: a second
 * request was parked before. Frame 2's report completes both, and the
 * request sent once it completes waits for the next report. */
static void HookWriteGipTwo(void)
{
    g_wHooks++;
    WriteGip(g_w, 0x02);
}

static void DoorbellAndKick(PlanProfile *p, int legacy)
{
    Ctl c;
    char label[160];
    int saved = g_sdkSignal;
    if (legacy) g_sdkSignal = 0;
    snprintf(label, sizeof(label), "%s doorbell-and-kick-%s", p->id, legacy ? "legacy" : "gated");
    if (StartPad(&c, p, legacy ? "doorbell-and-kick-legacy" : "doorbell-and-kick-gated", label, 1)) {
        WaitOn(&c, 0);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        Check(label, "control: a request parked before frame 1 carries it", Served(g_ws[0], 0x01));
        int next = g_wsCount;
        if (!legacy) WaitOn(&c, 0);
        g_forwardHook = HookWriteGipTwo;
        WaitOn(&c, 0);
        g_forwardHook = NULL;
        Check(label, "frame 2 came while the WAIT_FOR_INPUT was forwarded", g_wHooks == 1);
        Settle(CtlThread(&c, 0));
        Check(label, "frame 2's report completes every request parked before it", AllCarry(next, 0x02));
        HmRequest *after = WaitOn(&c, 0);
        Settle(CtlThread(&c, 0));
        Check(label, "the request sent after frame 2's report waits", after->inQueue);
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

/* kick-without-doorbell: an SDK that makes no doorbell. Frame 1 is in the
 * section before a request parks, so the request waits for the next
 * report: its look kicks nothing, and the timer's report of frame 1 puts
 * it back. The tick after frame 2 reports to it, after the request parked
 * before frame 2 alone. Through e9a6e92d the look kicked the pump, which
 * served frame 1. */
static void KickWithoutDoorbell(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s kick-without-doorbell", p->id);
    if (StartPadNoDoorbell(&c, p, "kick-without-doorbell", label, 1)) {
        WriteGip(&c, 0x01);
        HmRequest *w = WaitOn(&c, 0);
        Settle(CtlThread(&c, 0));
        TimerTick(&c);
        Check(label, "a request parked after frame 1 waits through its look and a tick", w->inQueue && !w->completed);
        HmRequest *w2 = WaitOn(&c, 0);
        WriteGip(&c, 0x02);
        TimerTick(&c);
        Check(label, "control: the tick after frame 2 reports it to a request parked before it", Served(w2, 0x02));
        Check(label, "and to the request that waited, first", Served(w, 0x02) && w->doneNo < w2->doneNo);
        CtlStop(&c);
    }
}

/* resume-at-once: byte 3 of the WaitRequest0102 asks for the focus state.
 * xusb22 completes 2 at once for a process with focus, state[2] 3 and the
 * rest zero, as WGI's first request after it opens the pad, and parks 3.
 * Every process passes the focus test here. A report then completes the
 * request parked with state[2] 1. Through e9a6e92d both parked, and the
 * report carried state[2] 3. */
static void ResumeAtOnce(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s resume-at-once", p->id);
    if (StartPad(&c, p, "resume-at-once", label, 1)) {
        static const uint8_t mode2[4] = { 0x02, 0x01, 0x00, 0x02 }, mode3[4] = { 0x02, 0x01, 0x00, 0x03 };
        HmRequest *r2 = IoctlFile(c.dev, g_h[0], "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, mode2, sizeof(mode2), 29, NULL, 0);
        int rest = 1;
        for (int i = 3; i < 29; i++) rest &= r2->out[i] == 0;
        Check(label, "byte 3 = 2 completes at once, state[2] 3 and the rest zero",
              r2->completed && r2->status == STATUS_SUCCESS && r2->info == 29
              && r2->out[0] == 0x03 && r2->out[1] == 0x01 && r2->out[2] == 0x03 && rest);
        HmRequest *r3 = IoctlFile(c.dev, g_h[0], "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, mode3, sizeof(mode3), 29, NULL, 0);
        Check(label, "byte 3 = 3 parks", r3->inQueue && !r3->completed);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        Check(label, "frame 1's report completes it with state[2] 1", Served(r3, 0x01) && r3->out[2] == 0x01);
        CtlStop(&c);
    }
}

/* invalid-requests: xusb22's DispatchWaitForInput and WaitForInputChange
 * fail at once, with STATUS_INVALID_PARAMETER, a WAIT_FOR_INPUT whose input
 * is missing, shorter than four bytes or not version 0x0102, whose output
 * is shorter than 29 bytes, or whose index names a pad the device does not
 * have. A WaitRequest0102 for pad 0 with a 29-byte buffer parks. Through
 * e9a6e92d the inputs were not read, and a short output failed with
 * STATUS_BUFFER_TOO_SMALL. */
static int Invalid(const HmRequest *r)
{
    return r->completed && !r->refused && r->status == STATUS_INVALID_PARAMETER && r->info == 0;
}

static void InvalidRequests(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s invalid-requests", p->id);
    if (StartPad(&c, p, "invalid-requests", label, 1)) {
        static const uint8_t two[2] = { 0x02, 0x01 }, v0101[4] = { 0x01, 0x01, 0x00, 0x00 };
        static const uint8_t pad1[4] = { 0x02, 0x01, 0x01, 0x00 }, ok[4] = { 0x02, 0x01, 0x00, 0x00 };
        HmRequest *none = IoctlFile(c.dev, g_h[0], "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, NULL, 0, 29, NULL, 0);
        HmRequest *shortIn = IoctlFile(c.dev, g_h[0], "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, two, sizeof(two), 29, NULL, 0);
        HmRequest *version = IoctlFile(c.dev, g_h[0], "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, v0101, sizeof(v0101), 29, NULL, 0);
        HmRequest *shortOut = IoctlFile(c.dev, g_h[0], "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, ok, sizeof(ok), 28, NULL, 0);
        HmRequest *index = IoctlFile(c.dev, g_h[0], "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, pad1, sizeof(pad1), 29, NULL, 0);
        HmRequest *good = WaitOn(&c, 0);
        Check(label, "control: a WaitRequest0102 for pad 0 with a 29-byte buffer parks", good->inQueue && !good->completed);
        Check(label, "no input fails with STATUS_INVALID_PARAMETER", Invalid(none));
        Check(label, "a two-byte input fails with STATUS_INVALID_PARAMETER", Invalid(shortIn));
        Check(label, "version 0x0101 fails with STATUS_INVALID_PARAMETER", Invalid(version));
        Check(label, "a 28-byte output fails with STATUS_INVALID_PARAMETER", Invalid(shortOut));
        Check(label, "pad 1 fails with STATUS_INVALID_PARAMETER", Invalid(index));
        CtlStop(&c);
    }
}

/* close-handle: handle C closes with two requests parked. The framework
 * cancels both through EvtIoCanceledOnQueue, and frame 1 then reaches the
 * other two handles. */
static void CloseOneHandle(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s close-handle", p->id);
    if (StartPad(&c, p, "close-handle", label, 3)) {
        WaitOn(&c, 0);
        WaitOn(&c, 1);
        HmRequest *c1 = WaitOn(&c, 2);
        HmRequest *c2 = WaitOn(&c, 2);
        FileClose(g_h[2]);
        Check(label, "control: the closed handle's two requests complete canceled",
              c1->completed && c1->status == STATUS_CANCELLED && c2->completed && c2->status == STATUS_CANCELLED);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        Check(label, "frame 1 reaches the two open handles", HandleServed(0, 0x01) == 1 && HandleServed(1, 0x01) == 1);
        CtlStop(&c);
    }
}

/* ------------------------------------------------------------------ */
/*  Two of the companion's callers at once                             */
/* ------------------------------------------------------------------ */

/* The default queue dispatches in parallel, so another thread's GET_STATE
 * or SET_STATE can come while one is inside the companion. Each runs
 * beside the running one (RunBeside) and waits where it finds a lock the
 * running one holds. */
static Ctl *g_rc;
static HmRequest *g_inner;
static int g_innerCountdown, g_innerFired;

static void InnerGetState(void) { g_inner = GetState(g_rc); }

static void HookInnerGetState(void)
{
    if (--g_innerCountdown > 0) { g_csLeaveHook = HookInnerGetState; return; }
    g_innerFired = 1;
    RunBeside(InnerGetState);
}

/* packet-race-get-state: frame 2 comes, and a second GET_STATE is sent the
 * moment the first left its read of the section. Frame 2 changed the
 * state, so both report a packet number above frame 1's. Through e9a6e92d
 * the first set GipChanged under GipLock and tested it after the lock, so
 * the second, which read the same payload, cleared it between, and both
 * answered frame 2 with frame 1's packet number: a caller that skips an
 * unchanged packet number skipped frame 2. */
static void PacketRaceGetState(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s packet-race-get-state", p->id);
    if (StartPad(&c, p, "packet-race-get-state", label, 1)) {
        g_rc = &c;
        g_inner = NULL;
        g_innerFired = 0;
        WriteGip(&c, 0x01);
        HmRequest *a = GetState(&c);
        HmRequest *a2 = GetState(&c);
        Check(label, "control: an unchanged frame keeps its packet number", Packet(a) == Packet(a2));
        WriteGip(&c, 0x02);
        g_innerCountdown = 1;
        g_csLeaveHook = HookInnerGetState;
        HmRequest *b = GetState(&c);
        g_csLeaveHook = NULL;
        Check(label, "a second GET_STATE came as the first left its read of the section", g_innerFired && g_inner && !BesideWaiting());
        Check(label, "control: both carry frame 2's buttons", b->out[12] == 0x20 && g_inner && g_inner->out[12] == 0x20);
        Check(label, "frame 2 changed the state, so both report a packet number above frame 1's",
              Packet(b) != Packet(a) && g_inner && Packet(g_inner) != Packet(a));
        CtlStop(&c);
    }
}

/* packet-race-pump-N: the same between the pump's turn on frame 2 and a
 * GET_STATE sent as the turn leaves the section's critical section for the
 * Nth time: through e9a6e92d the first time was the turn's peek and the
 * second its read. When the turn leaves it fewer times, the GET_STATE is
 * sent after the turn. The completion and the GET_STATE report a packet
 * number above frame 1's. Through e9a6e92d, at the read, both carried
 * frame 1's. */
static void PacketRacePump(PlanProfile *p, int countdown)
{
    Ctl c;
    char label[160], name[48];
    snprintf(name, sizeof(name), "packet-race-pump-%d", countdown);
    snprintf(label, sizeof(label), "%s %s", p->id, name);
    if (StartPad(&c, p, name, label, 1)) {
        g_rc = &c;
        g_inner = NULL;
        g_innerFired = 0;
        WaitOn(&c, 0);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        HmRequest *a = GetState(&c);
        HmRequest *w = WaitOn(&c, 0);
        WriteGip(&c, 0x02);
        g_innerCountdown = countdown;
        g_csLeaveHook = HookInnerGetState;
        Settle(CtlThread(&c, 0));
        g_csLeaveHook = NULL;
        if (!g_innerFired) InnerGetState();
        Check(label, "control: the WAIT_FOR_INPUT carries frame 2", Served(w, 0x02));
        Check(label, "control: the GET_STATE beside the turn carries frame 2", g_inner && g_inner->out[12] == 0x20 && !BesideWaiting());
        Check(label, "frame 2 changed the state, so the completion and the GET_STATE report a packet number above frame 1's",
              w->completed && Packet(w) != Packet(a) && g_inner && Packet(g_inner) != Packet(a));
        CtlStop(&c);
    }
}

/* output-remap: SET_STATE 501 times, so the 501st reaches
 * EnsureOutputMapping's remap. -race: a second SET_STATE is sent as the
 * 501st maps the section again. After the removal the host holds no named
 * object and no view. Through e9a6e92d PublishOutput took no lock: the
 * second found no view, mapped its own, and the first's view replaced it,
 * so a handle and a view leaked for the host's life. */
static int g_setFired;

static void InnerSetState(void)
{
    uint8_t rumble[5] = { 0x00, 0x08, 0x40, 0x80, 0x00 };
    Ioctl(g_rc->dev, "xusb.setstate", 0x8000A010, rumble, sizeof(rumble), 0, NULL, 0);
}

static void HookInnerSetState(void)
{
    g_setFired = 1;
    RunBeside(InnerSetState);
}

static void OutputRemap(PlanProfile *p, int race)
{
    Ctl c;
    char label[160];
    static char held[8192];
    snprintf(label, sizeof(label), "%s output-remap%s", p->id, race ? "-race" : "");
    if (StartPad(&c, p, race ? "output-remap-race" : "output-remap", label, 0)) {
        g_rc = &c;
        g_setFired = 0;
        for (int i = 0; i < 500; i++) InnerSetState();
        if (race) g_mapHook = HookInnerSetState;
        InnerSetState();
        g_mapHook = NULL;
        if (race) Check(label, "the second SET_STATE came as the first mapped the section again", g_setFired && !BesideWaiting());
        CtlStop(&c);
        int n = HeldObjects(held, sizeof(held));
        if (n) printf("  held after removal (%d): %s\n", n, held);
        Check(label, "after the removal the host holds no named object and no view", n == 0);
    }
}

/* output-ring-lap: the output ring holds 64 packets. A reader that took
 * none checks slot 1's SeqNo, 1, as SharedMemoryIO.TryReadOutputFrame
 * does, and the companion publishes packets 2 to 65, so packet 65 refills
 * slot 1. At each barrier in that refill the reader copies the slot and
 * checks the SeqNo again. It may give up there, but a copy it accepts
 * carries packet 1. Through e9a6e92d the refill wrote the fields under the
 * old SeqNo and published the new one last, so the reader's second check
 * passed for packet 65's bytes. */
#define HM_RING_SLOT(buf, i) ((buf) + 8 + (size_t)(i) * 264)
static uint8_t *g_ringBuf;
static int g_ringAccepted, g_ringMixed, g_ringLooks;

static void RingPublish(Ctl *c, uint8_t k)
{
    uint8_t rumble[5] = { 0x00, 0x08, k, 0x80, 0x00 };
    Ioctl(c->dev, "xusb.setstate", 0x8000A010, rumble, sizeof(rumble), 0, NULL, 0);
}

/* The rest of the reader's copy: the fields, then the SeqNo again. */
static void HookLappedReader(void)
{
    const uint8_t *slot = HM_RING_SLOT(g_ringBuf, 0);
    uint8_t low = slot[8 + 2];
    MemoryBarrier();
    uint32_t after;
    memcpy(&after, slot, 4);
    g_ringLooks++;
    if (after == 1) {
        g_ringAccepted++;
        if (low != 1) g_ringMixed++;
    }
    g_barrierHook = HookLappedReader;
}

static void OutputRingLap(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s output-ring-lap", p->id);
    if (StartPad(&c, p, "output-ring-lap", label, 0)) {
        g_ringAccepted = g_ringMixed = g_ringLooks = 0;
        for (int k = 1; k <= 64; k++) RingPublish(&c, (uint8_t)k);
        g_ringBuf = c.sdk.output ? c.sdk.output->buf : NULL;
        uint32_t head = 0, before = 0;
        if (g_ringBuf) {
            memcpy(&head, g_ringBuf, 4);
            memcpy(&before, HM_RING_SLOT(g_ringBuf, 0), 4);
        }
        Check(label, "control: 64 packets in the ring, and slot 1 holds packet 1",
              g_ringBuf && head == 64 && before == 1 && HM_RING_SLOT(g_ringBuf, 0)[8 + 2] == 1);
        g_barrierHook = HookLappedReader;
        RingPublish(&c, 65);
        g_barrierHook = NULL;
        Check(label, "the reader looked at slot 1 while packet 65 refilled it", g_ringLooks > 0);
        Check(label, "no copy it accepts carries another packet's bytes", g_ringMixed == 0);
        CtlStop(&c);
    }
}

/* no-file-object: hidmaestro_xusb.inf admits kernel-mode clients and null
 * file objects (UmdfFileObjectPolicy), so a WAIT_FOR_INPUT can come with no
 * file object. The companion declares its file objects optional, and
 * frame 1's report completes that request as any other, after the one sent
 * before it through a handle. Through e9a6e92d it failed with
 * STATUS_INVALID_DEVICE_REQUEST, which b0fb7dc9 had served. */
static void NoFileObject(PlanProfile *p)
{
    Ctl c;
    char label[160];
    snprintf(label, sizeof(label), "%s no-file-object", p->id);
    if (StartPad(&c, p, "no-file-object", label, 1)) {
        static const uint8_t req[4] = { 0x02, 0x01, 0x00, 0x00 };
        HmRequest *h = WaitOn(&c, 0);
        HmRequest *n = IoctlNoFile(c.dev, "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, req, sizeof(req), 29);
        Check(label, "the companion declares its file objects optional", c.dev->fileOptional);
        Check(label, "the request with no file object parks", n->inQueue && !n->completed);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        Check(label, "control: the request through a handle carries frame 1", Served(h, 0x01));
        Check(label, "frame 1's report completes the request with no file object after it", Served(n, 0x01) && n->doneNo > h->doneNo);
        CtlStop(&c);
    }
}

/* seqno-again: a program before 2.1.0 zeroes the input section as it maps
 * the index and counts its SeqNo from 0, so its first frame can repeat the
 * SeqNo of the last frame the program before it wrote. Here both programs
 * ring every frame, the first wrote one frame, SeqNo 2, a request parked
 * after its report, and nothing read the zeroed section. The next
 * program's first frame, SeqNo 2 again with another state, is a report
 * that request was waiting for. Through e9a6e92d the reader had SeqNo 2,
 * and the frame waited for the program's next one. */
static void SeqNoAgain(PlanProfile *p)
{
    Ctl c;
    char label[160];
    int saved = g_sdkSignal;
    g_sdkSignal = 0;
    snprintf(label, sizeof(label), "%s seqno-again", p->id);
    if (StartPad(&c, p, "seqno-again", label, 1)) {
        WaitOn(&c, 0);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        Check(label, "control: the first program's frame reaches the request", Served(g_ws[0], 0x01));
        uint32_t last = c.sdk.seq;
        HmRequest *r = WaitOn(&c, 0);
        memset(c.sdk.input->buf, 0, c.sdk.input->size);
        c.sdk.seq = last - 2;
        WriteGip(&c, 0x02);
        Settle(CtlThread(&c, 0));
        Check(label, "precondition: the next program's first frame repeats SeqNo 2", last == 2 && c.sdk.seq == last);
        Check(label, "its report reaches the request parked before it", Served(r, 0x02));
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

/* final-frame-before-repark: a request parked before frame 1 carries it.
 * Frame 2, a new state, is written while no request is parked, and the
 * reader then sends its next request. The doorbell, the timer and
 * GET_STATE run. A pad sends at most one report per poll of its interrupt
 * endpoint, so a reader that sends its next wait as soon as one completes
 * has it parked before the pad's next report. The SDK's frames come at
 * any pace. v2.0.3's timer served frame 2 to that request, and e9a6e92d
 * served it at the request's look. first-frame-before-park: frame 1 is
 * written before the reader's first request. */
static void FinalFrameBeforeRepark(PlanProfile *p, int legacy)
{
    Ctl c;
    char label[160];
    int saved = g_sdkSignal;
    if (legacy) g_sdkSignal = 0;
    snprintf(label, sizeof(label), "%s final-frame-before-repark%s", p->id, legacy ? "-legacy" : "");
    if (StartPad(&c, p, legacy ? "final-frame-before-repark-legacy" : "final-frame-before-repark", label, 1)) {
        HmRequest *w1 = WaitOn(&c, 0);
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        Check(label, "control: the request parked before frame 1 carries it", Served(w1, 0x01));
        WriteGip(&c, 0x02);
        Settle(CtlThread(&c, 0));
        HmRequest *w2 = WaitOn(&c, 0);
        Settle(CtlThread(&c, 0));
        TimerTick(&c);
        Settle(CtlThread(&c, 0));
        HmRequest *g = GetState(&c);
        Settle(CtlThread(&c, 0));
        Check(label, "control: GET_STATE reads frame 2", g->completed && g->out[12] == 0x20);
        Check(label, "the reader's next request carries frame 2, the newest state", Served(w2, 0x02));
        CtlStop(&c);
    }
    g_sdkSignal = saved;
}

static void FirstFrameBeforePark(PlanProfile *p, int legacy)
{
    Ctl c;
    char label[160];
    int saved = g_sdkSignal;
    if (legacy) g_sdkSignal = 0;
    snprintf(label, sizeof(label), "%s first-frame-before-park%s", p->id, legacy ? "-legacy" : "");
    if (StartPad(&c, p, legacy ? "first-frame-before-park-legacy" : "first-frame-before-park", label, 1)) {
        WriteGip(&c, 0x01);
        Settle(CtlThread(&c, 0));
        HmRequest *w1 = WaitOn(&c, 0);
        Settle(CtlThread(&c, 0));
        TimerTick(&c);
        Settle(CtlThread(&c, 0));
        HmRequest *g = GetState(&c);
        Settle(CtlThread(&c, 0));
        Check(label, "control: GET_STATE reads frame 1", g->completed && g->out[12] == 0x10);
        Check(label, "the reader's first request carries frame 1, the state there is", Served(w1, 0x01));
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
    OlderAfterNewer(ds);
    SecondLookOlderAfterNewer(ds);
    ReportEveryRequest(xusb, 1);
    ReportEveryRequest(xusb, 0);
    ParkedAfterReport(xusb, 1);
    ParkedAfterReport(xusb, 0);
    SameState(xusb);
    GetStateReports(xusb);
    TimerWithoutDoorbell(xusb);
    TimerBesideDoorbell(xusb);
    ParksDuringReport(xusb);
    LateAfterReport(xusb);
    DoorbellAndKick(xusb, 1);
    DoorbellAndKick(xusb, 0);
    KickWithoutDoorbell(xusb);
    ResumeAtOnce(xusb);
    InvalidRequests(xusb);
    CloseOneHandle(xusb);
    NoFileObject(xusb);
    SeqNoAgain(xusb);
    PacketRaceGetState(xusb);
    PacketRacePump(xusb, 1);
    PacketRacePump(xusb, 2);
    OutputRemap(xusb, 0);
    OutputRemap(xusb, 1);
    OutputRingLap(xusb);
    FinalFrameBeforeRepark(xusb, 0);
    FinalFrameBeforeRepark(xusb, 1);
    FirstFrameBeforePark(xusb, 0);
    FirstFrameBeforePark(xusb, 1);
    printf("wake checks %d failed %d\n", g_checks, g_failed);
    return g_failed;
}
