/* Case 5, the Switch Pro responder (driver.c:472-518, 1357-1869 and its
 * call sites), driven through EvtIoDeviceControl with the switch-pro
 * profile's configuration.
 *
 * Output reports arrive as SDL sends them over Bluetooth: report 0x01 is
 * the 49-byte SwitchSubcommandOutputPacket_t, the report id, a 4-bit
 * packet number, two neutral rumble frames, the subcommand and its data,
 * zero-padded to k_unSwitchBluetoothPacketLength (SDL-reference 804cf65,
 * src/joystick/hidapi/SDL_hidapi_switch.c:129-132, 259-274, 468-500,
 * 591-608). Report 0x80 is the proprietary packet, id then command
 * (:276-282, 530-557). */
#include "hm_scenario.h"

#define SWITCH_BT_PACKET 49
#define SUBCMD_DATA_MAX  (SWITCH_BT_PACKET - 11)

static PlanProfile *g_sw;
static HmRng g_rng;

/* SetNeutralRumble's standard value, SDL_hidapi_switch.c:604-608. */
static const uint8_t k_neutralRumble[8] = { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 };

typedef struct Sdl {
    Ctl *c;
    uint8_t number;
    int viaSetOutput;
} Sdl;

static uint32_t ReadLen(void) { return g_sw->readLength ? g_sw->readLength : SWITCH_BT_PACKET; }

/* ConstructSubcommand and WritePacket (SDL_hidapi_switch.c:468-506): a
 * read is parked first, as SDL's ReadSubcommandReply reads right after
 * the write and the HID class keeps a read pending. */
static void SdlSubcommand(Sdl *s, uint8_t id, const uint8_t *data, uint32_t len)
{
    uint8_t pkt[SWITCH_BT_PACKET] = { 0 };
    pkt[0] = 0x01;
    pkt[1] = s->number;
    memcpy(pkt + 2, k_neutralRumble, 8);
    pkt[10] = id;
    if (len) memcpy(pkt + 11, data, len);
    s->number = (uint8_t)((s->number + 1) & 0xF);
    if (Parked(s->c) == 0) HidRead(s->c, ReadLen());
    if (s->viaSetOutput) HidSetOutput(s->c, pkt, sizeof(pkt));
    else HidWrite(s->c, pkt, sizeof(pkt));
}

static void SdlSpiRead(Sdl *s, uint32_t address, uint8_t length)
{
    /* SwitchSPIOpData_t, a 32-bit address then a length (:194-198). */
    uint8_t op[5] = { (uint8_t)address, (uint8_t)(address >> 8), (uint8_t)(address >> 16), (uint8_t)(address >> 24), length };
    SdlSubcommand(s, 0x10, op, 5);
}

static void Ticks(Ctl *c, int n)
{
    for (int i = 0; i < n; i++) {
        if (Parked(c) == 0) HidRead(c, ReadLen());
        SwitchTick(c);
    }
}

static const uint8_t *Body(int i)
{
    if (g_plan.bodyCount == 0) HmFail("the plan has no Switch Pro bodies");
    return g_plan.bodies[i % g_plan.bodyCount].data;
}

/* SDL's Bluetooth sequence for a Pro Controller: HIDAPI_DriverSwitch_
 * InitDevice reads the device info (:1599, 676-688), then
 * HIDAPI_DriverSwitch_OpenJoystick loads the stick calibration, user
 * block 0x8010 then factory 0x603D (:1660-1662, 979-1021, offsets
 * :134-140), the IMU calibration 0x6020 then the user block 0x8026
 * (:1675-1677, 1106-1143, offsets :142-148), enables vibration (:1680,
 * 731-733) and sets the player lights for player index 0 (:1713-1715,
 * 795-805). SetInputMode writes nothing when the mode SDL read first is
 * already the one it wants (:735-750), which is 0x30 here. Enabling the
 * sensors sends EnableIMU (:1949, 973-976). */
