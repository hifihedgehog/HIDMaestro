/*
 * hmswd.c — HIDMaestro SWD-enumerated device creation helper.
 *
 * Why a separate native binary: .NET 10 P/Invoke to cfgmgr32!SwDeviceCreate
 * on Win11 26200 returns hr=0x8007007E (ERROR_MOD_NOT_FOUND) synchronously,
 * while the identical C call succeeds. Neither CoInitializeEx, preloaded
 * DLLs, UnmanagedCallersOnly function pointers, nor explicit function-ptr
 * marshaling fixed it. Rather than ship a broken .NET migration path, this
 * small native helper takes HWID / ContainerID / description arguments,
 * calls SwDeviceCreate with SWDeviceLifetimeParentPresent so the device
 * persists past process exit, prints the resulting instance-id to stdout,
 * and exits. The SDK invokes it via Process.Start and records the instance
 * ID for later DIF_REMOVE teardown.
 *
 * Device removal uses standard PnP (DIF_REMOVE / devcon / pnputil) — no
 * special "destroy" command is needed because SWDeviceLifetimeParentPresent
 * keeps the device alive until reboot or explicit removal.
 *
 * Usage:
 *   hmswd.exe create <enumerator> <instance-id-suffix> <container-guid>
 *                    <hw-ids-csv> <compat-ids-csv> <description>
 *   hmswd.exe remove <enumerator> <instance-id-suffix> <container-guid>
 *                    <hw-ids-csv> <compat-ids-csv> <description>
 *       enumerator         string, becomes SWD\<enumerator>\<suffix>
 *       instance-id-suffix unique ID for the device
 *       container-guid     {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}
 *       hw-ids-csv         pipe-separated list of hardware IDs
 *       compat-ids-csv     pipe-separated list of compatible IDs (can be empty)
 *       description        human-readable description
 *
 *   `remove` reconnects to a previously-created SWD device by calling
 *   SwDeviceCreate with identical arguments (per the docs, this returns a
 *   fresh handle to the existing device), downgrades its lifetime from
 *   ParentPresent to Handle, then SwDeviceClose — which is the only
 *   documented path to tear down SWDeviceLifetimeParentPresent devices.
 *   pnputil /remove-device /force and DIF_REMOVE both silently no-op on
 *   FAILEDINSTALL-state SWD phantoms, which is why this helper exists.
 *
 * Exit codes:
 *   0   success (stdout: "OK <full-instance-id>" for create, "OK REMOVED" for remove)
 *   1   usage error
 *   2   SwDeviceCreate HRESULT (stderr: "ERR <hresult>")
 *   3   callback timeout
 *   4   callback returned non-zero HRESULT
 */
#define INITGUID
#include <windows.h>
#include <stdio.h>
#include <wchar.h>
#include <stdarg.h>
#include <initguid.h>
#include <swdevice.h>
#include <cfgmgr32.h>
#pragma comment(lib, "cfgmgr32.lib")

static HANDLE  g_done;
static HRESULT g_cb_hr = E_FAIL;
static WCHAR   g_inst_id[256];

/* Self-log: write every step's hresult to %TEMP%\HIDMaestro\hmswd_self.log
 * so we can diagnose silent no-ops (exit=0 is not proof of effect when PnP
 * refuses the operation). Appended, never truncated — rotate manually if
 * it grows. */
