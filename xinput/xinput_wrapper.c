/*
 * HIDMaestro XInput Wrapper
 *
 * Drop-in replacement for xinput1_4.dll / xinput1_3.dll / xinput9_1_0.dll.
 * Intercepts XInputGetState() and reads from HIDMaestro's virtual HID device.
 * All other XInput functions are forwarded to the real system DLL.
 *
 * Build: cl /LD /O2 xinput_wrapper.c /Fe:xinput1_4.dll hid.lib setupapi.lib
 *
 * Usage: Copy the built DLL to a game's directory. The game loads our DLL
 * instead of the system xinput1_4.dll (DLL search order). Our DLL finds
 * the HIDMaestro device, reads input from it, and translates to XInput format.
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
/*  XInput types (from xinput.h)                                       */
/* ------------------------------------------------------------------ */

typedef struct _XINPUT_GAMEPAD {
    WORD  wButtons;
    BYTE  bLeftTrigger;
    BYTE  bRightTrigger;
    SHORT sThumbLX;
    SHORT sThumbLY;
    SHORT sThumbRX;
    SHORT sThumbRY;
} XINPUT_GAMEPAD;

typedef struct _XINPUT_STATE {
    DWORD         dwPacketNumber;
    XINPUT_GAMEPAD Gamepad;
} XINPUT_STATE;

typedef struct _XINPUT_VIBRATION {
    WORD wLeftMotorSpeed;
    WORD wRightMotorSpeed;
} XINPUT_VIBRATION;

typedef struct _XINPUT_CAPABILITIES {
    BYTE           Type;
    BYTE           SubType;
    WORD           Flags;
    XINPUT_GAMEPAD Gamepad;
    XINPUT_VIBRATION Vibration;
} XINPUT_CAPABILITIES;

typedef struct _XINPUT_BATTERY_INFORMATION {
    BYTE BatteryType;
    BYTE BatteryLevel;
} XINPUT_BATTERY_INFORMATION;

typedef struct _XINPUT_KEYSTROKE {
    WORD VirtualKey;
    WCHAR Unicode;
    WORD Flags;
    BYTE UserIndex;
    BYTE HidCode;
} XINPUT_KEYSTROKE;

/* Error codes */
#ifndef ERROR_DEVICE_NOT_CONNECTED
#define ERROR_DEVICE_NOT_CONNECTED  1167
#endif
#define XUSER_MAX_COUNT             4

/* XInput types */
#define XINPUT_DEVTYPE_GAMEPAD      0x01
#define XINPUT_DEVSUBTYPE_GAMEPAD   0x01

/* Button masks */
#define XINPUT_GAMEPAD_DPAD_UP          0x0001
#define XINPUT_GAMEPAD_DPAD_DOWN        0x0002
#define XINPUT_GAMEPAD_DPAD_LEFT        0x0004
#define XINPUT_GAMEPAD_DPAD_RIGHT       0x0008
#define XINPUT_GAMEPAD_START            0x0010
#define XINPUT_GAMEPAD_BACK             0x0020
#define XINPUT_GAMEPAD_LEFT_THUMB       0x0040
#define XINPUT_GAMEPAD_RIGHT_THUMB      0x0080
#define XINPUT_GAMEPAD_LEFT_SHOULDER    0x0100
#define XINPUT_GAMEPAD_RIGHT_SHOULDER   0x0200
#define XINPUT_GAMEPAD_A                0x1000
#define XINPUT_GAMEPAD_B                0x2000
#define XINPUT_GAMEPAD_X                0x4000
#define XINPUT_GAMEPAD_Y                0x8000

/* ------------------------------------------------------------------ */
/*  Real XInput DLL forwarding                                         */
/* ------------------------------------------------------------------ */

typedef DWORD (WINAPI *PFN_XInputGetState)(DWORD, XINPUT_STATE*);
typedef DWORD (WINAPI *PFN_XInputSetState)(DWORD, XINPUT_VIBRATION*);
typedef DWORD (WINAPI *PFN_XInputGetCapabilities)(DWORD, DWORD, XINPUT_CAPABILITIES*);
typedef void  (WINAPI *PFN_XInputEnable)(BOOL);
typedef DWORD (WINAPI *PFN_XInputGetBatteryInformation)(DWORD, BYTE, XINPUT_BATTERY_INFORMATION*);
typedef DWORD (WINAPI *PFN_XInputGetKeystroke)(DWORD, DWORD, XINPUT_KEYSTROKE*);

