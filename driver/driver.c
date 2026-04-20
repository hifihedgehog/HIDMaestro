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

/* Default registry path (single-instance fallback) */
static const WCHAR CONFIG_REG_PATH[] = L"\\Registry\\Machine\\SOFTWARE\\HIDMaestro";

/* Append the decimal representation of a ULONG to a wide-string buffer.
 * Self-contained — no C runtime dependency. The driver doesn't link against
 * MSVCRT, so swprintf/wsprintf aren't available. Buffer must be NUL-terminated. */
static VOID
AppendUlongDecimal(_Inout_ WCHAR *dest, _In_ ULONG value, _In_ SIZE_T maxChars)
{
    SIZE_T len = 0;
    while (len < maxChars && dest[len] != 0) len++;
    if (len + 1 >= maxChars) return;

    WCHAR tmp[16];
    int n = 0;
    if (value == 0) {
        tmp[n++] = L'0';
    } else {
        while (value > 0 && n < 15) {
            tmp[n++] = L'0' + (WCHAR)(value % 10);
            value /= 10;
        }
    }
    while (n > 0 && len + 1 < maxChars) {
        dest[len++] = tmp[--n];
    }
    dest[len] = 0;
}

/* Initialize per-instance paths from ControllerIndex.
 * Reads ControllerIndex from device HW key (written by test app at device creation).
 * Falls back to index 0 / legacy global paths if not found. */
