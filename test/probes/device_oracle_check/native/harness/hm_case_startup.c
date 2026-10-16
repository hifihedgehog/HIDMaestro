/* Case 5, the startup path both drivers run: EvtDeviceAdd
 * (driver.c:2465-2720, with InitInstancePaths and ReadConfigFromRegistry)
 * and CompanionDeviceAdd (companion.c:441-592), with each configuration
 * value present and absent, FunctionMode under Controller0, the defaults
 * when the configuration key is missing, and the objects the SDK makes
 * present or not. What the device then answers is recorded: the HID
 * class's start queries, one frame through a read, the version and
 * acknowledgement sections, the registry values the driver writes. */
#include "hm_scenario.h"

enum Variant {
    V_FULL = 0,
    V_DROP_VALUE,          /* one configuration value absent */
    V_NO_KEY,              /* neither Controller<N> nor SOFTWARE\HIDMaestro */
    V_PARENT_ONLY,         /* the values in SOFTWARE\HIDMaestro, no Controller<N> */
    V_BAD_VALUE,           /* one value of another type or out of range */
    V_NO_SDK,              /* the device starts before the SDK made anything */
    V_NO_PIPE,             /* sections and events, no owner pipe */
    V_WATCH_ENDED,         /* the acknowledgement carries the ended marker */
    V_OWNER_GONE,          /* the owner pipe closes after the start */
    V_EVENTS_HOST,         /* the named events exist, LocalService's with a DACL */
    V_EVENTS_USER,         /* the named events exist, another program's */
    V_EVENTS_NULL_DACL,    /* the named events exist, LocalService's with a NULL DACL */
    V_READD,               /* removed and added again on the index */
};

static void Widen(const char *s, WCHAR *dst, size_t cap)
{
    size_t i = 0;
    for (; s[i] && i + 1 < cap; i++) dst[i] = (WCHAR)(unsigned char)s[i];
    dst[i] = 0;
}

static void PreEvents(int index, int owner, int hasDacl)
{
    WCHAR n[96];
    swprintf_s(n, ARRAYSIZE(n), L"Global\\HIDMaestroStopEvent%d", index);
    Hold(&EventNew(n, TRUE, TRUE, owner, hasDacl)->o);
    swprintf_s(n, ARRAYSIZE(n), L"Global\\HIDMaestroOutputEvent%d", index);
    HmEvent *d = EventNew(n, FALSE, FALSE, owner, hasDacl);
    d->role = EV_DOORBELL;
    Hold(&d->o);
}

/* After the start: what the HID class asks first, one frame through a
 * parked read, the polled input report, the sections the driver wrote. */
void DriverProbe(Ctl *c)
{
    HidStartQueries(c);
    uint32_t readLen = c->p && c->p->readLength ? c->p->readLength : 64;
    HidRead(c, readLen);
    if (c->sdk.input) {
        uint8_t data[256];
        for (int i = 0; i < 256; i++) data[i] = (uint8_t)(i * 7 + 3);
        Frame(c, data, 64, NULL, NULL, 0);
        HidRead(c, readLen);
        HidGetInput(c, 0x01, readLen);
        HidGetInput(c, 0x00, readLen);
        Frame(c, data + 64, 64, NULL, NULL, 0);
        /* The extended lane (driver.c:1951-1958), as an armed Sony
         * Bluetooth profile writes it: 78 bytes, report id first, here
         * with a zeroed run where motion sits in a DualSense 0x31 report,
         * as a frame whose motion is off carries. Then the lane cleared
         * again. */
        uint8_t ext[78];
        memcpy(ext, data + 128, sizeof(ext));
        ext[0] = 0x31;
        memset(ext + 16, 0, 12);
        HidRead(c, readLen);
        Frame(c, NULL, 0, NULL, ext, sizeof(ext));
        HidRead(c, readLen);
        Frame(c, data, 16, NULL, NULL, 0);
    }
    RecordSections(&c->sdk);
}

