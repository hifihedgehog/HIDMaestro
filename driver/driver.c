/*
 * HIDMaestro — UMDF2 Virtual HID Minidriver
 *
 * Architecture:
 *   HidClass.sys → MsHidUmdf.sys (pass-through) → HIDMaestro.dll (lower filter)
 *
 * Configuration via registry (HKLM\SOFTWARE\HIDMaestro):
 *   ReportDescriptor (REG_BINARY) — raw HID descriptor bytes
 *   VendorId (REG_DWORD)
 *   ProductId (REG_DWORD)
 *   VersionNumber (REG_DWORD)
 *
 * Input reports flow via HidD_SetOutputReport() from user-mode → WRITE_REPORT →
 * stored in driver → completed on next READ_REPORT from HID class.
 *
 * Output reports (game → device) flow via WRITE_REPORT with output report ID.
 */

#include "driver.h"

/* Registry path for device configuration */
static const WCHAR CONFIG_REG_PATH[] = L"\\Registry\\Machine\\SOFTWARE\\HIDMaestro";

/* ================================================================== */
/*  Helper: copy bytes to request output buffer                        */
/* ================================================================== */

static NTSTATUS
RequestCopyFromBuffer(
    _In_ WDFREQUEST  Request,
    _In_ PVOID       SourceBuffer,
    _In_ size_t      NumBytes)
{
    NTSTATUS    status;
    WDFMEMORY   memory;
    size_t      outputSize;

    status = WdfRequestRetrieveOutputMemory(Request, &memory);
    if (!NT_SUCCESS(status)) return status;

    WdfMemoryGetBuffer(memory, &outputSize);
    if (outputSize < NumBytes) return STATUS_INVALID_BUFFER_SIZE;

    status = WdfMemoryCopyFromBuffer(memory, 0, SourceBuffer, NumBytes);
    if (!NT_SUCCESS(status)) return status;

    WdfRequestSetInformation(Request, NumBytes);
    return STATUS_SUCCESS;
}

/* ================================================================== */
/*  Registry: read descriptor + VID/PID at device init                 */
/* ================================================================== */

static VOID
ReadConfigFromRegistry(
    _In_ PDEVICE_CONTEXT ctx)
{
    NTSTATUS        status;
    WDFKEY          key = NULL;
    UNICODE_STRING  keyPath;
    UNICODE_STRING  valueName;
    ULONG           dwordVal;

    RtlInitUnicodeString(&keyPath, CONFIG_REG_PATH);

    status = WdfRegistryOpenKey(NULL, &keyPath, KEY_READ, WDF_NO_OBJECT_ATTRIBUTES, &key);
    if (!NT_SUCCESS(status)) {
        return; /* No config key — use defaults */
    }

    /* Read ReportDescriptor (REG_BINARY) */
    RtlInitUnicodeString(&valueName, L"ReportDescriptor");
    {
        WDFMEMORY memory = NULL;
        status = WdfRegistryQueryMemory(key, &valueName, PagedPool,
            WDF_NO_OBJECT_ATTRIBUTES, &memory, NULL);
        if (NT_SUCCESS(status) && memory != NULL) {
            size_t bufSize;
            PVOID buf = WdfMemoryGetBuffer(memory, &bufSize);
            if (buf && bufSize > 0 && bufSize <= HIDMAESTRO_MAX_DESCRIPTOR_SIZE) {
                RtlCopyMemory(ctx->ReportDescriptor, buf, bufSize);
                ctx->ReportDescriptorSize = (ULONG)bufSize;
                ctx->HidDescriptor.DescriptorList[0].wReportLength = (USHORT)bufSize;
                ctx->DescriptorSet = TRUE;
            }
            WdfObjectDelete(memory);
        }
    }

    /* Read VendorId */
    RtlInitUnicodeString(&valueName, L"VendorId");
    status = WdfRegistryQueryULong(key, &valueName, &dwordVal);
    if (NT_SUCCESS(status)) {
        ctx->HidDeviceAttributes.VendorID = (USHORT)dwordVal;
    }

    /* Read ProductId */
    RtlInitUnicodeString(&valueName, L"ProductId");
    status = WdfRegistryQueryULong(key, &valueName, &dwordVal);
    if (NT_SUCCESS(status)) {
        ctx->HidDeviceAttributes.ProductID = (USHORT)dwordVal;
    }

    /* Read VersionNumber */
    RtlInitUnicodeString(&valueName, L"VersionNumber");
    status = WdfRegistryQueryULong(key, &valueName, &dwordVal);
    if (NT_SUCCESS(status)) {
        ctx->HidDeviceAttributes.VersionNumber = (USHORT)dwordVal;
    }

    WdfRegistryClose(key);
}

