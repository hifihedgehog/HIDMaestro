/* The --lifecycle mode: what a driver holds of its controller index as
 * its device goes through PnP, checked rather than recorded.
 *
 * A driver host can see IRP_MN_SURPRISE_REMOVAL and never the
 * IRP_MN_REMOVE_DEVICE after it: the PnP manager sends the remove only
 * once every handle to the device is closed, and a second host started
 * for the same instance while the first one's device was being removed
 * kept index 0's output doorbell for 25 minutes and more in lane T's
 * sandbox. The framework runs the context cleanup only on the remove
 * (pnpstatemachine.cpp:1995-2058), so a driver that lets go of the index
 * only there keeps it in that host. Three sequences, for the HID driver
 * (a Switch Pro with its stream thread, a DualSense with the output and
 * PID sections) and for the XUSB companion:
 *   - start, then a surprise removal and no removal: the process holds
 *     no handle to a named object and no view of a section, every thread
 *     of the device has returned and its timer is stopped, and a request
 *     sent after it is refused and opens nothing again;
 *   - start, a rebalance stop and a restart: the device still holds its
 *     objects, its threads still run, and a frame, a read and an output
 *     report still pass through it;
 *   - start, a surprise removal, then the removal, and start then an
 *     orderly removal: nothing is left and nothing is closed twice (the
 *     stand-ins stop the run on a second CloseHandle, UnmapViewOfFile or
 *     DeleteCriticalSection).
 * Before each removal the same check lists what the device holds, the
 * positive control that the list shows a held object at all. */
#include "hm_scenario.h"

#define IOCTL_XUSB_GET_STATE        0x8000E00C
#define IOCTL_XUSB_SET_STATE        0x8000A010
#define IOCTL_XUSB_WAIT_FOR_INPUT   0x8000E3AC

static int g_failed;
static int g_checks;

static void Check(const char *what, int ok, const char *detail)
{
    g_checks++;
    if (ok) {
        printf("[PASS] %s\n", what);
        return;
    }
    g_failed++;
    printf("[FAIL] %s%s%s\n", what, detail && *detail ? ": " : "", detail ? detail : "");
    fprintf(stderr, "[FAIL] %s\n", what);
}

/* The names a device on this index is expected to hold once it ran,
 * each found in what HeldObjects lists. */
static void CheckHolds(const char *label, const char *const *names, int count)
{
    static char held[8192];
    char what[256];
    HeldObjects(held, sizeof(held));
    for (int i = 0; i < count; i++) {
        snprintf(what, sizeof(what), "%s: the device holds %s", label, names[i]);
        Check(what, strstr(held, names[i]) != NULL, held);
    }
}

/* Nothing of the index is held, every thread of the device has returned
 * and its timer is stopped. */
static void CheckReleased(const char *label, HmDevice *dev)
{
    static char held[8192];
    char what[256];
    int n = HeldObjects(held, sizeof(held));
    snprintf(what, sizeof(what), "%s: the process holds no named object and no view", label);
    Check(what, n == 0, held);
    for (int i = 0; i < dev->threadCount; i++) {
        snprintf(what, sizeof(what), "%s: thread %d of the device has returned", label, i);
        Check(what, dev->threads[i]->exited, "it still waits");
    }
    for (int i = 0; i < dev->timerCount; i++) {
        snprintf(what, sizeof(what), "%s: timer %d of the device is stopped", label, i);
        Check(what, !dev->timers[i]->started, "it still runs");
    }
}

/* A request after the surprise removal: the I/O queues are purged, so the
 * framework refuses it and nothing of the index opens again. A queue left
 * dispatching would let the driver map a section or make the doorbell
 * once more. */
static void CheckRefused(const char *label, const char *request, HmRequest *r)
{
    static char held[8192];
    char what[256];
    int n = HeldObjects(held, sizeof(held));
    snprintf(what, sizeof(what), "%s: %s after it is refused and opens nothing", label, request);
    Check(what, r->refused && n == 0, r->refused ? held : "the driver took the request");
}

