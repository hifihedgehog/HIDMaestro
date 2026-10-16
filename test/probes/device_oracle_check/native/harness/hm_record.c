/* The recorder, the plan reader and the SDK side of a controller index.
 *
 * Output file: the records Golden.cs's NativeStreamReader reads, all
 * little-endian. 'S', a 16-bit length and the stream name; 'O', an 8-bit
 * length and the tag, a 32-bit length and the bytes; 'E' at the end.
 *
 * Plan file, written by Case5Driver.cs, ASCII lines:
 *   hm-driver-plan 1
 *   profile <group> <id>
 *   devnode <prefix>         the HID device node's instance ID up to its
 *                            token, as the SDK builds it for the profile
 *   value <name> <registry type> <hex bytes, or - for none>
 *   features <hex ids>       the feature report ids the descriptor declares
 *   reportids <0|1>          the descriptor uses report ids
 *   readlength <n>           the largest input report, report id included
 *   pidrids <6 hex>          PidReportIdExtractor's ids for the PID section
 *   end
 *   body <name> <96 hex>     a 48-byte SwitchProPacker body */
#include "hm_harness.h"
#include <stdarg.h>

/* ------------------------------------------------------------------ */
/*  Recorder                                                           */
/* ------------------------------------------------------------------ */

static FILE *g_out;
static uint64_t g_outputs;
static uint32_t g_streams;
static uint32_t g_ringSeen;
static HmSection *g_ringSection;
static HmSection *g_ackSection;
static int g_recording;

void RecOpen(const char *path)
{
    if (fopen_s(&g_out, path, "wb") != 0 || !g_out) HmFail("cannot open %s", path);
    setvbuf(g_out, NULL, _IOFBF, 1 << 20);
}

static void Put(const void *p, size_t n)
{
    if (n && fwrite(p, 1, n, g_out) != n) HmFail("write to the output file failed");
}

void RecStream(const char *fmt, ...)
{
    char name[256];
    va_list ap;
    va_start(ap, fmt);
    int n = vsnprintf(name, sizeof(name), fmt, ap);
    va_end(ap);
    if (n <= 0 || n >= (int)sizeof(name)) HmFail("stream name too long");
    for (int i = 0; i < n; i++)
        if (name[i] <= ' ' || name[i] > '~') HmFail("stream name '%s' is not printable ASCII without spaces", name);
    uint16_t len = (uint16_t)n;
    Put("S", 1);
    Put(&len, 2);
    Put(name, (size_t)n);
    g_streams++;
    g_ringSeen = 0;
    g_ringSection = NULL;
    g_ackSection = NULL;
    g_recording = 1;
}

static void Out(const char *tag, const void *data, uint32_t len)
{
    size_t t = strlen(tag);
    if (t == 0 || t > 64) HmFail("tag '%s' is not 1 to 64 characters", tag);
    uint8_t tl = (uint8_t)t;
    Put("O", 1);
    Put(&tl, 1);
    Put(tag, t);
    Put(&len, 4);
    Put(data, len);
    g_outputs++;
}

void RecOut(const char *tag, const void *data, uint32_t len)
{
    if (!g_recording) HmFail("an output before any stream: %s", tag);
    RecFlushRing();
    Out(tag, data, len);
}

/* Output ring layout, driver.h:453-478 and SharedMemoryIO.cs:80-90:
 * Head at 0, slots of 264 bytes from 8 (SeqNo, Source, ReportId,
 * DataSize, Data[256]). */
#define RING_SLOTS 64u
#define RING_SLOT_SIZE 264u

void RecFlushRing(void)
{
    if (!g_ringSection) return;
    const uint8_t *b = g_ringSection->buf;
    uint32_t head;
    memcpy(&head, b, 4);
    if (head == g_ringSeen) return;
    if (head - g_ringSeen > RING_SLOTS) {
        uint32_t lost = head - g_ringSeen - RING_SLOTS;
        Out("ring.overrun", &lost, 4);
        g_ringSeen = head - RING_SLOTS;
    }
    while (g_ringSeen != head) {
        uint32_t seq = ++g_ringSeen;
        const uint8_t *slot = b + 8 + ((seq - 1) % RING_SLOTS) * RING_SLOT_SIZE;
        uint32_t slotSeq;
        uint16_t size;
        memcpy(&slotSeq, slot, 4);
        memcpy(&size, slot + 6, 2);
        if (slotSeq != seq) { Out("ring.unwritten", &seq, 4); continue; }
        if (size > 256) HmFail("ring slot size %u", size);
        uint8_t rec[4 + 256];
        rec[0] = slot[4];               /* Source */
        rec[1] = slot[5];               /* ReportId */
        memcpy(rec + 2, &size, 2);
        memcpy(rec + 4, slot + 8, size);
        Out("ring", rec, 4u + size);
    }
}

