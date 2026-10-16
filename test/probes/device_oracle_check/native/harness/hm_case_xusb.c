/* Case 5, the XUSB companion (companion.c) for each catalog profile that
 * needs one at 9bd6279 (ControllerProfile.RequiresXusbCompanion), with
 * the profile's registry values as the SDK writes them.
 *
 * IOCTL codes are the companion's own (companion.c:29-38). The GIP slice
 * is the 14 bytes HMController packs (HMController.cs:869-917) and
 * DecodeGipToXInput reads (companion.c:608-650): four 16-bit sticks,
 * two 10-bit triggers, the button bytes with the hat at bits 2-5 of the
 * second. */
#include "hm_scenario.h"

#define IOCTL_XUSB_GET_INFORMATION    0x80006000
#define IOCTL_XUSB_GET_CAPABILITIES   0x8000E004
#define IOCTL_XUSB_GET_LED_STATE      0x8000E008
#define IOCTL_XUSB_GET_STATE          0x8000E00C
#define IOCTL_XUSB_SET_STATE          0x8000A010
#define IOCTL_XUSB_WAIT_GUIDE         0x8000E014
#define IOCTL_XUSB_GET_BATTERY_INFO   0x8000E018
#define IOCTL_XUSB_GET_INFORMATION_EX 0x8000E3FC
#define IOCTL_XUSB_WAIT_FOR_INPUT     0x8000E3AC
#define IOCTL_XUSB_POWER_INFO         0x80006380

static HmRng g_rng;

/* Output sizes that cross each boundary the companion tests: 3, 12, 24,
 * 29, 36 and 64 bytes. */
static const uint32_t k_sizes[] = { 0, 1, 2, 3, 11, 12, 23, 24, 28, 29, 35, 36, 63, 64, 65 };

static const struct { ULONG code; const char *tag; } k_ioctls[] = {
    { IOCTL_XUSB_GET_INFORMATION,    "xusb.information" },
    { IOCTL_XUSB_GET_CAPABILITIES,   "xusb.capabilities" },
    { IOCTL_XUSB_GET_LED_STATE,      "xusb.led" },
    { IOCTL_XUSB_GET_STATE,          "xusb.state" },
    { IOCTL_XUSB_WAIT_GUIDE,         "xusb.waitguide" },
    { IOCTL_XUSB_GET_BATTERY_INFO,   "xusb.battery" },
    { IOCTL_XUSB_GET_INFORMATION_EX, "xusb.informationex" },
    { IOCTL_XUSB_POWER_INFO,         "xusb.power" },
    { 0x80006004,                    "xusb.unknown" },
    { 0x8000E3B0,                    "xusb.unknown" },
};

static HmRequest *X(Ctl *c, const char *tag, ULONG code, const void *in, size_t inLen, size_t outLen)
{
    return Ioctl(c->dev, tag, code, in, inLen, outLen, NULL, 0);
}

static void Gip(Ctl *c, const uint8_t gip[14])
{
    Frame(c, NULL, 0, gip, NULL, 0);
}

/* One period of the companion's pump timer: the clock moves by the period
 * the companion created the timer with (companion.c:559), then the
 * callback runs. */
static void TimerTick(Ctl *c)
{
    HmTimer *t = c->dev->timerCount ? c->dev->timers[0] : NULL;
    if (!t || !t->started) return;
    g_clockMs += t->period;
    t->fn((WDFTIMER)t);
}

static void Doorbell(Ctl *c)
{
    if (c->dev->threadCount) ThreadResume(c->dev->threads[0]);
}

static void PutU16(uint8_t *g, int at, uint16_t v) { g[at] = (uint8_t)v; g[at + 1] = (uint8_t)(v >> 8); }

/* A centered slice, as HMController packs a resting pad: sticks at
 * 32767, triggers 0. */
static void Rest(uint8_t g[14])
{
    memset(g, 0, 14);
    for (int i = 0; i < 4; i++) PutU16(g, i * 2, 0x7FFF);
}

