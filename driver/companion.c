/*
 * HIDMaestro Companion: UMDF2 driver for XUSB XInput + WinExInput.
 * Registers XUSB and WinExInput device interfaces.
 * Reads gamepad state from a per-instance pagefile-backed shared section
 * (Global\HIDMaestroInput<N>): RAM-only, no disk I/O.
 * No HID, no filter mode: runs as System-class function driver.
 */

#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <wdf.h>
#include "hm_contract.h"
#include <sddl.h>     /* ConvertStringSecurityDescriptorToSecurityDescriptorW */
#include <aclapi.h>   /* GetSecurityInfo */

DRIVER_INITIALIZE DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD CompanionDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL CompanionIoControl;
EVT_WDF_OBJECT_CONTEXT_CLEANUP CompanionDeviceCleanup;
EVT_WDF_DEVICE_SELF_MANAGED_IO_FLUSH CompanionSelfManagedIoFlush;
EVT_WDF_TIMER CompanionPumpTimer;
EVT_WDF_IO_QUEUE_IO_CANCELED_ON_QUEUE CompanionWaitCanceled;
static DWORD WINAPI CompanionInputPumpProc(LPVOID param);
struct _COMPANION_CTX;
static BOOLEAN ReadGipDataLocked(struct _COMPANION_CTX *ctx, UCHAR gipOut[14], ULONGLONG *genOut);

static const GUID XUSB_GUID =
    { 0xEC87F1E3, 0xC13B, 0x4100, { 0xB5, 0xF7, 0x8B, 0x84, 0xD5, 0x42, 0x60, 0xCB } };

#define IOCTL_XUSB_GET_INFORMATION   0x80006000
#define IOCTL_XUSB_GET_CAPABILITIES  0x8000E004
#define IOCTL_XUSB_GET_LED_STATE     0x8000E008
#define IOCTL_XUSB_GET_STATE         0x8000E00C
#define IOCTL_XUSB_SET_STATE         0x8000A010
#define IOCTL_XUSB_WAIT_GUIDE        0x8000E014
#define IOCTL_XUSB_GET_BATTERY_INFO  0x8000E018
#define IOCTL_XUSB_GET_INFORMATION_EX 0x8000E3FC
#define IOCTL_XUSB_WAIT_FOR_INPUT    0x8000E3AC
#define IOCTL_XUSB_POWER_INFO        0x80006380

/* v2.0.0: the version contract of the design note's decision (c), as
 * hm_contract.h lays it out for both drivers. The companion reads a
 * controller's v2.0.0 values only when, at add, the controller's version
 * section exists, its range holds HM_COMPANION_PROTOCOL and its identity
 * tag is this devnode's own. It writes its own field and its error word,
 * and nothing of the HID driver's. */
#define HM_COMPANION_PROTOCOL           4
#define HM_COMPANION_PROTOCOL_MIN       4
/* CompanionConfigError: which value the companion could not read. */
#define HM_CONFIG_ERROR_XUSB_ANSWERS    1

/* The XusbAnswers blob the SDK compiles from the profile's xinput block
 * (XusbAnswers.cs, which carries the same limits and the layout): a
 * 12-byte header (Length, Format, Count), Count entries of 12 bytes
 * (IoControlCode, MinOutput, Mode, Offset, Size) and the answers' bytes. */
#define HM_XUSB_ANSWERS_FORMAT          4
#define HM_XUSB_ANSWERS_MAX_ENTRIES     8
#define HM_XUSB_ANSWERS_MAX_DATA        64
#define HM_XUSB_ANSWERS_MAX_BLOB        512
#define HM_XUSB_ANSWERS_HEADER          12
#define HM_XUSB_ANSWERS_ENTRY           12
#define HM_XUSB_ANSWER_EXACT            0   /* all Size bytes, or STATUS_BUFFER_TOO_SMALL */
#define HM_XUSB_ANSWER_UP_TO            1   /* as many of Size as the buffer holds */

typedef struct _XUSB_ANSWER {
    ULONG  IoControlCode;
    USHORT MinOutput;
    USHORT Mode;
    USHORT Size;
    UCHAR  Data[HM_XUSB_ANSWERS_MAX_DATA];
} XUSB_ANSWER;

/* XINPUT_GAMEPAD as the XUSB state carries it at byte 11: wButtons, the
 * triggers, then the sticks. */
typedef struct _XUSB_PAD {
    USHORT Buttons;
    UCHAR  LeftTrigger;
    UCHAR  RightTrigger;
    SHORT  ThumbLX;
    SHORT  ThumbLY;
    SHORT  ThumbRX;
    SHORT  ThumbRY;
} XUSB_PAD;

typedef struct _COMPANION_CTX {
    ULONG PacketCount;           /* reports so far, under ReportLock */
    ULONG ControllerIndex;
    /* v2.0.0: this devnode's identity token, from its instance ID, and
     * whether the version contract holds. Without it the companion reads no
     * v2.0.0 value, registers no XUSB interface and answers nothing. */
    WCHAR Token[40];
    BOOLEAN Contract;
    WCHAR VersionMappingName[64]; /* e.g. L"Global\HIDMaestroVersion0" */
    ULONG AnswerCount;
    XUSB_ANSWER Answers[HM_XUSB_ANSWERS_MAX_ENTRIES];
    WCHAR ConfigRegPath[64];   /* e.g. L"SOFTWARE\HIDMaestro\Controller0" */
    WCHAR SharedMappingName[64]; /* e.g. L"Global\HIDMaestroInput0" */
    WCHAR OutputMappingName[64]; /* e.g. L"Global\HIDMaestroOutput0" */
    WCHAR OutputEventName[64];   /* e.g. L"Global\HIDMaestroOutputEvent0" */
    HANDLE OutputSignalEvent;    /* Output-ring doorbell (issue #34), lazy */
    WCHAR CompanionInputEventName[64]; /* Global\HIDMaestroCompanionInputEvent<N> */
    HANDLE CompanionInputEvent;  /* input doorbell (perf audit 2026-07-21), lazy */
    HANDLE PumpStopEvent;        /* stops the input-doorbell thread */
    HANDLE PumpThread;           /* completes parked WAIT_FOR_INPUT on doorbell */
    CRITICAL_SECTION GipLock;    /* serializes the section's view and the
                                  * observations below: the doorbell thread,
                                  * the timer, GET_STATE and WAIT_FOR_INPUT
                                  * dispatch all read concurrently */
    BOOLEAN GipLockInit;
    /* 2.1.0: under GipLock. Every read of the section is an observation,
     * and ObservedGen goes up by one at each read that finds another frame
     * than the last one read: another SeqNo than ObservedSeq, which is
     * COMPANION_NO_FRAME while no view could be mapped, or, when both reads
     * were whole, the same SeqNo with other GIP bytes than ObservedGip. A
     * program before 2.1.0 counts its SeqNo from 0, so its first frame can
     * repeat the SeqNo of the last frame the program before it wrote. A
     * frame written or begun after one observation gives every later one a
     * larger number, unless it repeats the last frame read, SeqNo and
     * bytes, and so carries no new state. */
    ULONG ObservedSeq;
    BOOLEAN Observed;
    BOOLEAN ObservedGipWhole;
    UCHAR ObservedGip[14];
    ULONGLONG ObservedGen;
    HANDLE SharedMemHandle;    /* OpenFileMapping handle (lazy) */
    PVOID SharedMemPtr;        /* MapViewOfFile pointer (lazy) */
    HANDLE OutputMemHandle;    /* CreateFileMapping handle for output (lazy) */
    PVOID OutputMemPtr;        /* MapViewOfFile RW pointer for output */
    ULONG OutputWriteCount;    /* Stale-detection: total writes since last re-open */
    /* 2.1.0: serializes PublishOutput, EnsureOutputMapping's remap
     * included, as driver.c's PublishOutput takes its OutputLock: the XUSB
     * queue dispatches IOCTLs in parallel, and two SET_STATEs at the 500th
     * write each mapped a view, one of which leaked with its handle, or
     * one wrote its slot into the view the other had just unmapped. The
     * zeroed context's SRWLOCK_INIT. */
    SRWLOCK OutputLock;
    ULONG LastGipSeqNo;        /* Stale-detection: last SeqNo seen from GIP shared memory */
    ULONG GipStaleCount;       /* Consecutive reads with unchanged SeqNo */

    /* Throttled live-swap gate (IOCTL_XUSB_SET_STATE). The registry's
     * XusbCompanion for this index is re-read at most every 500 ms instead
     * of every IOCTL, so a per-frame XInputSetState does not force a
     * per-frame registry round-trip. A live-swap that re-profiles this
     * index is still caught within one throttle window (a transient
     * phantom is bounded). */
    ULONGLONG LastGateCheckTick;
    BOOLEAN   CachedGateOpen;
    BOOLEAN   GateCheckValid;
    /* Async XUSB input pump: holds pended IOCTL_XUSB_WAIT_FOR_INPUT
     * requests. WGI's XusbDevice::QueueInputBuffer (Windows.Gaming.Input.dll
     * 10.0.26100.9549) sends a WaitRequest0102 whose byte 3 is 2 until a
     * completion with state[2] = 3 has resumed it, and 3 after, and
     * XusbDevice::ProcessInput sends the next request once one completes.
     * A completion with an error ends that pump, and without the resume
     * Gamepad::SendControllerVibration bails at the flag_0x184 gate, since
     * OnInputResumed never fires on the Gamepad's IGameControllerInputSink. */
    WDFQUEUE WaitForInputQueue;
    WDFTIMER PumpTimer;
    /* 2.1.0: the signal gate (hm_contract.h, HIDMAESTRO_SIGNAL), mapped at
     * add under the contract and kept until the device goes. Each
     * WAIT_FOR_INPUT the companion holds is counted in CompanionParked: up
     * once it is in WaitForInputQueue, down when a report completes it or
     * the framework cancels it (CompanionWaitCanceled). A report that puts
     * a request back holds it counted. The SDK rings the doorbell only
     * while the count is not zero. NULL without a section, and the SDK
     * rings on every GIP frame. */
    WCHAR SignalMappingName[64]; /* e.g. L"Global\HIDMaestroSignal0" */
    HANDLE SignalMemHandle;
    PHIDMAESTRO_SIGNAL SignalPtr;
    /* A WAIT_FOR_INPUT that looks at the section before a frame is written
     * and is counted only after the SDK read the count gets no doorbell for
     * that frame, and one that enters the queue after the frame's report
     * went through it was not in that report. Its IOCTL thread then sets
     * this unnamed auto-reset event, which the pump also waits on, and the
     * pump's turn completes it with that report. Made for every companion
     * under the contract, which alone registers the XUSB interface. NULL
     * when it could not be made or no pump runs, and the IOCTL thread takes
     * that turn itself. */
    HANDLE ParkKick;
    /* 2.1.0: the reports, as xusb22 makes one for each report from the pad,
     * which the pad sends when its state changes. ReportLock lets one turn
     * run at a time, the doorbell's, a kick's, the timer's or GET_STATE's,
     * and holds the fields below. LastPad is the state of the last report,
     * ReportGen the observation of its frame. The zeroed context's
     * SRWLOCK_INIT. */
    SRWLOCK ReportLock;
    BOOLEAN HaveReport;
    XUSB_PAD LastPad;
    ULONGLONG ReportGen;
} COMPANION_CTX, *PCOMPANION_CTX;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(COMPANION_CTX, GetCompanionCtx)

