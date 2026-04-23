/*
 * HIDMaestro Companion — UMDF2 driver for XUSB XInput + WinExInput.
 * Registers XUSB and WinExInput device interfaces.
 * Reads gamepad state from a per-instance pagefile-backed shared section
 * (Global\HIDMaestroInput<N>) — RAM-only, no disk I/O.
 * No HID, no filter mode — runs as System-class function driver.
 */

#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <wdf.h>

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD CompanionDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL CompanionIoControl;
EVT_WDF_OBJECT_CONTEXT_CLEANUP CompanionDeviceCleanup;
EVT_WDF_TIMER CompanionPumpTimer;

static const GUID XUSB_GUID =
    { 0xEC87F1E3, 0xC13B, 0x4100, { 0xB5, 0xF7, 0x8B, 0x84, 0xD5, 0x42, 0x60, 0xCB } };
static const GUID WINEXINPUT_GUID =
    { 0x6C53D5FD, 0x6480, 0x440F, { 0xB6, 0x18, 0x47, 0x67, 0x50, 0xC5, 0xE1, 0xA6 } };
/* From WGI's enumeration table (Windows.Gaming.Input.dll @ 0xAA9D0).
 * Semantically unknown but appears on Xbox Series BT HID children; Max's
 * external review hypothesized it may be the GIP device interface class
 * WGI uses for gamepad-class promotion of non-xinputhid Xbox paths.
 * Registering it on HMCOMPANION is additive and benign. */
static const GUID WGI_UNK1_GUID =
    { 0x08A7EE33, 0xA682, 0x49EE, { 0xB8, 0xBF, 0x3E, 0x41, 0xC9, 0x9D, 0xB3, 0xC0 } };

#define IOCTL_XUSB_GET_INFORMATION   0x80006000
#define IOCTL_XUSB_GET_CAPABILITIES  0x8000E004
#define IOCTL_XUSB_GET_LED_STATE     0x8000E008
#define IOCTL_XUSB_GET_STATE         0x8000E00C
#define IOCTL_XUSB_SET_STATE         0x8000A010
#define IOCTL_XUSB_WAIT_GUIDE        0x8000E014
#define IOCTL_XUSB_GET_BATTERY_INFO  0x8000E018
#define IOCTL_XUSB_GET_INFORMATION_EX 0x8000E3FC
#define IOCTL_XUSB_WAIT_FOR_INPUT    0x8000E3AC
#define IOCTL_XUSB_POWER_INFO        0x80006380

typedef struct _COMPANION_CTX {
    ULONG PacketCount;
    USHORT VendorId;
    USHORT ProductId;
    ULONG ControllerIndex;
    WCHAR ConfigRegPath[64];   /* e.g. L"SOFTWARE\HIDMaestro\Controller0" */
    WCHAR SharedMappingName[64]; /* e.g. L"Global\HIDMaestroInput0" */
    WCHAR OutputMappingName[64]; /* e.g. L"Global\HIDMaestroOutput0" */
    HANDLE SharedMemHandle;    /* OpenFileMapping handle (lazy) */
    PVOID SharedMemPtr;        /* MapViewOfFile pointer (lazy) */
    HANDLE OutputMemHandle;    /* CreateFileMapping handle for output (lazy) */
    PVOID OutputMemPtr;        /* MapViewOfFile RW pointer for output */
    ULONG OutputSeqNoLocal;    /* Last value we wrote (always increment) */
    ULONG OutputWriteCount;    /* Stale-detection: total writes since last re-open */
    ULONG LastGipSeqNo;        /* Stale-detection: last SeqNo seen from GIP shared memory */
    ULONG GipStaleCount;       /* Consecutive reads with unchanged SeqNo */
    /* Async XUSB input pump — holds pended IOCTL_XUSB_WAIT_FOR_INPUT
     * requests. WGI's XusbDevice::QueueInputBuffer (Windows.Gaming.Input.dll
     * @ 0x18006af0c) issues this IOCTL async via InputOutputIoctlAsync and
     * waits for the 29-byte XUSB state to arrive. Completing it synchronously
     * — or with an error — kills the pump, and Gamepad::SendControllerVibration
     * silently bails at the flag_0x184 gate because OnInputResumed never fires
     * on the WGI Gamepad's IGameControllerInputSink. See Ghidra decomp. */
    WDFQUEUE WaitForInputQueue;
    WDFTIMER PumpTimer;
} COMPANION_CTX, *PCOMPANION_CTX;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(COMPANION_CTX, GetCompanionCtx)

/* Append the decimal representation of a ULONG to a wide-string buffer.
 * Self-contained — companion doesn't link MSVCRT, no swprintf available.
 * Lets the section/registry-path builders support indices > 9 so the
 * test app and SDK aren't capped at 4 controllers. */
static VOID
AppendUlongDecimal(WCHAR *dest, ULONG value, SIZE_T maxChars)
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