static HMODULE              g_realXInput = NULL;
static PFN_XInputGetState   g_realGetState = NULL;
static PFN_XInputSetState   g_realSetState = NULL;
static PFN_XInputGetCapabilities g_realGetCapabilities = NULL;
static PFN_XInputEnable     g_realEnable = NULL;
static PFN_XInputGetBatteryInformation g_realGetBattery = NULL;
static PFN_XInputGetKeystroke g_realGetKeystroke = NULL;

/* ------------------------------------------------------------------ */
/*  HIDMaestro device state                                            */
/* ------------------------------------------------------------------ */

static HANDLE   g_hidDevice = INVALID_HANDLE_VALUE;
static DWORD    g_packetNumber = 0;
static BOOL     g_initialized = FALSE;
static DWORD    g_maestroSlot = XUSER_MAX_COUNT; /* Which XInput slot we occupy */

/* HIDMaestro universal descriptor input report layout (Report ID 1):
 *   Bytes 0-1:  Left Stick X (uint16)
 *   Bytes 2-3:  Left Stick Y (uint16)
 *   Bytes 4-5:  Right Stick X (uint16)
 *   Bytes 6-7:  Right Stick Y (uint16)
 *   Bytes 8-9:  Left Trigger (uint16)
 *   Bytes 10-11: Right Trigger (uint16)
 *   Byte 12:    Buttons low (bits 0-7 = buttons 1-8)
 *   Byte 13:    Buttons high (bit 0-1 = buttons 9-10, bits 2-7 = padding)
 *                + Hat (bits 2-5) + padding
 */

static void LoadRealXInput(void)
{
    WCHAR sysDir[MAX_PATH];
    WCHAR dllPath[MAX_PATH];

    GetSystemDirectoryW(sysDir, MAX_PATH);

    /* Try xinput1_4.dll first, then 1_3, then 9_1_0 */
    wsprintfW(dllPath, L"%s\\xinput1_4.dll", sysDir);
    g_realXInput = LoadLibraryW(dllPath);
    if (!g_realXInput) {
        wsprintfW(dllPath, L"%s\\xinput1_3.dll", sysDir);
        g_realXInput = LoadLibraryW(dllPath);
    }
    if (!g_realXInput) {
        wsprintfW(dllPath, L"%s\\xinput9_1_0.dll", sysDir);
        g_realXInput = LoadLibraryW(dllPath);
    }

    if (g_realXInput) {
        g_realGetState = (PFN_XInputGetState)GetProcAddress(g_realXInput, "XInputGetState");
        g_realSetState = (PFN_XInputSetState)GetProcAddress(g_realXInput, "XInputSetState");
        g_realGetCapabilities = (PFN_XInputGetCapabilities)GetProcAddress(g_realXInput, "XInputGetCapabilities");
        g_realEnable = (PFN_XInputEnable)GetProcAddress(g_realXInput, "XInputEnable");
        g_realGetBattery = (PFN_XInputGetBatteryInformation)GetProcAddress(g_realXInput, "XInputGetBatteryInformation");
        g_realGetKeystroke = (PFN_XInputGetKeystroke)GetProcAddress(g_realXInput, "XInputGetKeystroke");
    }
}