/* 2.1.0: the context of every request of the device
 * (WdfDeviceInitSetRequestAttributes), which only WAIT_FOR_INPUT uses.
 * ParkGen is the observation of the section before the request entered
 * the queue: a report completes the request when it observed its frame
 * later. NextKept chains the requests a report puts back. */
typedef struct _COMPANION_WAIT {
    ULONGLONG ParkGen;
    WDFREQUEST NextKept;
} COMPANION_WAIT, *PCOMPANION_WAIT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(COMPANION_WAIT, GetCompanionWait)

/* The context of every file object (WdfDeviceInitSetFileObjectConfig):
 * whether a request through the handle carried a report or a GET_STATE
 * through it answered one, and the packet number of the last. Zeroed for
 * a handle that has seen none. Under ReportLock. */
typedef struct _COMPANION_HANDLE {
    BOOLEAN Seen;
    ULONG LastPacket;
} COMPANION_HANDLE, *PCOMPANION_HANDLE;
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(COMPANION_HANDLE, GetCompanionHandle)

/* Odd: no finished frame carries it. The observation with no view. */
#define COMPANION_NO_FRAME 1

/* Append the decimal representation of a ULONG to a wide-string buffer.
 * Self-contained: companion doesn't link MSVCRT, no swprintf available.
 * Lets the section/registry-path builders support indices > 9 so the
 * test app and SDK aren't capped at 4 controllers. */
static VOID
AppendUlongDecimal(WCHAR *dest, ULONG value, SIZE_T maxChars)
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

#pragma pack(push, 1)
/* Must stay in sync with HIDMAESTRO_SHARED_INPUT in driver/driver.h.
 * Data[] widened from 64 to 256 bytes 2026-04-23 to carry full DualSense
 * BT / Switch Pro gyro-bearing reports through the shared memory pipe
 * without truncation. v1.3.5: added ExtendedReportSize/ExtendedReportData
 * tail for Sony BT vendor-blob mode-switch path. Companion only reads
 * SeqNo + GipData; the tail is unused here but mirrored so any
 * MapViewOfFile sizing or future field offset reuses driver.h's layout. */
typedef struct {
    ULONG SeqNo;
    ULONG DataSize;
    UCHAR Data[256];
    UCHAR GipData[14];
    ULONG ExtendedReportSize;
    UCHAR ExtendedReportData[80];
} SHARED_INPUT;

/* Mirror of HIDMAESTRO_SHARED_OUTPUT (v1.1.40 ring) in driver.h. Companion
 * doesn't include driver.h (separate context type, no HID headers), so the
 * layout is duplicated here. Must stay byte-compatible with driver.h. */
#define HM_OUTPUT_RING_SLOTS     64u
#define HM_OUTPUT_SLOT_DATA_CAP  256u

typedef struct {
    volatile ULONG  SeqNo;           /* Per-slot. == Head value at write time. */
    UCHAR           Source;          /* OUT_SOURCE_XINPUT etc. */
    UCHAR           ReportId;
    USHORT          DataSize;
    UCHAR           Data[HM_OUTPUT_SLOT_DATA_CAP];
} HM_OUTPUT_SLOT;

typedef struct {
    volatile ULONG  Head;            /* Monotonic total writes. */
    ULONG           _Reserved;
    HM_OUTPUT_SLOT  Slots[HM_OUTPUT_RING_SLOTS];
} SHARED_OUTPUT;
#pragma pack(pop)

#define OUT_SOURCE_XINPUT      2

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, CompanionDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES, &config, WDF_NO_HANDLE);
}

/* Map the input section if no view is mapped. Under GipLock. */
static BOOLEAN MapInputLocked(PCOMPANION_CTX ctx)
{
    /* Lazy-open named section. RAM-only, no disk fallback. */
    if (ctx->SharedMemPtr == NULL) {
        HANDLE h = OpenFileMappingW(FILE_MAP_READ, FALSE, ctx->SharedMappingName);
        if (h == NULL) return FALSE;
        PVOID v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, sizeof(SHARED_INPUT));
        if (v == NULL) { CloseHandle(h); return FALSE; }
        ctx->SharedMemHandle = h;
        ctx->SharedMemPtr = v;
        ctx->LastGipSeqNo = 0;
        ctx->GipStaleCount = 0;
    }
    return TRUE;
}

/* 2.1.0: an observation of the section (ObservedGen): its SeqNo, and its
 * GIP bytes when the read was whole, else NULL. Under GipLock. */
static ULONGLONG ObserveLocked(PCOMPANION_CTX ctx, ULONG seq, _In_reads_opt_(14) const UCHAR *gip)
{
    if (!ctx->Observed || seq != ctx->ObservedSeq
        || (gip != NULL && ctx->ObservedGipWhole && RtlCompareMemory(gip, ctx->ObservedGip, 14) != 14)) {
        ctx->ObservedSeq = seq;
        ctx->Observed = TRUE;
        ctx->ObservedGipWhole = FALSE;
        ctx->ObservedGen++;
    }
    if (gip != NULL) {
        RtlCopyMemory(ctx->ObservedGip, gip, 14);
        ctx->ObservedGipWhole = TRUE;
    }
    return ctx->ObservedGen;
}

/* 2.1.0: the observation of the section as it is now, mapping the view
 * first. A WAIT_FOR_INPUT's IOCTL thread, before its request enters the
 * queue and after its count. The read retries as ReadGipDataLocked's does,
 * and a write under way through every retry is observed by its SeqNo. */
static ULONGLONG ObserveSection(PCOMPANION_CTX ctx)
{
    ULONGLONG gen;
    EnterCriticalSection(&ctx->GipLock);
    if (!MapInputLocked(ctx)) {
        gen = ObserveLocked(ctx, COMPANION_NO_FRAME, NULL);
    } else {
        volatile SHARED_INPUT *src = (volatile SHARED_INPUT *)ctx->SharedMemPtr;
        UCHAR gip[14];
        ULONG seq1, seq2;
        int retries = 4;
        do {
            seq1 = src->SeqNo;
            MemoryBarrier();
            for (int i = 0; i < 14; i++) gip[i] = src->GipData[i];
            MemoryBarrier();
            seq2 = src->SeqNo;
        } while ((seq1 != seq2 || (seq1 & 1)) && --retries > 0);
        gen = ObserveLocked(ctx, seq2, seq1 == seq2 && !(seq1 & 1) ? gip : NULL);
    }
    LeaveCriticalSection(&ctx->GipLock);
    return gen;
}

/* The newest frame's GIP bytes, and in *genOut the observation of its
 * SeqNo. FALSE with no frame to read: no view could be mapped, a write was
 * under way through every retry, which rings or is found again once it
 * ends, or the section holds SeqNo 0 with no data, as an SDK zeroes it
 * when it maps the index (driver.c ProcessSharedInput). */
static BOOLEAN ReadGipData(PCOMPANION_CTX ctx, UCHAR gipOut[14], ULONGLONG *genOut)
{
    /* Serialize against the other readers. The doorbell thread, the timer,
     * GET_STATE and WAIT_FOR_INPUT dispatch all land here or in
     * ObserveSection. The mapping handles, the observations and the stale
     * counters are shared ctx state. Uncontended cost is tens of ns. */
    EnterCriticalSection(&ctx->GipLock);
    BOOLEAN ok = ReadGipDataLocked(ctx, gipOut, genOut);
    LeaveCriticalSection(&ctx->GipLock);
    return ok;
}

