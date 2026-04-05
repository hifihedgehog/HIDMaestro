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
    /* Log that filter loaded */
    {
        HANDLE h = CreateFileW(L"C:\ProgramData\HIDMaestro\btnfix_log.txt",
            FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, 0, NULL);
        if (h != INVALID_HANDLE_VALUE) {
            char msg[] = "BtnFix DeviceAdd OK\r\n";
            DWORD d; WriteFile(h, msg, sizeof(msg)-1, &d, NULL); CloseHandle(h);
        }
    }
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = BtnFixIoControl;
    return WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, NULL);
}

void BtnFixIoControl(
    _In_ WDFQUEUE Queue, _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength, _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
    WDFDEVICE device = WdfIoQueueGetDevice(Queue);
    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    /* Log first 10 IOCTLs */
    {
        static LONG count = 0;
        if (InterlockedIncrement(&count) <= 10) {
            HANDLE h = CreateFileW(L"C:\ProgramData\HIDMaestro\btnfix_log.txt",
                FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, 0, NULL);
            if (h != INVALID_HANDLE_VALUE) {
                char msg[64]; char *p = msg;
                *p++='I';*p++='O';*p++=' ';
                for (int k = 7; k >= 0; k--) { int n = (IoControlCode >> (k*4)) & 0xF; *p++ = (char)(n<10?'0'+n:'A'+n-10); }
                *p++='\r';*p++='\n';
                DWORD d; WriteFile(h, msg, (int)(p-msg), &d, NULL); CloseHandle(h);
            }
        }
    }

    if (IoControlCode == IOCTL_HID_GET_REPORT_DESCRIPTOR) {
        WDF_REQUEST_SEND_OPTIONS options;
        WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_SYNCHRONOUS);
        options.Timeout = WDF_REL_TIMEOUT_IN_SEC(5);
        if (WdfRequestSend(Request, WdfDeviceGetIoTarget(device), &options)) {
            NTSTATUS status = WdfRequestGetStatus(Request);
            if (NT_SUCCESS(status)) {
                PVOID outBuf; size_t outLen;
                if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, 1, &outBuf, &outLen))) {
                    /* Patch buttons: 05 09 ... 29 10 → 29 0A, 95 10 → 95 0A */
                    PUCHAR d = (PUCHAR)outBuf;
                    for (ULONG i = 0; i + 3 < (ULONG)outLen; i++) {
                        if (d[i] == 0x05 && d[i+1] == 0x09) {
                            for (ULONG j = i+2; j+1 < (ULONG)outLen && j < i+30; j++) {
                                if (d[j] == 0x29 && d[j+1] == 0x10) {
                                    d[j+1] = 0x0A;
                                    for (ULONG k = j+2; k+1 < (ULONG)outLen && k < j+15; k++) {
                                        if (d[k] == 0x95 && d[k+1] == 0x10) { d[k+1] = 0x0A; break; }
                                    }
                                    break;
                                }
                            }
                            break;
                        }
                    }
                    /* Log patch */
                    HANDLE h = CreateFileW(L"C:\ProgramData\HIDMaestro\btnfix_log.txt",
                        FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, 0, NULL);
                    if (h != INVALID_HANDLE_VALUE) {
                        char msg[] = "PATCHED descriptor\r\n";
                        DWORD dum; WriteFile(h, msg, sizeof(msg)-1, &dum, NULL); CloseHandle(h);
                    }
                }
            }
            WdfRequestComplete(Request, status);
        } else {
            WdfRequestComplete(Request, WdfRequestGetStatus(Request));
        }
    } else {
        WDF_REQUEST_SEND_OPTIONS options;
        WDF_REQUEST_SEND_OPTIONS_INIT(&options, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
        if (!WdfRequestSend(Request, WdfDeviceGetIoTarget(device), &options)) {
            WdfRequestComplete(Request, WdfRequestGetStatus(Request));
        }
    }
}