static uint32_t RingHead(Ctl *c)
{
    uint32_t head;
    memcpy(&head, c->sdk.output->buf, 4);
    return head;
}

/* ------------------------------------------------------------------ */
/*  The HID driver                                                     */
/* ------------------------------------------------------------------ */

static const char *const k_hidNames[] = {
    "event handle Global\\HIDMaestroInputEvent0",
    "event handle Global\\HIDMaestroStopEvent0",
    "event handle Global\\HIDMaestroOutputEvent0",
    "event handle Global\\HIDMaestroInputAckEvent0",
    "section handle Global\\HIDMaestroInput0",
    "mapped view of Global\\HIDMaestroInput0",
    "section handle Global\\HIDMaestroInputAck0",
    "mapped view of Global\\HIDMaestroInputAck0",
    "section handle Global\\HIDMaestroOutput0",
    "mapped view of Global\\HIDMaestroOutput0",
    "pipe client of \\\\.\\pipe\\HIDMaestroOwner0",
};
static const char *const k_pidNames[] = {
    "section handle Global\\HIDMaestroPidState0",
    "mapped view of Global\\HIDMaestroPidState0",
};

/* The SDK's objects with the PID section, the configuration, the add and
 * the start. */
static int HidStart(Ctl *c, PlanProfile *p)
{
    memset(c, 0, sizeof(*c));
    c->p = p;
    SdkCreate(&c->sdk, 0, SDK_ALL);
    if (p->hasPidRids) SdkPidCreate(&c->sdk, p->pidRids);
    ConfigWrite(p, 0);
    c->dev = DeviceAdd(0);
    if (!c->dev) return 0;
    DeviceStart(c->dev);
    return 1;
}

static int IsSwitch(Ctl *c) { return !strcmp(c->p->group, "switch"); }

/* A frame through a parked read: the worker maps the input section and
 * the acknowledgement beside it, connects the owner watch and makes the
 * doorbell (driver.c:2131-2150, 625-640). Returns whether the read came
 * back with the frame. */
static int HidFrame(Ctl *c, int n)
{
    uint8_t data[64];
    for (int i = 0; i < 64; i++) data[i] = (uint8_t)(n * 16 + i);
    uint32_t len = c->p->readLength ? c->p->readLength : 64;
    HmRequest *r = HidRead(c, len);
    Frame(c, data, IsSwitch(c) ? 48 : 63, NULL, NULL, 0);
    if (IsSwitch(c)) SwitchTick(c);
    return r->completed && r->info > 0;
}

/* An output report, which the driver publishes to the output ring
 * (driver.c:2901-2950), and a PID feature read, which maps the PID
 * section (driver.c:3405-3420). Returns whether the ring moved. */
static int HidOutput(Ctl *c)
{
    uint32_t before = RingHead(c);
    uint8_t report[48] = { 0 };
    report[0] = IsSwitch(c) ? 0x10 : 0x02;
    HidWrite(c, report, sizeof(report));
    if (c->p->hasPidRids) HidGetFeature(c, c->p->pidRids[0], 64);
    return RingHead(c) != before;
}

static void HidHolds(Ctl *c, const char *label)
{
    CheckHolds(label, k_hidNames, (int)ARRAYSIZE(k_hidNames));
    if (c->p->hasPidRids) CheckHolds(label, k_pidNames, (int)ARRAYSIZE(k_pidNames));
}

static void HidSurpriseNoRemove(PlanProfile *p)
{
    Ctl c;
    char label[128];
    snprintf(label, sizeof(label), "%s surprise removal, no removal", p->id);
    StreamBegin("lifecycle/%s/surprise-no-remove", p->id);
    if (!HidStart(&c, p)) { Check(label, 0, "the device add failed"); return; }
    Check("the read takes the frame", HidFrame(&c, 1), NULL);
    Check("the output report reaches the ring", HidOutput(&c), NULL);
    HidHolds(&c, label);
    DeviceSurpriseRemove(c.dev);
    CheckReleased(label, c.dev);
    uint8_t report[48] = { 0 };
    report[0] = IsSwitch(&c) ? 0x10 : 0x02;
    CheckRefused(label, "an output report", HidWrite(&c, report, sizeof(report)));
    if (p->hasPidRids) CheckRefused(label, "a PID feature read", HidGetFeature(&c, p->pidRids[0], 64));
}

