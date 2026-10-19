/*
 * HIDMaestro: UMDF2 Virtual HID Minidriver
 *
 * Architecture:
 *   HidClass.sys → MsHidUmdf.sys (pass-through) → HIDMaestro.dll (lower filter)
 *
 * Configuration via registry (HKLM\SOFTWARE\HIDMaestro):
 *   ReportDescriptor (REG_BINARY): raw HID descriptor bytes
 *   VendorId (REG_DWORD)
 *   ProductId (REG_DWORD)
 *   VersionNumber (REG_DWORD)
 *
 * Input reports flow via HidD_SetOutputReport() from user-mode → WRITE_REPORT →
 * stored in driver → completed on next READ_REPORT from HID class.
 *
 * Output reports (game → device) flow via WRITE_REPORT with output report ID.
 */

#include "driver.h"
#include <sddl.h>     /* ConvertStringSecurityDescriptorToSecurityDescriptorW, ConvertSidToStringSidW */
#include <aclapi.h>   /* GetSecurityInfo */
#include "version_gen.h"   /* HM_VER_*, generated into the build directory from Directory.Build.props */

/* Append the decimal representation of a ULONG to a wide-string buffer.
 * Self-contained: no C runtime dependency. The driver doesn't link against
 * MSVCRT, so swprintf/wsprintf aren't available. Buffer must be NUL-terminated. */
static VOID
AppendUlongDecimal(_Inout_ WCHAR *dest, _In_ ULONG value, _In_ SIZE_T maxChars)
{
    SIZE_T len = 0;
    while (len < maxChars && dest[len] != 0) len++;
    if (len + 1 >= maxChars) return;

    WCHAR tmp[16];
    int n = 0;
    if (value == 0) {
        tmp[n++] = L'0';
    } else {
        while (value > 0 && n < 15) {
            tmp[n++] = L'0' + (WCHAR)(value % 10);
            value /= 10;
        }
    }
    while (n > 0 && len + 1 < maxChars) {
        dest[len++] = tmp[--n];
    }
    dest[len] = 0;
}

/* Initialize per-instance paths from the controller index EvtDeviceAdd
 * chose before WdfDeviceCreate (ChooseControllerIndex). */