static void SdlInit(Sdl *s)
{
    static const uint8_t one = 1;
    static const uint8_t player0 = 0x1;
    SdlSubcommand(s, 0x02, NULL, 0);
    SwitchTick(s->c);
    SdlSpiRead(s, 0x8010, 0x8025 - 0x8010 + 1);
    SdlSpiRead(s, 0x603D, 0x604E - 0x603D + 1);
    SdlSpiRead(s, 0x6020, 0x6037 - 0x6020 + 1);
    SdlSpiRead(s, 0x8026, 0x8039 - 0x8026 + 1);
    SdlSubcommand(s, 0x48, &one, 1);
    SdlSubcommand(s, 0x30, &player0, 1);
    Ticks(s->c, 2);
    SdlSubcommand(s, 0x40, &one, 1);
}

static int Start(Ctl *c, int index)
{
    if (!CtlStart(c, g_sw, index, SDK_ALL, 0)) return 0;
    return 1;
}

/* SDL's whole sequence on a new pad, with the pad's input before and
 * after it. */
static void StreamSdlInit(int index, int viaSetOutput)
{
    Ctl c;
    Sdl s = { &c, 0, viaSetOutput };
    StreamBegin("switch/%s/sdl-init%s-idx%d", g_sw->id, viaSetOutput ? "-setoutput" : "", index);
    if (!Start(&c, index)) return;
    Ticks(&c, 3);                         /* 0x3F frames before any protocol traffic */
    Frame(&c, Body(0), 48, NULL, NULL, 0);
    Ticks(&c, 2);
    VersionMidRun(&c);
    SdlInit(&s);
    for (int i = 0; i < g_plan.bodyCount; i++) {
        Frame(&c, Body(i), 48, NULL, NULL, 0);
        Ticks(&c, 1);
    }
    HidGetInput(&c, 0x30, ReadLen());
    HidGetInput(&c, 0x01, ReadLen());
    HidGetInput(&c, 0x00, ReadLen());
    HidGetInput(&c, 0x3F, ReadLen());
    CtlStop(&c);
}

/* The polled input report for every id before the handshake, after it,
 * and after stream frames and replies have passed: only ids 0 and 1 read
 * the cache the stream refreshes (driver.c:1856-1859, 3603-3613). */
static void StreamGetInput(void)
{
    Ctl c;
    Sdl s = { &c, 0, 0 };
    StreamBegin("switch/%s/get-input-report", g_sw->id);
    if (!Start(&c, 0)) return;
    GetInputSweep(&c);
    Frame(&c, Body(6), 48, NULL, NULL, 0);
    Ticks(&c, 2);
    GetInputSweep(&c);
    SdlInit(&s);
    GetInputSweep(&c);
    Frame(&c, Body(7), 48, NULL, NULL, 0);
    Ticks(&c, 3);
    SdlSubcommand(&s, 0x02, NULL, 0);
    GetInputSweep(&c);
    CtlStop(&c);
}

/* The MAC the driver fabricates per controller index, 0x30 plus the index
 * in its last byte (driver.c:484-486), through the device-info reply
 * (subcommand 0x02, driver.c:1640-1649) and the USB status reply (0x80
 * 0x01, driver.c:1572-1583), at indexes 0 to 15 and 255 (review F8). */
static void StreamMac(int index)
{
    Ctl c;
    Sdl s = { &c, 0, 0 };
    StreamBegin("switch/%s/mac-idx%d", g_sw->id, index);
    if (!Start(&c, index)) return;
    SdlSubcommand(&s, 0x02, NULL, 0);
    uint8_t status[2] = { 0x80, 0x01 };
    if (Parked(&c) == 0) HidRead(&c, ReadLen());
    HidWrite(&c, status, 2);
    CtlStop(&c);
}

/* The polled input report for ids 0 and 1 straight after a 0x21 reply has
 * passed: the cache holds the last stream frame, never a reply
 * (driver.c:1856-1859, 3603-3613). DD-5's cache rule replays it (N9). */
