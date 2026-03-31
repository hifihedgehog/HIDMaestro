/*
 * HIDMaestro XInput System-Wide Hook
 *
 * Loaded via AppInit_DLLs into all processes. Hooks XInputGetState and
 * related functions to inject HIDMaestro virtual controllers as additional
 * XInput slots. Real controllers are passed through untouched.
 *
 * Architecture:
 *   1. DllMain (DLL_PROCESS_ATTACH) — check if process imports xinput
 *   2. If yes, hook XInputGetState/SetState/GetCapabilities via IAT patching
 *   3. Hooked functions check for HIDMaestro HID device
 *   4. Real XInput calls forwarded to original functions
 *
 * Build: cl /LD /O2 xinput_hook.c /Fe:hidmaestro_xinput.dll hid.lib setupapi.lib
 */

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <hidsdi.h>
#include <setupapi.h>
#include <stdint.h>

#pragma comment(lib, "hid.lib")
#pragma comment(lib, "setupapi.lib")

/* ------------------------------------------------------------------ */
/*  XInput types                                                       */
/* ------------------------------------------------------------------ */

typedef struct { WORD wButtons; BYTE bLT, bRT; SHORT sLX, sLY, sRX, sRY; } XGAMEPAD;
typedef struct { DWORD dwPacket; XGAMEPAD Gamepad; } XSTATE;
typedef struct { WORD wLeftMotor, wRightMotor; } XVIBRATION;
typedef struct { BYTE Type, SubType; WORD Flags; XGAMEPAD Gamepad; XVIBRATION Vibration; } XCAPS;

typedef DWORD (WINAPI *PFN_GetState)(DWORD, XSTATE*);
typedef DWORD (WINAPI *PFN_SetState)(DWORD, XVIBRATION*);
typedef DWORD (WINAPI *PFN_GetCaps)(DWORD, DWORD, XCAPS*);

#ifndef ERROR_DEVICE_NOT_CONNECTED
#define ERROR_DEVICE_NOT_CONNECTED 1167
#endif

/* ------------------------------------------------------------------ */
/*  Global state                                                       */
/* ------------------------------------------------------------------ */

static PFN_GetState g_origGetState = NULL;
static PFN_SetState g_origSetState = NULL;
static PFN_GetCaps  g_origGetCaps  = NULL;

static HANDLE  g_hidDevice = INVALID_HANDLE_VALUE;
static DWORD   g_packetNum = 0;
static DWORD   g_maestroSlot = 4; /* 4 = not assigned */
static BOOL    g_hooked = FALSE;
static BOOL    g_scanned = FALSE;

/* ------------------------------------------------------------------ */
/*  HIDMaestro device discovery                                        */
/* ------------------------------------------------------------------ */

static HANDLE FindHIDMaestroDevice(void)
{
    GUID hidGuid;
    HDEVINFO devInfo;
    SP_DEVICE_INTERFACE_DATA did;
    DWORD idx;

    HidD_GetHidGuid(&hidGuid);
    devInfo = SetupDiGetClassDevsW(&hidGuid, NULL, NULL, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (devInfo == INVALID_HANDLE_VALUE) return INVALID_HANDLE_VALUE;

    did.cbSize = sizeof(did);
    for (idx = 0; SetupDiEnumDeviceInterfaces(devInfo, NULL, &hidGuid, idx, &did); idx++)
    {
        DWORD reqSize;
        SP_DEVICE_INTERFACE_DETAIL_DATA_W *detail;
        HANDLE h;
        HIDD_ATTRIBUTES attrs;

        SetupDiGetDeviceInterfaceDetailW(devInfo, &did, NULL, 0, &reqSize, NULL);
        detail = (SP_DEVICE_INTERFACE_DETAIL_DATA_W*)HeapAlloc(GetProcessHeap(), 0, reqSize);
        if (!detail) continue;
        detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);

        if (!SetupDiGetDeviceInterfaceDetailW(devInfo, &did, detail, reqSize, NULL, NULL))
        {
            HeapFree(GetProcessHeap(), 0, detail);
            continue;
        }

        /* Only look at HIDCLASS devices (our virtual devices) */
        if (!wcsstr(detail->DevicePath, L"hidclass") && !wcsstr(detail->DevicePath, L"HIDCLASS"))
        {
            HeapFree(GetProcessHeap(), 0, detail);
            continue;
        }

        h = CreateFileW(detail->DevicePath,
            GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            NULL, OPEN_EXISTING, 0, NULL);
        HeapFree(GetProcessHeap(), 0, detail);

        if (h == INVALID_HANDLE_VALUE) continue;

        attrs.Size = sizeof(attrs);
        if (HidD_GetAttributes(h, &attrs))
        {
            /* Check for Xbox VID/PIDs that indicate an XInput-capable HIDMaestro device */
            if (attrs.VendorID == 0x045E &&
                (attrs.ProductID == 0x028E || attrs.ProductID == 0x02D1 ||
                 attrs.ProductID == 0x02DD || attrs.ProductID == 0x02EA ||
                 attrs.ProductID == 0x0B12 || attrs.ProductID == 0x0B00 ||
                 attrs.ProductID == 0x02E3 || attrs.ProductID == 0x0B0A))
            {
                SetupDiDestroyDeviceInfoList(devInfo);
                return h;
            }
        }
        CloseHandle(h);
    }

    SetupDiDestroyDeviceInfoList(devInfo);
    return INVALID_HANDLE_VALUE;
}

