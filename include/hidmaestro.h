/*
 * HIDMaestro — Shared header for driver and user-mode clients.
 *
 * Defines the IOCTLs and structures used to communicate between a
 * user-mode application (e.g., PadForge) and the HIDMaestro virtual
 * HID minidriver.
 *
 * The driver is a thin pipe: user-mode supplies a raw HID descriptor
 * and raw input reports, and the driver passes them to the HID class
 * driver unchanged. Output reports from games flow back to user-mode
 * the same way.
 */

#ifndef HIDMAESTRO_H
#define HIDMAESTRO_H

#include <initguid.h>
#include <devioctl.h>

/* ------------------------------------------------------------------ */
/*  Device interface GUID                                              */
/*  User-mode opens this interface to send/receive raw HID reports.    */
/* ------------------------------------------------------------------ */

// {A3F2C6D8-7B4E-4F1A-9E5D-8C3B2A1F0E7D}
DEFINE_GUID(GUID_DEVINTERFACE_HIDMAESTRO,
    0xa3f2c6d8, 0x7b4e, 0x4f1a,
    0x9e, 0x5d, 0x8c, 0x3b, 0x2a, 0x1f, 0x0e, 0x7d);

/* ------------------------------------------------------------------ */
/*  IOCTL codes                                                        */
/*                                                                     */
/*  These are sent by user-mode to the HIDMaestro device interface.    */
/*  They are distinct from the HID_* IOCTLs that the HID class driver  */
/*  sends internally.                                                  */
/* ------------------------------------------------------------------ */

#define FILE_DEVICE_HIDMAESTRO  0x8000

/*
 * IOCTL_HIDMAESTRO_SET_DESCRIPTOR
 *
 * Sets the HID report descriptor for this virtual device.
 * Must be called before the device is active (before first input report).
 * The descriptor is returned verbatim on IOCTL_HID_GET_REPORT_DESCRIPTOR.
 *
 * Input:  Raw HID report descriptor bytes (max 4096 bytes)
 * Output: None
 */
#define IOCTL_HIDMAESTRO_SET_DESCRIPTOR \
    CTL_CODE(FILE_DEVICE_HIDMAESTRO, 0x800, METHOD_BUFFERED, FILE_WRITE_ACCESS)

/*
 * IOCTL_HIDMAESTRO_SET_DEVICE_INFO
 *
 * Sets the VID, PID, version number, and product string.
 * Must be called before the device is active.
 *
 * Input:  HIDMAESTRO_DEVICE_INFO structure
 * Output: None
 */
#define IOCTL_HIDMAESTRO_SET_DEVICE_INFO \
    CTL_CODE(FILE_DEVICE_HIDMAESTRO, 0x801, METHOD_BUFFERED, FILE_WRITE_ACCESS)

/*
 * IOCTL_HIDMAESTRO_SUBMIT_INPUT_REPORT
 *
 * Submits a raw HID input report. The bytes are copied directly to
 * the pending IOCTL_HID_READ_REPORT request — no translation, no
 * field mapping, just memcpy.
 *
 * Input:  Raw input report bytes (must match the descriptor's format)
 * Output: None
 */
#define IOCTL_HIDMAESTRO_SUBMIT_INPUT_REPORT \
    CTL_CODE(FILE_DEVICE_HIDMAESTRO, 0x802, METHOD_BUFFERED, FILE_WRITE_ACCESS)

/*
 * IOCTL_HIDMAESTRO_READ_OUTPUT_REPORT
 *
 * Reads the next output report written by a game/application via the
 * HID class driver. Blocks until an output report is available.
 * The raw bytes are returned exactly as the game wrote them.
 *
 * Input:  None
 * Output: Raw output report bytes
 */
#define IOCTL_HIDMAESTRO_READ_OUTPUT_REPORT \
    CTL_CODE(FILE_DEVICE_HIDMAESTRO, 0x803, METHOD_BUFFERED, FILE_READ_ACCESS)

/*
 * IOCTL_HIDMAESTRO_GET_STATUS
 *
 * Returns the current device status (active, descriptor set, etc.).
 *
 * Input:  None
 * Output: HIDMAESTRO_STATUS structure
 */
#define IOCTL_HIDMAESTRO_GET_STATUS \
    CTL_CODE(FILE_DEVICE_HIDMAESTRO, 0x804, METHOD_BUFFERED, FILE_READ_ACCESS)

/* ------------------------------------------------------------------ */
/*  Structures                                                         */
/* ------------------------------------------------------------------ */

#pragma pack(push, 1)

typedef struct _HIDMAESTRO_DEVICE_INFO {
    USHORT VendorId;
    USHORT ProductId;
    USHORT VersionNumber;
} HIDMAESTRO_DEVICE_INFO, *PHIDMAESTRO_DEVICE_INFO;

typedef struct _HIDMAESTRO_STATUS {
    BOOLEAN DescriptorSet;          /* TRUE if SET_DESCRIPTOR was called */
    BOOLEAN DeviceInfoSet;          /* TRUE if SET_DEVICE_INFO was called */
    BOOLEAN Active;                 /* TRUE if device is active (has HID children) */
    ULONG   DescriptorSize;         /* Current descriptor size in bytes */
    USHORT  VendorId;
    USHORT  ProductId;
    ULONG   InputReportsSubmitted;  /* Lifetime counter */
    ULONG   OutputReportsReceived;  /* Lifetime counter */
} HIDMAESTRO_STATUS, *PHIDMAESTRO_STATUS;

#pragma pack(pop)

/* ------------------------------------------------------------------ */
/*  Limits                                                             */
/* ------------------------------------------------------------------ */

#define HIDMAESTRO_MAX_DESCRIPTOR_SIZE  4096
#define HIDMAESTRO_MAX_REPORT_SIZE      1024

#endif /* HIDMAESTRO_H */
