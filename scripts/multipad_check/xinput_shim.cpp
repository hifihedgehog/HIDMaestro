// xinput_shim.cpp — provides a XInputGetState symbol that satisfies the
// linker for xinput_backend.cpp's static call, without requiring xinput.lib
// to be in the link line.
//
// Why: linking xinput.lib in the same process as windowsapp.lib (WinRT) on
// this Windows build (Windows 11 IoT LTSC 2024 / build 26200) causes the
// process to segfault during startup. Linking xinput1_4.dll dynamically via
// LoadLibrary in the same process works fine. So we provide our own
// XInputGetState that defers the load until first call. xinput_backend.cpp's
// static call resolves to this shim at link time and the call works the
// same as if we had linked xinput.lib.
//
// MPT's xinput_backend.cpp also uses XInputGetStateEx via ordinal 100 if
// available. That call goes through GetProcAddress on the same handle so
// no shim is needed there — it works as long as the DLL is loaded.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

// Forward-declare XINPUT_STATE in opaque form. xinput_backend.cpp passes a
// pointer to a real XINPUT_STATE through this function — we don't access
// any fields here, just forward the pointer to the dynamically-loaded
// implementation, so an opaque struct works fine.
struct XINPUT_STATE_opaque;

extern "C" {

typedef DWORD (WINAPI *PFN_XInputGetState)(DWORD, void*);

static HMODULE             s_xinputModule = nullptr;
static PFN_XInputGetState  s_pfnXInputGetState = nullptr;

static void EnsureLoaded() {
    if (s_pfnXInputGetState) return;
    if (!s_xinputModule) {
        s_xinputModule = LoadLibraryW(L"xinput1_4.dll");
        if (!s_xinputModule) s_xinputModule = LoadLibraryW(L"xinput1_3.dll");
        if (!s_xinputModule) s_xinputModule = LoadLibraryW(L"xinput9_1_0.dll");
    }
    if (s_xinputModule) {
        s_pfnXInputGetState = reinterpret_cast<PFN_XInputGetState>(
            GetProcAddress(s_xinputModule, "XInputGetState"));
    }
}

// Match the SAL annotations that xinput.h uses so the linker accepts our
// definition as a substitute for the imported symbol. This must be the same
// signature xinput_backend.cpp's static call expects (it includes <Xinput.h>
// which declares the canonical prototype).
DWORD WINAPI XInputGetState(DWORD dwUserIndex, void* pState) {
    EnsureLoaded();
    if (!s_pfnXInputGetState) return 1167; // ERROR_DEVICE_NOT_CONNECTED
    return s_pfnXInputGetState(dwUserIndex, pState);
}

} // extern "C"