static VOID
InitInstancePaths(
    _In_ PDEVICE_CONTEXT ctx,
    _In_ WDFDEVICE       device)
{
    ULONG index = 0;

    /* Try reading ControllerIndex from device's HW registry key */
    {
        WDFKEY hKey;
        if (NT_SUCCESS(WdfDeviceOpenRegistryKey(device, PLUGPLAY_REGKEY_DEVICE,
                KEY_READ, WDF_NO_OBJECT_ATTRIBUTES, &hKey))) {
            UNICODE_STRING valueName;
            RtlInitUnicodeString(&valueName, L"ControllerIndex");
            ULONG val = 0;
            if (NT_SUCCESS(WdfRegistryQueryULong(hKey, &valueName, &val)))
                index = val;
            WdfRegistryClose(hKey);
        }
    }

    ctx->ControllerIndex = index;

    /* Build per-instance paths. Multi-digit indices fully supported — there's
     * no artificial cap on controller count. XInput tops out at 4 slots
     * (Microsoft's limit, not ours), but DInput / HIDAPI / WGI / browser
     * see all virtual controllers regardless of count. */
    {
        static const WCHAR prefix[] = L"SOFTWARE\\HIDMaestro\\Controller";
        SIZE_T cap = sizeof(ctx->ConfigRegPath) / sizeof(WCHAR);
        RtlCopyMemory(ctx->ConfigRegPath, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->ConfigRegPath, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroInput";
        SIZE_T cap = sizeof(ctx->SharedMappingName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->SharedMappingName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->SharedMappingName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroOutput";
        SIZE_T cap = sizeof(ctx->OutputMappingName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->OutputMappingName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->OutputMappingName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroInputEvent";
        SIZE_T cap = sizeof(ctx->InputEventName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->InputEventName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->InputEventName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroStopEvent";
        SIZE_T cap = sizeof(ctx->StopEventName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->StopEventName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->StopEventName, index, cap);
    }

    /* Per-instance serial number. Format: "HM-CTL-<index>" zero-padded to
     * at least 4 digits so it sorts naturally. SDL3 / HIDAPI use this string
     * to distinguish identical controllers; without it, two virtual DualSense
     * with the same VID/PID/ProductString get bucketed as one device by
     * hid_enumerate. The exact format isn't part of any contract — consumers
     * are expected to treat the string as opaque. */
    {
        static const WCHAR prefix[] = L"HM-CTL-";
        SIZE_T cap = sizeof(ctx->SerialString) / sizeof(WCHAR);
        RtlCopyMemory(ctx->SerialString, prefix, sizeof(prefix));
        /* Zero-pad to 4 digits */
        if (index < 1000) {
            SIZE_T len = (sizeof(prefix) / sizeof(WCHAR)) - 1;
            if (index < 10)   { ctx->SerialString[len++] = L'0'; ctx->SerialString[len++] = L'0'; ctx->SerialString[len++] = L'0'; }
            else if (index < 100)  { ctx->SerialString[len++] = L'0'; ctx->SerialString[len++] = L'0'; }
            else if (index < 1000) { ctx->SerialString[len++] = L'0'; }
            ctx->SerialString[len] = 0;
        }
        AppendUlongDecimal(ctx->SerialString, index, cap);
        /* Compute byte length including the trailing NUL */
        SIZE_T slen = 0;
        while (slen < cap && ctx->SerialString[slen] != 0) slen++;
        ctx->SerialStringBytes = (ULONG)((slen + 1) * sizeof(WCHAR));
    }
}

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

    /* Try per-instance key first, fall back to legacy global key */
    result = RegOpenKeyExW(HKEY_LOCAL_MACHINE, ctx->ConfigRegPath,
                           0, KEY_READ, &hKey);
    if (result != ERROR_SUCCESS) {
        result = RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\HIDMaestro",
                               0, KEY_READ, &hKey);
        if (result != ERROR_SUCCESS)
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

    /* Read HidAttrPid (REG_DWORD) — overrides ProductID in HID attributes only.
     * Companion still reads ProductId for XUSB identity.
     * PID 0x0001 prevents GameInput/HIDAPI from claiming xinputhid devices,
     * so SDL3 falls through to XInput backend (correct identity). */
    dwordSize = sizeof(dwordVal);
    result = RegQueryValueExW(hKey, L"HidAttrPid", NULL,
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

    /* Read InputReportByteLength (REG_DWORD) — for capping SET_FEATURE→input.
     * Bounds-check to HIDMAESTRO_MAX_REPORT_SIZE so a corrupt registry can't
     * cause buffer overflows or wildly wrong report sizing. */
    dwordSize = sizeof(dwordVal);
    result = RegQueryValueExW(hKey, L"InputReportByteLength", NULL,
                              &regType, (LPBYTE)&dwordVal, &dwordSize);
    if (result == ERROR_SUCCESS && regType == REG_DWORD &&
        dwordVal > 0 && dwordVal <= HIDMAESTRO_MAX_REPORT_SIZE) {
        ctx->InputReportByteLength = dwordVal;
    }

    RegCloseKey(hKey);
}

/* ================================================================== */
/*  Shared Memory Poll Timer                                           */
/* ================================================================== */

/* Try to open and map the named section. Returns TRUE on success.
 * On failure, leaves SharedMemHandle/SharedMemPtr unchanged (NULL). */
static BOOLEAN
TryOpenSharedMapping(_In_ PDEVICE_CONTEXT ctx)
{
    HANDLE h = OpenFileMappingW(FILE_MAP_READ, FALSE, ctx->SharedMappingName);
    if (h == NULL) return FALSE;

    PVOID view = MapViewOfFile(h, FILE_MAP_READ, 0, 0, sizeof(HIDMAESTRO_SHARED_INPUT));
    if (view == NULL) { CloseHandle(h); return FALSE; }

    ctx->SharedMemHandle = h;
    ctx->SharedMemPtr = view;
    return TRUE;
}

/* Open the OUTPUT section. The test app pre-creates the named section with
 * a permissive SDDL during EmulateProfile setup; we just attach with R/W
 * access. Pagefile-backed, RAM-only. We retry on every capture call until
 * the section appears (test app may not have created it yet at first IOCTL).
 *
 * IMPORTANT: WUDFHost runs as LocalService which lacks SeCreateGlobalPrivilege,
 * so the driver CANNOT CreateFileMapping in the Global\ namespace — only
 * the test app (running elevated) can. */
/* Stale-handle recovery (issue #2, output side of #1): periodic re-open
 * every 500 writes (~2s) so we pick up fresh sections after SDK teardown. */
static BOOLEAN
EnsureOutputMapping(_In_ PDEVICE_CONTEXT ctx)
{
    if (ctx->OutputMemPtr != NULL) {
        if (++ctx->OutputWriteCount < 500) return TRUE;
        UnmapViewOfFile(ctx->OutputMemPtr); ctx->OutputMemPtr = NULL;
        CloseHandle(ctx->OutputMemHandle);  ctx->OutputMemHandle = NULL;
        ctx->OutputWriteCount = 0;
    }

    HANDLE h = OpenFileMappingW(FILE_MAP_WRITE | FILE_MAP_READ, FALSE,
                                ctx->OutputMappingName);
    if (h == NULL) return FALSE;

    PVOID view = MapViewOfFile(h, FILE_MAP_WRITE | FILE_MAP_READ, 0, 0,
                               sizeof(HIDMAESTRO_SHARED_OUTPUT));
    if (view == NULL) { CloseHandle(h); return FALSE; }

    ctx->OutputMemHandle = h;
    ctx->OutputMemPtr = view;
    ctx->OutputWriteCount = 0;
    return TRUE;
}

/* Publish a captured output report to the shared section.
 * Source: HIDMAESTRO_OUTPUT_SOURCE_*  reportId: HID Report ID byte (0 if none)
 * data/size: payload (size will be clamped to 256). Seqlock-write pattern
 * mirrors the input direction's seqlock-read in driver/companion. */
static VOID
PublishOutput(_In_ PDEVICE_CONTEXT ctx,
              _In_ UCHAR Source,
              _In_ UCHAR ReportId,
              _In_reads_bytes_(DataSize) const UCHAR *Data,
              _In_ ULONG DataSize)
{
    if (DataSize > sizeof(((HIDMAESTRO_SHARED_OUTPUT*)0)->Data))
        DataSize = sizeof(((HIDMAESTRO_SHARED_OUTPUT*)0)->Data);

    /* Serialize writers (the queue dispatches IOCTLs in parallel, so two
     * threads can call PublishOutput concurrently). The reader is in another
     * process and uses the seqlock pattern (sample, copy, sample, retry). */
    WdfWaitLockAcquire(ctx->OutputLock, NULL);

    if (!EnsureOutputMapping(ctx)) {
        WdfWaitLockRelease(ctx->OutputLock);
        return;
    }

    volatile HIDMAESTRO_SHARED_OUTPUT *dst =
        (volatile HIDMAESTRO_SHARED_OUTPUT *)ctx->OutputMemPtr;

    /* Matches the input direction's seqlock convention: writer increments
     * SeqNo AFTER the payload is fully written; reader samples SeqNo, copies,
     * samples SeqNo again, and retries if they differ. */
    dst->Source = Source;
    dst->ReportId = ReportId;
    dst->DataSize = (USHORT)DataSize;
    for (ULONG i = 0; i < DataSize; i++) dst->Data[i] = Data[i];
    MemoryBarrier();
    ctx->OutputSeqNoLocal++;
    dst->SeqNo = ctx->OutputSeqNoLocal;

    WdfWaitLockRelease(ctx->OutputLock);

    InterlockedIncrement(&ctx->OutputReportsReceived);
}

/* Read shared input via memory mapping. RAM-only — no disk fallback.
 * Output: *out is filled with the shared struct on success. */
static BOOLEAN
ReadSharedInput(_In_ PDEVICE_CONTEXT ctx, _Out_ HIDMAESTRO_SHARED_INPUT *out)
{
    /* Lazy open: try the mapping on every tick until it succeeds.
     * The test app may create the section after the device starts. */
    if (ctx->SharedMemPtr == NULL && !TryOpenSharedMapping(ctx))
        return FALSE;

    /* Seqlock-style read: retry until SeqNo is stable across the copy.
     * Single writer / many readers, lock-free. */
    volatile HIDMAESTRO_SHARED_INPUT *src = (volatile HIDMAESTRO_SHARED_INPUT *)ctx->SharedMemPtr;
    ULONG seq1, seq2;
    int retries = 4;
    do {
        seq1 = src->SeqNo;
        MemoryBarrier();
        RtlCopyMemory(out, (const void *)src, sizeof(*out));
        MemoryBarrier();
        seq2 = src->SeqNo;
    } while (seq1 != seq2 && --retries > 0);
    return TRUE;
}

/* Core per-frame work extracted from the old EvtSharedMemTimer.
 * Called from the event-driven worker thread whenever the SDK signals
 * InputDataEvent (or the 50 ms safety tick fires). Doing all the HID
 * report-build + manual-queue drain here — no WDF timer, no IRQL games:
 * WdfRequestComplete / WdfWaitLock* are documented safe from a raw worker
 * thread in UMDF2. */
static void
ProcessSharedInput(_In_ PDEVICE_CONTEXT ctx)
{
    HIDMAESTRO_SHARED_INPUT shared;
    if (!ReadSharedInput(ctx, &shared)) return;

    ULONG seqNo = shared.SeqNo;
    if (seqNo == ctx->SharedMemSeqNo) return; /* No new data */
    ctx->SharedMemSeqNo = seqNo;

    /* Build HID input report from shared file Data (native descriptor format).
     * Report MUST be exactly InputReportByteLength bytes — HidClass rejects
     * short reports.  Zero-fill first, then overlay actual data. */
    UCHAR inputReport[HIDMAESTRO_MAX_REPORT_SIZE];
    RtlZeroMemory(inputReport, sizeof(inputReport));
    ULONG dataLen = shared.DataSize;

    BOOLEAN hasReportId = (ctx->FirstInputReportId != 0);

    ULONG expectedSize;
    if (hasReportId) {
        expectedSize = ctx->InputReportByteLength > 0 ? ctx->InputReportByteLength : 17;
    } else {
        expectedSize = ctx->InputReportByteLength > 0 ? ctx->InputReportByteLength : 17;
    }

    ULONG maxData;
    if (hasReportId) {
        maxData = expectedSize > 1 ? expectedSize - 1 : 16;
    } else {
        maxData = expectedSize;
    }
    if (dataLen > maxData) dataLen = maxData;
    if (dataLen > sizeof(shared.Data)) dataLen = sizeof(shared.Data);

    ULONG inputSize;
    if (hasReportId) {
        inputReport[0] = ctx->FirstInputReportId;
        RtlCopyMemory(inputReport + 1, shared.Data, dataLen);
        inputSize = expectedSize; /* Always send full expected length */
    } else {
        RtlCopyMemory(inputReport, shared.Data, dataLen);
        inputSize = expectedSize; /* Always send full expected length */
    }

    /* Store GIP data for XUSB GET_STATE (from GipData field in shared struct) */
    WdfWaitLockAcquire(ctx->InputLock, NULL);
    RtlCopyMemory(ctx->XusbReport, shared.GipData, 14);
    ctx->XusbReportReady = TRUE;
    WdfWaitLockRelease(ctx->InputLock);

    /* Build Col2 report (Report ID 0x20) with same gamepad data */
    UCHAR col2Report[HIDMAESTRO_MAX_REPORT_SIZE];
    ULONG col2Size = 0;
    /* Build Col2 if descriptor has a second TLC (Report ID 0x20).
     * Check if Report ID 0x20 exists in the descriptor. */
    if (ctx->ReportDescriptorSize > 130) {
        /* Check if descriptor contains Report ID 0x20 (0x85 0x20) */
        BOOLEAN hasCol2 = FALSE;
        for (ULONG i = 0; i + 1 < ctx->ReportDescriptorSize; i++) {
            if (ctx->ReportDescriptor[i] == 0x85 && ctx->ReportDescriptor[i+1] == 0x20) {
                hasCol2 = TRUE; break;
            }
        }
        if (hasCol2) {
            col2Report[0] = 0x20; /* Report ID */
            /* Write separate trigger data: Brake(LT) and Accelerator(RT) as 16-bit values */
            USHORT lt16 = (USHORT)((*(USHORT*)&shared.GipData[8] & 0x03FF) * 65535 / 1023);
            USHORT rt16 = (USHORT)((*(USHORT*)&shared.GipData[10] & 0x03FF) * 65535 / 1023);
            *(USHORT*)&col2Report[1] = lt16;
            *(USHORT*)&col2Report[3] = rt16;
            col2Size = 5; /* Report ID + Brake(2) + Accel(2) */
        }
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
        /* Also store for polled GET_INPUT_REPORT, and bump the seqno gate
         * so the next IOCTL_HID_READ_REPORT for this seqno completes
         * directly (the queued ones have already been drained above). */
        WdfWaitLockAcquire(ctx->InputLock, NULL);
        RtlCopyMemory(ctx->InputReport, inputReport, inputSize);
        ctx->InputReportSize = inputSize;
        ctx->InputReportReady = TRUE;
        ctx->LastDeliveredInputSeqNo = ctx->SharedMemSeqNo;
        WdfWaitLockRelease(ctx->InputLock);
    }
}

/* Event-driven worker thread. Bulletproof design: the ONLY way this
 * function returns is StopEvent being signaled. Every other condition —
 * WAIT_FAILED, WAIT_TIMEOUT, stale-handle detection, invalid-handle,
 * OpenEvent/OpenFileMapping failure — recycles the handles and loops
 * back to Phase 1 to re-discover fresh kernel objects.
 *
 * This is a deliberate departure from the prior "5s timeout OR 250 stale
 * wakeups" logic, which could leave the worker stuck in scenarios where
 * the SDK kept signaling the old event (keeping staleWakeups small) but
 * the shared-memory view was pointing at destroyed/stale pages. In that
 * state the 5s timeout never fired (events kept arriving) and the stale
 * counter reset on each signal, so recycle never triggered — permanent
 * deadlock until WUDFHost was killed.
 *
 * Two-phase wait:
 *   Phase 1 (bootstrap): the driver may attach before the SDK has created
 *     Global\HIDMaestroInputEvent<N>. Wait on StopEvent only with a short
 *     200 ms timeout; on each timeout, retry OpenEventW. Once it succeeds,
 *     drop into Phase 2.
 *   Phase 2 (steady state): wait on (StopEvent, InputDataEvent) with a
 *     500 ms timeout so even if the SDK never signals, we recycle and
 *     re-verify handles every half second. When the SDK is active this
 *     is still effectively zero CPU (events arrive well under 500 ms) —
 *     the timeout is a safety net, not a polling interval.
 *
 * StopEvent is signaled from:
 *   (a) EvtDeviceContextCleanup — normal PnP teardown
 *   (b) External SDK RemoveAllVirtualControllers cleanup — opens the
 *       named stop event and signals it to unblock worker threads of
 *       force-killed prior processes
 * Both cases result in a clean return 0. The 2-second thread-join in
 * EvtDeviceContextCleanup is the backstop if the worker is somehow stuck
 * outside the wait (e.g., inside ProcessSharedInput's WdfRequestComplete
 * during a concurrent teardown). */
static DWORD WINAPI
SharedInputWorkerProc(_In_ LPVOID Parameter)
{
    PDEVICE_CONTEXT ctx = (PDEVICE_CONTEXT)Parameter;

    /* Outer recovery loop. Phase 1 discovers/re-discovers the named event;
     * Phase 2 processes frames until StopEvent OR until we detect stale
     * handles, at which point we fall back through to Phase 1 with NULL
     * handles to re-open fresh. There is NO return path out of this loop
     * except StopEvent. */
    for (;;) {
        /* Phase 1: bootstrap — wait for the SDK to create the named event.
         * StopEvent is checked on every 200 ms tick so teardown stays
         * responsive even when the SDK hasn't started up yet. */
        while (ctx->InputDataEvent == NULL) {
            HANDLE ev = OpenEventW(EVENT_MODIFY_STATE | SYNCHRONIZE, FALSE,
                                   ctx->InputEventName);
            if (ev != NULL) {
                ctx->InputDataEvent = ev;
                break;
            }
            if (WaitForSingleObject(ctx->StopEvent, 200) == WAIT_OBJECT_0)
                return 0;
        }

        /* Phase 2: steady state. The 500 ms timeout + unconditional recycle
         * on ANY non-signal rc (TIMEOUT, FAILED, ABANDONED, unexpected)
         * guarantees recovery from every class of stale-handle failure
         * within half a second of the SDK stopping signaling. The stale-
         * seqno counter recycles after 250 wakeups (~5s at ~50 Hz input)
         * to catch the "event fires but shared-memory view is stale" case,
         * where the SDK keeps signaling an event object we share by name
         * but writes to a view the driver isn't reading from anymore. */
        ULONG staleWakeups = 0;
        BOOLEAN recycle = FALSE;
        HANDLE waits[2] = { ctx->StopEvent, ctx->InputDataEvent };

        for (;;) {
            DWORD rc = WaitForMultipleObjects(2, waits, FALSE, 500);

            if (rc == WAIT_OBJECT_0)
                return 0; /* StopEvent → the only legitimate exit */

            if (rc == WAIT_OBJECT_0 + 1) {
                ULONG prevSeq = ctx->SharedMemSeqNo;
                ProcessSharedInput(ctx);
                if (ctx->SharedMemSeqNo == prevSeq) {
                    if (++staleWakeups > 250) { recycle = TRUE; break; }
                } else {
                    staleWakeups = 0;
                }
                continue;
            }

            /* WAIT_TIMEOUT (258), WAIT_FAILED (0xFFFFFFFF), or any other
             * unexpected value. Previously WAIT_FAILED returned 0 and
             * killed the worker permanently; now we recycle like every
             * other non-signal path and let Phase 1 re-open fresh handles.
             * The 2-second thread-join timeout in EvtDeviceContextCleanup
             * still bounds any teardown race. */
            recycle = TRUE;
            break;
        }

        if (recycle) {
            /* Close stale handles — Phase 1 will re-open fresh ones.
             * Unmapping the view first, then closing handles, keeps the
             * sequence symmetric with TryOpenSharedMapping. */
            CloseHandle(ctx->InputDataEvent);
            ctx->InputDataEvent = NULL;
            if (ctx->SharedMemPtr)    { UnmapViewOfFile(ctx->SharedMemPtr);    ctx->SharedMemPtr = NULL; }
            if (ctx->SharedMemHandle) { CloseHandle(ctx->SharedMemHandle);     ctx->SharedMemHandle = NULL; }
            /* Also reset cached seqno so the fresh mapping's first read
             * (even if it happens to land on a SeqNo matching our previous
             * cached value by chance) is treated as new data. */
            ctx->SharedMemSeqNo = 0;
        }
    }
}

/* ================================================================== */
/* Context cleanup: unmap shared memory section on device teardown */
static EVT_WDF_OBJECT_CONTEXT_CLEANUP EvtDeviceContextCleanup;
static void EvtDeviceContextCleanup(_In_ WDFOBJECT Object)
{
    PDEVICE_CONTEXT ctx = GetDeviceContext((WDFDEVICE)Object);

    /* Stop the worker thread first so nothing is touching SharedMemPtr
     * while we unmap. SetEvent → 2-second join (should be instant — the
     * worker wakes on the stop event and returns). */
    if (ctx->StopEvent) SetEvent(ctx->StopEvent);
    if (ctx->WorkerThread) {
        WaitForSingleObject(ctx->WorkerThread, 2000);
        CloseHandle(ctx->WorkerThread);
        ctx->WorkerThread = NULL;
    }
    if (ctx->InputDataEvent) { CloseHandle(ctx->InputDataEvent); ctx->InputDataEvent = NULL; }
    if (ctx->StopEvent)      { CloseHandle(ctx->StopEvent);      ctx->StopEvent = NULL; }

    if (ctx->SharedMemPtr) { UnmapViewOfFile(ctx->SharedMemPtr); ctx->SharedMemPtr = NULL; }
    if (ctx->SharedMemHandle) { CloseHandle(ctx->SharedMemHandle); ctx->SharedMemHandle = NULL; }
    if (ctx->OutputMemPtr) { UnmapViewOfFile(ctx->OutputMemPtr); ctx->OutputMemPtr = NULL; }
    if (ctx->OutputMemHandle) { CloseHandle(ctx->OutputMemHandle); ctx->OutputMemHandle = NULL; }
}

/* ================================================================== */
/* SelfManagedIo: retry XUSB interface enable after device fully starts */
static NTSTATUS EvtSelfManagedIoInit(_In_ WDFDEVICE Device)
{
    UNREFERENCED_PARAMETER(Device);
    /* XUSB interface is NOT registered on the main device — companion handles it.
     * Do NOT re-enable stale XUSB interfaces here. */
    return STATUS_SUCCESS;
}

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

    /* HIDMaestro.dll only loads for HIDClass devices (gamepad companion).
     * XUSB companion uses HMXInput.dll — separate DLL, no shared code. */

    /* FunctionMode=1 skips filter mode so we can register XUSB on the HID device.
     * This tells DI to use XInput mapping (5 axes) instead of raw HID.
     * Also used later to skip WinExInput on main device (companion handles it). */
    DWORD functionMode = 0;
    {
        HKEY hFm;
        /* FunctionMode is read BEFORE device creation — ctx not yet available.
         * Use Controller0 as default (test app writes here for the primary device). */
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\HIDMaestro\\Controller0", 0, KEY_READ, &hFm) == ERROR_SUCCESS
            || RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\HIDMaestro", 0, KEY_READ, &hFm) == ERROR_SUCCESS) {
            DWORD val, sz = sizeof(val);
            if (RegQueryValueExW(hFm, L"FunctionMode", NULL, NULL, (LPBYTE)&val, &sz) == ERROR_SUCCESS)
                functionMode = val;
            RegCloseKey(hFm);
        }
        if (!functionMode)
            WdfFdoInitSetFilter(DeviceInit);
    }

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = EvtDeviceContextCleanup;

    /* SelfManagedIo callback: retry XUSB interface enable after device starts */
    {
        WDF_PNPPOWER_EVENT_CALLBACKS pnpCallbacks;
        WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpCallbacks);
        pnpCallbacks.EvtDeviceSelfManagedIoInit = EvtSelfManagedIoInit;
        WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnpCallbacks);
    }

    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    ctx = GetDeviceContext(device);
    RtlZeroMemory(ctx, sizeof(DEVICE_CONTEXT));
    ctx->Device = device;

    /* Initialize per-instance paths (registry key, shared file) from ControllerIndex */
    InitInstancePaths(ctx, device);

    /* Detect gamepad companion (root\HIDMaestroGamepad) — must NOT register WinExInput.
     * WinExInput on the companion creates a 3rd browser entry; only the main device
     * (or XUSB companion) should provide it. */
    BOOLEAN isGamepadCompanion = FALSE;
    {
        WCHAR hwId[256] = {0};
        ULONG hwIdLen = 0;
        if (NT_SUCCESS(WdfDeviceQueryProperty(device, DevicePropertyHardwareID,
                sizeof(hwId), hwId, &hwIdLen)) && hwId[0]) {
            /* Multi-string: scan all entries */
            for (WCHAR *p = hwId; *p; p += wcslen(p) + 1) {
                if (wcsstr(p, L"HIDMaestroGamepad")) {
                    isGamepadCompanion = TRUE;
                    break;
                }
            }
        }
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
    if (ctx->ReportDescriptorSize >= 2) {
        int colDepth = 0;
        BOOLEAN inFirstCollection = FALSE;
        ULONG ri = 0;
        ULONG end = ctx->ReportDescriptorSize - 1; /* keep room for [ri+1] read */
        while (ri < end) {
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
                if (colDepth == 0 && inFirstCollection) break;
            }
            if (inFirstCollection && prefix == 0x85 && (ri == 0 || ctx->ReportDescriptor[ri-1] != 0x09)) {
                ctx->FirstInputReportId = ctx->ReportDescriptor[ri + 1];
                break;
            }
            /* Advance past prefix + value bytes; clamp to end on overflow */
            ULONG step = (ULONG)(1 + bSize);
            if (ri + step <= ri) break; /* overflow guard */
            ri += step;
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

    /* XUSB interface is NEVER registered on the main device.
     * The XUSB companion (HMXInput.dll) handles all XInput IOCTLs. If we
     * registered XUSB here too, mshidumdf would corrupt the IOCTL path and
     * xinput1_4 would talk to the wrong device. */

    /* WinExInput is NEVER registered on the main HID device.
     *
     * Registering it here caused duplicate entries in the browser Gamepad
     * API: plain HID virtuals (DualSense, Stadia, custom profiles) showed up
     * once as a WGI "standard gamepad" (via this WinExInput registration)
     * AND once as a raw HID device (via RawInput). See issue #6.
     *
     * Xbox profiles with WGI detection needs: the XUSB companion
     * (HMXInput.dll) registers WinExInput with the same XI_00 reference
     * string. WGI fires GamepadAdded for the companion; the main HID is
     * seen only via RawInput by apps that want it. No duplicate.
     *
     * Plain HID virtuals (non-Xbox): browsers detect them via RawInput
     * directly. No WGI path is required or desired. Chrome applies its
     * standard-gamepad mapping heuristic based on VID:PID and descriptor
     * shape, not on WinExInput presence. */

    /* Create locks */
    status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &ctx->InputLock);
    if (!NT_SUCCESS(status)) return status;
    status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &ctx->OutputLock);
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

    /* Shared memory section for data injection (bypasses upper filter drivers) */
    ctx->SharedMemHandle = NULL;
    ctx->SharedMemPtr = NULL;
    ctx->SharedMemSeqNo = 0;

    /* Event-driven shared-input worker. The SDK creates
     * Global\HIDMaestroInputEvent<N> alongside the section and SetEvents
     * it per frame; we OpenEvent lazily in the worker (it may not exist
     * yet at EvtDeviceAdd time). StopEvent is our sentinel for shutdown.
     * Replaces the old 1 ms WdfTimer busy poll — see commit/diff for the
     * CPU-saturation root cause. */
    ctx->InputDataEvent = NULL;
    /* Create a NAMED StopEvent so external cleanup code (SDK's
     * RemoveAllVirtualControllers) can signal it after a force-kill,
     * breaking the deadlock where PnP waits for WUDFHost to release
     * and WUDFHost waits for our worker thread to exit. Without this,
     * cleanup of force-killed controllers takes ~28s (kernel query-
     * remove timeout per device). With it, cleanup signals the named
     * event, the worker exits, WUDFHost releases, and PnP removes
     * the device instantly.
     *
     * Uses a permissive NULL DACL so any elevated process can open it. */
    {
        SECURITY_ATTRIBUTES sa;
        SECURITY_DESCRIPTOR sd;
        InitializeSecurityDescriptor(&sd, SECURITY_DESCRIPTOR_REVISION);
        SetSecurityDescriptorDacl(&sd, TRUE, NULL, FALSE);
        sa.nLength = sizeof(sa);
        sa.lpSecurityDescriptor = &sd;
        sa.bInheritHandle = FALSE;
        ctx->StopEvent = CreateEventW(&sa, TRUE /* manual reset */, FALSE, ctx->StopEventName);
    }
    if (ctx->StopEvent != NULL) {
        /* CRITICAL: reset the StopEvent explicitly before starting the worker.
         * Windows' CreateEventW, when called on an existing named event, IGNORES
         * the initialState argument and returns a handle to the existing object
         * in whatever signal state it's in. On live-swap (teardown old context
         * + create new context on the same ControllerIndex), the OLD context's
         * EvtDeviceContextCleanup signaled this event (manual-reset → stays
         * signaled) to wake its worker. If any process still holds a handle to
         * the event when the new context runs — the SDK's
         * RemoveAllVirtualControllers utility keeps a handle briefly, and the
         * kernel object survives as long as any ref exists — then our
         * CreateEventW above hands us that still-signaled event. The worker
         * immediately sees WAIT_OBJECT_0 on StopEvent and returns 0: HID input
         * path dead for the rest of this session. (HMCOMPANION still runs its
         * own path, so XUSB / Guide still works — which is the exact partial-
         * hang symptom: only Guide flashes after a live-swap on Xbox 360.) */
        ResetEvent(ctx->StopEvent);
        ctx->WorkerThread = CreateThread(NULL, 0, SharedInputWorkerProc, ctx, 0, NULL);
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
         * The input buffer's low 16 bits identify which device-level string
         * the HID class wants. The values aren't the documented HID_STRING_ID_*
         * constants from the WDK headers (1/2/3) — under MsHidUmdf the HID
         * class actually sends 14/15/16 for manufacturer/product/serial. Both
         * the constant-form and the actual-observed-form are accepted in case
         * the mapping changes between Windows versions.
         *
         * For SERIAL (16 or 3) we return a UNIQUE per-instance serial built
         * from ControllerIndex. Without this, two virtual controllers that
         * share VID/PID/ProductString (e.g. 2× DualSense) get bucketed as
         * one device by SDL3/HIDAPI's hid_enumerate, which uses the serial
         * string as the disambiguator. PadForge has the same problem.
         *
         * For all other string IDs we return the product string — that's
         * what joy.cpl and games display.
         */
        PVOID  inBuf = NULL;
        size_t inBufSize = 0;
        ULONG  stringId = 0;

        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(ULONG), &inBuf, &inBufSize))) {
            stringId = *(ULONG*)inBuf & 0xFFFF;
        }

        BOOLEAN isSerial = (stringId == 16 || stringId == 3 /* HID_STRING_ID_ISERIALNUMBER */);
        if (isSerial && ctx->SerialStringBytes > 0) {
            status = RequestCopyFromBuffer(Request,
                ctx->SerialString, ctx->SerialStringBytes);
        } else {
            status = RequestCopyFromBuffer(Request,
                ctx->ProductString, ctx->ProductStringBytes);
        }
        break;
    }

    case IOCTL_HID_GET_INDEXED_STRING: {
        /* IOCTL_HID_GET_INDEXED_STRING is for raw HID descriptor string
         * indices (the iManufacturer/iProduct/iSerialNumber fields in the
         * HID device descriptor). Our descriptor doesn't declare any string
         * indices, so this path is rarely hit; HidClass routes the named
         * string queries through IOCTL_HID_GET_STRING instead, where we
         * handle the per-instance serial. Fall back to ProductString. */
        status = RequestCopyFromBuffer(Request,
            ctx->ProductString, ctx->ProductStringBytes);
        break;
    }

    case IOCTL_HID_READ_REPORT: {
        /*
         * HID class wants an input report.
         *
         * Critical: only complete IMMEDIATELY when the cached report is
         * NEWER than the last one we delivered. Otherwise pend in ManualQueue
         * and let ProcessSharedInput drain it when the SDK next signals.
         *
         * Without this seqno gate, every READ_REPORT completes instantly
         * with stale cached data, HIDClass immediately re-issues, and we
         * burn a core per device hammering the kernel↔user mode bridge.
         * GET_INPUT_REPORT (a different IOCTL, polled diagnostic path) is
         * unaffected — it still reads the cache directly.
         */
        WdfWaitLockAcquire(ctx->InputLock, NULL);

        if (ctx->InputReportReady && ctx->SharedMemSeqNo > ctx->LastDeliveredInputSeqNo) {
            status = RequestCopyFromBuffer(Request,
                ctx->InputReport, ctx->InputReportSize);
            ctx->LastDeliveredInputSeqNo = ctx->SharedMemSeqNo;
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
         * HID write path — used by HIDAPI / SDL3 / WriteFile to send output
         * reports (DualSense report 0x02 haptics+triggers+LED, generic LED
         * control, etc). The first byte is the HID Report ID (0 if the
         * descriptor uses no IDs). Forward to the output shared section so
         * the consumer (PadForge) can deliver it to real hardware.
         */
        PVOID  wrBuf;
        size_t wrSize;
        status = WdfRequestRetrieveInputBuffer(Request, 1, &wrBuf, &wrSize);
        if (!NT_SUCCESS(status)) break;

        {
            const UCHAR *p = (const UCHAR *)wrBuf;
            UCHAR  reportId = (wrSize > 0) ? p[0] : 0;
            const UCHAR *payload = (wrSize > 0) ? p + 1 : p;
            ULONG payloadLen = (ULONG)((wrSize > 0) ? wrSize - 1 : 0);
            PublishOutput(ctx, HIDMAESTRO_OUTPUT_SOURCE_HID_OUTPUT,
                          reportId, payload, payloadLen);
        }
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_UMDF_HID_SET_FEATURE: {
        /*
         * HidD_SetFeature path. DualSense and DualShock 4 use feature reports
         * for some configuration writes; some HID stacks route data here.
         * Forward to the output shared section tagged as a feature report so
         * the consumer can distinguish from regular output reports.
         */
        PVOID  featureBuf;
        size_t featureSize;

        status = WdfRequestRetrieveInputBuffer(Request, 1, &featureBuf, &featureSize);
        if (!NT_SUCCESS(status)) break;

        {
            const UCHAR *p = (const UCHAR *)featureBuf;
            UCHAR  reportId = (featureSize > 0) ? p[0] : 0;
            const UCHAR *payload = (featureSize > 0) ? p + 1 : p;
            ULONG payloadLen = (ULONG)((featureSize > 0) ? featureSize - 1 : 0);
            PublishOutput(ctx, HIDMAESTRO_OUTPUT_SOURCE_HID_FEATURE,
                          reportId, payload, payloadLen);
        }
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_UMDF_HID_GET_FEATURE:
        status = STATUS_NOT_SUPPORTED;
        break;

    case IOCTL_UMDF_HID_SET_OUTPUT_REPORT: {
        /*
         * The HID class delivers HidD_SetOutputReport here as a HID_XFER_PACKET.
         * dinput8 also routes generated PID effect output reports through this
         * IOCTL when a game calls IDirectInputEffect::Start. Forward the bytes
         * to the output shared section.
         *
         * UMDF2 input buffer layout for HID_XFER_PACKET-style IOCTLs is just
         * the raw report bytes (Report ID byte first if descriptor uses IDs).
         */
        PVOID  outBuf;
        size_t outBufSize;

        status = WdfRequestRetrieveInputBuffer(Request, 1, &outBuf, &outBufSize);
        if (!NT_SUCCESS(status)) break;

        {
            const UCHAR *p = (const UCHAR *)outBuf;
            UCHAR  reportId = (outBufSize > 0) ? p[0] : 0;
            const UCHAR *payload = (outBufSize > 0) ? p + 1 : p;
            ULONG payloadLen = (ULONG)((outBufSize > 0) ? outBufSize - 1 : 0);
            PublishOutput(ctx, HIDMAESTRO_OUTPUT_SOURCE_HID_OUTPUT,
                          reportId, payload, payloadLen);
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
         * Wire: [0-1]Version then XINPUT_CAPABILITIES at [2]:
         *   [2]Type [3]SubType [4-5]Flags [6-7]wButtons [8]LT [9]RT
         *   [10-17]4xi16 thumb maxes [18-19]wLeftMotorSpeed
         *   [20-21]wRightMotorSpeed [22-23]reserved.
         */
        UCHAR caps[24];
        RtlZeroMemory(caps, sizeof(caps));
        *(USHORT*)&caps[0] = 0x0101;  /* XUSBVersion */
        caps[2] = 0x01;                /* Type: XINPUT_DEVTYPE_GAMEPAD */
        caps[3] = 0x01;                /* SubType: XINPUT_DEVSUBTYPE_GAMEPAD */
        *(USHORT*)&caps[6] = 0xF7FF;  /* wButtons: DPAD+Start/Back/LS/RS+LB/RB+ABXY+Guide */
        caps[8] = 0xFF;                /* bLeftTrigger */
        caps[9] = 0xFF;                /* bRightTrigger */
        *(SHORT*)&caps[10] = 32767;    /* ThumbLX */
        *(SHORT*)&caps[12] = 32767;    /* ThumbLY */
        *(SHORT*)&caps[14] = 32767;    /* ThumbRX */
        *(SHORT*)&caps[16] = 32767;    /* ThumbRY */
        *(USHORT*)&caps[18] = 0xFFFF;  /* wLeftMotorSpeed */
        *(USHORT*)&caps[20] = 0xFFFF;  /* wRightMotorSpeed */
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
            if (btnLow & 0x40) buttons |= 0x0040; /* LS → LEFT_THUMB */
            if (btnLow & 0x80) buttons |= 0x0080; /* RS → RIGHT_THUMB */
            if (btnHigh & 0x01) buttons |= 0x0020; /* Back → BACK */
            if (btnHigh & 0x02) buttons |= 0x0010; /* Start → START */
            if (btnHigh & 0x40) buttons |= 0x0400; /* Guide (XInputGetStateEx) */
            switch (hat) {
                case 1: buttons |= 0x0001; break; case 2: buttons |= 0x0009; break;
                case 3: buttons |= 0x0008; break; case 4: buttons |= 0x000A; break;
                case 5: buttons |= 0x0002; break; case 6: buttons |= 0x0006; break;
                case 7: buttons |= 0x0004; break; case 8: buttons |= 0x0005; break;
            }
            /* GamepadState0101 layout (29 bytes):
             * 0x00: Version(2) 0x02: Status(1) 0x03: unk(2) 0x05: Packet(4)
             * 0x09: unk(2) — 2 reserved bytes before GAMEPAD
             * 0x0B: wButtons(2) 0x0D: bLT(1) 0x0E: bRT(1)
             * 0x0F: sLX(2) 0x11: sLY(2) 0x13: sRX(2) 0x15: sRY(2) */
            *(USHORT*)&state[0x0B] = buttons;
            state[0x0D] = (UCHAR)((*(USHORT*)&d[8] & 0x03FF) * 255 / 1023);
            state[0x0E] = (UCHAR)((*(USHORT*)&d[10] & 0x03FF) * 255 / 1023);
            *(SHORT*)&state[0x0F] = (SHORT)((int)(*(USHORT*)&d[0]) - 32768);       /* LX */
            *(SHORT*)&state[0x11] = (SHORT)(32767 - (int)(*(USHORT*)&d[2]));    /* LY */
            *(SHORT*)&state[0x13] = (SHORT)((int)(*(USHORT*)&d[4]) - 32768);    /* RX */
            *(SHORT*)&state[0x15] = (SHORT)(32767 - (int)(*(USHORT*)&d[6]));    /* RY */
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
        /* XInput rumble. The XUSB protocol defines a 5-byte vibration packet
         * here (the actual XINPUT_VIBRATION struct is 4 bytes wLeft+wRight,
         * but the wire format the driver receives is the 5-byte cmd packet).
         * Forward to the output shared section tagged as XInput so the
         * consumer can distinguish from HID output reports.
         *
         * Gate on VendorID == 0x045E (Microsoft / Xbox family). Non-Xbox
         * profiles (DualSense, DualShock, Switch, etc.) register WinExInput
         * on their main device for WGI GamepadAdded detection, and
         * GameInputSvc + the Xbox UI's Guide-haptic layer indiscriminately
         * open that interface and dispatch IOCTL_XUSB_SET_STATE through it.
         * For an Xbox-family profile that's legitimate (it's the XInput
         * rumble path). For a PS/Switch/etc. profile the caller's intent
         * is "rumble this gamepad" and we already emit the correct native
         * HID output report — publishing the duplicate XInput-source
         * packet to the SDK surfaces as a confusing "XInput rumble on a
         * DualShock 4" ghost, especially just after a live-swap from an
         * Xbox profile where the Xbox UI kept its slot-0 rumble state
         * latched. Silently succeed (so the caller's API returns OK and
         * nothing retries) but do not publish. */
        PVOID setBuf; size_t setBufSize;
        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, 1, &setBuf, &setBufSize))
            && setBufSize >= 1) {
            if (ctx->HidDeviceAttributes.VendorID == 0x045E) {
                PublishOutput(ctx, HIDMAESTRO_OUTPUT_SOURCE_XINPUT,
                              0, (const UCHAR *)setBuf, (ULONG)setBufSize);
            }
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