static BOOLEAN ReadGipDataLocked(PCOMPANION_CTX ctx, UCHAR gipOut[14], ULONGLONG *genOut)
{
    if (!MapInputLocked(ctx)) return FALSE;

    /* Seqlock read: retry if writer was mid-update */
    volatile SHARED_INPUT* src = (volatile SHARED_INPUT*)ctx->SharedMemPtr;
    ULONG seq1, seq2, dataSize, extendedSize;
    int retries = 4;
    UCHAR tmp[14];
    do {
        seq1 = src->SeqNo;
        MemoryBarrier();
        dataSize = src->DataSize;
        for (int i = 0; i < 14; i++) tmp[i] = src->GipData[i];
        extendedSize = src->ExtendedReportSize;
        MemoryBarrier();
        seq2 = src->SeqNo;
    } while ((seq1 != seq2 || (seq1 & 1)) && --retries > 0);

    /* Perf audit 2026-07-21 (I6): an odd SeqNo is mid-write and an
     * unequal pair is torn. Nothing half-written is read. */
    if (seq1 != seq2 || (seq1 & 1)) return FALSE;
    *genOut = ObserveLocked(ctx, seq1, tmp);
    for (int i = 0; i < 14; i++) gipOut[i] = tmp[i];

    /* Stale-handle recovery (issue #1): if the SDK tore down and recreated
     * the shared memory section (RemoveAllVirtualControllers → Cleanup →
     * EnsureInputMapping), our cached handle points at the old destroyed
     * section. SeqNo will never advance. After 500 consecutive stale reads
     * (~2s at typical XInput polling rate), close and re-open. */
    if (seq1 == ctx->LastGipSeqNo) {
        if (++ctx->GipStaleCount > 500) {
            UnmapViewOfFile(ctx->SharedMemPtr); ctx->SharedMemPtr = NULL;
            CloseHandle(ctx->SharedMemHandle);  ctx->SharedMemHandle = NULL;
            ctx->GipStaleCount = 0;
            /* Perf audit 2026-07-21 (I4): gipOut already holds the last
             * valid payload (unchanged SeqNo means unchanged bytes), so
             * it stands. The next call lazy-opens the fresh section. */
        }
    } else {
        ctx->LastGipSeqNo = seq1;
        ctx->GipStaleCount = 0;
    }
    /* 2.1.0: a zeroed section is no frame. */
    return !(seq1 == 0 && dataSize == 0 && extendedSize == 0);
}

/* Let go of every object of the index. Runs from the self-managed I/O
 * flush and again from the context cleanup, where it finds nothing left:
 * each step clears what it closed. */
static VOID CompanionRelease(PCOMPANION_CTX ctx)
{
    if (ctx->PumpThread) {
        if (ctx->PumpStopEvent) SetEvent(ctx->PumpStopEvent);
        WaitForSingleObject(ctx->PumpThread, 2000);
        CloseHandle(ctx->PumpThread);
        ctx->PumpThread = NULL;
    }
    if (ctx->PumpStopEvent)      { CloseHandle(ctx->PumpStopEvent);      ctx->PumpStopEvent = NULL; }
    if (ctx->ParkKick)           { CloseHandle(ctx->ParkKick);           ctx->ParkKick = NULL; }
    if (ctx->CompanionInputEvent){ CloseHandle(ctx->CompanionInputEvent); ctx->CompanionInputEvent = NULL; }
    if (ctx->PumpTimer) { WdfTimerStop(ctx->PumpTimer, TRUE); ctx->PumpTimer = NULL; }
    /* After the doorbell thread is joined and the timer is stopped
     * (synchronously, above): no ReadGipData caller can remain. */
    if (ctx->GipLockInit) { DeleteCriticalSection(&ctx->GipLock); ctx->GipLockInit = FALSE; }
    if (ctx->SharedMemPtr) { UnmapViewOfFile(ctx->SharedMemPtr); ctx->SharedMemPtr = NULL; }
    if (ctx->SharedMemHandle) { CloseHandle(ctx->SharedMemHandle); ctx->SharedMemHandle = NULL; }
    if (ctx->OutputMemPtr) { UnmapViewOfFile(ctx->OutputMemPtr); ctx->OutputMemPtr = NULL; }
    if (ctx->OutputMemHandle) { CloseHandle(ctx->OutputMemHandle); ctx->OutputMemHandle = NULL; }
    if (ctx->OutputSignalEvent) { CloseHandle(ctx->OutputSignalEvent); ctx->OutputSignalEvent = NULL; }
    /* The queues were purged before the flush, and the pump and the timer
     * are stopped above, so nothing counts in the section any more. */
    if (ctx->SignalPtr) { UnmapViewOfFile(ctx->SignalPtr); ctx->SignalPtr = NULL; }
    if (ctx->SignalMemHandle) { CloseHandle(ctx->SignalMemHandle); ctx->SignalMemHandle = NULL; }
}

/* The device is gone: surprise removed, or removed. A host can process
 * IRP_MN_SURPRISE_REMOVAL and never receive the IRP_MN_REMOVE_DEVICE that
 * runs the context cleanup, since the PnP manager sends it only after
 * every handle to the device is closed. Until then the pump thread and
 * the timer ran on with no device, and in one test run the thread made
 * index 0's output doorbell on its first idle wake after the device was
 * gone and the host held it for 25 minutes. The framework runs this
 * flush on both paths and never on a stop for a rebalance, which keeps
 * the device and restarts it without a new CompanionDeviceAdd. Both
 * queues are power managed (no WdfFdoInitSetFilter here), so the
 * framework has purged them and finished their requests before it runs,
 * and no I/O callback can open a section or the doorbell after it. */
VOID CompanionSelfManagedIoFlush(_In_ WDFDEVICE Device)
{
    CompanionRelease(GetCompanionCtx(Device));
}

void CompanionDeviceCleanup(_In_ WDFOBJECT Object)
{
    /* A device that never started gets no flush. */
    CompanionRelease(GetCompanionCtx((WDFDEVICE)Object));
}

/* The output doorbell's descriptor and its check on an existing object,
 * as driver.c's CreateHostEvent with takeOverNullDacl set: SYSTEM,
 * Administrators and LocalService. An object that exists already is used
 * when one of those three owns it under a DACL, or LocalService or SYSTEM
 * owns it under the NULL DACL a driver before v2.0.0 gave it, which is
 * then replaced with the descriptor above. The doorbell is read-only to a
 * consumer, so a program holding it from before the takeover gains nothing.
 * driver.c keeps refusing the StopEvent for the opposite reason. NULL when
 * the name is unusable or the create fails. */
static const WCHAR g_HostEventSddl[] = L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)";

static HANDLE CreateHostEvent(BOOL manualReset, BOOL takeOverNullDacl, LPCWSTR name)
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
                /* The output doorbell. A driver before v2.0.0 made it with
                 * a NULL DACL, and a program still holds it, the SDK's output
                 * reader most often. A program that is not elevated cannot
                 * own an object as LocalService or SYSTEM. The owner's
                 * account may rewrite the DACL, so this host, which runs as
                 * that account, gives the object the descriptor above. A new
                 * open by name is then checked against it. A handle opened
                 * before keeps its access, which is harmless on a doorbell a
                 * consumer only reads. */
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

/* Create the output doorbell if there is none yet. Two threads call this:
 * an output IOCTL opening the mapping (the XUSB queue dispatches in
 * parallel) and the input pump. The handle is published only if the field
 * is still NULL, so a second creator closes its own handle instead of
 * leaking one. PublishOutput reads NULL or the handle, nothing else. */
static VOID TryCreateDoorbell(PCOMPANION_CTX ctx)
{
    if (ctx->OutputSignalEvent != NULL) return;
    HANDLE ev = CreateHostEvent(FALSE /* auto reset */, TRUE /* take over a NULL-DACL doorbell */, ctx->OutputEventName);
    if (ev != NULL
        && InterlockedCompareExchangePointer((PVOID volatile *)&ctx->OutputSignalEvent, ev, NULL) != NULL)
        CloseHandle(ev);
}

/* Open the per-controller output section. The test app pre-creates it with
 * a permissive SDDL; we just attach. WUDFHost runs as LocalService and
 * cannot create Global\ sections itself (no SeCreateGlobalPrivilege).
 *
 * Stale-handle recovery (issue #2, output side of #1): if the SDK tears
 * down and recreates the output section between sessions, our cached
 * handle points at the old (destroyed) kernel object. Writes go nowhere.
 * Every 500 writes (~2s at typical XInput polling rate), close and
 * re-open to pick up the fresh section. */
static BOOLEAN EnsureOutputMapping(PCOMPANION_CTX ctx)
{
    /* Periodic re-open: close stale mapping every 500 writes */
    if (ctx->OutputMemPtr != NULL) {
        if (++ctx->OutputWriteCount < 500) return TRUE;
        /* Time to re-validate: close and re-open */
        UnmapViewOfFile(ctx->OutputMemPtr); ctx->OutputMemPtr = NULL;
        CloseHandle(ctx->OutputMemHandle);  ctx->OutputMemHandle = NULL;
        ctx->OutputWriteCount = 0;
    }

    HANDLE h = OpenFileMappingW(FILE_MAP_WRITE | FILE_MAP_READ, FALSE,
                                ctx->OutputMappingName);
    if (h == NULL) return FALSE;

    PVOID v = MapViewOfFile(h, FILE_MAP_WRITE | FILE_MAP_READ, 0, 0, sizeof(SHARED_OUTPUT));
    if (v == NULL) { CloseHandle(h); return FALSE; }

    /* Output-ring doorbell (issue #34), acquired BEFORE the mapping
     * pointer becomes visible (audit of #34): the XUSB queue dispatches
     * IOCTLs in parallel, so a sibling callback that fast-paths on
     * OutputMemPtr must already see the event handle, or its first
     * publish would skip the signal and wait on the SDK's safety
     * timeout. CreateEventW creates the auto-reset event if we're first
     * or opens the HID child driver's existing object otherwise, so
     * ordering between the two hosts doesn't matter. The event object's
     * lifetime is the device session (unlike the SDK-recreated section),
     * so a live handle is kept across the 500-write mapping recycles and
     * only re-attempted while NULL, here and by the input pump on a steady
     * input stream (CompanionInputPumpProc). Non-fatal, an unusable name
     * included: on failure PublishOutput skips the signal and the SDK's
     * safety timeout drains the ring. */
    TryCreateDoorbell(ctx);

    ctx->OutputMemHandle = h;
    MemoryBarrier();
    ctx->OutputMemPtr = v;
    ctx->OutputWriteCount = 0;
    return TRUE;
}