static HANDLE FindHIDMaestroDevice(void)
{
    GUID hidGuid;
    HidD_GetHidGuid(&hidGuid);

    HDEVINFO devInfo = SetupDiGetClassDevsW(&hidGuid, NULL, NULL,
        DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (devInfo == INVALID_HANDLE_VALUE) return INVALID_HANDLE_VALUE;

    SP_DEVICE_INTERFACE_DATA did;
    did.cbSize = sizeof(did);

    for (DWORD idx = 0; SetupDiEnumDeviceInterfaces(devInfo, NULL, &hidGuid, idx, &did); idx++) {
        DWORD reqSize;
        SetupDiGetDeviceInterfaceDetailW(devInfo, &did, NULL, 0, &reqSize, NULL);

        SP_DEVICE_INTERFACE_DETAIL_DATA_W *detail =
            (SP_DEVICE_INTERFACE_DETAIL_DATA_W*)HeapAlloc(GetProcessHeap(), 0, reqSize);
        if (!detail) continue;
        detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);

        if (!SetupDiGetDeviceInterfaceDetailW(devInfo, &did, detail, reqSize, NULL, NULL)) {
            HeapFree(GetProcessHeap(), 0, detail);
            continue;
        }

        /* Look for HIDCLASS in the path — that's our virtual device */
        if (!wcsstr(detail->DevicePath, L"hidclass") &&
            !wcsstr(detail->DevicePath, L"HIDCLASS")) {
            HeapFree(GetProcessHeap(), 0, detail);
            continue;
        }

        HANDLE h = CreateFileW(detail->DevicePath,
            GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            NULL, OPEN_EXISTING, 0, NULL);

        HeapFree(GetProcessHeap(), 0, detail);

        if (h == INVALID_HANDLE_VALUE) continue;

        /* Verify it's a Microsoft Xbox controller by VID/PID */
        HIDD_ATTRIBUTES attrs;
        attrs.Size = sizeof(attrs);
        if (HidD_GetAttributes(h, &attrs)) {
            if (attrs.VendorID == 0x045E &&
                (attrs.ProductID == 0x028E || /* Xbox 360 */
                 attrs.ProductID == 0x02D1 || /* Xbox One */
                 attrs.ProductID == 0x02DD ||
                 attrs.ProductID == 0x02EA ||
                 attrs.ProductID == 0x0B12 || /* Xbox Series */
                 attrs.ProductID == 0x0B00))  /* Xbox Elite v2 */
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

static void Initialize(void)
{
    if (g_initialized) return;
    g_initialized = TRUE;

    LoadRealXInput();

    /* Find HIDMaestro device */
    g_hidDevice = FindHIDMaestroDevice();

    if (g_hidDevice != INVALID_HANDLE_VALUE) {
        /* Determine which XInput slot to use.
         * Find the first unused slot (real XInput devices occupy lower slots).
         */
        for (DWORD i = 0; i < XUSER_MAX_COUNT; i++) {
            if (g_realGetState) {
                XINPUT_STATE state;
                if (g_realGetState(i, &state) == ERROR_DEVICE_NOT_CONNECTED) {
                    g_maestroSlot = i;
                    break;
                }
            } else {
                g_maestroSlot = 0;
                break;
            }
        }
    }
}

static BOOL ReadHIDMaestroState(XINPUT_GAMEPAD *pad)
{
    if (g_hidDevice == INVALID_HANDLE_VALUE) return FALSE;

    /* Read input report via HidD_GetInputReport (polled, non-blocking) */
    BYTE report[17]; /* Report ID + 16 bytes for universal descriptor */
    report[0] = 0x01; /* Input Report ID */

    if (!HidD_GetInputReport(g_hidDevice, report, sizeof(report)))
        return FALSE;

    /* Parse universal descriptor layout:
     * report[1-2]: LX (uint16, 0-65535, center 32768)
     * report[3-4]: LY
     * report[5-6]: RX
     * report[7-8]: RY
     * report[9-10]: LT
     * report[11-12]: RT
     * report[13]: Buttons low (bits 0-7)
     * report[14]: Buttons high (bits 0-1) + hat (bits 2-5)
     */
    uint16_t lx = *(uint16_t*)&report[1];
    uint16_t ly = *(uint16_t*)&report[3];
    uint16_t rx = *(uint16_t*)&report[5];
    uint16_t ry = *(uint16_t*)&report[7];
    uint16_t lt = *(uint16_t*)&report[9];
    uint16_t rt = *(uint16_t*)&report[11];
    uint8_t  btnLow  = report[13];
    uint8_t  btnHigh = report[14];
    uint8_t  hat = (btnHigh >> 2) & 0x0F;

    /* Convert axes: uint16 (0-65535) -> int16 (-32768 to 32767) */
    pad->sThumbLX = (SHORT)((int)lx - 32768);
    pad->sThumbLY = (SHORT)((int)ly - 32768);
    pad->sThumbRX = (SHORT)((int)rx - 32768);
    pad->sThumbRY = (SHORT)((int)ry - 32768);

    /* Convert triggers: uint16 (0-65535) -> uint8 (0-255) */
    pad->bLeftTrigger  = (BYTE)(lt >> 8);
    pad->bRightTrigger = (BYTE)(rt >> 8);

    /* Map buttons: our descriptor has 10 buttons (1-10)
     * Map to standard Xbox layout:
     *   1=A, 2=B, 3=X, 4=Y, 5=LB, 6=RB, 7=Back, 8=Start, 9=LThumb, 10=RThumb
     */
    WORD buttons = 0;
    if (btnLow & 0x01) buttons |= XINPUT_GAMEPAD_A;
    if (btnLow & 0x02) buttons |= XINPUT_GAMEPAD_B;
    if (btnLow & 0x04) buttons |= XINPUT_GAMEPAD_X;
    if (btnLow & 0x08) buttons |= XINPUT_GAMEPAD_Y;
    if (btnLow & 0x10) buttons |= XINPUT_GAMEPAD_LEFT_SHOULDER;
    if (btnLow & 0x20) buttons |= XINPUT_GAMEPAD_RIGHT_SHOULDER;
    if (btnLow & 0x40) buttons |= XINPUT_GAMEPAD_BACK;
    if (btnLow & 0x80) buttons |= XINPUT_GAMEPAD_START;
    if (btnHigh & 0x01) buttons |= XINPUT_GAMEPAD_LEFT_THUMB;
    if (btnHigh & 0x02) buttons |= XINPUT_GAMEPAD_RIGHT_THUMB;

    /* Map hat to D-pad buttons (hat values 1-8, 0=centered) */
    switch (hat) {
        case 1: buttons |= XINPUT_GAMEPAD_DPAD_UP; break;
        case 2: buttons |= XINPUT_GAMEPAD_DPAD_UP | XINPUT_GAMEPAD_DPAD_RIGHT; break;
        case 3: buttons |= XINPUT_GAMEPAD_DPAD_RIGHT; break;
        case 4: buttons |= XINPUT_GAMEPAD_DPAD_RIGHT | XINPUT_GAMEPAD_DPAD_DOWN; break;
        case 5: buttons |= XINPUT_GAMEPAD_DPAD_DOWN; break;
        case 6: buttons |= XINPUT_GAMEPAD_DPAD_DOWN | XINPUT_GAMEPAD_DPAD_LEFT; break;
        case 7: buttons |= XINPUT_GAMEPAD_DPAD_LEFT; break;
        case 8: buttons |= XINPUT_GAMEPAD_DPAD_LEFT | XINPUT_GAMEPAD_DPAD_UP; break;
    }

    pad->wButtons = buttons;
    return TRUE;
}

/* ------------------------------------------------------------------ */
/*  Exported XInput functions                                          */
/* ------------------------------------------------------------------ */

__declspec(dllexport) DWORD WINAPI XInputGetState(DWORD dwUserIndex, XINPUT_STATE *pState)
{
    Initialize();

    /* Is this our HIDMaestro slot? */
    if (dwUserIndex == g_maestroSlot && g_hidDevice != INVALID_HANDLE_VALUE) {
        ZeroMemory(pState, sizeof(*pState));
        if (ReadHIDMaestroState(&pState->Gamepad)) {
            pState->dwPacketNumber = ++g_packetNumber;
            return ERROR_SUCCESS;
        }
    }

    /* Forward to real XInput */
    if (g_realGetState) {
        /* Adjust slot index: if our slot is before this one, shift down */
        DWORD realIndex = dwUserIndex;
        if (g_maestroSlot != XUSER_MAX_COUNT && dwUserIndex > g_maestroSlot)
            realIndex = dwUserIndex - 1;
        else if (dwUserIndex == g_maestroSlot)
            return ERROR_DEVICE_NOT_CONNECTED;
        return g_realGetState(realIndex, pState);
    }

    return ERROR_DEVICE_NOT_CONNECTED;
}

__declspec(dllexport) DWORD WINAPI XInputSetState(DWORD dwUserIndex, XINPUT_VIBRATION *pVibration)
{
    Initialize();
    if (dwUserIndex == g_maestroSlot) return ERROR_SUCCESS; /* Absorb vibration */
    if (g_realSetState) return g_realSetState(dwUserIndex, pVibration);
    return ERROR_DEVICE_NOT_CONNECTED;
}

__declspec(dllexport) DWORD WINAPI XInputGetCapabilities(DWORD dwUserIndex, DWORD dwFlags, XINPUT_CAPABILITIES *pCapabilities)
{
    Initialize();
    if (dwUserIndex == g_maestroSlot && g_hidDevice != INVALID_HANDLE_VALUE) {
        ZeroMemory(pCapabilities, sizeof(*pCapabilities));
        pCapabilities->Type = XINPUT_DEVTYPE_GAMEPAD;
        pCapabilities->SubType = XINPUT_DEVSUBTYPE_GAMEPAD;
        pCapabilities->Gamepad.wButtons = 0xFFFF;
        pCapabilities->Gamepad.bLeftTrigger = 255;
        pCapabilities->Gamepad.bRightTrigger = 255;
        pCapabilities->Gamepad.sThumbLX = (SHORT)32767;
        pCapabilities->Gamepad.sThumbLY = (SHORT)32767;
        pCapabilities->Gamepad.sThumbRX = (SHORT)32767;
        pCapabilities->Gamepad.sThumbRY = (SHORT)32767;
        return ERROR_SUCCESS;
    }
    if (g_realGetCapabilities) return g_realGetCapabilities(dwUserIndex, dwFlags, pCapabilities);
    return ERROR_DEVICE_NOT_CONNECTED;
}

__declspec(dllexport) void WINAPI XInputEnable(BOOL enable)
{
    Initialize();
    if (g_realEnable) g_realEnable(enable);
}

__declspec(dllexport) DWORD WINAPI XInputGetBatteryInformation(DWORD dwUserIndex, BYTE devType, XINPUT_BATTERY_INFORMATION *pBatteryInformation)
{
    Initialize();
    if (dwUserIndex == g_maestroSlot) {
        pBatteryInformation->BatteryType = 0x01; /* BATTERY_TYPE_WIRED */
        pBatteryInformation->BatteryLevel = 0x03; /* BATTERY_LEVEL_FULL */
        return ERROR_SUCCESS;
    }
    if (g_realGetBattery) return g_realGetBattery(dwUserIndex, devType, pBatteryInformation);
    return ERROR_DEVICE_NOT_CONNECTED;
}

__declspec(dllexport) DWORD WINAPI XInputGetKeystroke(DWORD dwUserIndex, DWORD dwReserved, XINPUT_KEYSTROKE *pKeystroke)
{
    Initialize();
    if (dwUserIndex == g_maestroSlot) return ERROR_EMPTY;
    if (g_realGetKeystroke) return g_realGetKeystroke(dwUserIndex, dwReserved, pKeystroke);
    return ERROR_DEVICE_NOT_CONNECTED;
}

/* Ordinal 100 — undocumented XInputGetStateEx (includes Guide button) */
__declspec(dllexport) DWORD WINAPI XInputGetStateEx(DWORD dwUserIndex, XINPUT_STATE *pState)
{
    return XInputGetState(dwUserIndex, pState);
}

/* ------------------------------------------------------------------ */
/*  DLL Entry Point                                                    */
/* ------------------------------------------------------------------ */

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpReserved)
{
    (void)hinstDLL; (void)lpReserved;
    switch (fdwReason) {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(hinstDLL);
        break;
    case DLL_PROCESS_DETACH:
        if (g_hidDevice != INVALID_HANDLE_VALUE) CloseHandle(g_hidDevice);
        if (g_realXInput) FreeLibrary(g_realXInput);
        break;
    }
    return TRUE;
}
