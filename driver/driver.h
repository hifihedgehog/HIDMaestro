/*
 * HIDMaestro — UMDF2 Virtual HID Minidriver (driver-internal header)
 *
 * This is a lower filter driver under MsHidUmdf.sys. The pass-through
 * driver handles HID class registration; we just respond to IOCTLs.
 *
 * UMDF2 differences from KMDF:
 *   - Compiles as a DLL, not .sys
 *   - Uses EvtIoDeviceControl (not InternalDeviceControl)
 *   - HID_XFER_PACKET is marshalled differently (IOCTL_UMDF_HID_*)
 *   - Includes <windows.h> instead of <ntddk.h>
 */

#ifndef DRIVER_H
#define DRIVER_H

#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <wdf.h>
#include <hidport.h>
#include <devpropdef.h>

/* Hard caps on the descriptor and report buffers the driver allocates.
 * Previously lived in include/hidmaestro.h alongside an old IOCTL-based
 * design that's now obsolete (the driver and SDK communicate via shared
 * memory, not IOCTLs). Inlined here so the obsolete header can go away. */
#define HIDMAESTRO_MAX_DESCRIPTOR_SIZE  4096
#define HIDMAESTRO_MAX_REPORT_SIZE      1024

/*
 * Common definitions shared with the vhidmini2 sample.
 * These must match what MsHidUmdf.sys expects.
 */
#ifndef IOCTL_UMDF_HID_SET_FEATURE
#define IOCTL_UMDF_HID_SET_FEATURE          0x210003
#endif
#ifndef IOCTL_UMDF_HID_GET_FEATURE
#define IOCTL_UMDF_HID_GET_FEATURE          0x210007
#endif
#ifndef IOCTL_UMDF_HID_SET_OUTPUT_REPORT
#define IOCTL_UMDF_HID_SET_OUTPUT_REPORT    0x21000B
#endif
#ifndef IOCTL_UMDF_HID_GET_INPUT_REPORT
#define IOCTL_UMDF_HID_GET_INPUT_REPORT     0x21000F
#endif

/* ------------------------------------------------------------------ */
/*  Device context                                                     */
/* ------------------------------------------------------------------ */

