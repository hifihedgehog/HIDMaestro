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
        static const WCHAR prefix[] = L"Global\\HIDMaestroPidState";
        SIZE_T cap = sizeof(ctx->PidStateMappingName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->PidStateMappingName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->PidStateMappingName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroInputEvent";
        SIZE_T cap = sizeof(ctx->InputEventName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->InputEventName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->InputEventName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroOutputEvent";
        SIZE_T cap = sizeof(ctx->OutputEventName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->OutputEventName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->OutputEventName, index, cap);
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

        /* Cache whether the descriptor declares a second collection with
         * Report ID 0x20 (0x85 0x20). The descriptor is fixed after this
         * point, so scanning it once here avoids a linear byte scan on
         * every ProcessSharedInput frame (~250 Hz per controller). */
        ctx->HasCol2Report = FALSE;
        if (ctx->ReportDescriptorSize > 130) {
            for (ULONG i = 0; i + 1 < ctx->ReportDescriptorSize; i++) {
                if (ctx->ReportDescriptor[i] == 0x85 && ctx->ReportDescriptor[i+1] == 0x20) {
                    ctx->HasCol2Report = TRUE;
                    break;
                }
            }
        }
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

    /* Read HidAttrPid (REG_DWORD). Overrides ProductID in HID attributes only.
     * Companion still reads ProductId for XUSB identity.
     * PID 0x0001 prevents GameInput/HIDAPI from claiming xinputhid devices,
     * so SDL3 falls through to XInput backend (correct identity). */
    dwordSize = sizeof(dwordVal);
    result = RegQueryValueExW(hKey, L"HidAttrPid", NULL,
                              &regType, (LPBYTE)&dwordVal, &dwordSize);
    if (result == ERROR_SUCCESS && regType == REG_DWORD) {
        ctx->HidDeviceAttributes.ProductID = (USHORT)dwordVal;
    }

    /* Switch Pro protocol responder (issue #33): keyed on the Nintendo
     * VID/PID family, per the spec's "hardcode the responder; the state
     * machine is protocol, not layout". The MAC is fabricated but stable
     * per controller index so a host that caches by MAC (Steam) sees the
     * same identity across sessions. Starts in full-report mode: real
     * hardware waits for `80 04`, but streaming immediately lets SDL's
     * GetInitialInputMode lock mode 0x30 from the first read
     * (SDL_hidapi_switch.c ReadInput report-ID sniff). */
    if (ctx->HidDeviceAttributes.VendorID == 0x057E
        && ctx->HidDeviceAttributes.ProductID == 0x2009) {
        ctx->SwitchProtocol = TRUE;
        ctx->SwitchInputMode = 0x30;
        ctx->SwitchMac[0] = 0x98; ctx->SwitchMac[1] = 0xB6;
        ctx->SwitchMac[2] = 0xE9; ctx->SwitchMac[3] = 0x48;
        ctx->SwitchMac[4] = 0x4D; ctx->SwitchMac[5] = (UCHAR)(0x30 + ctx->ControllerIndex);
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

/* Lazy-open of the PID FFB state section. Returns FALSE if the SDK
 * consumer hasn't created Global\HIDMaestroPidState<N> yet (e.g.
 * non-FFB consumer that never calls HMController.PublishPid*). The
 * GetFeature handler treats FALSE as "FFB not available" and returns
 * STATUS_NO_SUCH_DEVICE for the Pool report, matching vJoy's
 * convention for FFB-disabled devices. */
static BOOLEAN
EnsurePidStateMapping(_In_ PDEVICE_CONTEXT ctx)
{
    if (ctx->PidStateMemPtr != NULL) return TRUE;

    /* v1.1.39 — opened READ_WRITE because v1.1.37+ driver-side EBI
     * allocation, FreeEbi, and ResetPidState all WRITE the section
     * (BL_* fields, EbiAllocBitmap, State_*). Pre-1.1.39 we opened
     * FILE_MAP_READ which made every write an access violation that
     * terminated WUDFHost — surfaced to the caller as Win32 1291
     * "The process hosting the driver for this device has been
     * terminated." Combined with the IOCTL_UMDF_HID_* constants being
     * wrong (the case statements never matched the framework's
     * IoControlCode), the AV path didn't trigger before v1.1.39. */
    HANDLE h = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE,
                                ctx->PidStateMappingName);
    if (h == NULL) return FALSE;

    PVOID view = MapViewOfFile(h, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0,
                               sizeof(HIDMAESTRO_SHARED_PID_STATE));
    if (view == NULL) { CloseHandle(h); return FALSE; }

    ctx->PidStateMemHandle = h;
    ctx->PidStateMemPtr = view;
    return TRUE;
}

/* Seqlocked snapshot of the PID state section. Returns FALSE if the
 * mapping is not open or the snapshot couldn't stabilize across the
 * read window (extremely rare; only happens if the SDK is publishing
 * concurrently across multiple PublishPid* calls in flight on
 * different threads, which the API doesn't sanction). */
static BOOLEAN
ReadPidState(_In_ PDEVICE_CONTEXT ctx, _Out_ HIDMAESTRO_SHARED_PID_STATE *out)
{
    if (ctx->PidStateMemPtr == NULL && !EnsurePidStateMapping(ctx))
        return FALSE;

    volatile HIDMAESTRO_SHARED_PID_STATE *src =
        (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
    ULONG seq1, seq2;
    int retries = 4;
    do {
        seq1 = src->SeqNo;
        if (seq1 & 1) { /* publisher mid-write; brief retry */
            seq1 = src->SeqNo;
        }
        MemoryBarrier();
        out->PidEnabled                     = src->PidEnabled;
        out->BL_EffectBlockIndex            = src->BL_EffectBlockIndex;
        out->BL_LoadStatus                  = src->BL_LoadStatus;
        out->BL_RAMPoolAvailable            = src->BL_RAMPoolAvailable;
        out->Pool_RAMPoolSize               = src->Pool_RAMPoolSize;
        out->Pool_MaxSimultaneousEffects    = src->Pool_MaxSimultaneousEffects;
        out->Pool_MemoryManagement          = src->Pool_MemoryManagement;
        out->State_EffectBlockIndex         = src->State_EffectBlockIndex;
        out->State_Flags                    = src->State_Flags;
        MemoryBarrier();
        seq2 = src->SeqNo;
        if (seq1 == seq2 && !(seq1 & 1)) break;
    } while (--retries > 0);

    return retries > 0;
}

/* Driver-side EBI allocator. Picks the next free EBI from the
 * shared section's bitmap, updates BL_* fields atomically (with seqlock),
 * and increments the allocated count. Pool full → BL_LoadStatus = Full
 * (PID 1.0 §5.5 enum value 2), no bit set.
 *
 * Called synchronously from the IOCTL_UMDF_HID_SET_FEATURE handler when
 * dinput8 issues SetFeature(0x11 Create New Effect). The follow-up
 * GetFeature(0x12) on the same handshake reads the same BL_* fields,
 * which are now populated. Mirrors vJoy's `Ffb_GetNextFreeEffect` +
 * `pid->PIDBlockLoad.*` synchronous update inside `Ffb_ProcessPacket`.
 */
static VOID
AllocateEbiInBlockLoad(_In_ PDEVICE_CONTEXT ctx)
{
    volatile HIDMAESTRO_SHARED_PID_STATE *pid =
        (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
    if (pid == NULL) return;

    /* Cap effect count by Pool_MaxSimultaneousEffects (consumer-published).
     * 0 means consumer hasn't published a Pool yet, in which case we use
     * the bitmap width (32) as an upper bound. */
    UCHAR cap = pid->Pool_MaxSimultaneousEffects;
    if (cap == 0 || cap > 32) cap = 32;

    /* Find first clear bit in the lowest `cap` positions. Atomic OR ensures
     * concurrent allocations from another thread don't double-issue. */
    UCHAR allocatedEbi = 0;
    UCHAR loadStatus = 2; /* Full (PID 1.0 §5.5) */
    ULONG remainingPool = 0;

    for (UCHAR ebi = 1; ebi <= cap; ebi++) {
        ULONG bit = 1UL << (ebi - 1);
        ULONG prev = (ULONG)InterlockedOr((volatile LONG *)&pid->EbiAllocBitmap, (LONG)bit);
        if ((prev & bit) == 0) {
            /* We just transitioned this bit from 0 to 1 — we own EBI. */
            allocatedEbi = ebi;
            loadStatus = 1; /* Success */
            InterlockedIncrement((volatile LONG *)&pid->EbiAllocatedCount);
            break;
        }
        /* Bit was already set; another thread (or prior allocation) owns
         * it. Continue without unsetting. InterlockedOr is idempotent. */
    }

    /* RAMPoolAvailable: total pool minus a synthetic per-effect cost.
     * Real PID devices report bytes free; our consumer doesn't track the
     * underlying physical pool, so we approximate as
     *   (RAMPoolSize / cap) * (cap - allocatedCount)
     * which gives dinput8 a monotonic shrinkage as effects are created. */
    USHORT poolSize = pid->Pool_RAMPoolSize;
    ULONG allocCount = (ULONG)pid->EbiAllocatedCount;
    if (cap > 0 && allocCount <= cap) {
        remainingPool = ((ULONG)poolSize / cap) * (cap - allocCount);
    }

    /* Seqlock-write the BL_* fields. Odd SeqNo signals "writer in progress"
     * to the reader (ReadPidState retries). */
    ULONG seq = pid->SeqNo + 1; /* odd */
    pid->SeqNo = seq;
    MemoryBarrier();
    pid->BL_EffectBlockIndex   = allocatedEbi;
    pid->BL_LoadStatus         = loadStatus;
    pid->BL_RAMPoolAvailable   = (USHORT)remainingPool;
    MemoryBarrier();
    pid->SeqNo = seq + 1; /* even */
}

/* v1.1.38 — PID Device Reset (CTRL_DEVRST=4). Mirrors vJoy's
 * `Ffb_ResetPIDData` (vJoy-Brunner/driver/sys/hid.c:2627). Clears all
 * EBI allocations and resets the Block Load and State fields to safe
 * initial values. Pool fields are NOT reset — those are consumer-
 * published static config. dinput8's
 * IDirectInputDevice8::SendForceFeedbackCommand(DISFFC_RESET) arrives as
 * IOCTL_UMDF_HID_SET_OUTPUT_REPORT with Report ID 0x1C, Control byte 4. */
static VOID
ResetPidState(_In_ PDEVICE_CONTEXT ctx)
{
    volatile HIDMAESTRO_SHARED_PID_STATE *pid =
        (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
    if (pid == NULL) return;

    /* Clear bitmap atomically. */
    InterlockedExchange((volatile LONG *)&pid->EbiAllocBitmap, 0);
    InterlockedExchange((volatile LONG *)&pid->EbiAllocatedCount, 0);

    /* Seqlock-write the Block Load and State fields. */
    ULONG seq = pid->SeqNo + 1;
    pid->SeqNo = seq;
    MemoryBarrier();
    pid->BL_EffectBlockIndex   = 0;
    pid->BL_LoadStatus         = 0;
    pid->BL_RAMPoolAvailable   = pid->Pool_RAMPoolSize;
    pid->State_EffectBlockIndex = 0;
    pid->State_Flags           = 0;
    MemoryBarrier();
    pid->SeqNo = seq + 1;
}

/* Driver-side EBI free. Atomically clears the bit. No-op if the bit is
 * already clear (defensive against duplicate Block Free packets).
 * Mirrors vJoy's Ffb_BlockIndexFree + RAMPool update. */
static VOID
FreeEbi(_In_ PDEVICE_CONTEXT ctx, _In_ UCHAR ebi)
{
    if (ebi < 1 || ebi > 32) return;

    volatile HIDMAESTRO_SHARED_PID_STATE *pid =
        (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
    if (pid == NULL) return;

    ULONG bit = 1UL << (ebi - 1);
    ULONG prev = (ULONG)InterlockedAnd((volatile LONG *)&pid->EbiAllocBitmap, (LONG)~bit);
    if ((prev & bit) != 0) {
        InterlockedDecrement((volatile LONG *)&pid->EbiAllocatedCount);
    }
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
    if (DataSize > HIDMAESTRO_OUTPUT_SLOT_DATA_CAP)
        DataSize = HIDMAESTRO_OUTPUT_SLOT_DATA_CAP;

    /* Serialize writers (the queue dispatches IOCTLs in parallel, so two
     * threads can call PublishOutput concurrently). v1.1.40 ring buffer
     * uses per-slot seqlock for torn-write detection on the reader side;
     * the WdfWaitLock here ensures only one writer increments Head and
     * fills its slot at a time. */
    WdfWaitLockAcquire(ctx->OutputLock, NULL);

    if (!EnsureOutputMapping(ctx)) {
        WdfWaitLockRelease(ctx->OutputLock);
        return;
    }

    volatile HIDMAESTRO_SHARED_OUTPUT *dst =
        (volatile HIDMAESTRO_SHARED_OUTPUT *)ctx->OutputMemPtr;

    /* v1.1.40 ring buffer: SeqNo=0 reserved for "never written"; first
     * write is SeqNo=1. Slot index is (SeqNo - 1) % N. Writer:
     *   1) compute new SeqNo (Head + 1)
     *   2) write slot fields including Data[]
     *   3) MemoryBarrier
     *   4) write slot.SeqNo = new SeqNo (publishes the slot)
     *   5) MemoryBarrier + write Head = new SeqNo (publishes the ring head)
     *
     * Reader scans slots from LastSeen+1 to Head, validates each by
     * checking slot.SeqNo == expected, copies, re-checks SeqNo for
     * torn-write detection. If LastSeen+N < Head, oldest packets have
     * been overwritten — reader logs and skips ahead to Head-N+1. */
    /* Multi-producer reservation (audit of #34, pre-existing bug): the
     * main driver and the XUSB companion BOTH publish to this ring
     * (DirectInput FFB / HID output here, XInput rumble there), and the
     * old local-counter scheme let the two producers mint the same
     * sequence number and silently overwrite each other's slot.
     * InterlockedIncrement on the shared Head atomically reserves a
     * unique sequence for every writer in every process. The slot's
     * SeqNo store (fenced, below) remains the publish gate: the reader
     * validates slot.SeqNo == expected and simply retries a reserved-
     * but-unwritten slot on its next wake, so the reservation being
     * visible before the payload is harmless. This also removes the
     * stale-local-counter gap after an SDK section re-zero: the next
     * reservation continues from the live Head, whatever it is. */
    ULONG newSeq = (ULONG)InterlockedIncrement((volatile LONG *)&dst->Head);
    ULONG slotIdx = (newSeq - 1) % HIDMAESTRO_OUTPUT_RING_SLOTS;
    volatile HIDMAESTRO_OUTPUT_SLOT *slot = &dst->Slots[slotIdx];

    slot->Source = Source;
    slot->ReportId = ReportId;
    slot->DataSize = (USHORT)DataSize;
    for (ULONG i = 0; i < DataSize; i++) slot->Data[i] = Data[i];
    MemoryBarrier();
    slot->SeqNo = newSeq;

    WdfWaitLockRelease(ctx->OutputLock);

    /* Doorbell LAST (issue #34): Head is already published, so a reader
     * woken by this signal always sees the new packet. Signaling outside
     * the lock keeps the writer's hold time unchanged. SetEvent on an
     * already-set auto-reset event is a no-op, which coalesces bursts
     * exactly like the reader's drain-to-Head loop expects. */
    if (ctx->OutputSignalEvent) SetEvent(ctx->OutputSignalEvent);
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

/* ================================================================== */
/*  Switch Pro protocol responder (issue #33)                          */
/*                                                                     */
/*  Device-side implementation of the Nintendo Switch init +           */
/*  subcommand protocol, so SDL's HIDAPI_DriverSwitch / Steam /        */
/*  BetterJoy complete their handshake against the virtual pad.        */
/*  Grounded in the cloned references:                                 */
/*    - nxbt protocol.py (authoritative responder: ACK bytes, reply    */
/*      payloads, fabricated SPI content)                              */
/*    - dekuNukem USB-HID-Notes.md (0x80 init commands + 81 01 reply)  */
/*    - dekuNukem spi_flash_notes.md (calibration addresses)           */
/*    - SDL_hidapi_switch.c (the client under test: reply framing it   */
/*      validates, SPI address echo, calibration decode)               */
/* ================================================================== */

/* Battery/connection byte: high nibble 9 = full + charging (USB          */
/* powered), low nibble 1 = wired. SDL ignores it; Steam reads it.        */
#define SWITCH_BATTERY_CONN 0x91
/* Vibrator status byte. SDL ignores; nxbt rotates A0/B0/C0/90.           */
#define SWITCH_VIBRATOR     0xB0

/* Fabricated SPI flash image, served byte-wise so ANY (address, length)
 * read a host issues gets a consistent answer. 0xFF = unwritten flash,
 * the convention real controllers use for absent regions; this covers
 * the serial (0x6000, "no serial" per nxbt spi_read) and both user
 * calibration regions (0x8010 stick / 0x8026 IMU, no 0xB2A1/0xA1B2
 * magic), steering SDL to the factory data below. */
static UCHAR SwitchSpiByte(_In_ ULONG a)
{
    /* IMU factory calibration @0x6020 (24 bytes): accel origin 0,
     * accel coeff 0x4000, gyro origin 0, gyro coeff 0x343B. With zero
     * origins these coefficients reduce SDL's LoadIMUCalibration math
     * to exactly its own default scales (SWITCH_ACCEL_SCALE 4096,
     * SWITCH_GYRO_SCALE 14.2842), so the SDK's g / deg/s conversions
     * hold whether or not the host reads calibration. Coefficients per
     * nxbt sa_calibration. */
    static const UCHAR imuCal[24] = {
        0x00,0x00, 0x00,0x00, 0x00,0x00,
        0x00,0x40, 0x00,0x40, 0x00,0x40,
        0x00,0x00, 0x00,0x00, 0x00,0x00,
        0x3B,0x34, 0x3B,0x34, 0x3B,0x34,
    };
    /* Stick factory calibration @0x603D (9 bytes per stick). 12-bit
     * packed pairs; field ORDER differs per stick (SDL
     * LoadStickCalibration comment): Left = max/center/min,
     * Right = center/min/max. Values: center 0x800, range 0x600, so
     * the SDK packer's 2048 +/- 1536*v lands exactly on SDL's
     * normalized full scale. pack12(0x600,0x600)=00 06 60,
     * pack12(0x800,0x800)=00 08 80. */
    static const UCHAR stickCal[18] = {
        0x00,0x06,0x60,  0x00,0x08,0x80,  0x00,0x06,0x60,   /* left  */
        0x00,0x08,0x80,  0x00,0x06,0x60,  0x00,0x06,0x60,   /* right */
    };
    /* Colors @0x6050: body #323232, buttons #FFFFFF, grips absent. */
    static const UCHAR colors[12] = {
        0x32,0x32,0x32, 0xFF,0xFF,0xFF, 0xFF,0xFF,0xFF, 0xFF,0xFF,0xFF,
    };
    /* Six-axis + stick device parameters @0x6080/@0x6098, nxbt's Pro
     * Controller bytes (spi_read :414-443). */
    static const UCHAR sixAxisParams[6] = { 0x50,0xFD,0x00,0x00,0xC6,0x0F };
    static const UCHAR stickParams[18] = {
        0x0F,0x30,0x61, 0x96,0x30,0xF3, 0xD4,0x14,0x54,
        0x41,0x15,0x54, 0xC7,0x79,0x9C, 0x33,0x36,0x63,
    };

    if (a >= 0x6020 && a < 0x6020 + 24) return imuCal[a - 0x6020];
    if (a >= 0x603D && a < 0x603D + 18) return stickCal[a - 0x603D];
    if (a >= 0x6050 && a < 0x6050 + 12) return colors[a - 0x6050];
    if (a >= 0x6080 && a < 0x6080 + 6)  return sixAxisParams[a - 0x6080];
    if (a >= 0x6086 && a < 0x6086 + 18) return stickParams[a - 0x6086];
    if (a >= 0x6098 && a < 0x6098 + 18) return stickParams[a - 0x6098];
    return 0xFF;
}

/* Copy the latest consumer-submitted 0x30 body fields (buttons 3B +
 * sticks 6B) into a reply/stream frame at frame[3..11]. The SDK's
 * Switch packer writes the body as
 *   Data[0]=counter, [1]=battery, [2..4]=buttons, [5..10]=sticks,
 *   [11]=vibrator, [12..47]=IMU (3 frames x 12 bytes)
 * (SwitchProPacker.cs). Neutral = no buttons, both sticks centered
 * at 0x800 (packed 00 08 80). */
static VOID SwitchFillLatestState(_In_ PDEVICE_CONTEXT ctx, _Out_writes_(46) UCHAR *dst)
{
    HIDMAESTRO_SHARED_INPUT shared;
    RtlZeroMemory(dst, 46);
    dst[3] = 0x00; dst[4] = 0x08; dst[5] = 0x80;   /* left stick neutral  */
    dst[6] = 0x00; dst[7] = 0x08; dst[8] = 0x80;   /* right stick neutral */

    /* The WORKER thread owns the lazy TryOpenSharedMapping (via
     * ProcessSharedInput's ReadSharedInput); calling ReadSharedInput
     * here before the mapping exists would race two threads through
     * the open. Until the worker has mapped (no consumer yet), neutral
     * frames are the correct output anyway. */
    if (ctx->SharedMemPtr == NULL) return;

    if (ReadSharedInput(ctx, &shared) && shared.DataSize >= 11) {
        RtlCopyMemory(dst, shared.Data + 2, 9);    /* buttons + sticks */
        if (ctx->SwitchImuEnabled && shared.DataSize >= 48) {
            RtlCopyMemory(dst + 10, shared.Data + 12, 36); /* IMU x3 */
        }
    }
}

/* Queue a synthesized input report (0x81 / 0x21) and complete one
 * pending READ_REPORT with the oldest queued reply if HidClass has a
 * read parked. Ring + indices are guarded by InputLock, the same lock
 * the READ_REPORT dispatch takes. */
static VOID SwitchQueueReply(_In_ PDEVICE_CONTEXT ctx, _In_reads_(64) const UCHAR *report)
{
    WDFREQUEST pendingRead = NULL;
    UCHAR out[64];
    BOOLEAN haveOut = FALSE;

    WdfWaitLockAcquire(ctx->InputLock, NULL);
    if (ctx->SwitchReplyCount == HIDMAESTRO_SWITCH_REPLY_SLOTS) {
        /* Drop the oldest (host stopped reading; keep newest replies). */
        ctx->SwitchReplyRead = (ctx->SwitchReplyRead + 1) % HIDMAESTRO_SWITCH_REPLY_SLOTS;
        ctx->SwitchReplyCount--;
    }
    RtlCopyMemory(
        ctx->SwitchReplies[(ctx->SwitchReplyRead + ctx->SwitchReplyCount) % HIDMAESTRO_SWITCH_REPLY_SLOTS],
        report, 64);
    ctx->SwitchReplyCount++;

    if (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->ManualQueue, &pendingRead))) {
        RtlCopyMemory(out, ctx->SwitchReplies[ctx->SwitchReplyRead], 64);
        ctx->SwitchReplyRead = (ctx->SwitchReplyRead + 1) % HIDMAESTRO_SWITCH_REPLY_SLOTS;
        ctx->SwitchReplyCount--;
        haveOut = TRUE;
    }
    WdfWaitLockRelease(ctx->InputLock);

    if (haveOut) {
        NTSTATUS cs = RequestCopyFromBuffer(pendingRead, out, 64);
        WdfRequestComplete(pendingRead, NT_SUCCESS(cs) ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL);
    }
}

/* READ_REPORT fast path: serve a queued reply if one is pending.
 * Returns TRUE when the request was completed here. Caller holds
 * nothing; InputLock is taken inside. */
static BOOLEAN SwitchTryServeReply(_In_ PDEVICE_CONTEXT ctx, _In_ WDFREQUEST Request)
{
    UCHAR out[64];
    BOOLEAN haveOut = FALSE;

    WdfWaitLockAcquire(ctx->InputLock, NULL);
    if (ctx->SwitchReplyCount > 0) {
        RtlCopyMemory(out, ctx->SwitchReplies[ctx->SwitchReplyRead], 64);
        ctx->SwitchReplyRead = (ctx->SwitchReplyRead + 1) % HIDMAESTRO_SWITCH_REPLY_SLOTS;
        ctx->SwitchReplyCount--;
        haveOut = TRUE;
    }
    WdfWaitLockRelease(ctx->InputLock);

    if (haveOut) {
        NTSTATUS cs = RequestCopyFromBuffer(Request, out, 64);
        WdfRequestComplete(Request, NT_SUCCESS(cs) ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL);
        return TRUE;
    }
    return FALSE;
}

/* USB init commands, output report 0x80 (dekuNukem USB-HID-Notes.md).
 * payload[0] is the proprietary command id (report ID already
 * stripped by the caller). */
static VOID SwitchHandleProprietary(_In_ PDEVICE_CONTEXT ctx,
                                    _In_reads_(payloadLen) const UCHAR *payload,
                                    _In_ ULONG payloadLen)
{
    UCHAR reply[64];
    UCHAR cmd = (payloadLen > 0) ? payload[0] : 0;

    RtlZeroMemory(reply, sizeof(reply));
    reply[0] = 0x81;
    reply[1] = cmd;

    switch (cmd) {
    case 0x01: {
        /* Status: 81 01 00 <type> <MAC LSB-first>. Sample in
         * USB-HID-Notes.md: "81 01 00 02 57 30 ea 8a bb 7c" for a
         * right Joy-Con with MAC 7c:bb:8a:ea:30:57. Type 0x03 = Pro.
         * SDL re-reverses the bytes (BReadDeviceInfo USB path). */
        int i;
        reply[2] = 0x00;
        reply[3] = 0x03;
        for (i = 0; i < 6; i++) reply[4 + i] = ctx->SwitchMac[5 - i];
        SwitchQueueReply(ctx, reply);
        break;
    }
    case 0x02:  /* Handshake ack: 81 02. Load-bearing for BTrySetupUSB. */
    case 0x03:  /* Baud-switch ack: 81 03. SDL tolerates absence; cheap. */
        SwitchQueueReply(ctx, reply);
        break;
    case 0x04:  /* ForceUSB: no reply defined; keep streaming. */
    case 0x05:  /* ClearUSB: tolerated unanswered. */
    case 0x06:  /* ResetMCU: tolerated unanswered. */
    default:
        break;
    }
}

/* Subcommand request-reply, output report 0x01 -> input report 0x21.
 * Reply layout (SDL SwitchSubcommandInputPacket_t after the report ID):
 *   [1]=timer  [2]=battery/conn  [3..5]=buttons  [6..11]=sticks
 *   [12]=vibrator  [13]=ACK  [14]=subcommand id  [15..]=payload
 * ACK values per nxbt protocol.py (0x82 device info, 0x90 SPI, 0x83
 * trigger-elapsed, 0x82 vibration, 0x80 the rest). Unknown subcommands
 * get a generic 0x80 ACK with the id echoed: nxbt ignores unknowns to
 * avoid arguing with the CONSOLE, but SDL's WriteSubcommand retries an
 * unanswered subcommand for ~100 ms x 5 attempts, so a generic ACK is
 * the fast, loop-free answer for a PC host. */
static VOID SwitchHandleSubcommand(_In_ PDEVICE_CONTEXT ctx,
                                   _In_reads_(payloadLen) const UCHAR *payload,
                                   _In_ ULONG payloadLen)
{
    UCHAR reply[64];
    UCHAR subcmd;
    const UCHAR *args;
    ULONG argLen;
    UCHAR state[46];

    /* payload = [counter, rumble 8B, subcmd, args...] with the report
     * ID already stripped. */
    if (payloadLen < 10) return;
    subcmd = payload[9];
    args = payload + 10;
    argLen = payloadLen - 10;

    RtlZeroMemory(reply, sizeof(reply));
    SwitchFillLatestState(ctx, state);
    reply[0] = 0x21;
    WdfWaitLockAcquire(ctx->InputLock, NULL);
    reply[1] = ctx->SwitchTimer++;
    WdfWaitLockRelease(ctx->InputLock);
    reply[2] = SWITCH_BATTERY_CONN;
    RtlCopyMemory(reply + 3, state, 9);     /* buttons + sticks */
    reply[12] = SWITCH_VIBRATOR;
    reply[13] = 0x80;                        /* generic ACK default */
    reply[14] = subcmd;

    switch (subcmd) {
    case 0x02:  /* Request device info (nxbt set_device_info) */
        reply[13] = 0x82;
        reply[15] = 0x03;                    /* firmware 03.8B */
        reply[16] = 0x8B;
        reply[17] = 0x03;                    /* type: Pro Controller */
        reply[18] = 0x02;                    /* always 0x02 */
        RtlCopyMemory(reply + 19, ctx->SwitchMac, 6);
        reply[25] = 0x01;                    /* always 0x01 */
        reply[26] = 0x01;                    /* colors live in SPI */
        break;

    case 0x03:  /* Set input report mode */
        if (argLen >= 1 &&
            (args[0] == 0x30 || args[0] == 0x31 || args[0] == 0x3F)) {
            ctx->SwitchInputMode = args[0];
        }
        break;

    case 0x04:  /* Trigger buttons elapsed time (nxbt: ACK 0x83, zeros) */
        reply[13] = 0x83;
        break;

    case 0x10: { /* SPI flash read: echo address+length, serve the image */
        ULONG addr, i;
        UCHAR len;
        if (argLen < 5) break;
        addr = (ULONG)args[0] | ((ULONG)args[1] << 8)
             | ((ULONG)args[2] << 16) | ((ULONG)args[3] << 24);
        len = args[4];
        if (len > 0x1D) len = 0x1D;          /* dekuNukem: max SPI read */
        reply[13] = 0x90;
        RtlCopyMemory(reply + 15, args, 5);  /* SDL memcmp's this echo */
        for (i = 0; i < len; i++) reply[20 + i] = SwitchSpiByte(addr + i);
        break;
    }

    case 0x21:  /* Set NFC/IR MCU config (nxbt set_nfc_ir_config) */
        reply[13] = 0xA0;
        reply[15] = 0x01; reply[16] = 0x00; reply[17] = 0xFF;
        reply[18] = 0x00; reply[19] = 0x08; reply[20] = 0x00;
        reply[21] = 0x1B; reply[22] = 0x01;
        reply[48] = 0xC8;                    /* nxbt report[49], -1 for RID */
        break;

    case 0x40:  /* Enable/disable IMU streaming */
        if (argLen >= 1) ctx->SwitchImuEnabled = (args[0] != 0);
        break;

    case 0x48:  /* Enable vibration (nxbt: ACK 0x82). The arg is not
                 * tracked driver-side: rumble decode is SDK-side off the
                 * raw 0x01/0x10 publish, and player lights (0x30 below)
                 * likewise reach consumers through the raw 0x01 lane. */
        reply[13] = 0x82;
        break;

    case 0x06:  /* Set HCI state     */
    case 0x08:  /* Set shipment      */
    case 0x22:  /* Set NFC/IR state  */
    case 0x30:  /* Set player lights */
    case 0x38:  /* Set HOME light    */
    case 0x41:  /* IMU sensitivity   */
    default:    /* Unknown: generic ACK, keep streaming, never NACK. */
        break;
    }

    SwitchQueueReply(ctx, reply);
}

/* 60 Hz input report 0x30 streamer. Real Pro Controller cadence is
 * 15 ms; WaitForSingleObject's default timer resolution gives ~15.6 ms
 * which SDL treats identically. Serves ONE pending READ_REPORT per tick
 * (the one-report-per-frame discipline ProcessSharedInput documents)
 * and refreshes the GET_INPUT_REPORT cache. Exits on SwitchStreamStop. */
static DWORD WINAPI SwitchStreamProc(_In_ LPVOID Parameter)
{
    PDEVICE_CONTEXT ctx = (PDEVICE_CONTEXT)Parameter;

    for (;;) {
        if (WaitForSingleObject(ctx->SwitchStreamStop, 15) == WAIT_OBJECT_0)
            return 0;

        if (ctx->SwitchInputMode != 0x30 && ctx->SwitchInputMode != 0x31)
            continue;

        {
            UCHAR frame[64];
            UCHAR state[46];
            WDFREQUEST pendingRead = NULL;

            RtlZeroMemory(frame, sizeof(frame));
            SwitchFillLatestState(ctx, state);
            frame[0] = 0x30;
            frame[2] = SWITCH_BATTERY_CONN;
            RtlCopyMemory(frame + 3, state, 9);        /* buttons+sticks */
            frame[12] = SWITCH_VIBRATOR;
            RtlCopyMemory(frame + 13, state + 10, 36); /* IMU (zeros when disabled) */

            WdfWaitLockAcquire(ctx->InputLock, NULL);
            frame[1] = ctx->SwitchTimer++;
            /* Refresh the polled GET_INPUT_REPORT cache with the frame. */
            RtlCopyMemory(ctx->InputReport, frame, sizeof(frame));
            ctx->InputReportSize = sizeof(frame);
            ctx->InputReportReady = TRUE;
            WdfWaitLockRelease(ctx->InputLock);

            if (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->ManualQueue, &pendingRead))) {
                NTSTATUS cs = RequestCopyFromBuffer(pendingRead, frame, sizeof(frame));
                WdfRequestComplete(pendingRead, NT_SUCCESS(cs) ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL);
            }
        }
    }
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
    /* NOTE: SharedMemSeqNo is advanced LATER, under InputLock, together
     * with the InputReport cache write (see the completion block below).
     * Publishing it here would let a concurrent IOCTL_HID_READ_REPORT
     * observe the advanced seqno and complete with the PREVIOUS frame's
     * cached report tagged as the new one (stale-frame TOCTOU). The
     * local `seqNo` drives the rest of this call. */

    /* Switch Pro mode: the protocol stream owns pacing and completion.
     * SwitchStreamProc serves 0x30 frames at the wire's 15 ms cadence
     * (reading the same shared body via SwitchFillLatestState), and
     * subcommand replies preempt via SwitchQueueReply. Completing reads
     * here as well would serve SDK-cadence duplicates interleaved into
     * the protocol stream. Advance the cached seqno here (the Switch
     * path has no InputReport-cache TOCTOU: its reader is the stream
     * thread reading the view directly, never SharedMemSeqNo), so the
     * worker's stale-wakeup counter still resets on new frames. */
    if (ctx->SwitchProtocol) { ctx->SharedMemSeqNo = seqNo; return; }

    /* Build HID input report from shared file Data (native descriptor format).
     * Report MUST be exactly InputReportByteLength bytes — HidClass rejects
     * short reports.  Zero-fill first, then overlay actual data.
     *
     * v1.3.5 — vendor-blob mode-switch path. When ExtendedReportSize > 0
     * the SDK passes the FULL RID-included extended report (e.g. 78-byte
     * Sony BT Report 0x31 with CRC32). Pass through verbatim. */
    UCHAR inputReport[HIDMAESTRO_MAX_REPORT_SIZE];
    RtlZeroMemory(inputReport, sizeof(inputReport));

    ULONG dataLen = shared.DataSize;
    BOOLEAN hasReportId = (ctx->FirstInputReportId != 0);
    ULONG expectedSize = ctx->InputReportByteLength > 0 ? ctx->InputReportByteLength : 17;

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

    /* v1.3.5 — vendor-blob mode-switch path. When the SDK arms extended
     * emission (Sony BT post-handshake, ExtendedReportSize > 0), it has
     * already written the FULL RID-included extended report (e.g. 78-byte
     * Sony BT Report 0x31 with CRC32) into shared.ExtendedReportData.
     * Overwrite the legacy-encoded inputReport/inputSize with the
     * pass-through bytes — the legacy encode above is harmless work for
     * the few-microsecond window before the overwrite, which keeps the
     * function single-path-shape (Visual C++ codegen produces materially
     * different code under compilers' cache-line / branch-prediction
     * heuristics for nested if-else than for straight-line; an early
     * benchmark with the extended path as a leading branch showed a
     * ~6× input-rate regression on USB DS5 even when the branch was not
     * taken — see issue #21). For the common case (ExtendedReportSize=0)
     * this is one cmp + jz; on modern x86 with branch prediction it's
     * effectively free. */
    if (shared.ExtendedReportSize > 0
        && shared.ExtendedReportSize <= sizeof(shared.ExtendedReportData)
        && shared.ExtendedReportSize <= sizeof(inputReport))
    {
        RtlZeroMemory(inputReport, inputSize);
        RtlCopyMemory(inputReport, shared.ExtendedReportData, shared.ExtendedReportSize);
        inputSize = shared.ExtendedReportSize;
    }

    /* Build Col2 report (Report ID 0x20) with same gamepad data. The
     * descriptor scan for 0x85 0x20 is cached at config-read into
     * ctx->HasCol2Report (the descriptor is immutable after init), so
     * this hot path no longer rescans up to 4 KB every frame. */
    UCHAR col2Report[HIDMAESTRO_MAX_REPORT_SIZE];
    ULONG col2Size = 0;
    if (ctx->HasCol2Report) {
        col2Report[0] = 0x20; /* Report ID */
        /* Write separate trigger data: Brake(LT) and Accelerator(RT) as 16-bit values */
        USHORT lt16 = (USHORT)((*(USHORT*)&shared.GipData[8] & 0x03FF) * 65535 / 1023);
        USHORT rt16 = (USHORT)((*(USHORT*)&shared.GipData[10] & 0x03FF) * 65535 / 1023);
        *(USHORT*)&col2Report[1] = lt16;
        *(USHORT*)&col2Report[3] = rt16;
        col2Size = 5; /* Report ID + Brake(2) + Accel(2) */
    }

    /* Complete exactly ONE pending READ_REPORT per shared-memory state
     * change — not ALL queued requests. HidClass pre-queues READ_REPORTs
     * for performance; draining the entire queue with the same cached
     * report means one logical press from user mode becomes N HID
     * reports (where N = queue depth), each of which RawInput delivers
     * as a separate WM_INPUT. Consumers that handle RawInput per-message
     * (Start Menu / Xbox accessories UI) then register N navigation
     * events per single press — the triple/double-movement bug in
     * issue #8, empirically verified via InputSourceCounter probe
     * (5 WM_INPUTs from one hDevice per single press, state change
     * visible only once at XInput/RGC/UINav).
     *
     * One report per state change matches what real hardware does:
     * a physical Xbox controller produces exactly one HID report per
     * actual input change, not N reports to satisfy N queued reads.
     * Subsequent queued READ_REPORTs stay parked until the SDK
     * SubmitStates the next frame. */
    {
        WDFREQUEST pendingRead;
        if (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->ManualQueue, &pendingRead))) {
            /* Send Col1 (GIP, no Report ID) */
            NTSTATUS cs = RequestCopyFromBuffer(pendingRead, inputReport, inputSize);
            WdfRequestComplete(pendingRead, NT_SUCCESS(cs) ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL);

            /* Send Col2 (Report ID 0x20) if available — one Col2 read
             * paired with one Col1 read, still one logical "frame." */
            if (col2Size > 0 &&
                NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->ManualQueue, &pendingRead))) {
                cs = RequestCopyFromBuffer(pendingRead, col2Report, col2Size);
                WdfRequestComplete(pendingRead, NT_SUCCESS(cs) ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL);
            }
        }
        /* Store for polled GET_INPUT_REPORT, and bump the seqno gate
         * so the next IOCTL_HID_READ_REPORT for this seqno completes
         * directly (the queued ones have already been drained above). */
        WdfWaitLockAcquire(ctx->InputLock, NULL);
        RtlCopyMemory(ctx->InputReport, inputReport, inputSize);
        ctx->InputReportSize = inputSize;
        ctx->InputReportReady = TRUE;
        /* Publish the seqno LAST, under the same lock IOCTL_HID_READ_REPORT
         * takes, so a concurrent read sees either the old seqno (and parks)
         * or the new seqno paired with the freshly-written InputReport.
         * Advancing it in ProcessSharedInput's preamble (before this cache
         * write) was the stale-frame TOCTOU. */
        ctx->SharedMemSeqNo = seqNo;
        ctx->LastDeliveredInputSeqNo = seqNo;
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
            /* Close stale handles. Phase 1 will re-open fresh ones.
             * Unmapping the view first, then closing handles, keeps the
             * sequence symmetric with TryOpenSharedMapping. */
            CloseHandle(ctx->InputDataEvent);
            ctx->InputDataEvent = NULL;

            /* Switch Pro: the SEPARATE SwitchStreamProc thread reads
             * SharedMemPtr via SwitchFillLatestState at 15 ms cadence,
             * so the worker must NOT unmap the view out from under it
             * (use-after-unmap). For the standard HID path the worker is
             * the sole reader of the view, so recycling it here is safe.
             * The named section is a stable kernel object; the driver's
             * own handle keeps the view valid regardless of SDK restarts,
             * so keeping it mapped for the device lifetime is correct.
             * The view is unmapped exclusively in EvtDeviceContextCleanup
             * after both threads join, mirroring the VR transport's
             * publish-vs-unmap discipline. */
            if (!ctx->SwitchProtocol) {
                if (ctx->SharedMemPtr)    { UnmapViewOfFile(ctx->SharedMemPtr);    ctx->SharedMemPtr = NULL; }
                if (ctx->SharedMemHandle) { CloseHandle(ctx->SharedMemHandle);     ctx->SharedMemHandle = NULL; }
            }
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
     * while we unmap. SetEvent → 2-second join, which should be instant
     * (the worker wakes on the stop event and returns). */
    if (ctx->StopEvent) SetEvent(ctx->StopEvent);
    if (ctx->WorkerThread) {
        WaitForSingleObject(ctx->WorkerThread, 2000);
        CloseHandle(ctx->WorkerThread);
        ctx->WorkerThread = NULL;
    }

    /* Same discipline for the Switch 0x30 streamer: it reads
     * SharedMemPtr via SwitchFillLatestState, so it must be joined
     * before the unmaps below. */
    if (ctx->SwitchStreamStop) SetEvent(ctx->SwitchStreamStop);
    if (ctx->SwitchStreamThread) {
        WaitForSingleObject(ctx->SwitchStreamThread, 2000);
        CloseHandle(ctx->SwitchStreamThread);
        ctx->SwitchStreamThread = NULL;
    }
    if (ctx->SwitchStreamStop) { CloseHandle(ctx->SwitchStreamStop); ctx->SwitchStreamStop = NULL; }
    if (ctx->InputDataEvent) { CloseHandle(ctx->InputDataEvent); ctx->InputDataEvent = NULL; }
    if (ctx->StopEvent)      { CloseHandle(ctx->StopEvent);      ctx->StopEvent = NULL; }
    if (ctx->OutputSignalEvent) { CloseHandle(ctx->OutputSignalEvent); ctx->OutputSignalEvent = NULL; }

    if (ctx->SharedMemPtr) { UnmapViewOfFile(ctx->SharedMemPtr); ctx->SharedMemPtr = NULL; }
    if (ctx->SharedMemHandle) { CloseHandle(ctx->SharedMemHandle); ctx->SharedMemHandle = NULL; }
    if (ctx->OutputMemPtr) { UnmapViewOfFile(ctx->OutputMemPtr); ctx->OutputMemPtr = NULL; }
    if (ctx->OutputMemHandle) { CloseHandle(ctx->OutputMemHandle); ctx->OutputMemHandle = NULL; }
    if (ctx->PidStateMemPtr) { UnmapViewOfFile(ctx->PidStateMemPtr); ctx->PidStateMemPtr = NULL; }
    if (ctx->PidStateMemHandle) { CloseHandle(ctx->PidStateMemHandle); ctx->PidStateMemHandle = NULL; }
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

    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    ctx = GetDeviceContext(device);
    RtlZeroMemory(ctx, sizeof(DEVICE_CONTEXT));
    ctx->Device = device;

    /* Initialize per-instance paths (registry key, shared file) from ControllerIndex */
    InitInstancePaths(ctx, device);

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

        /* Output-ring doorbell (issue #34). Auto-reset, same permissive
         * NULL DACL. Created here (not SDK-side like the input event) so
         * the SDK can DETECT support by OpenEvent success: open works on
         * a new driver (block on the event, zero idle wakes), fails on an
         * old driver (fall back to the 8 ms poll). The companion creates
         * or opens the same name for its XUSB rumble publishes; CreateEventW
         * on an existing name returns the existing object, so creation
         * order between the two hosts doesn't matter. Non-fatal on
         * failure: PublishOutput skips the signal and the SDK's safety
         * timeout still drains the ring. */
        ctx->OutputSignalEvent = CreateEventW(&sa, FALSE /* auto reset */, FALSE,
                                              ctx->OutputEventName);
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
         * path dead for the rest of this session. (HIDMAESTRO still runs its
         * own path, so XUSB / Guide still works — which is the exact partial-
         * hang symptom: only Guide flashes after a live-swap on Xbox 360.) */
        ResetEvent(ctx->StopEvent);
        ctx->WorkerThread = CreateThread(NULL, 0, SharedInputWorkerProc, ctx, 0, NULL);
    }

    /* Switch Pro mode: start the 0x30 streamer. Unnamed stop event, so
     * none of the live-swap stale-signal hazards the named StopEvent
     * comment above documents apply; created-then-thread ordering per
     * the driver.c:1110 stop-event-before-worker rule. */
    if (ctx->SwitchProtocol) {
        ctx->SwitchStreamStop = CreateEventW(NULL, TRUE, FALSE, NULL);
        if (ctx->SwitchStreamStop != NULL) {
            ctx->SwitchStreamThread = CreateThread(NULL, 0, SwitchStreamProc, ctx, 0, NULL);
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
        /* Switch Pro mode: pending 0x81/0x21 replies preempt; otherwise
         * every read parks and the 60 Hz stream thread completes it on
         * its next tick, giving the wire the real controller's cadence
         * instead of the SDK's submit rate. */
        if (ctx->SwitchProtocol) {
            if (SwitchTryServeReply(ctx, Request)) {
                completeRequest = FALSE; /* completed inside */
                break;
            }
            status = WdfRequestForwardToIoQueue(Request, ctx->ManualQueue);
            if (NT_SUCCESS(status)) {
                completeRequest = FALSE;
            }
            break;
        }

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

            /* Switch Pro protocol interception (issue #33):
             *   0x80  USB init command -> synthesize the 0x81 reply;
             *         pure protocol noise, not published to consumers.
             *   0x01  rumble + subcommand -> synthesize the 0x21 reply
             *         AND publish raw (the rumble bytes ride it; the
             *         SDK decodes them onto OutputDecoded).
             *   0x10  rumble only -> publish raw, no reply (real
             *         hardware sends none and SDL never waits). */
            if (ctx->SwitchProtocol && reportId == 0x80) {
                SwitchHandleProprietary(ctx, payload, payloadLen);
                status = STATUS_SUCCESS;
                break;
            }
            if (ctx->SwitchProtocol && reportId == 0x01) {
                SwitchHandleSubcommand(ctx, payload, payloadLen);
            }

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
         *
         * v1.1.37 — PID FFB Create New Effect (0x11) and Block Free (0x1F)
         * are handled SYNCHRONOUSLY inside this IOCTL handler, before
         * forwarding to the consumer. Mirrors vJoy's `Ffb_ProcessPacket`
         * for `HID_ID_NEWEFREP+0x10`: kernel/driver allocates the EBI in
         * the same address space as the device-extension state so dinput8's
         * follow-up GetFeature(0x12) reads consistent state without
         * crossing the WUDFHost ↔ consumer process boundary. Issue #16.
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

            /* PID FFB report routing inside SetFeature. v1.1.39 covers
             * all three Set-direction PID handshake reports here because
             * the canonical PID descriptor declares 0x11/0x1B/0x1C as
             * BOTH Feature and Output direction — pid.dll/dinput8 may
             * route via either HidD_SetFeature OR HidD_SetOutputReport
             * depending on transport-mode global. The same handlers
             * exist in IOCTL_UMDF_HID_SET_OUTPUT_REPORT below; whichever
             * IOCTL the framework delivers, the driver acts the same.
             *
             * v1.3.7 — Report IDs are profile-specific. SDK writes the
             * descriptor-derived overrides into the shared section so
             * non-canonical PID layouts (Microsoft SideWinder uses
             * Set Effect=0x01, Block Free=0x0B, Device Control=0x0C)
             * route through the same handlers as the canonical
             * 0x11/0x1B/0x1C builder-emitted layouts. We MUST open the
             * shared section BEFORE reading the RID overrides — on
             * first IOCTL the section is unmapped and an unconditional
             * read would fall back to canonical IDs and miss
             * non-canonical RIDs entirely. EnsurePidStateMapping is
             * idempotent and ~free after the first successful map. */
            BOOLEAN haveMap = EnsurePidStateMapping(ctx);
            UCHAR createNewEffectRid = HIDMAESTRO_PID_CREATE_NEW_EFFECT_REPORT_ID;
            UCHAR blockFreeRid       = HIDMAESTRO_PID_BLOCK_FREE_REPORT_ID;
            UCHAR deviceControlRid   = HIDMAESTRO_PID_DEVICE_CONTROL_REPORT_ID;
            if (haveMap) {
                volatile HIDMAESTRO_SHARED_PID_STATE *sec =
                    (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
                if (sec->CreateNewEffectReportId) createNewEffectRid = sec->CreateNewEffectReportId;
                if (sec->BlockFreeReportId)       blockFreeRid       = sec->BlockFreeReportId;
                if (sec->DeviceControlReportId)   deviceControlRid   = sec->DeviceControlReportId;
            }
            if (reportId == createNewEffectRid && haveMap)
            {
                AllocateEbiInBlockLoad(ctx);
            }
            else if (reportId == blockFreeRid
                     && payloadLen >= 1
                     && haveMap)
            {
                FreeEbi(ctx, payload[0]);
            }
            else if (reportId == deviceControlRid
                     && payloadLen >= 1
                     && haveMap
                     && payload[0] == 4 /* CTRL_DEVRST */)
            {
                ResetPidState(ctx);
            }

            PublishOutput(ctx, HIDMAESTRO_OUTPUT_SOURCE_HID_FEATURE,
                          reportId, payload, payloadLen);
        }
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_UMDF_HID_GET_FEATURE: {
        /*
         * HidD_GetFeature path. DirectInput's PID FFB handshake reads
         * Block Load (0x12), PID Pool (0x13), and PID State (0x14) via
         * this IOCTL during dinput8!CDIEffect::CreateEffect. The SDK
         * consumer (e.g. PadForge) publishes the current values to the
         * shared PidState section via HMController.PublishPid*. We read
         * them via seqlock and pack into the IOCTL output buffer.
         *
         * Architectural model mirrors vJoy: device-extension state lives
         * on the user side of the kernel-user boundary because HIDMaestro
         * is UMDF2 (driver runs in WUDFHost user-mode). Synchronous read
         * from shared memory; no IPC round-trip, no timeout, no responder
         * thread.
         *
         * Backward compat: if no SDK has published (PidEnabled == 0) or
         * the section doesn't exist (consumer doesn't use FFB), Pool
         * returns STATUS_NO_SUCH_DEVICE and Block Load / State return
         * STATUS_NOT_SUPPORTED — matching vJoy's "FFB not enabled"
         * convention and HIDMaestro's pre-v1.1.35 behavior of
         * STATUS_NOT_SUPPORTED across all GetFeature calls.
         *
         * Block Load status: when BL_LoadStatus is 0 (unpublished, or
         * cleared by a DISFFC_RESET / Device Control reset), GetFeature
         * (0x12) reports LoadStatus = Error(3) in a valid 5-byte report
         * and returns STATUS_SUCCESS (see the Block Load wire-format block
         * below). v1.1.37 driver-side EBI allocation populates the BL
         * fields synchronously inside the SetFeature(0x11) handler, so a
         * real Create New Effect leaves LoadStatus at Success(1)/Full(2);
         * the Error clamp only surfaces in the brief window before any
         * Create New Effect arrives, or immediately after a reset.
         *
         * GetFeature(0x11 Create New Effect) is bidirectional in the
         * canonical PID descriptor. v1.1.37 mirrors vJoy: returns
         * STATUS_SUCCESS with the buffer untouched (vJoy's vJoyGetFeature
         * has no case for HID_ID_NEWEFREP and falls through with the
         * default STATUS_SUCCESS init). HIDMaestro pre-1.1.37 returned
         * STATUS_NOT_SUPPORTED, a divergence from vJoy that may have
         * contributed to issue #16.
         */
        PVOID  outBuf;
        size_t outSize;
        PVOID  inBuf;
        size_t inSize;

        status = WdfRequestRetrieveOutputBuffer(Request, 1, &outBuf, &outSize);
        if (!NT_SUCCESS(status)) break;

        if (outSize < 1) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        /* UMDF2 HID convention: the requested Report ID lives in the
         * IRP's INPUT buffer (byte 0); the OUTPUT buffer is what the
         * driver fills with the response. Pre-v1.3.5 read RID from
         * outBuf[0], which was always 0 because HidClass zeros the
         * output buffer before dispatch. The PID Block Load / Pool /
         * State paths happened to work despite the bug because dinput8
         * only reaches them via the SetFeature path (which does read
         * the input buffer); Get_Feature for a real Sony BT handshake
         * (0x05 / 0x09 / 0x20) was unreachable until this fix. */
        UCHAR reportId = 0;
        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, 1, &inBuf, &inSize))
            && inSize >= 1)
        {
            reportId = ((const UCHAR *)inBuf)[0];
        }
        if (reportId == 0) {
            /* Fallback for callers that put the RID in the output buffer
             * (some PID consumers). Keeps the pre-v1.3.5 PID dispatch
             * path working. */
            reportId = ((UCHAR *)outBuf)[0];
        }

        /* Sony BT extended-mode handshake. Real DualSense / DualShock 4
         * firmware switches from emitting Report 0x01 (basic) to Report
         * 0x31 / 0x11 (vendor blob with CRC32) once the host issues
         * specific Get_Feature reads:
         *   DS5: 0x05 (calibration), 0x09 (pairing/MAC), 0x20 (firmware)
         *   DS4: 0x02 (calibration), 0xA3 (firmware/HW info)
         * See Linux drivers/hid/hid-playstation.c dualsense_create /
         * dualshock4_get_calibration init flows. We serve minimal stubs
         * so consumers (Steam Input, dualsense-tester, ds.daidr.me,
         * Chrome's Gamepad API, WGI's GameInput DS4 protocol selector)
         * don't error on the read, AND publish a feature-read
         * notification to the SDK so the extendedReport.armOn watcher
         * can flip vendor-blob emission on. Notification is fired ONLY
         * for these arm IDs to keep the Get_Feature hot path cheap for
         * PID polling consumers (DInput / Steam Input).
         *
         * GATED on Sony VID (0x054C): only Sony BT profiles declare
         * extendedReport.armOn, so only they need these stubs. Without
         * the gate, feature IDs 0x02 (DS4 calibration) and 0xA3 collide
         * with unrelated profiles' declared feature reports. The Xbox 360
         * descriptor declares Feature Report ID 0x02 (driver.h), so a
         * non-Sony HidD_GetFeature(0x02) was getting a zero-filled DS4
         * calibration blob instead of falling through to the PID / vJoy
         * handlers below. */
        if (ctx->HidDeviceAttributes.VendorID == 0x054C
         && (reportId == 0x05 || reportId == 0x09 || reportId == 0x20
          || reportId == 0x02 || reportId == 0xA3)) {
            UCHAR *p = (UCHAR *)outBuf;
            ULONG stubSize = 0;
            if (reportId == 0x05) {
                /* DS5 calibration: 41 bytes */
                if (outSize < 41) { status = STATUS_BUFFER_TOO_SMALL; break; }
                stubSize = 41;
                RtlZeroMemory(p, stubSize);
                p[0] = reportId;
            } else if (reportId == 0x09) {
                /* DS5 pairing/MAC: 17 bytes */
                if (outSize < 17) { status = STATUS_BUFFER_TOO_SMALL; break; }
                stubSize = 17;
                RtlZeroMemory(p, stubSize);
                p[0] = reportId;
            } else if (reportId == 0x20) {
                /* DS5 firmware: 64 bytes. daidr's FactoryInfo.vue gates
                 * render on fwType ∈ {2,3} (byte 20 of the response).
                 * Seed fwType=2 so the panel renders without errors. */
                if (outSize < 64) { status = STATUS_BUFFER_TOO_SMALL; break; }
                stubSize = 64;
                RtlZeroMemory(p, stubSize);
                p[0] = reportId;
                p[20] = 0x02;
            } else if (reportId == 0x02) {
                /* DS4 calibration. USB = 37 bytes
                 * (DS4_FEATURE_REPORT_CALIBRATION_SIZE), BT = 41 bytes
                 * (DS4_FEATURE_REPORT_CALIBRATION_BLUETOOTH_SIZE — the
                 * extra 4 are CRC32; we don't compute a real CRC, the
                 * known DS4 consumers tolerate zeros the same way the
                 * existing DS5 stubs leak past CRC validation). The
                 * outSize parameter from the caller's BufferSize lets
                 * us serve whichever variant they asked for. */
                if (outSize >= 41) {
                    stubSize = 41;
                } else if (outSize >= 37) {
                    stubSize = 37;
                } else {
                    status = STATUS_BUFFER_TOO_SMALL;
                    break;
                }
                RtlZeroMemory(p, stubSize);
                p[0] = reportId;
            } else /* 0xA3 */ {
                /* DS4 firmware/HW info: 49 bytes
                 * (DS4_FEATURE_REPORT_FIRMWARE_INFO_SIZE). Same byte
                 * count on USB and BT. */
                if (outSize < 49) { status = STATUS_BUFFER_TOO_SMALL; break; }
                stubSize = 49;
                RtlZeroMemory(p, stubSize);
                p[0] = reportId;
            }
            WdfRequestSetInformation(Request, stubSize);
            PublishOutput(ctx, HIDMAESTRO_OUTPUT_SOURCE_HID_FEATURE_READ,
                          reportId, NULL, 0);
            status = STATUS_SUCCESS;
            break;
        }

        /* v1.3.7 — descriptor-driven PID Report ID overrides for
         * non-canonical PID layouts (Microsoft SideWinder etc.). Open
         * the shared section first so a fresh-IOCTL read on a profile
         * with non-canonical IDs picks up the SDK-published overrides
         * instead of falling back to canonical and missing the RID
         * entirely. EnsurePidStateMapping is idempotent. */
        (void)EnsurePidStateMapping(ctx);
        UCHAR createNewEffectRid = HIDMAESTRO_PID_CREATE_NEW_EFFECT_REPORT_ID;
        UCHAR poolRid            = HIDMAESTRO_PID_POOL_REPORT_ID;
        UCHAR stateRid           = HIDMAESTRO_PID_STATE_REPORT_ID;
        UCHAR blockLoadRid       = HIDMAESTRO_PID_BLOCK_LOAD_REPORT_ID;
        if (ctx->PidStateMemPtr != NULL) {
            volatile HIDMAESTRO_SHARED_PID_STATE *sec =
                (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
            if (sec->CreateNewEffectReportId) createNewEffectRid = sec->CreateNewEffectReportId;
            if (sec->PoolReportId)            poolRid            = sec->PoolReportId;
            if (sec->StateReportId)           stateRid           = sec->StateReportId;
            if (sec->BlockLoadReportId)       blockLoadRid       = sec->BlockLoadReportId;
        }

        /* GetFeature(Create New Effect) — silent success (mirrors vJoy).
         * Buffer left untouched. Doesn't gate on PidEnabled so this works
         * even for non-FFB consumers, matching vJoy's "always SUCCESS for
         * unhandled report IDs" fallthrough behavior. */
        if (reportId == createNewEffectRid) {
            status = STATUS_SUCCESS;
            break;
        }

        HIDMAESTRO_SHARED_PID_STATE pid = {0};
        BOOLEAN haveState = ReadPidState(ctx, &pid);

        if (!haveState || !pid.PidEnabled) {
            /* No SDK consumer publishing FFB state. Pool Report MUST
             * return STATUS_NO_SUCH_DEVICE so DInput definitively
             * concludes "device exists but no FFB" rather than
             * retrying. Other report IDs return NOT_SUPPORTED. */
            status = (reportId == poolRid)
                   ? STATUS_NO_SUCH_DEVICE
                   : STATUS_NOT_SUPPORTED;
            break;
        }

        UCHAR *p = (UCHAR *)outBuf;

        if (reportId == blockLoadRid) {
            /* Wire format: [reportId, EBI, LoadStatus, RAMPool LSB, RAMPool MSB].
             * If LoadStatus is zero (unpublished), report Error=3 per HID PID
             * spec §5.5 (LoadStatus enum: 1=Success, 2=Full, 3=Error). vJoy
             * uses Error=1 in its raw byte from a different historical
             * layout; dinput accepts both. */
            if (outSize < 5) { status = STATUS_BUFFER_TOO_SMALL; break; }
            p[0] = reportId;
            p[1] = pid.BL_EffectBlockIndex;
            p[2] = (pid.BL_LoadStatus >= 1 && pid.BL_LoadStatus <= 3)
                 ? pid.BL_LoadStatus : (UCHAR)3 /* Error */;
            p[3] = (UCHAR)(pid.BL_RAMPoolAvailable & 0xFF);
            p[4] = (UCHAR)((pid.BL_RAMPoolAvailable >> 8) & 0xFF);
            WdfRequestSetInformation(Request, 5);
            status = STATUS_SUCCESS;
        }
        else if (reportId == poolRid) {
            /* Wire format: [reportId, RAMPool LSB, RAMPool MSB, MaxSim, MemMgmt] */
            if (outSize < 5) { status = STATUS_BUFFER_TOO_SMALL; break; }
            p[0] = reportId;
            p[1] = (UCHAR)(pid.Pool_RAMPoolSize & 0xFF);
            p[2] = (UCHAR)((pid.Pool_RAMPoolSize >> 8) & 0xFF);
            p[3] = pid.Pool_MaxSimultaneousEffects;
            p[4] = pid.Pool_MemoryManagement;
            WdfRequestSetInformation(Request, 5);
            status = STATUS_SUCCESS;
        }
        else if (reportId == stateRid) {
            /* Wire format: [reportId, EBI, StateFlags] */
            if (outSize < 3) { status = STATUS_BUFFER_TOO_SMALL; break; }
            p[0] = reportId;
            p[1] = pid.State_EffectBlockIndex;
            p[2] = pid.State_Flags;
            WdfRequestSetInformation(Request, 3);
            status = STATUS_SUCCESS;
        }
        else {
            status = STATUS_NOT_SUPPORTED;
        }
        break;
    }

    case IOCTL_UMDF_HID_SET_OUTPUT_REPORT: {
        /*
         * The HID class delivers HidD_SetOutputReport here as a HID_XFER_PACKET.
         * dinput8 also routes generated PID effect output reports through this
         * IOCTL when a game calls IDirectInputEffect::Start. Forward the bytes
         * to the output shared section.
         *
         * UMDF2 input buffer layout for HID_XFER_PACKET-style IOCTLs is just
         * the raw report bytes (Report ID byte first if descriptor uses IDs).
         *
         * v1.1.38 — handle PID Block Free (0x1B) and Device Control (0x1C)
         * here. Block Free is Output direction in the canonical PID
         * descriptor (vJoy-Brunner/driver/sys/hidReportDescSingle.h:558,
         * `0x91, 0x02` items). Device Control is Output direction
         * (hidReportDescSingle.h:571) and on Control=4 (CTRL_DEVRST) we
         * reset PID state — mirrors vJoy hid.c:2849.
         *
         * Report ID 0x11 is intentionally NOT handled here. 0x11 is
         * dual-purpose in the PID descriptor: Feature direction = Create
         * New Effect (allocates EBI; handled in SetFeature), Output
         * direction = Set Effect (selects an EXISTING EBI to start; no
         * allocation). v1.1.37 mistakenly allocated an EBI for both,
         * leaking one EBI per Set Effect. Fixed in v1.1.38.
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

            /* Switch Pro protocol interception, mirroring the
             * IOCTL_HID_WRITE_REPORT branch verbatim: SDL/Steam write
             * through WriteFile -> WRITE_REPORT today, but any host
             * routing via HidD_SetOutputReport lands here instead, and
             * an unmirrored sibling would stall that host's handshake
             * with no diagnostic. */
            if (ctx->SwitchProtocol && reportId == 0x80) {
                SwitchHandleProprietary(ctx, payload, payloadLen);
                status = STATUS_SUCCESS;
                break;
            }
            if (ctx->SwitchProtocol && reportId == 0x01) {
                SwitchHandleSubcommand(ctx, payload, payloadLen);
            }

            BOOLEAN haveOutMap = EnsurePidStateMapping(ctx);
            UCHAR blockFreeRid     = HIDMAESTRO_PID_BLOCK_FREE_REPORT_ID;
            UCHAR deviceControlRid = HIDMAESTRO_PID_DEVICE_CONTROL_REPORT_ID;
            if (haveOutMap) {
                volatile HIDMAESTRO_SHARED_PID_STATE *sec =
                    (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
                if (sec->BlockFreeReportId)     blockFreeRid     = sec->BlockFreeReportId;
                if (sec->DeviceControlReportId) deviceControlRid = sec->DeviceControlReportId;
            }
            if (reportId == blockFreeRid
                && payloadLen >= 1
                && haveOutMap)
            {
                FreeEbi(ctx, payload[0]);
            }
            else if (reportId == deviceControlRid
                     && payloadLen >= 1
                     && haveOutMap
                     && payload[0] == 4 /* CTRL_DEVRST */)
            {
                ResetPidState(ctx);
            }

            PublishOutput(ctx, HIDMAESTRO_OUTPUT_SOURCE_HID_OUTPUT,
                          reportId, payload, payloadLen);
        }
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_UMDF_HID_GET_INPUT_REPORT: {
        /*
         * v1.1.38 — vJoy returns STATUS_NOT_SUPPORTED for this IOCTL
         * unconditionally (vJoy-Brunner/driver/sys/hid.c:244). HIDMaestro
         * pre-1.1.38 returned 17 zeroed bytes via a default-size fallback,
         * which dinput8 would parse against descriptor-declared report
         * lengths (e.g. PID State at 3 bytes) and AV when the lengths
         * disagreed. Strong AV candidate per the v1.1.38 audit.
         *
         * Fix: if the caller asks for a specific report ID we know about
         * (PID State 0x14), return correctly-sized bytes from shared
         * state. For everything else, return STATUS_NOT_SUPPORTED to
         * mirror vJoy. dinput8 falls back to other paths (or accepts
         * the device as unread-state) on NOT_SUPPORTED.
         */
        PVOID  inBuf;
        size_t inBufSize;

        status = WdfRequestRetrieveOutputBuffer(Request, 1, &inBuf, &inBufSize);
        if (!NT_SUCCESS(status)) break;

        if (inBufSize < 1) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        UCHAR inReportId = ((UCHAR *)inBuf)[0];

        /* PID State Report (Input direction in the canonical descriptor —
         * vJoy hidReportDescSingle.h:752 declares 0x14 with embedded
         * Input items inside a Feature collection). dinput8 may issue
         * HidD_GetInputReport(StateRid) during CreateEffect to read State.
         * v1.3.7 — match against SDK-published State RID with canonical
         * fallback so non-canonical PID layouts (SideWinder etc.) resolve.
         * Open shared section first; otherwise the very first IOCTL read
         * before any other handler ran would miss the override. */
        (void)EnsurePidStateMapping(ctx);
        UCHAR stateRid = HIDMAESTRO_PID_STATE_REPORT_ID;
        if (ctx->PidStateMemPtr != NULL) {
            volatile HIDMAESTRO_SHARED_PID_STATE *sec =
                (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
            if (sec->StateReportId) stateRid = sec->StateReportId;
        }
        if (inReportId == stateRid) {
            HIDMAESTRO_SHARED_PID_STATE pid = {0};
            BOOLEAN haveState = ReadPidState(ctx, &pid);
            if (inBufSize < 3) { status = STATUS_BUFFER_TOO_SMALL; break; }
            ((UCHAR *)inBuf)[0] = inReportId;
            ((UCHAR *)inBuf)[1] = haveState ? pid.State_EffectBlockIndex : 0;
            ((UCHAR *)inBuf)[2] = haveState ? pid.State_Flags : 0;
            WdfRequestSetInformation(Request, 3);
            status = STATUS_SUCCESS;
            break;
        }

        /* Standard input report (Report ID 1 or no-RID descriptors): return
         * the latest cached input frame the SDK published. */
        WdfWaitLockAcquire(ctx->InputLock, NULL);
        if (ctx->InputReportReady
            && (inReportId == 0x01 || inReportId == 0x00))
        {
            status = RequestCopyFromBuffer(Request,
                ctx->InputReport, ctx->InputReportSize);
            WdfWaitLockRelease(ctx->InputLock);
            break;
        }
        WdfWaitLockRelease(ctx->InputLock);

        /* Anything else: vJoy parity — STATUS_NOT_SUPPORTED. */
        status = STATUS_NOT_SUPPORTED;
        break;
    }

    case IOCTL_HID_ACTIVATE_DEVICE:
    case IOCTL_HID_DEACTIVATE_DEVICE:
    case IOCTL_HID_SEND_IDLE_NOTIFICATION_REQUEST:
        status = STATUS_SUCCESS;
        break;

    /* XUSB IOCTLs (IOCTL_XUSB_GET_INFORMATION/GET_CAPABILITIES/GET_STATE/
     * SET_STATE/GET_LED_STATE/GET_BATTERY_INFO/POWER_INFO) used to be
     * handled here. Removed in v1.3.4 — the main HID device never
     * registers the XUSB interface (see WdfDeviceCreateDeviceInterface
     * comment further up); xinput1_4 talks exclusively to the XUSB
     * companion (HMXInput.dll), which has its own handlers in
     * companion.c. The handlers here were unreachable. */

    default:
        status = STATUS_NOT_IMPLEMENTED;
        break;
    }

    if (completeRequest) {
        WdfRequestComplete(Request, status);
    }
}