static void StreamGetInputAfterReply(void)
{
    Ctl c;
    Sdl s = { &c, 0, 0 };
    StreamBegin("switch/%s/get-input-after-reply", g_sw->id);
    if (!Start(&c, 0)) return;
    Frame(&c, Body(8), 48, NULL, NULL, 0);
    SdlInit(&s);
    Ticks(&c, 2);
    static const uint8_t ids[] = { 0x00, 0x01 };
    static const uint32_t sizes[] = { 12, 49, 64, 362 };
    for (int round = 0; round < 3; round++) {
        if (round == 1) VersionMidRun(&c);
        /* A device-info request, then the IMU calibration read SDL makes. */
        static const uint8_t spi[5] = { 0x20, 0x60, 0x00, 0x00, 0x18 };
        if (round == 1) SdlSubcommand(&s, 0x10, spi, 5);
        else SdlSubcommand(&s, 0x02, NULL, 0);
        for (int i = 0; i < 2; i++)
            for (int k = 0; k < (int)ARRAYSIZE(sizes); k++) HidGetInput(&c, ids[i], sizes[k]);
        Ticks(&c, 1);
        for (int i = 0; i < 2; i++) HidGetInput(&c, ids[i], ReadLen());
    }
    CtlStop(&c);
}

/* One subcommand id at every argument length the 49-byte report allows,
 * delivered both cut to its length and padded to 49 as SDL pads it. The
 * cut writes are the driver's inputs here only: live, HID class hands the
 * driver the report's declared length (Wine hidclass.sys device.c:484,
 * 500-506), so a program's write arrives padded or not at all. */
static void StreamSubcommand(int id)
{
    Ctl c;
    Sdl s = { &c, 0, 0 };
    StreamBegin("switch/%s/subcommand-%02X", g_sw->id, id);
    if (!Start(&c, 0)) return;
    Frame(&c, Body(1), 48, NULL, NULL, 0);
    for (uint32_t len = 0; len <= SUBCMD_DATA_MAX; len++) {
        uint8_t data[SUBCMD_DATA_MAX];
        RngFill(&g_rng, data, len);
        uint8_t pkt[SWITCH_BT_PACKET] = { 0 };
        pkt[0] = 0x01;
        pkt[1] = (uint8_t)(len & 0xF);
        memcpy(pkt + 2, k_neutralRumble, 8);
        pkt[10] = (uint8_t)id;
        memcpy(pkt + 11, data, len);
        if (Parked(&c) == 0) HidRead(&c, ReadLen());
        HidWrite(&c, pkt, 11 + len);
        SdlSubcommand(&s, (uint8_t)id, data, len);
    }
    Ticks(&c, 2);
    CtlStop(&c);
}

/* The 0x80 family at every command byte and payload length. */
static void StreamProprietary(int cmd)
{
    Ctl c;
    StreamBegin("switch/%s/usb-command-%02X", g_sw->id, cmd);
    if (!Start(&c, 0)) return;
    for (uint32_t len = 0; len < SWITCH_BT_PACKET; len++) {
        uint8_t pkt[SWITCH_BT_PACKET] = { 0 };
        pkt[0] = 0x80;
        if (len) {
            pkt[1] = (uint8_t)cmd;
            RngFill(&g_rng, pkt + 2, len - 1);
        }
        if (Parked(&c) == 0) HidRead(&c, ReadLen());
        if (len & 1) HidSetOutput(&c, pkt, 1 + len);
        else HidWrite(&c, pkt, 1 + len);
    }
    Ticks(&c, 2);
    CtlStop(&c);
}

/* SPI reads over the whole fabricated image (driver.c:1384-1442) and its
 * edges, every length up to past the 0x1D cap. */