typedef struct _DEVICE_CONTEXT {

    WDFDEVICE   Device;

    /* HID report descriptor (set by user-mode, returned to HID class) */
    UCHAR   ReportDescriptor[HIDMAESTRO_MAX_DESCRIPTOR_SIZE];
    ULONG   ReportDescriptorSize;
    BOOLEAN DescriptorSet;

    /* HID device descriptor (wraps ReportDescriptorSize) */
    HID_DESCRIPTOR          HidDescriptor;
    HID_DEVICE_ATTRIBUTES   HidDeviceAttributes;

    /* Input report byte length (Report ID + data, from HID caps) */
    ULONG   InputReportByteLength;

    /* First Input Report ID from descriptor (0 if no Report IDs) */
    UCHAR   FirstInputReportId;

    /* Latest raw input report for HID READ_REPORT (native descriptor format) */
    UCHAR   InputReport[HIDMAESTRO_MAX_REPORT_SIZE];
    ULONG   InputReportSize;
    BOOLEAN InputReportReady;

    /* Latest raw GIP data for XUSB GET_STATE (always 14 bytes GIP format) */
    UCHAR   XusbReport[14];
    BOOLEAN XusbReportReady;

    /* Product string (returned on IOCTL_HID_GET_STRING / HID_STRING_ID_IPRODUCT) */
    WCHAR   ProductString[128];
    ULONG   ProductStringBytes; /* size in bytes including null terminator */

    /* Serial number string (returned on IOCTL_HID_GET_STRING / HID_STRING_ID_ISERIALNUMBER).
     * Built per-instance from ControllerIndex so SDL3/HIDAPI/PadForge can
     * disambiguate two virtual controllers that share VID/PID/ProductString
     * (e.g. 2× DualSense). Without a unique serial, hid_enumerate buckets
     * them as one device. */
    WCHAR   SerialString[64];
    ULONG   SerialStringBytes;

    /* Latest raw output report (received from HID class) */
    UCHAR   OutputReport[HIDMAESTRO_MAX_REPORT_SIZE];
    ULONG   OutputReportSize;
    BOOLEAN OutputReportReady;

    /* Queues */
    WDFQUEUE    DefaultQueue;        /* Parallel — HID IOCTLs + our custom IOCTLs */
    WDFQUEUE    ManualQueue;         /* Manual — pending IOCTL_HID_READ_REPORT */

    /* Synchronization */
    WDFWAITLOCK InputLock;
    WDFWAITLOCK OutputLock;

    /* Shared memory data injection (bypasses upper filter drivers) */
    HANDLE  SharedMemHandle;     /* CreateFileMapping handle */
    PVOID   SharedMemPtr;        /* MapViewOfFile pointer */
    ULONG   SharedMemSeqNo;      /* Last sequence number we processed */

    /* Sequence-number gate for IOCTL_HID_READ_REPORT. The READ_REPORT
     * handler returns a cached input report immediately ONLY when there's
     * new data since the last delivery (SharedMemSeqNo > LastDeliveredSeqNo).
     * Otherwise the request is parked in ManualQueue and the worker thread
     * completes it on the next ProcessSharedInput tick. Without this gate,
     * HIDClass hammers READ_REPORT in a tight loop because every call
     * returns instantly with stale data — the original CPU saturation
     * culprit. */
    ULONG   LastDeliveredInputSeqNo;

    /* Event-driven IPC: SDK signals InputDataEvent after every seqlock write,
     * the worker thread (WorkerThread) waits on (StopEvent, InputDataEvent)
     * and processes frames. Replaces the old 1ms WdfTimer busy-poll which
     * saturated CPU cores at scale. A 50 ms safety timeout on WaitForMultiple
     * Objects keeps things moving if a signal is ever dropped. */
    HANDLE  InputDataEvent;      /* OpenEvent on Global\HIDMaestroInputEvent<N> */
    HANDLE  StopEvent;            /* Named: Global\HIDMaestroStopEvent<N> */
    HANDLE  WorkerThread;        /* CreateThread handle */
    WCHAR   InputEventName[64];  /* e.g. L"Global\\HIDMaestroInputEvent0" */
    WCHAR   StopEventName[64];   /* e.g. L"Global\\HIDMaestroStopEvent0" */

    /* Multi-instance: controller index (0, 1, 2, 3) */
    ULONG   ControllerIndex;
    WCHAR   ConfigRegPath[64];      /* e.g. L"SOFTWARE\\HIDMaestro\\Controller0" */
    WCHAR   SharedMappingName[64];  /* e.g. L"Global\\HIDMaestroInput0" */
    WCHAR   OutputMappingName[64];  /* e.g. L"Global\\HIDMaestroOutput0" */

    /* Output channel — host→device pass-through (rumble, haptics, FFB, LED).
     * Driver/companion are dumb pass-throughs; the consumer (PadForge or test
     * app) opens this section read-only and decodes by (Source, ReportId). */
    HANDLE  OutputMemHandle;
    PVOID   OutputMemPtr;
    ULONG   OutputSeqNoLocal;       /* Last value we wrote (so we always increment) */
    ULONG   OutputWriteCount;       /* Stale-detection: writes since last re-open (#2) */

    /* PID FFB state channel — consumer→driver state for HidD_GetFeature
     * responses on the canonical PID Report IDs (0x12 Block Load, 0x13
     * Pool, 0x14 State). Driver reads via seqlock on IOCTL_UMDF_HID_GET_
     * FEATURE; consumer writes via HMController.PublishPid* methods.
     * Lazy-mapped: NULL until the SDK creates Global\HIDMaestroPidState<N>. */
    HANDLE  PidStateMemHandle;
    PVOID   PidStateMemPtr;
    WCHAR   PidStateMappingName[64]; /* e.g. L"Global\\HIDMaestroPidState0" */

    /* Diagnostics */
    LONG    InputReportsSubmitted;
    LONG    OutputReportsReceived;

} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

/* Shared memory layout: written by user-mode, read by driver
 * Contains BOTH native HID report data AND GIP data for XUSB GET_STATE.
 * Total size: 4 + 4 + 256 + 14 = 278 bytes.
 *
 * Data[] is 256 bytes to cover every HID input report size in the profile
 * database without truncation. DualSense BT report 0x31 is 78 bytes;
 * Switch Pro standard input reports can run to ~64; gyro/accelerometer
 * passthrough on these pads writes motion values LATE in the report, so
 * the prior 64-byte pipe clipped exactly the motion fields consumers
 * (Dolphin, Cemu, yuzu/Citron, RetroArch) need. 256 matches the mirror
 * in SHARED_OUTPUT and gives headroom for custom profile descriptors. */
#pragma pack(push, 1)
typedef struct _HIDMAESTRO_SHARED_INPUT {
    volatile ULONG  SeqNo;           /* Incremented each write */
    ULONG           DataSize;        /* HID input report data size (excluding Report ID) */
    UCHAR           Data[256];       /* HID input report data (native descriptor format) */
    UCHAR           GipData[14];     /* GIP-format data for XUSB GET_STATE (always 14 bytes) */
} HIDMAESTRO_SHARED_INPUT, *PHIDMAESTRO_SHARED_INPUT;
#pragma pack(pop)