void CompanionProbe(Ctl *c)
{
    static const ULONG codes[] = { 0x80006000, 0x8000E004, 0x8000E3FC, 0x8000E00C };
    static const uint32_t sizes[] = { 12, 24, 36, 64, 29 };
    for (int i = 0; i < (int)ARRAYSIZE(codes); i++)
        Ioctl(c->dev, "xusb.query", codes[i], NULL, 0, sizes[i == 3 ? 4 : i], NULL, 0);
    Ioctl(c->dev, "xusb.query", 0x8000E004, NULL, 0, 36, NULL, 0);
    uint8_t rumble[5] = { 0, 8, 0, 0x40, 0x80 };
    Ioctl(c->dev, "xusb.setstate", 0x8000A010, rumble, 5, 0, NULL, 0);
}

static void Startup(PlanProfile *p, int companion, int index, int variant, const char *value, const char *label)
{
    Ctl c;
    StreamBegin("startup/%s/%s-%s", p ? p->id : "none", companion ? "companion" : "driver", label);
    memset(&c, 0, sizeof(c));
    c.p = p;
    c.index = index;
    c.companion = companion;

    int sdkFlags = variant == V_NO_SDK ? SDK_NONE : variant == V_NO_PIPE ? (SDK_SECTIONS | SDK_OUTPUT) : SDK_ALL;
    SdkCreate(&c.sdk, index, sdkFlags);
    if (variant == V_WATCH_ENDED) {
        uint32_t ended = 0x80000000u;
        memcpy(c.sdk.ack->buf + 4, &ended, 4);
    }
    if (variant == V_EVENTS_HOST) PreEvents(index, OWN_LOCALSERVICE, TRUE);
    if (variant == V_EVENTS_USER) PreEvents(index, OWN_USER, TRUE);
    if (variant == V_EVENTS_NULL_DACL) PreEvents(index, OWN_LOCALSERVICE, FALSE);

    if (p && variant != V_NO_KEY) {
        ConfigWrite(p, index);
        WCHAR path[96], name[64];
        swprintf_s(path, ARRAYSIZE(path), L"SOFTWARE\\HIDMaestro\\Controller%d", index);
        if (variant == V_DROP_VALUE) {
            ConfigDropValue(index, value);
        } else if (variant == V_PARENT_ONLY) {
            HmRegKey *parent = KeyMake(L"SOFTWARE\\HIDMaestro");
            for (int i = 0; i < p->valueCount; i++) {
                Widen(p->values[i].name, name, ARRAYSIZE(name));
                ValueSet(parent, name, p->values[i].type, p->values[i].data, p->values[i].size);
            }
            KeyDrop(path);
        } else if (variant == V_BAD_VALUE) {
            HmRegKey *k = KeyMake(path);
            Widen(value, name, ARRAYSIZE(name));
            if (!strcmp(label, "bad-descriptor-4097") || !strcmp(label, "bad-descriptor-4096")) {
                size_t n = !strcmp(label, "bad-descriptor-4097") ? 4097 : 4096;
                uint8_t *d = (uint8_t *)calloc(1, n);
                PlanValue *v = PlanValueFind(p, "ReportDescriptor");
                if (v) memcpy(d, v->data, v->size);
                ValueSet(k, name, REG_BINARY, d, (DWORD)n);
                free(d);
            } else if (!strcmp(label, "bad-descriptor-empty")) {
                ValueSet(k, name, REG_BINARY, "", 0);
            } else if (!strcmp(label, "bad-productstring-long")) {
                WCHAR s[200];
                for (int i = 0; i < 199; i++) s[i] = (WCHAR)('A' + i % 26);
                s[199] = 0;
                ValueSet(k, name, REG_SZ, s, sizeof(s));
            } else if (!strcmp(label, "bad-productstring-128")) {
                WCHAR s[128];
                for (int i = 0; i < 127; i++) s[i] = (WCHAR)('a' + i % 26);
                s[127] = 0;
                ValueSet(k, name, REG_SZ, s, sizeof(s));
            } else if (!strcmp(label, "bad-productstring-binary")) {
                ValueSet(k, name, REG_BINARY, L"Pad", 8);
            } else if (!strncmp(label, "bad-dword-sz-", 13)) {
                ValueSet(k, name, REG_SZ, L"1", 4);
            } else if (!strcmp(label, "bad-inputlength-0")) {
                ConfigOverrideDword(index, value, 0);
            } else if (!strcmp(label, "bad-inputlength-1024")) {
                ConfigOverrideDword(index, value, 1024);
            } else if (!strcmp(label, "bad-inputlength-1025")) {
                ConfigOverrideDword(index, value, 1025);
            } else if (!strcmp(label, "bad-vendor-wide")) {
                ConfigOverrideDword(index, value, 0x1234054C);
            } else if (!strcmp(label, "bad-controllerindex-sz")) {
                ValueDrop(g_nextHwKey, L"ControllerIndex");
                ValueSet(g_nextHwKey, L"ControllerIndex", REG_SZ, L"3", 4);
            } else if (!strcmp(label, "no-controllerindex") || !strcmp(label, "no-controllerindex-beside-controller0")) {
                ValueDrop(g_nextHwKey, L"ControllerIndex");
            } else {
                HmFail("unknown bad value %s", label);
            }
        }
    }
    if (!p || variant == V_NO_KEY) {
        g_nextHwKey = KeyNewDetached(L"hw");
        g_nextHwSubKey = KeyNewDetached(L"hw-sub");
        g_nextDevnodeProfile = p;
        g_nextDevnodeIndex = index;
        DWORD idx = (DWORD)index;
        ValueSet(g_nextHwKey, L"ControllerIndex", REG_DWORD, &idx, 4);
    }

    if (variant == V_BAD_VALUE && !strcmp(label, "no-controllerindex-beside-controller0")) {
        /* Another controller owns Controller0. The device node added
         * next stays this profile's. */
        PlanProfile *other = PlanFind("xusb", NULL);
        HmRegKey *hw = g_nextHwKey, *sub = g_nextHwSubKey;
        PlanProfile *node = g_nextDevnodeProfile;
        int nodeIndex = g_nextDevnodeIndex;
        if (other) ConfigWrite(other, 0);
        g_nextHwKey = hw;
        g_nextHwSubKey = sub;
        g_nextDevnodeProfile = node;
        g_nextDevnodeIndex = nodeIndex;
    }
    if (variant == V_NO_KEY && !strcmp(label, "no-configuration-no-controllerindex"))
        ValueDrop(g_nextHwKey, L"ControllerIndex");

    c.dev = DeviceAdd(companion);
    if (!c.dev) return;
    int full = variant == V_FULL && !strcmp(label, "full") && !companion;
    if (full) {
        StringSweep(&c);
        GetInputSweep(&c);
    }
    if (companion) CompanionProbe(&c); else DriverProbe(&c);
    if (full) GetInputSweep(&c);

    if (variant == V_OWNER_GONE && c.sdk.pipe) {
        PipeServerClose(c.sdk.pipe);
        ThreadResume(c.dev->threads[0]);
        RecordSections(&c.sdk);
    }
    if (variant == V_READD) {
        /* The SDK's output reader holds the doorbell the driver made
         * (SharedMemoryIO.TryOpenOutputEvent), so the next device on the
         * index finds it. */
        WCHAR n[96];
        swprintf_s(n, ARRAYSIZE(n), L"Global\\HIDMaestroOutputEvent%d", index);
        HmObj *bell = ObjFindNamed(n);
        if (bell) Hold(bell);
        CtlStop(&c);
        if (p) ConfigWrite(p, index);
        c.dev = DeviceAdd(companion);
        if (!c.dev) return;
        if (companion) CompanionProbe(&c); else DriverProbe(&c);
    }
    CtlStop(&c);
}

