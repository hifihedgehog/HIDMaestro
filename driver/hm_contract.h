/*
 * hm_contract.h: the version contract (the design note's decision (c)) as
 * the HID driver (driver.c) and the XUSB companion (companion.c) both read
 * it: the version section's layout, the identity tag, and the identity
 * entry that names a controller's index before its first start. One copy,
 * so the two drivers cannot read it two ways. Include after <windows.h>
 * and <wdf.h>.
 */
#pragma once

/* v2.0.0: SOFTWARE\HIDMaestro\Identity\<token>, value ControllerIndex
 * (REG_DWORD): the index the SDK gave the controller whose devnodes carry
 * the token, written before any PnP work (decision (c), "The index before
 * the first start"). */
#define HIDMAESTRO_IDENTITY_ROOT    L"SOFTWARE\\HIDMaestro\\Identity\\"
#define HIDMAESTRO_IDENTITY_VALUE   L"ControllerIndex"

#pragma pack(push, 1)
/* Version handshake (v1.13.0): Global\HIDMaestroVersion<N>, 64 bytes,
 * created by the SDK. The SDK writes its half before the device exists:
 * the first 20 bytes and, from protocol 4, the identity tag. The driver
 * writes its half at add and whenever it opens the input section, the
 * same values each time: DriverVersion first, DriverProtocol last, so a
 * reader that sees a nonzero protocol sees the version that goes with it.
 * It clears nothing in its cleanup. The XUSB companion writes its own
 * field at 40 and nothing of the HID driver's.
 *
 * Protocol 1: the input and output sections, the events and the PID state,
 *             through v1.11.0.
 * Protocol 2: adds InputAck and the owner pipe, v1.12.0.
 * Protocol 3: adds this section, v1.13.0, which never shipped.
 * Protocol 4: the version contract, v2.0.0 (the design note's decision
 *             (c)): the identity tag and the identity entry, under which
 *             a driver reads a controller's v2.0.0 values.
 * A driver keeps every older layout. The SDK runs on a driver whose range
 * from DriverProtocolMin to DriverProtocol holds its protocol. */
#define HIDMAESTRO_VERSION_LAYOUT       1
#define HIDMAESTRO_DRIVER_PROTOCOL      4
#define HIDMAESTRO_DRIVER_PROTOCOL_MIN  4
/* The protocol whose SDK's values this driver reads. */
#define HIDMAESTRO_CONTRACT_PROTOCOL    4
typedef struct _HIDMAESTRO_VERSION_INFO {
    volatile LONG   LayoutVersion;       /* SDK: HIDMAESTRO_VERSION_LAYOUT */
    volatile LONG   SdkProtocolMin;      /* SDK: the oldest driver protocol it runs on */
    volatile LONG   SdkProtocolMax;      /* SDK: the protocol it speaks */
    USHORT          SdkVersion[4];       /* SDK: major, minor, patch, 0 */
    volatile LONG   DriverProtocol;      /* driver: 0 until it has written its half */
    USHORT          DriverVersion[4];    /* driver: major, minor, patch, 0 */
    volatile LONG   DriverCapabilities;  /* driver: HIDMAESTRO_INPUT_ACK_CAP_* it supports */
    volatile LONG   DriverProtocolMin;   /* driver: the oldest SDK protocol it serves */
    volatile LONG   CompanionProtocol;   /* companion: its protocol */
    volatile LONG   CompanionProtocolMin;/* companion: the oldest SDK protocol it serves */
    ULONGLONG       IdentityTag;         /* SDK: HmIdentityTag of the controller's token */
    volatile LONG   DriverConfigError;   /* driver: HIDMAESTRO_CONFIG_ERROR_* before a failed start */
    volatile LONG   CompanionConfigError;/* companion: the same for its values */
} HIDMAESTRO_VERSION_INFO, *PHIDMAESTRO_VERSION_INFO;
C_ASSERT(sizeof(HIDMAESTRO_VERSION_INFO) == 64);
C_ASSERT(FIELD_OFFSET(HIDMAESTRO_VERSION_INFO, DriverProtocolMin) == 36);
C_ASSERT(FIELD_OFFSET(HIDMAESTRO_VERSION_INFO, IdentityTag) == 48);
C_ASSERT(FIELD_OFFSET(HIDMAESTRO_VERSION_INFO, DriverConfigError) == 56);
#pragma pack(pop)

