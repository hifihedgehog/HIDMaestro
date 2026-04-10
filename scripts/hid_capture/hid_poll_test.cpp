// hid_poll_test.cpp — try multiple HID class read paths against a device
//   1) HidD_GetInputReport (polled, returns cached current state)
//   2) ReadFile blocking with sync handle (no overlapped)
//   3) ReadFile overlapped (already tested elsewhere)
//
// Purpose: figure out WHY hid_capture saw zero reports from the real
// Xbox BT controller. Is HID class delivery blocked entirely, or just
// the change-event path?

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <hidsdi.h>
#include <hidpi.h>
#include <SetupAPI.h>
#include <cstdio>
#include <cstdlib>
#include <vector>
#include <string>

#pragma comment(lib, "hid.lib")
#pragma comment(lib, "setupapi.lib")

static std::wstring FindDevice(USHORT vid, USHORT pid)
{
    GUID hidGuid; HidD_GetHidGuid(&hidGuid);
    HDEVINFO devs = SetupDiGetClassDevsW(&hidGuid, nullptr, nullptr,
                                          DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (devs == INVALID_HANDLE_VALUE) return L"";
    SP_DEVICE_INTERFACE_DATA ifData{}; ifData.cbSize = sizeof(ifData);
    std::wstring result;
    for (DWORD i = 0; SetupDiEnumDeviceInterfaces(devs, nullptr, &hidGuid, i, &ifData); ++i) {
        DWORD reqSize = 0;
        SetupDiGetDeviceInterfaceDetailW(devs, &ifData, nullptr, 0, &reqSize, nullptr);
        if (!reqSize) continue;
        std::vector<BYTE> buf(reqSize);
        auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(buf.data());
        detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);
        if (!SetupDiGetDeviceInterfaceDetailW(devs, &ifData, detail, reqSize, nullptr, nullptr))
            continue;
        HANDLE h = CreateFileW(detail->DevicePath, 0,
                               FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                               OPEN_EXISTING, 0, nullptr);
        if (h == INVALID_HANDLE_VALUE) continue;
        HIDD_ATTRIBUTES a{}; a.Size = sizeof(a);
        if (HidD_GetAttributes(h, &a) && a.VendorID == vid && a.ProductID == pid) {
            result = detail->DevicePath;
            CloseHandle(h);
            break;
        }
        CloseHandle(h);
    }
    SetupDiDestroyDeviceInfoList(devs);
    return result;
}

static void DumpBytes(const char* label, const BYTE* buf, ULONG len)
{
    printf("  %s (%lu bytes):\n    ", label, len);
    for (ULONG i = 0; i < len; ++i) printf("%02X ", buf[i]);
    printf("\n");
}

