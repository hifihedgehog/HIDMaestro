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

void CaseThreads(void)
{
    PlanProfile *sw = PlanFind("switch", NULL);
    PlanProfile *ds = NULL;
    for (int i = 0; i < g_plan.profileCount && !ds; i++)
        if (!strcmp(g_plan.profiles[i].group, "sony") && PlanDword(&g_plan.profiles[i], "Bluetooth", 0))
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
}