static VOID PublishOutput(PCOMPANION_CTX ctx, UCHAR source, UCHAR reportId,
                          const UCHAR *data, ULONG dataSize)
{
    if (dataSize > HM_OUTPUT_SLOT_DATA_CAP) dataSize = HM_OUTPUT_SLOT_DATA_CAP;
    AcquireSRWLockExclusive(&ctx->OutputLock);
    if (!EnsureOutputMapping(ctx)) {
        ReleaseSRWLockExclusive(&ctx->OutputLock);
        return;
    }

    volatile SHARED_OUTPUT *dst = (volatile SHARED_OUTPUT *)ctx->OutputMemPtr;

    /* v1.1.40 ring writer (matches driver.c PublishOutput). OutputLock
     * keeps the companion's own writers one at a time. The driver-side
     * writer and the companion-side writer can target different slots
     * concurrently, but each slot uses a MemoryBarrier-fenced SeqNo for
     * torn-write detection.
     *
     * Multi-producer reservation (audit of #34, pre-existing bug): the
     * old max(Head, local)+1 read-modify-write could mint the same
     * sequence as the main HID driver publishing concurrently from its
     * own process, silently overwriting a slot. InterlockedIncrement on
     * the shared Head reserves a unique sequence across both producers;
     * the fenced slot.SeqNo store below remains the publish gate the
     * reader validates (a reserved-but-unwritten slot is retried on the
     * reader's next wake). Mirrors driver.c PublishOutput. */
    ULONG newSeq = (ULONG)InterlockedIncrement((volatile LONG *)&dst->Head);
    ULONG slotIdx = (newSeq - 1) % HM_OUTPUT_RING_SLOTS;
    volatile HM_OUTPUT_SLOT *slot = &dst->Slots[slotIdx];

    /* 2.1.0: a reader HM_OUTPUT_RING_SLOTS packets behind reads the very
     * slot this packet refills, checking its SeqNo before and after the
     * copy (SharedMemoryIO.TryReadOutputFrame). Left in place through the
     * refill, the old SeqNo passed both checks for a copy mixed from two
     * packets. Zero, the never-written value, goes in first, with a barrier
     * before the fields, so a copy that saw any new field reads the SeqNo
     * changed. */
    slot->SeqNo = 0;
    MemoryBarrier();

    slot->Source = source;
    slot->ReportId = reportId;
    slot->DataSize = (USHORT)dataSize;
    for (ULONG i = 0; i < dataSize; i++) slot->Data[i] = data[i];
    MemoryBarrier();
    slot->SeqNo = newSeq;
    ReleaseSRWLockExclusive(&ctx->OutputLock);

    /* Doorbell LAST (issue #34): Head is published, so a reader woken by
     * this signal always sees the new packet. Mirrors driver.c. */
    if (ctx->OutputSignalEvent) SetEvent(ctx->OutputSignalEvent);
}

/* The version section of the controller's index, mapped read-write, or
 * NULL when there is none. */
static PHIDMAESTRO_VERSION_INFO MapVersionSection(_In_ PCOMPANION_CTX ctx, _Out_ HANDLE *handle)
{
    *handle = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, ctx->VersionMappingName);
    if (*handle == NULL) return NULL;
    PHIDMAESTRO_VERSION_INFO v = (PHIDMAESTRO_VERSION_INFO)MapViewOfFile(
        *handle, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(HIDMAESTRO_VERSION_INFO));
    if (v == NULL) { CloseHandle(*handle); *handle = NULL; }
    return v;
}

static VOID UnmapVersionSection(_In_opt_ PHIDMAESTRO_VERSION_INFO view, _In_opt_ HANDLE handle)
{
    if (view) UnmapViewOfFile(view);
    if (handle) CloseHandle(handle);
}

/* Reads the controller's XusbAnswers blob into the context. FALSE when it
 * is missing, shorter than its header or its Length, of another format,
 * past a limit, or an entry's bytes leave the blob. */
static BOOLEAN LoadAnswers(_In_ PCOMPANION_CTX ctx)
{
    UCHAR blob[HM_XUSB_ANSWERS_MAX_BLOB];
    DWORD size = sizeof(blob), type = 0;
    HKEY hKey;
    ctx->AnswerCount = 0;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, ctx->ConfigRegPath, 0, KEY_READ, &hKey) != ERROR_SUCCESS) return FALSE;
    LSTATUS rs = RegQueryValueExW(hKey, L"XusbAnswers", NULL, &type, blob, &size);
    RegCloseKey(hKey);
    if (rs != ERROR_SUCCESS || type != REG_BINARY || size < HM_XUSB_ANSWERS_HEADER) return FALSE;
    ULONG length = *(ULONG UNALIGNED *)&blob[0];
    ULONG format = *(ULONG UNALIGNED *)&blob[4];
    ULONG count = *(ULONG UNALIGNED *)&blob[8];
    if (length != size || format != HM_XUSB_ANSWERS_FORMAT || count > HM_XUSB_ANSWERS_MAX_ENTRIES) return FALSE;
    ULONG arena = HM_XUSB_ANSWERS_HEADER + count * HM_XUSB_ANSWERS_ENTRY;
    if (arena > size) return FALSE;
    for (ULONG i = 0; i < count; i++) {
        PUCHAR e = &blob[HM_XUSB_ANSWERS_HEADER + i * HM_XUSB_ANSWERS_ENTRY];
        USHORT mode = *(USHORT UNALIGNED *)&e[6];
        USHORT offset = *(USHORT UNALIGNED *)&e[8];
        USHORT bytes = *(USHORT UNALIGNED *)&e[10];
        if (bytes > HM_XUSB_ANSWERS_MAX_DATA || (ULONG)arena + offset + bytes > size
            || (mode != HM_XUSB_ANSWER_EXACT && mode != HM_XUSB_ANSWER_UP_TO))
            return FALSE;
        XUSB_ANSWER *a = &ctx->Answers[i];
        a->IoControlCode = *(ULONG UNALIGNED *)&e[0];
        a->MinOutput = *(USHORT UNALIGNED *)&e[4];
        a->Mode = mode;
        a->Size = bytes;
        RtlCopyMemory(a->Data, &blob[arena + offset], bytes);
    }
    ctx->AnswerCount = count;
    return TRUE;
}

/* The answer to an IOCTL of this code: the entry with the largest
 * MinOutput the caller's buffer meets, or the smallest MinOutput when it
 * meets none. NULL for a code the blob does not answer. */
static const XUSB_ANSWER *FindAnswer(_In_ PCOMPANION_CTX ctx, ULONG code, size_t outLen)
{
    const XUSB_ANSWER *best = NULL, *smallest = NULL;
    for (ULONG i = 0; i < ctx->AnswerCount; i++) {
        const XUSB_ANSWER *a = &ctx->Answers[i];
        if (a->IoControlCode != code) continue;
        if (!smallest || a->MinOutput < smallest->MinOutput) smallest = a;
        if (a->MinOutput <= outLen && (!best || a->MinOutput > best->MinOutput)) best = a;
    }
    return best ? best : smallest;
}

