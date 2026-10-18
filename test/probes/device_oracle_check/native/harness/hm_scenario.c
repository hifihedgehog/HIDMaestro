/* Scenario helpers and the harness's entry point.
 *
 * Usage: driver_harness_<oracle|tree>.exe <plan> <output> <seed>
 *        driver_harness_<oracle|tree>.exe --script <plan> <script> <output>
 *        driver_harness_<oracle|tree>.exe --lifecycle <plan> <output> <seed>
 * Exit 0 with a complete output file, 2 for bad arguments, 3 when a
 * scenario stopped (the message names the reason). --lifecycle exits 1
 * when a check failed (hm_case_lifecycle.c). */
#include "hm_scenario.h"
#include <stdarg.h>

uint64_t g_seed;

void StreamBegin(const char *fmt, ...)
{
    char name[256];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(name, sizeof(name), fmt, ap);
    va_end(ap);
    if (g_versionParam != VERSION_SDK) {
        size_t n = strlen(name);
        snprintf(name + n, sizeof(name) - n, "/version-%s", VersionLabel(g_versionParam));
    }
    WorldReset();
    RecStream("%s", name);
}

int CtlStart(Ctl *c, PlanProfile *p, int index, int sdkFlags, int companion)
{
    memset(c, 0, sizeof(*c));
    c->p = p;
    c->index = index;
    c->companion = companion;
    SdkCreate(&c->sdk, index, sdkFlags);
    if (p) ConfigWrite(p, index);
    else { g_nextDevnodeProfile = NULL; g_nextDevnodeIndex = index; }
    c->dev = DeviceAdd(companion);
    VersionAfterAdd(c);
    return c->dev != NULL;
}

void CtlStop(Ctl *c)
{
    if (c->dev && g_versionParam != VERSION_SDK) RecordSections(&c->sdk);
    if (c->dev) DeviceRemove(c->dev);
    if (c->dev && g_versionParam != VERSION_SDK) RecordSections(&c->sdk);
    c->dev = NULL;
}

void VersionAfterAdd(Ctl *c)
{
    if (g_versionParam != VERSION_SDK) RecordSections(&c->sdk);
}

void VersionMidRun(Ctl *c)
{
    SdkVersionMidRun(&c->sdk);
}

HmThread *CtlThread(Ctl *c, int i)
{
    if (!c->dev || i >= c->dev->threadCount) HmFail("the device has no thread %d", i);
    return c->dev->threads[i];
}

void RunWorker(Ctl *c)
{
    if (!c->dev || c->dev->threadCount == 0) return;
    ThreadResume(c->dev->threads[0]);
}

/* The stream thread's wait times out: the clock runs to the deadline the
 * driver's own wait set (driver.c:1819 waits 15 ms at 9bd6279), not a
 * constant. A driver with no stream thread, one that leaves the Switch Pro
 * to the SDK's protocol host, has no period to run: 0, and nothing runs. */
int SwitchTick(Ctl *c)
{
    if (!c->dev || c->dev->threadCount < 2) return 0;
    ThreadResume(c->dev->threads[1]);
    return 1;
}

void Frame(Ctl *c, const uint8_t *data, uint32_t len, const uint8_t *gip, const uint8_t *ext, uint32_t extLen)
{
    SdkWriteFrame(&c->sdk, data, len, gip, ext, extLen);
    if (!c->companion) RunWorker(c);
}

HmRequest *HidRead(Ctl *c, uint32_t len)
{
    return Ioctl(c->dev, "read", IOCTL_HID_READ_REPORT, NULL, 0, len, NULL, 0);
}

/* The report id a write carries in OutputBufferLength: the report's first
 * byte with report ids, 0 without them (hm_wdf.c IsWriteCode). */
static size_t WriteId(Ctl *c, const uint8_t *buf, uint32_t len)
{
    if (c->p && !c->p->usesReportIds) return 0;
    return len ? buf[0] : 0;
}

HmRequest *HidWrite(Ctl *c, const uint8_t *buf, uint32_t len)
{
    return Ioctl(c->dev, "write", IOCTL_HID_WRITE_REPORT, buf, len, WriteId(c, buf, len), NULL, 0);
}

