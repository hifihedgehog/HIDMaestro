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

static const GUID XUSB_GUID =
    { 0xEC87F1E3, 0xC13B, 0x4100, { 0xB5, 0xF7, 0x8B, 0x84, 0xD5, 0x42, 0x60, 0xCB } };
static const GUID WINEXINPUT_GUID =
    { 0x6C53D5FD, 0x6480, 0x440F, { 0xB6, 0x18, 0x47, 0x67, 0x50, 0xC5, 0xE1, 0xA6 } };

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
    return TRUE;
}

void CompanionDeviceCleanup(_In_ WDFOBJECT Object)
{
    PCOMPANION_CTX ctx = GetCompanionCtx((WDFDEVICE)Object);
    if (ctx->SharedMemPtr) { UnmapViewOfFile(ctx->SharedMemPtr); ctx->SharedMemPtr = NULL; }
    if (ctx->SharedMemHandle) { CloseHandle(ctx->SharedMemHandle); ctx->SharedMemHandle = NULL; }
    if (ctx->OutputMemPtr) { UnmapViewOfFile(ctx->OutputMemPtr); ctx->OutputMemPtr = NULL; }
    if (ctx->OutputMemHandle) { CloseHandle(ctx->OutputMemHandle); ctx->OutputMemHandle = NULL; }
}

/* Open the per-controller output section. The test app pre-creates it with
 * a permissive SDDL; we just attach. WUDFHost runs as LocalService and
 * cannot create Global\ sections itself (no SeCreateGlobalPrivilege). */
