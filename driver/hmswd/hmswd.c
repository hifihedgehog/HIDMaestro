/*
 * hmswd.c: HIDMaestro SWD-enumerated device creation helper.
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
 * ID. The device stays until `remove` below takes it away.
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
 *   ParentPresent to Handle, then SwDeviceClose: which is the only
 *   documented path to tear down SWDeviceLifetimeParentPresent devices.
 *   pnputil /remove-device /force and DIF_REMOVE both silently no-op on
 *   FAILEDINSTALL-state SWD phantoms, which is why this helper exists.
 *
 * Steps: create and remove print each step they reach to stdout, one
 * line each, flushed at once, so the SDK can tell how far a run it waits
 * for has got. "STEP STARTED" comes before SwDeviceCreate, "STEP CREATED"
 * once it returns S_OK, "STEP ENUMERATED" once the callback has fired or
 * the devnode is found started, and "STEP CLOSING <code>" right before
 * every SwDeviceClose, with the exit code the helper returns after it. A
 * create prints "OK <full-instance-id>" once the device's lifetime is
 * ParentPresent, so the device outlives the handle. A remove prints "STEP
 * LIFETIME HANDLE" once the lifetime is Handle: from then on the handle's
 * close removes the device, whether SwDeviceClose closes it or the end of
 * the process does. SwDeviceClose "returns before this removal is
 * complete" (Learn), so after CLOSING only the close is left. One that
 * does not return (issue #18, a stale WUDFHost holding the device) can be
 * ended by stopping the process, which closes the handle the same way.
 *
 * Exit codes:
 *   0   success (stdout: "OK <full-instance-id>" for create, "OK REMOVED" for remove)
 *   1   usage error
 *   2   SwDeviceCreate HRESULT (stderr: "ERR SwDeviceCreate: <hresult>")
 *   3   callback timeout (stderr: "ERR callback timeout")
 *   4   callback returned a failure HRESULT, create only
 *       (stderr: "ERR callback hr=<hresult>")
 *   5   SwDeviceSetLifetime HRESULT (stderr: "ERR SwDeviceSetLifetime: <hresult>"),
 *       for a create only after its creation callback
 */
#define INITGUID
#include <windows.h>
#include <stdio.h>
#include <wchar.h>
#include <initguid.h>
#include <swdevice.h>
#include <cfgmgr32.h>
#pragma comment(lib, "cfgmgr32.lib")
#include <newdev.h>
#pragma comment(lib, "newdev.lib")

static HANDLE  g_done;
static HRESULT g_cb_hr = E_FAIL;
static WCHAR   g_inst_id[256];

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

/* One step a create or remove has reached (see Steps above). */
static void step(const wchar_t *what)
{
    wprintf(L"STEP %s\n", what);
    fflush(stdout);
}