HmRequest *HidSetOutput(Ctl *c, const uint8_t *buf, uint32_t len)
{
    return Ioctl(c->dev, "setoutput", IOCTL_UMDF_HID_SET_OUTPUT_REPORT, buf, len, WriteId(c, buf, len), NULL, 0);
}

HmRequest *HidSetFeature(Ctl *c, const uint8_t *buf, uint32_t len)
{
    return Ioctl(c->dev, "setfeature", IOCTL_UMDF_HID_SET_FEATURE, buf, len, WriteId(c, buf, len), NULL, 0);
}

HmRequest *HidGetFeature(Ctl *c, uint8_t id, uint32_t len)
{
    uint8_t rid = c->p && !c->p->usesReportIds ? 0 : id;
    return Ioctl(c->dev, "getfeature", IOCTL_UMDF_HID_GET_FEATURE, &rid, 1, len, &id, 1);
}

HmRequest *HidGetInput(Ctl *c, uint8_t id, uint32_t len)
{
    uint8_t rid = c->p && !c->p->usesReportIds ? 0 : id;
    return Ioctl(c->dev, "getinput", IOCTL_UMDF_HID_GET_INPUT_REPORT, &rid, 1, len, &id, 1);
}

void HidStartQueries(Ctl *c)
{
    Ioctl(c->dev, "get.devicedescriptor", IOCTL_HID_GET_DEVICE_DESCRIPTOR, NULL, 0, 64, NULL, 0);
    Ioctl(c->dev, "get.reportdescriptor", IOCTL_HID_GET_REPORT_DESCRIPTOR, NULL, 0, 4096, NULL, 0);
    Ioctl(c->dev, "get.attributes", IOCTL_HID_GET_DEVICE_ATTRIBUTES, NULL, 0, sizeof(HID_DEVICE_ATTRIBUTES), NULL, 0);
    /* HID class asks for strings 14, 15 and 16 under MsHidUmdf
     * (driver.c:2759-2766), the documented ids 1 to 3 as well. */
    static const ULONG ids[] = { 1, 2, 3, 14, 15, 16, 0x0409000F };
    for (int i = 0; i < (int)ARRAYSIZE(ids); i++)
        Ioctl(c->dev, "get.string", IOCTL_HID_GET_STRING, &ids[i], 4, 256, NULL, 0);
    Ioctl(c->dev, "get.string", IOCTL_HID_GET_STRING, NULL, 0, 256, NULL, 0);
    Ioctl(c->dev, "get.string", IOCTL_HID_GET_STRING, &ids[4], 4, 8, NULL, 0);
    ULONG idx = 1;
    Ioctl(c->dev, "get.indexedstring", IOCTL_HID_GET_INDEXED_STRING, &idx, 4, 256, NULL, 0);
    Ioctl(c->dev, "activate", IOCTL_HID_ACTIVATE_DEVICE, NULL, 0, 0, NULL, 0);
    Ioctl(c->dev, "idle", IOCTL_HID_SEND_IDLE_NOTIFICATION_REQUEST, NULL, 0, 0, NULL, 0);
    Ioctl(c->dev, "unknown", IOCTL_HID_GET_MS_GENRE_DESCRIPTOR, NULL, 0, 64, NULL, 0);
}

void GetInputSweep(Ctl *c)
{
    static const uint32_t sizes[] = { 0, 1, 2, 3, 12, 49, 64 };
    for (int id = 0; id < 256; id++)
        for (int s = 0; s < (int)ARRAYSIZE(sizes); s++)
            Ioctl(c->dev, "getinput", IOCTL_UMDF_HID_GET_INPUT_REPORT, &(uint8_t){ (uint8_t)id }, 1,
                  sizes[s], &(uint8_t){ (uint8_t)id }, 1);
}