#pragma pack(push, 1)
typedef struct {
    ULONG SeqNo;
    ULONG DataSize;
    UCHAR Data[64];
    UCHAR GipData[14];
} SHARED_INPUT;

/* Mirror of HIDMAESTRO_SHARED_OUTPUT in driver.h. Companion does not include
 * driver.h (it has its own context type and doesn't pull in HID headers), so
 * the layout is duplicated here. Keep in sync. */
typedef struct {
    volatile ULONG SeqNo;
    UCHAR  Source;       /* 0=HID output, 1=HID feature, 2=XInput rumble */
    UCHAR  ReportId;
    USHORT DataSize;
    UCHAR  Data[256];
} SHARED_OUTPUT;
#pragma pack(pop)

#define OUT_SOURCE_HID_OUTPUT  0
#define OUT_SOURCE_HID_FEATURE 1
#define OUT_SOURCE_XINPUT      2

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, CompanionDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

static BOOLEAN ReadGipData(PCOMPANION_CTX ctx, UCHAR gipOut[14])
{
    /* Lazy-open named section. RAM-only — no disk fallback. */
    if (ctx->SharedMemPtr == NULL) {
        HANDLE h = OpenFileMappingW(FILE_MAP_READ, FALSE, ctx->SharedMappingName);
        if (h == NULL) return FALSE;
        PVOID v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, sizeof(SHARED_INPUT));
        if (v == NULL) { CloseHandle(h); return FALSE; }
        ctx->SharedMemHandle = h;
        ctx->SharedMemPtr = v;
        ctx->LastGipSeqNo = 0;
        ctx->GipStaleCount = 0;
    }

    /* Seqlock read: retry if writer was mid-update */
    volatile SHARED_INPUT* src = (volatile SHARED_INPUT*)ctx->SharedMemPtr;
    ULONG seq1, seq2;
    int retries = 4;
    UCHAR tmp[14];
    do {
        seq1 = src->SeqNo;
        MemoryBarrier();
        for (int i = 0; i < 14; i++) tmp[i] = src->GipData[i];
        MemoryBarrier();
        seq2 = src->SeqNo;
    } while (seq1 != seq2 && --retries > 0);
    for (int i = 0; i < 14; i++) gipOut[i] = tmp[i];

    /* Stale-handle recovery (issue #1): if the SDK tore down and recreated
     * the shared memory section (RemoveAllVirtualControllers → Cleanup →
     * EnsureInputMapping), our cached handle points at the old destroyed
     * section. SeqNo will never advance. After 500 consecutive stale reads
     * (~2s at typical XInput polling rate), close and re-open. */
    if (seq1 == ctx->LastGipSeqNo) {
        if (++ctx->GipStaleCount > 500) {
            UnmapViewOfFile(ctx->SharedMemPtr); ctx->SharedMemPtr = NULL;
            CloseHandle(ctx->SharedMemHandle);  ctx->SharedMemHandle = NULL;
            ctx->GipStaleCount = 0;
            return FALSE; /* next call will lazy-open the fresh section */
        }
    } else {
        ctx->LastGipSeqNo = seq1;
        ctx->GipStaleCount = 0;
    }
    return TRUE;
}

void CompanionDeviceCleanup(_In_ WDFOBJECT Object)
{
    PCOMPANION_CTX ctx = GetCompanionCtx((WDFDEVICE)Object);
    if (ctx->PumpTimer) { WdfTimerStop(ctx->PumpTimer, TRUE); ctx->PumpTimer = NULL; }
    if (ctx->SharedMemPtr) { UnmapViewOfFile(ctx->SharedMemPtr); ctx->SharedMemPtr = NULL; }
    if (ctx->SharedMemHandle) { CloseHandle(ctx->SharedMemHandle); ctx->SharedMemHandle = NULL; }
    if (ctx->OutputMemPtr) { UnmapViewOfFile(ctx->OutputMemPtr); ctx->OutputMemPtr = NULL; }
    if (ctx->OutputMemHandle) { CloseHandle(ctx->OutputMemHandle); ctx->OutputMemHandle = NULL; }
}

/* Open the per-controller output section. The test app pre-creates it with
 * a permissive SDDL; we just attach. WUDFHost runs as LocalService and
 * cannot create Global\ sections itself (no SeCreateGlobalPrivilege).
 *
 * Stale-handle recovery (issue #2, output side of #1): if the SDK tears
 * down and recreates the output section between sessions, our cached
 * handle points at the old (destroyed) kernel object. Writes go nowhere.
 * Every 500 writes (~2s at typical XInput polling rate), close and
 * re-open to pick up the fresh section. */