/* Close the handle, after saying so with the exit code that follows. */
static int close_and_exit(HSWDEVICE hDev, int code)
{
    wprintf(L"STEP CLOSING %d\n", code);
    fflush(stdout);
    if (hDev) SwDeviceClose(hDev);
    return code;
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
            L"<hw-ids-pipe-sep> <compat-ids-pipe-sep> <description>\n"
            L"       hmswd.exe force-driver <inf-path> <hardware-id>\n");
        return 1;
    }

    /* force-driver <inf> <hardware-id>: put one INF on every device that
       carries the hardware id, even when a package with a higher DriverVer
       is in the store. The SDK uses it to move the usbip-win2 host
       controller from 0.9.7.7 to the pinned release, and runs it here
       rather than in its own process so that a driver that will not let
       go can be abandoned on a timeout instead of freezing the consumer.
       Exit 0 done, 3010 done and Windows wants a restart, otherwise the
       Win32 error. */
    if (_wcsicmp(argv[1], L"force-driver") == 0) {
        if (argc < 4) goto usage;
        BOOL reboot = FALSE;
        if (!UpdateDriverForPlugAndPlayDevicesW(NULL, argv[3], argv[2], INSTALLFLAG_FORCE, &reboot)) {
            DWORD err = GetLastError();
            fwprintf(stderr, L"force-driver failed: 0x%08lX\n", err);
            return err ? (int)err : 1;
        }
        return reboot ? 3010 : 0;
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
    // intentionally drop DriverRequired: the device we're reconnecting to
    // may be in CONFIGFLAG_FAILEDINSTALL state (the whole reason we're
    // removing it), and DriverRequired would make SwDeviceCreate block
    // forever waiting for a bind that will never succeed.
    ci.CapabilityFlags      = SWDeviceCapabilitiesSilentInstall
                            | SWDeviceCapabilitiesNoDisplayInUI;
    if (isCreate) ci.CapabilityFlags |= SWDeviceCapabilitiesDriverRequired;
    ci.pszDeviceDescription = desc;

    g_done = CreateEventW(NULL, TRUE, FALSE, NULL);
    HSWDEVICE hDev = NULL;

    step(L"STARTED");
    HRESULT hr = SwDeviceCreate(
        enumerator,
        L"HTREE\\ROOT\\0",
        &ci, 0, NULL,
        CreationCallback, NULL,
        &hDev);

    if (FAILED(hr)) {
        fwprintf(stderr, L"ERR SwDeviceCreate: 0x%08X\n", hr);
        free(hw_multi);
        free(compat_multi);
        return 2;
    }
    step(L"CREATED");

    /* Poll BOTH the callback event AND CM_Locate_DevNodeW / CM_Get_DevNode_Status,
     * so a create whose device reaches the PnP tree started is seen even if
     * the callback never fires. */
    DWORD wait = WAIT_TIMEOUT;
    int startedWithoutCallback = 0;
    wchar_t expectedInstId[512];
    swprintf_s(expectedInstId, 512, L"SWD\\%s\\%s", enumerator, suffix);
    for (int polled = 0; polled < 600; polled++) {     /* 60s upper bound */
        if (WaitForSingleObject(g_done, 100) == WAIT_OBJECT_0) {
            wait = WAIT_OBJECT_0;
            break;
        }
        DEVINST di;
        CONFIGRET cr = CM_Locate_DevNodeW(&di, expectedInstId, CM_LOCATE_DEVNODE_NORMAL);
        ULONG devStatus = 0, problem = 0;
        if (cr == CR_SUCCESS)
            CM_Get_DevNode_Status(&devStatus, &problem, di, 0);
        /* Treat "live in PnP tree with DN_STARTED set" as authoritative
         * success: but only for CREATE. The fast path was added for
         * the subsequent-run SwDeviceCreate reuse-fast-path scenario
         * where the callback never fires even though the devnode is
         * live. For REMOVE, the SwDevice handle returned by
         * SwDeviceCreate must be fully bound (callback fired) before
         * SwDeviceSetLifetime(Handle) is legal: taking the fast path
         * on REMOVE leaves hDev half-bound and SetLifetime fails with
         * hr=0x80070032 ERROR_NOT_SUPPORTED, after which a subsequent
         * SwDeviceClose does NOT downgrade lifetime, the kernel
         * re-enumerates the SWDeviceLifetimeParentPresent devnode (and
         * any HID children with bumped instance IDs &1&), and the
         * harness's leftover detector trips. Reproduces deterministically
         * on Atom under S08/S11's parallel teardown stress. */
        if (!isRemove && cr == CR_SUCCESS && (devStatus & 0x00000008 /*DN_STARTED*/) != 0) {
            wait = WAIT_OBJECT_0;
            startedWithoutCallback = 1;
            g_cb_hr = S_OK;
            wcsncpy_s(g_inst_id, 256, expectedInstId, _TRUNCATE);
            break;
        }
    }
    free(hw_multi);
    free(compat_multi);

    if (wait != WAIT_OBJECT_0) {
        fwprintf(stderr, L"ERR callback timeout\n");
        return close_and_exit(hDev, 3);
    }
    step(L"ENUMERATED");

    /* For create, a failed callback HR means install failed and we return
     * non-zero. For remove, we intentionally proceed even if the callback
     * reported an error: the point of remove is to clean up a broken-state
     * device, so a non-success callback HR on reconnect is expected and
     * does not prevent the lifetime downgrade from tearing the device down. */
    if (isCreate && FAILED(g_cb_hr)) {
        fwprintf(stderr, L"ERR callback hr=0x%08X\n", g_cb_hr);
        return close_and_exit(hDev, 4);
    }

    if (isCreate) {
        /* Lifetime = ParentPresent. Device persists past our exit. After the
         * creation callback, a lifetime that did not take would leave the
         * default Handle lifetime, and the close below would remove the
         * device reported created, so the create fails instead. A devnode
         * found started without a callback is the reuse fast path above:
         * Learn allows SwDeviceSetLifetime only after the callback, and a
         * device an earlier create marked ParentPresent keeps that lifetime
         * when SwDeviceCreate reconnects to it, so a failure there is
         * printed and changes nothing, as before. */
        hr = SwDeviceSetLifetime(hDev, SWDeviceLifetimeParentPresent);
        if (FAILED(hr) && !startedWithoutCallback) {
            fwprintf(stderr, L"ERR SwDeviceSetLifetime: 0x%08X\n", hr);
            return close_and_exit(hDev, 5);
        }
        if (FAILED(hr)) {
            wprintf(L"STEP LIFETIME UNCHANGED 0x%08X\n", (unsigned)hr);
            fflush(stdout);
        }
        wprintf(L"OK %s\n", g_inst_id);
        fflush(stdout);
        return close_and_exit(hDev, 0);
    }

    /* remove: downgrade lifetime to Handle, then Close: PnP will remove the
     * devnode when the last HSWDEVICE handle goes away. This is the only
     * documented path that tears down SWDeviceLifetimeParentPresent devices
     * stuck in FAILEDINSTALL state. */
    hr = SwDeviceSetLifetime(hDev, SWDeviceLifetimeHandle);
    if (FAILED(hr)) {
        fwprintf(stderr, L"ERR SwDeviceSetLifetime: 0x%08X\n", hr);
        return close_and_exit(hDev, 5);
    }
    step(L"LIFETIME HANDLE");
    close_and_exit(hDev, 0);
    wprintf(L"OK REMOVED\n");
    fflush(stdout);
    return 0;
}
