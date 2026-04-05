/*
 * HIDMaestro Button Fix Filter — UMDF2 upper filter on xinputhid HID child.
 * Intercepts IOCTL_HID_GET_REPORT_DESCRIPTOR and patches Usage Maximum from 16 to 10.
 * All other IOCTLs pass through unchanged.
 */
#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <wdf.h>

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD BtnFixDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL BtnFixIoControl;

#define IOCTL_HID_GET_REPORT_DESCRIPTOR 0xB0192

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, BtnFixDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS BtnFixDeviceAdd(_In_ WDFDRIVER Driver, _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    NTSTATUS status;
    WDFDEVICE device;
    WDF_IO_QUEUE_CONFIG queueConfig;

    UNREFERENCED_PARAMETER(Driver);
    WdfFdoInitSetFilter(DeviceInit);

    status = WdfDeviceCreate(&DeviceInit, WDF_NO_OBJECT_ATTRIBUTES, &device);
    if (!NT_SUCCESS(status)) return status;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = BtnFixIoControl;
    return WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, NULL);
}

static void PatchDescriptor(PUCHAR desc, ULONG len)
{
    /* Find Usage Maximum (16) in Button usage page and change to 10.
     * HID descriptor pattern: 29 10 (Usage Maximum 16) → 29 0A (Usage Maximum 10)
     * Also: 95 10 (Report Count 16) → 95 0A (Report Count 10) — but only for buttons

     * We look for the sequence: 09 01..29 10 within Button page (05 09) */
    for (ULONG i = 0; i + 3 < len; i++) {
        /* Find Usage Page (Button) = 05 09 */
        if (desc[i] == 0x05 && desc[i+1] == 0x09) {
            /* Search forward for Usage Maximum = 29 xx */
            for (ULONG j = i + 2; j + 1 < len && j < i + 20; j++) {
                if (desc[j] == 0x29 && desc[j+1] == 0x10) {
                    desc[j+1] = 0x0A; /* Usage Maximum = 10 */
                    /* Find Report Count nearby */
                    for (ULONG k = j + 2; k + 1 < len && k < j + 10; k++) {
                        if (desc[k] == 0x95 && desc[k+1] == 0x10) {
                            desc[k+1] = 0x0A; /* Report Count = 10 */
                            return;
                        }
                    }
                    return;
                }
            }
        }
    }
}

void BtnFixIoControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
    UNREFERENCED_PARAMETER(Queue);
    UNREFERENCED_PARAMETER(InputBufferLength);

    if (IoControlCode == IOCTL_HID_GET_REPORT_DESCRIPTOR) {
        /* Forward the request, then patch the result */
        WDF_REQUEST_SEND_OPTIONS options;
        WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);

        /* Can't easily modify after send-and-forget. Use synchronous send instead. */
        WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_SYNCHRONOUS);
        options.Timeout = WDF_REL_TIMEOUT_IN_SEC(5);

        BOOLEAN sent = WdfRequestSend(Request, WdfDeviceGetIoTarget(WdfIoQueueGetDevice(Queue)), &options);
        if (sent) {
            NTSTATUS status = WdfRequestGetStatus(Request);
            if (NT_SUCCESS(status)) {
                PVOID outBuf; size_t outLen;
                if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, 1, &outBuf, &outLen))) {
                    PatchDescriptor((PUCHAR)outBuf, (ULONG)outLen);
                }
            }
            WdfRequestComplete(Request, status);
        } else {
            WdfRequestComplete(Request, WdfRequestGetStatus(Request));
        }
    } else {
        /* Pass through */
        WDF_REQUEST_SEND_OPTIONS options;
        WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
        if (!WdfRequestSend(Request, WdfDeviceGetIoTarget(WdfIoQueueGetDevice(Queue)), &options)) {
            WdfRequestComplete(Request, WdfRequestGetStatus(Request));
        }
    }
}
