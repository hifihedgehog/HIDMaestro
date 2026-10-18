/* Case 5, the DualShock 3 in sixaxis.sys form (dualshock-3-full,
 * Ds3Sixaxis 1): driver.c:129-215, 1273-1324, 1960-2010, 3079-3100.
 *
 * The SDK writes the native 49-byte report into the extended lane with
 * DataSize 0 (HMController.cs:935-944, the profile's extendedReport is
 * alwaysArmed). Byte offsets of the native report are the profile's and
 * driver.c:131-136's: buttons at 2-4, sticks at 6-9, pressures at 14-25,
 * battery at 30, big-endian 10-bit motion at 41-48. */
#include "hm_scenario.h"

static PlanProfile *g_ds3;
static HmRng g_rng;

static uint32_t ReadLen(void) { return g_ds3->readLength ? g_ds3->readLength : 12; }

/* A native report written as the SDK writes it, the read the HID class
 * keeps parked completed by the worker, then the report 0 feature. */
static void State(Ctl *c, const uint8_t raw[49], int featureSizes)
{
    if (Parked(c) == 0) HidRead(c, ReadLen());
    Frame(c, NULL, 0, NULL, raw, 49);
    if (featureSizes)
        for (uint32_t size = 0; size <= 65; size++) HidGetFeature(c, 0x00, size);
    else
        HidGetFeature(c, 0x00, 49);
}

static void Neutral(uint8_t raw[49])
{
    memset(raw, 0, 49);
    raw[0] = 0x01;
    raw[6] = raw[7] = raw[8] = raw[9] = 0x80;
    raw[41] = 0x02; raw[43] = 0x02; raw[45] = 0x02; raw[47] = 0x02;   /* 512, the motion center */
}

static void StreamScripted(void)
{
    Ctl c;
    uint8_t raw[49];
    StreamBegin("ds3/%s/states-scripted", g_ds3->id);
    if (!CtlStart(&c, g_ds3, 0, SDK_ALL, 0)) return;
    Neutral(raw);
    State(&c, raw, 1);
    VersionMidRun(&c);
    /* Each button bit alone, bytes 2 to 4. */
    for (int byte = 2; byte <= 4; byte++)
        for (int bit = 0; bit < 8; bit++) {
            Neutral(raw);
            raw[byte] = (uint8_t)(1 << bit);
            State(&c, raw, 0);
        }
    /* Every d-pad nibble, the hat's sixteen cases (driver.c:175-185). */
    for (int nib = 0; nib < 16; nib++) {
        Neutral(raw);
        raw[2] = (uint8_t)(nib << 4);
        State(&c, raw, 0);
    }
    /* Sticks and pressures at their edges. */
    static const uint8_t edges[] = { 0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF };
    for (int byte = 6; byte <= 25; byte++) {
        if (byte >= 10 && byte <= 13) continue;
        for (int e = 0; e < (int)ARRAYSIZE(edges); e++) {
            Neutral(raw);
            raw[byte] = edges[e];
            State(&c, raw, 0);
        }
    }
    /* Motion words at their limits, and past the 10 bits: above 0x3FF the
     * accelerometer X mirror 0x3FF - x wraps in a USHORT (driver.c:211). */
    static const uint16_t words[] = { 0x0000, 0x0001, 0x01FF, 0x0200, 0x0201, 0x03FE, 0x03FF,
                                      0x0400, 0x0401, 0x07FF, 0x0800, 0x8000, 0xFC00, 0xFFFE, 0xFFFF };
    for (int w = 41; w <= 47; w += 2)
        for (int v = 0; v < (int)ARRAYSIZE(words); v++) {
            Neutral(raw);
            raw[w] = (uint8_t)(words[v] >> 8);
            raw[w + 1] = (uint8_t)words[v];
            uint16_t x = words[v];
            State(&c, raw, x == 0 || x == 0x03FF || x == 0x0400 || x == 0xFFFF);
        }
    /* Bytes the driver passes through or ignores. */
    for (int byte = 0; byte < 49; byte++) {
        Neutral(raw);
        raw[byte] ^= 0xA5;
        State(&c, raw, 0);
    }
    /* Extended lane sizes around 49: the form needs at least 49
     * (driver.c:1966-1968), the generic path takes up to 80. */
    static const uint32_t sizes[] = { 0, 1, 12, 48, 49, 50, 79, 80, 81, 200 };
    for (int i = 0; i < (int)ARRAYSIZE(sizes); i++) {
        uint8_t ext[80];
        RngFill(&g_rng, ext, sizeof(ext));
        if (Parked(&c) == 0) HidRead(&c, ReadLen());
        Frame(&c, NULL, 0, NULL, ext, sizes[i]);
        HidGetFeature(&c, 0x00, 49);
        HidGetInput(&c, 0x00, 64);
        HidGetInput(&c, 0x01, 64);
    }
    /* A frame on the data lane, as an SDK without the form writes. */
    {
        uint8_t data[12];
        RngFill(&g_rng, data, sizeof(data));
        if (Parked(&c) == 0) HidRead(&c, ReadLen());
        Frame(&c, data, sizeof(data), NULL, NULL, 0);
        HidGetFeature(&c, 0x00, 49);
    }
    /* Reads with buffers too small for the 12-byte report. */
    for (uint32_t len = 0; len <= 13; len++) {
        HidRead(&c, len);
        Neutral(raw);
        raw[3] = (uint8_t)len;
        Frame(&c, NULL, 0, NULL, raw, 49);
    }
    /* The feature with ids other than 0 in the caller's buffer, which the
     * HID class delivers as report 0 (driver.c:3082-3088). */
    static const uint8_t asked[] = { 0x01, 0xF2, 0xEF, 0xF5, 0xF8 };
    for (int i = 0; i < (int)ARRAYSIZE(asked); i++) HidGetFeature(&c, asked[i], 64);
    CtlStop(&c);
}