static void StreamSpi(void)
{
    Ctl c;
    Sdl s = { &c, 0, 0 };
    StreamBegin("switch/%s/spi-read", g_sw->id);
    if (!Start(&c, 0)) return;
    for (uint32_t a = 0x5FF0; a < 0x60C0; a++)
        for (uint32_t len = 0; len <= 0x20; len += (a & 7) == 0 ? 1 : 7)
            SdlSpiRead(&s, a, (uint8_t)len);
    static const uint32_t distantAddresses[] = { 0x00000000, 0x00007FFF, 0x00008010, 0x00008026, 0x0007FFFF, 0x00080000, 0xFFFFFFF0, 0x01006020 };
    for (int i = 0; i < (int)ARRAYSIZE(distantAddresses); i++) SdlSpiRead(&s, distantAddresses[i], 0x1D);
    CtlStop(&c);
}

/* Subcommand 0x03 with each mode, each followed by stream periods: modes
 * 0x30 and 0x31 stream, any other stores or not (driver.c:1651-1656,
 * 1822-1823). */
static void StreamInputMode(void)
{
    Ctl c;
    Sdl s = { &c, 0, 0 };
    StreamBegin("switch/%s/input-mode", g_sw->id);
    if (!Start(&c, 0)) return;
    SdlInit(&s);
    Frame(&c, Body(2), 48, NULL, NULL, 0);
    static const uint8_t modes[] = { 0x3F, 0x30, 0x31, 0x00, 0x21, 0x32, 0x33, 0x3F, 0x80, 0xFF, 0x31, 0x30 };
    for (int i = 0; i < (int)ARRAYSIZE(modes); i++) {
        SdlSubcommand(&s, 0x03, &modes[i], 1);
        Ticks(&c, 3);
    }
    SdlSubcommand(&s, 0x03, NULL, 0);
    Ticks(&c, 2);
    {
        /* The mode byte with no argument delivered: payload of 10 bytes. */
        uint8_t pkt[11] = { 0x01, 0x0F, 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40, 0x03 };
        HidWrite(&c, pkt, 11);
        Ticks(&c, 2);
    }
    CtlStop(&c);
}

/* IMU on and off (driver.c:1684-1686, 1467-1469), with bodies carrying
 * motion. */
static void StreamImu(void)
{
    Ctl c;
    Sdl s = { &c, 0, 0 };
    StreamBegin("switch/%s/imu", g_sw->id);
    if (!Start(&c, 0)) return;
    SdlInit(&s);
    static const uint8_t args[] = { 0x00, 0x01, 0x02, 0x00, 0xFF, 0x00, 0x03 };
    for (int i = 0; i < (int)ARRAYSIZE(args); i++) {
        SdlSubcommand(&s, 0x40, &args[i], 1);
        for (int b = 0; b < g_plan.bodyCount; b++) {
            Frame(&c, Body(b), 48, NULL, NULL, 0);
            Ticks(&c, 1);
            if (Parked(&c) == 0) HidRead(&c, ReadLen());
            SdlSubcommand(&s, 0x02, NULL, 0);
        }
    }
    SdlSubcommand(&s, 0x40, NULL, 0);
    Ticks(&c, 1);
    {
        /* A body shorter than 48 bytes, and one shorter than 11
         * (driver.c:1465-1469). */
        Frame(&c, Body(3), 47, NULL, NULL, 0);
        Ticks(&c, 1);
        Frame(&c, Body(3), 10, NULL, NULL, 0);
        Ticks(&c, 1);
        Frame(&c, Body(3), 11, NULL, NULL, 0);
        Ticks(&c, 1);
    }
    CtlStop(&c);
}

/* More replies than HIDMAESTRO_SWITCH_REPLY_SLOTS before any read: the
 * ring drops the oldest (driver.c:1496-1500). */
