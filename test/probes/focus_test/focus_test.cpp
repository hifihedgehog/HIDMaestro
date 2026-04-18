// Focus-gated GameInput SetRumbleState test with a REAL Win32 window
// owned by this process. Rumble fires only while our own window is the
// system foreground window. This is the test Microsoft Learn documents
// as the gating condition: "SetRumbleState will only be applied to the
// device while the application is in focus."

#include <windows.h>
#include <GameInput.h>
#include <stdio.h>
#include <vector>
#include <mutex>

struct DeviceInfo {
    IGameInputDevice* dev;
    uint16_t vid;
    uint16_t pid;
    uint32_t supportedRumble;
};
static std::mutex g_mtx;
static std::vector<DeviceInfo> g_devs;
static HWND g_hwnd = nullptr;
static bool g_firing = false;
static IGameInputDevice* g_target = nullptr;

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    switch (m) {
    case WM_CHAR:
        if (w == '0' || w == '1' || w == '2') {
            int idx = (int)(w - '0');
            std::lock_guard<std::mutex> lk(g_mtx);
            if (idx < (int)g_devs.size()) { g_target = g_devs[idx].dev; printf("[focus_test] target = [%d] VID=0x%04X PID=0x%04X supRumble=0x%X\n", idx, g_devs[idx].vid, g_devs[idx].pid, g_devs[idx].supportedRumble); }
        } else if (w == 'r' || w == 'R') {
            g_firing = !g_firing;
            printf("[focus_test] firing = %s\n", g_firing ? "ON" : "OFF");
        } else if (w == 'q' || w == 'Q') {
            PostQuitMessage(0);
        }
        return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProc(h, m, w, l);
}

static void CALLBACK DeviceCb(GameInputCallbackToken tok, void* ctx, IGameInputDevice* d, uint64_t ts, GameInputDeviceStatus now, GameInputDeviceStatus prev) {
    (void)tok; (void)ctx; (void)ts; (void)prev;
    if (!(now & GameInputDeviceConnected)) return;
    const GameInputDeviceInfo* di = d->GetDeviceInfo();
    if (!di) return;
    DeviceInfo r{};
    r.dev = d; d->AddRef();
    r.vid = di->vendorId;
    r.pid = di->productId;
    r.supportedRumble = (uint32_t)di->supportedRumbleMotors;
    std::lock_guard<std::mutex> lk(g_mtx);
    g_devs.push_back(r);
    printf("[cb] [%zu] VID=0x%04X PID=0x%04X supRumble=0x%X\n", g_devs.size() - 1, r.vid, r.pid, r.supportedRumble);
}

int main() {
    // Allocate a console since this is a WinMain-style Win32 app with console output
    AllocConsole();
    freopen("CONOUT$", "w", stdout);
    freopen("CONIN$", "r", stdin);

    printf("GameInput focus-gated rumble test\n");
    printf("================================\n");
    printf("\n");

    // Register a window class
    WNDCLASSA wc = {};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = GetModuleHandle(NULL);
    wc.lpszClassName = "GIFocusTestWnd";
    wc.hCursor = LoadCursor(NULL, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    RegisterClassA(&wc);

    g_hwnd = CreateWindowA(
        "GIFocusTestWnd", "GameInput Focus Test (click here for focus, press 0/1/2 to pick target, R to toggle rumble, Q to quit)",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE,
        CW_USEDEFAULT, CW_USEDEFAULT, 720, 160,
        NULL, NULL, GetModuleHandle(NULL), NULL);
    if (!g_hwnd) { printf("CreateWindow failed\n"); return 1; }
    SetForegroundWindow(g_hwnd);

    IGameInput* gi = nullptr;
    HRESULT hr = GameInputCreate(&gi);
    if (FAILED(hr)) { printf("GameInputCreate failed: 0x%08lX\n", hr); return 1; }

    GameInputCallbackToken tok = 0;
    gi->RegisterDeviceCallback(nullptr, GameInputKindGamepad, GameInputDeviceConnected, GameInputBlockingEnumeration, nullptr, DeviceCb, &tok);
    printf("\nInstructions:\n");
    printf("  1. Click this WINDOW (not the console) to give it foreground focus\n");
    printf("  2. Press 0, 1, or 2 to select a device by enumeration index\n");
    printf("  3. Press R to toggle rumble ON. Rumble only fires while this window is foreground.\n");
    printf("  4. Press Q to quit.\n");
    printf("\nFocus-gating rule: SetRumbleState fires only if GetForegroundWindow() == own_hwnd.\n\n");

    // Main message + rumble loop
    GameInputRumbleParams rp = {};
    rp.lowFrequency = 1.0f; rp.highFrequency = 1.0f;
    rp.leftTrigger = 1.0f; rp.rightTrigger = 1.0f;
    GameInputRumbleParams zero = {};

    MSG msg;
    DWORD lastFire = 0;
    bool hadTarget = false;
    while (true) {
        while (PeekMessageA(&msg, NULL, 0, 0, PM_REMOVE)) {
            if (msg.message == WM_QUIT) goto done;
            TranslateMessage(&msg);
            DispatchMessageA(&msg);
        }
        DWORD now = GetTickCount();
        if (now - lastFire >= 50) {
            lastFire = now;
            HWND fg = GetForegroundWindow();
            bool focused = (fg == g_hwnd);
            if (g_firing && g_target && focused) {
                g_target->SetRumbleState(&rp);
                hadTarget = true;
            } else if (hadTarget && g_target) {
                g_target->SetRumbleState(&zero);
                hadTarget = false;
            }
        }
        Sleep(1);
    }
done:
    if (g_target) g_target->SetRumbleState(&zero);
    if (tok) gi->UnregisterCallback(tok, 0);
    { std::lock_guard<std::mutex> lk(g_mtx); for (auto& d : g_devs) d.dev->Release(); }
    if (gi) gi->Release();
    printf("exit\n");
    return 0;
}