void RecordAck(void)
{
    if (!g_ackSection) HmFail("acknowledgement signaled without its section");
    RecOut("ack", g_ackSection->buf, 4);   /* DeliveredSeqNo */
}

void RecEnd(void)
{
    Put("E", 1);
    if (fflush(g_out) != 0 || fclose(g_out) != 0) HmFail("closing the output file failed");
    g_out = NULL;
}

uint64_t RecOutputCount(void) { return g_outputs; }
uint32_t RecStreamCount(void) { return g_streams; }

/* ------------------------------------------------------------------ */
/*  Plan                                                               */
/* ------------------------------------------------------------------ */

Plan g_plan;

static int HexNibble(int c)
{
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return -1;
}

static size_t Unhex(const char *s, uint8_t *dst, size_t cap)
{
    if (strcmp(s, "-") == 0) return 0;
    size_t n = strlen(s);
    if (n % 2 || n / 2 > cap) HmFail("plan: bad hex '%.40s'", s);
    for (size_t i = 0; i < n / 2; i++) {
        int hi = HexNibble(s[2 * i]), lo = HexNibble(s[2 * i + 1]);
        if (hi < 0 || lo < 0) HmFail("plan: bad hex '%.40s'", s);
        dst[i] = (uint8_t)(hi << 4 | lo);
    }
    return n / 2;
}

void PlanLoad(const char *path)
{
    FILE *f;
    if (fopen_s(&f, path, "rb") != 0 || !f) HmFail("cannot open the plan %s", path);
    static char line[1 << 16];
    static uint8_t bin[1 << 15];
    PlanProfile *p = NULL;
    int lineNo = 0;
    if (!fgets(line, sizeof(line), f) || strncmp(line, "hm-driver-plan 1", 16) != 0) HmFail("not a driver plan: %s", path);
    while (fgets(line, sizeof(line), f)) {
        lineNo++;
        size_t n = strlen(line);
        while (n && (line[n - 1] == '\n' || line[n - 1] == '\r')) line[--n] = 0;
        if (!n) continue;
        char *ctx = NULL;
        char *word = strtok_s(line, " ", &ctx);
        if (strcmp(word, "profile") == 0) {
            if (g_plan.profileCount >= (int)ARRAYSIZE(g_plan.profiles)) HmFail("plan: too many profiles");
            p = &g_plan.profiles[g_plan.profileCount++];
            strcpy_s(p->group, sizeof(p->group), strtok_s(NULL, " ", &ctx));
            strcpy_s(p->id, sizeof(p->id), strtok_s(NULL, " ", &ctx));
        } else if (strcmp(word, "devnode") == 0 && p) {
            char *d = strtok_s(NULL, " ", &ctx);
            if (!d) HmFail("plan line %d: devnode needs a prefix", lineNo + 1);
            strcpy_s(p->devnode, sizeof(p->devnode), d);
        } else if (strcmp(word, "value") == 0 && p) {
            if (p->valueCount >= (int)ARRAYSIZE(p->values)) HmFail("plan: too many values");
            PlanValue *v = &p->values[p->valueCount++];
            strcpy_s(v->name, sizeof(v->name), strtok_s(NULL, " ", &ctx));
            v->type = (DWORD)strtoul(strtok_s(NULL, " ", &ctx), NULL, 10);
            v->size = (DWORD)Unhex(strtok_s(NULL, " ", &ctx), bin, sizeof(bin));
            v->data = (uint8_t *)malloc(v->size ? v->size : 1);
            if (!v->data) HmFail("out of memory");
            memcpy(v->data, bin, v->size);
        } else if (strcmp(word, "features") == 0 && p) {
            for (char *t = strtok_s(NULL, " ", &ctx); t; t = strtok_s(NULL, " ", &ctx)) {
                if (p->featureCount >= (int)ARRAYSIZE(p->features)) HmFail("plan: too many feature ids");
                p->features[p->featureCount++] = (uint8_t)strtoul(t, NULL, 16);
            }
        } else if (strcmp(word, "reportids") == 0 && p) {
            p->usesReportIds = atoi(strtok_s(NULL, " ", &ctx));
        } else if (strcmp(word, "readlength") == 0 && p) {
            p->readLength = (uint32_t)strtoul(strtok_s(NULL, " ", &ctx), NULL, 10);
        } else if (strcmp(word, "pidrids") == 0 && p) {
            for (int i = 0; i < 6; i++) p->pidRids[i] = (uint8_t)strtoul(strtok_s(NULL, " ", &ctx), NULL, 16);
            p->hasPidRids = 1;
        } else if (strcmp(word, "end") == 0) {
            p = NULL;
        } else if (strcmp(word, "body") == 0) {
            if (g_plan.bodyCount >= (int)ARRAYSIZE(g_plan.bodies)) HmFail("plan: too many bodies");
            PlanBody *b = &g_plan.bodies[g_plan.bodyCount++];
            strcpy_s(b->name, sizeof(b->name), strtok_s(NULL, " ", &ctx));
            if (Unhex(strtok_s(NULL, " ", &ctx), b->data, sizeof(b->data)) != 48) HmFail("plan: a body is 48 bytes");
        } else {
            HmFail("plan line %d: unknown '%s'", lineNo + 1, word);
        }
    }
    fclose(f);
}

