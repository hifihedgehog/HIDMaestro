// hid_capture.cpp — HID input report stream capture tool
//
// Opens a HID device by VID/PID and an instance index, reads input
// reports via overlapped ReadFile in a tight loop, and writes each
// report to stdout (and optionally a file) with a high-resolution
// timestamp prefix.
//
// Purpose: capture the post-xinputhid byte stream from BOTH a real
// xbox-series-xs-bt controller AND our virtual one, with the same
// user input state (A held, sticks at rest), so we can diff the two
// streams and find what bytes are actually different.
//
// Build: scripts\hid_capture\build.cmd
// Run:   sudo build\hid_capture.exe <vid> <pid> [seconds] [outfile]
//          vid/pid in hex, no 0x prefix, e.g.  045E 0B13
//          seconds defaults to 3
//          outfile is appended to; '-' means stdout only
//
// Examples:
//   sudo build\hid_capture.exe 045E 0B13 3 c:\tmp\real.log
//   sudo build\hid_capture.exe 045E 0B13 3 c:\tmp\virtual.log
//
// Lists all matching devices on startup so you can confirm the right
// one is being read (real vs virtual will both match VID/PID — pick
// the one with the right device path).

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <hidsdi.h>
#include <hidpi.h>
#include <SetupAPI.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#include <string>
#include <chrono>
#include <thread>

#pragma comment(lib, "hid.lib")
#pragma comment(lib, "setupapi.lib")

struct HidDev {
    std::wstring path;
    USHORT vid, pid;
    USHORT inputReportByteLength;
    bool   isVirtual;       // best-effort: true if path looks like a HIDMaestro virtual
};

// =============================================================================
// Discovery
// =============================================================================

static std::vector<HidDev> EnumMatching(USHORT wantVid, USHORT wantPid)
{
    std::vector<HidDev> out;
    GUID hidGuid; HidD_GetHidGuid(&hidGuid);
    HDEVINFO devs = SetupDiGetClassDevsW(&hidGuid, nullptr, nullptr,
                                          DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (devs == INVALID_HANDLE_VALUE) return out;

    SP_DEVICE_INTERFACE_DATA ifData{}; ifData.cbSize = sizeof(ifData);
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
        if (!HidD_GetAttributes(h, &a) || a.VendorID != wantVid || a.ProductID != wantPid) {
            CloseHandle(h); continue;
        }
        PHIDP_PREPARSED_DATA pp = nullptr;
        USHORT len = 0;
        if (HidD_GetPreparsedData(h, &pp)) {
            HIDP_CAPS caps{};
            if (HidP_GetCaps(pp, &caps) == HIDP_STATUS_SUCCESS)
                len = caps.InputReportByteLength;
            HidD_FreePreparsedData(pp);
        }
        CloseHandle(h);

        // The two virtual-controller path patterns we care about:
        //   \\?\hid#vid_xxxx&pid_xxxx&ig_00#1&839e9ca&...   ← virtual (HIDMaestro)
        //   \\?\hid#{00001812-...}&dev&vid_xxxx&pid_xxxx&...&a88c3e2c8a96&ig_00#... ← real BT
        std::wstring path = detail->DevicePath;
        bool isVirt = path.find(L"&839e9ca&") != std::wstring::npos
                   || path.find(L"&839E9CA&") != std::wstring::npos
                   || path.find(L"&1af39a6f&") != std::wstring::npos
                   || path.find(L"&2dad4b14&") != std::wstring::npos;

        out.push_back(HidDev{ path, a.VendorID, a.ProductID, len, isVirt });
    }
    SetupDiDestroyDeviceInfoList(devs);
    return out;
}

// =============================================================================
// Capture loop
// =============================================================================