/* FunctionMode is read from Controller0 for every index
 * (driver.c:2484-2498). */
static void FunctionModeCross(PlanProfile *xusb, PlanProfile *plain)
{
    static const struct { int index; int f0; int f1; const char *label; } v[] = {
        { 1, 1, 0, "index1-functionmode0-is-1" },
        { 1, 0, 1, "index1-functionmode0-is-0" },
        { 0, 1, 0, "index0-functionmode-1" },
    };
    for (int i = 0; i < (int)ARRAYSIZE(v); i++) {
        Ctl c;
        StreamBegin("startup/%s/driver-%s", plain->id, v[i].label);
        memset(&c, 0, sizeof(c));
        c.p = v[i].index == 0 ? xusb : plain;
        c.index = v[i].index;
        SdkCreate(&c.sdk, v[i].index, SDK_ALL);
        if (v[i].index == 1) ConfigWrite(xusb, 0);
        ConfigWrite(c.p, v[i].index);     /* the device node added next */
        if (v[i].index == 1) {
            ConfigOverrideDword(0, "FunctionMode", (DWORD)v[i].f0);
            ConfigOverrideDword(1, "FunctionMode", (DWORD)v[i].f1);
        } else {
            ConfigOverrideDword(0, "FunctionMode", (DWORD)v[i].f0);
        }
        c.dev = DeviceAdd(0);
        if (!c.dev) continue;
        DriverProbe(&c);
        CtlStop(&c);
    }
    /* No Controller0, FunctionMode in the parent key. */
    {
        Ctl c;
        StreamBegin("startup/%s/driver-functionmode-parent", plain->id);
        memset(&c, 0, sizeof(c));
        c.p = plain;
        c.index = 2;
        SdkCreate(&c.sdk, 2, SDK_ALL);
        ConfigWrite(plain, 2);
        DWORD one = 1;
        ValueSet(KeyMake(L"SOFTWARE\\HIDMaestro"), L"FunctionMode", REG_DWORD, &one, 4);
        c.dev = DeviceAdd(0);
        if (c.dev) { DriverProbe(&c); CtlStop(&c); }
    }
}