static void StreamAnswers(PlanProfile *p, int index)
{
    Ctl c;
    StreamBegin("xusb/%s/answers-idx%d", p->id, index);
    if (!CtlStart(&c, p, index, SDK_ALL, 1)) return;
    uint8_t g[14];
    Rest(g);
    Gip(&c, g);
    for (int i = 0; i < (int)ARRAYSIZE(k_ioctls); i++) {
        if (i == (int)ARRAYSIZE(k_ioctls) / 2) VersionMidRun(&c);
        for (int s = 0; s < (int)ARRAYSIZE(k_sizes); s++)
            X(&c, k_ioctls[i].tag, k_ioctls[i].code, NULL, 0, k_sizes[s]);
    }
    /* Input buffers do not matter to the getters: one with input. */
    uint8_t in[8] = { 1, 2, 3, 4, 5, 6, 7, 8 };
    X(&c, "xusb.state", IOCTL_XUSB_GET_STATE, in, sizeof(in), 29);
    X(&c, "xusb.capabilities", IOCTL_XUSB_GET_CAPABILITIES, in, sizeof(in), 36);
    for (int s = 0; s < (int)ARRAYSIZE(k_sizes); s++) {
        X(&c, "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, NULL, 0, k_sizes[s]);
        TimerTick(&c);
    }
    CtlStop(&c);
}

static void StateBoth(Ctl *c)
{
    X(c, "xusb.state", IOCTL_XUSB_GET_STATE, NULL, 0, 29);
    X(c, "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, NULL, 0, 29);
    TimerTick(c);
}

static void StreamStates(PlanProfile *p)
{
    Ctl c;
    uint8_t g[14];
    StreamBegin("xusb/%s/states-scripted", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 1)) return;
    /* Before any frame: the section holds zeros. */
    StateBoth(&c);
    Rest(g);
    Gip(&c, g);
    StateBoth(&c);
    StateBoth(&c);            /* unchanged: the packet number holds */
    for (int byte = 12; byte <= 13; byte++)
        for (int bit = 0; bit < 8; bit++) {
            Rest(g);
            g[byte] = (uint8_t)(1 << bit);
            Gip(&c, g);
            StateBoth(&c);
        }
    for (int hat = 0; hat < 16; hat++) {
        Rest(g);
        g[13] = (uint8_t)(hat << 2);
        Gip(&c, g);
        StateBoth(&c);
    }
    static const uint16_t sticks[] = { 0x0000, 0x0001, 0x7FFE, 0x7FFF, 0x8000, 0x8001, 0xFFFE, 0xFFFF };
    for (int axis = 0; axis < 4; axis++)
        for (int v = 0; v < (int)ARRAYSIZE(sticks); v++) {
            Rest(g);
            PutU16(g, axis * 2, sticks[v]);
            Gip(&c, g);
            StateBoth(&c);
        }
    static const uint16_t triggers[] = { 0x0000, 0x0001, 0x0004, 0x0005, 0x01FF, 0x0200, 0x03FE, 0x03FF, 0x0400, 0x07FF, 0xFFFF };
    for (int t = 0; t < 2; t++)
        for (int v = 0; v < (int)ARRAYSIZE(triggers); v++) {
            Rest(g);
            PutU16(g, 8 + t * 2, triggers[v]);
            Gip(&c, g);
            StateBoth(&c);
        }
    /* The doorbell completes a parked WAIT_FOR_INPUT as the frame lands
     * (companion.c:780-819). */
    for (int i = 0; i < 6; i++) {
        X(&c, "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, NULL, 0, 29);
        Rest(g);
        g[12] = (uint8_t)(1 << i);
        Gip(&c, g);
        Doorbell(&c);
    }
    /* Two parked, one frame, then the timer. */
    X(&c, "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, NULL, 0, 29);
    X(&c, "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, NULL, 0, 29);
    Gip(&c, g);
    Doorbell(&c);
    TimerTick(&c);
    TimerTick(&c);
    CtlStop(&c);
}

/* The seqlock: a slice read while the SDK is mid-write serves the last
 * good one (companion.c:204-212), and 500 reads with one SeqNo re-map the
 * section (companion.c:227-239). */
static void StreamSeqlock(PlanProfile *p)
{
    Ctl c;
    uint8_t g[14];
    StreamBegin("xusb/%s/seqlock", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 1)) return;
    /* Odd SeqNo before any good read: no data. */
    {
        uint32_t odd = 1;
        memcpy(c.sdk.input->buf, &odd, 4);
        X(&c, "xusb.state", IOCTL_XUSB_GET_STATE, NULL, 0, 29);
        uint32_t zero = 0;
        memcpy(c.sdk.input->buf, &zero, 4);
    }
    Rest(g);
    g[12] = 0x01;
    Gip(&c, g);
    X(&c, "xusb.state", IOCTL_XUSB_GET_STATE, NULL, 0, 29);
    /* Mid-write: odd SeqNo and new bytes. */
    {
        uint32_t odd = c.sdk.seq + 1;
        memcpy(c.sdk.input->buf, &odd, 4);
        c.sdk.input->buf[264 + 12] = 0x80;
        X(&c, "xusb.state", IOCTL_XUSB_GET_STATE, NULL, 0, 29);
        X(&c, "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, NULL, 0, 29);
        TimerTick(&c);
        memcpy(c.sdk.input->buf, &c.sdk.seq, 4);
        c.sdk.input->buf[264 + 12] = 0x01;
    }
    for (int i = 0; i < 503; i++) X(&c, "xusb.state", IOCTL_XUSB_GET_STATE, NULL, 0, 29);
    g[12] = 0x02;
    Gip(&c, g);
    X(&c, "xusb.state", IOCTL_XUSB_GET_STATE, NULL, 0, 29);
    CtlStop(&c);
}

static void StreamRandom(PlanProfile *p)
{
    Ctl c;
    StreamBegin("xusb/%s/states-random", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 1)) return;
    for (int i = 0; i < 3000; i++) {
        int r = RngNext(&g_rng, 10);
        uint8_t g[14];
        if (r < 4) {
            RngFill(&g_rng, g, sizeof(g));
            if (RngNext(&g_rng, 4) == 0) Rest(g);
            Gip(&c, g);
            if (RngNext(&g_rng, 2)) Doorbell(&c);
        } else if (r < 6) {
            X(&c, "xusb.state", IOCTL_XUSB_GET_STATE, NULL, 0, 29);
        } else if (r < 8) {
            X(&c, "xusb.waitforinput", IOCTL_XUSB_WAIT_FOR_INPUT, NULL, 0, 29);
        } else {
            TimerTick(&c);
        }
    }
    CtlStop(&c);
}

/* SET_STATE's rumble gate (companion.c:928-983): published only while the
 * registry's VendorId for the index is 0x045E, read again at most every
 * 500 ms on the harness clock. */
static void StreamRumble(PlanProfile *p)
{
    Ctl c;
    StreamBegin("xusb/%s/rumble-gate", p->id);
    g_clockMs = 1000;
    if (!CtlStart(&c, p, 0, SDK_ALL, 1)) return;
    /* xinput's SET_STATE input is the vibration packet, sent at every
     * length from 0 to 8. */
    for (uint32_t len = 0; len <= 8; len++) {
        uint8_t in[8] = { 0x00, 0x08, 0x00, (uint8_t)(0x10 * len), (uint8_t)(0xF0 - len), 0x00, 0x11, 0x22 };
        X(&c, "xusb.setstate", IOCTL_XUSB_SET_STATE, in, len, 0);
    }
    static const struct { int64_t at; int vid; } steps[] = {
        /* vid: 0 the profile's, 1 another vendor, 2 absent, 3 a 32-bit value
         * whose low word is 0x045E, 4 one whose high word is: the gate
         * compares (USHORT)val (companion.c:962) */
        { 0, 1 }, { 499, 1 }, { 500, 1 }, { 999, 2 }, { 1000, 2 }, { 1499, 0 }, { 1500, 0 },
        { 1600, 1 }, { 1999, 1 }, { 2000, 1 }, { 2500, 0 }, { 2999, 0 }, { 3000, 0 },
        { 3500, 1 }, { 4000, 3 }, { 4499, 4 }, { 4500, 4 }, { 5000, 3 }, { 5500, 0 },
    };
    uint64_t base = g_clockMs;
    for (int i = 0; i < (int)ARRAYSIZE(steps); i++) {
        if (i == (int)ARRAYSIZE(steps) / 2) VersionMidRun(&c);
        g_clockMs = base + (uint64_t)steps[i].at;
        if (steps[i].vid == 0) ConfigWrite(p, 0);
        else if (steps[i].vid == 1) ConfigOverrideDword(0, "VendorId", 0x054C);
        else if (steps[i].vid == 3) ConfigOverrideDword(0, "VendorId", 0x1234045E);
        else if (steps[i].vid == 4) ConfigOverrideDword(0, "VendorId", 0x045E0000);
        else ConfigDropValue(0, "VendorId");
        uint8_t in[5] = { 0x00, 0x08, 0x00, (uint8_t)i, (uint8_t)(0xFF - i) };
        X(&c, "xusb.setstate", IOCTL_XUSB_SET_STATE, in, sizeof(in), 0);
        X(&c, "xusb.setstate", IOCTL_XUSB_SET_STATE, in, 4, 0);
    }
    CtlStop(&c);
}

void CaseXusb(void)
{
    RngInit(&g_rng, g_seed, 0x5401);
    int n = 0;
    for (int i = 0; i < g_plan.profileCount; i++) {
        PlanProfile *p = &g_plan.profiles[i];
        if (strcmp(p->group, "xusb")) continue;
        n++;
        StreamAnswers(p, 0);
        StreamAnswers(p, 3);
        StreamStates(p);
        StreamSeqlock(p);
        StreamRandom(p);
        StreamRumble(p);
    }
    if (n == 0) HmFail("the plan has no xusb profiles");
}

/* The companion under each version parameter: its answers at index 0 and
 * the rumble gate, on every xusb profile (hm_case_version.c). */
void CaseXusbVersion(void)
{
    for (int i = 0; i < g_plan.profileCount; i++) {
        PlanProfile *p = &g_plan.profiles[i];
        if (strcmp(p->group, "xusb")) continue;
        StreamAnswers(p, 0);
        StreamRumble(p);
    }
}

void XusbAnswerSweep(Ctl *c)
{
    uint8_t g[14];
    Rest(g);
    Gip(c, g);
    for (int i = 0; i < (int)ARRAYSIZE(k_ioctls); i++)
        for (int s = 0; s < (int)ARRAYSIZE(k_sizes); s++)
            X(c, k_ioctls[i].tag, k_ioctls[i].code, NULL, 0, k_sizes[s]);
    for (uint32_t len = 0; len <= 8; len++) {
        uint8_t in[8] = { 0x00, 0x08, 0x00, (uint8_t)(0x10 * len), (uint8_t)(0xF0 - len), 0x00, 0x11, 0x22 };
        X(c, "xusb.setstate", IOCTL_XUSB_SET_STATE, in, len, 0);
    }
}
