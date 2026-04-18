/*
 * HIDMaestro — XUSB Interface Shim (UMDF2 HID upper filter)
 *
 * Registers XUSB interface class {EC87F1E3-...} on our HID gamepad children
 * (xinputhid-excluded PIDs — 028E, 0291, 0719) so WGI's Gamepad enumerator
 * sees the interface on the HID PDO. No HID descriptor modification —
 * 10-button fidelity preserved.
 *
 * When the interface is opened and IOCTL_XUSB_SET_STATE arrives, the motor
 * bytes are published to `Global\HIDMaestroOutput<N>` (the same shared
 * section HMCOMPANION writes to) with Source = OUT_SOURCE_XINPUT. The SDK's
 * existing output worker delivers it to OutputReceived — no new SDK code
 * needed.
 *
 * ControllerIndex comes from the parent devnode's HW key (set by the SDK
 * when it creates the virtual controller). The filter queries the parent's
 * instance ID via DEVPKEY_Device_Parent, then reads ControllerIndex from
 * the parent's Device Parameters key.
 *
 * Diagnostic log at C:\ProgramData\HIDMaestro\xusbshim_log.txt captures
 * every IOCTL code (+ hex dump for XUSB IOCTLs) so we know what WGI
 * actually sends through this interface.
 *
 * Experimental — v1-dev-experiment-xusb-child-pdo.
 */
#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <wdf.h>

/* Define DEVPROPKEY for parent instance ID locally — avoids having to include
 * devpkey.h with INITGUID (which requires careful single-translation-unit
 * handling). Fmtid + PID from devpkey.h DEVPKEY_Device_Parent. */
static const DEVPROPKEY DEVPKEY_Device_ParentLocal = {
    { 0x4340A6C5, 0x93FA, 0x4706, { 0x97, 0x2C, 0x7B, 0x64, 0x80, 0x08, 0xA5, 0xA7 } },
    8
};

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD XusbShimDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEFAULT XusbShimIoDefault;
EVT_WDF_OBJECT_CONTEXT_CLEANUP XusbShimDeviceCleanup;

/* XUSB interface class — same GUID xinputhid registers on real Xbox HID
 * children. */
static const GUID GUID_DEVINTERFACE_XUSB = {
    0xEC87F1E3, 0xC13B, 0x4100,
    { 0xB5, 0xF7, 0x8B, 0x84, 0xD5, 0x42, 0x60, 0xCB }
};

/* XUSB IOCTL codes (public RE of xusb22.sys / xinputhid.sys). */
#define IOCTL_XUSB_GET_INFORMATION       0x80006000u
#define IOCTL_XUSB_GET_CAPABILITIES      0x8000E000u
#define IOCTL_XUSB_GET_LED_STATE         0x8000E004u
#define IOCTL_XUSB_GET_STATE             0x8000E008u
#define IOCTL_XUSB_SET_STATE             0x8000A010u
#define IOCTL_XUSB_WAIT_GUIDE_BUTTON     0x8000E00Cu
#define IOCTL_XUSB_GET_BATTERY_INFO      0x8000E018u

/* Mirror of SHARED_OUTPUT from driver.h / companion.c. Keep in sync. */
#pragma pack(push, 1)
typedef struct {
    volatile ULONG SeqNo;
    UCHAR  Source;       /* 0=HID output, 1=HID feature, 2=XInput rumble */
    UCHAR  ReportId;
    USHORT DataSize;
    UCHAR  Data[256];
} SHARED_OUTPUT;
#pragma pack(pop)
#define OUT_SOURCE_XINPUT 2

typedef struct _DEVICE_CTX {
    ULONG  ControllerIndex;
    WCHAR  OutputMappingName[64];  /* e.g. "Global\HIDMaestroOutput0" */
    HANDLE OutputMemHandle;
    PVOID  OutputMemPtr;
    ULONG  OutputSeqNoLocal;
    ULONG  OutputWriteCount;
} DEVICE_CTX, *PDEVICE_CTX;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CTX, GetDevCtx)

/* ---- Small utilities (no CRT) -------------------------------------- */

