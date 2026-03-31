/* Quick test: load our xinput1_4.dll wrapper and check XInputGetState */
#include <windows.h>
#include <stdio.h>

typedef struct { WORD wButtons; BYTE bLT, bRT; SHORT sLX, sLY, sRX, sRY; } XGAMEPAD;
typedef struct { DWORD dwPacket; XGAMEPAD Gamepad; } XSTATE;
typedef DWORD (WINAPI *PFN)(DWORD, XSTATE*);

int main(void)
{
    /* Load our wrapper DLL from the build directory */
    HMODULE h = LoadLibraryW(L"C:\\Users\\sonic\\OneDrive\\Documents\\GitHub\\HIDMaestro\\build\\xinput1_4.dll");
    if (!h) { printf("Failed to load DLL: %lu\n", GetLastError()); return 1; }

    PFN getState = (PFN)GetProcAddress(h, "XInputGetState");
    if (!getState) { printf("No XInputGetState\n"); return 1; }

    printf("Polling XInput (via HIDMaestro wrapper)...\n\n");
    for (int iter = 0; iter < 100; iter++) {
        for (DWORD i = 0; i < 4; i++) {
            XSTATE state = {0};
            DWORD r = getState(i, &state);
            if (r == 0) {
                printf("  Slot %u: LX=%6d LY=%6d RX=%6d RY=%6d LT=%3u RT=%3u Btns=0x%04X pkt=%u\n",
                    i, state.Gamepad.sLX, state.Gamepad.sLY,
                    state.Gamepad.sRX, state.Gamepad.sRY,
                    state.Gamepad.bLT, state.Gamepad.bRT,
                    state.Gamepad.wButtons, state.dwPacket);
            }
        }
        printf("\r");
        Sleep(100);
    }

    FreeLibrary(h);
    return 0;
}
