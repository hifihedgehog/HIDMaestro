/* Case 5, the Sony feature table (driver.c:3102-3372 with its tables and
 * CRC, 24-127): every catalog profile with vendor 0x054C on the UMDF2
 * backend, every feature report id its descriptor declares and the ids
 * the table answers whether declared or not, at every output buffer size
 * from 0 to 65, Bluetooth on and off, controller indexes 0, 1 and 15.
 * heusinkveld-ultimate-pedals is the positive control: no Sony vendor id,
 * so the same requests fall through to the PID path, once with no PID
 * section and once with the section a force-feedback consumer creates. */
#include "hm_scenario.h"

/* The ids the table answers (driver.c:3126-3129) and the PID ids
 * (driver.h:566-581). */
static const uint8_t k_tableIds[] = { 0x02, 0x05, 0x09, 0x12, 0x20, 0x22, 0xA3 };
static const uint8_t k_pidIds[] = { 0x11, 0x12, 0x13, 0x14, 0x1B, 0x1C };

static int CollectIds(PlanProfile *p, uint8_t ids[256], int withPid)
{
    uint8_t seen[256] = { 0 };
    int n = 0;
    for (int i = 0; i < p->featureCount; i++) seen[p->features[i]] = 1;
    for (int i = 0; i < (int)ARRAYSIZE(k_tableIds); i++) seen[k_tableIds[i]] = 1;
    if (withPid) for (int i = 0; i < (int)ARRAYSIZE(k_pidIds); i++) seen[k_pidIds[i]] = 1;
    seen[0x00] = 1;   /* report 0: a descriptor without ids delivers every read as it */
    /* The table's ids first, so the first outputs a golden file keeps are
     * the answers, then the rest in order. */
    for (int i = 0; i < (int)ARRAYSIZE(k_tableIds); i++) { ids[n++] = k_tableIds[i]; seen[k_tableIds[i]] = 0; }
    for (int id = 0; id < 256; id++) if (seen[id]) ids[n++] = (uint8_t)id;
    return n;
}

/* label NULL: the profile's own vendor and product. Otherwise the stream
 * is named by label and VendorId and ProductId take vid and pid when they
 * are nonzero. */
static void StreamFeaturesAs(PlanProfile *p, int bluetooth, int index, int pid, const char *label, DWORD vid, DWORD pidValue);

static void StreamFeatures(PlanProfile *p, int bluetooth, int index, int pid)
{
    StreamFeaturesAs(p, bluetooth, index, pid, NULL, 0, 0);
}

static void StreamFeaturesAs(PlanProfile *p, int bluetooth, int index, int pid, const char *label, DWORD vid, DWORD pidValue)
{
    Ctl c;
    uint8_t ids[256];
    int n = CollectIds(p, ids, !strcmp(p->group, "control"));
    if (label)
        StreamBegin("sony/%s/%s-bt%d-idx%d", p->id, label, bluetooth, index);
    else if (!strcmp(p->group, "control"))
        StreamBegin("sony/%s/control-%s-idx%d", p->id, pid ? "pid" : "nopid", index);
    else
        StreamBegin("sony/%s/bt%d-idx%d", p->id, bluetooth, index);
    memset(&c, 0, sizeof(c));
    c.p = p;
    c.index = index;
    SdkCreate(&c.sdk, index, SDK_ALL);
    if (pid) {
        uint8_t rids[6] = { 0 };
        if (p->hasPidRids) memcpy(rids, p->pidRids, 6);
        SdkPidCreate(&c.sdk, rids);
        SdkPidPool(&c.sdk, 0x1000, 16, 1);
    }
    ConfigWrite(p, index);
    if (strcmp(p->group, "control")) ConfigOverrideDword(index, "Bluetooth", (DWORD)bluetooth);
    if (vid) ConfigOverrideDword(index, "VendorId", vid);
    if (pidValue) ConfigOverrideDword(index, "ProductId", pidValue);
    c.dev = DeviceAdd(0);
    VersionAfterAdd(&c);
    if (!c.dev) return;
    for (int i = 0; i < n; i++) {
        if (i == n / 2) VersionMidRun(&c);
        for (uint32_t size = 0; size <= 65; size++)
            HidGetFeature(&c, ids[i], size);
    }
    CtlStop(&c);
}