static SIZE_T WstrLen(const WCHAR *s) { SIZE_T n = 0; while (s[n]) n++; return n; }
static VOID   WstrCopy(WCHAR *d, const WCHAR *s, SIZE_T cap)
{
    SIZE_T i = 0; while (s[i] && i + 1 < cap) { d[i] = s[i]; i++; } d[i] = 0;
}
static VOID   WstrCat(WCHAR *d, const WCHAR *s, SIZE_T cap)
{
    SIZE_T i = WstrLen(d); SIZE_T j = 0;
    while (s[j] && i + 1 < cap) { d[i++] = s[j++]; } d[i] = 0;
}
static VOID   WstrAppendUlong(WCHAR *d, ULONG v, SIZE_T cap)
{
    WCHAR tmp[16]; int n = 0;
    if (v == 0) tmp[n++] = L'0';
    else { while (v && n < 15) { tmp[n++] = L'0' + (WCHAR)(v % 10); v /= 10; } }
    SIZE_T i = WstrLen(d);
    while (n > 0 && i + 1 < cap) d[i++] = tmp[--n];
    d[i] = 0;
}

/* ---- Diagnostic log ------------------------------------------------- */

static VOID LogLine(_In_z_ const char *msg, _In_ SIZE_T len)
{
    HANDLE h = CreateFileW(
        L"C:\\ProgramData\\HIDMaestro\\xusbshim_log.txt",
        FILE_APPEND_DATA, FILE_SHARE_READ, NULL,
        OPEN_ALWAYS, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    DWORD written;
    WriteFile(h, msg, (DWORD)len, &written, NULL);
    CloseHandle(h);
}

static VOID LogEvent(_In_z_ const char *tag, _In_ ULONG value)
{
    static volatile LONG counter = 0;
    if (InterlockedIncrement(&counter) > 400) return;
    char buf[80]; char *p = buf;
    while (*tag) *p++ = *tag++;
    *p++ = ' ';
    for (int shift = 28; shift >= 0; shift -= 4) {
        int nibble = (value >> shift) & 0xF;
        *p++ = (char)(nibble < 10 ? '0' + nibble : 'A' + nibble - 10);
    }
    *p++ = '\r'; *p++ = '\n';
    LogLine(buf, (SIZE_T)(p - buf));
}

static VOID LogBytes(_In_z_ const char *tag, _In_bytecount_(len) const UCHAR *buf, _In_ SIZE_T len)
{
    char msg[112]; char *p = msg;
    while (*tag) *p++ = *tag++;
    *p++ = ' ';
    SIZE_T n = (len > 32) ? 32 : len;
    for (SIZE_T i = 0; i < n; i++) {
        int hi = (buf[i] >> 4) & 0xF;
        int lo = buf[i] & 0xF;
        *p++ = (char)(hi < 10 ? '0' + hi : 'A' + hi - 10);
        *p++ = (char)(lo < 10 ? '0' + lo : 'A' + lo - 10);
        *p++ = ' ';
    }
    *p++ = '\r'; *p++ = '\n';
    LogLine(msg, (SIZE_T)(p - msg));
}

/* ---- ControllerIndex from parent devnode --------------------------- */

static ULONG
ReadParentControllerIndex(_In_ WDFDEVICE device)
{
    WDF_DEVICE_PROPERTY_DATA prop;
    WDF_DEVICE_PROPERTY_DATA_INIT(&prop, &DEVPKEY_Device_ParentLocal);
    prop.Lcid = LOCALE_NEUTRAL;

    WCHAR      parentId[200] = {0};
    ULONG      reqSize = 0;
    DEVPROPTYPE propType = 0;
    NTSTATUS   s = WdfDeviceQueryPropertyEx(
        device, &prop, sizeof(parentId), parentId, &reqSize, &propType);
    if (!NT_SUCCESS(s) || reqSize == 0) return 0;

    /* Build \SYSTEM\CurrentControlSet\Enum\<parentId>\Device Parameters */
    WCHAR regPath[400];
    WstrCopy(regPath, L"SYSTEM\\CurrentControlSet\\Enum\\", 400);
    WstrCat (regPath, parentId, 400);
    WstrCat (regPath, L"\\Device Parameters", 400);

    HKEY hKey;
    ULONG idx = 0;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, regPath, 0, KEY_READ, &hKey) == ERROR_SUCCESS) {
        DWORD val = 0, sz = sizeof(val);
        if (RegQueryValueExW(hKey, L"ControllerIndex", NULL, NULL,
                             (LPBYTE)&val, &sz) == ERROR_SUCCESS) {
            idx = val;
        }
        RegCloseKey(hKey);
    }
    return idx;
}

/* ---- Shared-memory publish ----------------------------------------- */