NTSTATUS CompanionDeviceAdd(_In_ WDFDRIVER Driver, _Inout_ PWDFDEVICE_INIT DeviceInit)
{
    NTSTATUS status;
    WDFDEVICE device;
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WCHAR token[40];
    ULONG identityIndex = 0;
    BOOLEAN haveIdentityIndex;

    UNREFERENCED_PARAMETER(Driver);

    {
        WDF_PNPPOWER_EVENT_CALLBACKS pnpPower;
        WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpPower);
        pnpPower.EvtDeviceSelfManagedIoFlush = CompanionSelfManagedIoFlush;
        WdfDeviceInitSetPnpPowerEventCallbacks(DeviceInit, &pnpPower);
    }
    /* Before WdfDeviceCreate: the devnode's token and the index the SDK
     * wrote for it (decision (c)). */
    HmReadInstanceToken(DeviceInit, token, ARRAYSIZE(token));
    haveIdentityIndex = HmReadIdentityIndex(token, &identityIndex);

    /* 2.1.0: a COMPANION_WAIT on every request, which the framework
     * allocates with the request object. hidmaestro_xusb.inf admits
     * kernel-mode clients and null and unknown file objects
     * (UmdfFileObjectPolicy), so the file objects are optional, and a
     * request that comes with none is served as any other: the class a
     * UMDF device has by default (Windows-Driver-Frameworks
     * shared/core/fxdevice.cpp) with WdfFileObjectCanBeOptional, without
     * which the framework's lookup of a null file object fails at a
     * verifier break (fxfileobject.cpp, _GetFileObjectFromWdm). */
    {
        WDF_OBJECT_ATTRIBUTES requestAttributes;
        WDF_FILEOBJECT_CONFIG fileConfig;
        WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&requestAttributes, COMPANION_WAIT);
        WdfDeviceInitSetRequestAttributes(DeviceInit, &requestAttributes);
        WDF_FILEOBJECT_CONFIG_INIT(&fileConfig, WDF_NO_EVENT_CALLBACK, WDF_NO_EVENT_CALLBACK, WDF_NO_EVENT_CALLBACK);
        fileConfig.FileObjectClass = (WDF_FILEOBJECT_CLASS)(WdfFileObjectWdfCannotUseFsContexts | WdfFileObjectCanBeOptional);
        WDF_OBJECT_ATTRIBUTES fileAttributes;
        WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&fileAttributes, COMPANION_HANDLE);
        WdfDeviceInitSetFileObjectConfig(DeviceInit, &fileConfig, &fileAttributes);
    }

    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, COMPANION_CTX);
    attributes.EvtCleanupCallback = CompanionDeviceCleanup;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status)) return status;

    {
        PCOMPANION_CTX ctx = GetCompanionCtx(device);
        BOOLEAN haveHardwareIndex = FALSE;
        ctx->PacketCount = 0;
        ctx->ControllerIndex = 0;
        for (SIZE_T i = 0; i < ARRAYSIZE(token); i++) ctx->Token[i] = token[i];

        /* A v1 program's devnode: ControllerIndex from Device Parameters,
         * as every version reads it. */
        {
            NTSTATUS rkStatus;
            WDFKEY hkDevice;
            rkStatus = WdfDeviceOpenRegistryKey(device, PLUGPLAY_REGKEY_DEVICE,
                KEY_READ, WDF_NO_OBJECT_ATTRIBUTES, &hkDevice);
            if (NT_SUCCESS(rkStatus))
            {
                UNICODE_STRING valueName;
                RtlInitUnicodeString(&valueName, L"ControllerIndex");
                ULONG idx = 0;
                if (NT_SUCCESS(WdfRegistryQueryULong(hkDevice, &valueName, &idx))) {
                    ctx->ControllerIndex = idx;
                    haveHardwareIndex = TRUE;
                }
                WdfRegistryClose(hkDevice);
            }
        }

        /* v2.0.0: the identity entry names the index, and the contract holds
         * when that index's version section speaks this companion's protocol
         * and carries this devnode's identity tag. Otherwise the devnode is
         * a v1 program's: it keeps the index of its hardware key, and fails
         * its start when its hardware key holds none, as earlier companions
         * read index 0, another controller's, or when that index's section
         * carries another token's tag, since it is then not that
         * controller. */
        {
            ULONGLONG ownTag = HmIdentityTag(ctx->Token);
            ULONG candidates[2] = { identityIndex, ctx->ControllerIndex };
            for (int pass = haveIdentityIndex && ctx->Token[0] ? 0 : 1; pass < 2; pass++) {
                static const WCHAR verPrefix[] = L"Global\\HIDMaestroVersion";
                SIZE_T cap = sizeof(ctx->VersionMappingName) / sizeof(WCHAR);
                for (int i = 0; verPrefix[i]; i++) ctx->VersionMappingName[i] = verPrefix[i];
                ctx->VersionMappingName[(sizeof(verPrefix) / sizeof(WCHAR)) - 1] = L'\0';
                AppendUlongDecimal(ctx->VersionMappingName, candidates[pass], cap);
                HANDLE vh;
                PHIDMAESTRO_VERSION_INFO v = MapVersionSection(ctx, &vh);
                if (v == NULL) continue;
                LONG sdkMin = v->SdkProtocolMin;
                LONG sdkMax = v->SdkProtocolMax;
                ULONGLONG tag = *(volatile ULONGLONG UNALIGNED *)&v->IdentityTag;
                UnmapVersionSection(v, vh);
                if (pass == 0) {
                    if (sdkMin <= HM_COMPANION_PROTOCOL && HM_COMPANION_PROTOCOL <= sdkMax && tag == ownTag) {
                        ctx->ControllerIndex = identityIndex;
                        ctx->Contract = TRUE;
                        break;
                    }
                } else if (tag != 0 && tag != ownTag) {
                    return STATUS_DEVICE_CONFIGURATION_ERROR;
                }
            }
            if (!ctx->Contract && !haveHardwareIndex) return STATUS_DEVICE_CONFIGURATION_ERROR;
        }

        /* Build per-instance paths. Multi-digit indices supported. */
        {
            static const WCHAR prefix[] = L"SOFTWARE\\HIDMaestro\\Controller";
            SIZE_T cap = sizeof(ctx->ConfigRegPath) / sizeof(WCHAR);
            for (int i = 0; prefix[i]; i++) ctx->ConfigRegPath[i] = prefix[i];
            ctx->ConfigRegPath[(sizeof(prefix) / sizeof(WCHAR)) - 1] = L'\0';
            AppendUlongDecimal(ctx->ConfigRegPath, ctx->ControllerIndex, cap);
        }
        {
            static const WCHAR mapPrefix[] = L"Global\\HIDMaestroInput";
            SIZE_T cap = sizeof(ctx->SharedMappingName) / sizeof(WCHAR);
            for (int i = 0; mapPrefix[i]; i++) ctx->SharedMappingName[i] = mapPrefix[i];
            ctx->SharedMappingName[(sizeof(mapPrefix) / sizeof(WCHAR)) - 1] = L'\0';
            AppendUlongDecimal(ctx->SharedMappingName, ctx->ControllerIndex, cap);
        }
        {
            static const WCHAR outPrefix[] = L"Global\\HIDMaestroOutput";
            SIZE_T cap = sizeof(ctx->OutputMappingName) / sizeof(WCHAR);
            for (int i = 0; outPrefix[i]; i++) ctx->OutputMappingName[i] = outPrefix[i];
            ctx->OutputMappingName[(sizeof(outPrefix) / sizeof(WCHAR)) - 1] = L'\0';
            AppendUlongDecimal(ctx->OutputMappingName, ctx->ControllerIndex, cap);
        }
        {
            static const WCHAR evPrefix[] = L"Global\\HIDMaestroOutputEvent";
            SIZE_T cap = sizeof(ctx->OutputEventName) / sizeof(WCHAR);
            for (int i = 0; evPrefix[i]; i++) ctx->OutputEventName[i] = evPrefix[i];
            ctx->OutputEventName[(sizeof(evPrefix) / sizeof(WCHAR)) - 1] = L'\0';
            AppendUlongDecimal(ctx->OutputEventName, ctx->ControllerIndex, cap);
        }
        {
            static const WCHAR ciPrefix[] = L"Global\\HIDMaestroCompanionInputEvent";
            SIZE_T cap = sizeof(ctx->CompanionInputEventName) / sizeof(WCHAR);
            for (int i = 0; ciPrefix[i]; i++) ctx->CompanionInputEventName[i] = ciPrefix[i];
            ctx->CompanionInputEventName[(sizeof(ciPrefix) / sizeof(WCHAR)) - 1] = L'\0';
            AppendUlongDecimal(ctx->CompanionInputEventName, ctx->ControllerIndex, cap);
        }

        /* Under the contract: this companion's own field of the version
         * section, the oldest SDK protocol first and its protocol last, so a
         * reader that sees the protocol sees the minimum with it, and then
         * the controller's answers. A blob it cannot read fails the start,
         * with the value named in its error word first, and the create then
         * names it rather than leaving a companion that answers nothing.
         * Nothing of the HID driver's is written, and nothing is cleared
         * at cleanup. */
        if (ctx->Contract) {
            HANDLE vh;
            PHIDMAESTRO_VERSION_INFO v = MapVersionSection(ctx, &vh);
            if (v != NULL) {
                InterlockedExchange(&v->CompanionProtocolMin, HM_COMPANION_PROTOCOL_MIN);
                MemoryBarrier();
                InterlockedExchange(&v->CompanionProtocol, HM_COMPANION_PROTOCOL);
                if (!LoadAnswers(ctx)) {
                    InterlockedExchange(&v->CompanionConfigError, HM_CONFIG_ERROR_XUSB_ANSWERS);
                    UnmapVersionSection(v, vh);
                    return STATUS_DEVICE_CONFIGURATION_ERROR;
                }
                UnmapVersionSection(v, vh);
            } else {
                ctx->Contract = FALSE;
            }
        }

        /* 2.1.0: the signal gate, before any queue exists, so every
         * WAIT_FOR_INPUT the queue ever holds is counted. */
        if (ctx->Contract) {
            static const WCHAR sigPrefix[] = L"Global\\HIDMaestroSignal";
            SIZE_T cap = sizeof(ctx->SignalMappingName) / sizeof(WCHAR);
            for (int i = 0; sigPrefix[i]; i++) ctx->SignalMappingName[i] = sigPrefix[i];
            ctx->SignalMappingName[(sizeof(sigPrefix) / sizeof(WCHAR)) - 1] = L'\0';
            AppendUlongDecimal(ctx->SignalMappingName, ctx->ControllerIndex, cap);
            ctx->SignalPtr = HmMapSignal(ctx->SignalMappingName, &ctx->SignalMemHandle);
            if (ctx->SignalPtr != NULL)
                InterlockedOr(&ctx->SignalPtr->CompanionCaps, (LONG)HIDMAESTRO_SIGNAL_CAP_COMPANION_PARKED);
        }

        /* A v1 program's devnode whose index has no Controller<N> key was
         * left behind by a program and fails its start: there is no default
         * device. Under the contract LoadAnswers reads that key and names
         * a failure in the error word. */
        if (!ctx->Contract) {
            HKEY hKey;
            if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, ctx->ConfigRegPath, 0, KEY_READ, &hKey) != ERROR_SUCCESS)
                return STATUS_DEVICE_CONFIGURATION_ERROR;
            RegCloseKey(hKey);
        }
    }

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = CompanionIoControl;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, NULL);
    if (!NT_SUCCESS(status)) return status;

    /* Manual-dispatch queue for pended IOCTL_XUSB_WAIT_FOR_INPUT. */
    {
        WDF_IO_QUEUE_CONFIG manualCfg;
        WDF_IO_QUEUE_CONFIG_INIT(&manualCfg, WdfIoQueueDispatchManual);
        /* A request canceled in the queue, or purged with it, comes back
         * here, so it leaves CompanionParked as it entered it. The
         * framework calls this for a request it presented to the driver
         * (Windows-Driver-Frameworks fxioqueue.cpp, CancelForQueue), which
         * every forwarded WAIT_FOR_INPUT is. */
        manualCfg.EvtIoCanceledOnQueue = CompanionWaitCanceled;
        PCOMPANION_CTX ctx = GetCompanionCtx(device);
        status = WdfIoQueueCreate(device, &manualCfg, WDF_NO_OBJECT_ATTRIBUTES,
                                  &ctx->WaitForInputQueue);
        if (!NT_SUCCESS(status)) return status;

        /* Must precede WdfTimerStart: the first tick can land 8 ms later
         * and takes GipLock inside ReadGipData. */
        InitializeCriticalSection(&ctx->GipLock);
        ctx->GipLockInit = TRUE;

        WDF_TIMER_CONFIG timerCfg;
        WDF_TIMER_CONFIG_INIT_PERIODIC(&timerCfg, CompanionPumpTimer, 8);
        WDF_OBJECT_ATTRIBUTES timerAttrs;
        WDF_OBJECT_ATTRIBUTES_INIT(&timerAttrs);
        timerAttrs.ParentObject = device;
        status = WdfTimerCreate(&timerCfg, &timerAttrs, &ctx->PumpTimer);
        if (!NT_SUCCESS(status)) return status;
        WdfTimerStart(ctx->PumpTimer, WDF_REL_TIMEOUT_IN_MS(8));

        /* Input-doorbell thread (perf audit 2026-07-21): see
         * CompanionInputPumpProc. Non-fatal on failure: the 8 ms timer
         * remains the pump. 2.1.0: the kick for every companion that
         * registers the XUSB interface, signal section or not. Without it a
         * request that entered the queue after its frame's report went
         * through it waited for the next report, under every SDK before
         * 2.1.0. */
        ctx->PumpStopEvent = CreateEventW(NULL, TRUE /* manual reset */, FALSE, NULL);
        if (ctx->Contract)
            ctx->ParkKick = CreateEventW(NULL, FALSE /* auto reset */, FALSE, NULL);
        if (ctx->PumpStopEvent != NULL) {
            ctx->PumpThread = CreateThread(NULL, 0, CompanionInputPumpProc,
                                           (LPVOID)device, 0, NULL);
            if (ctx->PumpThread == NULL) {
                CloseHandle(ctx->PumpStopEvent);
                ctx->PumpStopEvent = NULL;
            }
        }
        /* No pump, nobody to kick. */
        if (ctx->PumpThread == NULL && ctx->ParkKick != NULL) {
            CloseHandle(ctx->ParkKick);
            ctx->ParkKick = NULL;
        }
    }

    /* v2.0.0: the XUSB interface is registered only for a controller whose
     * answers the companion holds, so no reader opens a device that would
     * answer nothing (a v1 program's, decision (c)).
     *
     * HIDMAESTRO publishes ONLY GUID_DEVINTERFACE_XUSB. With our INF's
     * UpperFilters="xinputhid" tripwire, ProviderManagerWorker::OnPnpDeviceAdded
     * sees cVar3=true AND interface==XUSB {ec87f1e3}, and dispatches via the
     * XUSB path (LAB_18005f241). Publishing additional interfaces (WinExInput,
     * speculative WGI_UNK1) produces extra PnpDevice arrivals that confuse WGI
     * into classifying our one logical controller as multiple WGI entities
     * the known hang mode documented in
     * memory:feedback-one-wgi-device-per-controller.md. */
    if (GetCompanionCtx(device)->Contract)
        WdfDeviceCreateDeviceInterface(device, (LPGUID)&XUSB_GUID, NULL);

    return STATUS_SUCCESS;
}