void CaseStartup(void)
{
    PlanProfile *sw = PlanFind("switch", NULL);
    PlanProfile *ds3 = PlanFind("ds3", NULL);
    PlanProfile *xusb = PlanFind("xusb", NULL);
    PlanProfile *sonyBt = NULL;
    for (int i = 0; i < g_plan.profileCount && !sonyBt; i++)
        if (!strcmp(g_plan.profiles[i].group, "sony") && PlanDword(&g_plan.profiles[i], "Bluetooth", 0))
            sonyBt = &g_plan.profiles[i];
    if (!sw || !ds3 || !xusb || !sonyBt) HmFail("the plan lacks a profile the startup case needs");

    /* Every plan profile through a full start. */
    for (int i = 0; i < g_plan.profileCount; i++) {
        PlanProfile *p = &g_plan.profiles[i];
        int seen = 0;   /* dualshock-3-full is in two groups */
        for (int j = 0; j < i; j++) seen |= !strcmp(g_plan.profiles[j].id, p->id);
        if (seen) continue;
        Startup(p, 0, 0, V_FULL, NULL, "full");
        if (!strcmp(p->group, "xusb")) Startup(p, 1, 0, V_FULL, NULL, "full");
    }

    /* Each value absent, on three profiles whose values each matter. */
    PlanProfile *matrix[] = { sw, ds3, sonyBt };
    for (int m = 0; m < (int)ARRAYSIZE(matrix); m++) {
        PlanProfile *p = matrix[m];
        for (int i = 0; i < p->valueCount; i++) {
            char label[80];
            snprintf(label, sizeof(label), "no-%s", p->values[i].name);
            Startup(p, 0, 0, V_DROP_VALUE, p->values[i].name, label);
        }
        Startup(p, 0, 1, V_FULL, NULL, "full-idx1");
        Startup(p, 0, 15, V_FULL, NULL, "full-idx15");
        Startup(p, 0, 0, V_PARENT_ONLY, NULL, "parent-key-only");
        Startup(p, 0, 0, V_BAD_VALUE, "ReportDescriptor", "bad-descriptor-4097");
        Startup(p, 0, 0, V_BAD_VALUE, "ReportDescriptor", "bad-descriptor-4096");
        Startup(p, 0, 0, V_BAD_VALUE, "ReportDescriptor", "bad-descriptor-empty");
        Startup(p, 0, 0, V_BAD_VALUE, "ProductString", "bad-productstring-long");
        Startup(p, 0, 0, V_BAD_VALUE, "ProductString", "bad-productstring-128");
        Startup(p, 0, 0, V_BAD_VALUE, "ProductString", "bad-productstring-binary");
        Startup(p, 0, 0, V_BAD_VALUE, "VendorId", "bad-dword-sz-VendorId");
        Startup(p, 0, 0, V_BAD_VALUE, "ProductId", "bad-dword-sz-ProductId");
        Startup(p, 0, 0, V_BAD_VALUE, "Bluetooth", "bad-dword-sz-Bluetooth");
        Startup(p, 0, 0, V_BAD_VALUE, "Ds3Sixaxis", "bad-dword-sz-Ds3Sixaxis");
        Startup(p, 0, 0, V_BAD_VALUE, "VendorId", "bad-vendor-wide");
        Startup(p, 0, 0, V_BAD_VALUE, "InputReportByteLength", "bad-inputlength-0");
        Startup(p, 0, 0, V_BAD_VALUE, "InputReportByteLength", "bad-inputlength-1024");
        Startup(p, 0, 0, V_BAD_VALUE, "InputReportByteLength", "bad-inputlength-1025");
        Startup(p, 0, 4, V_BAD_VALUE, "ControllerIndex", "no-controllerindex");
        Startup(p, 0, 4, V_BAD_VALUE, "ControllerIndex", "bad-controllerindex-sz");
    }
    Startup(NULL, 0, 0, V_NO_KEY, NULL, "no-configuration");
    Startup(NULL, 0, 7, V_NO_KEY, NULL, "no-configuration-idx7");
    Startup(NULL, 0, 0, V_NO_KEY, NULL, "no-configuration-no-controllerindex");
    /* ControllerIndex absent: InitInstancePaths falls back to index 0
     * (driver.c:251-267) and reads the configuration of the controller
     * that owns Controller0. */
    Startup(sonyBt, 0, 4, V_BAD_VALUE, "ControllerIndex", "no-controllerindex-beside-controller0");
    Startup(sw, 0, 4, V_BAD_VALUE, "ControllerIndex", "no-controllerindex-beside-controller0");
    Startup(sw, 0, 0, V_NO_SDK, NULL, "no-sdk-objects");
    Startup(sonyBt, 0, 0, V_NO_PIPE, NULL, "no-owner-pipe");
    Startup(sonyBt, 0, 0, V_WATCH_ENDED, NULL, "watch-ended");
    Startup(sonyBt, 0, 0, V_OWNER_GONE, NULL, "owner-gone");
    Startup(sonyBt, 0, 0, V_EVENTS_HOST, NULL, "events-exist-host");
    Startup(sonyBt, 0, 0, V_EVENTS_USER, NULL, "events-exist-user");
    Startup(sonyBt, 0, 0, V_EVENTS_NULL_DACL, NULL, "events-exist-null-dacl");
    Startup(sonyBt, 0, 0, V_READD, NULL, "re-add");
    Startup(sw, 0, 1, V_READD, NULL, "re-add-idx1");
    FunctionModeCross(xusb, sonyBt);

    /* The companion: each value it reads absent, the key missing, the
     * index's own value, the doorbell's name taken. */
    for (int i = 0; i < xusb->valueCount; i++) {
        if (strcmp(xusb->values[i].name, "VendorId") && strcmp(xusb->values[i].name, "ProductId")) continue;
        char label[80];
        snprintf(label, sizeof(label), "no-%s", xusb->values[i].name);
        Startup(xusb, 1, 0, V_DROP_VALUE, xusb->values[i].name, label);
    }
    Startup(xusb, 1, 2, V_FULL, NULL, "full-idx2");
    Startup(xusb, 1, 0, V_PARENT_ONLY, NULL, "parent-key-only");
    Startup(NULL, 1, 0, V_NO_KEY, NULL, "no-configuration");
    Startup(xusb, 1, 0, V_BAD_VALUE, "VendorId", "bad-dword-sz-VendorId");
    Startup(xusb, 1, 0, V_BAD_VALUE, "VendorId", "bad-vendor-wide");
    Startup(xusb, 1, 4, V_BAD_VALUE, "ControllerIndex", "no-controllerindex");
    Startup(xusb, 1, 4, V_BAD_VALUE, "ControllerIndex", "bad-controllerindex-sz");
    Startup(xusb, 1, 0, V_NO_SDK, NULL, "no-sdk-objects");
    Startup(xusb, 1, 0, V_EVENTS_USER, NULL, "events-exist-user");
    Startup(xusb, 1, 0, V_EVENTS_NULL_DACL, NULL, "events-exist-null-dacl");
    Startup(xusb, 1, 1, V_READD, NULL, "re-add");
}
