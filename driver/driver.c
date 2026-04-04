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

/* USB device interface GUID — WGI/GameInputSvc discovers Xbox controllers through this */
static const GUID USB_DEVICE_INTERFACE_GUID =
    { 0xA5DCBF10, 0x6530, 0x11D2, { 0x90, 0x1F, 0x00, 0xC0, 0x4F, 0xB9, 0x51, 0xED } };

/* WinExInput interface GUID — WGI fires GamepadAdded for devices with this interface + &XI_ path */
static const GUID WINEXINPUT_INTERFACE_GUID =
    { 0x6C53D5FD, 0x6480, 0x440F, { 0xB6, 0x18, 0x47, 0x67, 0x50, 0xC5, 0xE1, 0xA6 } };

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
#define IOCTL_XUSB_POWER_INFO          0x80006380  /* xinputhid sends this repeatedly */

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

    /* Read InputReportByteLength (REG_DWORD) — for capping SET_FEATURE→input */
    dwordSize = sizeof(dwordVal);
    result = RegQueryValueExW(hKey, L"InputReportByteLength", NULL,
                              &regType, (LPBYTE)&dwordVal, &dwordSize);
    if (result == ERROR_SUCCESS && regType == REG_DWORD && dwordVal > 0) {
        ctx->InputReportByteLength = dwordVal;
    }

    RegCloseKey(hKey);
}

/* ================================================================== */
/*  Shared Memory Poll Timer                                           */
/* ================================================================== */

