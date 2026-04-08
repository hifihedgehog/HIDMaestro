/*
 * HIDMaestro Companion — UMDF2 driver for XUSB XInput + WinExInput.
 * Registers XUSB (conditionally via XusbNeeded) + WinExInput interfaces.
 * Reads gamepad state from shared file (C:\ProgramData\HIDMaestro\input_N.bin).
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
    WCHAR SharedFilePath[128]; /* legacy fallback path */
    WCHAR ConfigRegPath[64];   /* e.g. L"SOFTWARE\HIDMaestro\Controller0" */
    WCHAR SharedMappingName[64]; /* e.g. L"Global\HIDMaestroInput0" */
    HANDLE SharedMemHandle;    /* OpenFileMapping handle (lazy) */
    PVOID SharedMemPtr;        /* MapViewOfFile pointer (lazy) */
} COMPANION_CTX, *PCOMPANION_CTX;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(COMPANION_CTX, GetCompanionCtx)

#pragma pack(push, 1)
typedef struct {
    ULONG SeqNo;
    ULONG DataSize;
    UCHAR Data[64];
    UCHAR GipData[14];
} SHARED_INPUT;
#pragma pack(pop)

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, CompanionDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

static BOOLEAN ReadGipData(PCOMPANION_CTX ctx, UCHAR gipOut[14])
{
    /* Lazy-open named section (preferred path). The test app creates it
     * before our device is created, but we re-try here in case the section
     * appears later. */
    if (ctx->SharedMemPtr == NULL) {
        HANDLE h = OpenFileMappingW(FILE_MAP_READ, FALSE, ctx->SharedMappingName);
        if (h != NULL) {
            PVOID v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, sizeof(SHARED_INPUT));
            if (v != NULL) { ctx->SharedMemHandle = h; ctx->SharedMemPtr = v; }
            else CloseHandle(h);
        }
    }

    if (ctx->SharedMemPtr != NULL) {
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

    /* Legacy file fallback */
    HANDLE hFile = CreateFileW(ctx->SharedFilePath,
        GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return FALSE;
    SHARED_INPUT shared;
    DWORD bytesRead = 0;
    BOOL ok = ReadFile(hFile, &shared, sizeof(shared), &bytesRead, NULL);
    CloseHandle(hFile);
    if (!ok || bytesRead < 72 + 14) return FALSE;
    RtlCopyMemory(gipOut, shared.GipData, 14);
    return TRUE;
}

void CompanionDeviceCleanup(_In_ WDFOBJECT Object)
{
    PCOMPANION_CTX ctx = GetCompanionCtx((WDFDEVICE)Object);
    if (ctx->SharedMemPtr) { UnmapViewOfFile(ctx->SharedMemPtr); ctx->SharedMemPtr = NULL; }
    if (ctx->SharedMemHandle) { CloseHandle(ctx->SharedMemHandle); ctx->SharedMemHandle = NULL; }
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

        /* Build per-instance paths */
        {
            /* Registry: SOFTWARE\HIDMaestro\Controller<N> */
            static const WCHAR prefix[] = L"SOFTWARE\\HIDMaestro\\Controller";
            WCHAR *p = ctx->ConfigRegPath;
            for (int i = 0; prefix[i]; i++) *p++ = prefix[i];
            *p++ = L'0' + (WCHAR)(ctx->ControllerIndex % 10);
            *p = L'\0';

            /* Shared file: C:\ProgramData\HIDMaestro\input_<N>.bin (legacy) */
            static const WCHAR filePrefix[] = L"C:\\ProgramData\\HIDMaestro\\input_";
            static const WCHAR fileSuffix[] = L".bin";
            p = ctx->SharedFilePath;
            for (int i = 0; filePrefix[i]; i++) *p++ = filePrefix[i];
            *p++ = L'0' + (WCHAR)(ctx->ControllerIndex % 10);
            for (int i = 0; fileSuffix[i]; i++) *p++ = fileSuffix[i];
            *p = L'\0';

            /* Memory-mapped section: Global\HIDMaestroInput<N> (preferred) */
            static const WCHAR mapPrefix[] = L"Global\\HIDMaestroInput";
            p = ctx->SharedMappingName;
            for (int i = 0; mapPrefix[i]; i++) *p++ = mapPrefix[i];
            *p++ = L'0' + (WCHAR)(ctx->ControllerIndex % 10);
            *p = L'\0';
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

    /* Register XUSB (conditionally) + WinExInput interfaces */
    {
        PCOMPANION_CTX dbgCtx = GetCompanionCtx(device);
        NTSTATUS s1 = STATUS_SUCCESS;
        NTSTATUS s2;

        /* XUSB companion ALWAYS registers XUSB — that's its sole purpose.
         * The XusbNeeded flag controls the MAIN device's XUSB registration
         * (to avoid duplicate XInput slots), not the companion's. */
        DWORD xusbNeeded = 1;
        s1 = WdfDeviceCreateDeviceInterface(device, (LPGUID)&XUSB_GUID, NULL);

        /* Always register WinExInput for browser gamepad API */
        UNICODE_STRING refStr;
        RtlInitUnicodeString(&refStr, L"XI_00");
        s2 = WdfDeviceCreateDeviceInterface(device, (LPGUID)&WINEXINPUT_GUID, &refStr);

        /* Debug log — FILE_SHARE_WRITE allows multiple companions to append */
        HANDLE hLog = CreateFileW(L"C:\\ProgramData\\HIDMaestro\\companion_debug.txt",
            FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, 0, NULL);
        if (hLog != INVALID_HANDLE_VALUE) {
            char msg[200];
            char* p = msg;
            RtlCopyMemory(p, "idx=", 4); p += 4;
            *p++ = (char)('0' + dbgCtx->ControllerIndex % 10);
            *p++ = ' ';
            RtlCopyMemory(p, "xusb=", 5); p += 5;
            *p++ = xusbNeeded ? '1' : '0';
            *p++ = ' ';
            RtlCopyMemory(p, "file=", 5); p += 5;
            for (int fi = 0; fi < 30 && dbgCtx->SharedFilePath[fi]; fi++)
                *p++ = (char)(dbgCtx->SharedFilePath[fi] & 0x7F);
            *p++ = ' ';
            RtlCopyMemory(p, "s1=", 3); p += 3;
            /* Simple hex output */
            for (int k = 7; k >= 0; k--) {
                int nib = (s1 >> (k*4)) & 0xF;
                *p++ = (char)(nib < 10 ? '0'+nib : 'A'+nib-10);
            }
            *p++ = ' '; *p++ = 's'; *p++ = '2'; *p++ = '=';
            for (int k = 7; k >= 0; k--) {
                int nib = (s2 >> (k*4)) & 0xF;
                *p++ = (char)(nib < 10 ? '0'+nib : 'A'+nib-10);
            }
            *p++ = '\r'; *p++ = '\n';
            int len = (int)(p - msg);
            DWORD dummy;
            WriteFile(hLog, msg, len, &dummy, NULL);
            CloseHandle(hLog);
        }
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
        *(USHORT*)&caps[4] = 0xF3FF; /* wButtons mask */
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
            if (btnLow & 0x40) buttons |= 0x0020; /* LS */
            if (btnLow & 0x80) buttons |= 0x0010; /* RS */
            if (btnHigh & 0x01) buttons |= 0x0040; /* Back */
            if (btnHigh & 0x02) buttons |= 0x0080; /* Start */
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
        /* Debug: log GET_STATE trigger values */
        {
            static LONG gsCount = 0;
            if (InterlockedIncrement(&gsCount) == 100) {
                HANDLE hLog = CreateFileW(L"C:\\ProgramData\\HIDMaestro\\getstate_debug.txt",
                    FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, 0, NULL);
                if (hLog != INVALID_HANDLE_VALUE) {
                    char msg[60]; char *p = msg;
                    *p++='L';*p++='T';*p++='=';
                    UCHAR ltv = state[0x0D];
                    *p++='0'+(ltv/100)%10;*p++='0'+(ltv/10)%10;*p++='0'+ltv%10;
                    *p++=' ';*p++='R';*p++='T';*p++='=';
                    UCHAR rtv = state[0x0E];
                    *p++='0'+(rtv/100)%10;*p++='0'+(rtv/10)%10;*p++='0'+rtv%10;
                    *p++='\r';*p++='\n';
                    DWORD dummy;
                    WriteFile(hLog, msg, (int)(p-msg), &dummy, NULL);
                    CloseHandle(hLog);
                }
            }
        }
        CopyToRequest(Request, state, 29);
        break;
    }

    case IOCTL_XUSB_SET_STATE:
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