static void ScanForDevice(void)
{
    if (g_scanned) return;
    g_scanned = TRUE;

    g_hidDevice = FindHIDMaestroDevice();
    if (g_hidDevice == INVALID_HANDLE_VALUE) return;

    /* Find first unused XInput slot */
    if (g_origGetState)
    {
        DWORD i;
        for (i = 0; i < 4; i++)
        {
            XSTATE st;
            if (g_origGetState(i, &st) == ERROR_DEVICE_NOT_CONNECTED)
            {
                g_maestroSlot = i;
                return;
            }
        }
    }
    /* All 4 slots occupied — take slot 3 as last resort */
    g_maestroSlot = 3;
}

static BOOL ReadHIDInput(XGAMEPAD *pad)
{
    BYTE report[17]; /* Report ID 1 + 16 bytes */
    uint16_t lx, ly, rx, ry, lt, rt;
    uint8_t btnLow, btnHigh, hat;
    WORD buttons;

    if (g_hidDevice == INVALID_HANDLE_VALUE) return FALSE;

    report[0] = 0x01;
    if (!HidD_GetInputReport(g_hidDevice, report, sizeof(report)))
        return FALSE;

    lx = *(uint16_t*)&report[1];
    ly = *(uint16_t*)&report[3];
    rx = *(uint16_t*)&report[5];
    ry = *(uint16_t*)&report[7];
    lt = *(uint16_t*)&report[9];
    rt = *(uint16_t*)&report[11];
    btnLow  = report[13];
    btnHigh = report[14];
    hat = (btnHigh >> 2) & 0x0F;

    pad->sLX = (SHORT)((int)lx - 32768);
    pad->sLY = (SHORT)((int)ly - 32768);
    pad->sRX = (SHORT)((int)rx - 32768);
    pad->sRY = (SHORT)((int)ry - 32768);
    pad->bLT = (BYTE)(lt >> 8);
    pad->bRT = (BYTE)(rt >> 8);

    buttons = 0;
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
    pad->wButtons = buttons;
    return TRUE;
}

/* ------------------------------------------------------------------ */
/*  Hooked XInput functions                                            */
/* ------------------------------------------------------------------ */

static DWORD WINAPI Hook_XInputGetState(DWORD idx, XSTATE *pState)
{
    ScanForDevice();

    if (idx == g_maestroSlot && g_hidDevice != INVALID_HANDLE_VALUE)
    {
        ZeroMemory(pState, sizeof(*pState));
        if (ReadHIDInput(&pState->Gamepad))
        {
            pState->dwPacket = ++g_packetNum;
            return 0; /* ERROR_SUCCESS */
        }
    }

    /* Pass through to real XInput — adjust index if needed */
    if (g_origGetState)
    {
        if (g_maestroSlot < 4 && idx >= g_maestroSlot && idx < 3)
            return g_origGetState(idx + 1, pState);
        if (idx == g_maestroSlot)
            return g_origGetState(idx, pState); /* Fallback if HID read fails */
        return g_origGetState(idx, pState);
    }
    return ERROR_DEVICE_NOT_CONNECTED;
}

static DWORD WINAPI Hook_XInputSetState(DWORD idx, XVIBRATION *pVib)
{
    ScanForDevice();
    if (idx == g_maestroSlot) return 0; /* Absorb rumble */
    if (g_origSetState) return g_origSetState(idx, pVib);
    return ERROR_DEVICE_NOT_CONNECTED;
}

