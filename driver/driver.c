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

/* XUSB interface GUID — xinput1_4.dll enumerates this to find Xbox controllers */
static const GUID XUSB_INTERFACE_CLASS_GUID =
    { 0xEC87F1E3, 0xC13B, 0x4100, { 0xB5, 0xF7, 0x8B, 0x84, 0xD5, 0x42, 0x60, 0xCB } };

/* XUSB IOCTL codes (from OpenXinput / XInputHooker) */
#define IOCTL_XUSB_GET_INFORMATION      0x80006000
#define IOCTL_XUSB_GET_CAPABILITIES     0x8000E004
#define IOCTL_XUSB_GET_LED_STATE        0x8000E008
#define IOCTL_XUSB_GET_STATE            0x8000E00C
#define IOCTL_XUSB_SET_STATE            0x8000A010
#define IOCTL_XUSB_WAIT_GUIDE           0x8000E014
#define IOCTL_XUSB_GET_BATTERY_INFO     0x8000E018
#define IOCTL_XUSB_GET_INFORMATION_EX   0x8000E3FC
#define IOCTL_XUSB_WAIT_FOR_INPUT       0x8000E3AC

/* XUSB report structure (20 bytes — matches XUSB_INTERRUPT_IN_PACKET) */
#pragma pack(push, 1)
typedef struct _XUSB_REPORT {
    UCHAR  Id;          /* 0x00 */
    UCHAR  Size;        /* 0x14 (20) */
    USHORT Buttons;
    UCHAR  LeftTrigger;
    UCHAR  RightTrigger;
    SHORT  ThumbLX;
    SHORT  ThumbLY;
    SHORT  ThumbRX;
    SHORT  ThumbRY;
    UCHAR  Reserved[6];
} XUSB_REPORT;
#pragma pack(pop)

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
    /*
     * UMDF2 runs in user-mode (WUDFHost.exe), so WdfRegistryOpenKey with
     * kernel-style paths (\Registry\Machine\...) does NOT work. We use
     * the Win32 RegOpenKeyExW API directly — UMDF2 has full Win32 access.
     */
    HKEY    hKey = NULL;
    LONG    result;
    DWORD   dwordVal, dwordSize;
    BYTE    binBuf[HIDMAESTRO_MAX_DESCRIPTOR_SIZE];
    DWORD   binSize;
    DWORD   regType;

    result = RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\HIDMaestro",
                           0, KEY_READ, &hKey);
    if (result != ERROR_SUCCESS) {
        return; /* No config key — use defaults */
    }

    /* Read ReportDescriptor (REG_BINARY) */
    binSize = sizeof(binBuf);
    result = RegQueryValueExW(hKey, L"ReportDescriptor", NULL,
                              &regType, binBuf, &binSize);
    if (result == ERROR_SUCCESS && regType == REG_BINARY &&
        binSize > 0 && binSize <= HIDMAESTRO_MAX_DESCRIPTOR_SIZE) {
        /*
         * Use the profile descriptor as-is. The test client is responsible
         * for ensuring the descriptor includes whatever data channel items
         * are needed (e.g., Feature Report ID 2).
         *
         * We do NOT modify the descriptor here — injecting Report IDs into
         * descriptors that use the default (no-ID) report can violate HID
         * validation rules. The client pre-processes the descriptor.
         */
        RtlCopyMemory(ctx->ReportDescriptor, binBuf, binSize);
        ctx->ReportDescriptorSize = (ULONG)binSize;
        ctx->HidDescriptor.DescriptorList[0].wReportLength =
            (USHORT)ctx->ReportDescriptorSize;
        ctx->DescriptorSet = TRUE;
    }

    /* Read VendorId (REG_DWORD) */
    dwordSize = sizeof(dwordVal);
    result = RegQueryValueExW(hKey, L"VendorId", NULL,
                              &regType, (LPBYTE)&dwordVal, &dwordSize);
    if (result == ERROR_SUCCESS && regType == REG_DWORD) {
        ctx->HidDeviceAttributes.VendorID = (USHORT)dwordVal;
    }

    /* Read ProductId (REG_DWORD) */
    dwordSize = sizeof(dwordVal);
    result = RegQueryValueExW(hKey, L"ProductId", NULL,
                              &regType, (LPBYTE)&dwordVal, &dwordSize);
    if (result == ERROR_SUCCESS && regType == REG_DWORD) {
        ctx->HidDeviceAttributes.ProductID = (USHORT)dwordVal;
    }

    /* Read VersionNumber (REG_DWORD) */
    dwordSize = sizeof(dwordVal);
    result = RegQueryValueExW(hKey, L"VersionNumber", NULL,
                              &regType, (LPBYTE)&dwordVal, &dwordSize);
    if (result == ERROR_SUCCESS && regType == REG_DWORD) {
        ctx->HidDeviceAttributes.VersionNumber = (USHORT)dwordVal;
    }

    /* Read ProductString (REG_SZ) */
    {
        WCHAR strBuf[128];
        DWORD strSize = sizeof(strBuf);
        result = RegQueryValueExW(hKey, L"ProductString", NULL,
                                  &regType, (LPBYTE)strBuf, &strSize);
        if (result == ERROR_SUCCESS && regType == REG_SZ && strSize > 0) {
            RtlCopyMemory(ctx->ProductString, strBuf, strSize);
            ctx->ProductStringBytes = strSize;
        }
    }

    RegCloseKey(hKey);
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

    /* Default product string */
    {
        static const WCHAR defaultStr[] = L"Controller (XBOX 360 For Windows)";
        RtlCopyMemory(ctx->ProductString, defaultStr, sizeof(defaultStr));
        ctx->ProductStringBytes = sizeof(defaultStr);
    }

    /* Read config from registry (overrides defaults if present) */
    ReadConfigFromRegistry(ctx);

    /*
     * Set BusReportedDeviceDesc so joy.cpl shows the profile name.
     * DEVPKEY_Device_BusReportedDeviceDesc = {540b947e-8b40-45bc-a8a2-6a0b894cbda2}, 4
     */
    {
        static const DEVPROPKEY busDescKey = {
            { 0x540b947e, 0x8b40, 0x45bc, { 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2 } },
            4
        };
        WDF_DEVICE_PROPERTY_DATA propData;
        WDF_DEVICE_PROPERTY_DATA_INIT(&propData, &busDescKey);
        propData.Lcid = LOCALE_NEUTRAL;
        WdfDeviceAssignProperty(device, &propData, DEVPROP_TYPE_STRING,
            ctx->ProductStringBytes, ctx->ProductString);
    }

    /* Register XUSB device interface so XInput can find us.
     * Ignore failure — XInput just won't see us if this fails. */
    {
        NTSTATUS ifaceStatus = WdfDeviceCreateDeviceInterface(device,
            (LPGUID)&XUSB_INTERFACE_CLASS_GUID, NULL);
        (void)ifaceStatus; /* Non-fatal */
    }

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
        /*
         * The string ID is passed in the lower 16 bits of the input value.
         * For UMDF2, retrieve it from the input buffer.
         * HID_STRING_ID_IMANUFACTURER = 1
         * HID_STRING_ID_IPRODUCT = 2
         * HID_STRING_ID_ISERIALNUMBER = 3
         * We return the product string for all queries — this is what
         * joy.cpl and games see.
         */
        PVOID   inBuf = NULL;
        size_t  inBufSize = 0;
        ULONG   stringId = 0;

        /* Try to get string ID from input buffer */
        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(ULONG), &inBuf, &inBufSize))) {
            stringId = *(ULONG*)inBuf;
        }

        /* Return product string for all string types */
        status = RequestCopyFromBuffer(Request,
            ctx->ProductString, ctx->ProductStringBytes);
        break;
    }

    case IOCTL_HID_GET_INDEXED_STRING: {
        status = RequestCopyFromBuffer(Request,
            ctx->ProductString, ctx->ProductStringBytes);
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
         * We prepend Report ID 1 and deliver as input.
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

    /* ============================================================ */
    /*  XUSB IOCTLs — XInput talks to us through these              */
    /* ============================================================ */

    case IOCTL_XUSB_GET_INFORMATION: {
        /*
         * Returns basic device information.
         * xinput1_4.dll calls this first to discover the device.
         * Output: 12 bytes — vendor ID, product ID, input ID, etc.
         */
        UCHAR info[12];
        RtlZeroMemory(info, sizeof(info));
        info[0] = 0x00; /* Type: Gamepad */
        info[1] = 0x01; /* SubType: Gamepad */
        info[2] = 0x01; /* VendorSpecific count */
        info[4] = 0x01; /* InputId */
        status = RequestCopyFromBuffer(Request, info, sizeof(info));
        break;
    }

    case IOCTL_XUSB_GET_CAPABILITIES: {
        /*
         * Returns device capabilities — what buttons/axes it has.
         * Very similar to XInputGetCapabilities output.
         */
        UCHAR caps[20];
        RtlZeroMemory(caps, sizeof(caps));
        caps[0] = 0x01; /* Type: XINPUT_DEVTYPE_GAMEPAD */
        caps[1] = 0x01; /* SubType: XINPUT_DEVSUBTYPE_GAMEPAD */
        caps[2] = 0x04; /* Flags: XINPUT_CAPS_FFB_SUPPORTED */
        /* Gamepad caps (buttons mask = all supported) */
        caps[4] = 0xFF; caps[5] = 0xF3; /* wButtons mask */
        caps[6] = 0xFF; /* bLeftTrigger */
        caps[7] = 0xFF; /* bRightTrigger */
        /* Axes: report max range */
        *(SHORT*)&caps[8]  = 32767; /* ThumbLX */
        *(SHORT*)&caps[10] = 32767; /* ThumbLY */
        *(SHORT*)&caps[12] = 32767; /* ThumbRX */
        *(SHORT*)&caps[14] = 32767; /* ThumbRY */
        status = RequestCopyFromBuffer(Request, caps, sizeof(caps));
        break;
    }

    case IOCTL_XUSB_GET_STATE: {
        /*
         * Returns current gamepad state — this is THE critical IOCTL.
         * xinput1_4.dll calls this for XInputGetState().
         * Output: XUSB_REPORT (22 bytes: 1 byte id + 1 byte size + 20 byte data)
         */
        XUSB_REPORT report;
        RtlZeroMemory(&report, sizeof(report));
        report.Id = 0x00;
        report.Size = 0x14; /* 20 bytes */

        WdfWaitLockAcquire(ctx->InputLock, NULL);
        if (ctx->InputReportReady && ctx->InputReportSize >= 12) {
            /* Parse input report (universal descriptor format):
             * Bytes 0-1: LX, 2-3: LY, 4-5: RX, 6-7: RY,
             * 8-9: LT, 10-11: RT, 12: buttons low, 13: buttons high + hat
             */
            PUCHAR d = ctx->InputReport;
            /* Skip Report ID if present */
            if (d[0] == 0x01 && ctx->InputReportSize > 12) d++;

            report.ThumbLX     = (SHORT)((int)(*(USHORT*)&d[0]) - 32768);
            report.ThumbLY     = (SHORT)((int)(*(USHORT*)&d[2]) - 32768);
            report.ThumbRX     = (SHORT)((int)(*(USHORT*)&d[4]) - 32768);
            report.ThumbRY     = (SHORT)((int)(*(USHORT*)&d[6]) - 32768);
            report.LeftTrigger  = (UCHAR)(*(USHORT*)&d[8] >> 8);
            report.RightTrigger = (UCHAR)(*(USHORT*)&d[10] >> 8);

            /* Button mapping: our descriptor buttons -> XUSB buttons */
            {
                UCHAR btnLow = d[12];
                UCHAR btnHigh = d[13];
                UCHAR hat = (btnHigh >> 2) & 0x0F;
                USHORT buttons = 0;

                if (btnLow & 0x01) buttons |= 0x1000; /* A */
                if (btnLow & 0x02) buttons |= 0x2000; /* B */
                if (btnLow & 0x04) buttons |= 0x4000; /* X */
                if (btnLow & 0x08) buttons |= 0x8000; /* Y */
                if (btnLow & 0x10) buttons |= 0x0100; /* LB */
                if (btnLow & 0x20) buttons |= 0x0200; /* RB */
                if (btnLow & 0x40) buttons |= 0x0020; /* Back */
                if (btnLow & 0x80) buttons |= 0x0010; /* Start */
                if (btnHigh & 0x01) buttons |= 0x0040; /* LThumb */
                if (btnHigh & 0x02) buttons |= 0x0080; /* RThumb */

                /* Hat -> D-pad */
                switch (hat) {
                    case 1: buttons |= 0x0001; break;
                    case 2: buttons |= 0x0001|0x0008; break;
                    case 3: buttons |= 0x0008; break;
                    case 4: buttons |= 0x0008|0x0002; break;
                    case 5: buttons |= 0x0002; break;
                    case 6: buttons |= 0x0002|0x0004; break;
                    case 7: buttons |= 0x0004; break;
                    case 8: buttons |= 0x0004|0x0001; break;
                }
                report.Buttons = buttons;
            }
        }
        WdfWaitLockRelease(ctx->InputLock);

        status = RequestCopyFromBuffer(Request, &report, sizeof(report));
        break;
    }

    case IOCTL_XUSB_SET_STATE: {
        /* Vibration — absorb for now, could pass to user-mode later */
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_XUSB_GET_LED_STATE: {
        UCHAR led[3] = { 0x01, 0x03, 0x02 }; /* Player 1 LED */
        status = RequestCopyFromBuffer(Request, led, sizeof(led));
        break;
    }

    case IOCTL_XUSB_GET_BATTERY_INFO: {
        UCHAR batt[2] = { 0x01, 0x03 }; /* Wired, Full */
        status = RequestCopyFromBuffer(Request, batt, sizeof(batt));
        break;
    }

    case IOCTL_XUSB_WAIT_GUIDE:
    case IOCTL_XUSB_WAIT_FOR_INPUT:
        /* Async waits — park the request */
        status = WdfRequestForwardToIoQueue(Request, ctx->ManualQueue);
        if (NT_SUCCESS(status)) completeRequest = FALSE;
        break;

    case IOCTL_XUSB_GET_INFORMATION_EX: {
        UCHAR infoEx[8];
        RtlZeroMemory(infoEx, sizeof(infoEx));
        infoEx[0] = 0x01; /* Version */
        infoEx[4] = 0x01; /* Count */
        status = RequestCopyFromBuffer(Request, infoEx, sizeof(infoEx));
        break;
    }

    default:
        status = STATUS_NOT_IMPLEMENTED;
        break;
    }

    if (completeRequest) {
        WdfRequestComplete(Request, status);
    }
}