static BOOLEAN EnsureOutputMapping(PCOMPANION_CTX ctx)
{
    /* Periodic re-open: close stale mapping every 500 writes */
    if (ctx->OutputMemPtr != NULL) {
        if (++ctx->OutputWriteCount < 500) return TRUE;
        /* Time to re-validate — close and re-open */
        UnmapViewOfFile(ctx->OutputMemPtr); ctx->OutputMemPtr = NULL;
        CloseHandle(ctx->OutputMemHandle);  ctx->OutputMemHandle = NULL;
        ctx->OutputWriteCount = 0;
    }

    HANDLE h = OpenFileMappingW(FILE_MAP_WRITE | FILE_MAP_READ, FALSE,
                                ctx->OutputMappingName);
    if (h == NULL) return FALSE;

    PVOID v = MapViewOfFile(h, FILE_MAP_WRITE | FILE_MAP_READ, 0, 0, sizeof(SHARED_OUTPUT));
    if (v == NULL) { CloseHandle(h); return FALSE; }

    ctx->OutputMemHandle = h;
    ctx->OutputMemPtr = v;
    ctx->OutputWriteCount = 0;
    return TRUE;
}

static VOID PublishOutput(PCOMPANION_CTX ctx, UCHAR source, UCHAR reportId,
                          const UCHAR *data, ULONG dataSize)
{
    if (!EnsureOutputMapping(ctx)) return;
    if (dataSize > sizeof(((SHARED_OUTPUT*)0)->Data))
        dataSize = sizeof(((SHARED_OUTPUT*)0)->Data);

    volatile SHARED_OUTPUT *dst = (volatile SHARED_OUTPUT *)ctx->OutputMemPtr;

    /* The companion has a single XUSB queue serializing IOCTLs per device,
     * so two PublishOutput calls on the same controller can't race here.
     * The driver and companion both write to the same section but they're
     * delivering DIFFERENT events at different times — the latest-wins
     * semantics are what the consumer wants either way. */
    dst->Source = source;
    dst->ReportId = reportId;
    dst->DataSize = (USHORT)dataSize;
    for (ULONG i = 0; i < dataSize; i++) dst->Data[i] = data[i];
    MemoryBarrier();
    /* Read-modify-write SeqNo from the section itself so we never go
     * backwards relative to whatever the driver wrote. */
    ULONG next = dst->SeqNo + 1;
    if (next <= ctx->OutputSeqNoLocal) next = ctx->OutputSeqNoLocal + 1;
    ctx->OutputSeqNoLocal = next;
    dst->SeqNo = next;
}