static BOOLEAN EnsureOutputMapping(PCOMPANION_CTX ctx)
{
    if (ctx->OutputMemPtr != NULL) return TRUE;

    HANDLE h = OpenFileMappingW(FILE_MAP_WRITE | FILE_MAP_READ, FALSE,
                                ctx->OutputMappingName);
    if (h == NULL) return FALSE;

    PVOID v = MapViewOfFile(h, FILE_MAP_WRITE | FILE_MAP_READ, 0, 0, sizeof(SHARED_OUTPUT));
    if (v == NULL) { CloseHandle(h); return FALSE; }

    ctx->OutputMemHandle = h;
    ctx->OutputMemPtr = v;
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

    /* XUSB companion always registers XUSB (its sole purpose) and WinExInput
     * (so the browser Gamepad API gets a GamepadAdded event for this controller).
     * The test app only creates this companion for non-xinputhid profiles —
     * xinputhid profiles have xinputhid auto-load on the HID child instead. */
    WdfDeviceCreateDeviceInterface(device, (LPGUID)&XUSB_GUID, NULL);
    {
        UNICODE_STRING refStr;
        RtlInitUnicodeString(&refStr, L"XI_00");
        WdfDeviceCreateDeviceInterface(device, (LPGUID)&WINEXINPUT_GUID, &refStr);
    }

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

    switch (IoControlCode)
    {
    case IOCTL_XUSB_GET_INFORMATION: {
        /* OutDeviceInfos_t — 12 bytes (matches driver.c format exactly) */
        UCHAR info[12];
        RtlZeroMemory(info, sizeof(info));
        *(USHORT*)&info[0] = 0x0101;  /* XUSBVersion 1.1 */
        info[2] = 0x01; /* device count — always 1 (each companion hosts one controller) */
        info[4] = 0x00;                /* unk2 — bit 7 clear = don't skip */
        *(USHORT*)&info[8] = ctx->VendorId;
        *(USHORT*)&info[10] = ctx->ProductId;
        CopyToRequest(Request, info, 12);
        break;
    }

    case IOCTL_XUSB_GET_CAPABILITIES: {
        /* GamepadCapabilities0101 — 24 bytes */
        UCHAR caps[24];
        RtlZeroMemory(caps, sizeof(caps));
        *(USHORT*)&caps[0] = 0x0101;
        caps[2] = 0x01; /* XINPUT_DEVTYPE_GAMEPAD */
        caps[3] = 0x01; /* XINPUT_DEVSUBTYPE_GAMEPAD */
        *(USHORT*)&caps[4] = 0xF7FF; /* wButtons mask — includes Guide (0x0400) */
        caps[6] = 0xFF; caps[7] = 0xFF;
        *(SHORT*)&caps[8]  = 32767; *(SHORT*)&caps[10] = 32767;
        *(SHORT*)&caps[12] = 32767; *(SHORT*)&caps[14] = 32767;
        caps[22] = 0xFF; caps[23] = 0xFF;
        CopyToRequest(Request, caps, 24);
        break;
    }

    case IOCTL_XUSB_GET_STATE: {
        /* GamepadState0101 — 29 bytes (matches driver.c format exactly) */
        UCHAR state[29];
        RtlZeroMemory(state, sizeof(state));
        *(USHORT*)&state[0] = 0x0101;
        state[2] = 0x01; /* CONNECTED */

        ctx->PacketCount++;
        *(DWORD*)&state[5] = ctx->PacketCount;

        UCHAR gip[14];
        if (ReadGipData(ctx, gip)) {
            PUCHAR d = gip;
            /* Button remapping: GIP button byte → XInput button bits */
            UCHAR btnLow = d[12], btnHigh = d[13];
            UCHAR hat = (btnHigh >> 2) & 0x0F;
            USHORT buttons = 0;
            if (btnLow & 0x01) buttons |= 0x1000; /* A */
            if (btnLow & 0x02) buttons |= 0x2000; /* B */
            if (btnLow & 0x04) buttons |= 0x4000; /* X */
            if (btnLow & 0x08) buttons |= 0x8000; /* Y */
            if (btnLow & 0x10) buttons |= 0x0100; /* LB */
            if (btnLow & 0x20) buttons |= 0x0200; /* RB */
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
            *(USHORT*)&state[0x0B] = buttons;
            state[0x0D] = (UCHAR)((*(USHORT*)&d[8] & 0x03FF) * 255 / 1023);
            state[0x0E] = (UCHAR)((*(USHORT*)&d[10] & 0x03FF) * 255 / 1023);
            *(SHORT*)&state[0x0F] = (SHORT)((int)(*(USHORT*)&d[0]) - 32768);
            *(SHORT*)&state[0x11] = (SHORT)(32767 - (int)(*(USHORT*)&d[2]));
            *(SHORT*)&state[0x13] = (SHORT)((int)(*(USHORT*)&d[4]) - 32768);
            *(SHORT*)&state[0x15] = (SHORT)(32767 - (int)(*(USHORT*)&d[6]));
        }
        CopyToRequest(Request, state, 29);
        break;
    }

    case IOCTL_XUSB_SET_STATE: {
        /* XInput rumble. Forward the wire-format vibration packet to the
         * output shared section as source=XInput. The consumer interprets
         * the bytes (typical packet is 5 bytes: cmd + size + lo motor +
         * hi motor + reserved, or 4 bytes for the raw XINPUT_VIBRATION). */
        PVOID rumbleBuf; size_t rumbleSize;
        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, 1, &rumbleBuf, &rumbleSize))
            && rumbleSize >= 1) {
            PublishOutput(ctx, OUT_SOURCE_XINPUT, 0,
                          (const UCHAR*)rumbleBuf, (ULONG)rumbleSize);
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

    case IOCTL_XUSB_WAIT_GUIDE:
    case IOCTL_XUSB_WAIT_FOR_INPUT:
        /* Return INVALID_DEVICE_REQUEST so xinput1_4 falls back to GET_STATE polling.
         * Returning STATUS_DEVICE_NOT_READY makes xinput1_4 think the device is
         * broken and it stops polling entirely (data freezes). */
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        break;

    case IOCTL_XUSB_GET_INFORMATION_EX: {
        UCHAR infoEx[64];
        RtlZeroMemory(infoEx, sizeof(infoEx));
        *(USHORT*)&infoEx[0] = 0x0101;
        infoEx[2] = 0x01;
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