static void CopyToRequest(WDFREQUEST Request, const void* data, size_t len)
{
    PVOID outBuf; size_t outLen;
    if (NT_SUCCESS(WdfRequestRetrieveOutputBuffer(Request, len, &outBuf, &outLen))) {
        RtlCopyMemory(outBuf, data, len);
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, len);
    } else {
        WdfRequestComplete(Request, STATUS_BUFFER_TOO_SMALL);
    }
}

/* The XInput state of a GIP frame's 14 bytes. */
static VOID DecodeGipToXInput(_In_reads_(14) const UCHAR *d, _Out_ XUSB_PAD *pad)
{
    UCHAR btnLow = d[12], btnHigh = d[13];
    UCHAR hat = (btnHigh >> 2) & 0x0F;
    USHORT buttons = 0;
    if (btnLow & 0x01) buttons |= 0x1000;
    if (btnLow & 0x02) buttons |= 0x2000;
    if (btnLow & 0x04) buttons |= 0x4000;
    if (btnLow & 0x08) buttons |= 0x8000;
    if (btnLow & 0x10) buttons |= 0x0100;
    if (btnLow & 0x20) buttons |= 0x0200;
    if (btnLow & 0x40) buttons |= 0x0040;
    if (btnLow & 0x80) buttons |= 0x0080;
    if (btnHigh & 0x01) buttons |= 0x0020;
    if (btnHigh & 0x02) buttons |= 0x0010;
    if (btnHigh & 0x40) buttons |= 0x0400;
    switch (hat) {
        case 1: buttons |= 0x0001; break; case 2: buttons |= 0x0009; break;
        case 3: buttons |= 0x0008; break; case 4: buttons |= 0x000A; break;
        case 5: buttons |= 0x0002; break; case 6: buttons |= 0x0006; break;
        case 7: buttons |= 0x0004; break; case 8: buttons |= 0x0005; break;
    }
    pad->Buttons = buttons;
    pad->LeftTrigger = (UCHAR)((*(const USHORT UNALIGNED *)&d[8] & 0x03FF) * 255 / 1023);
    pad->RightTrigger = (UCHAR)((*(const USHORT UNALIGNED *)&d[10] & 0x03FF) * 255 / 1023);
    pad->ThumbLX = (SHORT)((int)(*(const USHORT UNALIGNED *)&d[0]) - 32768);
    pad->ThumbLY = (SHORT)(32767 - (int)(*(const USHORT UNALIGNED *)&d[2]));
    pad->ThumbRX = (SHORT)((int)(*(const USHORT UNALIGNED *)&d[4]) - 32768);
    pad->ThumbRY = (SHORT)(32767 - (int)(*(const USHORT UNALIGNED *)&d[6]));
}

/* The 29-byte state xusb22 answers IOCTL_XUSB_GET_STATE with and completes
 * a WAIT_FOR_INPUT with on a report (XInputRequestServicers::GetState0101
 * and XInputControllerDevice::ProcessWaitRequests in xusb22.sys
 * 10.0.26100.9549): version 0x0103, state[2] = 1, state[3] the pad's slot
 * on its device, 0 for the one pad here, and state[4] 0xFF for no headset.
 * Both copy the rest from where HandleIncomingStateReport keeps the last
 * report: its packet number at state[5], then the pad's 20-byte report,
 * 0x00 and its length 0x14 at state[9] and state[10], and XINPUT_GAMEPAD:
 * wButtons at state[0x0B], the triggers at [0x0D] and [0x0E], the sticks
 * at [0x0F..0x16]. Before the first report all of it is zero. xinput1_4
 * parses that layout. WGI's XusbDevice::ProcessInput skips a completion
 * whose state[10] is 0 and hands state[9] to its sinks as the report id,
 * which XusbInputParser's Gamepad template needs at 0 (a fingerprint test
 * on 2026-04-23 read all zeros with 0x14 there). DO NOT rearrange without
 * re-verifying XInput/DirectInput/HIDAPI. Under ReportLock. */
static VOID BuildXusbState(_In_ PCOMPANION_CTX ctx, _Out_writes_(29) UCHAR state[29])
{
    RtlZeroMemory(state, 29);
    *(USHORT *)&state[0] = 0x0103;
    state[2] = 0x01;
    state[4] = 0xFF;
    *(ULONG UNALIGNED *)&state[5] = ctx->PacketCount;
    if (!ctx->HaveReport) return;
    state[0x0A] = 0x14;
    *(USHORT UNALIGNED *)&state[0x0B] = ctx->LastPad.Buttons;
    state[0x0D] = ctx->LastPad.LeftTrigger;
    state[0x0E] = ctx->LastPad.RightTrigger;
    *(SHORT UNALIGNED *)&state[0x0F] = ctx->LastPad.ThumbLX;
    *(SHORT UNALIGNED *)&state[0x11] = ctx->LastPad.ThumbLY;
    *(SHORT UNALIGNED *)&state[0x13] = ctx->LastPad.ThumbRX;
    *(SHORT UNALIGNED *)&state[0x15] = ctx->LastPad.ThumbRY;
}

/* The request's handle carries the last report now. Under ReportLock. */
static VOID NoteHandleLocked(_In_ PCOMPANION_CTX ctx, _In_ WDFREQUEST req)
{
    WDFFILEOBJECT fo = WdfRequestGetFileObject(req);
    if (fo == NULL) return;
    PCOMPANION_HANDLE h = GetCompanionHandle(fo);
    h->Seen = TRUE;
    h->LastPacket = ctx->PacketCount;
}

