/* Extended-lane frames at every length, for the bound of the design
 * note's decision (c): the v2.0.0 driver sends an extended frame only at
 * the declared input length of the report its first byte names, or for a
 * descriptor without report IDs of its one input report, and 9bd6279's
 * driver bounds it only by the lane's 80 bytes (driver.c:1951-1958,
 * SharedMemoryIO's EXTENDED_DATA_CAPACITY). This records 9bd6279's side.
 *
 * Profiles: dualsense-bt and dualshock-4-v2-bt (report IDs, extended
 * reports 0x31 and 0x11), dualshock-3-full (no report IDs, a 49-byte
 * native report on a 12-byte declared input), the control profile and
 * the first UMDF2 Valve profile. First bytes 0x00, 0x01, 0x11, 0x21, 0x30,
 * 0x31 and 0x3F, the input report IDs the catalog's UMDF2 profiles use,
 * at every length from 1 to 80: per frame, a read parked first, then the
 * frame, so the stream shows the report that went out and the number of
 * reads still parked. */
#include "hm_scenario.h"

static void Lengths(PlanProfile *p)
{
    if (!p) HmFail("the plan lacks a profile the extended-length streams name");
    Ctl c;
    StreamBegin("extended-lengths/%s", p->id);
    if (!CtlStart(&c, p, 0, SDK_ALL, 0)) return;
    uint32_t readLen = p->readLength ? p->readLength : 64;
    static const uint8_t firsts[] = { 0x00, 0x01, 0x11, 0x21, 0x30, 0x31, 0x3F };
    uint8_t ext[80];
    for (int f = 0; f < (int)ARRAYSIZE(firsts); f++)
        for (uint32_t len = 1; len <= sizeof(ext); len++) {
            for (uint32_t i = 0; i < len; i++) ext[i] = (uint8_t)(i * 13 + len);
            ext[0] = firsts[f];
            HidRead(&c, readLen);
            Frame(&c, NULL, 0, NULL, ext, len);
            int parked = Parked(&c);
            RecOut("parked", &parked, 4);
        }
    CtlStop(&c);
}

void CaseExtendedLengths(void)
{
    Lengths(PlanFind("sony", "dualsense-bt"));
    Lengths(PlanFind("sony", "dualshock-4-v2-bt"));
    Lengths(PlanFind("ds3", NULL));
    Lengths(PlanFind("control", NULL));
    Lengths(PlanFind("steam", NULL));
}