static void self_log(const wchar_t *fmt, ...)
{
    wchar_t path[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"TEMP", path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return;
    wcscat_s(path, MAX_PATH, L"\\HIDMaestro\\hmswd_self.log");

    FILE *f = NULL;
    if (_wfopen_s(&f, path, L"a, ccs=UTF-8") != 0 || !f) return;

    SYSTEMTIME st; GetLocalTime(&st);
    fwprintf(f, L"[%04d-%02d-%02d %02d:%02d:%02d.%03d] ",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    va_list ap;
    va_start(ap, fmt);
    vfwprintf(f, fmt, ap);
    va_end(ap);

    fwprintf(f, L"\n");
    fclose(f);
}

static VOID WINAPI CreationCallback(
    HSWDEVICE hSwDevice,
    HRESULT   CreateResult,
    PVOID     pContext,
    PCWSTR    pszDeviceInstanceId)
{
    (void)hSwDevice; (void)pContext;
    g_cb_hr = CreateResult;
    if (pszDeviceInstanceId)
        wcsncpy_s(g_inst_id, 256, pszDeviceInstanceId, _TRUNCATE);
    SetEvent(g_done);
}

/* Replace '|' separators with NUL to build a PCZZWSTR. Returns heap alloc.
 * Caller must free with free(). 'outCount' gets the number of strings. */
static wchar_t *make_multi_sz(const wchar_t *pipe_list, size_t *outLen)
{
    if (!pipe_list) { *outLen = 2; wchar_t *buf = (wchar_t*)calloc(2, sizeof(wchar_t)); return buf; }
    size_t n = wcslen(pipe_list);
    /* n chars + extra for final double-null */
    wchar_t *buf = (wchar_t*)calloc(n + 2, sizeof(wchar_t));
    for (size_t i = 0; i < n; i++) buf[i] = (pipe_list[i] == L'|') ? L'\0' : pipe_list[i];
    buf[n]     = 0;
    buf[n + 1] = 0;
    *outLen = n + 2;
    return buf;
}

static int parse_guid(const wchar_t *s, GUID *out)
{
    /* Accept either {XXXXXXXX-...} or bare form */
    IID iid;
    HRESULT hr = IIDFromString(s, &iid);
    if (FAILED(hr)) return 0;
    *out = iid;
    return 1;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc < 2) {
usage:
        fwprintf(stderr,
            L"Usage: hmswd.exe {create|remove} <enumerator> <suffix> <{container-guid}> "
            L"<hw-ids-pipe-sep> <compat-ids-pipe-sep> <description>\n");
        return 1;
    }

    int isCreate = _wcsicmp(argv[1], L"create") == 0;
    int isRemove = _wcsicmp(argv[1], L"remove") == 0;
    if (!isCreate && !isRemove) goto usage;
    if (argc < 8) goto usage;

    const wchar_t *enumerator  = argv[2];
    const wchar_t *suffix      = argv[3];
    const wchar_t *guid_str    = argv[4];
    const wchar_t *hw_list     = argv[5];
    const wchar_t *compat_list = argv[6];
    const wchar_t *desc        = argv[7];

    GUID container;
    if (!parse_guid(guid_str, &container)) {
        fwprintf(stderr, L"ERR BAD_GUID: %s\n", guid_str);
        return 1;
    }

    size_t hwLen, compLen;
    wchar_t *hw_multi    = make_multi_sz(hw_list,     &hwLen);
    wchar_t *compat_multi = make_multi_sz(compat_list, &compLen);

    SW_DEVICE_CREATE_INFO ci = {0};
    ci.cbSize               = sizeof(ci);
    ci.pszInstanceId        = suffix;
    ci.pszzHardwareIds      = hw_multi;
    ci.pszzCompatibleIds    = compat_multi;
    ci.pContainerId         = &container;
    // SilentInstall + NoDisplayInUI suppress UAC prompts and Device-Setup
    // balloons. For create we also pass DriverRequired so SwDeviceCreate
    // blocks until the matching INF is installed and bound. For remove we
    // intentionally drop DriverRequired — the device we're reconnecting to
    // may be in CONFIGFLAG_FAILEDINSTALL state (the whole reason we're
    // removing it), and DriverRequired would make SwDeviceCreate block
    // forever waiting for a bind that will never succeed.
    ci.CapabilityFlags      = SWDeviceCapabilitiesSilentInstall
                            | SWDeviceCapabilitiesNoDisplayInUI;
    if (isCreate) ci.CapabilityFlags |= SWDeviceCapabilitiesDriverRequired;
    ci.pszDeviceDescription = desc;

    self_log(L"==== %s %s\\%s container=%s ====",
        isCreate ? L"CREATE" : L"REMOVE", enumerator, suffix, guid_str);

    g_done = CreateEventW(NULL, TRUE, FALSE, NULL);
    HSWDEVICE hDev = NULL;

    HRESULT hr = SwDeviceCreate(
        enumerator,
        L"HTREE\\ROOT\\0",
        &ci, 0, NULL,
        CreationCallback, NULL,
        &hDev);

    self_log(L"  SwDeviceCreate hr=0x%08X hDev=%p", hr, hDev);

    if (FAILED(hr)) {
        fwprintf(stderr, L"ERR SwDeviceCreate: 0x%08X\n", hr);
        free(hw_multi);
        free(compat_multi);
        return 2;
    }

    /* Poll BOTH the callback event AND CM_Locate_DevNodeW / CM_Get_DevNode_Status
     * so we can observe exactly when the device transitions into the PnP
     * tree even if the callback never fires. Log every 500ms so we can
     * diagnose the phantom-15s-wait on subsequent-run SwDeviceCreates. */
    DWORD wait = WAIT_TIMEOUT;
    wchar_t expectedInstId[512];
    swprintf_s(expectedInstId, 512, L"SWD\\%s\\%s", enumerator, suffix);
    DWORD startTick = GetTickCount();
    DWORD lastLogTick = startTick;
    int firstSeenAt = -1;
    for (int polled = 0; polled < 600; polled++) {     /* 60s upper bound */
        if (WaitForSingleObject(g_done, 100) == WAIT_OBJECT_0) {
            wait = WAIT_OBJECT_0;
            self_log(L"  callback fired at %dms", GetTickCount() - startTick);
            break;
        }
        DEVINST di;
        CONFIGRET cr = CM_Locate_DevNodeW(&di, expectedInstId, CM_LOCATE_DEVNODE_NORMAL);
        ULONG devStatus = 0, problem = 0;
        if (cr == CR_SUCCESS) {
            CM_Get_DevNode_Status(&devStatus, &problem, di, 0);
            if (firstSeenAt < 0) {
                firstSeenAt = (int)(GetTickCount() - startTick);
                self_log(L"  CM_Locate found device at +%dms  status=0x%08X problem=%u", firstSeenAt, devStatus, problem);
            }
        }
        /* Log heartbeat every 2s so we see the stall in real time. */
        if (GetTickCount() - lastLogTick > 2000) {
            lastLogTick = GetTickCount();
            self_log(L"  poll +%dms  cm=%d devStatus=0x%08X problem=%u",
                lastLogTick - startTick, (int)cr, devStatus, problem);
        }
        /* Treat "live in PnP tree with DN_STARTED set" as authoritative success. */
        if (cr == CR_SUCCESS && (devStatus & 0x00000008 /*DN_STARTED*/) != 0) {
            wait = WAIT_OBJECT_0;
            g_cb_hr = S_OK;
            wcsncpy_s(g_inst_id, 256, expectedInstId, _TRUNCATE);
            self_log(L"  device DN_STARTED at +%dms (callback did not fire)", GetTickCount() - startTick);
            break;
        }
    }
    free(hw_multi);
    free(compat_multi);

    self_log(L"  callback wait=%u cb_hr=0x%08X inst_id=%s",
        wait, g_cb_hr, g_inst_id[0] ? g_inst_id : L"(null)");

    if (wait != WAIT_OBJECT_0) {
        fwprintf(stderr, L"ERR callback timeout\n");
        if (hDev) SwDeviceClose(hDev);
        return 3;
    }

    /* For create, a failed callback HR means install failed and we return
     * non-zero. For remove, we intentionally proceed even if the callback
     * reported an error: the point of remove is to clean up a broken-state
     * device, so a non-success callback HR on reconnect is expected and
     * does not prevent the lifetime downgrade from tearing the device down. */
    if (isCreate && FAILED(g_cb_hr)) {
        fwprintf(stderr, L"ERR callback hr=0x%08X\n", g_cb_hr);
        if (hDev) SwDeviceClose(hDev);
        return 4;
    }

    if (isCreate) {
        /* Lifetime = ParentPresent. Device persists past our exit. */
        SwDeviceSetLifetime(hDev, SWDeviceLifetimeParentPresent);
        wprintf(L"OK %s\n", g_inst_id);
        fflush(stdout);
        SwDeviceClose(hDev);
        return 0;
    }

    /* remove: downgrade lifetime to Handle, then Close — PnP will remove the
     * devnode when the last HSWDEVICE handle goes away. This is the only
     * documented path that tears down SWDeviceLifetimeParentPresent devices
     * stuck in FAILEDINSTALL state. */
    hr = SwDeviceSetLifetime(hDev, SWDeviceLifetimeHandle);
    self_log(L"  SwDeviceSetLifetime(Handle) hr=0x%08X", hr);
    if (FAILED(hr)) {
        fwprintf(stderr, L"ERR SwDeviceSetLifetime: 0x%08X\n", hr);
        SwDeviceClose(hDev);
        return 5;
    }
    SwDeviceClose(hDev);
    self_log(L"  SwDeviceClose returned — device should now be removed");
    wprintf(L"OK REMOVED\n");
    fflush(stdout);
    return 0;
}