/* Output passthrough section: written by driver/companion, read by consumer.
 * Game sends rumble/haptics/FFB → we capture bytes and publish here.
 * Latest-wins (no ring): old commands are stale and shouldn't be replayed.
 * Total size: 4 + 1 + 1 + 2 + 256 = 264 bytes
 *
 * Source enum tells the consumer which API the game used:
 *   0 = HID output report  (HidD_SetOutputReport, dinput8 PID, HIDAPI write)
 *   1 = HID feature report (HidD_SetFeature)
 *   2 = XInput rumble      (XInputSetState — companion-side)
 *
 * For Source=HID*, ReportId is the HID Report ID byte (0 if none).
 * For Source=XInputRumble, ReportId is reserved (0); Data is the 5-byte
 *   XINPUT_VIBRATION-style payload from the IOCTL_XUSB_SET_STATE input buffer.
 *
 * Consumer is expected to interpret bytes per (profile, Source, ReportId).
 * The driver does NOT classify rumble vs haptic vs adaptive trigger — that
 * distinction is semantic and lives in the consumer. */
#define HIDMAESTRO_OUTPUT_SOURCE_HID_OUTPUT   0
#define HIDMAESTRO_OUTPUT_SOURCE_HID_FEATURE  1
#define HIDMAESTRO_OUTPUT_SOURCE_XINPUT       2

#pragma pack(push, 1)
typedef struct _HIDMAESTRO_SHARED_OUTPUT {
    volatile ULONG  SeqNo;           /* Incremented each write */
    UCHAR           Source;          /* HIDMAESTRO_OUTPUT_SOURCE_* */
    UCHAR           ReportId;        /* HID Report ID (0 if descriptor uses no IDs) */
    USHORT          DataSize;        /* Bytes valid in Data[] */
    UCHAR           Data[256];       /* Raw output payload */
} HIDMAESTRO_SHARED_OUTPUT, *PHIDMAESTRO_SHARED_OUTPUT;
#pragma pack(pop)

/* PID Force-Feedback state section: written by SDK consumer
 * (HMController.PublishPid*), read by driver on IOCTL_UMDF_HID_GET_FEATURE.
 * Single-slot last-write-wins per the HID PID 1.0 spec (Block Load Report
 * is "the result of the most recent Create New Effect"). Mirrors vJoy's
 * device-extension PID storage shape, but on the user side of the shared
 * mapping because HIDMaestro is UMDF2 (driver runs in user-mode WUDFHost).
 *
 * PidEnabled gate: zero-initialized when the SDK creates the section.
 * First call to HMController.PublishPidPool flips it to 1, atomic with
 * the Pool fields write under the same seqlock cycle. Driver checks
 * PidEnabled before reading any other field — when 0, returns
 * STATUS_NO_SUCH_DEVICE for Pool (matches vJoy's "FFB not enabled"
 * convention) and STATUS_NOT_SUPPORTED for Block Load / State.
 *
 * Wire layout matches the PID 1.0 report formats so the driver can copy
 * fields straight into the IOCTL output buffer with minimal packing. */
#pragma pack(push, 1)
typedef struct _HIDMAESTRO_SHARED_PID_STATE {
    volatile ULONG  SeqNo;           /* Incremented each write — seqlock */
    UCHAR           PidEnabled;      /* 0 until first PublishPidPool */
    UCHAR           _pad0[3];

    /* Block Load Report (Report ID 0x12, per HID PID 1.0 §5.5) */
    UCHAR           BL_EffectBlockIndex;
    UCHAR           BL_LoadStatus;        /* 1=Success, 2=Full, 3=Error */
    USHORT          BL_RAMPoolAvailable;

    /* PID Pool Report (Report ID 0x13, per HID PID 1.0 §5.7) */
    USHORT          Pool_RAMPoolSize;
    UCHAR           Pool_MaxSimultaneousEffects;
    UCHAR           Pool_MemoryManagement;  /* bit0=DeviceManagedPool, bit1=SharedParamBlocks */

    /* PID State Report (Report ID 0x14, per HID PID 1.0 §5.8) */
    UCHAR           State_EffectBlockIndex;
    UCHAR           State_Flags;
    UCHAR           _pad1[2];
} HIDMAESTRO_SHARED_PID_STATE, *PHIDMAESTRO_SHARED_PID_STATE;
#pragma pack(pop)

/* Default PID Report IDs per the canonical HID PID 1.0 descriptor.
 * Profiles may use different IDs; the driver falls back to STATUS_NOT_SUPPORTED
 * for unknown IDs, preserving today's behavior. */
