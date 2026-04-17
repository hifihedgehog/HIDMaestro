/*
 * HIDMaestro — XUSB Interface Shim (UMDF2 HID upper filter)
 *
 * Purpose: on any HID gamepad child under our ROOT device, register the
 * XUSB interface class {EC87F1E3-C13B-4100-B5F7-8B84D54260CB}. That is
 * the interface xinputhid registers on real Xbox HID children and is the
 * strongest candidate signal for WGI's Gamepad-class promotion gate.
 *
 * Critically, this filter does NOT modify the HID report descriptor. Our
 * original profile-specified descriptor (10 buttons on Xbox 360, etc.)
 * is preserved end-to-end.
 *
 * Diagnostic logging: every IOCTL seen is appended to
 *   C:\ProgramData\HIDMaestro\xusbshim_log.txt
 * so the next test iteration can read the empirical set of IOCTLs WGI /
 * xinput1_4 sends via the XUSB interface (if any). The log is cheap — a
 * bounded count (first ~200 IOCTLs) protects against runaway file growth.
 *
 * Experimental — part of v1-dev-experiment-xusb-child-pdo. If interface
 * registration alone does NOT flip WGI Gamepad.Vibration routing, the
 * next iteration extends this filter to translate IOCTL_XUSB_SET_STATE
 * (0x8000A010) into HID WRITE_REPORT (Report ID 0x0F) so our ROOT
 * driver.c's PublishOutput path receives vibration events.
 */
#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <wdf.h>

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD XusbShimDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEFAULT XusbShimIoDefault;

/* XUSB interface class — same GUID xinputhid registers on real Xbox HID
 * children. Defined here rather than pulled from a header because this
 * file compiles standalone. */
static const GUID GUID_DEVINTERFACE_XUSB = {
    0xEC87F1E3, 0xC13B, 0x4100,
    { 0xB5, 0xF7, 0x8B, 0x84, 0xD5, 0x42, 0x60, 0xCB }
};

/* ---- Diagnostic log helpers ---------------------------------------- */