static void HidRebalance(PlanProfile *p)
{
    Ctl c;
    char label[128];
    snprintf(label, sizeof(label), "%s stop and restart", p->id);
    StreamBegin("lifecycle/%s/rebalance", p->id);
    if (!HidStart(&c, p)) { Check(label, 0, "the device add failed"); return; }
    HidFrame(&c, 1);
    HidOutput(&c);
    DeviceStop(c.dev);
    DeviceStart(c.dev);
    HidHolds(&c, label);
    for (int i = 0; i < c.dev->threadCount; i++) {
        char what[160];
        snprintf(what, sizeof(what), "%s: thread %d still runs", label, i);
        Check(what, !c.dev->threads[i]->exited, "it returned");
    }
    snprintf(label, sizeof(label), "%s stop and restart: a read takes the next frame", p->id);
    Check(label, HidFrame(&c, 2), NULL);
    snprintf(label, sizeof(label), "%s stop and restart: an output report reaches the ring", p->id);
    Check(label, HidOutput(&c), NULL);
    DeviceRemove(c.dev);
    snprintf(label, sizeof(label), "%s stop, restart, removal", p->id);
    CheckReleased(label, c.dev);
}

static void HidSurpriseThenRemove(PlanProfile *p, int surprise)
{
    Ctl c;
    char label[128];
    snprintf(label, sizeof(label), "%s %s", p->id, surprise ? "surprise removal, then removal" : "orderly removal");
    StreamBegin("lifecycle/%s/%s", p->id, surprise ? "surprise-then-remove" : "remove");
    if (!HidStart(&c, p)) { Check(label, 0, "the device add failed"); return; }
    HidFrame(&c, 1);
    HidOutput(&c);
    HidHolds(&c, label);
    if (surprise) DeviceSurpriseRemove(c.dev);
    DeviceRemove(c.dev);
    CheckReleased(label, c.dev);
}

/* ------------------------------------------------------------------ */
/*  The XUSB companion                                                 */
/* ------------------------------------------------------------------ */

static const char *const k_companionNames[] = {
    "event handle Global\\HIDMaestroCompanionInputEvent0",
    "event handle Global\\HIDMaestroOutputEvent0",
    "section handle Global\\HIDMaestroInput0",
    "mapped view of Global\\HIDMaestroInput0",
    "section handle Global\\HIDMaestroOutput0",
    "mapped view of Global\\HIDMaestroOutput0",
};

static int CompanionStart(Ctl *c, PlanProfile *p)
{
    if (!CtlStart(c, p, 0, SDK_ALL, 1)) return 0;
    DeviceStart(c->dev);
    return 1;
}

static void Tick(Ctl *c)
{
    HmTimer *t = c->dev->timers[0];
    if (!t->started) return;
    g_clockMs += t->period;
    t->fn((WDFTIMER)t);
}

/* A GIP frame wakes the input pump, a parked WAIT_FOR_INPUT completes on
 * the timer's tick, which maps the input section (companion.c:177-189),
 * and a rumble packet goes to the output ring, which maps the output
 * section and makes the doorbell (companion.c:362-401). Returns whether
 * the wait completed and the ring moved. */