PlanProfile *PlanFind(const char *group, const char *id)
{
    for (int i = 0; i < g_plan.profileCount; i++)
        if ((!group || strcmp(g_plan.profiles[i].group, group) == 0) && (!id || strcmp(g_plan.profiles[i].id, id) == 0))
            return &g_plan.profiles[i];
    return NULL;
}

PlanValue *PlanValueFind(PlanProfile *p, const char *name)
{
    for (int i = 0; i < p->valueCount; i++)
        if (strcmp(p->values[i].name, name) == 0) return &p->values[i];
    return NULL;
}

uint32_t PlanDword(PlanProfile *p, const char *name, uint32_t fallback)
{
    PlanValue *v = PlanValueFind(p, name);
    uint32_t x;
    if (!v || v->type != REG_DWORD || v->size != 4) return fallback;
    memcpy(&x, v->data, 4);
    return x;
}

/* ------------------------------------------------------------------ */
/*  The SDK side                                                       */
/* ------------------------------------------------------------------ */

/* Section sizes, SharedMemoryIO.cs: the input section (SHARED_INPUT_SIZE,
 * driver.h:327-338), the acknowledgement (INPUT_ACK_SIZE, driver.h:364-376),
 * the version handshake (64, driver.h:392-402), the output ring
 * (SHARED_OUTPUT_SIZE, 8 + 64 x 264) and the PID state (PID_STATE_SIZE). */
#define INPUT_SIZE     362u
#define ACK_SIZE       (16u + 8u * (4u + 80u))
#define VERSION_SIZE   64u
#define OUTPUT_SIZE    (8u + RING_SLOTS * RING_SLOT_SIZE)
#define PID_SIZE       40u

static void Name(WCHAR *dst, size_t cap, const WCHAR *prefix, int index)
{
    swprintf_s(dst, cap, L"%s%d", prefix, index);
}

/* The objects SharedMemoryIO.EnsureInputMapping (SharedMemoryIO.cs:340-434)
 * makes, in its order: the owner pipe, the input section, the companion
 * input event, the acknowledgement and its event, the version section
 * with the SDK's half, and the input event last. Then the output ring
 * (HMController's EnsureOutputMapping). All exist before the device node,
 * as DeviceOrchestrator creates them first. */
