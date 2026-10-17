/* Script mode: the harness driven by an input script rather than its own
 * scenarios, so device_oracle_check's compare-live can hand the driver
 * exactly the requests a live run made (CompareLive.cs builds the script
 * from the live recordings and the mapping its header states).
 *
 * Script, ASCII lines, one command each. Hex is two digits a byte, '-'
 * for none:
 *   hm-driver-script 1
 *   stream <name>                       a new stream on an empty world
 *   device <profile-id> <index> <driver|companion>
 *                                       the SDK's objects, the profile's
 *                                       configuration from the plan, the
 *                                       device add
 *   frame <data> <extended>             SharedMemoryIO.WriteInputFrame of
 *                                       the data lane and the extended lane,
 *                                       then the worker's turn
 *   gip <14 bytes>                      a frame carrying the GIP slice
 *   tick                                one period of the Switch stream
 *   call <n>                            records "call" with n: the outputs
 *                                       up to the next call belong to it
 *   read <length>                       IOCTL_HID_READ_REPORT
 *   write <bytes>                       IOCTL_HID_WRITE_REPORT
 *   setoutput <bytes>                   IOCTL_UMDF_HID_SET_OUTPUT_REPORT
 *   setfeature <bytes>                  IOCTL_UMDF_HID_SET_FEATURE
 *   getfeature <input> <length> <init>  IOCTL_UMDF_HID_GET_FEATURE: the
 *                                       report id buffer, the output
 *                                       buffer's length and what it holds
 *                                       on arrival
 *   getinput <input> <length> <init>    IOCTL_UMDF_HID_GET_INPUT_REPORT
 *   ioctl <code> <input> <length>       any other IOCTL, tagged "ioctl"
 *   remove                              the device's removal
 *   version <label>                     the version-section parameter the
 *                                       next device runs under
 *                                       (VersionLabel: sdk, absent, max3,
 *                                       max4, cleared, closed, tag-own,
 *                                       tag-other, range5, range45), sdk
 *                                       at each stream
 * Completions record the whole output buffer (g_fullBuffers). */
#include "hm_scenario.h"

static size_t ScriptHex(const char *s, uint8_t *dst, size_t cap, int line)
{
    if (!s) HmFail("script line %d: a value is missing", line);
    if (strcmp(s, "-") == 0) return 0;
    size_t n = strlen(s);
    if (n % 2 || n / 2 > cap) HmFail("script line %d: bad hex", line);
    for (size_t i = 0; i < n / 2; i++) {
        unsigned v;
        if (sscanf_s(s + 2 * i, "%2x", &v) != 1) HmFail("script line %d: bad hex", line);
        dst[i] = (uint8_t)v;
    }
    return n / 2;
}

static uint32_t ScriptNumber(const char *s, int line)
{
    if (!s) HmFail("script line %d: a number is missing", line);
    char *end;
    unsigned long v = strtoul(s, &end, 0);
    if (*end) HmFail("script line %d: bad number '%s'", line, s);
    return (uint32_t)v;
}