static DWORD WINAPI Hook_XInputGetCapabilities(DWORD idx, DWORD flags, XCAPS *pCaps)
{
    ScanForDevice();
    if (idx == g_maestroSlot && g_hidDevice != INVALID_HANDLE_VALUE)
    {
        ZeroMemory(pCaps, sizeof(*pCaps));
        pCaps->Type = 0x01;    /* XINPUT_DEVTYPE_GAMEPAD */
        pCaps->SubType = 0x01; /* XINPUT_DEVSUBTYPE_GAMEPAD */
        pCaps->Gamepad.wButtons = 0xFFFF;
        pCaps->Gamepad.bLT = 255;
        pCaps->Gamepad.bRT = 255;
        pCaps->Gamepad.sLX = 32767;
        pCaps->Gamepad.sLY = 32767;
        pCaps->Gamepad.sRX = 32767;
        pCaps->Gamepad.sRY = 32767;
        return 0;
    }
    if (g_origGetCaps) return g_origGetCaps(idx, flags, pCaps);
    return ERROR_DEVICE_NOT_CONNECTED;
}

/* ------------------------------------------------------------------ */
/*  IAT Hooking                                                        */
/* ------------------------------------------------------------------ */

static BOOL PatchIAT(HMODULE hModule, const char *targetDll, const char *funcName, void *hookFunc, void **origFunc)
{
    IMAGE_DOS_HEADER *dos;
    IMAGE_NT_HEADERS *nt;
    IMAGE_IMPORT_DESCRIPTOR *imp;
    ULONG_PTR base;
    DWORD rva, size;

    if (!hModule) return FALSE;
    base = (ULONG_PTR)hModule;
    dos = (IMAGE_DOS_HEADER*)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return FALSE;
    nt = (IMAGE_NT_HEADERS*)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return FALSE;

    rva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    size = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].Size;
    if (!rva || !size) return FALSE;

    imp = (IMAGE_IMPORT_DESCRIPTOR*)(base + rva);
    for (; imp->Name; imp++)
    {
        const char *dllName = (const char*)(base + imp->Name);
        IMAGE_THUNK_DATA *origThunk, *boundThunk;
        DWORD i;

        if (_stricmp(dllName, targetDll) != 0) continue;

        origThunk = (IMAGE_THUNK_DATA*)(base + imp->OriginalFirstThunk);
        boundThunk = (IMAGE_THUNK_DATA*)(base + imp->FirstThunk);

        for (i = 0; origThunk[i].u1.AddressOfData; i++)
        {
            IMAGE_IMPORT_BY_NAME *nameEntry;
            DWORD oldProtect;

            if (IMAGE_SNAP_BY_ORDINAL(origThunk[i].u1.Ordinal)) continue;
            nameEntry = (IMAGE_IMPORT_BY_NAME*)(base + origThunk[i].u1.AddressOfData);

            if (strcmp(nameEntry->Name, funcName) != 0) continue;

            /* Found it — patch the IAT entry */
            if (origFunc) *origFunc = (void*)boundThunk[i].u1.Function;

            if (VirtualProtect(&boundThunk[i].u1.Function, sizeof(void*), PAGE_READWRITE, &oldProtect))
            {
                boundThunk[i].u1.Function = (ULONG_PTR)hookFunc;
                VirtualProtect(&boundThunk[i].u1.Function, sizeof(void*), oldProtect, &oldProtect);
                return TRUE;
            }
        }
    }
    return FALSE;
}

static void InstallHooks(void)
{
    HMODULE exe = GetModuleHandleW(NULL);
    if (!exe) return;

    /* Try all XInput DLL names */
    const char *dllNames[] = { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll", "XINPUT1_4.dll", "XINPUT1_3.dll" };
    int i;
    for (i = 0; i < 5; i++)
    {
        if (PatchIAT(exe, dllNames[i], "XInputGetState", Hook_XInputGetState, (void**)&g_origGetState))
        {
            PatchIAT(exe, dllNames[i], "XInputSetState", Hook_XInputSetState, (void**)&g_origSetState);
            PatchIAT(exe, dllNames[i], "XInputGetCapabilities", Hook_XInputGetCapabilities, (void**)&g_origGetCaps);
            g_hooked = TRUE;
            break;
        }
    }
}

/* ------------------------------------------------------------------ */
/*  DLL Entry Point                                                    */
/* ------------------------------------------------------------------ */

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpReserved)
{
    (void)lpReserved;
    switch (fdwReason)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(hinstDLL);
        InstallHooks();
        break;
    case DLL_PROCESS_DETACH:
        if (g_hidDevice != INVALID_HANDLE_VALUE) CloseHandle(g_hidDevice);
        break;
    }
    return TRUE;
}
