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
 *       enumerator         string, becomes SWD\<enumerator>\<suffix>
 *       instance-id-suffix unique ID for the device
 *       container-guid     {XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}
 *       hw-ids-csv         pipe-separated list of hardware IDs
 *       compat-ids-csv     pipe-separated list of compatible IDs (can be empty)
 *       description        human-readable description
 *
 * Exit codes:
 *   0   success (stdout: "OK <full-instance-id>")
 *   1   usage error
 *   2   SwDeviceCreate HRESULT (stderr: "ERR <hresult>")
 *   3   callback timeout
 *   4   callback returned non-zero HRESULT
 */
#define INITGUID
#include <windows.h>
#include <stdio.h>
#include <wchar.h>
#include <initguid.h>
#include <swdevice.h>

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
            L"Usage: hmswd.exe create <enumerator> <suffix> <{container-guid}> "
            L"<hw-ids-pipe-sep> <compat-ids-pipe-sep> <description>\n");
        return 1;
    }

    if (_wcsicmp(argv[1], L"create") != 0) goto usage;
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
    // balloons. DriverRequired forces SwDeviceCreate to block until the
    // matching INF is installed and bound — without it, the device ends up
    // in CM_PROB_NOT_CONFIGURED state, visible only in the registry and
    // not in the PnP enumerator, which defeats the whole point (xinput1_4
    // can't see a devnode that isn't enumerated).
    ci.CapabilityFlags      = SWDeviceCapabilitiesSilentInstall
                            | SWDeviceCapabilitiesNoDisplayInUI
                            | SWDeviceCapabilitiesDriverRequired;
    ci.pszDeviceDescription = desc;

    g_done = CreateEventW(NULL, TRUE, FALSE, NULL);
    HSWDEVICE hDev = NULL;

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

    /* Wait up to 30s for the callback (driver install may take time). */
    DWORD wait = WaitForSingleObject(g_done, 30000);
    free(hw_multi);
    free(compat_multi);

    if (wait != WAIT_OBJECT_0) {
        fwprintf(stderr, L"ERR callback timeout\n");
        if (hDev) SwDeviceClose(hDev);
        return 3;
    }

    if (FAILED(g_cb_hr)) {
        fwprintf(stderr, L"ERR callback hr=0x%08X\n", g_cb_hr);
        if (hDev) SwDeviceClose(hDev);
        return 4;
    }

    /* Lifetime = ParentPresent. Device persists past our exit. */
    SwDeviceSetLifetime(hDev, SWDeviceLifetimeParentPresent);

    /* Print instance ID on stdout so caller can record it. */
    wprintf(L"OK %s\n", g_inst_id);
    fflush(stdout);

    /* Close our handle; device remains because lifetime is ParentPresent. */
    SwDeviceClose(hDev);
    return 0;
}
