// multipad_check.cpp — Headless cross-API gamepad ordering diagnostic.
//
// Uses MultiPadTester's backend classes 1:1 (xinput_backend, dinput_backend,
// hidapi_backend, wgi_backend, rawinput_backend). Each backend's source files
// are copied verbatim from github.com/nefarius/MultiPadTester/src/.
//
// This program does not render anything — it creates a hidden message-only
// window for backends that need one (DInput's SetCooperativeLevel, RawInput's
// WM_INPUT target), runs the same Init → message-pump + Poll() loop that
// MultiPadTester's main.cpp does, samples GetState() for each slot, and
// prints which slot of each backend is holding which marker button.
//
// Marker convention: when the test app is run with --mark, each controller
// holds button (1 << creationIndex). HID button usage 1 == HMButton.A == bit 0,
// usage 2 == B == bit 1, etc. We decode the held button(s) on each backend's
// slot and print "slot[i] -> creation[N]".
//
// Build:  scripts/multipad_check/build.cmd
// Usage:  multipad_check.exe [--expected N] [--window-ms 2000]

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define INITGUID
#include <windows.h>
#include <Xinput.h>

#include "input_backend.h"
#include "xinput_backend.h"
#include "dinput_backend.h"
#include "hidapi_backend.h"
#include "wgi_backend.h"
#include "rawinput_backend.h"

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <string>
#include <utility>
#include <vector>

// Standard mapping: HMButton bit i ⇒ Button enum value used by MultiPadTester.
// MPT's Button enum (from gamepad_state.h):
//   A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000,
//   LeftBumper = 0x0100, RightBumper = 0x0200,
//   Back = 0x0020, Start = 0x0010,
//   LeftThumb = 0x0040, RightThumb = 0x0080,
//   Guide = 0x0400.
// HMButton bit positions in HMGamepadState (HIDMaestro SDK):
//   bit 0 = A,  bit 1 = B,  bit 2 = X,  bit 3 = Y,
//   bit 4 = LB, bit 5 = RB, bit 6 = Back, bit 7 = Start,
//   bit 8 = LStick, bit 9 = RStick, bit 10 = Guide.
// So mark mode with creation index N maps the held button to:
//   N = 0 ⇒ A,  N = 1 ⇒ B,  N = 2 ⇒ X,  N = 3 ⇒ Y,
//   N = 4 ⇒ LB, N = 5 ⇒ RB, N = 6 ⇒ Back, N = 7 ⇒ Start,
//   N = 8 ⇒ LStick, N = 9 ⇒ RStick, N = 10 ⇒ Guide.
static int MptButtonsToCreationIndex(uint16_t mptBtns) {
    if (mptBtns == 0) return -1;
    auto Has = [&](uint16_t bit) { return (mptBtns & bit) != 0; };
    if (Has(0x1000)) return 0;  // A
    if (Has(0x2000)) return 1;  // B
    if (Has(0x4000)) return 2;  // X
    if (Has(0x8000)) return 3;  // Y
    if (Has(0x0100)) return 4;  // LeftBumper
    if (Has(0x0200)) return 5;  // RightBumper
    if (Has(0x0020)) return 6;  // Back
    if (Has(0x0010)) return 7;  // Start
    if (Has(0x0040)) return 8;  // LeftThumb (LStick)
    if (Has(0x0080)) return 9;  // RightThumb (RStick)
    if (Has(0x0400)) return 10; // Guide
    return -1;
}

static LRESULT CALLBACK HostWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    // Forward WM_INPUT and WM_INPUT_DEVICE_CHANGE to all backends so each
    // can handle the messages it cares about (RawInput cares about both).
    extern std::vector<std::unique_ptr<IInputBackend>>* g_backendsHost;
    if (g_backendsHost) {
        for (auto& b : *g_backendsHost) {
            if (b->OnWindowMessage(msg, wp, lp)) return 0;
        }
    }
    return DefWindowProcW(hwnd, msg, wp, lp);
}

std::vector<std::unique_ptr<IInputBackend>>* g_backendsHost = nullptr;