static VOID
InitInstancePaths(
    _In_ PDEVICE_CONTEXT ctx,
    _In_ ULONG           index)
{
    ctx->ControllerIndex = index;

    /* Build per-instance paths. Multi-digit indices fully supported: there's
     * no artificial cap on controller count. XInput tops out at 4 slots
     * (Microsoft's limit, not ours), but DInput / HIDAPI / WGI / browser
     * see all virtual controllers regardless of count. */
    {
        static const WCHAR prefix[] = L"SOFTWARE\\HIDMaestro\\Controller";
        SIZE_T cap = sizeof(ctx->ConfigRegPath) / sizeof(WCHAR);
        RtlCopyMemory(ctx->ConfigRegPath, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->ConfigRegPath, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroInput";
        SIZE_T cap = sizeof(ctx->SharedMappingName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->SharedMappingName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->SharedMappingName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroOutput";
        SIZE_T cap = sizeof(ctx->OutputMappingName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->OutputMappingName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->OutputMappingName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroPidState";
        SIZE_T cap = sizeof(ctx->PidStateMappingName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->PidStateMappingName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->PidStateMappingName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroInputEvent";
        SIZE_T cap = sizeof(ctx->InputEventName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->InputEventName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->InputEventName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroOutputEvent";
        SIZE_T cap = sizeof(ctx->OutputEventName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->OutputEventName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->OutputEventName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroStopEvent";
        SIZE_T cap = sizeof(ctx->StopEventName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->StopEventName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->StopEventName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroInputAck";
        SIZE_T cap = sizeof(ctx->InputAckMappingName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->InputAckMappingName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->InputAckMappingName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroInputAckEvent";
        SIZE_T cap = sizeof(ctx->InputAckEventName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->InputAckEventName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->InputAckEventName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroRemoveEvent";
        SIZE_T cap = sizeof(ctx->RemoveEventName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->RemoveEventName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->RemoveEventName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroRemovedEvent";
        SIZE_T cap = sizeof(ctx->RemovedEventName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->RemovedEventName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->RemovedEventName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"\\\\.\\pipe\\HIDMaestroOwner";
        SIZE_T cap = sizeof(ctx->OwnerPipeName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->OwnerPipeName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->OwnerPipeName, index, cap);
    }
    {
        static const WCHAR prefix[] = L"Global\\HIDMaestroVersion";
        SIZE_T cap = sizeof(ctx->VersionMappingName) / sizeof(WCHAR);
        RtlCopyMemory(ctx->VersionMappingName, prefix, sizeof(prefix));
        AppendUlongDecimal(ctx->VersionMappingName, index, cap);
    }

    /* Per-instance serial number. Format: "HM-CTL-<index>" zero-padded to
     * at least 4 digits so it sorts naturally. SDL3 / HIDAPI use this string
     * to distinguish identical controllers; without it, two virtual DualSense
     * with the same VID/PID/ProductString get bucketed as one device by
     * hid_enumerate. The exact format isn't part of any contract: consumers
     * are expected to treat the string as opaque. */
    {
        static const WCHAR prefix[] = L"HM-CTL-";
        SIZE_T cap = sizeof(ctx->SerialString) / sizeof(WCHAR);
        RtlCopyMemory(ctx->SerialString, prefix, sizeof(prefix));
        /* Zero-pad to 4 digits */
        if (index < 1000) {
            SIZE_T len = (sizeof(prefix) / sizeof(WCHAR)) - 1;
            if (index < 10)   { ctx->SerialString[len++] = L'0'; ctx->SerialString[len++] = L'0'; ctx->SerialString[len++] = L'0'; }
            else if (index < 100)  { ctx->SerialString[len++] = L'0'; ctx->SerialString[len++] = L'0'; }
            else if (index < 1000) { ctx->SerialString[len++] = L'0'; }
            ctx->SerialString[len] = 0;
        }
        AppendUlongDecimal(ctx->SerialString, index, cap);
        /* Compute byte length including the trailing NUL */
        SIZE_T slen = 0;
        while (slen < cap && ctx->SerialString[slen] != 0) slen++;
        ctx->SerialStringBytes = (ULONG)((slen + 1) * sizeof(WCHAR));
    }
}

/* ================================================================== */
/*  v2.0.0: the version contract (the design note's decision (c))      */
/* ================================================================== */

/* ControllerIndex from the device's hardware key, where every SDK version
 * writes it, through WdfFdoInitOpenRegistryKey before WdfDeviceCreate.
 * FALSE when there is none. Earlier drivers read index 0 then, the index
 * of another controller. */
static BOOLEAN
ReadHardwareIndex(_In_ PWDFDEVICE_INIT DeviceInit, _Out_ ULONG *index)
{
    BOOLEAN found = FALSE;
    WDFKEY hKey;
    *index = 0;
    if (NT_SUCCESS(WdfFdoInitOpenRegistryKey(DeviceInit, PLUGPLAY_REGKEY_DEVICE,
            KEY_READ, WDF_NO_OBJECT_ATTRIBUTES, &hKey))) {
        UNICODE_STRING valueName;
        RtlInitUnicodeString(&valueName, L"ControllerIndex");
        ULONG val = 0;
        if (NT_SUCCESS(WdfRegistryQueryULong(hKey, &valueName, &val))) {
            *index = val;
            found = TRUE;
        }
        WdfRegistryClose(hKey);
    }
    return found;
}

/* The version section of an index, mapped read-write, or NULL when there
 * is none. */
static PHIDMAESTRO_VERSION_INFO
MapVersionSection(_In_ ULONG index, _Out_ HANDLE *handle)
{
    WCHAR name[64];
    static const WCHAR prefix[] = L"Global\\HIDMaestroVersion";
    RtlCopyMemory(name, prefix, sizeof(prefix));
    AppendUlongDecimal(name, index, ARRAYSIZE(name));
    *handle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name);
    if (*handle == NULL) return NULL;
    PHIDMAESTRO_VERSION_INFO v = (PHIDMAESTRO_VERSION_INFO)MapViewOfFile(
        *handle, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(HIDMAESTRO_VERSION_INFO));
    if (v == NULL) { CloseHandle(*handle); *handle = NULL; }
    return v;
}

static VOID
UnmapVersionSection(_In_opt_ PHIDMAESTRO_VERSION_INFO view, _In_opt_ HANDLE handle)
{
    if (view) UnmapViewOfFile(view);
    if (handle) CloseHandle(handle);
}

/* The index this devnode serves and whether the contract holds. The
 * identity entry names the index, and the contract holds when that
 * index's section speaks protocol 4 and carries this devnode's identity
 * tag. Otherwise the devnode is a v1 program's: it keeps the index of its
 * hardware key, and fails its start when its hardware key holds none, or
 * when that index's section carries another token's tag, since it is then
 * not that controller (a v1 program's restart after its index write
 * recovers it). The XUSB companion applies the same rule
 * (CompanionDeviceAdd), as the design note's decision (c) gives it. */
static NTSTATUS
ChooseControllerIndex(_In_ const WCHAR *token, _In_ BOOLEAN haveIdentityIndex, _In_ ULONG identityIndex,
                      _In_ BOOLEAN haveHardwareIndex, _In_ ULONG hardwareIndex,
                      _Out_ ULONG *index, _Out_ BOOLEAN *contract)
{
    ULONGLONG ownTag = HmIdentityTag(token);
    HANDLE h;
    PHIDMAESTRO_VERSION_INFO v;
    *index = hardwareIndex;
    *contract = FALSE;
    if (haveIdentityIndex && token[0]) {
        v = MapVersionSection(identityIndex, &h);
        if (v != NULL) {
            LONG sdkMin = v->SdkProtocolMin, sdkMax = v->SdkProtocolMax;
            ULONGLONG tag = *(volatile ULONGLONG *)&v->IdentityTag;
            UnmapVersionSection(v, h);
            if (sdkMin <= HIDMAESTRO_CONTRACT_PROTOCOL && HIDMAESTRO_CONTRACT_PROTOCOL <= sdkMax && tag == ownTag) {
                *index = identityIndex;
                *contract = TRUE;
                return STATUS_SUCCESS;
            }
        }
    }
    if (!haveHardwareIndex) return STATUS_DEVICE_CONFIGURATION_ERROR;
    v = MapVersionSection(hardwareIndex, &h);
    if (v != NULL) {
        ULONGLONG tag = *(volatile ULONGLONG *)&v->IdentityTag;
        UnmapVersionSection(v, h);
        if (tag != 0 && tag != ownTag) return STATUS_DEVICE_CONFIGURATION_ERROR;
    }
    return STATUS_SUCCESS;
}

/* Writes which value this driver could not read into its error word,
 * before it fails its start, so the create names the value. */
static VOID
WriteConfigError(_In_ PDEVICE_CONTEXT ctx, _In_ LONG error)
{
    HANDLE h;
    PHIDMAESTRO_VERSION_INFO v = MapVersionSection(ctx->ControllerIndex, &h);
    if (v == NULL) return;
    InterlockedExchange(&v->DriverConfigError, error);
    UnmapVersionSection(v, h);
}

/* FeatureAnswers into the context. FALSE when it is missing, not its own
 * length, of another format, past a limit, or an entry's bytes leave it. */
static BOOLEAN
LoadFeatureAnswers(_In_ PDEVICE_CONTEXT ctx, _In_ HKEY hKey)
{
    UCHAR blob[HM_FEATURE_MAX_BLOB];
    DWORD size = sizeof(blob), type = 0;
    if (RegQueryValueExW(hKey, L"FeatureAnswers", NULL, &type, blob, &size) != ERROR_SUCCESS
        || type != REG_BINARY || size < HM_FEATURE_HEADER)
        return FALSE;
    ULONG length = *(ULONG UNALIGNED *)&blob[0];
    ULONG format = *(ULONG UNALIGNED *)&blob[4];
    ULONG count = *(USHORT UNALIGNED *)&blob[8];
    ULONG onCount = blob[10];
    if (length != size || format != HM_FEATURE_FORMAT || count > HM_FEATURE_MAX_ENTRIES
        || onCount > HM_FEATURE_MAX_ONREPORTS)
        return FALSE;
    ULONG arenaStart = HM_FEATURE_HEADER + count * HM_FEATURE_ENTRY_SIZE;
    if (arenaStart > size || size - arenaStart > HM_FEATURE_MAX_ARENA) return FALSE;
    ULONG arenaSize = size - arenaStart;
    for (ULONG i = 0; i < count; i++) {
        HM_FEATURE_ENTRY e;
        RtlCopyMemory(&e, &blob[HM_FEATURE_HEADER + i * HM_FEATURE_ENTRY_SIZE], sizeof(e));
        BOOLEAN message = (e.Flags & HM_FEATURE_FLAG_MESSAGE) != 0;
        BOOLEAN echo = (e.Flags & HM_FEATURE_FLAG_ECHO) != 0;
        if (e.Flags & ~HM_FEATURE_FLAGS_KNOWN) return FALSE;
        if (message) {
            if (e.OnSlot >= onCount || e.ValueLength == 0 || e.ValueLength > HM_FEATURE_MAX_MESSAGE
                || (ULONG)e.At + e.ValueLength > HM_FEATURE_MAX_DATA)
                return FALSE;
        } else if (e.ValueLength != 0 || (e.Flags & HM_FEATURE_FLAG_PARAM) || echo || e.PageCount != 1) {
            return FALSE;
        }
        if ((e.Flags & HM_FEATURE_FLAG_PARAM) && e.ParamAt >= HM_FEATURE_MAX_DATA) return FALSE;
        if (echo ? e.PageCount != 0 : (e.PageCount == 0 || e.PageCount > HM_FEATURE_MAX_PAGES)) return FALSE;
        if (e.Size == 0 || e.Size > HM_FEATURE_MAX_DATA || e.MinLength > e.Size || e.SizeCount > HM_FEATURE_MAX_SIZES)
            return FALSE;
        ULONG need = (ULONG)e.ValueLength + e.SizeCount + (ULONG)e.PageCount * e.Size;
        if ((ULONG)e.ArenaOffset + need > arenaSize) return FALSE;
        const UCHAR *sizes = &blob[arenaStart + e.ArenaOffset + e.ValueLength];
        for (ULONG k = 0; k < e.SizeCount; k++)
            if (sizes[k] == 0 || sizes[k] > e.Size || (k > 0 && sizes[k] > sizes[k - 1])) return FALSE;
        ctx->FeatureEntries[i] = e;
        ctx->FeaturePage[i] = 0;
    }
    for (ULONG i = 0; i < onCount; i++) ctx->FeatureOnReports[i] = blob[12 + i];
    RtlCopyMemory(ctx->FeatureArena, &blob[arenaStart], arenaSize);
    ctx->FeatureArenaSize = arenaSize;
    ctx->FeatureOnReportCount = onCount;
    ctx->FeatureCount = count;
    return TRUE;
}

/* CachedInputReportIds into the context. FALSE when it is missing or
 * longer than the driver keeps. */
static BOOLEAN
LoadCachedInputIds(_In_ PDEVICE_CONTEXT ctx, _In_ HKEY hKey)
{
    UCHAR ids[HM_CACHED_INPUT_IDS_MAX];
    DWORD size = sizeof(ids), type = 0;
    if (RegQueryValueExW(hKey, L"CachedInputReportIds", NULL, &type, ids, &size) != ERROR_SUCCESS
        || type != REG_BINARY || size > HM_CACHED_INPUT_IDS_MAX)
        return FALSE;
    RtlCopyMemory(ctx->CachedInputIds, ids, size);
    ctx->CachedInputIdCount = size;
    return TRUE;
}

/* LiveFeatureReport into the context: absent is none. FALSE when it is
 * not a REG_DWORD of 0 to 256, one plus a report ID. */
static BOOLEAN
LoadLiveFeature(_In_ PDEVICE_CONTEXT ctx, _In_ HKEY hKey)
{
    DWORD value = 0, size = sizeof(value), type = 0;
    LONG rc = RegQueryValueExW(hKey, L"LiveFeatureReport", NULL, &type, (LPBYTE)&value, &size);
    if (rc == ERROR_FILE_NOT_FOUND) { ctx->LiveFeatureReport = 0; return TRUE; }
    if (rc != ERROR_SUCCESS || type != REG_DWORD || size != sizeof(value) || value > 256)
        return FALSE;
    ctx->LiveFeatureReport = value;
    return TRUE;
}

/* The controller's v2.0.0 values, read only under the contract. Returns
 * 0 or the HIDMAESTRO_CONFIG_ERROR_* of the value it could not read. */
static LONG
LoadContractValues(_In_ PDEVICE_CONTEXT ctx)
{
    HKEY hKey;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, ctx->ConfigRegPath, 0, KEY_READ, &hKey) != ERROR_SUCCESS)
        return HIDMAESTRO_CONFIG_ERROR_FEATURE_ANSWERS;
    LONG error = !LoadFeatureAnswers(ctx, hKey) ? HIDMAESTRO_CONFIG_ERROR_FEATURE_ANSWERS
               : !LoadCachedInputIds(ctx, hKey) ? HIDMAESTRO_CONFIG_ERROR_CACHED_INPUT_IDS
               : !LoadLiveFeature(ctx, hKey) ? HIDMAESTRO_CONFIG_ERROR_LIVE_FEATURE
               : 0;
    RegCloseKey(hKey);
    return error;
}

/* Each input report's declared length from the descriptor, its ID byte
 * included when the descriptor declares IDs: the bits of its Input main
 * items, ReportSize times ReportCount, with Push and Pop honored. */
static VOID
BuildInputLengths(_In_ PDEVICE_CONTEXT ctx)
{
    ULONG bits[256];
    struct { ULONG size, count; UCHAR id; } stack[8];
    int depth = 0;
    ULONG reportSize = 0, reportCount = 0;
    UCHAR reportId = 0;
    BOOLEAN ids = FALSE;
    const UCHAR *d = ctx->ReportDescriptor;
    ULONG n = ctx->ReportDescriptorSize, i = 0;
    RtlZeroMemory(bits, sizeof(bits));
    while (i < n) {
        UCHAR prefix = d[i];
        if (prefix == 0xFE) {                       /* long item: size, tag, data */
            if (i + 1 >= n) break;
            i += 3 + (ULONG)d[i + 1];
            continue;
        }
        ULONG sz = prefix & 0x03;
        if (sz == 3) sz = 4;
        if (i + 1 + sz > n) break;
        ULONG val = 0;
        for (ULONG k = 0; k < sz; k++) val |= (ULONG)d[i + 1 + k] << (8 * k);
        UCHAR type = (prefix >> 2) & 0x03, tag = prefix >> 4;
        if (type == 1) {                            /* global */
            if (tag == 7) reportSize = val;
            else if (tag == 9) reportCount = val;
            else if (tag == 8) { reportId = (UCHAR)val; ids = TRUE; }
            else if (tag == 10 && depth < (int)ARRAYSIZE(stack)) {
                stack[depth].size = reportSize; stack[depth].count = reportCount; stack[depth].id = reportId; depth++;
            } else if (tag == 11 && depth > 0) {
                depth--; reportSize = stack[depth].size; reportCount = stack[depth].count; reportId = stack[depth].id;
            }
        } else if (type == 0 && tag == 8) {         /* Input */
            bits[reportId] += reportSize * reportCount;
        }
        i += 1 + sz;
    }
    ctx->UsesReportIds = ids;
    for (ULONG id = 0; id < 256; id++) {
        if (bits[id] == 0) { ctx->InputLengths[id] = 0; continue; }
        ULONG bytes = (bits[id] + 7) / 8 + (ids ? 1 : 0);
        ctx->InputLengths[id] = (USHORT)(bytes > 0xFFFF ? 0xFFFF : bytes);
    }
}

/* An extended frame goes out only at the declared length of the input
 * report its first byte names, or for a descriptor without IDs of its one
 * input report (the design note, "A v1 program on the v2.0.0 driver"). */
static BOOLEAN
ExtendedFrameDeclared(_In_ PDEVICE_CONTEXT ctx, _In_reads_(size) const UCHAR *frame, _In_ ULONG size)
{
    USHORT declared = ctx->UsesReportIds ? ctx->InputLengths[frame[0]] : ctx->InputLengths[0];
    return declared != 0 && declared == size;
}

/* The report IDs the PID block can answer or act on. The SDK writes the PID
 * report IDs into the PID state section, from PidReportIdExtractor over
 * this same descriptor: the report ID of a main item inside a Logical
 * Collection whose usage is Set Effect, Create New Effect, Block Free,
 * Device Control, Pool, Block Load or State (0x21, 0x88, 0x90, 0x96, 0x7F,
 * 0x89, 0x92), the innermost one that is, or else the canonical ID. Taken
 * here loosely enough to hold every SDK's reading: any page (b4d29d9's
 * extractor reads no page, this one's reads page 0x0F only), any enclosing
 * Logical Collection, and the report ID both with Push and Pop and without
 * (b4d29d9's ignores them). A descriptor this walk cannot follow to its end
 * makes every ID a candidate. */
static VOID
BuildPidCandidates(_In_ PDEVICE_CONTEXT ctx)
{
    struct { UCHAR type; UCHAR usage; } coll[32];
    UCHAR idStack[8];
    int depth = 0, idDepth = 0;
    UCHAR ridScoped = 0, ridFlat = 0;
    BOOLEAN haveUsage = FALSE, complete = TRUE;
    UCHAR firstUsage = 0;
    const UCHAR *d = ctx->ReportDescriptor;
    ULONG n = ctx->ReportDescriptorSize, i = 0;
    static const UCHAR canonical[] = {
        HIDMAESTRO_PID_CREATE_NEW_EFFECT_REPORT_ID, HIDMAESTRO_PID_BLOCK_LOAD_REPORT_ID,
        HIDMAESTRO_PID_POOL_REPORT_ID, HIDMAESTRO_PID_STATE_REPORT_ID,
        HIDMAESTRO_PID_BLOCK_FREE_REPORT_ID, HIDMAESTRO_PID_DEVICE_CONTROL_REPORT_ID };
    RtlZeroMemory(ctx->PidCandidate, sizeof(ctx->PidCandidate));
    for (ULONG k = 0; k < ARRAYSIZE(canonical); k++) ctx->PidCandidate[canonical[k]] = TRUE;
    while (i < n) {
        UCHAR prefix = d[i];
        if (prefix == 0xFE) {                       /* long item: size, tag, data */
            if (i + 2 >= n) { complete = FALSE; break; }
            i += 3 + (ULONG)d[i + 1];
            continue;
        }
        ULONG sz = prefix & 0x03;
        if (sz == 3) sz = 4;
        if (i + 1 + sz > n) { complete = FALSE; break; }
        ULONG val = 0;
        for (ULONG k = 0; k < sz; k++) val |= (ULONG)d[i + 1 + k] << (8 * k);
        UCHAR type = (prefix >> 2) & 0x03, tag = prefix >> 4;
        if (type == 0) {                            /* main */
            if (tag == 10) {                        /* Collection */
                if (depth == ARRAYSIZE(coll)) { complete = FALSE; break; }
                coll[depth].type = (UCHAR)val;
                coll[depth].usage = haveUsage ? firstUsage : 0;
                depth++;
            } else if (tag == 12) {                 /* End Collection */
                if (depth > 0) depth--;
            } else if (tag == 8 || tag == 9 || tag == 11) {   /* Input, Output, Feature */
                for (int k = depth - 1; k >= 0; k--) {
                    UCHAR u = coll[k].usage;
                    if (coll[k].type != 0x02) continue;
                    if (u == 0x21 || u == 0x88 || u == 0x90 || u == 0x96 || u == 0x7F || u == 0x89 || u == 0x92) {
                        if (ridScoped) ctx->PidCandidate[ridScoped] = TRUE;
                        if (ridFlat) ctx->PidCandidate[ridFlat] = TRUE;
                        break;
                    }
                }
            }
            haveUsage = FALSE;                      /* locals end at every main item */
        } else if (type == 1) {                     /* global */
            if (tag == 8) { ridScoped = (UCHAR)val; ridFlat = (UCHAR)val; }
            else if (tag == 10) {                   /* Push */
                if (idDepth == ARRAYSIZE(idStack)) { complete = FALSE; break; }
                idStack[idDepth++] = ridScoped;
            } else if (tag == 11 && idDepth > 0) {  /* Pop */
                ridScoped = idStack[--idDepth];
            }
        } else if (type == 2 && tag == 0 && !haveUsage) {   /* the first Usage */
            firstUsage = (UCHAR)val;
            haveUsage = TRUE;
        }
        i += 1 + sz;
    }
    if (!complete)
        for (ULONG k = 0; k < 256; k++) ctx->PidCandidate[k] = TRUE;
}

static BOOLEAN
InputIdCached(_In_ PDEVICE_CONTEXT ctx, _In_ UCHAR id)
{
    for (ULONG i = 0; i < ctx->CachedInputIdCount; i++)
        if (ctx->CachedInputIds[i] == id) return TRUE;
    return FALSE;
}

/* ------------------------------------------------------------------ */
/*  FeatureAnswers at run time                                         */
/* ------------------------------------------------------------------ */

/* A SET_FEATURE on a report a message entry follows: its buffer is kept
 * whole, up to HM_FEATURE_MAX_DATA bytes, and the pages of that report's
 * entries start again. For a descriptor that declares report IDs the
 * buffer starts with the ID. For one that declares none HID class hands
 * the minidriver the report without an ID byte, in both directions: the
 * caller's leading 0 is left out (Wine 2df1ee2 hidclass.sys device.c:485
 * and 505, offset = report->ReportID ? 0 : 1, for a GET and a SET alike,
 * and vhidmini2 197ba21 vhidmini.c:765-767 for a GET),
 * as ServeFeature answers from the buffer's first byte and as the USB/IP
 * device's data stage carries it (FeatureStubTable.OnSetFeature). */
static VOID
KeepFeatureSet(_In_ PDEVICE_CONTEXT ctx, _In_reads_(size) const UCHAR *buf, _In_ ULONG size)
{
    if (ctx->FeatureOnReportCount == 0 || size == 0) return;
    UCHAR rid = ctx->UsesReportIds ? buf[0] : 0;
    const UCHAR *data = buf;
    ULONG len = size;
    ULONG slot;
    for (slot = 0; slot < ctx->FeatureOnReportCount; slot++)
        if (ctx->FeatureOnReports[slot] == rid) break;
    if (slot == ctx->FeatureOnReportCount) return;
    if (len > HM_FEATURE_MAX_DATA) len = HM_FEATURE_MAX_DATA;
    WdfWaitLockAcquire(ctx->FeatureLock, NULL);
    RtlCopyMemory(ctx->FeatureKept[slot], data, len);
    ctx->FeatureKeptLength[slot] = len;
    for (ULONG i = 0; i < ctx->FeatureCount; i++)
        if ((ctx->FeatureEntries[i].Flags & HM_FEATURE_FLAG_MESSAGE) && ctx->FeatureEntries[i].OnSlot == slot)
            ctx->FeaturePage[i] = 0;
    WdfWaitLockRelease(ctx->FeatureLock);
}

/* The entry a GET on a report takes: a message entry whose message and
 * parameter match the last SET on its report, then one whose message
 * matches and that takes no parameter, then a plain entry. -1 when none
 * does. Under FeatureLock. FeatureStubTable.Find is the same walk. */
static LONG
FindFeatureEntry(_In_ PDEVICE_CONTEXT ctx, _In_ UCHAR id)
{
    LONG noParam = -1, plain = -1;
    for (ULONG i = 0; i < ctx->FeatureCount; i++) {
        const HM_FEATURE_ENTRY *e = &ctx->FeatureEntries[i];
        if (e->Id != id) continue;
        if (!(e->Flags & HM_FEATURE_FLAG_MESSAGE)) { if (plain < 0) plain = (LONG)i; continue; }
        ULONG len = ctx->FeatureKeptLength[e->OnSlot];
        const UCHAR *kept = ctx->FeatureKept[e->OnSlot];
        if (len < (ULONG)e->At + e->ValueLength
            || RtlCompareMemory(kept + e->At, ctx->FeatureArena + e->ArenaOffset, e->ValueLength) != e->ValueLength)
            continue;
        if (!(e->Flags & HM_FEATURE_FLAG_PARAM)) { if (noParam < 0) noParam = (LONG)i; continue; }
        if (e->ParamAt < len && kept[e->ParamAt] == e->ParamValue) return (LONG)i;
    }
    return noParam >= 0 ? noParam : plain;
}

/* The answer to a GET_FEATURE from the table. FALSE when no entry
 * answers, so the request goes on to the PID block. Otherwise *status is
 * STATUS_SUCCESS with *written bytes in out, or STATUS_BUFFER_TOO_SMALL
 * for a buffer shorter than the entry serves. */
static BOOLEAN
ServeFeature(_In_ PDEVICE_CONTEXT ctx, _In_ UCHAR requestId, _Out_writes_(outSize) UCHAR *out, _In_ ULONG outSize,
             _Out_ ULONG *written, _Out_ BOOLEAN *notify, _Out_ NTSTATUS *status)
{
    UCHAR full[HM_FEATURE_MAX_DATA];
    UCHAR id = ctx->UsesReportIds ? requestId : 0;
    *written = 0;
    *notify = FALSE;
    *status = STATUS_SUCCESS;
    WdfWaitLockAcquire(ctx->FeatureLock, NULL);
    LONG at = FindFeatureEntry(ctx, id);
    if (at < 0) { WdfWaitLockRelease(ctx->FeatureLock); return FALSE; }
    const HM_FEATURE_ENTRY *e = &ctx->FeatureEntries[at];
    const UCHAR *arena = ctx->FeatureArena + e->ArenaOffset;
    const UCHAR *sizes = arena + e->ValueLength;
    ULONG n = 0;
    if (e->SizeCount > 0) {
        for (ULONG k = 0; k < e->SizeCount; k++)
            if (sizes[k] <= outSize) { n = sizes[k]; break; }
    } else if (outSize >= e->MinLength) {
        n = e->Size < outSize ? e->Size : outSize;
    }
    if (n == 0) {
        WdfWaitLockRelease(ctx->FeatureLock);
        *status = STATUS_BUFFER_TOO_SMALL;
        return TRUE;
    }
    if (e->Flags & HM_FEATURE_FLAG_ECHO) {
        ULONG k = ctx->FeatureKeptLength[e->OnSlot];
        if (k > e->Size) k = e->Size;
        RtlZeroMemory(full, e->Size);
        RtlCopyMemory(full, ctx->FeatureKept[e->OnSlot], k);
    } else {
        ULONG page = ctx->FeaturePage[at];
        if (page > (ULONG)e->PageCount - 1) page = (ULONG)e->PageCount - 1;
        RtlCopyMemory(full, sizes + e->SizeCount + page * e->Size, e->Size);
        if ((e->Flags & HM_FEATURE_FLAG_MESSAGE) && e->PageCount > 1 && ctx->FeaturePage[at] < e->PageCount - 1)
            ctx->FeaturePage[at]++;
    }
    if (e->Flags & HM_FEATURE_FLAG_REPLY_HEAD) full[0] = requestId;
    *notify = (e->Flags & HM_FEATURE_FLAG_NOTIFY) != 0;
    WdfWaitLockRelease(ctx->FeatureLock);
    RtlCopyMemory(out, full, n);
    *written = n;
    return TRUE;
}

/* ================================================================== */
/*  Helper: copy bytes to request output buffer                        */
/* ================================================================== */

static NTSTATUS
RequestCopyFromBuffer(
    _In_ WDFREQUEST  Request,
    _In_ PVOID       SourceBuffer,
    _In_ size_t      NumBytes)
{
    NTSTATUS    status;
    WDFMEMORY   memory;
    size_t      outputSize;

    status = WdfRequestRetrieveOutputMemory(Request, &memory);
    if (!NT_SUCCESS(status)) return status;

    WdfMemoryGetBuffer(memory, &outputSize);
    if (outputSize < NumBytes) return STATUS_INVALID_BUFFER_SIZE;

    status = WdfMemoryCopyFromBuffer(memory, 0, SourceBuffer, NumBytes);
    if (!NT_SUCCESS(status)) return status;

    WdfRequestSetInformation(Request, NumBytes);
    return STATUS_SUCCESS;
}

/* ================================================================== */
/*  Registry: read descriptor + VID/PID at device init                 */
/* ================================================================== */

/* FALSE when Controller<index> does not open or holds no report
 * descriptor this driver can present. There is no default device to fall
 * back on. */
static BOOLEAN
ReadConfigFromRegistry(
    _In_ PDEVICE_CONTEXT ctx)
{
    /*
     * UMDF2 runs in user-mode (WUDFHost.exe), so WdfRegistryOpenKey with
     * kernel-style paths (\Registry\Machine\...) does NOT work. We use
     * the Win32 RegOpenKeyExW API directly: UMDF2 has full Win32 access.
     */
    HKEY    hKey = NULL;
    LONG    result;
    DWORD   dwordVal, dwordSize;
    BYTE    binBuf[5 + HIDMAESTRO_MAX_DESCRIPTOR_SIZE];
    DWORD   binSize;
    DWORD   regType;
    BOOLEAN haveDescriptor = FALSE;

    /* The controller's own key. Drivers before v2.0.0 fell back to the
     * legacy global key SOFTWARE\HIDMaestro, where v1.12.0's SDK writes no
     * controller's values. */
    result = RegOpenKeyExW(HKEY_LOCAL_MACHINE, ctx->ConfigRegPath,
                           0, KEY_READ, &hKey);
    if (result != ERROR_SUCCESS)
        return FALSE;

    /* Read ReportDescriptor (REG_BINARY) */
    binSize = sizeof(binBuf);
    result = RegQueryValueExW(hKey, L"ReportDescriptor", NULL,
                              &regType, binBuf, &binSize);
    if (result == ERROR_SUCCESS && regType == REG_BINARY &&
        binSize > 0 && binSize <= HIDMAESTRO_MAX_DESCRIPTOR_SIZE) {
        /*
         * Use the profile descriptor as-is. The test client is responsible
         * for ensuring the descriptor includes whatever data channel items
         * are needed (e.g., Feature Report ID 2).
         *
         * We do NOT modify the descriptor here: injecting Report IDs into
         * descriptors that use the default (no-ID) report can violate HID
         * validation rules. The client pre-processes the descriptor.
         */
        RtlCopyMemory(ctx->ReportDescriptor, binBuf, binSize);
        ctx->ReportDescriptorSize = (ULONG)binSize;
        ctx->HidDescriptor.DescriptorList[0].wReportLength =
            (USHORT)ctx->ReportDescriptorSize;
        haveDescriptor = TRUE;
    }

    /* Read VendorId (REG_DWORD) */
    dwordSize = sizeof(dwordVal);
    result = RegQueryValueExW(hKey, L"VendorId", NULL,
                              &regType, (LPBYTE)&dwordVal, &dwordSize);
    if (result == ERROR_SUCCESS && regType == REG_DWORD) {
        ctx->HidDeviceAttributes.VendorID = (USHORT)dwordVal;
    }

    /* Read ProductId (REG_DWORD) */
    dwordSize = sizeof(dwordVal);
    result = RegQueryValueExW(hKey, L"ProductId", NULL,
                              &regType, (LPBYTE)&dwordVal, &dwordSize);
    if (result == ERROR_SUCCESS && regType == REG_DWORD) {
        ctx->HidDeviceAttributes.ProductID = (USHORT)dwordVal;
    }

    /* Read VersionNumber (REG_DWORD) */
    dwordSize = sizeof(dwordVal);
    result = RegQueryValueExW(hKey, L"VersionNumber", NULL,
                              &regType, (LPBYTE)&dwordVal, &dwordSize);
    if (result == ERROR_SUCCESS && regType == REG_DWORD) {
        ctx->HidDeviceAttributes.VersionNumber = (USHORT)dwordVal;
    }

    /* Read ProductString (REG_SZ) */
    {
        WCHAR strBuf[128];
        DWORD strSize = sizeof(strBuf);
        result = RegQueryValueExW(hKey, L"ProductString", NULL,
                                  &regType, (LPBYTE)strBuf, &strSize);
        if (result == ERROR_SUCCESS && regType == REG_SZ && strSize > 0) {
            RtlCopyMemory(ctx->ProductString, strBuf, strSize);
            ctx->ProductStringBytes = strSize;
        }
    }

    /* Read InputReportByteLength (REG_DWORD): for capping SET_FEATURE→input.
     * Bounds-check to HIDMAESTRO_MAX_REPORT_SIZE so a corrupt registry can't
     * cause buffer overflows or wildly wrong report sizing. */
    dwordSize = sizeof(dwordVal);
    result = RegQueryValueExW(hKey, L"InputReportByteLength", NULL,
                              &regType, (LPBYTE)&dwordVal, &dwordSize);
    if (result == ERROR_SUCCESS && regType == REG_DWORD &&
        dwordVal > 0 && dwordVal <= HIDMAESTRO_MAX_REPORT_SIZE) {
        ctx->InputReportByteLength = dwordVal;
    }

    /* Read InputReportId (REG_BINARY): the input report the SDK fills in
     * byte 0, its length in bytes 1-4 (little-endian), then the report
     * descriptor the SDK read both from. An SDK from before the value
     * writes none and leaves one an earlier program wrote under this index
     * in place, with its own InputReportByteLength. So the ID counts only
     * when the length is the InputReportByteLength just read and the
     * descriptor is the one in use: never with another program's length.
     * A left value whose length happens to equal that program's passes.
     * In the catalog only switch2-pro-controller pairs that way (0x09 at
     * 64 where v1.12.0 sent 0x05 at 64), and its extendedReport is always
     * armed, so every frame goes out whole on the extended lane and this
     * ID never reaches the wire. Without the value DeviceAdd reads the ID
     * from the descriptor. */
    binSize = sizeof(binBuf);
    result = RegQueryValueExW(hKey, L"InputReportId", NULL,
                              &regType, binBuf, &binSize);
    if (result == ERROR_SUCCESS && regType == REG_BINARY &&
        binSize == 5 + ctx->ReportDescriptorSize) {
        ULONG idLength = (ULONG)binBuf[1] | ((ULONG)binBuf[2] << 8)
                       | ((ULONG)binBuf[3] << 16) | ((ULONG)binBuf[4] << 24);
        if (idLength == ctx->InputReportByteLength &&
            RtlCompareMemory(binBuf + 5, ctx->ReportDescriptor,
                             ctx->ReportDescriptorSize) == ctx->ReportDescriptorSize) {
            ctx->FirstInputReportId = binBuf[0];
            ctx->InputReportIdConfigured = TRUE;
        }
    }

    RegCloseKey(hKey);
    return haveDescriptor;
}

/* ================================================================== */
/*  Shared Memory Poll Timer                                           */
/* ================================================================== */

static VOID TryOpenInputAck(_In_ PDEVICE_CONTEXT ctx);
static VOID AnnounceVersion(_In_ PDEVICE_CONTEXT ctx);
static VOID CloseInputAckLocked(_In_ PDEVICE_CONTEXT ctx);
static VOID AckInputDeliveredLocked(_In_ PDEVICE_CONTEXT ctx, _In_ ULONG seqNo);
static VOID TryOpenOwnerWatch(_In_ PDEVICE_CONTEXT ctx);
static VOID CloseOwnerWatch(_In_ PDEVICE_CONTEXT ctx);
static VOID OwnerGone(_In_ PDEVICE_CONTEXT ctx);
static VOID DeliverReleaseFrames(_In_ PDEVICE_CONTEXT ctx);
static HANDLE CreateHostEvent(_In_ BOOL manualReset, _In_ BOOL takeOverNullDacl, _In_ LPCWSTR name);
static VOID TryCreateDoorbell(_In_ PDEVICE_CONTEXT ctx);

/* Create the output doorbell if there is none yet, and publish it for
 * PublishOutput. EvtDeviceAdd may have found the name unusable, held by an
 * object no driver host made under a DACL, and that object can be gone now.
 * The field only ever goes from NULL to a handle while the device runs, and
 * InterlockedExchangePointer is a full barrier, so a reader on the I/O
 * queue's threads sees NULL (skip the signal) or the handle. Worker thread
 * only. Cheap and skipped once the doorbell exists. */
static VOID
TryCreateDoorbell(_In_ PDEVICE_CONTEXT ctx)
{
    if (ctx->OutputSignalEvent != NULL) return;
    HANDLE ev = CreateHostEvent(FALSE /* auto reset */, TRUE /* take over a NULL-DACL doorbell */, ctx->OutputEventName);
    if (ev != NULL)
        InterlockedExchangePointer((PVOID volatile *)&ctx->OutputSignalEvent, ev);
}

/* Try to open and map the named section. Returns TRUE on success.
 * On failure, leaves SharedMemHandle/SharedMemPtr unchanged (NULL). */
static BOOLEAN
TryOpenSharedMapping(_In_ PDEVICE_CONTEXT ctx)
{
    HANDLE h = OpenFileMappingW(FILE_MAP_READ, FALSE, ctx->SharedMappingName);
    if (h == NULL) return FALSE;

    PVOID view = MapViewOfFile(h, FILE_MAP_READ, 0, 0, sizeof(HIDMAESTRO_SHARED_INPUT));
    if (view == NULL) { CloseHandle(h); return FALSE; }

    ctx->SharedMemHandle = h;
    ctx->SharedMemPtr = view;
    TryOpenInputAck(ctx);
    TryOpenOwnerWatch(ctx);
    AnnounceVersion(ctx);
    TryCreateDoorbell(ctx);
    return TRUE;
}

/* v2.0.0: write this driver's half of the version handshake, then let go
 * of the section. The SDK keeps it alive. Best effort, like the InputAck
 * open: an SDK that predates it creates no section. v2.0.0 writes it at
 * add as well, and DriverProtocolMin with it. Nothing is cleared at
 * cleanup, and each call writes the same values. */
static VOID
AnnounceVersion(_In_ PDEVICE_CONTEXT ctx)
{
    HANDLE h = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, ctx->VersionMappingName);
    if (h == NULL) return;
    PHIDMAESTRO_VERSION_INFO info = (PHIDMAESTRO_VERSION_INFO)MapViewOfFile(
        h, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(HIDMAESTRO_VERSION_INFO));
    if (info != NULL)
    {
        info->DriverVersion[0] = HM_VER_MAJOR;
        info->DriverVersion[1] = HM_VER_MINOR;
        info->DriverVersion[2] = HM_VER_PATCH;
        info->DriverVersion[3] = 0;
        InterlockedExchange(&info->DriverCapabilities,
            (LONG)(HIDMAESTRO_INPUT_ACK_CAP_DELIVERED | HIDMAESTRO_INPUT_ACK_CAP_RELEASE));
        InterlockedExchange(&info->DriverProtocolMin, HIDMAESTRO_DRIVER_PROTOCOL_MIN);
        MemoryBarrier();
        InterlockedExchange(&info->DriverProtocol, HIDMAESTRO_DRIVER_PROTOCOL);
        UnmapViewOfFile(info);
    }
    CloseHandle(h);
}

/* Issue #67: open the delivery acknowledgement the SDK creates beside the
 * input section, and announce the capability in it. Best effort: an SDK
 * that predates it creates neither object. Takes InputLock because the
 * read path writes through the pointer under it. Worker thread only, like
 * the owner watch it reads. */
static VOID
TryOpenInputAck(_In_ PDEVICE_CONTEXT ctx)
{
    HANDLE h = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, ctx->InputAckMappingName);
    if (h == NULL) return;
    PVOID view = MapViewOfFile(h, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(HIDMAESTRO_INPUT_ACK));
    if (view == NULL) { CloseHandle(h); return; }
    HANDLE ev = OpenEventW(EVENT_MODIFY_STATE, FALSE, ctx->InputAckEventName);

    WdfWaitLockAcquire(ctx->InputLock, NULL);
    CloseInputAckLocked(ctx);
    ctx->InputAckMemHandle = h;
    ctx->InputAckMemPtr = (volatile LONG *)view;
    ctx->InputAckEvent = ev;
    /* The release bit says the owner watch is connected. The watch stays
     * open across a reset of the worker's handles, which maps this section
     * again, so the bit is announced again with it. The ended marker is
     * kept: only a program that zeroes the section clears it. */
    {
        volatile LONG *caps = &((PHIDMAESTRO_INPUT_ACK)view)->Capabilities;
        LONG old, want;
        do {
            old = *caps;
            want = (LONG)(HIDMAESTRO_INPUT_ACK_CAP_DELIVERED
                          | (ctx->OwnerPipe != NULL ? HIDMAESTRO_INPUT_ACK_CAP_RELEASE : 0)
                          | ((ULONG)old & HIDMAESTRO_INPUT_ACK_WATCH_ENDED));
        } while (InterlockedCompareExchange(caps, want, old) != old);
    }
    WdfWaitLockRelease(ctx->InputLock);
}

/* Caller holds InputLock (or is the only thread left, at release). */
static VOID
CloseInputAckLocked(_In_ PDEVICE_CONTEXT ctx)
{
    if (ctx->InputAckMemPtr) { UnmapViewOfFile((PVOID)ctx->InputAckMemPtr); ctx->InputAckMemPtr = NULL; }
    if (ctx->InputAckMemHandle) { CloseHandle(ctx->InputAckMemHandle); ctx->InputAckMemHandle = NULL; }
    if (ctx->InputAckEvent) { CloseHandle(ctx->InputAckEvent); ctx->InputAckEvent = NULL; }
}

/* HID class holds the frame with this SeqNo. Caller holds InputLock. */
static VOID
AckInputDeliveredLocked(_In_ PDEVICE_CONTEXT ctx, _In_ ULONG seqNo)
{
    if (ctx->InputAckMemPtr == NULL) return;
    InterlockedExchange(&((PHIDMAESTRO_INPUT_ACK)ctx->InputAckMemPtr)->DeliveredSeqNo, (LONG)seqNo);
    if (ctx->InputAckEvent) SetEvent(ctx->InputAckEvent);
}

/* A READ_REPORT just completed with the frame with this SeqNo. Its
 * acknowledgement waits for HID class's next READ_REPORT (see AckPending in
 * driver.h). Caller holds InputLock. */
static VOID
NoteFrameCompletedLocked(_In_ PDEVICE_CONTEXT ctx, _In_ ULONG seqNo)
{
    ctx->AckPending = TRUE;
    ctx->AckPendingSeqNo = seqNo;
}

/* v2.0.0: the owner watch's state, for the SDK's census of who owns each
 * device (sdk OwnerCensus.cs). Any program can create the v1.12.0 owner
 * pipe's name, \\.\pipe\HIDMaestroOwner<N>, so the name alone does not show
 * that a program runs on index N. The driver is the pipe's client and asks
 * Windows who serves it. Three values in the device's own key, in the
 * subkey a UMDF driver may write (Device Parameters\HIDMaestro):
 *   OwnerConnected  1 while the watch is connected, 0 once it has ended
 *   OwnerProcessId  the server's process id (GetNamedPipeServerProcessId)
 *   OwnerPipeOwner  the pipe's owner SID as text: Administrators or SYSTEM
 *                   when an elevated token made it, which no other token
 *                   can name as an owner
 * Written only when the watch connects or ends, never per frame. A device
 * context deletes them when it starts, so they describe its own watch and
 * nothing a previous device on this instance left. Until the watch
 * connects there are none, which the census reads as a driver that cannot
 * report. Worker thread only, except the delete in EvtDeviceAdd before
 * the worker starts. */
static const PCWSTR g_OwnerWatchValues[] = { L"OwnerConnected", L"OwnerProcessId", L"OwnerPipeOwner" };

static BOOLEAN
OpenOwnerWatchKey(_In_ PDEVICE_CONTEXT ctx, _Out_ WDFKEY *key)
{
    /* The key object is the device's child: none is made once the
     * device's cleanup has begun. */
    *key = NULL;
    if (ctx->TearingDown) return FALSE;
    return NT_SUCCESS(WdfDeviceOpenRegistryKey(ctx->Device,
        PLUGPLAY_REGKEY_DEVICE | WDF_REGKEY_DEVICE_SUBKEY,
        KEY_READ | KEY_SET_VALUE, WDF_NO_OBJECT_ATTRIBUTES, key));
}

static VOID
RemoveOwnerWatchValues(_In_ WDFKEY key)
{
    for (ULONG i = 0; i < ARRAYSIZE(g_OwnerWatchValues); i++) {
        UNICODE_STRING name;
        RtlInitUnicodeString(&name, g_OwnerWatchValues[i]);
        WdfRegistryRemoveValue(key, &name);
    }
}

static VOID
ForgetOwnerWatch(_In_ PDEVICE_CONTEXT ctx)
{
    WDFKEY key;
    if (!OpenOwnerWatchKey(ctx, &key)) return;
    RemoveOwnerWatchValues(key);
    WdfRegistryClose(key);
}

/* The watch just connected on this client handle. A server the driver
 * cannot name, or values it cannot write, leave none. */
static VOID
RecordOwnerConnected(_In_ PDEVICE_CONTEXT ctx, _In_ HANDLE pipe)
{
    WDFKEY key;
    ULONG pid = 0;
    PSID owner = NULL;
    PSECURITY_DESCRIPTOR sd = NULL;
    LPWSTR ownerText = NULL;
    BOOLEAN written = FALSE;

    if (!OpenOwnerWatchKey(ctx, &key)) return;
    if (GetNamedPipeServerProcessId(pipe, &pid) && pid != 0
        && GetSecurityInfo(pipe, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION,
                           &owner, NULL, NULL, NULL, &sd) == ERROR_SUCCESS
        && owner != NULL && ConvertSidToStringSidW(owner, &ownerText)) {
        UNICODE_STRING name, text;
        RtlInitUnicodeString(&name, L"OwnerProcessId");
        written = NT_SUCCESS(WdfRegistryAssignULong(key, &name, pid));
        if (written) {
            RtlInitUnicodeString(&name, L"OwnerPipeOwner");
            RtlInitUnicodeString(&text, ownerText);
            written = NT_SUCCESS(WdfRegistryAssignUnicodeString(key, &name, &text));
        }
        if (written) {
            RtlInitUnicodeString(&name, L"OwnerConnected");
            written = NT_SUCCESS(WdfRegistryAssignULong(key, &name, 1));
        }
    }
    if (!written) RemoveOwnerWatchValues(key);
    if (ownerText != NULL) LocalFree(ownerText);
    if (sd != NULL) LocalFree(sd);
    WdfRegistryClose(key);
}

/* The watch ended: the server's end of the pipe closed. */
static VOID
RecordOwnerGone(_In_ PDEVICE_CONTEXT ctx)
{
    WDFKEY key;
    UNICODE_STRING name;
    if (!OpenOwnerWatchKey(ctx, &key)) return;
    RtlInitUnicodeString(&name, L"OwnerConnected");
    if (!NT_SUCCESS(WdfRegistryAssignULong(key, &name, 0)))
        RemoveOwnerWatchValues(key);
    WdfRegistryClose(key);
}

/* Issue #67: connect to the SDK's owner pipe and leave one read pending.
 * The SDK never writes to it, so the read completes only when the SDK's end
 * closes, which Windows does when the consumer's process ends however it
 * ends. Worker thread only. The pipe stays open across handle recycles: it
 * is a single connection the SDK does not reopen. */
static VOID
TryOpenOwnerWatch(_In_ PDEVICE_CONTEXT ctx)
{
    if (ctx->OwnerPipe != NULL) return;
    /* Connect only beside an acknowledgement section whose ended marker
     * (HIDMAESTRO_INPUT_ACK_WATCH_ENDED) is clear. Every SDK with an owner
     * pipe creates and zeroes that section when it sets up the index, so a
     * program that starts on the index clears the marker. Once a watch has
     * ended, a server answering the name with no new program behind it is
     * another process, not this device's owner. The marker lives in the section,
     * not in the device context, so it holds across a re-add of the device
     * while anything keeps the section open, and a context that outlives
     * its program (a companion another program adopts when its restart is
     * refused) still watches the next program. */
    {
        BOOLEAN allowed;
        WdfWaitLockAcquire(ctx->InputLock, NULL);
        allowed = ctx->InputAckMemPtr != NULL
            && (((ULONG)((PHIDMAESTRO_INPUT_ACK)ctx->InputAckMemPtr)->Capabilities
                 & HIDMAESTRO_INPUT_ACK_WATCH_ENDED) == 0);
        WdfWaitLockRelease(ctx->InputLock);
        if (!allowed) return;
    }
    HANDLE p = CreateFileW(ctx->OwnerPipeName, GENERIC_READ, 0, NULL,
                           OPEN_EXISTING, FILE_FLAG_OVERLAPPED, NULL);
    if (p == INVALID_HANDLE_VALUE) return;
    HANDLE ev = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (ev == NULL) { CloseHandle(p); return; }

    RtlZeroMemory(&ctx->OwnerOverlapped, sizeof(ctx->OwnerOverlapped));
    ctx->OwnerOverlapped.hEvent = ev;
    ctx->OwnerPipe = p;
    ctx->OwnerReadEvent = ev;
    if (!ReadFile(p, &ctx->OwnerReadByte, 1, NULL, &ctx->OwnerOverlapped)
        && GetLastError() != ERROR_IO_PENDING) {
        /* The SDK's end closed between the open and the read. */
        SetEvent(ev);
    }
    RecordOwnerConnected(ctx, p);

    WdfWaitLockAcquire(ctx->InputLock, NULL);
    if (ctx->InputAckMemPtr)
        InterlockedOr(&((PHIDMAESTRO_INPUT_ACK)ctx->InputAckMemPtr)->Capabilities,
                      (LONG)HIDMAESTRO_INPUT_ACK_CAP_RELEASE);
    WdfWaitLockRelease(ctx->InputLock);
}

/* Cancel the pending read and wait for it to finish before the OVERLAPPED
 * and its event go away. Worker thread, or the release after the worker has
 * been joined. */
static VOID
CloseOwnerWatch(_In_ PDEVICE_CONTEXT ctx)
{
    if (ctx->OwnerPipe != NULL) {
        DWORD n = 0;
        CancelIoEx(ctx->OwnerPipe, &ctx->OwnerOverlapped);
        GetOverlappedResult(ctx->OwnerPipe, &ctx->OwnerOverlapped, &n, TRUE);
        CloseHandle(ctx->OwnerPipe);
        ctx->OwnerPipe = NULL;
    }
    if (ctx->OwnerReadEvent != NULL) {
        CloseHandle(ctx->OwnerReadEvent);
        ctx->OwnerReadEvent = NULL;
    }
}

/* The consumer's end of the owner pipe closed. Queue the release frames the
 * SDK left in the acknowledgement section and send them, so whatever key or
 * button the consumer held when it went is let go instead of repeating
 * until the device is removed. A gamepad's SDK leaves no frames, and
 * nothing is sent. Worker thread only. */
static VOID
OwnerGone(_In_ PDEVICE_CONTEXT ctx)
{
    CloseOwnerWatch(ctx);

    /* The next program on this index zeroes the input section and counts
     * its frames from 1 (SharedMemoryIO.EnsureInputMapping), and it can
     * start inside the idle window, before any recycle. Forget the last
     * SeqNo as the recycle does, so its frame whose SeqNo equals this
     * program's last is read. SeenSeqNo is the worker's alone, and
     * SharedMemSeqNo is reset under InputLock below. */
    ctx->SeenSeqNo = 0;

    /* The watch connects only beside the acknowledgement section, so it is
     * mapped here. Mark the watch ended in it (TryOpenOwnerWatch) before
     * the values say so: the census reads OwnerConnected 0 beside a clear
     * marker as a program that started since (OwnerCensus.LegacyPipeLive). */
    WdfWaitLockAcquire(ctx->InputLock, NULL);
    if (ctx->InputAckMemPtr != NULL)
        InterlockedOr(&((PHIDMAESTRO_INPUT_ACK)ctx->InputAckMemPtr)->Capabilities,
                      (LONG)HIDMAESTRO_INPUT_ACK_WATCH_ENDED);
    WdfWaitLockRelease(ctx->InputLock);
    RecordOwnerGone(ctx);

    WdfWaitLockAcquire(ctx->InputLock, NULL);
    volatile HIDMAESTRO_INPUT_ACK *ack = (volatile HIDMAESTRO_INPUT_ACK *)ctx->InputAckMemPtr;
    ULONG count = 0;
    if (ack != NULL) {
        /* Seqlock read. Every version the SDK writes is a set of release
         * frames, so after four torn reads the last copy is still safe to
         * send: at worst an absolute pointer keeps a slightly older
         * position. */
        for (int attempt = 0; attempt < 4; attempt++) {
            LONG s1 = ack->ReleaseSeq;
            MemoryBarrier();
            count = (ULONG)ack->ReleaseCount;
            if (count > HIDMAESTRO_RELEASE_SLOTS) count = HIDMAESTRO_RELEASE_SLOTS;
            for (ULONG i = 0; i < count; i++) {
                ULONG size = ack->Release[i].Size;
                if (size > HIDMAESTRO_RELEASE_FRAME_CAP) size = HIDMAESTRO_RELEASE_FRAME_CAP;
                ctx->ReleaseSizes[i] = size;
                for (ULONG b = 0; b < size; b++)
                    ctx->ReleaseFrames[i][b] = ack->Release[i].Data[b];
            }
            MemoryBarrier();
            if ((s1 & 1) == 0 && s1 == ack->ReleaseSeq) break;
        }
    }
    ctx->ReleaseCount = count;
    ctx->ReleaseNext = 0;
    /* The delivered gate goes with the SeqNo: kept, it parked a read the
     * next program's first frame in the cache could serve, until that
     * program's SeqNo passed this one's last. */
    ctx->SharedMemSeqNo = 0;
    ctx->LastDeliveredInputSeqNo = 0;
    /* Polled reads (IOCTL_UMDF_HID_GET_INPUT_REPORT) see the released
     * state too: the frame for the cached report's ID replaces it. */
    for (ULONG i = 0; i < count; i++) {
        if (ctx->ReleaseSizes[i] == ctx->InputReportSize
            && (ctx->FirstInputReportId == 0 || ctx->ReleaseFrames[i][0] == ctx->InputReport[0])) {
            RtlCopyMemory(ctx->InputReport, ctx->ReleaseFrames[i], ctx->ReleaseSizes[i]);
            break;
        }
    }
    for (ULONG i = 0; i < count && ctx->CachedInputIdCount > 0 && ctx->PolledReportReady; i++) {
        if (ctx->ReleaseSizes[i] == ctx->PolledReportSize
            && (ctx->FirstInputReportId == 0 || ctx->ReleaseFrames[i][0] == ctx->PolledReport[0])) {
            RtlCopyMemory(ctx->PolledReport, ctx->ReleaseFrames[i], ctx->ReleaseSizes[i]);
            break;
        }
    }
    WdfWaitLockRelease(ctx->InputLock);

    DeliverReleaseFrames(ctx);
}

/* Hand queued release frames to the READ_REPORTs HID class has parked, one
 * frame per read. Frames left over go to the next READ_REPORTs as they
 * arrive (IOCTL_HID_READ_REPORT). */
static VOID
DeliverReleaseFrames(_In_ PDEVICE_CONTEXT ctx)
{
    for (;;) {
        UCHAR frame[HIDMAESTRO_RELEASE_FRAME_CAP];
        ULONG size;
        BOOLEAN more;
        WDFREQUEST pendingRead;

        WdfWaitLockAcquire(ctx->InputLock, NULL);
        more = ctx->ReleaseNext < ctx->ReleaseCount;
        WdfWaitLockRelease(ctx->InputLock);
        if (!more) return;

        /* The queue is taken outside InputLock, as ProcessSharedInput
         * takes it. A READ_REPORT can take the last frame meanwhile, and
         * then the read goes back where it was. */
        if (!NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->ManualQueue, &pendingRead)))
            return;
        WdfWaitLockAcquire(ctx->InputLock, NULL);
        if (ctx->ReleaseNext >= ctx->ReleaseCount) {
            WdfWaitLockRelease(ctx->InputLock);
            WdfRequestRequeue(pendingRead);
            return;
        }
        size = ctx->ReleaseSizes[ctx->ReleaseNext];
        RtlCopyMemory(frame, ctx->ReleaseFrames[ctx->ReleaseNext], size);
        ctx->ReleaseNext++;
        WdfWaitLockRelease(ctx->InputLock);

        NTSTATUS cs = RequestCopyFromBuffer(pendingRead, frame, size);
        WdfRequestComplete(pendingRead, NT_SUCCESS(cs) ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL);
    }
}

/* Open the OUTPUT section. The test app pre-creates the named section with
 * a permissive SDDL during EmulateProfile setup; we just attach with R/W
 * access. Pagefile-backed, RAM-only. We retry on every capture call until
 * the section appears (test app may not have created it yet at first IOCTL).
 *
 * IMPORTANT: WUDFHost runs as LocalService which lacks SeCreateGlobalPrivilege,
 * so the driver CANNOT CreateFileMapping in the Global\ namespace: only
 * the test app (running elevated) can. */
/* Stale-handle recovery (issue #2, output side of #1): periodic re-open
 * every 500 writes (~2s) so we pick up fresh sections after SDK teardown. */
static BOOLEAN
EnsureOutputMapping(_In_ PDEVICE_CONTEXT ctx)
{
    if (ctx->OutputMemPtr != NULL) {
        if (++ctx->OutputWriteCount < 500) return TRUE;
        UnmapViewOfFile(ctx->OutputMemPtr); ctx->OutputMemPtr = NULL;
        CloseHandle(ctx->OutputMemHandle);  ctx->OutputMemHandle = NULL;
        ctx->OutputWriteCount = 0;
    }

    HANDLE h = OpenFileMappingW(FILE_MAP_WRITE | FILE_MAP_READ, FALSE,
                                ctx->OutputMappingName);
    if (h == NULL) return FALSE;

    PVOID view = MapViewOfFile(h, FILE_MAP_WRITE | FILE_MAP_READ, 0, 0,
                               sizeof(HIDMAESTRO_SHARED_OUTPUT));
    if (view == NULL) { CloseHandle(h); return FALSE; }

    ctx->OutputMemHandle = h;
    ctx->OutputMemPtr = view;
    ctx->OutputWriteCount = 0;
    return TRUE;
}

/* Lazy-open of the PID FFB state section. Returns FALSE if the SDK
 * consumer hasn't created Global\HIDMaestroPidState<N> yet (e.g.
 * non-FFB consumer that never calls HMController.PublishPid*). The
 * GetFeature handler treats FALSE as "FFB not available" and returns
 * STATUS_NO_SUCH_DEVICE for the Pool report, matching vJoy's
 * convention for FFB-disabled devices. */
static BOOLEAN
EnsurePidStateMapping(_In_ PDEVICE_CONTEXT ctx)
{
    if (ctx->PidStateMemPtr != NULL) return TRUE;

    /* v1.1.39: opened READ_WRITE because v1.1.37+ driver-side EBI
     * allocation, FreeEbi, and ResetPidState all WRITE the section
     * (BL_* fields, EbiAllocBitmap, State_*). Pre-1.1.39 we opened
     * FILE_MAP_READ which made every write an access violation that
     * terminated WUDFHost: surfaced to the caller as Win32 1291
     * "The process hosting the driver for this device has been
     * terminated." Combined with the IOCTL_UMDF_HID_* constants being
     * wrong (the case statements never matched the framework's
     * IoControlCode), the AV path didn't trigger before v1.1.39. */
    HANDLE h = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE,
                                ctx->PidStateMappingName);
    if (h == NULL) return FALSE;

    PVOID view = MapViewOfFile(h, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0,
                               sizeof(HIDMAESTRO_SHARED_PID_STATE));
    if (view == NULL) { CloseHandle(h); return FALSE; }

    ctx->PidStateMemHandle = h;
    ctx->PidStateMemPtr = view;
    return TRUE;
}

/* Seqlocked snapshot of the PID state section. Returns FALSE if the
 * mapping is not open or the snapshot couldn't stabilize across the
 * read window (extremely rare; only happens if the SDK is publishing
 * concurrently across multiple PublishPid* calls in flight on
 * different threads, which the API doesn't sanction). */
static BOOLEAN
ReadPidState(_In_ PDEVICE_CONTEXT ctx, _Out_ HIDMAESTRO_SHARED_PID_STATE *out)
{
    if (ctx->PidStateMemPtr == NULL && !EnsurePidStateMapping(ctx))
        return FALSE;

    volatile HIDMAESTRO_SHARED_PID_STATE *src =
        (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
    ULONG seq1, seq2;
    int retries = 4;
    do {
        seq1 = src->SeqNo;
        if (seq1 & 1) { /* publisher mid-write; brief retry */
            seq1 = src->SeqNo;
        }
        MemoryBarrier();
        out->PidEnabled                     = src->PidEnabled;
        out->BL_EffectBlockIndex            = src->BL_EffectBlockIndex;
        out->BL_LoadStatus                  = src->BL_LoadStatus;
        out->BL_RAMPoolAvailable            = src->BL_RAMPoolAvailable;
        out->Pool_RAMPoolSize               = src->Pool_RAMPoolSize;
        out->Pool_MaxSimultaneousEffects    = src->Pool_MaxSimultaneousEffects;
        out->Pool_MemoryManagement          = src->Pool_MemoryManagement;
        out->State_EffectBlockIndex         = src->State_EffectBlockIndex;
        out->State_Flags                    = src->State_Flags;
        MemoryBarrier();
        seq2 = src->SeqNo;
        if (seq1 == seq2 && !(seq1 & 1)) break;
    } while (--retries > 0);

    return retries > 0;
}

/* Driver-side EBI allocator. Picks the next free EBI from the
 * shared section's bitmap, updates BL_* fields atomically (with seqlock),
 * and increments the allocated count. Pool full → BL_LoadStatus = Full
 * (PID 1.0 §5.5 enum value 2), no bit set.
 *
 * Called synchronously from the IOCTL_UMDF_HID_SET_FEATURE handler when
 * dinput8 issues SetFeature(0x11 Create New Effect). The follow-up
 * GetFeature(0x12) on the same handshake reads the same BL_* fields,
 * which are now populated. Mirrors vJoy's `Ffb_GetNextFreeEffect` +
 * `pid->PIDBlockLoad.*` synchronous update inside `Ffb_ProcessPacket`.
 */
static VOID
AllocateEbiInBlockLoad(_In_ PDEVICE_CONTEXT ctx)
{
    volatile HIDMAESTRO_SHARED_PID_STATE *pid =
        (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
    if (pid == NULL) return;

    /* Cap effect count by Pool_MaxSimultaneousEffects (consumer-published).
     * 0 means consumer hasn't published a Pool yet, in which case we use
     * the bitmap width (32) as an upper bound. */
    UCHAR cap = pid->Pool_MaxSimultaneousEffects;
    if (cap == 0 || cap > 32) cap = 32;

    /* Find first clear bit in the lowest `cap` positions. Atomic OR ensures
     * concurrent allocations from another thread don't double-issue. */
    UCHAR allocatedEbi = 0;
    UCHAR loadStatus = 2; /* Full (PID 1.0 §5.5) */
    ULONG remainingPool = 0;

    for (UCHAR ebi = 1; ebi <= cap; ebi++) {
        ULONG bit = 1UL << (ebi - 1);
        ULONG prev = (ULONG)InterlockedOr((volatile LONG *)&pid->EbiAllocBitmap, (LONG)bit);
        if ((prev & bit) == 0) {
            /* We just transitioned this bit from 0 to 1: we own EBI. */
            allocatedEbi = ebi;
            loadStatus = 1; /* Success */
            InterlockedIncrement((volatile LONG *)&pid->EbiAllocatedCount);
            break;
        }
        /* Bit was already set; another thread (or prior allocation) owns
         * it. Continue without unsetting. InterlockedOr is idempotent. */
    }

    /* RAMPoolAvailable: total pool minus a synthetic per-effect cost.
     * Real PID devices report bytes free; our consumer doesn't track the
     * underlying physical pool, so we approximate as
     *   (RAMPoolSize / cap) * (cap - allocatedCount)
     * which gives dinput8 a monotonic shrinkage as effects are created. */
    USHORT poolSize = pid->Pool_RAMPoolSize;
    ULONG allocCount = (ULONG)pid->EbiAllocatedCount;
    if (cap > 0 && allocCount <= cap) {
        remainingPool = ((ULONG)poolSize / cap) * (cap - allocCount);
    }

    /* Seqlock-write the BL_* fields. Odd SeqNo signals "writer in progress"
     * to the reader (ReadPidState retries). */
    ULONG seq = pid->SeqNo + 1; /* odd */
    pid->SeqNo = seq;
    MemoryBarrier();
    pid->BL_EffectBlockIndex   = allocatedEbi;
    pid->BL_LoadStatus         = loadStatus;
    pid->BL_RAMPoolAvailable   = (USHORT)remainingPool;
    MemoryBarrier();
    pid->SeqNo = seq + 1; /* even */
}

/* v1.1.38: PID Device Reset (CTRL_DEVRST=4). Mirrors vJoy's
 * `Ffb_ResetPIDData` (vJoy-Brunner/driver/sys/hid.c:2627). Clears all
 * EBI allocations and resets the Block Load and State fields to safe
 * initial values. Pool fields are NOT reset: those are consumer-
 * published static config. dinput8's
 * IDirectInputDevice8::SendForceFeedbackCommand(DISFFC_RESET) arrives as
 * IOCTL_UMDF_HID_SET_OUTPUT_REPORT with Report ID 0x1C, Control byte 4. */
static VOID
ResetPidState(_In_ PDEVICE_CONTEXT ctx)
{
    volatile HIDMAESTRO_SHARED_PID_STATE *pid =
        (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
    if (pid == NULL) return;

    /* Clear bitmap atomically. */
    InterlockedExchange((volatile LONG *)&pid->EbiAllocBitmap, 0);
    InterlockedExchange((volatile LONG *)&pid->EbiAllocatedCount, 0);

    /* Seqlock-write the Block Load and State fields. */
    ULONG seq = pid->SeqNo + 1;
    pid->SeqNo = seq;
    MemoryBarrier();
    pid->BL_EffectBlockIndex   = 0;
    pid->BL_LoadStatus         = 0;
    pid->BL_RAMPoolAvailable   = pid->Pool_RAMPoolSize;
    pid->State_EffectBlockIndex = 0;
    pid->State_Flags           = 0;
    MemoryBarrier();
    pid->SeqNo = seq + 1;
}

/* Driver-side EBI free. Atomically clears the bit. No-op if the bit is
 * already clear (defensive against duplicate Block Free packets).
 * Mirrors vJoy's Ffb_BlockIndexFree + RAMPool update. */
static VOID
FreeEbi(_In_ PDEVICE_CONTEXT ctx, _In_ UCHAR ebi)
{
    if (ebi < 1 || ebi > 32) return;

    volatile HIDMAESTRO_SHARED_PID_STATE *pid =
        (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
    if (pid == NULL) return;

    ULONG bit = 1UL << (ebi - 1);
    ULONG prev = (ULONG)InterlockedAnd((volatile LONG *)&pid->EbiAllocBitmap, (LONG)~bit);
    if ((prev & bit) != 0) {
        InterlockedDecrement((volatile LONG *)&pid->EbiAllocatedCount);
    }
}

/* Publish a captured output report to the shared section.
 * Source: HIDMAESTRO_OUTPUT_SOURCE_*  reportId: HID Report ID byte (0 if none)
 * data/size: payload (size will be clamped to 256). Seqlock-write pattern
 * mirrors the input direction's seqlock-read in driver/companion. */
static VOID
PublishOutput(_In_ PDEVICE_CONTEXT ctx,
              _In_ UCHAR Source,
              _In_ UCHAR ReportId,
              _In_reads_bytes_(DataSize) const UCHAR *Data,
              _In_ ULONG DataSize)
{
    if (DataSize > HIDMAESTRO_OUTPUT_SLOT_DATA_CAP)
        DataSize = HIDMAESTRO_OUTPUT_SLOT_DATA_CAP;

    /* Serialize writers (the queue dispatches IOCTLs in parallel, so two
     * threads can call PublishOutput concurrently). v1.1.40 ring buffer
     * uses per-slot seqlock for torn-write detection on the reader side;
     * the WdfWaitLock here ensures only one writer increments Head and
     * fills its slot at a time. */
    WdfWaitLockAcquire(ctx->OutputLock, NULL);

    if (!EnsureOutputMapping(ctx)) {
        WdfWaitLockRelease(ctx->OutputLock);
        return;
    }

    volatile HIDMAESTRO_SHARED_OUTPUT *dst =
        (volatile HIDMAESTRO_SHARED_OUTPUT *)ctx->OutputMemPtr;

    /* v1.1.40 ring buffer: SeqNo=0 reserved for "never written"; first
     * write is SeqNo=1. Slot index is (SeqNo - 1) % N. Writer:
     *   1) compute new SeqNo (Head + 1)
     *   2) write slot fields including Data[]
     *   3) MemoryBarrier
     *   4) write slot.SeqNo = new SeqNo (publishes the slot)
     *   5) MemoryBarrier + write Head = new SeqNo (publishes the ring head)
     *
     * Reader scans slots from LastSeen+1 to Head, validates each by
     * checking slot.SeqNo == expected, copies, re-checks SeqNo for
     * torn-write detection. If LastSeen+N < Head, oldest packets have
     * been overwritten: reader logs and skips ahead to Head-N+1. */
    /* Multi-producer reservation (audit of #34, pre-existing bug): the
     * main driver and the XUSB companion BOTH publish to this ring
     * (DirectInput FFB / HID output here, XInput rumble there), and the
     * old local-counter scheme let the two producers mint the same
     * sequence number and silently overwrite each other's slot.
     * InterlockedIncrement on the shared Head atomically reserves a
     * unique sequence for every writer in every process. The slot's
     * SeqNo store (fenced, below) remains the publish gate: the reader
     * validates slot.SeqNo == expected and simply retries a reserved-
     * but-unwritten slot on its next wake, so the reservation being
     * visible before the payload is harmless. This also removes the
     * stale-local-counter gap after an SDK section re-zero: the next
     * reservation continues from the live Head, whatever it is. */
    ULONG newSeq = (ULONG)InterlockedIncrement((volatile LONG *)&dst->Head);
    ULONG slotIdx = (newSeq - 1) % HIDMAESTRO_OUTPUT_RING_SLOTS;
    volatile HIDMAESTRO_OUTPUT_SLOT *slot = &dst->Slots[slotIdx];

    slot->Source = Source;
    slot->ReportId = ReportId;
    slot->DataSize = (USHORT)DataSize;
    for (ULONG i = 0; i < DataSize; i++) slot->Data[i] = Data[i];
    MemoryBarrier();
    slot->SeqNo = newSeq;

    WdfWaitLockRelease(ctx->OutputLock);

    /* Doorbell LAST (issue #34): Head is already published, so a reader
     * woken by this signal always sees the new packet. Signaling outside
     * the lock keeps the writer's hold time unchanged. SetEvent on an
     * already-set auto-reset event is a no-op, which coalesces bursts
     * exactly like the reader's drain-to-Head loop expects. */
    if (ctx->OutputSignalEvent) SetEvent(ctx->OutputSignalEvent);
}

/* Read shared input via memory mapping. RAM-only: no disk fallback.
 * Output: *out is filled with the shared struct on success. */
static BOOLEAN
ReadSharedInput(_In_ PDEVICE_CONTEXT ctx, _Out_ HIDMAESTRO_SHARED_INPUT *out)
{
    /* Lazy open: try the mapping on every tick until it succeeds.
     * The test app may create the section after the device starts. */
    if (ctx->SharedMemPtr == NULL && !TryOpenSharedMapping(ctx))
        return FALSE;

    /* Seqlock-style read: retry until SeqNo is stable across the copy.
     * Single writer / many readers, lock-free. */
    volatile HIDMAESTRO_SHARED_INPUT *src = (volatile HIDMAESTRO_SHARED_INPUT *)ctx->SharedMemPtr;
    ULONG seq1, seq2;
    int retries = 4;
    do {
        seq1 = src->SeqNo;
        MemoryBarrier();
        RtlCopyMemory(out, (const void *)src, sizeof(*out));
        MemoryBarrier();
        seq2 = src->SeqNo;
    } while ((seq1 != seq2 || (seq1 & 1)) && --retries > 0);
    /* Perf audit 2026-07-21 (I6): an odd SeqNo is a write in progress and
     * an unequal pair is a torn copy; serving either hands a half-written
     * frame downstream. Skip the frame instead: the SDK's per-frame
     * SetEvent redelivers within one frame interval. */
    if (seq1 != seq2 || (seq1 & 1))
        return FALSE;
    return TRUE;
}

/* The frame's data lane as the live feature report's answer. Under
 * InputLock. */
static VOID
KeepLiveFeatureLocked(_In_ PDEVICE_CONTEXT ctx, _In_ const HIDMAESTRO_SHARED_INPUT *shared)
{
    ULONG n = shared->DataSize;
    if (n > sizeof(shared->Data)) n = sizeof(shared->Data);
    if (n > sizeof(ctx->LiveFeature)) n = sizeof(ctx->LiveFeature);
    RtlCopyMemory(ctx->LiveFeature, shared->Data, n);
    ctx->LiveFeatureSize = n;
}

/* Core per-frame work extracted from the old EvtSharedMemTimer.
 * Called from the event-driven worker thread whenever the SDK signals
 * InputDataEvent (or the 50 ms safety tick fires). Doing all the HID
 * report-build + manual-queue drain here: no WDF timer, no IRQL games:
 * WdfRequestComplete / WdfWaitLock* are documented safe from a raw worker
 * thread in UMDF2. */
static void
ProcessSharedInput(_In_ PDEVICE_CONTEXT ctx)
{
    HIDMAESTRO_SHARED_INPUT shared;
    if (!ReadSharedInput(ctx, &shared)) return;

    ULONG seqNo = shared.SeqNo;
    if (seqNo == ctx->SeenSeqNo) return; /* No new data */
    /* v2.0.0: the worker's stale-wakeup count reads SeenSeqNo, since a
     * frame the driver does not send leaves SharedMemSeqNo alone. */
    ctx->SeenSeqNo = seqNo;
    /* NOTE: SharedMemSeqNo is advanced LATER, under InputLock, together
     * with the InputReport cache write (see the completion block below).
     * Publishing it here would let a concurrent IOCTL_HID_READ_REPORT
     * observe the advanced seqno and complete with the PREVIOUS frame's
     * cached report tagged as the new one (stale-frame TOCTOU). The
     * local `seqNo` drives the rest of this call. */

    /* v2.0.0: with LiveFeatureReport the data lane is the answer to that
     * feature report, never an input report. A frame that carries nothing
     * else only replaces the answer. */
    if (ctx->LiveFeatureReport != 0 && shared.ExtendedReportSize == 0) {
        WdfWaitLockAcquire(ctx->InputLock, NULL);
        KeepLiveFeatureLocked(ctx, &shared);
        WdfWaitLockRelease(ctx->InputLock);
        return;
    }

    /* Build HID input report from shared file Data (native descriptor format).
     * Report MUST be exactly InputReportByteLength bytes: HidClass rejects
     * short reports.  Zero-fill first, then overlay actual data.
     *
     * v1.3.5: vendor-blob mode-switch path. When ExtendedReportSize > 0
     * the SDK passes the FULL RID-included extended report (e.g. 78-byte
     * Sony BT Report 0x31 with CRC32). Pass through verbatim. */
    UCHAR inputReport[HIDMAESTRO_MAX_REPORT_SIZE];

    ULONG dataLen = shared.DataSize;
    BOOLEAN hasReportId = (ctx->FirstInputReportId != 0);
    ULONG expectedSize = ctx->InputReportByteLength > 0 ? ctx->InputReportByteLength : 17;
    /* Only the bytes that go out: every path below sends at most
     * expectedSize bytes of this buffer, or bytes it wrote itself. */
    RtlZeroMemory(inputReport, expectedSize);

    ULONG maxData;
    if (hasReportId) {
        maxData = expectedSize > 1 ? expectedSize - 1 : 16;
    } else {
        maxData = expectedSize;
    }
    if (dataLen > maxData) dataLen = maxData;
    if (dataLen > sizeof(shared.Data)) dataLen = sizeof(shared.Data);

    ULONG inputSize;
    if (hasReportId) {
        inputReport[0] = ctx->FirstInputReportId;
        RtlCopyMemory(inputReport + 1, shared.Data, dataLen);
        inputSize = expectedSize; /* Always send full expected length */
    } else {
        RtlCopyMemory(inputReport, shared.Data, dataLen);
        inputSize = expectedSize; /* Always send full expected length */
    }

    /* v1.3.5: vendor-blob mode-switch path. When the SDK arms extended
     * emission (Sony BT post-handshake, ExtendedReportSize > 0), it has
     * already written the FULL RID-included extended report (e.g. 78-byte
     * Sony BT Report 0x31 with CRC32) into shared.ExtendedReportData.
     * Overwrite the legacy-encoded inputReport/inputSize with the
     * pass-through bytes: the legacy encode above is harmless work for
     * the few-microsecond window before the overwrite, which keeps the
     * function single-path-shape (Visual C++ codegen produces materially
     * different code under compilers' cache-line / branch-prediction
     * heuristics for nested if-else than for straight-line; an early
     * benchmark with the extended path as a leading branch showed a
     * ~6× input-rate regression on USB DS5 even when the branch was not
     * taken: see issue #21). For the common case (ExtendedReportSize=0)
     * this is one cmp + jz; on modern x86 with branch prediction it's
     * effectively free. */
    if (shared.ExtendedReportSize > 0
        && shared.ExtendedReportSize <= sizeof(shared.ExtendedReportData)
        && shared.ExtendedReportSize <= sizeof(inputReport))
    {
        /* v2.0.0: only at its report's declared length. Any other frame
         * is not sent, and the last sent report stands. */
        if (!ExtendedFrameDeclared(ctx, shared.ExtendedReportData, shared.ExtendedReportSize))
            return;
        RtlZeroMemory(inputReport, inputSize);
        RtlCopyMemory(inputReport, shared.ExtendedReportData, shared.ExtendedReportSize);
        inputSize = shared.ExtendedReportSize;
    }

    /* Complete exactly ONE pending READ_REPORT per shared-memory state
     * change: not ALL queued requests. HidClass pre-queues READ_REPORTs
     * for performance; draining the entire queue with the same cached
     * report means one logical press from user mode becomes N HID
     * reports (where N = queue depth), each of which RawInput delivers
     * as a separate WM_INPUT. Consumers that handle RawInput per-message
     * (Start Menu / Xbox accessories UI) then register N navigation
     * events per single press: the triple/double-movement bug in
     * issue #8, empirically verified via InputSourceCounter probe
     * (5 WM_INPUTs from one hDevice per single press, state change
     * visible only once at XInput/RGC/UINav).
     *
     * One report per state change matches what real hardware does:
     * a physical Xbox controller produces exactly one HID report per
     * actual input change, not N reports to satisfy N queued reads.
     * Subsequent queued READ_REPORTs stay parked until the SDK
     * SubmitStates the next frame. */
    {
        BOOLEAN servedOne = FALSE;
        NTSTATUS cs = STATUS_SUCCESS;
        WDFREQUEST pendingRead = NULL;
        if (NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->ManualQueue, &pendingRead))) {
            servedOne = TRUE;
            cs = RequestCopyFromBuffer(pendingRead, inputReport, inputSize);
        }
        /* Store for polled GET_INPUT_REPORT, and bump the seqno gate
         * so the next IOCTL_HID_READ_REPORT for this seqno completes
         * directly (the parked one, if any, was taken above and completes
         * below). */
        WdfWaitLockAcquire(ctx->InputLock, NULL);
        RtlCopyMemory(ctx->InputReport, inputReport, inputSize);
        ctx->InputReportSize = inputSize;
        ctx->InputReportReady = TRUE;
        if (ctx->CachedInputIdCount > 0
            && InputIdCached(ctx, ctx->UsesReportIds ? inputReport[0] : (UCHAR)0)) {
            RtlCopyMemory(ctx->PolledReport, inputReport, inputSize);
            ctx->PolledReportSize = inputSize;
            ctx->PolledReportReady = TRUE;
        }
        if (ctx->LiveFeatureReport != 0)
            KeepLiveFeatureLocked(ctx, &shared);
        /* Publish the seqno LAST, under the same lock IOCTL_HID_READ_REPORT
         * takes, so a concurrent read sees either the old seqno (and parks)
         * or the new seqno paired with the freshly-written InputReport.
         * Advancing it in ProcessSharedInput's preamble (before this cache
         * write) was the stale-frame TOCTOU. */
        ctx->SharedMemSeqNo = seqNo;
        /* Perf audit 2026-07-21 (I5): advance the delivered gate ONLY when
         * a parked read was actually completed above. The unconditional
         * advance contradicted this block's own header comment and
         * disabled the late-read cache: a READ_REPORT arriving with no
         * request parked saw SharedMemSeqNo == LastDelivered, parked, and
         * waited a full frame interval for data already sitting in the
         * cache. The spin-trap the gate exists for stays closed because
         * the immediate-complete path advances LastDelivered itself. */
        if (servedOne) {
            ctx->LastDeliveredInputSeqNo = seqNo;
            NoteFrameCompletedLocked(ctx, seqNo);
        }
        WdfWaitLockRelease(ctx->InputLock);
        /* Complete only now. HID class can send its next READ_REPORT the
         * moment this one completes, and that read has to find AckPending
         * set. Completing before the lock let it arrive first, find nothing
         * to acknowledge and park, so the acknowledgement waited for a read
         * that only the SDK's next frame would bring: the SDK sat out its
         * full delivery timeout, most often on a busy machine. */
        if (servedOne)
            WdfRequestComplete(pendingRead, NT_SUCCESS(cs) ? STATUS_SUCCESS : STATUS_BUFFER_TOO_SMALL);
    }
}

/* Event-driven worker thread. Bulletproof design: the ONLY way this
 * function returns is StopEvent signaled WITH ctx->TearingDown set
 * (issue #38). Every other condition, including a foreign signal on the
 * shared named StopEvent, plus WAIT_FAILED, WAIT_TIMEOUT, stale-handle
 * detection, invalid-handle, OpenEvent/OpenFileMapping failure,
 * recycles the handles and loops back to Phase 1 to re-discover fresh
 * kernel objects.
 *
 * This is a deliberate departure from the prior "5s timeout OR 250 stale
 * wakeups" logic, which could leave the worker stuck in scenarios where
 * the SDK kept signaling the old event (keeping staleWakeups small) but
 * the shared-memory view was pointing at destroyed/stale pages. In that
 * state the 5s timeout never fired (events kept arriving) and the stale
 * counter reset on each signal, so recycle never triggered: permanent
 * deadlock until WUDFHost was killed.
 *
 * Two-phase wait:
 *   Phase 1 (bootstrap): the driver may attach before the SDK has created
 *     Global\HIDMaestroInputEvent<N>. Wait on StopEvent only with a short
 *     200 ms timeout; on each timeout, retry OpenEventW. Once it succeeds,
 *     drop into Phase 2.
 *   Phase 2 (steady state): wait on (StopEvent, InputDataEvent) with a
 *     500 ms timeout so even if the SDK never signals, we recycle and
 *     re-verify handles every half second. When the SDK is active this
 *     is still effectively zero CPU (events arrive well under 500 ms)
 *     the timeout is a safety net, not a polling interval.
 *
 * StopEvent is signaled from:
 *   (a) ReleaseIndexObjects: normal PnP teardown. Sets
 *       ctx->TearingDown FIRST, so the worker returns 0.
 *   (b) External SDK RemoveAllVirtualControllers cleanup: opens the
 *       named stop event and signals it to unblock worker threads of
 *       force-killed prior processes
 *   (c) A same-index sibling context's cleanup: the event is a NAMED
 *       object, so an orphan device tearing down late signals the same
 *       kernel object a freshly created device at that index waits on.
 * Issue #38: (b) and (c) used to return 0 too, permanently freezing a
 * healthy device's output at its last report (the SDK writer keeps
 * writing, nobody processes, parked READ_REPORTs never complete) while
 * everything looks alive from user land. The worker now exits ONLY when
 * ctx->TearingDown is set; foreign signals are absorbed (ResetEvent on
 * the manual-reset object, then recycle to Phase 1). The 2-second
 * thread-join in ReleaseIndexObjects is the backstop if the worker
 * is somehow stuck outside the wait (e.g., inside ProcessSharedInput's
 * WdfRequestComplete during a concurrent teardown), and also bounds the
 * benign race where a foreign absorb's ResetEvent eats our own
 * teardown's signal a beat before the flag re-check would have caught
 * it. */
static DWORD WINAPI
SharedInputWorkerProc(_In_ LPVOID Parameter)
{
    PDEVICE_CONTEXT ctx = (PDEVICE_CONTEXT)Parameter;

    /* Outer recovery loop. Phase 1 discovers/re-discovers the named event;
     * Phase 2 processes frames until StopEvent OR until we detect stale
     * handles, at which point we fall back through to Phase 1 with NULL
     * handles to re-open fresh. There is NO return path out of this loop
     * except StopEvent with TearingDown set (issue #38). */
    for (;;) {
        /* Phase 1: bootstrap: wait for the SDK to create the named event.
         * StopEvent is checked on every 200 ms tick so teardown stays
         * responsive even when the SDK hasn't started up yet. */
        while (ctx->InputDataEvent == NULL) {
            /* Process a pipe close here too: a watch that
             * connected on an earlier map must not sit stale while the
             * worker waits for the input event to come back. */
            if (ctx->OwnerReadEvent != NULL
                && WaitForSingleObject(ctx->OwnerReadEvent, 0) == WAIT_OBJECT_0)
                OwnerGone(ctx);
            HANDLE ev = OpenEventW(EVENT_MODIFY_STATE | SYNCHRONIZE, FALSE,
                                   ctx->InputEventName);
            if (ev != NULL) {
                ctx->InputDataEvent = ev;
                /* The remove events, which an SDK of this release
                 * makes with the input event. */
                ctx->RemoveEvent = OpenEventW(SYNCHRONIZE, FALSE, ctx->RemoveEventName);
                ctx->RemovedEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, ctx->RemovedEventName);
                /* The SDK creates the input section (and the delivery
                 * acknowledgement beside it, issue #67) before the event,
                 * so map both now rather than on the first frame: a writer
                 * that waits on delivery sees the capability before it
                 * sends anything. The worker owns this open, as it does
                 * the lazy one in ReadSharedInput. */
                if (ctx->SharedMemPtr == NULL)
                    TryOpenSharedMapping(ctx);
                break;
            }
            {
                /* Wait on the owner read too while it is open, so a pipe
                 * close during bootstrap is handled at once.
                 * The next loop turn runs OwnerGone above. */
                HANDLE boot[2] = { ctx->StopEvent, ctx->OwnerReadEvent };
                DWORD rc1 = WaitForMultipleObjects(ctx->OwnerReadEvent ? 2 : 1, boot, FALSE, 200);
                /* Issue #38: the FLAG is the authoritative exit
                 * condition, checked on EVERY wake including timeouts.
                 * The signal alone cannot be trusted in either
                 * direction: a foreign sweep signals without teardown,
                 * and a same-index sibling's device-start ResetEvent
                 * (driver.c device start) can EAT our own cleanup's
                 * SetEvent on the shared named object, leaving teardown
                 * signal-less. Relying on the signal there left an
                 * immortal worker competing for the input event with
                 * the successor device's worker (frame theft, stale
                 * GET_INPUT_REPORT cache: the S34 regression). */
                if (ctx->TearingDown)
                    return 0;
                if (rc1 == WAIT_OBJECT_0) {
                    /* Foreign signal during bootstrap: absorb. */
                    ResetEvent(ctx->StopEvent);
                    if (ctx->TearingDown)
                        return 0;
                }
                /* WAIT_OBJECT_0 + 1 is the owner read, run at the top of
                 * the next turn. */
            }
        }

        /* Phase 2: steady state. The 500 ms timeout + unconditional recycle
         * on ANY non-signal rc (TIMEOUT, FAILED, ABANDONED, unexpected)
         * guarantees recovery from every class of stale-handle failure
         * within half a second of the SDK stopping signaling. The stale-
         * seqno counter recycles after 250 wakeups (~5s at ~50 Hz input)
         * to catch the "event fires but shared-memory view is stale" case,
         * where the SDK keeps signaling an event object we share by name
         * but writes to a view the driver isn't reading from anymore. */
        ULONG staleWakeups = 0;
        ULONG idleTimeouts = 0;
        ULONG frameTick = 0;
        BOOLEAN recycle = FALSE;

        for (;;) {
            /* The owner watch (issue #67) joins the wait while it is open.
             * After it fires it is closed and the wait is back to two. */
            /* The remove event joins last, after the owner read while
             * that is open, so the indices below stay as they were. */
            HANDLE waits[4] = { ctx->StopEvent, ctx->InputDataEvent, ctx->OwnerReadEvent, ctx->RemoveEvent };
            DWORD count = 2;
            if (ctx->OwnerReadEvent != NULL) count = 3;
            DWORD removeAt = MAXDWORD;
            if (ctx->RemoveEvent != NULL) {
                waits[count] = ctx->RemoveEvent;
                removeAt = count++;
            }
            DWORD rc = WaitForMultipleObjects(count, waits, FALSE, 500);

            /* Issue #38: TearingDown is the authoritative exit
             * condition, checked on EVERY wake including timeouts. The
             * StopEvent signal cannot be trusted in either direction:
             * a foreign sweep signals without teardown, and a
             * same-index sibling's device-start ResetEvent can EAT our
             * own cleanup's SetEvent on the shared named object,
             * leaving teardown signal-less. Pre-flag-poll, that eaten
             * signal left an immortal worker past the cleanup join
             * competing with the successor device's worker for the
             * auto-reset input event (frame theft, stale
             * GET_INPUT_REPORT cache: the S34 regression). Worst-case
             * exit latency is one 500 ms wait, inside cleanup's 2 s
             * join. */
            if (ctx->TearingDown)
                return 0;

            if (rc == WAIT_OBJECT_0) {
                /* FOREIGN signal on the shared named event (another
                 * process's sweep, or a same-index orphan's late
                 * cleanup). Returning here left a healthy device frozen
                 * at its last report forever (issue #38). Absorb
                 * instead: reset the manual-reset object (else this
                 * loop spins), re-check the flag (our own cleanup may
                 * set-and-signal between the wait and the reset, and
                 * the reset eats that signal), then recycle handles
                 * through Phase 1 exactly like every other non-fatal
                 * wake. */
                ResetEvent(ctx->StopEvent);
                if (ctx->TearingDown)
                    return 0;
                recycle = TRUE;
                break;
            }

            if (rc == WAIT_OBJECT_0 + 1) {
                idleTimeouts = 0;
                ULONG prevSeq = ctx->SeenSeqNo;
                ProcessSharedInput(ctx);
                if (ctx->SeenSeqNo == prevSeq) {
                    if (++staleWakeups > 250) { recycle = TRUE; break; }
                } else {
                    staleWakeups = 0;
                }
                /* Every 256 frames, after the frame so neither attempt adds
                 * to its latency, retry what a map would otherwise retry. A
                 * streaming consumer never idles into a recycle.
                 *   The output doorbell, which the device could not make
                 *   while a program held the name.
                 *   The owner watch, while none is connected. A program
                 *   that starts on the index while the worker still holds
                 *   the previous program's input event, inside the idle
                 *   window before a recycle, opens that same event and
                 *   section, so no map follows. TryOpenOwnerWatch reads the
                 *   ended marker first, so a squatter's pipe is still
                 *   refused. While no pipe exists the cost is one failed
                 *   CreateFileW. */
                if ((++frameTick & 0xFF) == 0) {
                    if (ctx->OutputSignalEvent == NULL)
                        TryCreateDoorbell(ctx);
                    if (ctx->OwnerPipe == NULL)
                        TryOpenOwnerWatch(ctx);
                }
                continue;
            }

            if (removeAt != MAXDWORD && rc == WAIT_OBJECT_0 + removeAt) {
                /* The SDK's Dispose asks the device to leave as an
                 * unplugged one does. A driver with an unrecoverable error
                 * "must call WdfDeviceSetFailed so that the system can
                 * unload the device's drivers" (Learn, WdfDeviceSetFailed),
                 * and a function driver that reports its device failed
                 * starts a surprise removal (Learn, "Handling an
                 * IRP_MN_SURPRISE_REMOVAL Request"), which no open handle
                 * vetoes. Measured 2026-10-19: the device and its HID child
                 * left the tree within 70 ms while another program held
                 * the child open. A removal Windows queries instead is
                 * vetoed by that handle, and the vetoed SetupDiRemoveDevice
                 * left the child's record without its driver until a
                 * reboot. DsHidMini calls WdfDeviceSetFailed the same way,
                 * off any PnP callback as this worker is, with
                 * WdfDeviceFailedAttemptRestart to be enumerated again
                 * (driver\Device.c, DsDevice_EvtHidModeRestartTimerFunc).
                 * One request is all it takes, so the event closes here. */
                CloseHandle(ctx->RemoveEvent);
                ctx->RemoveEvent = NULL;
                WdfDeviceSetFailed(ctx->Device, WdfDeviceFailedNoRestart);
                if (ctx->RemovedEvent != NULL) SetEvent(ctx->RemovedEvent);
                continue;
            }

            if (rc == WAIT_OBJECT_0 + 2) {
                /* The consumer's end of the owner pipe closed. */
                OwnerGone(ctx);
                continue;
            }

            /* Perf audit 2026-07-21 (I2): a single 500 ms timeout is the
             * NORMAL idle state (consumer between frames, game menus),
             * and recycling handles on every one cost an unmap/reopen
             * cycle twice a second plus a mapping tax on the first frame
             * after every pause. Recycle only after 8 consecutive idle
             * timeouts (~4 s): stale-handle recovery after an SDK restart
             * still converges within 4 s, and the signaled-but-stale case
             * keeps its own 250-wakeup counter above. */
            if (ctx->OutputSignalEvent == NULL)
                TryCreateDoorbell(ctx);
            if (rc == WAIT_TIMEOUT && ++idleTimeouts < 8)
                continue;

            /* WAIT_TIMEOUT streak exhausted, WAIT_FAILED (0xFFFFFFFF), or
             * any other unexpected value. Previously WAIT_FAILED returned
             * 0 and killed the worker permanently; now we recycle like
             * every other non-signal path and let Phase 1 re-open fresh
             * handles. The 2-second thread-join timeout in
             * ReleaseIndexObjects still bounds any teardown race. */
            recycle = TRUE;
            break;
        }

        if (recycle) {
            /* Close stale handles. Phase 1 will re-open fresh ones.
             * Unmapping the view first, then closing handles, keeps the
             * sequence symmetric with TryOpenSharedMapping. */
            CloseHandle(ctx->InputDataEvent);
            ctx->InputDataEvent = NULL;
            if (ctx->RemoveEvent)  { CloseHandle(ctx->RemoveEvent);  ctx->RemoveEvent = NULL; }
            if (ctx->RemovedEvent) { CloseHandle(ctx->RemovedEvent); ctx->RemovedEvent = NULL; }

            if (ctx->SharedMemPtr)    { UnmapViewOfFile(ctx->SharedMemPtr);    ctx->SharedMemPtr = NULL; }
            if (ctx->SharedMemHandle) { CloseHandle(ctx->SharedMemHandle);     ctx->SharedMemHandle = NULL; }
            /* The acknowledgement stays mapped until the device goes.
             * Its section is how the SDK's census tells a v1.12.0
             * program that has exited from one older than v1.12.0
             * (OwnerCensus.ByLegacySigns), so it must outlive the
             * program for as long as the device exists. The name stays
             * bound to this object while the driver holds it, so the
             * next SDK on this index opens the same section, and
             * TryOpenSharedMapping maps it again with the input
             * section. */
            /* Also reset the cached SeqNos so the fresh mapping's first read
             * (even if it happens to land on a SeqNo matching our previous
             * cached value by chance) is treated as new data. SeenSeqNo is
             * the one ReadSharedInput's gate reads, and a program that
             * starts again on the index counts its frames from the start.
             * The delivered gate goes with SharedMemSeqNo, under the lock
             * READ_REPORT reads both under, as OwnerGone resets them. */
            WdfWaitLockAcquire(ctx->InputLock, NULL);
            ctx->SharedMemSeqNo = 0;
            ctx->LastDeliveredInputSeqNo = 0;
            WdfWaitLockRelease(ctx->InputLock);
            ctx->SeenSeqNo = 0;
        }
    }
}

/* ================================================================== */
/* Release: let go of every object of the index, on device teardown.
 * Runs from the self-managed I/O flush, after the I/O queues are purged,
 * and again from the context cleanup, where it finds nothing left: each
 * step clears what it closed. */
static VOID ReleaseIndexObjects(_In_ PDEVICE_CONTEXT ctx)
{
    /* Stop the worker thread first so nothing is touching SharedMemPtr
     * while we unmap. TearingDown BEFORE SetEvent (issue #38): the named
     * stop event is shared with sibling contexts and foreign sweeps, and
     * the flag is what tells OUR worker this wake is its real teardown.
     * SetEvent → 2-second join, which should be instant (the worker
     * wakes on the stop event and returns). */
    InterlockedExchange((volatile LONG *)&ctx->TearingDown, 1);
    if (ctx->StopEvent) SetEvent(ctx->StopEvent);
    if (ctx->WorkerThread) {
        WaitForSingleObject(ctx->WorkerThread, 2000);
        CloseHandle(ctx->WorkerThread);
        ctx->WorkerThread = NULL;
    }

    if (ctx->InputDataEvent) { CloseHandle(ctx->InputDataEvent); ctx->InputDataEvent = NULL; }
    if (ctx->RemoveEvent)    { CloseHandle(ctx->RemoveEvent);    ctx->RemoveEvent = NULL; }
    if (ctx->RemovedEvent)   { CloseHandle(ctx->RemovedEvent);   ctx->RemovedEvent = NULL; }
    if (ctx->StopEvent)      { CloseHandle(ctx->StopEvent);      ctx->StopEvent = NULL; }
    if (ctx->OutputSignalEvent) { CloseHandle(ctx->OutputSignalEvent); ctx->OutputSignalEvent = NULL; }

    if (ctx->SharedMemPtr) { UnmapViewOfFile(ctx->SharedMemPtr); ctx->SharedMemPtr = NULL; }
    if (ctx->SharedMemHandle) { CloseHandle(ctx->SharedMemHandle); ctx->SharedMemHandle = NULL; }
    /* Workers are joined and the queues purged before this runs, so no
     * read path can be touching the acknowledgement any more. */
    CloseInputAckLocked(ctx);
    CloseOwnerWatch(ctx);
    if (ctx->OutputMemPtr) { UnmapViewOfFile(ctx->OutputMemPtr); ctx->OutputMemPtr = NULL; }
    if (ctx->OutputMemHandle) { CloseHandle(ctx->OutputMemHandle); ctx->OutputMemHandle = NULL; }
    if (ctx->PidStateMemPtr) { UnmapViewOfFile(ctx->PidStateMemPtr); ctx->PidStateMemPtr = NULL; }
    if (ctx->PidStateMemHandle) { CloseHandle(ctx->PidStateMemHandle); ctx->PidStateMemHandle = NULL; }
}

/* The device is gone: surprise removed, or removed. A host can process
 * IRP_MN_SURPRISE_REMOVAL and never receive the IRP_MN_REMOVE_DEVICE that
 * runs the context cleanup, since the PnP manager sends it only after
 * every handle to the device is closed. Until then the host kept every
 * object of the index with no device behind it, and a later create on
 * the index met them. The framework runs this flush on both paths and
 * never on a stop for a rebalance, which keeps the device and restarts it
 * without a new EvtDeviceAdd. It purges the power-managed queues first,
 * but in filter mode (WdfFdoInitSetFilter) neither queue is power
 * managed, and those it purges only at the removal. Purged here, they
 * finish every request in flight and refuse new ones, so no I/O callback
 * reads a view the release unmaps, or opens one again. */
static EVT_WDF_DEVICE_SELF_MANAGED_IO_FLUSH EvtDeviceSelfManagedIoFlush;
static VOID EvtDeviceSelfManagedIoFlush(_In_ WDFDEVICE Device)
{
    PDEVICE_CONTEXT ctx = GetDeviceContext(Device);
    WdfIoQueuePurgeSynchronously(ctx->DefaultQueue);
    WdfIoQueuePurgeSynchronously(ctx->ManualQueue);
    ReleaseIndexObjects(ctx);
}

static EVT_WDF_OBJECT_CONTEXT_CLEANUP EvtDeviceContextCleanup;
static void EvtDeviceContextCleanup(_In_ WDFOBJECT Object)
{
    /* A device that never started gets no flush. */
    ReleaseIndexObjects(GetDeviceContext((WDFDEVICE)Object));
}

/*  DriverEntry                                                        */
/* ================================================================== */

NTSTATUS
DriverEntry(
    _In_ PDRIVER_OBJECT  DriverObject,
    _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    return WdfDriverCreate(
        DriverObject, RegistryPath,
        WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

/* ================================================================== */
/*  Named events                                                       */
/* ================================================================== */

/* The driver's named events (StopEvent, OutputEvent) admit SYSTEM,
 * Administrators and LocalService: the SDK's own descriptor without the
 * read for everyone (SharedMemoryIO.Sddl). The SDK, v1.12.0's included,
 * opens them elevated, and both driver hosts run as LocalService. Until
 * v2.0.0 they had a NULL DACL, which let any local program signal a
 * controller's StopEvent or wait on and reset its OutputEvent.
 * companion.c makes its OutputEvent the same way. */
static const WCHAR g_HostEventSddl[] = L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)";

/* Create the named event, or open it when a host made it first: the other
 * host of the same controller, or an earlier device context on the same
 * index. Any program can create a Global\ event, so one that exists already
 * is used only when its owner is one a program that is not elevated cannot
 * give an object: LocalService, SYSTEM or Administrators with a DACL.
 *
 * With takeOverNullDacl and a LocalService or SYSTEM owner, a NULL DACL is
 * also taken: a driver before v2.0.0 made the object that way, and this
 * host, which runs as that account, replaces the DACL with its own. This is
 * for the output doorbell only. A program that opened the old NULL-DACL
 * object keeps every right its handle was granted, WRITE_DAC included, so a
 * holder of the StopEvent could put the NULL DACL back and signal the
 * worker through its old handle. The doorbell is read-only to a consumer
 * and carries no such risk. The StopEvent therefore passes FALSE and falls
 * back to an unnamed event, as before v2.0.0 took the NULL DACL at all.
 * The SDK's reader applies the same rule for the doorbell
 * (SharedMemoryIO.TryOpenOutputEvent). NULL when the name is unusable or
 * the create fails. */
static HANDLE
CreateHostEvent(_In_ BOOL manualReset, _In_ BOOL takeOverNullDacl, _In_ LPCWSTR name)
{
    PSECURITY_DESCRIPTOR sd = NULL;
    SECURITY_ATTRIBUTES sa;
    HANDLE ev;
    BOOL usable = FALSE;

    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            g_HostEventSddl, SDDL_REVISION_1, &sd, NULL))
        return NULL;
    sa.nLength = sizeof(sa);
    sa.lpSecurityDescriptor = sd;
    sa.bInheritHandle = FALSE;
    ev = CreateEventW(&sa, manualReset, FALSE, name);
    if (ev == NULL || GetLastError() != ERROR_ALREADY_EXISTS) {
        LocalFree(sd);
        return ev;
    }

    {
        PSID owner = NULL;
        PACL dacl = NULL;
        PSECURITY_DESCRIPTOR found = NULL;
        if (GetSecurityInfo(ev, SE_KERNEL_OBJECT,
                            OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                            &owner, NULL, &dacl, NULL, &found) == ERROR_SUCCESS) {
            BOOL hostOwner = owner != NULL
                && (IsWellKnownSid(owner, WinLocalServiceSid) || IsWellKnownSid(owner, WinLocalSystemSid));
            BOOL adminOwner = owner != NULL && IsWellKnownSid(owner, WinBuiltinAdministratorsSid);
            if (dacl != NULL) {
                usable = hostOwner || adminOwner;
            } else if (hostOwner && takeOverNullDacl) {
                /* The output doorbell only (see the header). A driver before
                 * v2.0.0 made it with a NULL DACL, and a program still
                 * holds it, the SDK's output reader most often. A program
                 * that is not elevated cannot own an object as LocalService
                 * or SYSTEM. The owner's account may rewrite the DACL, so
                 * this host, which runs as that account, gives the object
                 * the descriptor above. A new open by name is then checked
                 * against it. A handle opened before keeps the access it was
                 * granted, WRITE_DAC among them against a NULL DACL, which is
                 * why the StopEvent is never taken this way. */
                PACL ours = NULL;
                BOOL present = FALSE, defaulted = FALSE;
                usable = GetSecurityDescriptorDacl(sd, &present, &ours, &defaulted) && present && ours != NULL
                    && SetSecurityInfo(ev, SE_KERNEL_OBJECT,
                                       DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                                       NULL, NULL, ours, NULL) == ERROR_SUCCESS;
            }
            LocalFree(found);
        }
    }
    LocalFree(sd);
    if (!usable) {
        CloseHandle(ev);
        ev = NULL;
    }
    return ev;
}

/* ================================================================== */
/*  EvtDeviceAdd                                                       */
/* ================================================================== */

NTSTATUS
EvtDeviceAdd(
    _In_    WDFDRIVER       Driver,
    _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    NTSTATUS                status;
    WDF_OBJECT_ATTRIBUTES   attributes;
    WDFDEVICE               device;
    PDEVICE_CONTEXT         ctx;
    WDF_IO_QUEUE_CONFIG     queueConfig;

    UNREFERENCED_PARAMETER(Driver);

    /* HIDMaestro.dll only loads for HIDClass devices (gamepad companion).
     * XUSB companion uses HMXInput.dll: separate DLL, no shared code. */

    /* v2.0.0, before WdfDeviceCreate: the devnode's token, the index the
     * identity entry names, the hardware key's index, and from those the
     * controller index and whether the version contract holds (decision
     * (c)). A devnode with no index, or that is not the controller its
     * section names, fails its start here. */
    WCHAR token[40];
    ULONG identityIndex = 0, hardwareIndex = 0, controllerIndex = 0;
    BOOLEAN haveIdentityIndex, haveHardwareIndex, contract = FALSE;
    HmReadInstanceToken(DeviceInit, token, ARRAYSIZE(token));
    haveIdentityIndex = HmReadIdentityIndex(token, &identityIndex);
    haveHardwareIndex = ReadHardwareIndex(DeviceInit, &hardwareIndex);
    status = ChooseControllerIndex(token, haveIdentityIndex, identityIndex,
                                   haveHardwareIndex, hardwareIndex, &controllerIndex, &contract);
    if (!NT_SUCCESS(status)) return status;

    /* FunctionMode=1 skips filter mode so we can register XUSB on the HID device.
     * This tells DI to use XInput mapping (5 axes) instead of raw HID.
     * Also used later to skip WinExInput on main device (companion handles it). */
    DWORD functionMode = 0;
    {
        HKEY hFm;
        /* FunctionMode is read BEFORE device creation, from the key of the
         * controller index chosen above. Before v2.0.0 it was always
         * Controller0's. A devnode whose index has no key is left behind
         * by a program and fails its start: there is no default device. */
        WCHAR fmPath[64];
        static const WCHAR fmPrefix[] = L"SOFTWARE\\HIDMaestro\\Controller";
        RtlCopyMemory(fmPath, fmPrefix, sizeof(fmPrefix));
        AppendUlongDecimal(fmPath, controllerIndex, ARRAYSIZE(fmPath));
        if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, fmPath, 0, KEY_READ, &hFm) != ERROR_SUCCESS)
            return STATUS_DEVICE_CONFIGURATION_ERROR;
        DWORD val, sz = sizeof(val);
        if (RegQueryValueExW(hFm, L"FunctionMode", NULL, NULL, (LPBYTE)&val, &sz) == ERROR_SUCCESS)
            functionMode = val;
        RegCloseKey(hFm);
        if (!functionMode)
            WdfFdoInitSetFilter(DeviceInit);
    }

    {
        WDF_PNPPOWER_EVENT_CALLBACKS pnpPower;
        WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpPower);
        pnpPower.EvtDeviceSelfManagedIoFlush = EvtDeviceSelfManagedIoFlush;
        WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnpPower);
    }

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    attributes.EvtCleanupCallback = EvtDeviceContextCleanup;

    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    ctx = GetDeviceContext(device);
    RtlZeroMemory(ctx, sizeof(DEVICE_CONTEXT));
    ctx->Device = device;

    /* Initialize per-instance paths (registry key, shared file) from the
     * controller index */
    InitInstancePaths(ctx, controllerIndex);
    ctx->Contract = contract;
    for (SIZE_T i = 0; i < ARRAYSIZE(token); i++) ctx->IdentityToken[i] = token[i];

    /* The HID descriptor's fixed fields. The report descriptor, the
     * attributes, the input length and the strings come from Controller<N>
     * alone: there is no default device. */
    ctx->HidDescriptor.bLength          = 0x09;
    ctx->HidDescriptor.bDescriptorType  = 0x21;
    ctx->HidDescriptor.bcdHID           = 0x0100;
    ctx->HidDescriptor.bCountry         = 0x00;
    ctx->HidDescriptor.bNumDescriptors  = 0x01;
    ctx->HidDescriptor.DescriptorList[0].bReportType   = 0x22;
    ctx->HidDeviceAttributes.Size          = sizeof(HID_DEVICE_ATTRIBUTES);

    if (!ReadConfigFromRegistry(ctx))
        return STATUS_DEVICE_CONFIGURATION_ERROR;

    /* Without the SDK's InputReportId (an SDK from before the value), find
     * the first Report ID from the FIRST Application Collection only, the
     * report that SDK measured InputReportByteLength on. For
     * dual-collection descriptors, Col2 may have a Report ID that we must
     * NOT use for Col1's reports. Stop scanning at the first End
     * Collection that closes the top-level Application Collection. */
    if (!ctx->InputReportIdConfigured && ctx->ReportDescriptorSize >= 2) {
        int colDepth = 0;
        BOOLEAN inFirstCollection = FALSE;
        ULONG ri = 0;
        ULONG end = ctx->ReportDescriptorSize - 1; /* keep room for [ri+1] read */
        while (ri < end) {
            UCHAR prefix = ctx->ReportDescriptor[ri];
            int bSize = prefix & 0x03;
            if (bSize == 3) bSize = 4;
            int bType = (prefix >> 2) & 0x03;
            int bTag = (prefix >> 4) & 0x0F;
            if (bType == 0 && bTag == 10) { /* Collection */
                colDepth++;
                if (colDepth == 1) inFirstCollection = TRUE;
            }
            if (bType == 0 && bTag == 12) { /* End Collection */
                colDepth--;
                if (colDepth == 0 && inFirstCollection) break;
            }
            if (inFirstCollection && prefix == 0x85 && (ri == 0 || ctx->ReportDescriptor[ri-1] != 0x09)) {
                ctx->FirstInputReportId = ctx->ReportDescriptor[ri + 1];
                break;
            }
            /* Advance past prefix + value bytes; clamp to end on overflow */
            ULONG step = (ULONG)(1 + bSize);
            if (ri + step <= ri) break; /* overflow guard */
            ri += step;
        }
    }

    /* v2.0.0: each input report's declared length, for the extended lane's
     * bound. */
    BuildInputLengths(ctx);
    BuildPidCandidates(ctx);

    /* v2.0.0: this driver's half of the version section at add, then the
     * controller's v2.0.0 values under the contract. A value it cannot
     * read fails the start, named in DriverConfigError first, so the
     * create names it rather than leaving a device that answers nothing. */
    AnnounceVersion(ctx);
    if (ctx->Contract) {
        LONG error = LoadContractValues(ctx);
        if (error != 0) {
            WriteConfigError(ctx, error);
            return STATUS_DEVICE_CONFIGURATION_ERROR;
        }
    }

    /*
     * Set BusReportedDeviceDesc so joy.cpl shows the profile name, when
     * the profile has one.
     * DEVPKEY_Device_BusReportedDeviceDesc = {540b947e-8b40-45bc-a8a2-6a0b894cbda2}, 4
     */
    if (ctx->ProductStringBytes > 0) {
        static const DEVPROPKEY busDescKey = {
            { 0x540b947e, 0x8b40, 0x45bc, { 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2 } },
            4
        };
        WDF_DEVICE_PROPERTY_DATA propData;
        WDF_DEVICE_PROPERTY_DATA_INIT(&propData, &busDescKey);
        propData.Lcid = LOCALE_NEUTRAL;
        WdfDeviceAssignProperty(device, &propData, DEVPROP_TYPE_STRING,
            ctx->ProductStringBytes, ctx->ProductString);
    }

    /* XUSB interface is NEVER registered on the main device.
     * The XUSB companion (HMXInput.dll) handles all XInput IOCTLs. If we
     * registered XUSB here too, mshidumdf would corrupt the IOCTL path and
     * xinput1_4 would talk to the wrong device. */

    /* WinExInput is NEVER registered on the main HID device.
     *
     * Registering it here caused duplicate entries in the browser Gamepad
     * API: plain HID virtuals (DualSense, Stadia, custom profiles) showed up
     * once as a WGI "standard gamepad" (via this WinExInput registration)
     * AND once as a raw HID device (via RawInput). See issue #6.
     *
     * Xbox profiles with WGI detection needs: the XUSB companion
     * (HMXInput.dll) registers WinExInput with the same XI_00 reference
     * string. WGI fires GamepadAdded for the companion; the main HID is
     * seen only via RawInput by apps that want it. No duplicate.
     *
     * Plain HID virtuals (non-Xbox): browsers detect them via RawInput
     * directly. No WGI path is required or desired. Chrome applies its
     * standard-gamepad mapping heuristic based on VID:PID and descriptor
     * shape, not on WinExInput presence. */

    /* Create locks */
    status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &ctx->InputLock);
    if (!NT_SUCCESS(status)) return status;
    status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &ctx->OutputLock);
    if (!NT_SUCCESS(status)) return status;
    status = WdfWaitLockCreate(WDF_NO_OBJECT_ATTRIBUTES, &ctx->FeatureLock);
    if (!NT_SUCCESS(status)) return status;

    /* Default queue (parallel): HID IOCTLs from MsHidUmdf */
    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = EvtIoDeviceControl;

    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES,
                              &ctx->DefaultQueue);
    if (!NT_SUCCESS(status)) return status;

    /* Manual queue for pending HID_READ_REPORT */
    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES,
                              &ctx->ManualQueue);
    if (!NT_SUCCESS(status)) return status;

    /* Shared memory section for data injection (bypasses upper filter drivers) */
    ctx->SharedMemHandle = NULL;
    ctx->SharedMemPtr = NULL;
    ctx->SharedMemSeqNo = 0;
    ctx->SeenSeqNo = 0;

    /* Event-driven shared-input worker. The SDK creates
     * Global\HIDMaestroInputEvent<N> alongside the section and SetEvents
     * it per frame; we OpenEvent lazily in the worker (it may not exist
     * yet at EvtDeviceAdd time). StopEvent is our sentinel for shutdown.
     * Replaces the old 1 ms WdfTimer busy poll: see commit/diff for the
     * CPU-saturation root cause. */
    ctx->InputDataEvent = NULL;
    /* Nothing an earlier device on this instance recorded about its owner
     * watch describes this one (v2.0.0, RecordOwnerConnected). */
    ForgetOwnerWatch(ctx);
    /* Create a NAMED StopEvent so external cleanup code (SDK's
     * RemoveAllVirtualControllers) can signal it after a force-kill,
     * breaking the deadlock where PnP waits for WUDFHost to release
     * and WUDFHost waits for our worker thread to exit. Without this,
     * cleanup of force-killed controllers takes ~28s (kernel query-
     * remove timeout per device). With it, cleanup signals the named
     * event, the worker exits, WUDFHost releases, and PnP removes
     * the device instantly.
     *
     * CreateHostEvent admits the elevated SDK and the driver hosts only.
     * The StopEvent passes takeOverNullDacl=FALSE: an event a program that
     * is not a driver host made first, or one a driver before v2.0.0 made
     * with a NULL DACL, is refused, and the worker gets an unnamed event
     * instead. It still stops at teardown, and a sweep after a force-kill
     * takes the slow removal path for this device. Taking over the old
     * NULL-DACL StopEvent would hand a program holding it from before the
     * takeover the means to signal the worker (its handle keeps WRITE_DAC),
     * so that object is never reused. */
    {
        ctx->StopEvent = CreateHostEvent(TRUE /* manual reset */, FALSE, ctx->StopEventName);
        if (ctx->StopEvent == NULL)
            ctx->StopEvent = CreateEventW(NULL, TRUE /* manual reset */, FALSE, NULL);

        /* Output-ring doorbell (issue #34). Auto-reset, the same
         * descriptor. Created here (not SDK-side like the input event) so
         * the SDK can DETECT support by OpenEvent success: open works on
         * a new driver (block on the event, zero idle wakes), fails on an
         * old driver (fall back to the 8 ms poll). The companion creates
         * or opens the same name for its XUSB rumble publishes; CreateEventW
         * on an existing name returns the existing object, so creation
         * order between the two hosts doesn't matter. Non-fatal on
         * failure, an unusable name included: PublishOutput skips the
         * signal and the SDK's safety timeout still drains the ring. */
        ctx->OutputSignalEvent = CreateHostEvent(FALSE /* auto reset */, TRUE /* take over a NULL-DACL doorbell */, ctx->OutputEventName);
    }
    if (ctx->StopEvent != NULL) {
        /* CRITICAL: reset the StopEvent explicitly before starting the worker.
         * Windows' CreateEventW, when called on an existing named event, IGNORES
         * the initialState argument and returns a handle to the existing object
         * in whatever signal state it's in. On live-swap (teardown old context
         * + create new context on the same ControllerIndex), the OLD context's
         * ReleaseIndexObjects signaled this event (manual-reset → stays
         * signaled) to wake its worker. If any process still holds a handle to
         * the event when the new context runs: the SDK's
         * RemoveAllVirtualControllers utility keeps a handle briefly, and the
         * kernel object survives as long as any ref exists: then our
         * CreateEventW above hands us that still-signaled event. The worker
         * immediately sees WAIT_OBJECT_0 on StopEvent and returns 0: HID input
         * path dead for the rest of this session. (HIDMAESTRO still runs its
         * own path, so XUSB / Guide still works: which is the exact partial-
         * hang symptom: only Guide flashes after a live-swap on Xbox 360.) */
        ResetEvent(ctx->StopEvent);
        ctx->WorkerThread = CreateThread(NULL, 0, SharedInputWorkerProc, ctx, 0, NULL);
    }

    return STATUS_SUCCESS;
}