static void
EvtSharedMemTimer(
    _In_ WDFTIMER Timer)
{
    PDEVICE_CONTEXT ctx = GetDeviceContext(WdfTimerGetParentObject(Timer));

    /* Read input data from shared file (C:\ProgramData\HIDMaestro\input.bin) */
    HANDLE hFile = CreateFileW(L"C:\\ProgramData\\HIDMaestro\\input.bin",
        GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (hFile == INVALID_HANDLE_VALUE) {
        /* Debug: write error to log file */
        static LONG debugCount = 0;
        if (InterlockedIncrement(&debugCount) <= 3) {
            HANDLE hLog = CreateFileW(L"C:\\ProgramData\\HIDMaestro\\timer_debug.txt",
                FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, 0, NULL);
            if (hLog != INVALID_HANDLE_VALUE) {
                char msg[] = "Timer: input.bin open FAILED\r\n";
                DWORD dummy;
                WriteFile(hLog, msg, sizeof(msg)-1, &dummy, NULL);
                CloseHandle(hLog);
            }
        }
        return;
    }

    HIDMAESTRO_SHARED_INPUT shared;
    DWORD bytesRead = 0;
    BOOL ok = ReadFile(hFile, &shared, sizeof(shared), &bytesRead, NULL);
    CloseHandle(hFile);

    /* Debug: log first successful read */
    {
        static LONG successCount = 0;
        if (InterlockedIncrement(&successCount) <= 3) {
            HANDLE hLog = CreateFileW(L"C:\\ProgramData\\HIDMaestro\\timer_debug.txt",
                FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, 0, NULL);
            if (hLog != INVALID_HANDLE_VALUE) {
                char msg[] = "Timer: file read OK\r\n";
                DWORD dummy;
                WriteFile(hLog, msg, sizeof(msg)-1, &dummy, NULL);
                CloseHandle(hLog);
            }
        }
    }

    if (!ok || bytesRead < 8) {
        static LONG debugCount2 = 0;
        if (InterlockedIncrement(&debugCount2) <= 3) {
            HANDLE hLog = CreateFileW(L"C:\\ProgramData\\HIDMaestro\\timer_debug.txt",
                FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, 0, NULL);
            if (hLog != INVALID_HANDLE_VALUE) {
                char msg[] = "Timer: ReadFile failed\r\n";
                DWORD dummy;
                int len = sizeof(msg) - 1;
                WriteFile(hLog, msg, len, &dummy, NULL);
                CloseHandle(hLog);
            }
        }
        return;
    }

    ULONG seqNo = shared.SeqNo;
    if (seqNo == ctx->SharedMemSeqNo) return; /* No new data */
    ctx->SharedMemSeqNo = seqNo;

    /* Build HID input report from shared file Data (native descriptor format) */
    UCHAR inputReport[HIDMAESTRO_MAX_REPORT_SIZE];
    ULONG dataLen = shared.DataSize;

    BOOLEAN hasReportId = (ctx->FirstInputReportId != 0);

    ULONG maxData;
    if (hasReportId) {
        maxData = ctx->InputReportByteLength > 1 ? ctx->InputReportByteLength - 1 : 16;
    } else {
        maxData = ctx->InputReportByteLength > 0 ? ctx->InputReportByteLength : 17;
    }
    if (dataLen > maxData) dataLen = maxData;
    if (dataLen > sizeof(shared.Data)) dataLen = sizeof(shared.Data);

    ULONG inputSize;
    if (hasReportId) {
        inputReport[0] = ctx->FirstInputReportId;
        RtlCopyMemory(inputReport + 1, shared.Data, dataLen);
        inputSize = dataLen + 1;
    } else {
        RtlCopyMemory(inputReport, shared.Data, dataLen);
        inputSize = dataLen;
    }

    /* Store GIP data for XUSB GET_STATE (from GipData field in shared struct) */
    WdfWaitLockAcquire(ctx->InputLock, NULL);
    RtlCopyMemory(ctx->XusbReport, shared.GipData, 14);
    ctx->XusbReportReady = TRUE;
    WdfWaitLockRelease(ctx->InputLock);

    /* Build Col2 report (Report ID 0x20) with same gamepad data */
    UCHAR col2Report[HIDMAESTRO_MAX_REPORT_SIZE];
    ULONG col2Size = 0;
    /* Only build Col2 if descriptor is large enough to have dual collections (>300 bytes) */
    if (ctx->ReportDescriptorSize > 300) {
        col2Report[0] = 0x20; /* Report ID */
        /* Copy first 15 bytes of GIP data (sticks + triggers + buttons + hat) */
        ULONG col2Data = 15;
        if (col2Data > sizeof(shared.Data)) col2Data = sizeof(shared.Data);
        RtlCopyMemory(col2Report + 1, shared.Data, col2Data);
        col2Size = col2Data + 1;
    }

    /* Complete ALL pending READ_REPORT requests, alternating between Col1 and Col2 */
    {
        WDFREQUEST pendingRead;
        while (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->ManualQueue, &pendingRead))) {
            /* Send Col1 (GIP, no Report ID) */
            NTSTATUS cs = RequestCopyFromBuffer(pendingRead, inputReport, inputSize);
            WdfRequestComplete(pendingRead, NT_SUCCESS(cs) ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL);

            /* Send Col2 (Report ID 0x20) if available */
            if (col2Size > 0 &&
                NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->ManualQueue, &pendingRead))) {
                cs = RequestCopyFromBuffer(pendingRead, col2Report, col2Size);
                WdfRequestComplete(pendingRead, NT_SUCCESS(cs) ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL);
            }
        }
        /* Also store for polled GET_INPUT_REPORT */
        WdfWaitLockAcquire(ctx->InputLock, NULL);
        RtlCopyMemory(ctx->InputReport, inputReport, inputSize);
        ctx->InputReportSize = inputSize;
        ctx->InputReportReady = TRUE;
        WdfWaitLockRelease(ctx->InputLock);
    }
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

    /* EARLIEST DEBUG - verify EvtDeviceAdd is called */
    {
        HANDLE hLog = CreateFileW(L"C:\\ProgramData\\HIDMaestro\\timer_debug.txt",
            FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, 0, NULL);
        if (hLog != INVALID_HANDLE_VALUE) {
            char msg[] = "EvtDeviceAdd: ENTERED\r\n";
            DWORD dummy;
            WriteFile(hLog, msg, sizeof(msg)-1, &dummy, NULL);
            CloseHandle(hLog);
        }
    }

    /*
     * Check if this is the XUSB companion device (function driver mode)
     * or a HID minidriver device (filter mode under mshidumdf).
     * XUSB companion INF sets XusbMode=1 in the device's hardware key.
     * We detect this by querying the hardware ID for "HIDMaestroXUSB".
     */
    BOOLEAN isXusbCompanion = FALSE;
    BOOLEAN isGamepadCompanion = FALSE;
    {
        /* Detect companion by device class. System class = companion, HIDClass = main.
         * All other detection methods (hardware ID query, registry key) are unreliable
         * for UMDF2 System-class devices during WdfFdoInit stage. */
        WDFMEMORY clsMem = NULL;
        if (NT_SUCCESS(WdfFdoInitAllocAndQueryProperty(DeviceInit,
            DevicePropertyClassName, NonPagedPoolNx,
            WDF_NO_OBJECT_ATTRIBUTES, &clsMem)) && clsMem != NULL) {
            PWCHAR clsName = (PWCHAR)WdfMemoryGetBuffer(clsMem, NULL);
            if (clsName && wcsstr(clsName, L"System") != NULL)
                isXusbCompanion = TRUE;
            WdfObjectDelete(clsMem);
        }
    }

    WdfFdoInitSetFilter(DeviceInit);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);

    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    ctx = GetDeviceContext(device);
    RtlZeroMemory(ctx, sizeof(DEVICE_CONTEXT));
    ctx->Device = device;

    if (isXusbCompanion) {
        /* XUSB companion: function driver mode — register XUSB + WinExInput interfaces.
         * WinExInput interface with XI_ reference string triggers WGI GamepadAdded.
         * Also set up minimal HID defaults so WGI HID probes don't fail. */

        /* HID defaults (same as main driver) */
        RtlCopyMemory(ctx->ReportDescriptor, G_DefaultReportDescriptor, sizeof(G_DefaultReportDescriptor));
        ctx->ReportDescriptorSize = sizeof(G_DefaultReportDescriptor);
        ctx->HidDescriptor.bLength = 0x09;
        ctx->HidDescriptor.bDescriptorType = 0x21;
        ctx->HidDescriptor.bcdHID = 0x0100;
        ctx->HidDescriptor.bNumDescriptors = 0x01;
        ctx->HidDescriptor.DescriptorList[0].bReportType = 0x22;
        ctx->HidDescriptor.DescriptorList[0].wReportLength = (USHORT)ctx->ReportDescriptorSize;
        ctx->HidDeviceAttributes.Size = sizeof(HID_DEVICE_ATTRIBUTES);
        ctx->HidDeviceAttributes.VendorID = 0x045E;
        ctx->HidDeviceAttributes.ProductID = 0x028E;
        ctx->HidDeviceAttributes.VersionNumber = 0x0114;
        ctx->InputReportByteLength = 17;
        {
            static const WCHAR defaultStr[] = L"Controller (XBOX 360 For Windows)";
            RtlCopyMemory(ctx->ProductString, defaultStr, sizeof(defaultStr));
            ctx->ProductStringBytes = sizeof(defaultStr);
        }

        /* Do NOT read config — keep defaults (045E:028E) so companion has different
         * VID/PID from main device. Prevents GameInput deduplication. */
        ReadConfigFromRegistry(ctx);
        WdfDeviceCreateDeviceInterface(device,
            (LPGUID)&XUSB_INTERFACE_CLASS_GUID, NULL);

        /* Register WinExInput interface — WGI monitors this GUID for Gamepad discovery */
        {
            UNICODE_STRING refStr;
            RtlInitUnicodeString(&refStr, L"XI_00");
            WdfDeviceCreateDeviceInterface(device,
                (LPGUID)&WINEXINPUT_INTERFACE_GUID, &refStr);
        }

        status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &ctx->InputLock);
        if (!NT_SUCCESS(status)) return status;

        WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
        queueConfig.EvtIoDeviceControl = EvtIoDeviceControl;
        status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES,
                                  &ctx->DefaultQueue);
        if (!NT_SUCCESS(status)) return status;

        WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
        status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES,
                                  &ctx->ManualQueue);
        if (!NT_SUCCESS(status)) return status;
        goto start_timer; /* Read GipData from shared file for GET_STATE */
    }

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

    /* Default input report byte length (Report ID + data) */
    ctx->InputReportByteLength = 17; /* safe default */

    /* Default product string */
    {
        static const WCHAR defaultStr[] = L"Controller (XBOX 360 For Windows)";
        RtlCopyMemory(ctx->ProductString, defaultStr, sizeof(defaultStr));
        ctx->ProductStringBytes = sizeof(defaultStr);
    }

    /* Read config from registry (overrides defaults if present) */
    ReadConfigFromRegistry(ctx);

    /* Find first Input Report ID from the FIRST Application Collection only.
     * For dual-collection descriptors, Col2 may have a Report ID that we
     * must NOT use for Col1's reports. Stop scanning at the first End Collection
     * that closes the top-level Application Collection. */
    ctx->FirstInputReportId = 0;
    {
        int colDepth = 0;
        BOOLEAN inFirstCollection = FALSE;
        for (ULONG ri = 0; ri < ctx->ReportDescriptorSize - 1; ri++) {
            UCHAR prefix = ctx->ReportDescriptor[ri];
            int bSize = prefix & 0x03;
            if (bSize == 3) bSize = 4;
            int bType = (prefix >> 2) & 0x03;
            int bTag = (prefix >> 4) & 0x0F;
            if (bType == 0 && bTag == 10) { /* Collection */
                colDepth++;
                if (colDepth == 1) inFirstCollection = TRUE;
            }
            if (bType == 0 && bTag == 12) { /* End Collection */
                colDepth--;
                if (colDepth == 0 && inFirstCollection) break; /* Done with first collection */
            }
            if (inFirstCollection && prefix == 0x85 && (ri == 0 || ctx->ReportDescriptor[ri-1] != 0x09)) {
                ctx->FirstInputReportId = ctx->ReportDescriptor[ri + 1];
                break;
            }
            ri += bSize; /* Skip value bytes */
        }
    }

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

    /*
     * Register XUSB interface on HID device too (acts like xinputhid for
     * PIDs that xinputhid doesn't support, e.g. Xbox 360 PID 028E).
     * XInput discovers this interface and sends XUSB IOCTLs which our
     * EvtIoDeviceControl already handles alongside HID IOCTLs.
     */
    /* Gamepad companion: no XUSB/USB interfaces (clean HID device for GameInput/SDL3) */
    if (!isGamepadCompanion) {
        WdfDeviceCreateDeviceInterface(device,
            (LPGUID)&XUSB_INTERFACE_CLASS_GUID, NULL);
        WdfDeviceCreateDeviceInterface(device,
            (LPGUID)&USB_DEVICE_INTERFACE_GUID, NULL);
    }

    /*
     * Set BusTypeGuid to USB for child devices (created by mshidumdf).
     * GameInput/SDL3 checks BusTypeGuid to determine how to read HID reports.
     * Without this, children have GUID_BUS_TYPE_HID and GameInput reads zeros.
     */
    /* Register XUSB + WinExInput on ALL devices (main HID + companion).
     * For the main HID device: enables direct XUSB IOCTL handling.
     * For the companion: provides XInput slot + WGI browser detection.
     * WdfFdoInitAllocAndQueryProperty is unreliable in UMDF2, so we
     * can't conditionally register. Registering on all devices is safe. */
    WdfDeviceCreateDeviceInterface(device,
        (LPGUID)&XUSB_INTERFACE_CLASS_GUID, NULL);
    WdfDeviceCreateDeviceInterface(device,
        (LPGUID)&USB_DEVICE_INTERFACE_GUID, NULL);
    {
        UNICODE_STRING refStr;
        RtlInitUnicodeString(&refStr, L"XI_00");
        WdfDeviceCreateDeviceInterface(device,
            (LPGUID)&WINEXINPUT_INTERFACE_GUID, &refStr);
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
    if (!NT_SUCCESS(status)) return status;

start_timer:
    /* Shared file for data injection (bypasses upper filter drivers) */
    ctx->SharedMemHandle = NULL;
    ctx->SharedMemPtr = NULL;
    ctx->SharedMemSeqNo = 0;

    /* Debug: verify we reach this point */
    {
        HANDLE hLog = CreateFileW(L"C:\\ProgramData\\HIDMaestro\\timer_debug.txt",
            FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, 0, NULL);
        if (hLog != INVALID_HANDLE_VALUE) {
            char msg[] = "EvtDeviceAdd: reached timer section\r\n";
            DWORD dummy;
            WriteFile(hLog, msg, sizeof(msg)-1, &dummy, NULL);
            CloseHandle(hLog);
        }
    }

    /* Poll timer: reads shared file every 4ms (~250Hz) for data injection */
    {
        WDF_TIMER_CONFIG timerConfig;
        WDF_TIMER_CONFIG_INIT_PERIODIC(&timerConfig, EvtSharedMemTimer, 4);
        WDF_OBJECT_ATTRIBUTES timerAttrs;
        WDF_OBJECT_ATTRIBUTES_INIT(&timerAttrs);
        timerAttrs.ParentObject = device;
        status = WdfTimerCreate(&timerConfig, &timerAttrs, &ctx->PollTimer);
        /* Debug: log timer creation result */
        {
            HANDLE hLog = CreateFileW(L"C:\\ProgramData\\HIDMaestro\\timer_debug.txt",
                FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, 0, NULL);
            if (hLog != INVALID_HANDLE_VALUE) {
                char msg[64];
                DWORD dummy;
                if (NT_SUCCESS(status)) {
                    char ok[] = "Timer created OK, starting...\r\n";
                    WriteFile(hLog, ok, sizeof(ok)-1, &dummy, NULL);
                } else {
                    char fail[] = "Timer creation FAILED\r\n";
                    WriteFile(hLog, fail, sizeof(fail)-1, &dummy, NULL);
                }
                CloseHandle(hLog);
            }
        }
        if (NT_SUCCESS(status)) {
            WdfTimerStart(ctx->PollTimer, WDF_REL_TIMEOUT_IN_MS(100));
        }
    }

    return STATUS_SUCCESS;
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

    /* Log all IOCTLs for debugging xinputhid protocol */
    {
        static LONG ioLogCount = 0;
        if (InterlockedIncrement(&ioLogCount) <= 500) {
            HANDLE hLog = CreateFileW(L"C:\\ProgramData\\HIDMaestro\\ioctl_debug.txt",
                FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, 0, NULL);
            if (hLog != INVALID_HANDLE_VALUE) {
                char msg[64];
                DWORD dummy;
                ULONG hi = (IoControlCode >> 16) & 0xFFFF;
                ULONG lo = IoControlCode & 0xFFFF;
                msg[0] = 'I'; msg[1] = 'O'; msg[2] = ':'; msg[3] = ' ';
                /* Simple hex encoding */
                static const char hex[] = "0123456789ABCDEF";
                msg[4] = hex[(hi>>12)&0xF]; msg[5] = hex[(hi>>8)&0xF];
                msg[6] = hex[(hi>>4)&0xF]; msg[7] = hex[hi&0xF];
                msg[8] = hex[(lo>>12)&0xF]; msg[9] = hex[(lo>>8)&0xF];
                msg[10] = hex[(lo>>4)&0xF]; msg[11] = hex[lo&0xF];
                msg[12] = '\r'; msg[13] = '\n';
                WriteFile(hLog, msg, 14, &dummy, NULL);
                CloseHandle(hLog);
            }
        }
    }

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

        /* Build input report: Report ID 1 + feature data (skip feature Report ID byte) */
        {
            UCHAR inputReport[HIDMAESTRO_MAX_REPORT_SIZE];
            ULONG inputSize;
            PUCHAR featureData = (PUCHAR)featureBuf;
            ULONG  featureDataLen = (ULONG)featureSize;

            /* The HID class includes the Feature Report ID as byte 0 — skip it */
            if (featureDataLen > 0) {
                featureData++;
                featureDataLen--;
            }

            inputReport[0] = ctx->FirstInputReportId;

            /* Cap data to match the declared input report size */
            {
                ULONG maxData = ctx->InputReportByteLength > 1
                    ? ctx->InputReportByteLength - 1 : 16;
                if (featureDataLen > maxData)
                    featureDataLen = maxData;
            }

            RtlCopyMemory(inputReport + 1, featureData, featureDataLen);
            inputSize = featureDataLen + 1;

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
        status = STATUS_NOT_SUPPORTED;
        break;

    case IOCTL_UMDF_HID_SET_OUTPUT_REPORT: {
        /*
         * Same as SET_FEATURE: user-mode sends output report with input data.
         * We strip the Report ID and deliver as Input Report ID 1.
         * Using Output Report instead of Feature Report avoids phantom axes
         * in Chrome's Gamepad API.
         */
        PVOID       outBuf;
        size_t      outBufSize;
        WDFREQUEST  pendingRead2;

        status = WdfRequestRetrieveInputBuffer(Request, 1, &outBuf, &outBufSize);
        if (!NT_SUCCESS(status)) break;

        {
            UCHAR inputReport2[HIDMAESTRO_MAX_REPORT_SIZE];
            ULONG inputSize2;
            PUCHAR outData = (PUCHAR)outBuf;
            ULONG  outDataLen = (ULONG)outBufSize;

            /* Skip the Output Report ID byte */
            if (outDataLen > 0) { outData++; outDataLen--; }

            inputReport2[0] = 0x01; /* Input Report ID */

            /* Cap to declared input report size */
            {
                ULONG maxData = ctx->InputReportByteLength > 1
                    ? ctx->InputReportByteLength - 1 : 16;
                if (outDataLen > maxData) outDataLen = maxData;
            }

            RtlCopyMemory(inputReport2 + 1, outData, outDataLen);
            inputSize2 = outDataLen + 1;

            if (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->ManualQueue, &pendingRead2))) {
                NTSTATUS cs = RequestCopyFromBuffer(pendingRead2, inputReport2, inputSize2);
                WdfRequestComplete(pendingRead2, NT_SUCCESS(cs) ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL);
            } else {
                WdfWaitLockAcquire(ctx->InputLock, NULL);
                RtlCopyMemory(ctx->InputReport, inputReport2, inputSize2);
                ctx->InputReportSize = inputSize2;
                ctx->InputReportReady = TRUE;
                WdfWaitLockRelease(ctx->InputLock);
            }
        }

        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_UMDF_HID_GET_INPUT_REPORT: {
        /* Return the latest input report for polled reading */
        WdfWaitLockAcquire(ctx->InputLock, NULL);
        if (ctx->InputReportReady) {
            status = RequestCopyFromBuffer(Request,
                ctx->InputReport, ctx->InputReportSize);
        } else {
            /* No data yet — return zeros matching descriptor format */
            UCHAR emptyReport[HIDMAESTRO_MAX_REPORT_SIZE];
            RtlZeroMemory(emptyReport, sizeof(emptyReport));
            ULONG emptySize = ctx->InputReportByteLength;
            /* Check if descriptor uses Report IDs */
            BOOLEAN hasIds = FALSE;
            for (ULONG i = 0; i < ctx->ReportDescriptorSize - 1; i++) {
                if (ctx->ReportDescriptor[i] == 0x85) { hasIds = TRUE; break; }
            }
            if (hasIds) {
                emptyReport[0] = 0x01; /* Report ID 1 */
            }
            if (emptySize == 0) emptySize = 17; /* safe default for GIP */
            status = RequestCopyFromBuffer(Request, emptyReport, emptySize);
        }
        WdfWaitLockRelease(ctx->InputLock);
        break;
    }

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
         * OutDeviceInfos_t — 12 bytes. No input buffer.
         * +0x00 WORD  XUSBVersion (0x0101)
         * +0x02 BYTE  deviceIndex (controller count — must be >= 1)
         * +0x03 BYTE  unk1
         * +0x04 BYTE  unk2 (bit 7 set = skip interface)
         * +0x05 BYTE  unk3
         * +0x06 WORD  unk4
         * +0x08 WORD  vendorId
         * +0x0A WORD  productId
         */
        UCHAR info[12];
        RtlZeroMemory(info, sizeof(info));
        *(USHORT*)&info[0] = 0x0101;  /* XUSBVersion 1.1 */
        info[2] = 0x01;                /* deviceIndex = 1 controller */
        info[4] = 0x00;                /* unk2 — bit 7 clear = don't skip */
        *(USHORT*)&info[8] = ctx->HidDeviceAttributes.VendorID;
        *(USHORT*)&info[10] = ctx->HidDeviceAttributes.ProductID;
        status = RequestCopyFromBuffer(Request, info, sizeof(info));
        break;
    }

    case IOCTL_XUSB_GET_CAPABILITIES: {
        /*
         * GamepadCapabilities0101 — 24 bytes.
         * Input: InBaseRequest_t (3 bytes: version + deviceIndex)
         */
        UCHAR caps[24];
        RtlZeroMemory(caps, sizeof(caps));
        *(USHORT*)&caps[0] = 0x0101;  /* XUSBVersion */
        caps[2] = 0x01;                /* Type: XINPUT_DEVTYPE_GAMEPAD */
        caps[3] = 0x01;                /* SubType: XINPUT_DEVSUBTYPE_GAMEPAD */
        *(USHORT*)&caps[4] = 0xF3FF;  /* wButtons mask (all) */
        caps[6] = 0xFF;                /* bLeftTrigger */
        caps[7] = 0xFF;                /* bRightTrigger */
        *(SHORT*)&caps[8]  = 32767;    /* ThumbLX */
        *(SHORT*)&caps[10] = 32767;    /* ThumbLY */
        *(SHORT*)&caps[12] = 32767;    /* ThumbRX */
        *(SHORT*)&caps[14] = 32767;    /* ThumbRY */
        caps[22] = 0xFF;               /* bLeftMotorSpeed */
        caps[23] = 0xFF;               /* bRightMotorSpeed */
        status = RequestCopyFromBuffer(Request, caps, sizeof(caps));
        break;
    }

    case IOCTL_XUSB_GET_STATE: {
        /*
         * GamepadState0101 — 29 bytes.
         * Input: InBaseRequest_t (3 bytes: WORD version + BYTE deviceIndex)
         *
         * ALSO: if input > 3 bytes, the extra bytes are input report data
         * from our user-mode feeder (piggyback on GET_STATE's RW access).
         */
        {
            PVOID gsInBuf; size_t gsInSize;
            if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, 1, &gsInBuf, &gsInSize))
                && gsInSize > 3) {
                /* Input report data piggybacked on GET_STATE */
                PUCHAR reportData = (PUCHAR)gsInBuf + 3;
                ULONG reportSize = (ULONG)(gsInSize - 3);
                WdfWaitLockAcquire(ctx->InputLock, NULL);
                if (reportSize > HIDMAESTRO_MAX_REPORT_SIZE)
                    reportSize = HIDMAESTRO_MAX_REPORT_SIZE;
                RtlCopyMemory(ctx->InputReport, reportData, reportSize);
                ctx->InputReportSize = reportSize;
                ctx->InputReportReady = TRUE;
                WdfWaitLockRelease(ctx->InputLock);
            }
        }

        UCHAR state[29];
        RtlZeroMemory(state, sizeof(state));
        *(USHORT*)&state[0] = 0x0101;
        state[2] = 0x01; /* CONNECTED */

        WdfWaitLockAcquire(ctx->InputLock, NULL);
        ctx->InputReportsSubmitted++;
        *(DWORD*)&state[5] = (DWORD)ctx->InputReportsSubmitted;

        /* Read from XusbReport (always GIP format, 14 bytes) */
        if (ctx->XusbReportReady) {
            PUCHAR d = ctx->XusbReport;
            UCHAR btnLow = d[12], btnHigh = d[13];
            UCHAR hat = (btnHigh >> 2) & 0x0F;
            USHORT buttons = 0;
            if (btnLow & 0x01) buttons |= 0x1000;
            if (btnLow & 0x02) buttons |= 0x2000;
            if (btnLow & 0x04) buttons |= 0x4000;
            if (btnLow & 0x08) buttons |= 0x8000;
            if (btnLow & 0x10) buttons |= 0x0100;
            if (btnLow & 0x20) buttons |= 0x0200;
            if (btnLow & 0x40) buttons |= 0x0020;
            if (btnLow & 0x80) buttons |= 0x0010;
            if (btnHigh & 0x01) buttons |= 0x0040;
            if (btnHigh & 0x02) buttons |= 0x0080;
            switch (hat) {
                case 1: buttons |= 0x0001; break; case 2: buttons |= 0x0009; break;
                case 3: buttons |= 0x0008; break; case 4: buttons |= 0x000A; break;
                case 5: buttons |= 0x0002; break; case 6: buttons |= 0x0006; break;
                case 7: buttons |= 0x0004; break; case 8: buttons |= 0x0005; break;
            }
            *(USHORT*)&state[0x0B] = buttons;
            /* GIP triggers: 10-bit (0-1023) in bits 0-9. Scale to 0-255 for XInput. */
            state[0x0D] = (UCHAR)((*(USHORT*)&d[8] & 0x03FF) * 255 / 1023);
            state[0x0E] = (UCHAR)((*(USHORT*)&d[10] & 0x03FF) * 255 / 1023);
            *(SHORT*)&state[0x0F] = (SHORT)((int)(*(USHORT*)&d[0]) - 32768);       /* LX */
            *(SHORT*)&state[0x11] = (SHORT)(32767 - (int)(*(USHORT*)&d[2]));    /* LY: negate (HID down=positive, XInput up=positive) */
            *(SHORT*)&state[0x13] = (SHORT)((int)(*(USHORT*)&d[4]) - 32768);    /* RX */
            *(SHORT*)&state[0x15] = (SHORT)(32767 - (int)(*(USHORT*)&d[6]));    /* RY: negate */
        }
        WdfWaitLockRelease(ctx->InputLock);

        /* Copy state to output buffer */
        {
            PVOID outBuf; size_t outSize;
            status = WdfRequestRetrieveOutputBuffer(Request, sizeof(state), &outBuf, &outSize);
            if (NT_SUCCESS(status)) {
                RtlCopyMemory(outBuf, state, sizeof(state));
                WdfRequestSetInformation(Request, sizeof(state));
            }
        }
        break;
    }

    case IOCTL_XUSB_SET_STATE: {
        /* Dual purpose: vibration (5 bytes) or input submission (>5 bytes) */
        PVOID setBuf; size_t setBufSize;
        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, 1, &setBuf, &setBufSize))
            && setBufSize > 5) {
            WdfWaitLockAcquire(ctx->InputLock, NULL);
            ULONG copySize = (ULONG)setBufSize;
            if (copySize > HIDMAESTRO_MAX_REPORT_SIZE)
                copySize = HIDMAESTRO_MAX_REPORT_SIZE;
            RtlCopyMemory(ctx->InputReport, setBuf, copySize);
            ctx->InputReportSize = copySize;
            ctx->InputReportReady = TRUE;
            WdfWaitLockRelease(ctx->InputLock);
        }
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_XUSB_GET_LED_STATE: {
        /* 3 bytes: version(2) + LED state(1) */
        UCHAR led[3];
        *(USHORT*)&led[0] = 0x0101;
        led[2] = 0x02; /* Player 1 */
        status = RequestCopyFromBuffer(Request, led, sizeof(led));
        break;
    }

    case IOCTL_XUSB_GET_BATTERY_INFO: {
        /* 4 bytes for v1.1: version(2) + batteryType(1) + batteryLevel(1) */
        UCHAR batt[4];
        *(USHORT*)&batt[0] = 0x0101;
        batt[2] = 0x01; /* BATTERY_TYPE_WIRED */
        batt[3] = 0x03; /* BATTERY_LEVEL_FULL */
        status = RequestCopyFromBuffer(Request, batt, sizeof(batt));
        break;
    }

    case IOCTL_XUSB_POWER_INFO: {
        /* xinputhid sends this repeatedly — return success with zeroed buffer */
        PVOID outBuf;
        size_t outSize;
        status = WdfRequestRetrieveOutputBuffer(Request, 1, &outBuf, &outSize);
        if (NT_SUCCESS(status)) {
            RtlZeroMemory(outBuf, outSize);
            WdfRequestSetInformation(Request, outSize);
        }
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