static void StreamReplyOverflow(void)
{
    Ctl c;
    StreamBegin("switch/%s/reply-overflow", g_sw->id);
    if (!Start(&c, 1)) return;
    Frame(&c, Body(4), 48, NULL, NULL, 0);
    for (int round = 0; round < 3; round++) {
        int n = 3 + round * 2;
        for (int i = 0; i < n; i++) {
            uint8_t pkt[SWITCH_BT_PACKET] = { 0 };
            pkt[0] = 0x01;
            pkt[1] = (uint8_t)i;
            memcpy(pkt + 2, k_neutralRumble, 8);
            static const uint8_t ids[] = { 0x02, 0x10, 0x48, 0x30, 0x04, 0x21, 0x40, 0x22 };
            pkt[10] = ids[i % ARRAYSIZE(ids)];
            pkt[11] = (uint8_t)(0x20 + i);
            pkt[12] = 0x60;
            pkt[15] = 0x10;
            HidWrite(&c, pkt, sizeof(pkt));
            if (i == 2) {
                uint8_t status[2] = { 0x80, 0x01 };
                HidWrite(&c, status, 2);
            }
        }
        for (int i = 0; i < n + 2; i++) HidRead(&c, ReadLen());
        Ticks(&c, 2);
        while (Parked(&c) > 0 && SwitchTick(&c)) { }
    }
    CtlStop(&c);
}

/* The descriptor-idle hold (driver.c:488-517): HKLM\SOFTWARE\HIDMaestro
 * SwitchDescriptorIdleHold, a FILETIME honored within 60 s of its write
 * and never from the future. */
static void StreamHold(const char *name, int present, DWORD type, int64_t ageTicks, DWORD size)
{
    Ctl c;
    Sdl s = { &c, 0, 0 };
    StreamBegin("switch/%s/idle-hold-%s", g_sw->id, name);
    g_clockMs = 120000;
    if (present) {
        uint64_t now = HM_FILETIME_BASE + g_clockMs * 10000ULL;
        uint64_t v = present == 2 ? 0 : (uint64_t)((int64_t)now - ageTicks);
        ValueSet(KeyMake(L"SOFTWARE\\HIDMaestro"), L"SwitchDescriptorIdleHold", type, &v, size);
    }
    if (!Start(&c, 0)) return;
    Ticks(&c, 2);
    SdlInit(&s);
    Frame(&c, Body(5), 48, NULL, NULL, 0);
    Ticks(&c, 3);
    {
        uint8_t usb[2] = { 0x80, 0x02 };
        HidWrite(&c, usb, 2);
        Ticks(&c, 2);
    }
    CtlStop(&c);
}

/* Seeded random output reports of every id and length, interleaved with
 * body updates, stream periods, reads and polled input reports. */
static void StreamRandom(int part, int reports)
{
    Ctl c;
    StreamBegin("switch/%s/random-%d", g_sw->id, part);
    if (!Start(&c, part & 1)) return;
    int sent = 0;
    while (sent < reports) {
        int r = RngNext(&g_rng, 100);
        if (r < 50) {
            uint8_t pkt[64];
            int pick = RngNext(&g_rng, 100);
            uint32_t len = 1 + (uint32_t)RngNext(&g_rng, 64);
            RngFill(&g_rng, pkt, sizeof(pkt));
            pkt[0] = pick < 30 ? 0x01 : pick < 45 ? 0x10 : pick < 65 ? 0x80 : pkt[0];
            if (pkt[0] == 0x01 && len >= 11 && RngNext(&g_rng, 2)) {
                static const uint8_t known[] = { 0x02, 0x03, 0x04, 0x10, 0x21, 0x40, 0x48, 0x30, 0x38 };
                pkt[10] = known[RngNext(&g_rng, ARRAYSIZE(known))];
            }
            if (RngNext(&g_rng, 10) < 7) HidWrite(&c, pkt, len);
            else HidSetOutput(&c, pkt, len);
            sent++;
        } else if (r < 62) {
            uint8_t body[48];
            if (RngNext(&g_rng, 4) == 0) memcpy(body, Body(RngNext(&g_rng, 64)), 48);
            else RngFill(&g_rng, body, 48);
            Frame(&c, body, 48, NULL, NULL, 0);
        } else if (r < 77) {
            SwitchTick(&c);
        } else if (r < 92) {
            if (Parked(&c) < 8) HidRead(&c, RngNext(&g_rng, 10) ? ReadLen() : 1 + (uint32_t)RngNext(&g_rng, 64));
        } else {
            static const uint8_t ids[] = { 0x00, 0x01, 0x30, 0x3F, 0x21 };
            uint8_t id = RngNext(&g_rng, 3) ? ids[RngNext(&g_rng, ARRAYSIZE(ids))] : RngByte(&g_rng);
            HidGetInput(&c, id, (uint32_t)RngNext(&g_rng, 65));
        }
    }
    while (Parked(&c) > 0 && sent < reports + 64 && SwitchTick(&c)) sent++;
    CtlStop(&c);
}

