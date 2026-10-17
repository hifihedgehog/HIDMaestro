/* The identity tag and the device instance ID, for the version contract
 * of the design note's decision (c): the v2.0.0 SDK writes into the
 * version section, at offset 48, the 64-bit FNV-1a hash of the
 * controller's identity token's UTF-16LE code units with no terminator,
 * and a driver reads its own instance ID through WdfFdoInitQueryPropertyEx
 * before WdfDeviceCreate and takes the token from its last segment.
 *
 * Every device this harness adds carries the instance ID the SDK gives
 * its device node (DevnodeInstanceId): the HID device ROOT\HIDCLASS\<token>,
 * ROOT\VID_xxxx&PID_xxxx&IG_00\<token> (DeviceNodeCreator.cs:106-133) or
 * SWD\HIDMAESTRO_VID_xxxx_PID_xxxx&IG_00\<token> (DeviceOrchestrator.cs:761,
 * 818-836), as its profile takes the xinputhid path, Microsoft's vendor ID
 * or neither, and the XUSB companion SWD\HIDMAESTRO\<token>
 * (DeviceOrchestrator.cs:945-954). The SDK passes ROOT\HIDClass\<token>
 * (DeviceNodeCreator.cs:133). Instance IDs compare without regard to case,
 * and the harness gives the ROOT enumerator in capitals. The token is the
 * controller's (ControllerToken): HM_<index in four digits> unless a
 * stream gives it a keyed one.
 *
 * The 9bd6279 driver reads neither the instance ID nor the tag, so its
 * streams under the tag parameters show what it does at protocols 4 to 4.
 *
 * Streams: identity/vectors, the tags of the note's two test vectors,
 * checked here, and of the tokens of indexes 0, 1 and 15. identity/query,
 * WdfFdoInitQueryPropertyEx as this harness answers it, at each buffer
 * length a driver can pass (InstanceIdProbe, hm_wdf.c), and
 * identity/query-keyed the same on a ROOT device node with a keyed token.
 * identity/instance-ids, the instance ID of every plan profile's device
 * nodes with an index's token and with a keyed one.
 * identity/fdo-open-registry, WdfFdoInitOpenRegistryKey's access table
 * (FdoRegistryProbe).
 * identity/keyed/<token>-idx<N>/..., under VERSION_TAG_OWN and
 * VERSION_TAG_OTHER, a device node of each instance-ID form and each
 * device family whose token is not the index's: a keyed token at index 3
 * and HM_0007 at index 2 (the key "index:7", DeviceIdentity.cs:131). There
 * the other tag is the index's own HM_<index>, so a driver that rebuilds
 * the token from ControllerIndex accepts the other section and refuses
 * its own, where a driver that reads its instance ID does the opposite
 * (review finding 1). */
#include "hm_scenario.h"

/* The flavor's driver.h tag (build.ps1 driver_tag.c): 0 and no function for
 * 9bd6279's, which has none. */
extern int HmDriverHasIdentityTag;
unsigned long long HmDriverIdentityTag(const unsigned short *token);

PlanProfile *g_nextDevnodeProfile;
int g_nextDevnodeIndex;

/* The tokens ControllerTokenSet gave in this stream, by index. */
static struct { int index; WCHAR token[24]; } g_tokens[8];
static int g_tokenCount;

void HarnessToken(int index, WCHAR *out, size_t count)
{
    swprintf_s(out, count, L"HM_%04d", index);
}

void ControllerToken(int index, WCHAR *out, size_t count)
{
    for (int i = 0; i < g_tokenCount; i++)
        if (g_tokens[i].index == index) { wcscpy_s(out, count, g_tokens[i].token); return; }
    HarnessToken(index, out, count);
}

void ControllerTokenSet(int index, const WCHAR *token)
{
    for (int i = 0; i < g_tokenCount; i++)
        if (g_tokens[i].index == index) { wcscpy_s(g_tokens[i].token, ARRAYSIZE(g_tokens[i].token), token); return; }
    if (g_tokenCount >= (int)ARRAYSIZE(g_tokens)) HmFail("too many controller tokens in one stream");
    g_tokens[g_tokenCount].index = index;
    wcscpy_s(g_tokens[g_tokenCount].token, ARRAYSIZE(g_tokens[g_tokenCount].token), token);
    g_tokenCount++;
}

void IdentityReset(void)
{
    g_tokenCount = 0;
    g_nextDevnodeProfile = NULL;
    g_nextDevnodeIndex = 0;
}

void DevnodeInstanceId(PlanProfile *p, int index, int companion, WCHAR *out, size_t count)
{
    WCHAR token[24], prefix[96];
    ControllerToken(index, token, ARRAYSIZE(token));
    if (companion) {
        wcscpy_s(prefix, ARRAYSIZE(prefix), L"SWD\\HIDMAESTRO");
    } else if (!p) {
        wcscpy_s(prefix, ARRAYSIZE(prefix), L"ROOT\\HIDCLASS");
    } else {
        if (!p->devnode[0]) HmFail("the plan gives %s no devnode line", p->id);
        size_t i = 0;
        for (; p->devnode[i] && i + 1 < ARRAYSIZE(prefix); i++) prefix[i] = (WCHAR)(unsigned char)p->devnode[i];
        prefix[i] = 0;
    }
    swprintf_s(out, count, L"%ls\\%ls", prefix, token);
}

uint64_t IdentityTag(const WCHAR *token)
{
    uint64_t h = 0xCBF29CE484222325ull;
    for (const WCHAR *p = token; *p; p++) {
        uint8_t b[2] = { (uint8_t)(*p & 0xFF), (uint8_t)(*p >> 8) };
        for (int i = 0; i < 2; i++) { h ^= b[i]; h *= 0x100000001B3ull; }
    }
    return h;
}