static BOOLEAN
EnsureOutputMapping(_In_ PDEVICE_CTX ctx)
{
    /* Periodic re-open: close stale mapping every 500 writes (same pattern
     * as companion.c uses so a transient unmap doesn't wedge us). */
    if (ctx->OutputMemPtr != NULL) {
        if (++ctx->OutputWriteCount < 500) return TRUE;
        UnmapViewOfFile(ctx->OutputMemPtr); ctx->OutputMemPtr = NULL;
        CloseHandle(ctx->OutputMemHandle); ctx->OutputMemHandle = NULL;
        ctx->OutputWriteCount = 0;
    }

    HANDLE h = OpenFileMappingW(
        FILE_MAP_WRITE | FILE_MAP_READ, FALSE, ctx->OutputMappingName);
    if (h == NULL) return FALSE;

    PVOID v = MapViewOfFile(
        h, FILE_MAP_WRITE | FILE_MAP_READ, 0, 0, sizeof(SHARED_OUTPUT));
    if (v == NULL) { CloseHandle(h); return FALSE; }

    ctx->OutputMemHandle = h;
    ctx->OutputMemPtr = v;
    ctx->OutputWriteCount = 0;
    return TRUE;
}

static VOID
PublishXusbRumble(_In_ PDEVICE_CTX ctx,
                  _In_bytecount_(len) const UCHAR *data, _In_ ULONG len)
{
    if (!EnsureOutputMapping(ctx)) return;
    if (len > sizeof(((SHARED_OUTPUT*)0)->Data))
        len = sizeof(((SHARED_OUTPUT*)0)->Data);

    volatile SHARED_OUTPUT *dst = (volatile SHARED_OUTPUT *)ctx->OutputMemPtr;

    dst->Source = OUT_SOURCE_XINPUT;
    dst->ReportId = 0;
    dst->DataSize = (USHORT)len;
    for (ULONG i = 0; i < len; i++) dst->Data[i] = data[i];
    MemoryBarrier();

    /* Monotonic SeqNo, coordinating with whatever the driver already wrote. */
    ULONG next = dst->SeqNo + 1;
    if (next <= ctx->OutputSeqNoLocal) next = ctx->OutputSeqNoLocal + 1;
    ctx->OutputSeqNoLocal = next;
    dst->SeqNo = next;
}