/* Output reports 0x80 and 0x01 on devices the Switch responder is off
 * for: the gate is VID 0x057E with PID 0x2009 (driver.c:480-481), and with
 * it closed both write paths publish the report and complete it with no
 * reply (WRITE_REPORT driver.c:2905-2915, SET_OUTPUT_REPORT 3509-3516).
 * A Sony profile, the control profile and switch-pro's configuration at
 * PID 0x2006 (Joy-Con L, SDL-reference usb_ids.h:126, a Nintendo product
 * id without the responder), with switch-pro itself as the gate-open
 * control in the same run. Each write follows a parked read, which a
 * reply would complete. The 0x80 reports are SDL's proprietary packet
 * (SDL_hidapi_switch.c:276-282) with each command, Status 0x01 to
 * ResetMCU 0x06 (:118-126). The 0x01 reports carry device info 0x02, an
 * SPI read and SetInputMode 0x30 as ConstructSubcommand lays them out
 * (:469-484). Lengths: the report id alone, the header, SDL's Bluetooth
 * 49 and USB 64 (:128-129), and their neighbors. Those are the driver's
 * inputs, not what HID class passes on for a program's write: live it
 * hands the driver the declared report's length and refuses an ID the
 * descriptor does not declare, as 0x80 is here (Wine hidclass.sys
 * device.c:484, 500-506). */