/* ================================================================== */
/*  DriverEntry                                                        */
/* ================================================================== */

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    return WdfDriverCreate(
        DriverObject, RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

/* ================================================================== */
/*  EvtDeviceAdd                                                       */
/* ================================================================== */

NTSTATUS
EvtDeviceAdd(
    _In_    WDFDRIVER       Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    NTSTATUS                status;
    WDF_OBJECT_ATTRIBUTES   attributes;
    WDFDEVICE               device;
    PDEVICE_CONTEXT         ctx;
    WDF_IO_QUEUE_CONFIG     queueConfig;

    UNREFERENCED_PARAMETER(Driver);

    WdfFdoInitSetFilter(DeviceInit);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);

    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    ctx = GetDeviceContext(device);
    RtlZeroMemory(ctx, sizeof(DEVICE_CONTEXT));
    ctx->Device = device;

    /* Initialize defaults */
    RtlCopyMemory(ctx->ReportDescriptor,
                   G_DefaultReportDescriptor,
                   sizeof(G_DefaultReportDescriptor));
    ctx->ReportDescriptorSize = sizeof(G_DefaultReportDescriptor);

    ctx->HidDescriptor.bLength          = 0x09;
    ctx->HidDescriptor.bDescriptorType  = 0x21;
    ctx->HidDescriptor.bcdHID           = 0x0100;
    ctx->HidDescriptor.bCountry         = 0x00;
    ctx->HidDescriptor.bNumDescriptors  = 0x01;
    ctx->HidDescriptor.DescriptorList[0].bReportType   = 0x22;
    ctx->HidDescriptor.DescriptorList[0].wReportLength = (USHORT)ctx->ReportDescriptorSize;

    ctx->HidDeviceAttributes.Size          = sizeof(HID_DEVICE_ATTRIBUTES);
    ctx->HidDeviceAttributes.VendorID      = 0x045E;  /* Microsoft */
    ctx->HidDeviceAttributes.ProductID     = 0x028E;  /* Xbox 360 Controller */
    ctx->HidDeviceAttributes.VersionNumber = 0x0114;

    /* Read config from registry (overrides defaults if present) */
    ReadConfigFromRegistry(ctx);

    /* Create locks */
    status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &ctx->InputLock);
    if (!NT_SUCCESS(status)) return status;

    /* Default queue (parallel) — HID IOCTLs from MsHidUmdf */
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = EvtIoDeviceControl;

    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES,
                              &ctx->DefaultQueue);
    if (!NT_SUCCESS(status)) return status;

    /* Manual queue for pending HID_READ_REPORT */
    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES,
                              &ctx->ManualQueue);

    return status;
}

/* ================================================================== */
/*  EvtIoDeviceControl                                                 */
/* ================================================================== */

