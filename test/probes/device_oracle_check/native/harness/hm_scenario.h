/* Helpers the scenarios share: one controller, its SDK side and its
 * device, and the requests the HID class and xinput send it. */
#pragma once
#include "hm_harness.h"

typedef struct Ctl {
    Sdk          sdk;
    HmDevice    *dev;
    PlanProfile *p;
    int          index;
    int          companion;
} Ctl;

/* A new stream on a world with nothing in it. */
void StreamBegin(const char *fmt, ...);
/* The SDK's objects, the profile's configuration, then the device add. */
int  CtlStart(Ctl *c, PlanProfile *p, int index, int sdkFlags, int companion);
void CtlStop(Ctl *c);
/* Under a version parameter, the section's bytes after the device add
 * (CtlStart calls it), part way through the stream (VersionMidRun) and on
 * either side of the removal (CtlStop). Nothing under VERSION_SDK. */
void VersionAfterAdd(Ctl *c);
void VersionMidRun(Ctl *c);

HmThread *CtlThread(Ctl *c, int i);
/* The worker after the SDK signaled a frame: one wait answered from the
 * objects' states, which hands it the input event. */
void RunWorker(Ctl *c);
/* One period of the Switch stream thread: its wait times out at the
 * deadline the driver set. */
int SwitchTick(Ctl *c);
void Frame(Ctl *c, const uint8_t *data, uint32_t len, const uint8_t *gip, const uint8_t *ext, uint32_t extLen);

/* MsHidUmdf's requests (Windows-driver-samples hid/vhidmini2/driver/
 * umdf2/util.c:30-37): an output report as the input buffer, report id
 * first; a read with only an output buffer; a feature or input report
 * get with the report id as the input buffer and the caller's buffer as
 * the output. The caller's buffer holds the id it asked for in byte 0,
 * which driver.c:3083-3088 relies on for a descriptor without report
 * ids. */
HmRequest *HidRead(Ctl *c, uint32_t len);
HmRequest *HidWrite(Ctl *c, const uint8_t *buf, uint32_t len);
HmRequest *HidSetOutput(Ctl *c, const uint8_t *buf, uint32_t len);
HmRequest *HidSetFeature(Ctl *c, const uint8_t *buf, uint32_t len);
HmRequest *HidGetFeature(Ctl *c, uint8_t id, uint32_t len);
HmRequest *HidGetInput(Ctl *c, uint8_t id, uint32_t len);
/* The device-level queries the HID class makes as it starts a device. */
void HidStartQueries(Ctl *c);

/* IOCTL_UMDF_HID_GET_INPUT_REPORT for every report id at sizes 0, 1, 2,
 * 3, 12, 49 and 64: the PID state id, ids 0 and 1 from the cache, every
 * other id refused (driver.c:3548-3619). */
void GetInputSweep(Ctl *c);
/* IOCTL_HID_GET_STRING for string ids 0 to 20 and
 * IOCTL_HID_GET_INDEXED_STRING for indexes 0 to 20, at buffer sizes on
 * both sides of the product string's and the serial's lengths
 * (driver.c:2758-2805). */
void StringSweep(Ctl *c);

/* Requests parked in the device's manual queue: the reads waiting. */
int Parked(Ctl *c);

/* What a device answers once started (hm_case_startup.c): the HID class's
 * start queries, frames through parked reads, the polled input report and
 * the sections, or for the companion its queries and one SET_STATE. */
void DriverProbe(Ctl *c);
void CompanionProbe(Ctl *c);
/* GET_FEATURE of every id the descriptor declares, the Sony table's ids
 * and report 0, at every size from 0 to 65 (hm_case_sony.c). */
void FeatureSweep(Ctl *c);
/* SDL's first Switch requests, 0x80 0x02 at 64 bytes and subcommand 0x02
 * at 49, each after a parked read, then two stream periods when the
 * device runs a stream thread (hm_case_switch.c). */
void SwitchProbe(Ctl *c);
/* Every companion IOCTL at every size the companion tests, then
 * SET_STATE at every length up to 8 (hm_case_xusb.c). */
void XusbAnswerSweep(Ctl *c);