static int CaptureStream(const std::wstring& devicePath, double durationSec, FILE* logFile)
{
    HANDLE h = CreateFileW(devicePath.c_str(), GENERIC_READ | GENERIC_WRITE,
                           FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                           OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
    if (h == INVALID_HANDLE_VALUE) {
        fprintf(stderr, "ERROR: CreateFile failed lerr=%lu\n", GetLastError());
        return 2;
    }

    PHIDP_PREPARSED_DATA pp = nullptr;
    if (!HidD_GetPreparsedData(h, &pp)) {
        fprintf(stderr, "ERROR: HidD_GetPreparsedData failed\n");
        CloseHandle(h);
        return 2;
    }
    HIDP_CAPS caps{};
    HidP_GetCaps(pp, &caps);
    HidD_FreePreparsedData(pp);

    ULONG reportLen = caps.InputReportByteLength;
    if (reportLen == 0) {
        fprintf(stderr, "ERROR: zero-length input report\n");
        CloseHandle(h);
        return 2;
    }
    fprintf(stderr, "InputReportByteLength = %lu\n", reportLen);
    if (logFile) fprintf(logFile, "# InputReportByteLength = %lu\n", reportLen);

    auto t0 = std::chrono::steady_clock::now();
    auto deadline = t0 + std::chrono::duration_cast<std::chrono::steady_clock::duration>(
                              std::chrono::duration<double>(durationSec));

    std::vector<BYTE> buf(reportLen);
    int reportCount = 0;
    int identicalCount = 0;
    std::vector<BYTE> lastReport(reportLen, 0xFF); // sentinel: never matches first

    OVERLAPPED ov{};
    ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);

    while (std::chrono::steady_clock::now() < deadline) {
        ResetEvent(ov.hEvent);
        DWORD bytesRead = 0;
        BOOL ok = ReadFile(h, buf.data(), reportLen, &bytesRead, &ov);
        if (!ok && GetLastError() == ERROR_IO_PENDING) {
            DWORD waitMs = (DWORD)std::chrono::duration_cast<std::chrono::milliseconds>(
                deadline - std::chrono::steady_clock::now()).count();
            if (waitMs == 0) waitMs = 1;
            DWORD wait = WaitForSingleObject(ov.hEvent, waitMs);
            if (wait != WAIT_OBJECT_0) {
                CancelIo(h);
                break;
            }
            GetOverlappedResult(h, &ov, &bytesRead, FALSE);
        }
        if (bytesRead == 0) continue;

        auto now = std::chrono::steady_clock::now();
        double tSec = std::chrono::duration<double>(now - t0).count();

        // Compare to last
        bool same = (reportCount > 0)
                 && std::memcmp(buf.data(), lastReport.data(), reportLen) == 0;

        if (same) ++identicalCount;
        std::memcpy(lastReport.data(), buf.data(), reportLen);

        // Log: t in ms, "S" for same/"D" for diff, then hex bytes
        char prefix[32];
        snprintf(prefix, sizeof(prefix), "%9.4f %c ",
                 tSec * 1000.0, same ? 'S' : 'D');
        fputs(prefix, stderr);
        if (logFile) fputs(prefix, logFile);
        for (DWORD k = 0; k < bytesRead; ++k) {
            fprintf(stderr, "%02X ", buf[k]);
            if (logFile) fprintf(logFile, "%02X ", buf[k]);
        }
        fputc('\n', stderr);
        if (logFile) fputc('\n', logFile);

        ++reportCount;
    }
    CloseHandle(ov.hEvent);
    CloseHandle(h);

    fprintf(stderr, "\n=== summary ===\n");
    fprintf(stderr, "  reports captured: %d\n", reportCount);
    fprintf(stderr, "  byte-identical to previous: %d\n", identicalCount);
    fprintf(stderr, "  bytes-changed-vs-previous: %d\n", reportCount - identicalCount);
    if (logFile) {
        fprintf(logFile, "# reports=%d identical=%d changed=%d\n",
                reportCount, identicalCount, reportCount - identicalCount);
    }
    return reportCount > 0 ? 0 : 3;
}

// =============================================================================
// Main
// =============================================================================

int main(int argc, char** argv)
{
    if (argc < 3) {
        fprintf(stderr,
            "Usage: hid_capture <vid> <pid> [seconds=3] [logfile|-]\n"
            "  e.g.  hid_capture 045E 0B13 3 c:\\tmp\\real.log\n");
        return 1;
    }
    USHORT vid = (USHORT)strtoul(argv[1], nullptr, 16);
    USHORT pid = (USHORT)strtoul(argv[2], nullptr, 16);
    double seconds = (argc >= 4) ? atof(argv[3]) : 3.0;
    const char* logArg = (argc >= 5) ? argv[4] : "-";

    fprintf(stderr, "hid_capture: VID=%04X PID=%04X duration=%.1fs\n",
            vid, pid, seconds);

    auto devs = EnumMatching(vid, pid);
    if (devs.empty()) {
        fprintf(stderr, "ERROR: no matching HID devices found\n");
        return 2;
    }
    fprintf(stderr, "Found %zu matching device(s):\n", devs.size());
    for (size_t i = 0; i < devs.size(); ++i) {
        char path[512];
        WideCharToMultiByte(CP_UTF8, 0, devs[i].path.c_str(), -1, path, sizeof(path), nullptr, nullptr);
        fprintf(stderr, "  [%zu] inputLen=%u  %s%s\n",
                i, devs[i].inputReportByteLength,
                devs[i].isVirtual ? "(VIRTUAL?) " : "",
                path);
    }

    // Pick: if exactly one device, use it. Otherwise prompt the user via env.
    int pick = 0;
    if (devs.size() > 1) {
        const char* env = getenv("HID_CAPTURE_INDEX");
        if (env) pick = atoi(env);
        else {
            // Default heuristic: pick the LAST one (likely the most-recently-added,
            // which is usually the one we just spun up).
            pick = (int)devs.size() - 1;
            fprintf(stderr, "  -> auto-selected index %d. Set HID_CAPTURE_INDEX env to override.\n", pick);
        }
    }
    if (pick < 0 || pick >= (int)devs.size()) {
        fprintf(stderr, "ERROR: HID_CAPTURE_INDEX=%d out of range\n", pick);
        return 1;
    }
    fprintf(stderr, "Capturing index %d for %.1fs...\n\n", pick, seconds);

    FILE* logFile = nullptr;
    if (strcmp(logArg, "-") != 0) {
        logFile = fopen(logArg, "w");
        if (!logFile) {
            fprintf(stderr, "ERROR: cannot open logfile '%s'\n", logArg);
            return 1;
        }
        fprintf(logFile, "# hid_capture VID=%04X PID=%04X duration=%.1fs\n",
                vid, pid, seconds);
        char path[512];
        WideCharToMultiByte(CP_UTF8, 0, devs[pick].path.c_str(), -1, path, sizeof(path), nullptr, nullptr);
        fprintf(logFile, "# device: %s\n", path);
    }

    int rc = CaptureStream(devs[pick].path, seconds, logFile);
    if (logFile) fclose(logFile);
    return rc;
}