NTSTATUS CompanionDeviceAdd(_In_ WDFDRIVER Driver, _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_IO_QUEUE_CONFIG queueConfig;

    UNREFERENCED_PARAMETER(Driver);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, COMPANION_CTX);
    attributes.EvtCleanupCallback = CompanionDeviceCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    {
        PCOMPANION_CTX ctx = GetCompanionCtx(device);
        ctx->PacketCount = 0;
        ctx->VendorId = 0x045E;
        ctx->ProductId = 0x028E;
        ctx->ControllerIndex = 0;

        /* Read ControllerIndex from Device Parameters.
         * Try WDF API first, fall back to direct registry via device instance ID
         * parsed from WdfDeviceGetDeviceProperty. */
        {
            NTSTATUS rkStatus;
            WDFKEY hkDevice;
            rkStatus = WdfDeviceOpenRegistryKey(device, PLUGPLAY_REGKEY_DEVICE,
                KEY_READ, WDF_NO_OBJECT_ATTRIBUTES, &hkDevice);
            if (NT_SUCCESS(rkStatus))
            {
                UNICODE_STRING valueName;
                RtlInitUnicodeString(&valueName, L"ControllerIndex");
                ULONG idx = 0;
                if (NT_SUCCESS(WdfRegistryQueryULong(hkDevice, &valueName, &idx)))
                    ctx->ControllerIndex = idx;
                WdfRegistryClose(hkDevice);
            }
        }

        /* Build per-instance paths. Multi-digit indices supported. */
        {
            static const WCHAR prefix[] = L"SOFTWARE\\HIDMaestro\\Controller";
            SIZE_T cap = sizeof(ctx->ConfigRegPath) / sizeof(WCHAR);
            for (int i = 0; prefix[i]; i++) ctx->ConfigRegPath[i] = prefix[i];
            ctx->ConfigRegPath[(sizeof(prefix) / sizeof(WCHAR)) - 1] = L'\0';
            AppendUlongDecimal(ctx->ConfigRegPath, ctx->ControllerIndex, cap);
        }
        {
            static const WCHAR mapPrefix[] = L"Global\\HIDMaestroInput";
            SIZE_T cap = sizeof(ctx->SharedMappingName) / sizeof(WCHAR);
            for (int i = 0; mapPrefix[i]; i++) ctx->SharedMappingName[i] = mapPrefix[i];
            ctx->SharedMappingName[(sizeof(mapPrefix) / sizeof(WCHAR)) - 1] = L'\0';
            AppendUlongDecimal(ctx->SharedMappingName, ctx->ControllerIndex, cap);
        }
        {
            static const WCHAR outPrefix[] = L"Global\\HIDMaestroOutput";
            SIZE_T cap = sizeof(ctx->OutputMappingName) / sizeof(WCHAR);
            for (int i = 0; outPrefix[i]; i++) ctx->OutputMappingName[i] = outPrefix[i];
            ctx->OutputMappingName[(sizeof(outPrefix) / sizeof(WCHAR)) - 1] = L'\0';
            AppendUlongDecimal(ctx->OutputMappingName, ctx->ControllerIndex, cap);
        }

        /* Read VID/PID from per-instance registry (falls back to global) */
        HKEY hKey;
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, ctx->ConfigRegPath, 0, KEY_READ, &hKey) == ERROR_SUCCESS) {
            DWORD val, sz = sizeof(val);
            if (RegQueryValueExW(hKey, L"VendorId", NULL, NULL, (LPBYTE)&val, &sz) == ERROR_SUCCESS)
                ctx->VendorId = (USHORT)val;
            sz = sizeof(val);
            if (RegQueryValueExW(hKey, L"ProductId", NULL, NULL, (LPBYTE)&val, &sz) == ERROR_SUCCESS)
                ctx->ProductId = (USHORT)val;
            RegCloseKey(hKey);
        } else if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\HIDMaestro", 0, KEY_READ, &hKey) == ERROR_SUCCESS) {
            DWORD val, sz = sizeof(val);
            if (RegQueryValueExW(hKey, L"VendorId", NULL, NULL, (LPBYTE)&val, &sz) == ERROR_SUCCESS)
                ctx->VendorId = (USHORT)val;
            sz = sizeof(val);
            if (RegQueryValueExW(hKey, L"ProductId", NULL, NULL, (LPBYTE)&val, &sz) == ERROR_SUCCESS)
                ctx->ProductId = (USHORT)val;
            RegCloseKey(hKey);
        }
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = CompanionIoControl;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, NULL);
    if (!NT_SUCCESS(status)) return status;

    /* Manual-dispatch queue for pended IOCTL_XUSB_WAIT_FOR_INPUT. */
    {
        WDF_IO_QUEUE_CONFIG manualCfg;
        WDF_IO_QUEUE_CONFIG_INIT(&manualCfg, WdfIoQueueDispatchManual);
        PCOMPANION_CTX ctx = GetCompanionCtx(device);
        status = WdfIoQueueCreate(device, &manualCfg, WDF_NO_OBJECT_ATTRIBUTES,
                                  &ctx->WaitForInputQueue);
        if (!NT_SUCCESS(status)) return status;

        WDF_TIMER_CONFIG timerCfg;
        WDF_TIMER_CONFIG_INIT_PERIODIC(&timerCfg, CompanionPumpTimer, 8);
        WDF_OBJECT_ATTRIBUTES timerAttrs;
        WDF_OBJECT_ATTRIBUTES_INIT(&timerAttrs);
        timerAttrs.ParentObject = device;
        status = WdfTimerCreate(&timerCfg, &timerAttrs, &ctx->PumpTimer);
        if (!NT_SUCCESS(status)) return status;
        WdfTimerStart(ctx->PumpTimer, WDF_REL_TIMEOUT_IN_MS(8));
    }

    /* HMCOMPANION publishes ONLY GUID_DEVINTERFACE_XUSB. With our INF's
     * UpperFilters="xinputhid" tripwire, ProviderManagerWorker::OnPnpDeviceAdded
     * sees cVar3=true AND interface==XUSB {ec87f1e3}, and dispatches via the
     * XUSB path (LAB_18005f241). Publishing additional interfaces (WinExInput,
     * speculative WGI_UNK1) produces extra PnpDevice arrivals that confuse WGI
     * into classifying our one logical controller as multiple WGI entities —
     * the known hang mode documented in
     * memory:feedback-one-wgi-device-per-controller.md. */
    WdfDeviceCreateDeviceInterface(device, (LPGUID)&XUSB_GUID, NULL);

    return STATUS_SUCCESS;
}

static void CopyToRequest(WDFREQUEST Request, const void* data, size_t len)
{
    PVOID outBuf; size_t outLen;
    if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, len, &outBuf, &outLen))) {
        RtlCopyMemory(outBuf, data, len);
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, len);
    } else {
        WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL);
    }
}

/* Derive the XInput button/trigger/stick values from the current
 * shared-memory GIP frame. Output is a packed XINPUT_GAMEPAD-like tuple the
 * two 29-byte formatters below position at different offsets. */