static void PrintBackendResults(IInputBackend* b, int expected) {
    printf("\n%s\n", b->GetName());
    for (size_t i = 0; i < strlen(b->GetName()); ++i) putchar('-');
    putchar('\n');

    int liveCount = 0;
    std::vector<int> creationIndices;
    for (int s = 0; s < b->GetMaxSlots(); ++s) {
        const GamepadState& gs = b->GetState(s);
        if (!gs.connected) continue;
        ++liveCount;
        int idx = MptButtonsToCreationIndex(gs.buttons);
        const char* name = b->GetSlotDisplayName(s);
        if (!name) name = "(no name)";
        if (idx >= 0) creationIndices.push_back(idx);
        printf("  slot[%d] -> creation[%s]  buttons=0x%04X  %s\n",
               s, idx >= 0 ? std::to_string(idx).c_str() : "?",
               (unsigned)gs.buttons, name);
    }

    if (creationIndices.empty()) {
        if (liveCount == 0)
            printf("  -> no live slots\n");
        else
            printf("  -> %d live slot(s) but no marker button held\n", liveCount);
        return;
    }
    bool sorted = true;
    for (size_t i = 1; i < creationIndices.size(); ++i)
        if (creationIndices[i] < creationIndices[i - 1]) { sorted = false; break; }
    bool perfect = sorted &&
                   (int)creationIndices.size() == expected &&
                   creationIndices.front() == 0 &&
                   creationIndices.back() == expected - 1;
    if (perfect) {
        printf("  -> CORRECT  creation order [");
        for (size_t i = 0; i < creationIndices.size(); ++i)
            printf("%s%d", i ? "," : "", creationIndices[i]);
        printf("]\n");
    } else if (sorted) {
        printf("  -> SORTED but partial (saw %zu of %d expected): [",
               creationIndices.size(), expected);
        for (size_t i = 0; i < creationIndices.size(); ++i)
            printf("%s%d", i ? "," : "", creationIndices[i]);
        printf("]\n");
    } else {
        printf("  -> REORDERED: filled slots hold creation indices [");
        for (size_t i = 0; i < creationIndices.size(); ++i)
            printf("%s%d", i ? "," : "", creationIndices[i]);
        printf("]\n");
    }
}

int wmain(int argc, wchar_t** argv) {
    int expected = 2;
    int windowMs = 2000;
    for (int i = 1; i < argc; ++i) {
        if (!wcscmp(argv[i], L"--expected") && i + 1 < argc) expected = (int)_wtoi(argv[++i]);
        else if (!wcscmp(argv[i], L"--window-ms") && i + 1 < argc) windowMs = (int)_wtoi(argv[++i]);
    }

    printf("==================================================================\n");
    printf("  multipad_check  -  cross-API HIDMaestro ordering diagnostic\n");
    printf("==================================================================\n");
    printf("Expected controllers in mark mode: %d\n", expected);
    printf("Sampling window:                   %d ms\n", windowMs);
    printf("Each creation index N is identified by HMButton bit (1 << N) held.\n");
    printf("Run the test app with --mark or type 'mark' at its prompt.\n");

    // Hidden message-only window for backends that need a HWND (DInput's
    // SetCooperativeLevel and RawInput's WM_INPUT target).
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = HostWndProc;
    wc.hInstance = (HINSTANCE)GetModuleHandleW(nullptr);
    wc.lpszClassName = L"MultipadCheckHostWnd";
    RegisterClassExW(&wc);
    HWND hwnd = CreateWindowExW(0, wc.lpszClassName, L"mpc",
                                0, 0, 0, 0, 0, HWND_MESSAGE, nullptr,
                                wc.hInstance, nullptr);
    if (!hwnd) {
        fprintf(stderr, "CreateWindowExW failed (%lu)\n", GetLastError());
        return 1;
    }

    // Construct backends in the same order as MultiPadTester's main.cpp.
    std::vector<std::unique_ptr<IInputBackend>> backends;
    backends.push_back(std::make_unique<XInputBackend>());
    backends.push_back(std::make_unique<RawInputBackend>());
    backends.push_back(std::make_unique<DInputBackend>());
    backends.push_back(std::make_unique<HidApiBackend>());
    backends.push_back(std::make_unique<WgiBackend>());
    g_backendsHost = &backends;

    for (auto& b : backends) {
        b->Init(hwnd);
    }

    // Drive the same loop MultiPadTester runs every frame: pump messages,
    // call Poll() on every backend, repeat for the sampling window.
    auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(windowMs);
    while (std::chrono::steady_clock::now() < deadline) {
        MSG msg;
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        for (auto& b : backends) b->Poll();
        Sleep(5);
    }

    // Dump per-backend results.
    for (auto& b : backends) PrintBackendResults(b.get(), expected);

    printf("\n==================================================================\n");
    printf("  Summary  (slot[i] -> creation[N], live slots only)\n");
    printf("==================================================================\n");
    auto compact = [&](IInputBackend* b) {
        printf("%-22s ", b->GetName());
        bool any = false;
        for (int s = 0; s < b->GetMaxSlots(); ++s) {
            const GamepadState& gs = b->GetState(s);
            if (!gs.connected) continue;
            int idx = MptButtonsToCreationIndex(gs.buttons);
            if (idx >= 0) {
                printf("%s%d", any ? "," : "[", idx);
                any = true;
            }
        }
        printf("%s\n", any ? "]" : "(no marked controllers)");
    };
    for (auto& b : backends) compact(b.get());

    g_backendsHost = nullptr;
    backends.clear();
    DestroyWindow(hwnd);
    UnregisterClassW(wc.lpszClassName, wc.hInstance);
    return 0;
}