void StringSweep(Ctl *c)
{
    /* The product string's bytes with its NUL, as the driver keeps them
     * (driver.c:541-551), and the serial "HM-CTL-0000" with its NUL. */
    PlanValue *v = c->p ? PlanValueFind(c->p, "ProductString") : NULL;
    uint32_t product = v ? v->size : 68;
    const uint32_t serial = 24;
    uint32_t sizes[] = { 0, 1, 2, 3, 4, product - 2, product - 1, product, product + 1, product + 2,
                         serial - 2, serial - 1, serial, serial + 1, serial + 2, 255, 256 };
    for (ULONG id = 0; id <= 20; id++)
        for (int s = 0; s < (int)ARRAYSIZE(sizes); s++) {
            if (sizes[s] > 0x7FFFFFFF) continue;
            Ioctl(c->dev, "get.string", IOCTL_HID_GET_STRING, &id, 4, sizes[s], NULL, 0);
            Ioctl(c->dev, "get.indexedstring", IOCTL_HID_GET_INDEXED_STRING, &id, 4, sizes[s], NULL, 0);
        }
    /* The id in a short input buffer: the driver then reads no id. */
    Ioctl(c->dev, "get.string", IOCTL_HID_GET_STRING, &(uint16_t){ 16 }, 2, 256, NULL, 0);
}

int Parked(Ctl *c)
{
    int n = 0;
    for (int i = 0; c->dev && i < c->dev->queueCount; i++)
        if (c->dev->queues[i] != c->dev->defaultQueue) n += QueueDepth(c->dev->queues[i]);
    return n;
}

int main(int argc, char **argv)
{
    /* No dialog for a fault: the probe that runs this reads the exit code. */
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    if (argc == 5 && strcmp(argv[1], "--script") == 0)
        return ScriptRun(argv[2], argv[3], argv[4]);
    if (argc == 5 && strcmp(argv[1], "--corpus") == 0) {
        /* Case 9: only the corpus groups of the plan Case9PhysicalCorpus.cs
         * writes (hm_case_corpus.c). */
        g_seed = _strtoui64(argv[4], NULL, 10);
        PlanLoad(argv[2]);
        FiberInit();
        WdfInit();
        RecOpen(argv[3]);
        CaseCorpus();
        WorldReset();
        RecEnd();
        fprintf(stdout, "streams %u outputs %llu\n", RecStreamCount(), (unsigned long long)RecOutputCount());
        return 0;
    }
    if (argc == 5 && strcmp(argv[1], "--stale") == 0) {
        /* Values a killed v2.0.0 program leaves under a v1 controller's
         * index: only the stale groups of the plan Case5Driver.cs writes
         * for it (hm_case_stale.c). */
        g_seed = _strtoui64(argv[4], NULL, 10);
        PlanLoad(argv[2]);
        FiberInit();
        WdfInit();
        RecOpen(argv[3]);
        CaseStale();
        WorldReset();
        RecEnd();
        fprintf(stdout, "streams %u outputs %llu\n", RecStreamCount(), (unsigned long long)RecOutputCount());
        return 0;
    }
    if (argc == 5 && strcmp(argv[1], "--lifecycle") == 0) {
        g_seed = _strtoui64(argv[4], NULL, 10);
        PlanLoad(argv[2]);
        FiberInit();
        WdfInit();
        RecOpen(argv[3]);
        int failed = CaseLifecycle();
        WorldReset();
        RecEnd();
        return failed ? 1 : 0;
    }
    if (argc != 4) {
        fputs("usage: driver_harness <plan> <output> <seed>\n"
              "       driver_harness --script <plan> <script> <output>\n"
              "       driver_harness --corpus <plan> <output> <seed>\n"
              "       driver_harness --stale <plan> <output> <seed>\n"
              "       driver_harness --lifecycle <plan> <output> <seed>\n", stderr);
        return 2;
    }
    g_seed = _strtoui64(argv[3], NULL, 10);
    PlanLoad(argv[1]);
    FiberInit();
    WdfInit();
    RecOpen(argv[2]);
    CaseStartup();
    CaseSwitch();
    CaseSony();
    CaseDs3();
    CaseXusb();
    CaseThreads();
    CaseVersion();
    CaseIdentity();
    CaseExtendedLengths();
    WorldReset();
    RecEnd();
    fprintf(stdout, "streams %u outputs %llu\n", RecStreamCount(), (unsigned long long)RecOutputCount());
    return 0;
}
