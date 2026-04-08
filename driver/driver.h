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

#include "../include/hidmaestro.h"

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

    /* Product string (returned on IOCTL_HID_GET_STRING) */
    WCHAR   ProductString[128];
    ULONG   ProductStringBytes; /* size in bytes including null terminator */

    /* Latest raw output report (received from HID class) */
    UCHAR   OutputReport[HIDMAESTRO_MAX_REPORT_SIZE];
    ULONG   OutputReportSize;
    BOOLEAN OutputReportReady;

    /* Queues */
    WDFQUEUE    DefaultQueue;        /* Parallel — HID IOCTLs + our custom IOCTLs */
    WDFQUEUE    ManualQueue;         /* Manual — pending IOCTL_HID_READ_REPORT */
    WDFQUEUE    OutputReportQueue;   /* Manual — pending user-mode output reads */

    /* Synchronization */
    WDFWAITLOCK InputLock;
    WDFWAITLOCK OutputLock;

    /* Shared memory data injection (bypasses upper filter drivers) */
    HANDLE  SharedMemHandle;     /* CreateFileMapping handle */
    PVOID   SharedMemPtr;        /* MapViewOfFile pointer */
    ULONG   SharedMemSeqNo;      /* Last sequence number we processed */
    WDFTIMER PollTimer;          /* Periodic timer to check shared memory */

    /* XInput state file — pre-built XInput state from user-mode */
    HANDLE  XInputFileHandle;
    PVOID   XInputFilePtr;
    ULONG   XInputSeqNo;

    /* Multi-instance: controller index (0, 1, 2, 3) */
    ULONG   ControllerIndex;
    WCHAR   ConfigRegPath[64];      /* e.g. L"SOFTWARE\\HIDMaestro\\Controller0" */
    WCHAR   SharedMappingName[64];  /* e.g. L"Global\\HIDMaestroInput0" */

    /* Diagnostics */
    LONG    InputReportsSubmitted;
    LONG    OutputReportsReceived;

} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

/* Shared memory layout: written by user-mode, read by driver
 * Contains BOTH native HID report data AND GIP data for XUSB GET_STATE.
 * Total size: 4 + 4 + 64 + 14 = 86 bytes */
#pragma pack(push, 1)
typedef struct _HIDMAESTRO_SHARED_INPUT {
    volatile ULONG  SeqNo;           /* Incremented each write */
    ULONG           DataSize;        /* HID input report data size (excluding Report ID) */
    UCHAR           Data[64];        /* HID input report data (native descriptor format) */
    UCHAR           GipData[14];     /* GIP-format data for XUSB GET_STATE (always 14 bytes) */
} HIDMAESTRO_SHARED_INPUT, *PHIDMAESTRO_SHARED_INPUT;
#pragma pack(pop)

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