void CaseSony(void)
{
    static const int indexes[] = { 0, 1, 15 };
    for (int i = 0; i < g_plan.profileCount; i++) {
        PlanProfile *p = &g_plan.profiles[i];
        if (strcmp(p->group, "sony")) continue;
        for (int bt = 0; bt <= 1; bt++)
            for (int k = 0; k < (int)ARRAYSIZE(indexes); k++)
                StreamFeatures(p, bt, indexes[k], 0);
    }
    PlanProfile *control = PlanFind("control", NULL);
    if (!control) HmFail("the plan has no control profile");
    StreamFeatures(control, 0, 0, 0);
    StreamFeatures(control, 0, 0, 1);
    StreamFeatures(control, 0, 15, 1);

    /* The DualShock 4 product ids the calibration order keys on
     * (driver.c:3140-3147): 0x0BA0, which no catalog profile has, and
     * 0x05C4, on the Bluetooth DualShock 4's configuration. */
    PlanProfile *ds4bt = PlanFind("sony", "dualshock-4-v2-bt");
    if (!ds4bt) HmFail("the plan has no dualshock-4-v2-bt");
    for (int bt = 0; bt <= 1; bt++) {
        StreamFeaturesAs(ds4bt, bt, 0, 0, "pid-0BA0", 0, 0x0BA0);
        StreamFeaturesAs(ds4bt, bt, 0, 0, "pid-05C4", 0, 0x05C4);
    }
    /* A 32-bit VendorId: the driver keeps its low word (driver.c:450), so
     * 0x1234054C passes the Sony gate and 0x054C0000 does not. */
    StreamFeaturesAs(ds4bt, 1, 0, 0, "vendor-1234054C", 0x1234054C, 0);
    StreamFeaturesAs(ds4bt, 1, 0, 0, "vendor-054C0000", 0x054C0000, 0);
    StreamFeaturesAs(control, 0, 0, 0, "vendor-ABCD054C", 0xABCD054C, 0);

    /* The UMDF2 Valve profiles: SDL's feature messages as SET_FEATURE of
     * report 0, each followed by GET_FEATURE of report 0 at every size
     * (FB1). SDL sends a 65-byte buffer, report number 0 first, over USB
     * (SDL-reference 804cf65 SDL_hidapi_steam.c:329-331, ResetSteamController
     * 441-551, ENABLE_MOUSE_MODE undefined at 187): GET_ATTRIBUTES_VALUES
     * 0x83, CLEAR_DIGITAL_MAPPINGS 0x81, LOAD_DEFAULT_SETTINGS 0x8E with a
     * zero, and SET_SETTINGS_VALUES 0x87 with its length 12 and four
     * settings, WIRELESS_PACKET_VERSION (49) 2, LEFT_TRACKPAD_MODE (7) and
     * RIGHT_TRACKPAD_MODE (8) TRACKPAD_NONE (7), SMOOTH_ABSOLUTE_MOUSE (24)
     * 0 (steam/controller_constants.h:58-71, 400-480). Each row is the
     * message's length, then the bytes after the report number. */
    static const uint8_t messages[][16] = {
        { 2, 0x83, 0x00 }, { 2, 0x81, 0x00 }, { 2, 0x8E, 0x00 },
        { 14, 0x87, 0x0C, 0x31, 0x02, 0x00, 0x07, 0x07, 0x00, 0x08, 0x07, 0x00, 0x18, 0x00, 0x00 },
    };
    for (int i = 0; i < g_plan.profileCount; i++) {
        PlanProfile *p = &g_plan.profiles[i];
        if (strcmp(p->group, "steam")) continue;
        Ctl c;
        StreamBegin("steam/%s/set-get-report0", p->id);
        if (!CtlStart(&c, p, 0, SDK_ALL, 0)) continue;
        for (int m = 0; m < (int)ARRAYSIZE(messages); m++) {
            uint8_t buf[65] = { 0 };
            /* With report ids the report number leads. Without them the
             * HID class passes the 64 bytes after it (M3). */
            int ids = p->usesReportIds;
            memcpy(buf + (ids ? 1 : 0), messages[m] + 1, messages[m][0]);
            HidSetFeature(&c, buf, ids ? 65 : 64);
            for (uint32_t size = 0; size <= 65; size++) HidGetFeature(&c, 0x00, size);
        }
        CtlStop(&c);
    }
}

/* The Sony table under each version parameter: every sony profile with its
 * own Bluetooth value at index 0, and the control without a PID section
 * (hm_case_version.c). */
void CaseSonyVersion(void)
{
    for (int i = 0; i < g_plan.profileCount; i++) {
        PlanProfile *p = &g_plan.profiles[i];
        if (strcmp(p->group, "sony")) continue;
        StreamFeatures(p, p->bluetooth, 0, 0);
    }
    PlanProfile *control = PlanFind("control", NULL);
    if (!control) HmFail("the plan has no control profile");
    StreamFeatures(control, 0, 0, 0);
}

void FeatureSweep(Ctl *c)
{
    uint8_t ids[256];
    int n = CollectIds(c->p, ids, 0);
    for (int i = 0; i < n; i++)
        for (uint32_t size = 0; size <= 65; size++)
            HidGetFeature(c, ids[i], size);
}