int main(int argc, char** argv)
{
    USHORT vid = (argc > 1) ? (USHORT)strtoul(argv[1], nullptr, 16) : 0x045E;
    USHORT pid = (argc > 2) ? (USHORT)strtoul(argv[2], nullptr, 16) : 0x0B13;

    std::wstring path = FindDevice(vid, pid);
    if (path.empty()) {
        fprintf(stderr, "ERROR: no device VID=%04X PID=%04X\n", vid, pid);
        return 2;
    }
    char p8[512];
    WideCharToMultiByte(CP_UTF8, 0, path.c_str(), -1, p8, sizeof(p8), nullptr, nullptr);
    printf("Device: %s\n\n", p8);

    // Test 1: open with various access flags, see what works
    struct OpenAttempt {
        const char* label;
        DWORD access;
        DWORD share;
        DWORD flags;
    } attempts[] = {
        { "GENERIC_READ|WRITE share=R|W flags=OVERLAPPED", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_FLAG_OVERLAPPED },
        { "GENERIC_READ        share=R|W flags=OVERLAPPED", GENERIC_READ,                FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_FLAG_OVERLAPPED },
        { "GENERIC_READ        share=R   flags=OVERLAPPED", GENERIC_READ,                FILE_SHARE_READ,                    FILE_FLAG_OVERLAPPED },
        { "0 access            share=R|W flags=0",          0,                           FILE_SHARE_READ | FILE_SHARE_WRITE, 0 },
        { "GENERIC_READ|WRITE share=R|W flags=0 (sync)",   GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, 0 },
    };
    for (auto& a : attempts) {
        HANDLE h = CreateFileW(path.c_str(), a.access, a.share, nullptr,
                               OPEN_EXISTING, a.flags, nullptr);
        if (h == INVALID_HANDLE_VALUE) {
            printf("OPEN: %s\n  FAILED err=%lu\n", a.label, GetLastError());
        } else {
            printf("OPEN: %s  -> ok\n", a.label);
            CloseHandle(h);
        }
    }
    printf("\n");

    // Test 2: HidD_GetInputReport (polled cached state)
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE,
                           FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                           OPEN_EXISTING, 0, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        printf("re-open for sync test failed err=%lu\n", GetLastError());
        return 3;
    }
    PHIDP_PREPARSED_DATA pp = nullptr;
    HidD_GetPreparsedData(h, &pp);
    HIDP_CAPS caps{};
    HidP_GetCaps(pp, &caps);
    printf("InputReportByteLength = %u\n", caps.InputReportByteLength);

    // Try GET_INPUT_REPORT with report ID 0
    {
        std::vector<BYTE> buf(caps.InputReportByteLength, 0);
        BOOL ok = HidD_GetInputReport(h, buf.data(), (ULONG)buf.size());
        printf("HidD_GetInputReport (id=0): %s err=%lu\n",
               ok ? "OK" : "FAIL", ok ? 0 : GetLastError());
        if (ok) DumpBytes("  cached state", buf.data(), (ULONG)buf.size());
    }

    HidD_FreePreparsedData(pp);
    CloseHandle(h);

    // Test 3: synchronous ReadFile (no overlapped) with 3-second wait via single read
    printf("\nTest 3: SYNCHRONOUS ReadFile, single read, expect to block until next report or controller event\n");
    HANDLE h2 = CreateFileW(path.c_str(), GENERIC_READ | GENERIC_WRITE,
                            FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                            OPEN_EXISTING, 0, nullptr);
    if (h2 == INVALID_HANDLE_VALUE) {
        printf("sync open failed err=%lu\n", GetLastError());
        return 4;
    }
    std::vector<BYTE> rbuf(caps.InputReportByteLength + 1, 0);
    DWORD bytesRead = 0;
    printf("  blocking ReadFile (will hang up to ~5s if nothing arrives)...\n");
    fflush(stdout);

    // Use a thread to time-out after 5 seconds via CancelSynchronousIo
    HANDLE mainThread = OpenThread(THREAD_ALL_ACCESS, FALSE, GetCurrentThreadId());
    DWORD startMs = GetTickCount();
    HANDLE cancelTimer = CreateThread(nullptr, 0, [](LPVOID p) -> DWORD {
        Sleep(5000);
        CancelSynchronousIo((HANDLE)p);
        return 0;
    }, mainThread, 0, nullptr);

    BOOL ok = ReadFile(h2, rbuf.data(), caps.InputReportByteLength, &bytesRead, nullptr);
    DWORD lerr = GetLastError();
    DWORD elapsed = GetTickCount() - startMs;

    if (cancelTimer) { WaitForSingleObject(cancelTimer, INFINITE); CloseHandle(cancelTimer); }
    if (mainThread) CloseHandle(mainThread);

    if (ok && bytesRead > 0) {
        printf("  ReadFile returned ok in %lu ms with %lu bytes\n", elapsed, bytesRead);
        DumpBytes("  report bytes", rbuf.data(), bytesRead);
    } else {
        printf("  ReadFile returned ok=%d bytes=%lu err=%lu after %lu ms\n",
               ok, bytesRead, lerr, elapsed);
    }
    CloseHandle(h2);
    return 0;
}