/* Whether the request's handle has carried no report, or an older one than
 * the last. A pad sends at most one report per poll of its interrupt
 * endpoint, so a reader that sends its next wait as soon as one completes
 * has it parked before the pad's next report. The SDK's frames come at any
 * pace, and a frame written between a completion and the reader's next
 * wait would leave that wait beside it until the state changed again.
 * Under ReportLock. */
static BOOLEAN HandleMissedReportLocked(_In_ PCOMPANION_CTX ctx, _In_ WDFREQUEST req)
{
    WDFFILEOBJECT fo = WdfRequestGetFileObject(req);
    if (fo == NULL || !ctx->HaveReport) return FALSE;
    PCOMPANION_HANDLE h = GetCompanionHandle(fo);
    return !h->Seen || h->LastPacket != ctx->PacketCount;
}

/* 2.1.0: a turn of the companion. The pad xusb22 drives sends a report
 * when its state changes, and on each one xusb22 bumps the packet number
 * and completes every WAIT_FOR_INPUT parked through any handle whose
 * process passes its focus test, all with the same 29 bytes, in the order
 * they were parked, and puts the rest back in that order
 * (XInputControllerDevice::HandleIncomingStateReport and
 * ProcessWaitRequests). Here a report is a frame whose XInput state
 * differs from the last report's, found by the first turn that reads it.
 * No focus information reaches the companion (docs/design/signal-gate.md),
 * so every request passes. A request that observed the section before
 * the frame (ParkGen below ReportGen) was waiting when the pad would have
 * sent the report, and gets it. One that observed the frame came after
 * that report and waits for the next. A turn for a kick (late) also
 * completes, with the last report, a request that observed the section
 * before that report's frame and entered the queue after the report's
 * turn took the requests.
 *
 * The completions run under ReportLock, so one report's completions all
 * come before the next one's, and no reader gets an older state after a
 * newer one. The count goes down only for a request completed: one put
 * back stays counted, so a frame written meanwhile rings. The doorbell, a
 * kick, the timer or GET_STATE. Under ReportLock. */
static VOID CompanionReportLocked(_In_ PCOMPANION_CTX ctx, BOOLEAN late)
{
    UCHAR gip[14];
    UCHAR state[29];
    ULONGLONG gen;
    BOOLEAN fresh = FALSE;
    WDFREQUEST kept = NULL;

    if (ctx->WaitForInputQueue == NULL) return;
    if (ReadGipData(ctx, gip, &gen)) {
        XUSB_PAD pad;
        DecodeGipToXInput(gip, &pad);
        if (!ctx->HaveReport || RtlCompareMemory(&pad, &ctx->LastPad, sizeof(pad)) != sizeof(pad)) {
            ctx->LastPad = pad;
            ctx->HaveReport = TRUE;
            ctx->ReportGen = gen;
            ctx->PacketCount++;
            fresh = TRUE;
        }
    }
    if (!ctx->HaveReport || (!fresh && !late)) return;

    BuildXusbState(ctx, state);
    for (;;) {
        WDFREQUEST req;
        PVOID out;
        size_t outLen;
        if (!NT_SUCCESS(WdfIoQueueRetrieveNextRequest(ctx->WaitForInputQueue, &req))) break;
        PCOMPANION_WAIT w = GetCompanionWait(req);
        if (w->ParkGen >= ctx->ReportGen) {
            w->NextKept = kept;
            kept = req;
            continue;
        }
        /* Completed: no longer counted for the SDK's doorbell. */
        if (ctx->SignalPtr != NULL) InterlockedDecrement(&ctx->SignalPtr->CompanionParked);
        /* The dispatch refused a buffer under 29 bytes. */
        if (!NT_SUCCESS(WdfRequestRetrieveOutputBuffer(req, 29, &out, &outLen))) {
            WdfRequestComplete(req, STATUS_BUFFER_TOO_SMALL);
            continue;
        }
        RtlCopyMemory(out, state, 29);
        NoteHandleLocked(ctx, req);
        WdfRequestCompleteWithInformation(req, STATUS_SUCCESS, 29);
    }
    /* Back at the head of the queue, the last one first, so they keep
     * their order ahead of any request parked since (Windows-Driver-
     * Frameworks fxioqueue.cpp, FxIoQueue::Requeue, InsertHeadIrpQueue).
     * One canceled meanwhile goes to CompanionWaitCanceled. */
    while (kept != NULL) {
        WDFREQUEST req = kept;
        kept = GetCompanionWait(req)->NextKept;
        NTSTATUS s = WdfRequestRequeue(req);
        if (!NT_SUCCESS(s)) {
            if (ctx->SignalPtr != NULL) InterlockedDecrement(&ctx->SignalPtr->CompanionParked);
            WdfRequestComplete(req, s);
        }
    }
}

static VOID CompanionTurn(_In_ PCOMPANION_CTX ctx, BOOLEAN late)
{
    AcquireSRWLockExclusive(&ctx->ReportLock);
    CompanionReportLocked(ctx, late);
    ReleaseSRWLockExclusive(&ctx->ReportLock);
}

VOID CompanionWaitCanceled(_In_ WDFQUEUE Queue, _In_ WDFREQUEST Request)
{
    PCOMPANION_CTX ctx = GetCompanionCtx(WdfIoQueueGetDevice(Queue));
    if (ctx->SignalPtr != NULL) InterlockedDecrement(&ctx->SignalPtr->CompanionParked);
    WdfRequestComplete(Request, STATUS_CANCELLED);
}

/* The 8 ms timer: the turns for an SDK that makes no companion doorbell,
 * one before the perf audit of 2026-07-21 or one that could not make it,
 * while the pump has none open. With the doorbell, every frame a parked
 * request is owed rings it or is kicked for, and a tick does nothing. */
VOID CompanionPumpTimer(_In_ WDFTIMER Timer)
{
    WDFDEVICE device = (WDFDEVICE)WdfTimerGetParentObject(Timer);
    PCOMPANION_CTX ctx = GetCompanionCtx(device);
    if (ctx->CompanionInputEvent != NULL) return;
    CompanionTurn(ctx, FALSE);
}

/* Input-doorbell thread (perf audit 2026-07-21). The SDK signals
 * Global\HIDMaestroCompanionInputEvent<N> per GIP-carrying frame, so a
 * parked WAIT_FOR_INPUT completes at frame arrival (sub-millisecond)
 * instead of waiting for the 8 ms timer tick that put a 0-8 ms phase
 * delay on the WGI/GameInput input path. The timer turns only while no
 * doorbell is open (old SDKs never create the event), and ReportLock
 * lets one turn run at a time. The 2 s wait timeout doubles as the
 * re-open cadence: the handle is let go and opened again, so an event
 * object a new SDK session made after the last one closed is picked up.
 * While none can be opened, the open is retried every 500 ms and the
 * timer serves. */
static DWORD WINAPI CompanionInputPumpProc(LPVOID param)
{
    WDFDEVICE device = (WDFDEVICE)param;
    PCOMPANION_CTX ctx = GetCompanionCtx(device);
    ULONG doorbellTick = 0;

    for (;;) {
        if (ctx->CompanionInputEvent == NULL) {
            ctx->CompanionInputEvent = OpenEventW(SYNCHRONIZE, FALSE,
                                                  ctx->CompanionInputEventName);
            if (ctx->CompanionInputEvent == NULL) {
                /* 2.1.0: a kick is answered here too, while the doorbell
                 * is closed. */
                HANDLE boot[2] = { ctx->PumpStopEvent, ctx->ParkKick };
                DWORD rb = WaitForMultipleObjects(ctx->ParkKick != NULL ? 2 : 1, boot, FALSE, 500);
                if (rb == WAIT_OBJECT_0)
                    return 0;
                if (rb == WAIT_OBJECT_0 + 1)
                    CompanionTurn(ctx, TRUE);
                continue;
            }
        }

        HANDLE waits[3] = { ctx->PumpStopEvent, ctx->CompanionInputEvent, ctx->ParkKick };
        DWORD rc = WaitForMultipleObjects(ctx->ParkKick != NULL ? 3 : 2, waits, FALSE, 2000);
        if (rc == WAIT_OBJECT_0)
            return 0;
        if (rc == WAIT_OBJECT_0 + 2) {
            /* A WAIT_FOR_INPUT observed a frame written after its first
             * look, which may have rung before it was counted, or entered
             * the queue after that frame's report. */
            CompanionTurn(ctx, TRUE);
            continue;
        }
        /* A doorbell this device could not make, while a program held its
         * name, is retried on a steady input stream every 256 frames and
         * on each 2 s idle wake, as the main driver's worker does. Output
         * writes alone retry it only every 500 publishes. On a frame the
         * attempt runs after the frame, so it never adds to its latency. */
        if (rc == WAIT_OBJECT_0 + 1) {
            CompanionTurn(ctx, FALSE);
            if (ctx->OutputSignalEvent == NULL && (++doorbellTick & 0xFF) == 0)
                TryCreateDoorbell(ctx);
            continue;
        }
        if (ctx->OutputSignalEvent == NULL)
            TryCreateDoorbell(ctx);
        /* Timeout or failure: drop the handle and re-open so a recreated
         * event object (new SDK session, same name) gets picked up. */
        CloseHandle(ctx->CompanionInputEvent);
        ctx->CompanionInputEvent = NULL;
    }
}