static VOID
LogLine(_In_z_ const char *msg, _In_ SIZE_T len)
{
    HANDLE h = CreateFileW(
        L"C:\\ProgramData\\HIDMaestro\\xusbshim_log.txt",
        FILE_APPEND_DATA, FILE_SHARE_READ, NULL,
        OPEN_ALWAYS, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD written;
    WriteFile(h, msg, (DWORD)len, &written, NULL);
    CloseHandle(h);
}

static VOID
LogEvent(_In_z_ const char *tag, _In_ ULONG value)
{
    /* Bounded: only the first ~200 events across all device instances.
     * Protects against runaway log growth if WGI polls IOCTL_GET_STATE. */
    static volatile LONG counter = 0;
    if (InterlockedIncrement(&counter) > 200) return;

    char buf[64];
    char *p = buf;
    while (*tag) *p++ = *tag++;
    *p++ = ' ';
    /* Hex-format value, 8 digits, no leading 0x (compact). */
    for (int shift = 28; shift >= 0; shift -= 4) {
        int nibble = (value >> shift) & 0xF;
        *p++ = (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
    }
    *p++ = '\r';
    *p++ = '\n';
    LogLine(buf, (SIZE_T)(p - buf));
}

/* ---- Driver entry / device-add ------------------------------------- */

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, XusbShimDeviceAdd);
    return WdfDriverCreate(
        DriverObject, RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS
XusbShimDeviceAdd(
    _In_    WDFDRIVER       Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    NTSTATUS            status;
    WDFDEVICE           device;
    WDF_IO_QUEUE_CONFIG queueConfig;
    UNREFERENCED_PARAMETER(Driver);

    WdfFdoInitSetFilter(DeviceInit);

    status = WdfDeviceCreate(&DeviceInit, WDF_NO_OBJECT_ATTRIBUTES, &device);
    if (!NT_SUCCESS(status)) return status;

    /* Ensure log directory exists. Harmless if it already does. */
    CreateDirectoryW(L"C:\\ProgramData\\HIDMaestro", NULL);
    LogEvent("DeviceAdd", 0);

    /* Register the XUSB interface class — the central experiment. */
    NTSTATUS ifs = WdfDeviceCreateDeviceInterface(
        device, (LPGUID)&GUID_DEVINTERFACE_XUSB, NULL);
    LogEvent("XUSB-ifreg", (ULONG)ifs);

    /* Default queue forwards all IOCTLs to the parent while logging them.
     *
     * NOTE: the lower HID stack will reject genuine XUSB IOCTLs (SET_STATE
     * etc.) with STATUS_INVALID_DEVICE_REQUEST. A follow-up iteration will
     * handle the XUSB IOCTL codes locally (synthesizing HID WRITE_REPORT
     * for vibration commands). For this first cut we only observe which
     * IOCTLs WGI sends — the log tells us what to implement. */
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(
        &queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDefault = XusbShimIoDefault;

    return WdfIoQueueCreate(
        device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, NULL);
}

/* XUSB IOCTL codes documented via public reverse engineering of xusb22.sys
 * and xinput1_4.dll. The ones below are the subset WGI / xinput1_4 are
 * most likely to call. Actual format is still to be confirmed empirically
 * — hence we log the first input bytes for SET_STATE when we see it. */
#define IOCTL_XUSB_GET_INFORMATION       0x80006000u  /* GetCapabilities */
#define IOCTL_XUSB_GET_CAPABILITIES      0x8000E000u
#define IOCTL_XUSB_GET_LED_STATE         0x8000E004u
#define IOCTL_XUSB_GET_STATE             0x8000E008u
#define IOCTL_XUSB_SET_STATE             0x8000A010u  /* vibration + LED */
#define IOCTL_XUSB_WAIT_GUIDE_BUTTON     0x8000E00Cu
#define IOCTL_XUSB_GET_BATTERY_INFO      0x8000E018u

static VOID
LogBytes(_In_z_ const char *tag, _In_bytecount_(len) const UCHAR *buf, _In_ SIZE_T len)
{
    if (len == 0) { LogEvent(tag, 0); return; }
    /* Hex-dump up to 32 bytes after the tag. */
    char msg[96];
    char *p = msg;
    while (*tag) *p++ = *tag++;
    *p++ = ' ';
    SIZE_T n = (len > 32) ? 32 : len;
    for (SIZE_T i = 0; i < n; i++) {
        int hi = (buf[i] >> 4) & 0xF;
        int lo = buf[i] & 0xF;
        *p++ = (char)(hi < 10 ? '0' + hi : 'A' + hi - 10);
        *p++ = (char)(lo < 10 ? '0' + lo : 'A' + lo - 10);
        *p++ = ' ';
    }
    *p++ = '\r';
    *p++ = '\n';
    LogLine(msg, (SIZE_T)(p - msg));
}

VOID
XusbShimIoDefault(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request)
{
    WDFDEVICE                device = WdfIoQueueGetDevice(Queue);
    WDF_REQUEST_PARAMETERS   params;
    WDF_REQUEST_PARAMETERS_INIT(&params);
    WdfRequestGetParameters(Request, &params);

    /* Log the IOCTL code if this is a device-control request; log the
     * WDF major function otherwise. This reveals exactly what WGI and
     * xinput1_4 are sending through the XUSB interface. */
    if (params.Type == WdfRequestTypeDeviceControl ||
        params.Type == WdfRequestTypeDeviceControlInternal) {
        ULONG code = params.Parameters.DeviceIoControl.IoControlCode;
        LogEvent(
            params.Type == WdfRequestTypeDeviceControl ? "IOCTL" : "INT-IOCTL",
            code);

        /* For the known XUSB IOCTLs, also dump the input buffer bytes so the
         * exact format (motor offsets, slot byte, etc.) is known empirically
         * before we write the WRITE_REPORT translator. */
        if (code == IOCTL_XUSB_SET_STATE ||
            code == IOCTL_XUSB_GET_STATE ||
            code == IOCTL_XUSB_GET_INFORMATION ||
            code == IOCTL_XUSB_GET_CAPABILITIES) {
            PVOID  buf;
            size_t sz;
            if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, 1, &buf, &sz))) {
                const char *tag =
                    (code == IOCTL_XUSB_SET_STATE)        ? "SET_STATE-in"    :
                    (code == IOCTL_XUSB_GET_STATE)        ? "GET_STATE-in"    :
                    (code == IOCTL_XUSB_GET_INFORMATION)  ? "GET_INFO-in"     :
                                                            "GET_CAPS-in";
                LogBytes(tag, (const UCHAR*)buf, sz);
            }
        }
    } else {
        LogEvent("MJ", (ULONG)params.Type);
    }

    WDF_REQUEST_SEND_OPTIONS opts;
    WDF_REQUEST_SEND_OPTIONS_INIT(&opts, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
    if (!WdfRequestSend(Request, WdfDeviceGetIoTarget(device), &opts)) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}