static void StreamGateClosed(PlanProfile *p, const char *label, DWORD pidValue, int viaSetOutput)
{
    static const uint32_t lengths[] = { 1, 2, 11, 48, 49, 50, 64, 65 };
    static const uint8_t proprietary[] = { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
    static const uint8_t spi[5] = { 0x20, 0x60, 0x00, 0x00, 0x18 };
    static const uint8_t mode = 0x30;
    struct { uint8_t id; const uint8_t *data; uint32_t len; } subcommands[] = {
        { 0x02, NULL, 0 }, { 0x10, spi, sizeof(spi) }, { 0x03, &mode, 1 },
    };
    Ctl c;
    uint32_t readLen = p->readLength ? p->readLength : 64;
    uint8_t number = 0;
    StreamBegin("switch/gate/%s/%s", label, viaSetOutput ? "setoutput" : "write");
    memset(&c, 0, sizeof(c));
    c.p = p;
    SdkCreate(&c.sdk, 0, SDK_ALL);
    ConfigWrite(p, 0);
    if (pidValue) ConfigOverrideDword(0, "ProductId", pidValue);
    c.dev = DeviceAdd(0);
    if (!c.dev) return;
    for (int k = 0; k < (int)ARRAYSIZE(lengths); k++) {
        uint32_t len = lengths[k];
        for (int i = 0; i < (int)ARRAYSIZE(proprietary); i++) {
            uint8_t pkt[65] = { 0x80, proprietary[i] };
            if (Parked(&c) == 0) HidRead(&c, readLen);
            if (viaSetOutput) HidSetOutput(&c, pkt, len);
            else HidWrite(&c, pkt, len);
        }
        for (int i = 0; i < (int)ARRAYSIZE(subcommands); i++) {
            uint8_t pkt[65] = { 0x01, number };
            number = (uint8_t)((number + 1) & 0xF);
            memcpy(pkt + 2, k_neutralRumble, 8);
            pkt[10] = subcommands[i].id;
            if (subcommands[i].len) memcpy(pkt + 11, subcommands[i].data, subcommands[i].len);
            if (Parked(&c) == 0) HidRead(&c, readLen);
            if (viaSetOutput) HidSetOutput(&c, pkt, len);
            else HidWrite(&c, pkt, len);
        }
    }
    uint32_t parked = (uint32_t)Parked(&c);
    RecOut("parked", &parked, 4);
    CtlStop(&c);
}

void CaseSwitch(void)
{
    g_sw = PlanFind("switch", NULL);
    if (!g_sw) HmFail("the plan has no switch profile");
    RngInit(&g_rng, g_seed, 0x5101);

    StreamSdlInit(0, 0);
    StreamSdlInit(1, 0);
    StreamSdlInit(0, 1);
    for (int id = 0; id < 256; id++) StreamSubcommand(id);
    for (int cmd = 0; cmd < 256; cmd++) StreamProprietary(cmd);
    StreamGetInput();
    StreamGetInputAfterReply();
    for (int i = 0; i <= 15; i++) StreamMac(i);
    StreamMac(255);
    StreamSpi();
    StreamInputMode();
    StreamImu();
    StreamReplyOverflow();

    PlanProfile *sony = PlanFind("sony", "dualsense");
    PlanProfile *control = PlanFind("control", NULL);
    if (!sony || !control) HmFail("the plan has no dualsense or no control profile");
    for (int via = 0; via <= 1; via++) {
        StreamGateClosed(sony, "dualsense", 0, via);
        StreamGateClosed(control, "control", 0, via);
        StreamGateClosed(g_sw, "nintendo-2006", 0x2006, via);
        StreamGateClosed(g_sw, "switch-pro", 0, via);
    }

    /* 60 s is 600000000 FILETIME ticks (driver.c:511-513). */
    StreamHold("absent", 0, REG_QWORD, 0, 8);
    StreamHold("now", 1, REG_QWORD, 0, 8);
    StreamHold("59999ms", 1, REG_QWORD, 599990000LL, 8);
    StreamHold("60s-less-1", 1, REG_QWORD, 599999999LL, 8);
    StreamHold("60s", 1, REG_QWORD, 600000000LL, 8);
    StreamHold("61s", 1, REG_QWORD, 610000000LL, 8);
    StreamHold("future-1", 1, REG_QWORD, -1LL, 8);
    StreamHold("future-10s", 1, REG_QWORD, -100000000LL, 8);
    StreamHold("zero", 2, REG_QWORD, 0, 8);
    StreamHold("dword", 1, REG_DWORD, 0, 4);
    StreamHold("binary", 1, REG_BINARY, 0, 8);
    StreamHold("qword-4-bytes", 1, REG_QWORD, 0, 4);

    RngInit(&g_rng, g_seed, 0x5102);
    for (int part = 0; part < 10; part++) StreamRandom(part, 10000);
}

/* The responder under each version parameter: SDL's sequence through
 * WRITE_REPORT and SET_OUTPUT_REPORT, and the GET_INPUT_REPORT of ids 0
 * and 1 after each reply (hm_case_version.c). */
void CaseSwitchVersion(void)
{
    g_sw = PlanFind("switch", NULL);
    if (!g_sw) HmFail("the plan has no switch profile");
    StreamSdlInit(0, 0);
    StreamSdlInit(0, 1);
    StreamGetInputAfterReply();
}

void SwitchProbe(Ctl *c)
{
    uint32_t readLen = c->p && c->p->readLength ? c->p->readLength : 64;
    uint8_t usb[64] = { 0x80, 0x02 };
    uint8_t sub[SWITCH_BT_PACKET] = { 0x01, 0x00 };
    memcpy(sub + 2, k_neutralRumble, 8);
    sub[10] = 0x02;
    if (Parked(c) == 0) HidRead(c, readLen);
    HidWrite(c, usb, sizeof(usb));
    if (Parked(c) == 0) HidRead(c, readLen);
    HidWrite(c, sub, sizeof(sub));
    if (c->dev && c->dev->threadCount > 1)
        for (int i = 0; i < 2; i++) {
            if (Parked(c) == 0) HidRead(c, readLen);
            SwitchTick(c);
        }
    uint32_t parked = (uint32_t)Parked(c);
    RecOut("parked", &parked, 4);
}
