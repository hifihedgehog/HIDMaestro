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
int wmain() {
    RoInitializeWrapper init(RO_INIT_MULTITHREADED);
    if (FAILED(init)) { wprintf(L"RoInit failed\n"); return 1; }
    ComPtr<IGamepadStatics> s;
    HStringReference cn(L"Windows.Gaming.Input.Gamepad");
    if (FAILED(RoGetActivationFactory(cn.Get(), IID_PPV_ARGS(&s)))) return 1;
    ComPtr<IVectorView<Gamepad*>> pads;
    UINT n = 0;
    for (int i = 0; i < 15; i++) {
        s->get_Gamepads(&pads);
        if (pads) { pads->get_Size(&n); if (n > 0) break; }
        Sleep(200);
    }
    wprintf(L"Gamepads: %u\n", n);
    if (!n) return 1;
    for (UINT idx = 0; idx < n; idx++) {
        ComPtr<IGamepad> gp;
        pads->GetAt(idx, &gp);
        wprintf(L"\n=== Gamepad[%u] sampled 5x @ 200ms ===\n", idx);
        for (int i = 0; i < 5; i++) {
            GamepadReading r = {};
            gp->GetCurrentReading(&r);
            wprintf(L"  t=%d: buttons=0x%08X LT=%.2f RT=%.2f LX=%+.3f LY=%+.3f RX=%+.3f RY=%+.3f\n",
                i, (unsigned)r.Buttons, r.LeftTrigger, r.RightTrigger,
                r.LeftThumbstickX, r.LeftThumbstickY, r.RightThumbstickX, r.RightThumbstickY);
            Sleep(200);
        }
    }
    return 0;
}