void SdkCreate(Sdk *s, int index, int flags)
{
    WCHAR n[96];
    memset(s, 0, sizeof(*s));
    s->index = index;
    if (flags & SDK_PIPE) {
        Name(n, ARRAYSIZE(n), L"\\\\.\\pipe\\HIDMaestroOwner", index);
        s->pipe = PipeServerNew(n, 0x1F44);
        Hold(&s->pipe->o);
    }
    if (flags & SDK_SECTIONS) {
        Name(n, ARRAYSIZE(n), L"Global\\HIDMaestroInput", index);
        s->input = SectionNew(n, INPUT_SIZE); Hold(&s->input->o);
        Name(n, ARRAYSIZE(n), L"Global\\HIDMaestroCompanionInputEvent", index);
        s->companionEv = EventNew(n, FALSE, FALSE, OWN_ADMINS, TRUE); Hold(&s->companionEv->o);
        Name(n, ARRAYSIZE(n), L"Global\\HIDMaestroInputAck", index);
        s->ack = SectionNew(n, ACK_SIZE); Hold(&s->ack->o);
        Name(n, ARRAYSIZE(n), L"Global\\HIDMaestroInputAckEvent", index);
        s->ackEv = EventNew(n, FALSE, FALSE, OWN_ADMINS, TRUE); Hold(&s->ackEv->o);
        s->ackEv->role = EV_ACK;
        if (g_versionParam != VERSION_ABSENT) {
            Name(n, ARRAYSIZE(n), L"Global\\HIDMaestroVersion", index);
            s->version = SectionNew(n, VERSION_SIZE); Hold(&s->version->o);
            /* The SDK's half: layout 1, protocols 2 to 3, version 1.13.0
             * (SharedMemoryIO.cs:171-181, 410-421). The parameters at 4
             * write SdkProtocolMax 4 and nothing else differs. */
            int max4 = g_versionParam == VERSION_MAX4 || g_versionParam == VERSION_CLEARED
                       || g_versionParam == VERSION_CLOSED;
            int32_t layout = 1, min = 2, max = max4 ? 4 : 3;
            uint16_t ver[4] = { 1, 13, 0, 0 };
            /* The tag parameters write what the design note's v2.0.0 SDK
             * writes: protocols 4 to 4, and at offset 48 the identity tag
             * of this controller's token (ControllerToken) or of another
             * one. The other is the token HarnessToken gives the index
             * when the controller carries a different one, so a driver
             * that builds its expected tag from the index takes the
             * section for its own, and HM_0123456789ABCDEF otherwise. */
            int tagged = g_versionParam == VERSION_TAG_OWN || g_versionParam == VERSION_TAG_OTHER;
            if (tagged) { min = 4; max = 4; }
            memcpy(s->version->buf + 0, &layout, 4);
            memcpy(s->version->buf + 4, &min, 4);
            memcpy(s->version->buf + 8, &max, 4);
            memcpy(s->version->buf + 12, ver, 8);
            if (tagged) {
                WCHAR token[24], byIndex[24];
                ControllerToken(index, token, ARRAYSIZE(token));
                HarnessToken(index, byIndex, ARRAYSIZE(byIndex));
                uint64_t tag = g_versionParam == VERSION_TAG_OWN ? IdentityTag(token)
                             : wcscmp(token, byIndex) != 0 ? IdentityTag(byIndex)
                             : IdentityTag(L"HM_0123456789ABCDEF");
                memcpy(s->version->buf + 48, &tag, 8);
            }
        }
        Name(n, ARRAYSIZE(n), L"Global\\HIDMaestroInputEvent", index);
        s->inputEv = EventNew(n, FALSE, FALSE, OWN_ADMINS, TRUE); Hold(&s->inputEv->o);
        g_ackSection = s->ack;
    }
    if (flags & SDK_OUTPUT) {
        Name(n, ARRAYSIZE(n), L"Global\\HIDMaestroOutput", index);
        s->output = SectionNew(n, OUTPUT_SIZE); Hold(&s->output->o);
        g_ringSection = s->output;
        g_ringSeen = 0;
    }
}

/* SharedMemoryIO.WriteInputFrame (SharedMemoryIO.cs:844-910). */
void SdkWriteFrame(Sdk *s, const uint8_t *data, uint32_t dataLen, const uint8_t *gip,
                   const uint8_t *ext, uint32_t extLen)
{
    uint8_t *b = s->input->buf;
    uint32_t pending = s->seq + 1;
    memcpy(b, &pending, 4);
    memcpy(b + 4, &dataLen, 4);
    if (dataLen) memcpy(b + 8, data, dataLen > 256 ? 256 : dataLen);
    if (gip) memcpy(b + 264, gip, 14);
    if (ext && extLen) {
        uint32_t n = extLen > 80 ? 80 : extLen;
        memcpy(b + 282, ext, n);
        memcpy(b + 278, &n, 4);
    } else {
        uint32_t zero = 0;
        memcpy(b + 278, &zero, 4);
    }
    s->seq = pending + 1;
    memcpy(b, &s->seq, 4);
    EventSet(s->inputEv);
    if (gip) EventSet(s->companionEv);
}

/* SharedMemoryIO.EnsurePidStateMapping (SharedMemoryIO.cs:588-622) and
 * WritePidReportIds, as HMController.EnsurePidStateViewLocked calls them. */