/* A keyed controller's token: "HM_" and 16 uppercase hex digits. */
#define KEYED_TOKEN L"HM_8C2F0B6E41D7A953"

static const struct { int index; const WCHAR *token; } k_keyed[] = {
    { 3, KEYED_TOKEN },
    { 2, L"HM_0007" },
};

static void Keyed(int k, PlanProfile *p, int companion, const char *what, void (*probe)(Ctl *), void (*probe2)(Ctl *))
{
    Ctl c;
    StreamBegin("identity/keyed/%ls-idx%d/%s/%s", k_keyed[k].token, k_keyed[k].index, p->id, what);
    ControllerTokenSet(k_keyed[k].index, k_keyed[k].token);
    if (!CtlStart(&c, p, k_keyed[k].index, SDK_ALL, companion)) return;
    probe(&c);
    if (probe2) probe2(&c);
    CtlStop(&c);
}

static void RecordInstanceId(PlanProfile *p, int index, int companion)
{
    WCHAR id[128];
    DevnodeInstanceId(p, index, companion, id, ARRAYSIZE(id));
    RecOut(companion ? "instance.companion" : "instance", id, (uint32_t)(wcslen(id) * sizeof(WCHAR)));
}

void CaseIdentity(void)
{
    /* The vectors driver.h and the SDK's test carry (design note,
     * decision (c)). */
    static const struct { const WCHAR *token; uint64_t tag; } vectors[] = {
        { L"HM_0000", 0x1FD048916B8D461Full },
        { L"HM_0123456789ABCDEF", 0x3B86F3634086D831ull },
    };
    StreamBegin("identity/vectors");
    for (int i = 0; i < (int)ARRAYSIZE(vectors); i++) {
        uint64_t tag = IdentityTag(vectors[i].token);
        if (tag != vectors[i].tag) HmFail("identity tag of %ls is %016llx, the note gives %016llx", vectors[i].token,
                                          (unsigned long long)tag, (unsigned long long)vectors[i].tag);
        RecOut("tag.vector", &tag, 8);
        /* The driver's own HmIdentityTag (driver.h), compiled from the
         * flavor's driver.h by build.ps1, where it has one. */
        if (HmDriverHasIdentityTag) {
            uint64_t d = HmDriverIdentityTag((const unsigned short *)vectors[i].token);
            if (d != vectors[i].tag) HmFail("driver.h's HmIdentityTag of %ls is %016llx, the note gives %016llx", vectors[i].token,
                                            (unsigned long long)d, (unsigned long long)vectors[i].tag);
            RecOut("tag.driver", &d, 8);
        }
    }
    static const int indexes[] = { 0, 1, 15 };
    for (int i = 0; i < (int)ARRAYSIZE(indexes); i++) {
        WCHAR token[24];
        HarnessToken(indexes[i], token, ARRAYSIZE(token));
        uint64_t tag = IdentityTag(token);
        RecOut("token", token, (uint32_t)(wcslen(token) * sizeof(WCHAR)));
        RecOut("tag", &tag, 8);
        if (HmDriverHasIdentityTag) {
            uint64_t d = HmDriverIdentityTag((const unsigned short *)token);
            if (d != tag) HmFail("driver.h's HmIdentityTag of %ls is %016llx, the harness's %016llx", token,
                                 (unsigned long long)d, (unsigned long long)tag);
        }
    }
    StreamBegin("identity/query");
    InstanceIdProbe(L"SWD\\HIDMAESTRO\\HM_0000");

    StreamBegin("identity/query-keyed");
    InstanceIdProbe(L"ROOT\\VID_045E&PID_028E&IG_00\\" KEYED_TOKEN);

    StreamBegin("identity/instance-ids");
    for (int i = 0; i < g_plan.profileCount; i++) {
        PlanProfile *p = &g_plan.profiles[i];
        int xusb = !strcmp(p->group, "xusb");
        RecordInstanceId(p, 0, 0);
        if (xusb) RecordInstanceId(p, 0, 1);
        ControllerTokenSet(3, KEYED_TOKEN);
        RecordInstanceId(p, 3, 0);
        if (xusb) RecordInstanceId(p, 3, 1);
        IdentityReset();
    }

    StreamBegin("identity/fdo-open-registry");
    FdoRegistryProbe();

    /* One device node of each instance-ID form and each device family. */
    PlanProfile *sw = PlanFind("switch", NULL);
    PlanProfile *sony = PlanFind("sony", "dualsense-bt");
    PlanProfile *ds3 = PlanFind("ds3", NULL);
    PlanProfile *xusb = PlanFind("xusb", NULL);
    PlanProfile *xih = PlanFind("xinputhid", NULL);
    if (!sw || !sony || !ds3 || !xusb || !xih) HmFail("the plan lacks a profile the keyed identity streams need");
    static const int params[] = { VERSION_TAG_OWN, VERSION_TAG_OTHER };
    for (int v = 0; v < (int)ARRAYSIZE(params); v++) {
        g_versionParam = params[v];
        for (int k = 0; k < (int)ARRAYSIZE(k_keyed); k++) {
            Keyed(k, sw, 0, "driver", DriverProbe, SwitchProbe);
            Keyed(k, sony, 0, "driver", DriverProbe, FeatureSweep);
            Keyed(k, ds3, 0, "driver", DriverProbe, FeatureSweep);
            Keyed(k, xusb, 0, "driver", DriverProbe, NULL);
            Keyed(k, xusb, 1, "companion", XusbAnswerSweep, NULL);
            Keyed(k, xih, 0, "driver", DriverProbe, NULL);
        }
    }
    g_versionParam = VERSION_SDK;
}
