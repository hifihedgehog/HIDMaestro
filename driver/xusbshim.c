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
 * is preserved end-to-end. All IOCTLs pass through unmodified.
 *
 * Experimental — part of v1-dev-experiment-xusb-child-pdo. The hypothesis
 * being tested: does WGI Gamepad.Vibration dispatch trigger when the HID
 * child exposes the XUSB interface class, independent of xinputhid being
 * the function/upper-filter?
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
    NTSTATUS           status;
    WDFDEVICE          device;
    WDF_IO_QUEUE_CONFIG queueConfig;
    UNREFERENCED_PARAMETER(Driver);

    WdfFdoInitSetFilter(DeviceInit);

    status = WdfDeviceCreate(&DeviceInit, WDF_NO_OBJECT_ATTRIBUTES, &device);
    if (!NT_SUCCESS(status)) return status;

    /* Register the XUSB interface class. WGI's Gamepad.Vibration dispatch
     * is hypothesized to gate on this interface being present on the HID
     * child. */
    (void)WdfDeviceCreateDeviceInterface(
        device, (LPGUID)&GUID_DEVINTERFACE_XUSB, NULL);

    /* Pass-through queue: default IOCTL handler forwards everything to
     * the parent (HidClass / mshidumdf / our ROOT driver.c).
     *
     * NOTE: if WGI opens the XUSB interface and sends IOCTL_XUSB_SET_STATE
     * (0x8000A010), the lower HidClass stack will reject it. A follow-up
     * iteration needs to translate XUSB IOCTLs into HID WRITE_REPORT. For
     * this first cut we observe whether the interface registration alone
     * is sufficient to unlock Gamepad class promotion. */
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDefault = XusbShimIoDefault;

    return WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, NULL);
}

VOID
XusbShimIoDefault(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request)
{
    WDFDEVICE                device = WdfIoQueueGetDevice(Queue);
    WDF_REQUEST_SEND_OPTIONS options;
    WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
    if (!WdfRequestSend(Request, WdfDeviceGetIoTarget(device), &options)) {
        WdfRequestComplete(Request, WdfRequestGetStatus(Request));
    }
}