VOID
EvtIoDeviceControl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     OutputBufferLength,
    _In_ size_t     InputBufferLength,
    _In_ ULONG      IoControlCode)
{
    NTSTATUS        status = STATUS_NOT_IMPLEMENTED;
    BOOLEAN         completeRequest = TRUE;
    PDEVICE_CONTEXT ctx = GetDeviceContext(WdfIoQueueGetDevice(Queue));

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    switch (IoControlCode) {

    case IOCTL_HID_GET_DEVICE_DESCRIPTOR:
        status = RequestCopyFromBuffer(Request,
            &ctx->HidDescriptor, ctx->HidDescriptor.bLength);
        break;

    case IOCTL_HID_GET_REPORT_DESCRIPTOR:
        status = RequestCopyFromBuffer(Request,
            ctx->ReportDescriptor, ctx->ReportDescriptorSize);
        break;

    case IOCTL_HID_GET_DEVICE_ATTRIBUTES:
        status = RequestCopyFromBuffer(Request,
            &ctx->HidDeviceAttributes, sizeof(HID_DEVICE_ATTRIBUTES));
        break;

    case IOCTL_HID_GET_STRING: {
        static const WCHAR prodStr[] = L"Controller (XBOX 360 For Windows)";
        status = RequestCopyFromBuffer(Request,
            (PVOID)prodStr, sizeof(prodStr));
        break;
    }

    case IOCTL_HID_GET_INDEXED_STRING: {
        static const WCHAR idxStr[] = L"Controller (XBOX 360 For Windows)";
        status = RequestCopyFromBuffer(Request,
            (PVOID)idxStr, sizeof(idxStr));
        break;
    }

    case IOCTL_HID_READ_REPORT: {
        /*
         * HID class wants an input report.
         * Check if user-mode has submitted one, else park the request.
         */
        WdfWaitLockAcquire(ctx->InputLock, NULL);

        if (ctx->InputReportReady) {
            status = RequestCopyFromBuffer(Request,
                ctx->InputReport, ctx->InputReportSize);
            ctx->InputReportReady = FALSE;
            WdfWaitLockRelease(ctx->InputLock);
        } else {
            WdfWaitLockRelease(ctx->InputLock);
            status = WdfRequestForwardToIoQueue(Request, ctx->ManualQueue);
            if (NT_SUCCESS(status)) {
                completeRequest = FALSE;
            }
        }
        break;
    }

    case IOCTL_HID_WRITE_REPORT: {
        /*
         * Two uses:
         * 1. Game writing output report (we store for later retrieval)
         * 2. User-mode writing input report via HidD_SetOutputReport
         *    (we treat as input data — complete pending READ_REPORT)
         *
         * Since this is a virtual device, ALL write reports are treated
         * as input data to feed to the HID class. This is the raw pipe:
         * user-mode calls HidD_SetOutputReport() with packed report bytes,
         * we store them, and complete the next HID_READ_REPORT with them.
         */
        PVOID       inBuf;
        size_t      inBufSize;
        WDFREQUEST  pendingRead;

        status = WdfRequestRetrieveInputBuffer(Request, 1, &inBuf, &inBufSize);
        if (!NT_SUCCESS(status)) break;

        if (inBufSize > HIDMAESTRO_MAX_REPORT_SIZE) {
            status = STATUS_BUFFER_OVERFLOW;
            break;
        }

        /* Fast path: complete pending HID_READ_REPORT directly */
        if (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->ManualQueue, &pendingRead))) {
            NTSTATUS copyStatus = RequestCopyFromBuffer(pendingRead, inBuf, inBufSize);
            WdfRequestComplete(pendingRead,
                NT_SUCCESS(copyStatus) ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL);
        } else {
            /* No pending read — store for later */
            WdfWaitLockAcquire(ctx->InputLock, NULL);
            RtlCopyMemory(ctx->InputReport, inBuf, inBufSize);
            ctx->InputReportSize = (ULONG)inBufSize;
            ctx->InputReportReady = TRUE;
            WdfWaitLockRelease(ctx->InputLock);
        }

        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_UMDF_HID_SET_FEATURE: {
        /*
         * User-mode calls HidD_SetFeature with Report ID 2.
         * The feature data IS the input report payload.
         * Store it and complete the next pending READ_REPORT.
         *
         * UMDF marshalling: input buffer = report data (report ID separate).
         * Output buffer = report ID byte.
         */
        PVOID       featureBuf;
        size_t      featureSize;
        WDFREQUEST  pendingRead;

        status = WdfRequestRetrieveInputBuffer(Request, 1, &featureBuf, &featureSize);
        if (!NT_SUCCESS(status)) break;

        /* Build input report: Report ID 1 + feature data bytes */
        {
            UCHAR inputReport[HIDMAESTRO_MAX_REPORT_SIZE];
            ULONG inputSize;

            inputReport[0] = 0x01; /* Input Report ID */
            inputSize = (ULONG)featureSize;
            if (inputSize > HIDMAESTRO_MAX_REPORT_SIZE - 1)
                inputSize = HIDMAESTRO_MAX_REPORT_SIZE - 1;
            RtlCopyMemory(inputReport + 1, featureBuf, inputSize);
            inputSize += 1;

            if (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->ManualQueue, &pendingRead))) {
                NTSTATUS cs = RequestCopyFromBuffer(pendingRead, inputReport, inputSize);
                WdfRequestComplete(pendingRead, NT_SUCCESS(cs) ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL);
            } else {
                WdfWaitLockAcquire(ctx->InputLock, NULL);
                RtlCopyMemory(ctx->InputReport, inputReport, inputSize);
                ctx->InputReportSize = inputSize;
                ctx->InputReportReady = TRUE;
                WdfWaitLockRelease(ctx->InputLock);
            }
        }

        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_UMDF_HID_GET_FEATURE:
    case IOCTL_UMDF_HID_SET_OUTPUT_REPORT:
    case IOCTL_UMDF_HID_GET_INPUT_REPORT:
        status = STATUS_NOT_SUPPORTED;
        break;

    case IOCTL_HID_ACTIVATE_DEVICE:
    case IOCTL_HID_DEACTIVATE_DEVICE:
    case IOCTL_HID_SEND_IDLE_NOTIFICATION_REQUEST:
        status = STATUS_SUCCESS;
        break;

    default:
        status = STATUS_NOT_IMPLEMENTED;
        break;
    }

    if (completeRequest) {
        WdfRequestComplete(Request, status);
    }
}
