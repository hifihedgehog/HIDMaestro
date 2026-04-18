// Native C++ WGI put_Vibration test. Bypasses the .NET CLR/WinRT projection
// to check whether WGI silent-sink is a CLR/marshaling quirk or the API
// itself silently dropping on this caller context.

#include <windows.h>
#include <roapi.h>
#include <windows.foundation.h>
#include <windows.gaming.input.h>
#include <wrl.h>
#include <wrl/wrappers/corewrappers.h>
#include <stdio.h>

using namespace Microsoft::WRL;
using namespace Microsoft::WRL::Wrappers;
using namespace ABI::Windows::Foundation::Collections;
using namespace ABI::Windows::Gaming::Input;

int wmain()
{
    RoInitializeWrapper initialize(RO_INIT_MULTITHREADED);
    if (FAILED(initialize)) { wprintf(L"RoInitialize failed: 0x%08X\n", (HRESULT)initialize); return 1; }

    ComPtr<IGamepadStatics> statics;
    HStringReference className(L"Windows.Gaming.Input.Gamepad");
    HRESULT hr = RoGetActivationFactory(className.Get(), IID_PPV_ARGS(&statics));
    if (FAILED(hr)) { wprintf(L"RoGetActivationFactory failed: 0x%08X\n", hr); return 1; }

    // Poll up to ~3s for Gamepad.Gamepads to populate
    ComPtr<IVectorView<Gamepad*>> gamepads;
    UINT count = 0;
    for (int i = 0; i < 15; i++) {
        hr = statics->get_Gamepads(&gamepads);
        if (SUCCEEDED(hr) && gamepads) { gamepads->get_Size(&count); if (count > 0) break; }
        Sleep(200);
    }
    wprintf(L"Gamepad.Gamepads count = %u\n", count);
    if (count == 0) { wprintf(L"No gamepads. Exiting.\n"); return 1; }

    for (UINT i = 0; i < count; i++) {
        ComPtr<IGamepad> gp;
        gamepads->GetAt(i, &gp);
        wprintf(L"\n=== Firing put_Vibration on Gamepad[%u] for 2 seconds ===\n", i);
        GamepadVibration v = {};
        v.LeftMotor = 1.0;
        v.RightMotor = 1.0;
        v.LeftTrigger = 1.0;
        v.RightTrigger = 1.0;
        auto t0 = GetTickCount64();
        while (GetTickCount64() - t0 < 2000) {
            gp->put_Vibration(v);
            Sleep(50);
        }
        GamepadVibration z = {};
        gp->put_Vibration(z);
        wprintf(L"=== cleared ===\n");
        Sleep(500);
    }

    wprintf(L"\nReport: did any physical gamepad rumble during any 2-second window?\n");
    return 0;
}