/* ---- Driver / device-add / IO handler ------------------------------ */

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, XusbShimDeviceAdd);
    return WdfDriverCreate(
        DriverObject, RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

NTSTATUS
XusbShimDeviceAdd(
    _In_    WDFDRIVER       Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    NTSTATUS               status;
    WDFDEVICE              device;
    WDF_IO_QUEUE_CONFIG    queueConfig;
    WDF_OBJECT_ATTRIBUTES  attributes;
    UNREFERENCED_PARAMETER(Driver);

    WdfFdoInitSetFilter(DeviceInit);

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CTX);
    attributes.EvtCleanupCallback = XusbShimDeviceCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    PDEVICE_CTX ctx = GetDevCtx(device);
    ctx->ControllerIndex = ReadParentControllerIndex(device);

    /* "Global\HIDMaestroOutput<N>" — the section HMCOMPANION writes to. */
    WstrCopy(ctx->OutputMappingName, L"Global\\HIDMaestroOutput",
             sizeof(ctx->OutputMappingName) / sizeof(WCHAR));
    WstrAppendUlong(ctx->OutputMappingName, ctx->ControllerIndex,
                    sizeof(ctx->OutputMappingName) / sizeof(WCHAR));

    CreateDirectoryW(L"C:\\ProgramData\\HIDMaestro", NULL);
    LogEvent("DeviceAdd idx", ctx->ControllerIndex);

    NTSTATUS ifs = WdfDeviceCreateDeviceInterface(
        device, (LPGUID)&GUID_DEVINTERFACE_XUSB, NULL);
    LogEvent("XUSB-ifreg", (ULONG)ifs);

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(
        &queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDefault = XusbShimIoDefault;

    return WdfIoQueueCreate(
        device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, NULL);
}

VOID
XusbShimDeviceCleanup(_In_ WDFOBJECT Object)
{
    PDEVICE_CTX ctx = GetDevCtx((WDFDEVICE)Object);
    if (ctx->OutputMemPtr) { UnmapViewOfFile(ctx->OutputMemPtr); ctx->OutputMemPtr = NULL; }
    if (ctx->OutputMemHandle) { CloseHandle(ctx->OutputMemHandle); ctx->OutputMemHandle = NULL; }
    LogEvent("DeviceCleanup", ctx->ControllerIndex);
}

VOID
XusbShimIoDefault(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request)
{
    WDFDEVICE              device = WdfIoQueueGetDevice(Queue);
    PDEVICE_CTX            ctx    = GetDevCtx(device);
    WDF_REQUEST_PARAMETERS params;
    BOOLEAN                handledLocally = FALSE;

    WDF_REQUEST_PARAMETERS_INIT(&params);
    WdfRequestGetParameters(Request, &params);

    if (params.Type == WdfRequestTypeDeviceControl ||
        params.Type == WdfRequestTypeDeviceControlInternal) {
        ULONG code = params.Parameters.DeviceIoControl.IoControlCode;
        LogEvent(
            params.Type == WdfRequestTypeDeviceControl ? "IOCTL" : "INT-IOCTL",
            code);

        /* Known XUSB IOCTLs: log payload + handle locally. */
        if (code == IOCTL_XUSB_SET_STATE) {
            PVOID  buf;
            size_t sz;
            if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, 1, &buf, &sz))) {
                LogBytes("SET_STATE-in", (const UCHAR*)buf, sz);
                PublishXusbRumble(ctx, (const UCHAR*)buf, (ULONG)sz);
            }
            WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, 0);
            handledLocally = TRUE;
        }
        else if (code == IOCTL_XUSB_GET_INFORMATION) {
            /* Minimal 12-byte GetInformation reply: XUSB 1.1, 1 device. */
            UCHAR info[12];
            for (int i = 0; i < 12; i++) info[i] = 0;
            info[0] = 0x01; info[1] = 0x01;       /* Version 1.1 */
            info[2] = 0x01;                        /* Device count */
            /* info[8..11] left zero — VID/PID unknown to the filter.
             * If WGI keys on these, HMCOMPANION's parallel XUSB interface
             * provides the proper VID/PID response. */
            PVOID  outBuf;
            size_t outLen;
            if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, 12, &outBuf, &outLen))) {
                if (outLen > 12) outLen = 12;
                for (size_t i = 0; i < outLen; i++) ((UCHAR*)outBuf)[i] = info[i];
                WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, outLen);
            } else {
                WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, 0);
            }
            handledLocally = TRUE;
        }
        else if (code == IOCTL_XUSB_GET_CAPABILITIES) {
            /* 24-byte capabilities: Gamepad type/subtype with full button mask
             * + vibration advertised. Mirrors HMCOMPANION's response. */
            UCHAR caps[24];
            for (int i = 0; i < 24; i++) caps[i] = 0;
            caps[0] = 0x01; caps[1] = 0x01;        /* Version 1.1 */
            caps[2] = 0x01;                         /* XINPUT_DEVTYPE_GAMEPAD */
            caps[3] = 0x01;                         /* XINPUT_DEVSUBTYPE_GAMEPAD */
            caps[4] = 0xFF; caps[5] = 0xF7;         /* wButtons mask (LE) */
            caps[6] = 0xFF; caps[7] = 0xFF;         /* triggers */
            caps[8] = 0xFF; caps[9] = 0x7F;         /* LX max */
            caps[10]= 0xFF; caps[11]= 0x7F;         /* LY max */
            caps[12]= 0xFF; caps[13]= 0x7F;         /* RX max */
            caps[14]= 0xFF; caps[15]= 0x7F;         /* RY max */
            caps[22]= 0xFF; caps[23]= 0xFF;         /* vibration max */
            PVOID  outBuf;
            size_t outLen;
            if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, 24, &outBuf, &outLen))) {
                if (outLen > 24) outLen = 24;
                for (size_t i = 0; i < outLen; i++) ((UCHAR*)outBuf)[i] = caps[i];
                WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, outLen);
            } else {
                WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, 0);
            }
            handledLocally = TRUE;
        }
    } else {
        LogEvent("MJ", (ULONG)params.Type);
    }

    if (!handledLocally) {
        WDF_REQUEST_SEND_OPTIONS opts;
        WDF_REQUEST_SEND_OPTIONS_INIT(&opts, WDF_REQUEST_SEND_OPTION_SEND_AND_FORGET);
        if (!WdfRequestSend(Request, WdfDeviceGetIoTarget(device), &opts)) {
            WdfRequestComplete(Request, WdfRequestGetStatus(Request));
        }
    }
}