#define HIDMAESTRO_PID_BLOCK_LOAD_REPORT_ID  0x12
#define HIDMAESTRO_PID_POOL_REPORT_ID        0x13
#define HIDMAESTRO_PID_STATE_REPORT_ID       0x14

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext)

/* ------------------------------------------------------------------ */
/*  Default HID descriptor (bare minimum until user-mode sets one)     */
/* ------------------------------------------------------------------ */

/* Xbox 360 gamepad descriptor: 6 axes (16-bit), 10 buttons, 1 hat */
static const UCHAR G_DefaultReportDescriptor[] = {
    0x05, 0x01,                     /* Usage Page (Generic Desktop) */
    0x09, 0x05,                     /* Usage (Game Pad) */
    0xA1, 0x01,                     /* Collection (Application) */
    0x85, 0x01,                     /*   Report ID (1) */
    /* Left stick X, Y */
    0x09, 0x30,                     /*   Usage (X) */
    0x09, 0x31,                     /*   Usage (Y) */
    0x15, 0x00,                     /*   Logical Minimum (0) */
    0x27, 0xFF, 0xFF, 0x00, 0x00,   /*   Logical Maximum (65535) */
    0x75, 0x10,                     /*   Report Size (16) */
    0x95, 0x02,                     /*   Report Count (2) */
    0x81, 0x02,                     /*   INPUT (Data, Var, Abs) */
    /* Right stick X, Y */
    0x09, 0x33,                     /*   Usage (Rx) */
    0x09, 0x34,                     /*   Usage (Ry) */
    0x81, 0x02,                     /*   INPUT (Data, Var, Abs) */
    /* Triggers */
    0x09, 0x32,                     /*   Usage (Z) */
    0x09, 0x35,                     /*   Usage (Rz) */
    0x81, 0x02,                     /*   INPUT (Data, Var, Abs) */
    /* Buttons 1-10 */
    0x05, 0x09,                     /*   Usage Page (Button) */
    0x19, 0x01,                     /*   Usage Minimum (1) */
    0x29, 0x0A,                     /*   Usage Maximum (10) */
    0x15, 0x00,                     /*   Logical Minimum (0) */
    0x25, 0x01,                     /*   Logical Maximum (1) */
    0x75, 0x01,                     /*   Report Size (1) */
    0x95, 0x0A,                     /*   Report Count (10) */
    0x81, 0x02,                     /*   INPUT (Data, Var, Abs) */
    /* 6-bit padding */
    0x75, 0x06,                     /*   Report Size (6) */
    0x95, 0x01,                     /*   Report Count (1) */
    0x81, 0x01,                     /*   INPUT (Cnst) */
    /* Hat switch */
    0x05, 0x01,                     /*   Usage Page (Generic Desktop) */
    0x09, 0x39,                     /*   Usage (Hat switch) */
    0x15, 0x01,                     /*   Logical Minimum (1) */
    0x25, 0x08,                     /*   Logical Maximum (8) */
    0x35, 0x00,                     /*   Physical Minimum (0) */
    0x46, 0x3B, 0x01,               /*   Physical Maximum (315) */
    0x66, 0x14, 0x00,               /*   Unit (Degrees) */
    0x75, 0x04,                     /*   Report Size (4) */
    0x95, 0x01,                     /*   Report Count (1) */
    0x81, 0x42,                     /*   INPUT (Data, Var, Abs, Null) */
    /* 4-bit padding */
    0x75, 0x04, 0x95, 0x01,
    0x15, 0x00, 0x25, 0x00,
    0x35, 0x00, 0x45, 0x00, 0x65, 0x00,
    0x81, 0x03,                     /*   INPUT (Cnst, Var) */
    /* Feature report for user-mode → driver data channel */
    0x85, 0x02,                     /*   Report ID (2) */
    0x06, 0x00, 0xFF,               /*   Usage Page (Vendor Defined) */
    0x09, 0x01,                     /*   Usage (0x01) */
    0x15, 0x00,                     /*   Logical Minimum (0) */
    0x26, 0xFF, 0x00,               /*   Logical Maximum (255) */
    0x75, 0x08,                     /*   Report Size (8) */
    0x95, 0x0E,                     /*   Report Count (14) */
    0xB1, 0x02,                     /*   FEATURE (Data, Var, Abs) */
    0xC0                            /* End Collection */
};

/* ------------------------------------------------------------------ */
/*  Function prototypes                                                */
/* ------------------------------------------------------------------ */

DRIVER_INITIALIZE                   DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD           EvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL  EvtIoDeviceControl;

#endif /* DRIVER_H */
