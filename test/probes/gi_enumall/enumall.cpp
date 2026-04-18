// Enumerate ALL gamepads GameInput sees via RegisterDeviceCallback.
// Print each one's VID/PID + supportedRumbleMotors. Then on the one with
// MOST motors (or any motor), call SetRumbleState for 3s.

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
    uint32_t mappedRumble;
    char name[64];
};

static std::mutex g_mtx;
static std::vector<DeviceInfo> g_devs;

static void CALLBACK DeviceCb(
    GameInputCallbackToken callbackToken,
    void * context,
    IGameInputDevice * device,
    uint64_t timestamp,
    GameInputDeviceStatus currentStatus,
    GameInputDeviceStatus previousStatus)
{
    (void)callbackToken; (void)context; (void)timestamp; (void)previousStatus;
    if (!(currentStatus & GameInputDeviceConnected)) return;
    const GameInputDeviceInfo* di = device->GetDeviceInfo();
    if (!di) return;
    DeviceInfo rec{};
    rec.dev = device;
    device->AddRef();
    rec.vid = di->vendorId;
    rec.pid = di->productId;
    rec.supportedRumble = (uint32_t)di->supportedRumbleMotors;
    rec.mappedRumble = 0;  // mappedRumbleMotors is per-motor on HapticFeedbackMotorInfo
    snprintf(rec.name, sizeof(rec.name), "(no name)");
    {
        std::lock_guard<std::mutex> lk(g_mtx);
        g_devs.push_back(rec);
    }
    printf("[cb] device: VID=0x%04X PID=0x%04X supportedRumble=0x%X mappedRumble=0x%X\n",
        rec.vid, rec.pid, rec.supportedRumble, rec.mappedRumble);
}

int main()
{
    IGameInput* gi = nullptr;
    HRESULT hr = GameInputCreate(&gi);
    if (FAILED(hr)) { printf("GameInputCreate failed: 0x%08lX\n", hr); return 1; }
    printf("GameInputCreate OK\n");

    GameInputCallbackToken tok = 0;
    hr = gi->RegisterDeviceCallback(
        nullptr,
        GameInputKindGamepad,
        GameInputDeviceConnected,
        GameInputBlockingEnumeration,
        nullptr,
        DeviceCb,
        &tok);
    if (FAILED(hr)) { printf("RegisterDeviceCallback failed: 0x%08lX\n", hr); gi->Release(); return 1; }
    printf("Enumeration started (BlockingEnumeration). Waiting 2s for devices to surface...\n");
    Sleep(2000);
    gi->UnregisterCallback(tok, 0);

    std::vector<DeviceInfo> devs;
    { std::lock_guard<std::mutex> lk(g_mtx); devs = g_devs; }
    printf("\n=== Total gamepads enumerated: %zu ===\n", devs.size());
    for (size_t i = 0; i < devs.size(); i++) {
        printf("[%zu] VID=0x%04X PID=0x%04X supportedRumble=0x%X mappedRumble=0x%X\n",
            i, devs[i].vid, devs[i].pid, devs[i].supportedRumble, devs[i].mappedRumble);
    }

    // For every device with non-zero supportedRumble, fire SetRumbleState for 1.5s each.
    for (size_t i = 0; i < devs.size(); i++) {
        if (devs[i].supportedRumble == 0) {
            printf("\nSkipping [%zu] VID=0x%04X PID=0x%04X (no motors).\n",
                i, devs[i].vid, devs[i].pid);
            continue;
        }
        printf("\n>>> Firing SetRumbleState(ALL=1.0) on [%zu] VID=0x%04X PID=0x%04X for 1.5s...\n",
            i, devs[i].vid, devs[i].pid);
        GameInputRumbleParams p = {};
        p.lowFrequency = 1.0f; p.highFrequency = 1.0f;
        p.leftTrigger = 1.0f;  p.rightTrigger = 1.0f;
        devs[i].dev->SetRumbleState(&p);
        Sleep(1500);
        GameInputRumbleParams z = {};
        devs[i].dev->SetRumbleState(&z);
        printf("<<< cleared\n");
    }

    // Additionally: for EVERY device, fire SetRumbleState regardless of reported
    // motor count (in case supportedRumble reporting is stale/wrong).
    printf("\n=== Second pass: fire SetRumbleState on EVERY device regardless of motor count ===\n");
    for (size_t i = 0; i < devs.size(); i++) {
        printf(">>> [%zu] VID=0x%04X PID=0x%04X SetRumbleState(ALL=1.0) 1.5s...\n",
            i, devs[i].vid, devs[i].pid);
        GameInputRumbleParams p = {};
        p.lowFrequency = 1.0f; p.highFrequency = 1.0f;
        p.leftTrigger = 1.0f;  p.rightTrigger = 1.0f;
        devs[i].dev->SetRumbleState(&p);
        Sleep(1500);
        GameInputRumbleParams z = {};
        devs[i].dev->SetRumbleState(&z);
        printf("<<< cleared\n");
    }

    for (auto& d : g_devs) d.dev->Release();
    gi->Release();
    return 0;
}