/* The identity tag (decision (c)): the 64-bit FNV-1a hash of the token's
 * UTF-16LE code units, no terminator, as the SDK's DeviceIdentity.TagOf
 * computes it. HM_0000 gives 0x1FD048916B8D461F and HM_0123456789ABCDEF
 * 0x3B86F3634086D831. */
static __inline ULONGLONG HmIdentityTag(_In_z_ const WCHAR *token)
{
    ULONGLONG h = 0xCBF29CE484222325ULL;
    for (SIZE_T i = 0; token[i]; i++) {
        h ^= (UCHAR)(token[i] & 0xFF);
        h *= 0x100000001B3ULL;
        h ^= (UCHAR)((token[i] >> 8) & 0xFF);
        h *= 0x100000001B3ULL;
    }
    return h;
}

/* DEVPKEY_Device_InstanceId (devpkey.h), written out so no file needs
 * initguid.h. */
static const DEVPROPKEY g_HmInstanceIdKey =
    { { 0x78c34fc8, 0x104a, 0x4aca, { 0x9e, 0xa4, 0x52, 0x4d, 0x52, 0x99, 0x6e, 0x57 } }, 256 };

/* The identity token, the last segment of the devnode's instance ID
 * (ROOT\<enumerator>\<token> or SWD\<enumerator>\<token>, issue #60), read
 * through WdfFdoInitQueryPropertyEx before WdfDeviceCreate, as Learn says
 * a driver calls it. Empty when the query fails. */
static __inline VOID
HmReadInstanceToken(_In_ PWDFDEVICE_INIT DeviceInit, _Out_writes_(cap) WCHAR *token, _In_ SIZE_T cap)
{
    WCHAR id[200];
    ULONG len = 0;
    DEVPROPTYPE type = 0;
    WDF_DEVICE_PROPERTY_DATA data;
    token[0] = 0;
    WDF_DEVICE_PROPERTY_DATA_INIT(&data, &g_HmInstanceIdKey);
    if (!NT_SUCCESS(WdfFdoInitQueryPropertyEx(DeviceInit, &data, sizeof(id), id, &len, &type))
        || type != DEVPROP_TYPE_STRING || len < sizeof(WCHAR))
        return;
    id[ARRAYSIZE(id) - 1] = 0;
    SIZE_T n = 0, last = 0;
    while (id[n]) { if (id[n] == L'\\') last = n + 1; n++; }
    SIZE_T t = 0;
    while (id[last + t] && t + 1 < cap) { token[t] = id[last + t]; t++; }
    token[t] = 0;
}

/* The index the SDK wrote under HIDMAESTRO_IDENTITY_ROOT<token>, value
 * HIDMAESTRO_IDENTITY_VALUE, before any PnP work. */
static __inline BOOLEAN
HmReadIdentityIndex(_In_ const WCHAR *token, _Out_ ULONG *index)
{
    static const WCHAR prefix[] = HIDMAESTRO_IDENTITY_ROOT;
    WCHAR path[96];
    HKEY hKey;
    DWORD val = 0, sz = sizeof(val), type = 0;
    SIZE_T n = 0;
    *index = 0;
    if (!token[0]) return FALSE;
    for (SIZE_T i = 0; prefix[i]; i++) path[n++] = prefix[i];
    for (SIZE_T i = 0; token[i] && n + 1 < ARRAYSIZE(path); i++) path[n++] = token[i];
    path[n] = 0;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, path, 0, KEY_READ, &hKey) != ERROR_SUCCESS) return FALSE;
    BOOLEAN ok = RegQueryValueExW(hKey, HIDMAESTRO_IDENTITY_VALUE, NULL, &type, (LPBYTE)&val, &sz) == ERROR_SUCCESS
              && type == REG_DWORD && sz == sizeof(val);
    RegCloseKey(hKey);
    if (ok) *index = val;
    return ok;
}