static VOID DecodeGipToXInput(
    PCOMPANION_CTX ctx,
    USHORT *outButtons, UCHAR *outLT, UCHAR *outRT,
    SHORT *outLX, SHORT *outLY, SHORT *outRX, SHORT *outRY,
    BOOLEAN *outValid)
{
    *outButtons = 0; *outLT = 0; *outRT = 0;
    *outLX = 0; *outLY = 0; *outRX = 0; *outRY = 0;
    *outValid = FALSE;

    UCHAR gip[14];
    if (!ReadGipData(ctx, gip)) return;

    PUCHAR d = gip;
    UCHAR btnLow = d[12], btnHigh = d[13];
    UCHAR hat = (btnHigh >> 2) & 0x0F;
    USHORT buttons = 0;
    if (btnLow & 0x01) buttons |= 0x1000;
    if (btnLow & 0x02) buttons |= 0x2000;
    if (btnLow & 0x04) buttons |= 0x4000;
    if (btnLow & 0x08) buttons |= 0x8000;
    if (btnLow & 0x10) buttons |= 0x0100;
    if (btnLow & 0x20) buttons |= 0x0200;
    if (btnLow & 0x40) buttons |= 0x0040;
    if (btnLow & 0x80) buttons |= 0x0080;
    if (btnHigh & 0x01) buttons |= 0x0020;
    if (btnHigh & 0x02) buttons |= 0x0010;
    if (btnHigh & 0x40) buttons |= 0x0400;
    switch (hat) {
        case 1: buttons |= 0x0001; break; case 2: buttons |= 0x0009; break;
        case 3: buttons |= 0x0008; break; case 4: buttons |= 0x000A; break;
        case 5: buttons |= 0x0002; break; case 6: buttons |= 0x0006; break;
        case 7: buttons |= 0x0004; break; case 8: buttons |= 0x0005; break;
    }
    *outButtons = buttons;
    *outLT = (UCHAR)((*(USHORT*)&d[8] & 0x03FF) * 255 / 1023);
    *outRT = (UCHAR)((*(USHORT*)&d[10] & 0x03FF) * 255 / 1023);
    *outLX = (SHORT)((int)(*(USHORT*)&d[0]) - 32768);
    *outLY = (SHORT)(32767 - (int)(*(USHORT*)&d[2]));
    *outRX = (SHORT)((int)(*(USHORT*)&d[4]) - 32768);
    *outRY = (SHORT)(32767 - (int)(*(USHORT*)&d[6]));
    *outValid = TRUE;
}

/* 29-byte IOCTL_XUSB_GET_STATE response layout — this is what xinput1_4
 * parses. Empirically verified: buttons at state[0x0B], triggers at
 * state[0x0D]/[0x0E], sticks at state[0x0F..0x16]. DO NOT rearrange without
 * re-verifying XInput/DirectInput/HIDAPI. */
static VOID BuildXusbStateForGetState(PCOMPANION_CTX ctx, UCHAR state[29])
{
    RtlZeroMemory(state, 29);
    *(USHORT*)&state[0] = 0x0103;
    state[2] = 0x01;
    ctx->PacketCount++;
    *(DWORD*)&state[5] = ctx->PacketCount;

    USHORT buttons; UCHAR lt, rt; SHORT lx, ly, rx, ry; BOOLEAN valid;
    DecodeGipToXInput(ctx, &buttons, &lt, &rt, &lx, &ly, &rx, &ry, &valid);
    if (!valid) return;

    *(USHORT*)&state[0x0B] = buttons;
    state[0x0D] = lt;
    state[0x0E] = rt;
    *(SHORT*)&state[0x0F] = lx;
    *(SHORT*)&state[0x11] = ly;
    *(SHORT*)&state[0x13] = rx;
    *(SHORT*)&state[0x15] = ry;
}

/* 29-byte IOCTL_XUSB_WAIT_FOR_INPUT response layout — WGI's expected format.
 * Derived from Ghidra decomp of XusbDevice::ProcessInput (all-xusb.c:~9551):
 *   - state[2]  (this+0x28a): state indicator. 3 = RESUMED (triggers
 *     LAB_18006aac2 which toggles this+0x2c8 to 1, enabling subsequent
 *     UpdateInputSinks calls to dispatch OnInputResumed on newly-registered
 *     sinks — clearing Gamepad::flag_0x184).
 *   - state[9]  (this+0x291): reportId passed to sink.OnInputReceived.
 *   - state[10] (this+0x292): gate — if zero, dispatch is SKIPPED at
 *     all-xusb.c:9647. Real xusb22 puts the payload-size marker (0x14) here
 *     as the first byte of the 0x13-byte payload that is handed to
 *     sink.OnInputReceived(eventId, reportId=state[9], size=0x13,
 *     data=&state[10]).
 *   - state[11..] : XINPUT_GAMEPAD starts here in the SAME layout as
 *     IOCTL_XUSB_GET_STATE (wButtons[11-12], LT[13], RT[14], sticks[15-22]).
 *     Gamepad::OnInputReceived at 0x180029d30 reads data[2] = state[12]
 *     = wButtons.high to extract the GUIDE bit. */