int ScriptRun(const char *plan, const char *script, const char *output)
{
    static char line[1 << 14];
    static uint8_t a[4096], b[4096];
    FILE *f;
    Ctl c;
    int lineNo = 1, inStream = 0;

    PlanLoad(plan);
    FiberInit();
    WdfInit();
    g_fullBuffers = 1;
    if (fopen_s(&f, script, "rb") != 0 || !f) HmFail("cannot open the script %s", script);
    if (!fgets(line, sizeof(line), f) || strncmp(line, "hm-driver-script 1", 18) != 0) HmFail("not a driver script: %s", script);
    RecOpen(output);
    memset(&c, 0, sizeof(c));

    while (fgets(line, sizeof(line), f)) {
        lineNo++;
        size_t n = strlen(line);
        while (n && (line[n - 1] == '\n' || line[n - 1] == '\r')) line[--n] = 0;
        if (!n) continue;
        char *ctx = NULL;
        char *cmd = strtok_s(line, " ", &ctx);
        char *x1 = strtok_s(NULL, " ", &ctx);
        char *x2 = strtok_s(NULL, " ", &ctx);
        char *x3 = strtok_s(NULL, " ", &ctx);

        if (strcmp(cmd, "stream") == 0) {
            if (c.dev) CtlStop(&c);
            if (!x1) HmFail("script line %d: a stream needs a name", lineNo);
            g_versionParam = VERSION_SDK;
            StreamBegin("%s", x1);
            memset(&c, 0, sizeof(c));
            inStream = 1;
            continue;
        }
        if (strcmp(cmd, "version") == 0) {
            if (!inStream || c.dev) HmFail("script line %d: version comes before the stream's device", lineNo);
            int found = -1;
            for (int v = 0; v < VERSION_PARAMS; v++)
                if (x1 && strcmp(x1, VersionLabel(v)) == 0) found = v;
            if (found < 0) HmFail("script line %d: no version parameter '%s'", lineNo, x1 ? x1 : "");
            g_versionParam = found;
            continue;
        }
        if (!inStream) HmFail("script line %d: '%s' before any stream", lineNo, cmd);
        if (strcmp(cmd, "device") == 0) {
            if (c.dev) HmFail("script line %d: one device a stream", lineNo);
            PlanProfile *p = PlanFind(NULL, x1);
            if (!p) HmFail("script line %d: the plan has no profile %s", lineNo, x1 ? x1 : "");
            int companion = x3 && strcmp(x3, "companion") == 0;
            if (!x3 || (!companion && strcmp(x3, "driver") != 0)) HmFail("script line %d: driver or companion", lineNo);
            CtlStart(&c, p, (int)ScriptNumber(x2, lineNo), SDK_ALL, companion);
            continue;
        }
        if (!c.dev && strcmp(cmd, "call") != 0) HmFail("script line %d: '%s' with no device", lineNo, cmd);
        if (strcmp(cmd, "call") == 0) {
            uint32_t k = ScriptNumber(x1, lineNo);
            RecOut("call", &k, 4);
        } else if (strcmp(cmd, "frame") == 0) {
            size_t na = ScriptHex(x1, a, sizeof(a), lineNo);
            size_t nb = ScriptHex(x2, b, sizeof(b), lineNo);
            Frame(&c, a, (uint32_t)na, NULL, nb ? b : NULL, (uint32_t)nb);
        } else if (strcmp(cmd, "gip") == 0) {
            if (ScriptHex(x1, a, sizeof(a), lineNo) != 14) HmFail("script line %d: a GIP slice is 14 bytes", lineNo);
            Frame(&c, NULL, 0, a, NULL, 0);
        } else if (strcmp(cmd, "tick") == 0) {
            SwitchTick(&c);
        } else if (strcmp(cmd, "read") == 0) {
            HidRead(&c, ScriptNumber(x1, lineNo));
        } else if (strcmp(cmd, "write") == 0) {
            HidWrite(&c, a, (uint32_t)ScriptHex(x1, a, sizeof(a), lineNo));
        } else if (strcmp(cmd, "setoutput") == 0) {
            HidSetOutput(&c, a, (uint32_t)ScriptHex(x1, a, sizeof(a), lineNo));
        } else if (strcmp(cmd, "setfeature") == 0) {
            HidSetFeature(&c, a, (uint32_t)ScriptHex(x1, a, sizeof(a), lineNo));
        } else if (strcmp(cmd, "getfeature") == 0 || strcmp(cmd, "getinput") == 0) {
            size_t na = ScriptHex(x1, a, sizeof(a), lineNo);
            uint32_t len = ScriptNumber(x2, lineNo);
            size_t nb = ScriptHex(x3, b, sizeof(b), lineNo);
            int feature = cmd[3] == 'f';
            Ioctl(c.dev, feature ? "getfeature" : "getinput",
                  feature ? IOCTL_UMDF_HID_GET_FEATURE : IOCTL_UMDF_HID_GET_INPUT_REPORT,
                  a, na, len, b, nb);
        } else if (strcmp(cmd, "ioctl") == 0) {
            uint32_t code = ScriptNumber(x1, lineNo);
            size_t na = ScriptHex(x2, a, sizeof(a), lineNo);
            Ioctl(c.dev, "ioctl", code, a, na, ScriptNumber(x3, lineNo), NULL, 0);
        } else if (strcmp(cmd, "remove") == 0) {
            CtlStop(&c);
        } else {
            HmFail("script line %d: unknown command '%s'", lineNo, cmd);
        }
    }
    fclose(f);
    if (c.dev) CtlStop(&c);
    g_versionParam = VERSION_SDK;
    WorldReset();
    RecEnd();
    fprintf(stdout, "streams %u outputs %llu\n", RecStreamCount(), (unsigned long long)RecOutputCount());
    return 0;
}