static void StreamRandomStates(void)
{
    Ctl c;
    StreamBegin("ds3/%s/states-random", g_ds3->id);
    if (!CtlStart(&c, g_ds3, 0, SDK_ALL, 0)) return;
    for (int i = 0; i < 10000; i++) {
        uint8_t raw[49];
        RngFill(&g_rng, raw, sizeof(raw));
        if (Parked(&c) == 0) HidRead(&c, ReadLen());
        Frame(&c, NULL, 0, NULL, raw, 49);
        HidGetFeature(&c, 0x00, (uint32_t)RngNext(&g_rng, 66));
        if (RngNext(&g_rng, 8) == 0) HidGetInput(&c, 0x00, 1 + (uint32_t)RngNext(&g_rng, 64));
    }
    CtlStop(&c);
}

/* Output commands 1, 2, 3, 9 and 10 at lengths 0 to 49, and the
 * 0x00-led 49-byte form, through one write IOCTL. Each is followed by
 * the ring slot PublishOutput wrote. These are the driver's inputs, not a
 * host's writes: live, HID class hands the driver the output report's
 * declared length, cutting a longer write and refusing a shorter one
 * (Wine hidclass.sys device.c:484, 500-506, and the B0 recording), so
 * only the 48-byte form reaches it from a program. */
static void StreamOutput(int viaSetOutput)
{
    Ctl c;
    static const uint8_t cmds[] = { 1, 2, 3, 9, 10 };
    StreamBegin("ds3/%s/output-%s", g_ds3->id, viaSetOutput ? "setoutput" : "write");
    if (!CtlStart(&c, g_ds3, 0, SDK_ALL, 0)) return;
    for (int k = 0; k < (int)ARRAYSIZE(cmds); k++) {
        for (uint32_t len = 0; len <= 49; len++) {
            uint8_t buf[49];
            RngFill(&g_rng, buf, sizeof(buf));
            if (len) buf[0] = cmds[k];
            if (viaSetOutput) HidSetOutput(&c, buf, len); else HidWrite(&c, buf, len);
        }
        uint8_t led[49];
        RngFill(&g_rng, led, sizeof(led));
        led[0] = 0x00;
        led[1] = cmds[k];
        if (viaSetOutput) HidSetOutput(&c, led, 49); else HidWrite(&c, led, 49);
    }
    VersionMidRun(&c);
    /* The LED command with each combination of LED modes (driver.c:1312-1318). */
    for (int m = 0; m < 16; m++) {
        uint8_t buf[8] = { 1, 0, 0, 0, 0, 0, 0, 0 };
        for (int b = 0; b < 4; b++) buf[4 + b] = (uint8_t)((m >> b) & 1 ? 1 + b % 2 : 0);
        if (viaSetOutput) HidSetOutput(&c, buf, 8); else HidWrite(&c, buf, 8);
    }
    /* Commands other than the five, and random sequences. */
    for (int i = 0; i < 2000; i++) {
        uint8_t buf[64];
        uint32_t len = (uint32_t)RngNext(&g_rng, 65);
        RngFill(&g_rng, buf, sizeof(buf));
        if (RngNext(&g_rng, 2)) buf[0] = cmds[RngNext(&g_rng, ARRAYSIZE(cmds))];
        if (RngNext(&g_rng, 10) == 0 && len > 0) { buf[0] = 0x00; len = 49; }
        if (viaSetOutput) HidSetOutput(&c, buf, len); else HidWrite(&c, buf, len);
    }
    CtlStop(&c);
}

void CaseDs3(void)
{
    g_ds3 = PlanFind("ds3", NULL);
    if (!g_ds3) HmFail("the plan has no ds3 profile");
    RngInit(&g_rng, g_seed, 0x5301);
    StreamScripted();
    StreamRandomStates();
    StreamOutput(0);
    StreamOutput(1);
}

/* The sixaxis form under each version parameter, its generator seeded
 * apart per parameter (hm_case_version.c). */
void CaseDs3Version(void)
{
    g_ds3 = PlanFind("ds3", NULL);
    if (!g_ds3) HmFail("the plan has no ds3 profile");
    RngInit(&g_rng, g_seed, 0x5310 + (uint64_t)g_versionParam);
    StreamScripted();
    StreamOutput(0);
    StreamOutput(1);
}