void CompanionIoControl(
    _In_ WDFQUEUE Queue,
    _In_ WDFREQUEST Request,
    _In_ size_t OutputBufferLength,
    _In_ size_t InputBufferLength,
    _In_ ULONG IoControlCode)
{
    PCOMPANION_CTX ctx = GetCompanionCtx(WdfIoQueueGetDevice(Queue));
    UNREFERENCED_PARAMETER(InputBufferLength);
    UNREFERENCED_PARAMETER(OutputBufferLength);

    switch (IoControlCode)
    {
    case IOCTL_XUSB_GET_INFORMATION:
    case IOCTL_XUSB_GET_CAPABILITIES:
    case IOCTL_XUSB_GET_BATTERY_INFO:
    case IOCTL_XUSB_GET_INFORMATION_EX: {
        /* v2.0.0: the controller's answers, from the XusbAnswers blob the
         * SDK compiles from the profile's xinput block (XusbAnswers.cs):
         * GET_INFORMATION's 12 bytes, GET_CAPABILITIES in its 24-byte form
         * (GamepadCapabilities0101) or, for a buffer of 36 or more, its
         * 36-byte form (GamepadCapabilities0102), GET_BATTERY_INFORMATION's
         * four bytes and GET_INFORMATION_EX's 64. A v1 program's companion
         * holds none and answers nothing (decision (c)). */
        const XUSB_ANSWER *a = ctx->Contract ? FindAnswer(ctx, IoControlCode, OutputBufferLength) : NULL;
        if (a == NULL) {
            WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        } else if (a->Mode == HM_XUSB_ANSWER_UP_TO) {
            CopyToRequest(Request, a->Data, OutputBufferLength < a->Size ? OutputBufferLength : a->Size);
        } else {
            CopyToRequest(Request, a->Data, a->Size);
        }
        break;
    }

    case IOCTL_XUSB_GET_STATE: {
        /* 2.1.0: a frame no turn has read yet is reported first, so the
         * packet number goes up once a report, as xusb22's does, and two
         * answers with one packet number carry one state (XINPUT_STATE,
         * dwPacketNumber). */
        UCHAR state[29];
        AcquireSRWLockExclusive(&ctx->ReportLock);
        CompanionReportLocked(ctx, FALSE);
        BuildXusbState(ctx, state);
        if (ctx->HaveReport) NoteHandleLocked(ctx, Request);
        ReleaseSRWLockExclusive(&ctx->ReportLock);
        CopyToRequest(Request, state, 29);
        break;
    }

    case IOCTL_XUSB_SET_STATE: {
        /* XInput rumble. Forward the wire-format vibration packet to the
         * output shared section as source=XInput. The consumer interprets
         * the bytes (typical packet is 5 bytes: cmd + size + lo motor +
         * hi motor + reserved, or 4 bytes for the raw XINPUT_VIBRATION).
         *
         * CRITICAL: a stale-HIDMAESTRO-after-live-swap gate. When the user
         * live-swaps from an Xbox 360 profile to another, TeardownController
         * removes the companion, but devcon can return "Removed on reboot"
         * and the companion persists as a phantom still serving IOCTLs.
         * xinput1_4 / GameInputSvc keep sending IOCTL_XUSB_SET_STATE here;
         * without this gate we publish Source=XInput to the SAME shared
         * output section the new controller is reading from: so its SDK
         * surfaces phantom XInput rumble packets. Fix: re-read the CURRENT
         * registry XusbCompanion for this ControllerIndex, which the SDK
         * writes 1 for a profile with a companion and deletes for any
         * other. If the index has been re-profiled, it is no longer 1 and
         * we silently drop. Safer than sampling ONCE at init because
         * live-swap DOES update the registry before creating the new
         * controller. Throttled to one read per 500 ms so a per-frame
         * XInputSetState (many games call it every frame regardless of
         * rumble change) does not force a per-frame registry round-trip;
         * a re-profiled index is still caught within one window. A v1
         * program's companion publishes nothing (decision (c)). */
        BOOLEAN gateOpen = FALSE;
        if (ctx->Contract) {
            ULONGLONG now = GetTickCount64();
            if (!ctx->GateCheckValid || (now - ctx->LastGateCheckTick) >= 500) {
                BOOLEAN open = FALSE;
                HKEY hKey;
                DWORD val = 0, sz = sizeof(val), type = 0;
                if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, ctx->ConfigRegPath,
                                  0, KEY_READ, &hKey) == ERROR_SUCCESS) {
                    if (RegQueryValueExW(hKey, L"XusbCompanion", NULL, &type,
                                         (LPBYTE)&val, &sz) == ERROR_SUCCESS
                        && type == REG_DWORD && val != 0) {
                        open = TRUE;
                    }
                    RegCloseKey(hKey);
                }
                ctx->CachedGateOpen = open;
                ctx->GateCheckValid = TRUE;
                ctx->LastGateCheckTick = now;
            }
            gateOpen = ctx->CachedGateOpen;
        }

        PVOID rumbleBuf; size_t rumbleSize;
        if (NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, 1, &rumbleBuf, &rumbleSize))
            && rumbleSize >= 1) {
            if (gateOpen) {
                PublishOutput(ctx, OUT_SOURCE_XINPUT, 0,
                              (const UCHAR*)rumbleBuf, (ULONG)rumbleSize);
            }
        }
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, 0);
        break;
    }

    case IOCTL_XUSB_POWER_INFO:
        WdfRequestCompleteWithInformation(Request, STATUS_SUCCESS, 0);
        break;

    case IOCTL_XUSB_GET_LED_STATE: {
        UCHAR led[3] = { 0, 0, 0x06 };
        CopyToRequest(Request, led, 3);
        break;
    }

    case IOCTL_XUSB_WAIT_FOR_INPUT: {
        /* 2.1.0: as xusb22's DispatchWaitForInput and
         * XInputRequestServicers::WaitForInputChange (10.0.26100.9549), an
         * input under 4 bytes or of a version other than 0x0102
         * (WaitRequest0102), an output under 29 bytes, or the index of a
         * pad this device does not have fails with STATUS_INVALID_PARAMETER.
         * Byte 3 asks for the focus state: with 2 a process that has focus
         * gets state[2] = 3 and the rest zero at once, and with 3 one that
         * lacks it gets state[2] = 2. Every process passes the focus test
         * here, so 2 completes at once and any other value parks. WGI sends
         * 2 until such a completion resumes it. */
        PUCHAR in;
        if (InputBufferLength < 4 || OutputBufferLength < 29
            || !NT_SUCCESS(WdfRequestRetrieveInputBuffer(Request, 4, (PVOID *)&in, NULL))
            || *(USHORT UNALIGNED *)in != 0x0102 || in[2] != 0) {
            WdfRequestComplete(Request, STATUS_INVALID_PARAMETER);
            break;
        }
        if (in[3] == 2) {
            UCHAR resumed[29];
            RtlZeroMemory(resumed, sizeof(resumed));
            *(USHORT *)&resumed[0] = 0x0103;
            resumed[2] = 0x03;
            CopyToRequest(Request, resumed, 29);
            break;
        }
        /* A turn first, then the handle's catch-up, then the first look,
         * all under ReportLock, so no turn reports a frame between the
         * catch-up's test and the look that the request would then wait
         * beside. Otherwise parked until a report completes it
         * (CompanionReportLocked). The request observes the section before
         * it enters the queue, where a turn can complete it at once, and is
         * not touched after. */
        AcquireSRWLockExclusive(&ctx->ReportLock);
        CompanionReportLocked(ctx, FALSE);
        if (HandleMissedReportLocked(ctx, Request)) {
            UCHAR state[29];
            BuildXusbState(ctx, state);
            NoteHandleLocked(ctx, Request);
            CopyToRequest(Request, state, 29);
            ReleaseSRWLockExclusive(&ctx->ReportLock);
            break;
        }
        ULONGLONG parkGen = ObserveSection(ctx);
        ReleaseSRWLockExclusive(&ctx->ReportLock);
        GetCompanionWait(Request)->ParkGen = parkGen;
        NTSTATUS s = WdfRequestForwardToIoQueue(Request, ctx->WaitForInputQueue);
        if (!NT_SUCCESS(s)) {
            WdfRequestComplete(Request, s);
            break;
        }
        /* 2.1.0: counted once it is in the queue, by an interlocked
         * increment, a full barrier, and then a second observation. The
         * SDK stores the SeqNo, fences and reads the count. So of a frame
         * written after the first observation, either the SDK finds the
         * count and rings, or the second observation finds the frame
         * (docs/design/signal-gate.md). A report can take the request
         * before the increment: the count is then -1 for a moment, and the
         * SDK rings on any count but 0. */
        if (ctx->SignalPtr != NULL) {
            InterlockedIncrement(&ctx->SignalPtr->CompanionParked);
#if defined(_M_ARM64)
            /* Without the v8.1 atomics the increment's helper is an
             * ldaxr/stlxr loop with no dmb after it: the fence keeps the
             * SeqNo read below after the count's store. */
            MemoryBarrier();
#endif
        }
        /* A frame between the two observations may have rung before the
         * count, or been reported by a turn that took the queue before the
         * request entered it. Without the section the SDK rings for every
         * frame, and stores the SeqNo before its SetEvent: either the
         * turn on that ring found this request, or this observation finds
         * the frame. The kick's turn reports the frame, or completes the
         * request with the report it missed. */
        if (ObserveSection(ctx) != parkGen) {
            if (ctx->ParkKick != NULL) SetEvent(ctx->ParkKick);
            else CompanionTurn(ctx, TRUE);
        }
        break;
    }

    case IOCTL_XUSB_WAIT_GUIDE:
        /* No Guide-button async notification surface yet. xinput1_4 falls
         * back to GET_STATE polling when this returns INVALID_DEVICE_REQUEST. */
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        break;

    default:
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_REQUEST);
        break;
    }
}