static int CompanionTraffic(Ctl *c, uint8_t button)
{
    uint8_t gip[14] = { 0xFF, 0x7F, 0xFF, 0x7F, 0xFF, 0x7F, 0xFF, 0x7F, 0, 0, 0, 0, button, 0 };
    Frame(c, NULL, 0, gip, NULL, 0);
    ThreadResume(CtlThread(c, 0));
    HmRequest *w = Ioctl(c->dev, "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, NULL, 0, 29, NULL, 0);
    Tick(c);
    uint32_t before = RingHead(c);
    uint8_t rumble[5] = { 0, 8, 0x40, 0x80, 0 };
    Ioctl(c->dev, "xusb.setstate", IOCTL_XUSB_SET_STATE, rumble, sizeof(rumble), 0, NULL, 0);
    return w->completed && w->info == 29 && RingHead(c) != before;
}

static void CompanionSurpriseNoRemove(PlanProfile *p)
{
    Ctl c;
    char label[128];
    snprintf(label, sizeof(label), "%s companion surprise removal, no removal", p->id);
    StreamBegin("lifecycle/%s/companion-surprise-no-remove", p->id);
    if (!CompanionStart(&c, p)) { Check(label, 0, "the device add failed"); return; }
    Check("the companion serves its input and output", CompanionTraffic(&c, 0x01), NULL);
    CheckHolds(label, k_companionNames, (int)ARRAYSIZE(k_companionNames));
    DeviceSurpriseRemove(c.dev);
    CheckReleased(label, c.dev);
    uint8_t rumble[5] = { 0, 8, 0x40, 0x80, 0 };
    CheckRefused(label, "a rumble packet",
                 Ioctl(c.dev, "xusb.setstate", IOCTL_XUSB_SET_STATE, rumble, sizeof(rumble), 0, NULL, 0));
    CheckRefused(label, "a state read",
                 Ioctl(c.dev, "xusb.state", IOCTL_XUSB_GET_STATE, NULL, 0, 29, NULL, 0));
}

static void CompanionRebalance(PlanProfile *p)
{
    Ctl c;
    char label[128];
    snprintf(label, sizeof(label), "%s companion stop and restart", p->id);
    StreamBegin("lifecycle/%s/companion-rebalance", p->id);
    if (!CompanionStart(&c, p)) { Check(label, 0, "the device add failed"); return; }
    CompanionTraffic(&c, 0x01);
    DeviceStop(c.dev);
    DeviceStart(c.dev);
    CheckHolds(label, k_companionNames, (int)ARRAYSIZE(k_companionNames));
    Check("the companion's pump thread still runs", !CtlThread(&c, 0)->exited, "it returned");
    Check("the companion's timer still runs", c.dev->timers[0]->started, "it was stopped");
    Check("after the restart the companion serves its input and output", CompanionTraffic(&c, 0x02), NULL);
    DeviceRemove(c.dev);
    snprintf(label, sizeof(label), "%s companion stop, restart, removal", p->id);
    CheckReleased(label, c.dev);
}

static void CompanionSurpriseThenRemove(PlanProfile *p, int surprise)
{
    Ctl c;
    char label[128];
    snprintf(label, sizeof(label), "%s companion %s", p->id, surprise ? "surprise removal, then removal" : "orderly removal");
    StreamBegin("lifecycle/%s/companion-%s", p->id, surprise ? "surprise-then-remove" : "remove");
    if (!CompanionStart(&c, p)) { Check(label, 0, "the device add failed"); return; }
    CompanionTraffic(&c, 0x01);
    CheckHolds(label, k_companionNames, (int)ARRAYSIZE(k_companionNames));
    if (surprise) DeviceSurpriseRemove(c.dev);
    DeviceRemove(c.dev);
    CheckReleased(label, c.dev);
}

int CaseLifecycle(void)
{
    PlanProfile *hid[] = { PlanFind("switch", "switch-pro"), PlanFind("sony", "dualsense") };
    PlanProfile *xusb = PlanFind("xusb", "xbox-360-wired");
    for (int i = 0; i < (int)ARRAYSIZE(hid); i++) {
        if (!hid[i]) HmFail("the plan lacks a lifecycle profile");
        HidSurpriseNoRemove(hid[i]);
        HidRebalance(hid[i]);
        HidSurpriseThenRemove(hid[i], 1);
        HidSurpriseThenRemove(hid[i], 0);
    }
    if (!xusb) HmFail("the plan lacks xbox-360-wired");
    CompanionSurpriseNoRemove(xusb);
    CompanionRebalance(xusb);
    CompanionSurpriseThenRemove(xusb, 1);
    CompanionSurpriseThenRemove(xusb, 0);
    printf("lifecycle checks %d failed %d\n", g_checks, g_failed);
    return g_failed;
}
