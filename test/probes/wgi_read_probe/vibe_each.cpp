// Fire vibration on EACH Gamepad separately. Lets us correlate "which index
// produced which motor burst" when watching HMController.OutputReceived on
// the consumer side.
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
        wprintf(L"\n==== Gamepad[%u]: firing put_Vibration(1,1,1,1) for 1.5s ====\n", idx);
        wprintf(L"(Watch HMController.OutputReceived on the consumer side during THIS window)\n");
        Sleep(500);  // let the dust settle from the previous round
        GamepadVibration v = {};
        v.LeftMotor = 1.0; v.RightMotor = 1.0;
        v.LeftTrigger = 1.0; v.RightTrigger = 1.0;
        auto t0 = GetTickCount64();
        while (GetTickCount64() - t0 < 1500) {
            gp->put_Vibration(v);
            Sleep(50);
        }
        GamepadVibration z = {};
        gp->put_Vibration(z);
        wprintf(L"==== Gamepad[%u]: cleared, holding quiet for 1.0s ====\n", idx);
        Sleep(1000);
    }
    return 0;
}