void SdkPidCreate(Sdk *s, const uint8_t rids[6])
{
    WCHAR n[96];
    Name(n, ARRAYSIZE(n), L"Global\\HIDMaestroPidState", s->index);
    s->pid = SectionNew(n, PID_SIZE); Hold(&s->pid->o);
    uint16_t pool = 200;
    memcpy(s->pid->buf + 12, &pool, 2);
    s->pid->buf[14] = 10;
    memcpy(s->pid->buf + 28, rids, 6);
}

/* SharedMemoryIO.WritePidPool (SharedMemoryIO.cs:651-664). */
void SdkPidPool(Sdk *s, uint16_t ramPoolSize, uint8_t maxSim, uint8_t memMgmt)
{
    uint8_t *b = s->pid->buf;
    uint32_t odd = s->pidSeq + 1;
    memcpy(b, &odd, 4);
    b[4] = 1;
    memcpy(b + 12, &ramPoolSize, 2);
    b[14] = maxSim;
    b[15] = memMgmt;
    s->pidSeq += 2;
    memcpy(b, &s->pidSeq, 4);
}

static void ConfigPath(WCHAR *dst, size_t cap, int index)
{
    swprintf_s(dst, cap, L"SOFTWARE\\HIDMaestro\\Controller%d", index);
}

static void Widen(const char *s, WCHAR *dst, size_t cap)
{
    size_t i = 0;
    for (; s[i] && i + 1 < cap; i++) dst[i] = (WCHAR)(unsigned char)s[i];
    dst[i] = 0;
}

void ConfigWrite(PlanProfile *p, int index)
{
    WCHAR path[96], name[64];
    ConfigPath(path, ARRAYSIZE(path), index);
    HmRegKey *k = KeyMake(path);
    for (int i = 0; i < p->valueCount; i++) {
        Widen(p->values[i].name, name, ARRAYSIZE(name));
        ValueSet(k, name, p->values[i].type, p->values[i].data, p->values[i].size);
    }
    /* DeviceNodeCreator writes the index into the new device node's
     * hardware key, where InitInstancePaths and CompanionDeviceAdd read it. */
    g_nextHwKey = KeyNewDetached(L"hw");
    g_nextHwSubKey = KeyNewDetached(L"hw-sub");
    g_nextDevnodeProfile = p;
    g_nextDevnodeIndex = index;
    DWORD idx = (DWORD)index;
    ValueSet(g_nextHwKey, L"ControllerIndex", REG_DWORD, &idx, 4);
}

void ConfigOverrideDword(int index, const char *name, DWORD value)
{
    WCHAR path[96], w[64];
    ConfigPath(path, ARRAYSIZE(path), index);
    Widen(name, w, ARRAYSIZE(w));
    ValueSet(KeyMake(path), w, REG_DWORD, &value, 4);
}

void ConfigDropValue(int index, const char *name)
{
    WCHAR path[96], w[64];
    ConfigPath(path, ARRAYSIZE(path), index);
    Widen(name, w, ARRAYSIZE(w));
    HmRegKey *k = KeyFind(path);
    if (k) ValueDrop(k, w);
}

void RecordSections(Sdk *s)
{
    if (s->version) RecOut("section.version", s->version->buf, VERSION_SIZE);
    if (s->ack) RecOut("section.ack", s->ack->buf, 16);
}

int g_versionParam;

const char *VersionLabel(int param)
{
    static const char *const labels[VERSION_PARAMS] = { "sdk", "absent", "max3", "max4", "cleared", "closed", "tag-own", "tag-other" };
    if (param < 0 || param >= VERSION_PARAMS) HmFail("no version parameter %d", param);
    return labels[param];
}

/* The SDK's side part way through a stream. Cleared: all 64 bytes zero,
 * the SDK's half and whatever the driver wrote, the section still open.
 * Closed: the SDK lets go of its handle (SharedMemoryIO's release of
 * s_versionHandles), so the name leaves the namespace once nothing else
 * holds it. The bytes as they stand are recorded before and after. */
void SdkVersionMidRun(Sdk *s)
{
    if (g_versionParam == VERSION_SDK) return;
    RecOut("version.midrun", VersionLabel(g_versionParam), (uint32_t)strlen(VersionLabel(g_versionParam)));
    RecordSections(s);
    if (g_versionParam == VERSION_CLEARED && s->version) {
        memset(s->version->buf, 0, VERSION_SIZE);
        RecordSections(s);
    } else if (g_versionParam == VERSION_CLOSED && s->version) {
        ObjRelease(&s->version->o);
        uint8_t named = ObjFindNamed(s->version->o.name) != NULL;
        RecOut("version.named", &named, 1);
        s->version = NULL;
    }
}