/* ================================================================== */
/*  EvtIoDeviceControl                                                 */
/* ================================================================== */

VOID
EvtIoDeviceControl(
    _In_ WDFQUEUE   Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t     OutputBufferLength,
    _In_ size_t     InputBufferLength,
    _In_ ULONG      IoControlCode)
{
    NTSTATUS        status = STATUS_NOT_IMPLEMENTED;
    BOOLEAN         completeRequest = TRUE;
    PDEVICE_CONTEXT ctx = GetDeviceContext(WdfIoQueueGetDevice(Queue));

    UNREFERENCED_PARAMETER(OutputBufferLength);
    UNREFERENCED_PARAMETER(InputBufferLength);

    switch (IoControlCode) {

    case IOCTL_HID_GET_DEVICE_DESCRIPTOR:
        status = RequestCopyFromBuffer(Request,
            &ctx->HidDescriptor, ctx->HidDescriptor.bLength);
        break;

    case IOCTL_HID_GET_REPORT_DESCRIPTOR:
        status = RequestCopyFromBuffer(Request,
            ctx->ReportDescriptor, ctx->ReportDescriptorSize);
        break;

    case IOCTL_HID_GET_DEVICE_ATTRIBUTES:
        status = RequestCopyFromBuffer(Request,
            &ctx->HidDeviceAttributes, sizeof(HID_DEVICE_ATTRIBUTES));
        break;

    case IOCTL_HID_GET_STRING: {
        /*
         * The input buffer's low 16 bits identify which device-level string
         * the HID class wants. The values aren't the documented HID_STRING_ID_*
         * constants from the WDK headers (1/2/3): under MsHidUmdf the HID
         * class actually sends 14/15/16 for manufacturer/product/serial. Both
         * the constant-form and the actual-observed-form are accepted in case
         * the mapping changes between Windows versions.
         *
         * For SERIAL (16 or 3) we return a UNIQUE per-instance serial built
         * from ControllerIndex. Without this, two virtual controllers that
         * share VID/PID/ProductString (e.g. 2× DualSense) get bucketed as
         * one device by SDL3/HIDAPI's hid_enumerate, which uses the serial
         * string as the disambiguator. PadForge has the same problem.
         *
         * For all other string IDs we return the product string: that's
         * what joy.cpl and games display.
         */
        PVOID  inBuf = NULL;
        size_t inBufSize = 0;
        ULONG  stringId = 0;

        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, sizeof(ULONG), &inBuf, &inBufSize))) {
            stringId = *(ULONG*)inBuf & 0xFFFF;
        }

        BOOLEAN isSerial = (stringId == 16 || stringId == 3 /* HID_STRING_ID_ISERIALNUMBER */);
        if (isSerial && ctx->SerialStringBytes > 0) {
            status = RequestCopyFromBuffer(Request,
                ctx->SerialString, ctx->SerialStringBytes);
        } else {
            status = RequestCopyFromBuffer(Request,
                ctx->ProductString, ctx->ProductStringBytes);
        }
        break;
    }

    case IOCTL_HID_GET_INDEXED_STRING: {
        /* IOCTL_HID_GET_INDEXED_STRING is for raw HID descriptor string
         * indices (the iManufacturer/iProduct/iSerialNumber fields in the
         * HID device descriptor). Our descriptor doesn't declare any string
         * indices, so this path is rarely hit; HidClass routes the named
         * string queries through IOCTL_HID_GET_STRING instead, where we
         * handle the per-instance serial. Fall back to ProductString. */
        status = RequestCopyFromBuffer(Request,
            ctx->ProductString, ctx->ProductStringBytes);
        break;
    }

    case IOCTL_HID_READ_REPORT: {
        /*
         * HID class wants an input report.
         *
         * Critical: only complete IMMEDIATELY when the cached report is
         * NEWER than the last one we delivered. Otherwise pend in ManualQueue
         * and let ProcessSharedInput drain it when the SDK next signals.
         *
         * Without this seqno gate, every READ_REPORT completes instantly
         * with stale cached data, HIDClass immediately re-issues, and we
         * burn a core per device hammering the kernel↔user mode bridge.
         * GET_INPUT_REPORT (a different IOCTL, polled diagnostic path) is
         * unaffected: it still reads the cache directly.
         */
        WdfWaitLockAcquire(ctx->InputLock, NULL);

        /* HID class asks again only after its completion routine has put
         * the last completed frame in its buffer: acknowledge that frame
         * now, so the SDK's next one cannot overtake it (issue #67). */
        if (ctx->AckPending) {
            ctx->AckPending = FALSE;
            AckInputDeliveredLocked(ctx, ctx->AckPendingSeqNo);
        }

        /* Release frames the consumer's exit left queued (issue #67) go
         * first, one per read. */
        if (ctx->ReleaseNext < ctx->ReleaseCount) {
            ULONG i = ctx->ReleaseNext++;
            status = RequestCopyFromBuffer(Request, ctx->ReleaseFrames[i], ctx->ReleaseSizes[i]);
            WdfWaitLockRelease(ctx->InputLock);
            break;
        }

        if (ctx->InputReportReady && ctx->SharedMemSeqNo > ctx->LastDeliveredInputSeqNo) {
            status = RequestCopyFromBuffer(Request,
                ctx->InputReport, ctx->InputReportSize);
            ctx->LastDeliveredInputSeqNo = ctx->SharedMemSeqNo;
            NoteFrameCompletedLocked(ctx, ctx->SharedMemSeqNo);
            WdfWaitLockRelease(ctx->InputLock);
        } else {
            WdfWaitLockRelease(ctx->InputLock);
            status = WdfRequestForwardToIoQueue(Request, ctx->ManualQueue);
            if (NT_SUCCESS(status)) {
                completeRequest = FALSE;
            }
        }
        break;
    }

    case IOCTL_HID_WRITE_REPORT: {
        /*
         * HID write path: used by HIDAPI / SDL3 / WriteFile to send output
         * reports (DualSense report 0x02 haptics+triggers+LED, generic LED
         * control, etc). The first byte is the HID Report ID (0 if the
         * descriptor uses no IDs). Forward to the output shared section so
         * the consumer (PadForge) can deliver it to real hardware.
         */
        PVOID  wrBuf;
        size_t wrSize;
        status = WdfRequestRetrieveInputBuffer(Request, 1, &wrBuf, &wrSize);
        if (!NT_SUCCESS(status)) break;

        {
            const UCHAR *p = (const UCHAR *)wrBuf;
            UCHAR  reportId = (wrSize > 0) ? p[0] : 0;
            const UCHAR *payload = (wrSize > 0) ? p + 1 : p;
            ULONG payloadLen = (ULONG)((wrSize > 0) ? wrSize - 1 : 0);

            PublishOutput(ctx, HIDMAESTRO_OUTPUT_SOURCE_HID_OUTPUT,
                          reportId, payload, payloadLen);
        }
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_UMDF_HID_SET_FEATURE: {
        /*
         * HidD_SetFeature path. DualSense and DualShock 4 use feature reports
         * for some configuration writes; some HID stacks route data here.
         * Forward to the output shared section tagged as a feature report so
         * the consumer can distinguish from regular output reports.
         *
         * v1.1.37: PID FFB Create New Effect (0x11) and Block Free (0x1F)
         * are handled SYNCHRONOUSLY inside this IOCTL handler, before
         * forwarding to the consumer. Mirrors vJoy's `Ffb_ProcessPacket`
         * for `HID_ID_NEWEFREP+0x10`: kernel/driver allocates the EBI in
         * the same address space as the device-extension state so dinput8's
         * follow-up GetFeature(0x12) reads consistent state without
         * crossing the WUDFHost ↔ consumer process boundary. Issue #16.
         */
        PVOID  featureBuf;
        size_t featureSize;

        status = WdfRequestRetrieveInputBuffer(Request, 1, &featureBuf, &featureSize);
        if (!NT_SUCCESS(status)) break;

        {
            const UCHAR *p = (const UCHAR *)featureBuf;
            UCHAR  reportId = (featureSize > 0) ? p[0] : 0;
            const UCHAR *payload = (featureSize > 0) ? p + 1 : p;
            ULONG payloadLen = (ULONG)((featureSize > 0) ? featureSize - 1 : 0);

            /* v2.0.0: the data a message entry of FeatureAnswers follows. */
            KeepFeatureSet(ctx, p, (ULONG)featureSize);

            /* PID FFB report routing inside SetFeature. v1.1.39 covers
             * all three Set-direction PID handshake reports here because
             * the canonical PID descriptor declares 0x11/0x1B/0x1C as
             * BOTH Feature and Output direction: pid.dll/dinput8 may
             * route via either HidD_SetFeature OR HidD_SetOutputReport
             * depending on transport-mode global. The same handlers
             * exist in IOCTL_UMDF_HID_SET_OUTPUT_REPORT below; whichever
             * IOCTL the framework delivers, the driver acts the same.
             *
             * v1.3.7: Report IDs are profile-specific. SDK writes the
             * descriptor-derived overrides into the shared section so
             * non-canonical PID layouts (Microsoft SideWinder uses
             * Set Effect=0x01, Block Free=0x0B, Device Control=0x0C)
             * route through the same handlers as the canonical
             * 0x11/0x1B/0x1C builder-emitted layouts. We MUST open the
             * shared section BEFORE reading the RID overrides: on
             * first IOCTL the section is unmapped and an unconditional
             * read would fall back to canonical IDs and miss
             * non-canonical RIDs entirely. EnsurePidStateMapping is
             * idempotent and ~free after the first successful map.
             * v2.0.0: only an ID a PID report can arrive on opens it
             * (BuildPidCandidates). Any other matches no ID below. */
            BOOLEAN haveMap = ctx->PidCandidate[reportId] && EnsurePidStateMapping(ctx);
            UCHAR createNewEffectRid = HIDMAESTRO_PID_CREATE_NEW_EFFECT_REPORT_ID;
            UCHAR blockFreeRid       = HIDMAESTRO_PID_BLOCK_FREE_REPORT_ID;
            UCHAR deviceControlRid   = HIDMAESTRO_PID_DEVICE_CONTROL_REPORT_ID;
            if (haveMap) {
                volatile HIDMAESTRO_SHARED_PID_STATE *sec =
                    (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
                if (sec->CreateNewEffectReportId) createNewEffectRid = sec->CreateNewEffectReportId;
                if (sec->BlockFreeReportId)       blockFreeRid       = sec->BlockFreeReportId;
                if (sec->DeviceControlReportId)   deviceControlRid   = sec->DeviceControlReportId;
            }
            if (reportId == createNewEffectRid && haveMap)
            {
                AllocateEbiInBlockLoad(ctx);
            }
            else if (reportId == blockFreeRid
                     && payloadLen >= 1
                     && haveMap)
            {
                FreeEbi(ctx, payload[0]);
            }
            else if (reportId == deviceControlRid
                     && payloadLen >= 1
                     && haveMap
                     && payload[0] == 4 /* CTRL_DEVRST */)
            {
                ResetPidState(ctx);
            }

            PublishOutput(ctx, HIDMAESTRO_OUTPUT_SOURCE_HID_FEATURE,
                          reportId, payload, payloadLen);
        }
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_UMDF_HID_GET_FEATURE: {
        /*
         * HidD_GetFeature path. DirectInput's PID FFB handshake reads
         * Block Load (0x12), PID Pool (0x13), and PID State (0x14) via
         * this IOCTL during dinput8!CDIEffect::CreateEffect. The SDK
         * consumer (e.g. PadForge) publishes the current values to the
         * shared PidState section via HMController.PublishPid*. We read
         * them via seqlock and pack into the IOCTL output buffer.
         *
         * Architectural model mirrors vJoy: device-extension state lives
         * on the user side of the kernel-user boundary because HIDMaestro
         * is UMDF2 (driver runs in WUDFHost user-mode). Synchronous read
         * from shared memory; no IPC round-trip, no timeout, no responder
         * thread.
         *
         * Backward compat: if no SDK has published (PidEnabled == 0) or
         * the section doesn't exist (consumer doesn't use FFB), Pool
         * returns STATUS_NO_SUCH_DEVICE and Block Load / State return
         * STATUS_NOT_SUPPORTED: matching vJoy's "FFB not enabled"
         * convention and HIDMaestro's pre-v1.1.35 behavior of
         * STATUS_NOT_SUPPORTED across all GetFeature calls.
         *
         * Block Load status: when BL_LoadStatus is 0 (unpublished, or
         * cleared by a DISFFC_RESET / Device Control reset), GetFeature
         * (0x12) reports LoadStatus = Error(3) in a valid 5-byte report
         * and returns STATUS_SUCCESS (see the Block Load wire-format block
         * below). v1.1.37 driver-side EBI allocation populates the BL
         * fields synchronously inside the SetFeature(0x11) handler, so a
         * real Create New Effect leaves LoadStatus at Success(1)/Full(2);
         * the Error clamp only surfaces in the brief window before any
         * Create New Effect arrives, or immediately after a reset.
         *
         * GetFeature(0x11 Create New Effect) is bidirectional in the
         * canonical PID descriptor. v1.1.37 mirrors vJoy: returns
         * STATUS_SUCCESS with the buffer untouched (vJoy's vJoyGetFeature
         * has no case for HID_ID_NEWEFREP and falls through with the
         * default STATUS_SUCCESS init). HIDMaestro pre-1.1.37 returned
         * STATUS_NOT_SUPPORTED, a divergence from vJoy that may have
         * contributed to issue #16.
         */
        PVOID  outBuf;
        size_t outSize;
        PVOID  inBuf;
        size_t inSize;

        status = WdfRequestRetrieveOutputBuffer(Request, 1, &outBuf, &outSize);
        if (!NT_SUCCESS(status)) break;

        if (outSize < 1) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        /* UMDF2 HID convention: the requested Report ID lives in the
         * IRP's INPUT buffer (byte 0); the OUTPUT buffer is what the
         * driver fills with the response. Pre-v1.3.5 read RID from
         * outBuf[0], which was always 0 because HidClass zeros the
         * output buffer before dispatch. The PID Block Load / Pool /
         * State paths happened to work despite the bug because dinput8
         * only reaches them via the SetFeature path (which does read
         * the input buffer); Get_Feature for a real Sony BT handshake
         * (0x05 / 0x09 / 0x20) was unreachable until this fix. */
        UCHAR reportId = 0;
        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, 1, &inBuf, &inSize))
            && inSize >= 1)
        {
            reportId = ((const UCHAR *)inBuf)[0];
        }
        if (reportId == 0) {
            /* Fallback for callers that put the RID in the output buffer
             * (some PID consumers). Keeps the pre-v1.3.5 PID dispatch
             * path working. */
            reportId = ((UCHAR *)outBuf)[0];
        }

        /* v2.0.0: LiveFeatureReport, the feature report whose answer is the
         * device's state: the latest the SDK wrote on the data lane, as
         * far as the caller's buffer holds it. On a descriptor without
         * report IDs the HID class hands every GET here as report 0,
         * whatever ID the caller put in its buffer. */
        if (ctx->LiveFeatureReport != 0
            && (!ctx->UsesReportIds || reportId == (UCHAR)(ctx->LiveFeatureReport - 1))) {
            ULONG n;
            WdfWaitLockAcquire(ctx->InputLock, NULL);
            n = ctx->LiveFeatureSize < (ULONG)outSize ? ctx->LiveFeatureSize : (ULONG)outSize;
            RtlCopyMemory(outBuf, ctx->LiveFeature, n);
            WdfWaitLockRelease(ctx->InputLock);
            WdfRequestSetInformation(Request, n);
            status = STATUS_SUCCESS;
            break;
        }

        /* v2.0.0: FeatureAnswers, the controller's answers the SDK compiled
         * (the design note's mechanism 4), parsed at add under the version
         * contract. A GET no entry answers goes on to the PID block and
         * then fails with STATUS_NOT_SUPPORTED, publishing nothing. On a
         * descriptor without report IDs every GET is report 0's. */
        {
            ULONG written;
            BOOLEAN notify;
            NTSTATUS answered;
            if (ctx->FeatureCount > 0
                && ServeFeature(ctx, reportId, (UCHAR *)outBuf, (ULONG)outSize, &written, &notify, &answered)) {
                if (NT_SUCCESS(answered)) {
                    WdfRequestSetInformation(Request, written);
                    if (notify)
                        PublishOutput(ctx, HIDMAESTRO_OUTPUT_SOURCE_HID_FEATURE_READ, reportId, NULL, 0);
                }
                status = answered;
                break;
            }
        }

        /* v1.3.7: descriptor-driven PID Report ID overrides for
         * non-canonical PID layouts (Microsoft SideWinder etc.). Open
         * the shared section first so a fresh-IOCTL read on a profile
         * with non-canonical IDs picks up the SDK-published overrides
         * instead of falling back to canonical and missing the RID
         * entirely. EnsurePidStateMapping is idempotent.
         * v2.0.0: an ID no PID report can arrive on (BuildPidCandidates)
         * matches none of the IDs below, mapped or not, and fails as it
         * would below, without the section's open. */
        if (!ctx->PidCandidate[reportId]) {
            status = STATUS_NOT_SUPPORTED;
            break;
        }
        (void)EnsurePidStateMapping(ctx);
        UCHAR createNewEffectRid = HIDMAESTRO_PID_CREATE_NEW_EFFECT_REPORT_ID;
        UCHAR poolRid            = HIDMAESTRO_PID_POOL_REPORT_ID;
        UCHAR stateRid           = HIDMAESTRO_PID_STATE_REPORT_ID;
        UCHAR blockLoadRid       = HIDMAESTRO_PID_BLOCK_LOAD_REPORT_ID;
        if (ctx->PidStateMemPtr != NULL) {
            volatile HIDMAESTRO_SHARED_PID_STATE *sec =
                (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
            if (sec->CreateNewEffectReportId) createNewEffectRid = sec->CreateNewEffectReportId;
            if (sec->PoolReportId)            poolRid            = sec->PoolReportId;
            if (sec->StateReportId)           stateRid           = sec->StateReportId;
            if (sec->BlockLoadReportId)       blockLoadRid       = sec->BlockLoadReportId;
        }

        /* GetFeature(Create New Effect): silent success (mirrors vJoy).
         * Buffer left untouched. Doesn't gate on PidEnabled so this works
         * even for non-FFB consumers, matching vJoy's "always SUCCESS for
         * unhandled report IDs" fallthrough behavior. */
        if (reportId == createNewEffectRid) {
            status = STATUS_SUCCESS;
            break;
        }

        HIDMAESTRO_SHARED_PID_STATE pid = {0};
        BOOLEAN haveState = ReadPidState(ctx, &pid);

        if (!haveState || !pid.PidEnabled) {
            /* No SDK consumer publishing FFB state. Pool Report MUST
             * return STATUS_NO_SUCH_DEVICE so DInput definitively
             * concludes "device exists but no FFB" rather than
             * retrying. Other report IDs return NOT_SUPPORTED. */
            status = (reportId == poolRid)
                   ? STATUS_NO_SUCH_DEVICE
                   : STATUS_NOT_SUPPORTED;
            break;
        }

        UCHAR *p = (UCHAR *)outBuf;

        if (reportId == blockLoadRid) {
            /* Wire format: [reportId, EBI, LoadStatus, RAMPool LSB, RAMPool MSB].
             * If LoadStatus is zero (unpublished), report Error=3 per HID PID
             * spec §5.5 (LoadStatus enum: 1=Success, 2=Full, 3=Error). vJoy
             * uses Error=1 in its raw byte from a different historical
             * layout; dinput accepts both. */
            if (outSize < 5) { status = STATUS_BUFFER_TOO_SMALL; break; }
            p[0] = reportId;
            p[1] = pid.BL_EffectBlockIndex;
            p[2] = (pid.BL_LoadStatus >= 1 && pid.BL_LoadStatus <= 3)
                 ? pid.BL_LoadStatus : (UCHAR)3 /* Error */;
            p[3] = (UCHAR)(pid.BL_RAMPoolAvailable & 0xFF);
            p[4] = (UCHAR)((pid.BL_RAMPoolAvailable >> 8) & 0xFF);
            WdfRequestSetInformation(Request, 5);
            status = STATUS_SUCCESS;
        }
        else if (reportId == poolRid) {
            /* Wire format: [reportId, RAMPool LSB, RAMPool MSB, MaxSim, MemMgmt] */
            if (outSize < 5) { status = STATUS_BUFFER_TOO_SMALL; break; }
            p[0] = reportId;
            p[1] = (UCHAR)(pid.Pool_RAMPoolSize & 0xFF);
            p[2] = (UCHAR)((pid.Pool_RAMPoolSize >> 8) & 0xFF);
            p[3] = pid.Pool_MaxSimultaneousEffects;
            p[4] = pid.Pool_MemoryManagement;
            WdfRequestSetInformation(Request, 5);
            status = STATUS_SUCCESS;
        }
        else if (reportId == stateRid) {
            /* Wire format: [reportId, EBI, StateFlags] */
            if (outSize < 3) { status = STATUS_BUFFER_TOO_SMALL; break; }
            p[0] = reportId;
            p[1] = pid.State_EffectBlockIndex;
            p[2] = pid.State_Flags;
            WdfRequestSetInformation(Request, 3);
            status = STATUS_SUCCESS;
        }
        else {
            status = STATUS_NOT_SUPPORTED;
        }
        break;
    }

    case IOCTL_UMDF_HID_SET_OUTPUT_REPORT: {
        /*
         * The HID class delivers HidD_SetOutputReport here as a HID_XFER_PACKET.
         * dinput8 also routes generated PID effect output reports through this
         * IOCTL when a game calls IDirectInputEffect::Start. Forward the bytes
         * to the output shared section.
         *
         * UMDF2 input buffer layout for HID_XFER_PACKET-style IOCTLs is just
         * the raw report bytes (Report ID byte first if descriptor uses IDs).
         *
         * v1.1.38: handle PID Block Free (0x1B) and Device Control (0x1C)
         * here. Block Free is Output direction in the canonical PID
         * descriptor (vJoy-Brunner/driver/sys/hidReportDescSingle.h:558,
         * `0x91, 0x02` items). Device Control is Output direction
         * (hidReportDescSingle.h:571) and on Control=4 (CTRL_DEVRST) we
         * reset PID state: mirrors vJoy hid.c:2849.
         *
         * Report ID 0x11 is intentionally NOT handled here. 0x11 is
         * dual-purpose in the PID descriptor: Feature direction = Create
         * New Effect (allocates EBI; handled in SetFeature), Output
         * direction = Set Effect (selects an EXISTING EBI to start; no
         * allocation). v1.1.37 mistakenly allocated an EBI for both,
         * leaking one EBI per Set Effect. Fixed in v1.1.38.
         */
        PVOID  outBuf;
        size_t outBufSize;

        status = WdfRequestRetrieveInputBuffer(Request, 1, &outBuf, &outBufSize);
        if (!NT_SUCCESS(status)) break;

        {
            const UCHAR *p = (const UCHAR *)outBuf;
            UCHAR  reportId = (outBufSize > 0) ? p[0] : 0;
            const UCHAR *payload = (outBufSize > 0) ? p + 1 : p;
            ULONG payloadLen = (ULONG)((outBufSize > 0) ? outBufSize - 1 : 0);

            /* Only an ID a PID report can arrive on opens the PID section
             * (BuildPidCandidates). Any other matches no ID below. */
            BOOLEAN haveOutMap = ctx->PidCandidate[reportId] && EnsurePidStateMapping(ctx);
            UCHAR blockFreeRid     = HIDMAESTRO_PID_BLOCK_FREE_REPORT_ID;
            UCHAR deviceControlRid = HIDMAESTRO_PID_DEVICE_CONTROL_REPORT_ID;
            if (haveOutMap) {
                volatile HIDMAESTRO_SHARED_PID_STATE *sec =
                    (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
                if (sec->BlockFreeReportId)     blockFreeRid     = sec->BlockFreeReportId;
                if (sec->DeviceControlReportId) deviceControlRid = sec->DeviceControlReportId;
            }
            if (reportId == blockFreeRid
                && payloadLen >= 1
                && haveOutMap)
            {
                FreeEbi(ctx, payload[0]);
            }
            else if (reportId == deviceControlRid
                     && payloadLen >= 1
                     && haveOutMap
                     && payload[0] == 4 /* CTRL_DEVRST */)
            {
                ResetPidState(ctx);
            }

            PublishOutput(ctx, HIDMAESTRO_OUTPUT_SOURCE_HID_OUTPUT,
                          reportId, payload, payloadLen);
        }
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_UMDF_HID_GET_INPUT_REPORT: {
        /*
         * v1.1.38: vJoy returns STATUS_NOT_SUPPORTED for this IOCTL
         * unconditionally (vJoy-Brunner/driver/sys/hid.c:244). HIDMaestro
         * pre-1.1.38 returned 17 zeroed bytes via a default-size fallback,
         * which dinput8 would parse against descriptor-declared report
         * lengths (e.g. PID State at 3 bytes) and AV when the lengths
         * disagreed. Strong AV candidate per the v1.1.38 audit.
         *
         * Fix: if the caller asks for a specific report ID we know about
         * (PID State 0x14), return correctly-sized bytes from shared
         * state. For everything else, return STATUS_NOT_SUPPORTED to
         * mirror vJoy. dinput8 falls back to other paths (or accepts
         * the device as unread-state) on NOT_SUPPORTED.
         */
        PVOID  inBuf;
        size_t inBufSize;

        status = WdfRequestRetrieveOutputBuffer(Request, 1, &inBuf, &inBufSize);
        if (!NT_SUCCESS(status)) break;

        if (inBufSize < 1) {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        UCHAR inReportId = ((UCHAR *)inBuf)[0];

        /* PID State Report (Input direction in the canonical descriptor
         * vJoy hidReportDescSingle.h:752 declares 0x14 with embedded
         * Input items inside a Feature collection). dinput8 may issue
         * HidD_GetInputReport(StateRid) during CreateEffect to read State.
         * v1.3.7: match against SDK-published State RID with canonical
         * fallback so non-canonical PID layouts (SideWinder etc.) resolve.
         * Open shared section first; otherwise the very first IOCTL read
         * before any other handler ran would miss the override.
         * v2.0.0: an ID no PID report can arrive on goes straight to the
         * input cache below (BuildPidCandidates). */
        if (ctx->PidCandidate[inReportId])
            (void)EnsurePidStateMapping(ctx);
        UCHAR stateRid = HIDMAESTRO_PID_STATE_REPORT_ID;
        if (ctx->PidCandidate[inReportId] && ctx->PidStateMemPtr != NULL) {
            volatile HIDMAESTRO_SHARED_PID_STATE *sec =
                (volatile HIDMAESTRO_SHARED_PID_STATE *)ctx->PidStateMemPtr;
            if (sec->StateReportId) stateRid = sec->StateReportId;
        }
        if (inReportId == stateRid) {
            HIDMAESTRO_SHARED_PID_STATE pid = {0};
            BOOLEAN haveState = ReadPidState(ctx, &pid);
            if (inBufSize < 3) { status = STATUS_BUFFER_TOO_SMALL; break; }
            ((UCHAR *)inBuf)[0] = inReportId;
            ((UCHAR *)inBuf)[1] = haveState ? pid.State_EffectBlockIndex : 0;
            ((UCHAR *)inBuf)[2] = haveState ? pid.State_Flags : 0;
            WdfRequestSetInformation(Request, 3);
            status = STATUS_SUCCESS;
            break;
        }

        /* Standard input report (Report ID 1 or no-RID descriptors): return
         * the latest cached input frame the SDK published. v2.0.0: with a
         * cached-ID list, the latest frame of a listed report. */
        WdfWaitLockAcquire(ctx->InputLock, NULL);
        {
            BOOLEAN listed = ctx->CachedInputIdCount > 0;
            BOOLEAN ready = listed ? ctx->PolledReportReady : ctx->InputReportReady;
            if (ready && (inReportId == 0x01 || inReportId == 0x00))
            {
                status = RequestCopyFromBuffer(Request,
                    listed ? ctx->PolledReport : ctx->InputReport,
                    listed ? ctx->PolledReportSize : ctx->InputReportSize);
                WdfWaitLockRelease(ctx->InputLock);
                break;
            }
        }
        WdfWaitLockRelease(ctx->InputLock);

        /* Anything else: vJoy parity: STATUS_NOT_SUPPORTED. */
        status = STATUS_NOT_SUPPORTED;
        break;
    }

    case IOCTL_HID_ACTIVATE_DEVICE:
    case IOCTL_HID_DEACTIVATE_DEVICE:
    case IOCTL_HID_SEND_IDLE_NOTIFICATION_REQUEST:
        status = STATUS_SUCCESS;
        break;

    /* XUSB IOCTLs (IOCTL_XUSB_GET_INFORMATION/GET_CAPABILITIES/GET_STATE/
     * SET_STATE/GET_LED_STATE/GET_BATTERY_INFO/POWER_INFO) used to be
     * handled here. Removed in v1.3.4: the main HID device never
     * registers the XUSB interface (see WdfDeviceCreateDeviceInterface
     * comment further up); xinput1_4 talks exclusively to the XUSB
     * companion (HMXInput.dll), which has its own handlers in
     * companion.c. The handlers here were unreachable. */

    default:
        status = STATUS_NOT_IMPLEMENTED;
        break;
    }

    if (completeRequest) {
        WdfRequestComplete(Request, status);
    }
}