static VOID BuildXusbStateForWaitInput(PCOMPANION_CTX ctx, UCHAR state[29])
{
    RtlZeroMemory(state, 29);
    *(USHORT*)&state[0] = 0x0103;
    state[2] = 0x03;                    /* RESUMED */
    ctx->PacketCount++;
    *(DWORD*)&state[5] = ctx->PacketCount;
    /* state[9]  = reportId passed to sink.OnInputReceived (caps non-zero per
     *   dispatch arg at all-xusb.c:9657).
     * state[10] = first byte of the 0x13-byte payload passed to the sink,
     *   AND the gate byte checked at all-xusb.c:9647. In XUSB internal
     *   convention this byte is the XINPUT_STATE packet-type marker (0x14
     *   = XINPUT packet). The XusbInputParser expects the remaining payload
     *   (wButtons, triggers, sticks) at data[1..] = state[11..22]. */
    state[9]  = 0x14;
    state[10] = 0x14;

    USHORT buttons; UCHAR lt, rt; SHORT lx, ly, rx, ry; BOOLEAN valid;
    DecodeGipToXInput(ctx, &buttons, &lt, &rt, &lx, &ly, &rx, &ry, &valid);
    if (!valid) return;

    /* XINPUT_GAMEPAD layout at state[11..22] as the parser expects when
     * offset=10 (from GetInputReportProperties) and data[0]=packet-type. */
    *(USHORT*)&state[11] = buttons;
    state[13] = lt;
    state[14] = rt;
    *(SHORT*)&state[15] = lx;
    *(SHORT*)&state[17] = ly;
    *(SHORT*)&state[19] = rx;
    *(SHORT*)&state[21] = ry;
}

/* Periodic pump for pended IOCTL_XUSB_WAIT_FOR_INPUT requests. WGI
 * (Windows.Gaming.Input.dll XusbDevice::QueueInputBuffer @ 0x18006af0c)
 * issues this IOCTL async and waits on the OVERLAPPED. If we never complete
 * it, ProcessInput -> UpdateInputSinks -> OnInputResumed never fires, and
 * Gamepad::SendControllerVibrationCommand silently bails at the flag_0x184
 * gate (non-GIP dispatch requires flag_0x184 == 0, which only OnInputResumed
 * sets). Completing one pended request per tick with the current 29-byte
 * state mirrors a real xusb22 device's behavior.
 *
 * 8ms period roughly matches a wired Xbox 360's 125Hz USB polling cadence.
 * Only one pending request is completed per tick; if WGI's pump is deep
 * enough it'll re-queue immediately and the next tick picks it up. */
EVT_WDF_TIMER CompanionPumpTimer;
VOID CompanionPumpTimer(_In_ WDFTIMER Timer)
{
    WDFDEVICE device = (WDFDEVICE)WdfTimerGetParentObject(Timer);
    PCOMPANION_CTX ctx = GetCompanionCtx(device);
    if (ctx->WaitForInputQueue == NULL) return;

    WDFREQUEST req;
    NTSTATUS s = WdfIoQueueRetrieveNextRequest(ctx->WaitForInputQueue, &req);
    if (!NT_SUCCESS(s)) return;  /* nothing pended */

    UCHAR state[29];
    BuildXusbStateForWaitInput(ctx, state);
    CopyToRequest(req, state, 29);
}

void CompanionIoControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
    PCOMPANION_CTX ctx = GetCompanionCtx(WdfIoQueueGetDevice(Queue));
    UNREFERENCED_PARAMETER(InputBufferLength);
    UNREFERENCED_PARAMETER(OutputBufferLength);

    /* Bounded diagnostic log. Writes to the SAME file xusbshim uses so a
     * single `Get-Content xusbshim_log.txt` captures BOTH paths' activity
     * during a Chromium playEffect() call — critical for deciding whether
     * WGI routes vibration via HMCOMPANION or via the HID-child xusbshim
     * interface (per external-AI review 2026-04-17). Cap matches xusbshim. */
    {
        static volatile LONG counter = 0;
        ULONG c = (ULONG)InterlockedIncrement(&counter);
        BOOLEAN isSetState = (IoControlCode == IOCTL_XUSB_SET_STATE);
        BOOLEAN isWait = (IoControlCode == IOCTL_XUSB_WAIT_FOR_INPUT);
        if (isSetState || isWait || c <= 400) {
            HANDLE h = CreateFileW(
                L"C:\\ProgramData\\HIDMaestro\\xusbshim_log.txt",
                FILE_APPEND_DATA, FILE_SHARE_READ, NULL,
                OPEN_ALWAYS, 0, NULL);
            if (h != INVALID_HANDLE_VALUE) {
                char buf[160];
                char *p = buf;
                const char tag[] = "[HMCOMP] IOCTL ";
                for (int i = 0; tag[i]; i++) *p++ = tag[i];
                for (int shift = 28; shift >= 0; shift -= 4) {
                    int n = (IoControlCode >> shift) & 0xF;
                    *p++ = (char)(n < 10 ? '0' + n : 'A' + n - 10);
                }
                /* For SET_STATE, also dump first 8 input bytes so we see
                 * the motor values WGI actually sent. */
                if (isSetState && InputBufferLength > 0) {
                    PVOID inBuf;
                    size_t inLen;
                    if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(
                            Request, 1, &inBuf, &inLen))) {
                        *p++ = ' '; *p++ = '[';
                        size_t n = inLen > 8 ? 8 : inLen;
                        const UCHAR *ib = (const UCHAR *)inBuf;
                        for (size_t i = 0; i < n; i++) {
                            int hi = (ib[i] >> 4) & 0xF;
                            int lo = ib[i] & 0xF;
                            *p++ = (char)(hi < 10 ? '0' + hi : 'A' + hi - 10);
                            *p++ = (char)(lo < 10 ? '0' + lo : 'A' + lo - 10);
                            if (i + 1 < n) *p++ = ' ';
                        }
                        *p++ = ']';
                    }
                }
                *p++ = '\r'; *p++ = '\n';
                DWORD w;
                WriteFile(h, buf, (DWORD)(p - buf), &w, NULL);
                CloseHandle(h);
            }
        }
    }

    switch (IoControlCode)
    {
    case IOCTL_XUSB_GET_INFORMATION: {
        /* OutDeviceInfos_t — 12 bytes (matches driver.c format exactly) */
        UCHAR info[12];
        RtlZeroMemory(info, sizeof(info));
        *(USHORT*)&info[0] = 0x0103;  /* XUSBVersion 0x0103. 2026-04-23 empirical: 0x0101 lets WGI poll state but not dispatch put_Vibration; 0x0103 matches physical Xbox 360 USB and HIDMaestro BT (both work) — flip to unblock SET_STATE dispatch. */
        info[2] = 0x01; /* device count — always 1 (each companion hosts one controller) */
        info[3] = 0x01; /* slot/capability marker — empirical xinputhid value;
                         * xusb22 may also set this. WGI may read it as a
                         * "device is usable" flag. Was 0x00 originally. */
        info[4] = 0x00;                /* unk2 — bit 7 clear = don't skip */
        *(USHORT*)&info[8] = ctx->VendorId;
        *(USHORT*)&info[10] = ctx->ProductId;
        CopyToRequest(Request, info, 12);
        break;
    }

    case IOCTL_XUSB_GET_CAPABILITIES: {
        /* GamepadCapabilities0101 — 24-byte wire: [0-1]Version, then the
         * XINPUT_CAPABILITIES struct starting at [2]:
         *   [2]Type [3]SubType [4-5]Flags [6-7]wButtons [8]LT [9]RT
         *   [10-17]sThumbLX/LY/RX/RY (4xi16) [18-19]wLeftMotorSpeed
         *   [20-21]wRightMotorSpeed [22-23]reserved.
         *
         * Prior bug: motors were at [22-23] and wButtons at [4-5]. WGI
         * read motor maxes from [18-21] -> zeros -> ForceFeedbackMotors=0
         * -> silently dropped put_Vibration. */
        /* Two distinct wire formats, verified 2026-04-23 against a live
         * physical Xbox 360 wired controller's xusb22.sys 36-byte response.
         * See driver/xusbshim.c:IOCTL_XUSB_GET_CAPABILITIES for the full
         * layout comment. When WGI calls GET_CAPABILITIES with output size
         * 36, it expects V2 extended format (header + V1 struct at byte 16).
         * Returning V1-shaped data in a 36-byte buffer makes WGI read
         * garbage at the V2 field offsets and the device looks malformed. */
        /* Motor max speed fields set to 0xFFFF to advertise rumble capability.
         * Previous zero values made WGI's QueryDeviceCapabilities report
         * supportedRumble=0x3 but mappedRumble=0x0 (motors detected but not
         * wired to an actuator). */
        UCHAR caps_v1[24] = {
            0x03, 0x01,
            0x00, 0x01,
            0xFF, 0xF7,
            0xFF, 0xFF,
            0xC0, 0xFF, 0xC0, 0xFF, 0xC0, 0xFF, 0xC0, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF,    /* LeftMotor, RightMotor max speeds */
            0x00, 0x00,                /* pad */
            0xFF, 0xFF                 /* trailer */
        };
        UCHAR caps_v2[36] = {
            0x03, 0x01,
            0x01, 0x01,
            0x0C, 0x00,
            (UCHAR)(ctx->VendorId & 0xFF), (UCHAR)((ctx->VendorId >> 8) & 0xFF),
            (UCHAR)(ctx->ProductId & 0xFF), (UCHAR)((ctx->ProductId >> 8) & 0xFF),
            0x10, 0x01,
            (UCHAR)(ctx->ControllerIndex & 0xFF), 0xFA, 0x34, 0x22,
            0xFF, 0xF7,
            0xFF,
            0xFF,
            0xC0, 0xFF, 0xC0, 0xFF, 0xC0, 0xFF, 0xC0, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF,    /* LeftMotor, RightMotor max speeds */
            0x00, 0x00,                /* pad */
            0xFF, 0xFF                 /* motor-present trailer */
        };
        PVOID outBuf; size_t outLen;
        if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, 24, &outBuf, &outLen))) {
            SIZE_T copy;
            if (outLen >= 36) {
                copy = 36;
                RtlCopyMemory(outBuf, caps_v2, copy);
            } else {
                copy = outLen > 24 ? 24 : outLen;
                RtlCopyMemory(outBuf, caps_v1, copy);
            }
            WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, copy);
        } else {
            WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL);
        }
        break;
    }

    case IOCTL_XUSB_GET_STATE: {
        UCHAR state[29];
        BuildXusbStateForGetState(ctx, state);
        CopyToRequest(Request, state, 29);
        break;
    }

    case IOCTL_XUSB_SET_STATE: {
        /* XInput rumble. Forward the wire-format vibration packet to the
         * output shared section as source=XInput. The consumer interprets
         * the bytes (typical packet is 5 bytes: cmd + size + lo motor +
         * hi motor + reserved, or 4 bytes for the raw XINPUT_VIBRATION).
         *
         * CRITICAL: a stale-HMCOMPANION-after-live-swap gate. When the user
         * live-swaps from Xbox 360 → DS4, TeardownController removes the
         * Xbox 360 HMCOMPANION, but devcon can return "Removed on reboot"
         * and the companion persists as a phantom still serving IOCTLs.
         * xinput1_4 / GameInputSvc keep sending IOCTL_XUSB_SET_STATE here;
         * without this gate we publish Source=XInput to the SAME shared
         * output section that the new DS4 is reading from — so the user's
         * DS4 SDK surfaces phantom XInput rumble packets. Fix: re-read the
         * CURRENT registry VendorId for this ControllerIndex every IOCTL.
         * If the index has been re-profiled to a non-Xbox controller, the
         * current VendorId will not be 0x045E and we silently drop. Cheap
         * — one small registry read, already done at init so the HKEY is
         * warm. Safer than sampling at init because live-swap DOES update
         * the registry before creating the new controller. */
        BOOLEAN isXboxNow = FALSE;
        {
            HKEY hKey;
            DWORD val = 0, sz = sizeof(val);
            if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, ctx->ConfigRegPath,
                              0, KEY_READ, &hKey) == ERROR_SUCCESS) {
                if (RegQueryValueExW(hKey, L"VendorId", NULL, NULL,
                                     (LPBYTE)&val, &sz) == ERROR_SUCCESS) {
                    if ((USHORT)val == 0x045E) isXboxNow = TRUE;
                }
                RegCloseKey(hKey);
            }
        }

        PVOID rumbleBuf; size_t rumbleSize;
        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, 1, &rumbleBuf, &rumbleSize))
            && rumbleSize >= 1) {
            if (isXboxNow) {
                PublishOutput(ctx, OUT_SOURCE_XINPUT, 0,
                              (const UCHAR*)rumbleBuf, (ULONG)rumbleSize);
            }
        }
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, 0);
        break;
    }

    case IOCTL_XUSB_POWER_INFO:
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, 0);
        break;

    case IOCTL_XUSB_GET_LED_STATE: {
        UCHAR led[3] = { 0, 0, 0x06 };
        CopyToRequest(Request, led, 3);
        break;
    }

    case IOCTL_XUSB_GET_BATTERY_INFO: {
        UCHAR batt[4] = { 0, 0x01, 0x03, 0 };
        CopyToRequest(Request, batt, 4);
        break;
    }

    case IOCTL_XUSB_WAIT_FOR_INPUT: {
        /* Pend the request; CompanionPumpTimer completes one per tick with
         * the current 29-byte XUSB state. WGI's XusbDevice::ProcessInput
         * expects exactly 0x1D bytes from the async read and re-queues after
         * each completion. */
        NTSTATUS s = WdfRequestForwardToIoQueue(Request, ctx->WaitForInputQueue);
        if (!NT_SUCCESS(s)) {
            WdfRequestComplete(Request, s);
        }
        break;
    }

    case IOCTL_XUSB_WAIT_GUIDE:
        /* No Guide-button async notification surface yet. xinput1_4 falls
         * back to GET_STATE polling when this returns INVALID_DEVICE_REQUEST. */
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        break;

    case IOCTL_XUSB_GET_INFORMATION_EX: {
        UCHAR infoEx[64];
        RtlZeroMemory(infoEx, sizeof(infoEx));
        *(USHORT*)&infoEx[0] = 0x0103;      /* Version 0x0103 — match GET_INFORMATION. */
        infoEx[2] = 0x01;
        infoEx[3] = 0x01;  /* slot/capability marker — mirror GET_INFORMATION */
        *(USHORT*)&infoEx[8] = ctx->VendorId;
        *(USHORT*)&infoEx[10] = ctx->ProductId;
        ULONG outLen = OutputBufferLength < 64 ? (ULONG)OutputBufferLength : 64;
        CopyToRequest(Request, infoEx, outLen);
        break;
    }

    default:
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        break;
    }
}
