/*
 * HIDMaestro: UMDF2 Virtual HID Minidriver (driver-internal header)
 *
 * This is a lower filter driver under MsHidUmdf.sys. The pass-through
 * driver handles HID class registration; we just respond to IOCTLs.
 *
 * UMDF2 differences from KMDF:
 *   - Compiles as a DLL, not .sys
 *   - Uses EvtIoDeviceControl (not InternalDeviceControl)
 *   - HID_XFER_PACKET is marshalled differently (IOCTL_UMDF_HID_*)
 *   - Includes <windows.h> instead of <ntddk.h>
 */

#ifndef DRIVER_H
#define DRIVER_H

#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <wdf.h>
#include <hidport.h>
#include <devpropdef.h>

/* Hard caps on the descriptor and report buffers the driver allocates.
 * Previously lived in include/hidmaestro.h alongside an old IOCTL-based
 * design that's now obsolete (the driver and SDK communicate via shared
 * memory, not IOCTLs). Inlined here so the obsolete header can go away. */
#define HIDMAESTRO_MAX_DESCRIPTOR_SIZE  4096
#define HIDMAESTRO_MAX_REPORT_SIZE      1024

/* Release frames in the delivery acknowledgement (HIDMAESTRO_INPUT_ACK). */
#define HIDMAESTRO_RELEASE_SLOTS         8
#define HIDMAESTRO_RELEASE_FRAME_CAP     80

/* v2.0.0: FeatureAnswers, REG_BINARY in SOFTWARE\HIDMaestro\Controller<N>,
 * the answers to GET_FEATURE the SDK compiles for one controller (the design
 * note's mechanism 4, FeatureStubTable.ToBlob). Every value little-endian.
 *
 * Header, 16 bytes: ULONG Length (the whole blob), ULONG Format (4), USHORT
 * EntryCount, UCHAR OnReportCount, UCHAR reserved, UCHAR OnReports[4], the
 * reports message entries follow.
 *
 * Entries, 16 bytes each (HM_FEATURE_ENTRY), then the arena: each entry's
 * message bytes, its sizes largest first, then its pages, Size bytes each.
 * An echo entry has no page and answers with the last SET on its report.
 *
 * The SDK carries the same limits (FeatureStubTable.Max*) and refuses a
 * model past one at load. A blob past one fails the start. */
#define HM_FEATURE_FORMAT           4
#define HM_FEATURE_HEADER           16
#define HM_FEATURE_ENTRY_SIZE       16
#define HM_FEATURE_MAX_ENTRIES      64
#define HM_FEATURE_MAX_DATA         64
#define HM_FEATURE_MAX_PAGES        16
#define HM_FEATURE_MAX_ONREPORTS    4
#define HM_FEATURE_MAX_ARENA        16384
#define HM_FEATURE_MAX_MESSAGE      8
#define HM_FEATURE_MAX_SIZES        4
#define HM_FEATURE_MAX_BLOB         (HM_FEATURE_HEADER + HM_FEATURE_MAX_ENTRIES * HM_FEATURE_ENTRY_SIZE + HM_FEATURE_MAX_ARENA)
#define HM_FEATURE_FLAG_MESSAGE     0x01
#define HM_FEATURE_FLAG_PARAM       0x02
#define HM_FEATURE_FLAG_ECHO        0x04
#define HM_FEATURE_FLAG_REPLY_HEAD  0x08
#define HM_FEATURE_FLAG_NOTIFY      0x10
#define HM_FEATURE_FLAGS_KNOWN      0x1F

#pragma pack(push, 1)
typedef struct _HM_FEATURE_ENTRY {
    UCHAR   Id;             /* the report ID the GET arrives on, 0 without IDs */
    UCHAR   Flags;          /* HM_FEATURE_FLAG_* */
    UCHAR   OnSlot;         /* index into OnReports, message entries */
    UCHAR   At;             /* the message's offset in the kept SET */
    UCHAR   ValueLength;    /* the message's length */
    UCHAR   ParamAt;        /* the parameter's offset, HM_FEATURE_FLAG_PARAM */
    UCHAR   ParamValue;
    UCHAR   PageCount;
    UCHAR   Size;           /* each page's length */
    UCHAR   MinLength;      /* a shorter request is too small */
    UCHAR   SizeCount;      /* the sizes served, largest first, or 0 */
    UCHAR   Reserved1;
    USHORT  ArenaOffset;    /* the message's first byte in the arena */
    USHORT  Reserved2;
} HM_FEATURE_ENTRY;
#pragma pack(pop)
C_ASSERT(sizeof(HM_FEATURE_ENTRY) == HM_FEATURE_ENTRY_SIZE);

/* v2.0.0: CachedInputReportIds, REG_BINARY beside it: the input report IDs
 * whose frames the polled GET_INPUT_REPORT answers from. Empty caches every
 * frame, as every earlier driver did. */
#define HM_CACHED_INPUT_IDS_MAX     16

/* v2.0.0: what the driver writes into DriverConfigError before it fails a
 * start under the version contract, naming the value it could not read. */
#define HIDMAESTRO_CONFIG_ERROR_FEATURE_ANSWERS     1
#define HIDMAESTRO_CONFIG_ERROR_CACHED_INPUT_IDS    2
#define HIDMAESTRO_CONFIG_ERROR_LIVE_FEATURE        3

/*
 * Common definitions shared with the vhidmini2 sample.
 * These must match what MsHidUmdf.sys expects.
 */
/*
 * v1.1.39: IOCTL_UMDF_HID_* corrected values per WDK
 * <hidport.h> (10.0.26100/km/hidport.h:196-200):
 *
 *   #define IOCTL_UMDF_HID_SET_FEATURE        HID_CTL_CODE(20)  = 0x000B0053
 *   #define IOCTL_UMDF_HID_GET_FEATURE        HID_CTL_CODE(21)  = 0x000B0057
 *   #define IOCTL_UMDF_HID_SET_OUTPUT_REPORT  HID_CTL_CODE(22)  = 0x000B005B
 *   #define IOCTL_UMDF_HID_GET_INPUT_REPORT   HID_CTL_CODE(23)  = 0x000B005F
 *
 * Pre-1.1.39, this header defined fabricated values (0x210003, 0x210007,
 * 0x21000B, 0x21000F) that DO NOT match what mshidumdf delivers. Every
 * SetFeature/GetFeature/SetOutputReport/GetInputReport handler we
 * shipped since v1.1.35 has compiled but never fired: the case
 * statement constants didn't match the framework's IoControlCode, so
 * dispatch fell through to default and returned STATUS_NOT_IMPLEMENTED.
 *
 * <hidport.h> already provides the right values via <hidclass.h> +
 * HID_CTL_CODE; we just include hidport.h in driver.h above. These
 * #ifndef guards are kept only as a safety net in case the WDK header
 * is unavailable, but should never actually fire on a current WDK.
 */
#ifndef IOCTL_UMDF_HID_SET_FEATURE
#define IOCTL_UMDF_HID_SET_FEATURE          HID_CTL_CODE(20)
#endif
#ifndef IOCTL_UMDF_HID_GET_FEATURE
#define IOCTL_UMDF_HID_GET_FEATURE          HID_CTL_CODE(21)
#endif
#ifndef IOCTL_UMDF_HID_SET_OUTPUT_REPORT
#define IOCTL_UMDF_HID_SET_OUTPUT_REPORT    HID_CTL_CODE(22)
#endif
#ifndef IOCTL_UMDF_HID_GET_INPUT_REPORT
#define IOCTL_UMDF_HID_GET_INPUT_REPORT     HID_CTL_CODE(23)
#endif

/* ------------------------------------------------------------------ */
/*  Device context                                                     */
/* ------------------------------------------------------------------ */

typedef struct _DEVICE_CONTEXT {

    WDFDEVICE   Device;

    /* HID report descriptor (set by user-mode, returned to HID class) */
    UCHAR   ReportDescriptor[HIDMAESTRO_MAX_DESCRIPTOR_SIZE];
    ULONG   ReportDescriptorSize;

    /* HID device descriptor (wraps ReportDescriptorSize) */
    HID_DESCRIPTOR          HidDescriptor;
    HID_DEVICE_ATTRIBUTES   HidDeviceAttributes;

    /* Input report byte length (Report ID + data, from HID caps) */
    ULONG   InputReportByteLength;

    /* The input report a data-only frame goes out as (0 if no Report
     * IDs): the SDK's InputReportId, or without it the first Report ID
     * item of the descriptor's first Application Collection. */
    UCHAR   FirstInputReportId;

    /* FirstInputReportId came from the registry's InputReportId. */
    BOOLEAN InputReportIdConfigured;

    /* v2.0.0: the descriptor declares report IDs, and each input report's
     * declared length, its ID byte included, by ID. Without IDs the one
     * input report's length is at [0]. Built from the descriptor at add
     * (BuildInputLengths). An extended frame goes out only at its report's
     * length. */
    BOOLEAN UsesReportIds;
    USHORT  InputLengths[256];

    /* v2.0.0: the version contract held at add (decision (c)): the version
     * section of the index the identity entry names speaks protocol 4 and
     * carries this devnode's identity tag. Only then does the driver read
     * the controller's v2.0.0 values. Set once, for the devnode's life. */
    BOOLEAN Contract;
    WCHAR   IdentityToken[40];

    /* v2.0.0: FeatureAnswers, parsed at add under the contract. The data
     * of the last SET_FEATURE on each report a message entry follows, up
     * to HM_FEATURE_MAX_DATA bytes, from the report ID for a descriptor
     * with IDs and from the first data byte for one without, and each
     * entry's next page. FeatureLock guards the kept data and the pages. */
    WDFWAITLOCK FeatureLock;
    ULONG   FeatureCount;
    ULONG   FeatureOnReportCount;
    UCHAR   FeatureOnReports[HM_FEATURE_MAX_ONREPORTS];
    HM_FEATURE_ENTRY FeatureEntries[HM_FEATURE_MAX_ENTRIES];
    UCHAR   FeatureArena[HM_FEATURE_MAX_ARENA];
    ULONG   FeatureArenaSize;
    UCHAR   FeatureKept[HM_FEATURE_MAX_ONREPORTS][HM_FEATURE_MAX_DATA];
    ULONG   FeatureKeptLength[HM_FEATURE_MAX_ONREPORTS];
    UCHAR   FeaturePage[HM_FEATURE_MAX_ENTRIES];

    /* v2.0.0: CachedInputReportIds, read under the contract. With a list,
     * the polled GET_INPUT_REPORT answers from PolledReport, which keeps
     * only frames of the listed IDs. Under InputLock. */
    ULONG   CachedInputIdCount;
    UCHAR   CachedInputIds[HM_CACHED_INPUT_IDS_MAX];
    UCHAR   PolledReport[HIDMAESTRO_MAX_REPORT_SIZE];
    ULONG   PolledReportSize;
    BOOLEAN PolledReportReady;

    /* The last shared-input SeqNo the worker read, sent or not. A frame
     * the driver does not send leaves SharedMemSeqNo and the cache as they
     * were. */
    ULONG   SeenSeqNo;

    /* v2.0.0: LiveFeatureReport, read under the contract: one plus the
     * ID of the feature report whose answer is the device's state, or 0
     * for none. The SDK writes that answer on each frame's data lane, and
     * the worker keeps the latest in LiveFeature, under InputLock, for
     * GET_FEATURE to copy out. On a descriptor without report IDs every
     * GET is report 0's. */
    ULONG   LiveFeatureReport;
    UCHAR   LiveFeature[256];
    ULONG   LiveFeatureSize;

    /* Latest raw input report for HID READ_REPORT (native descriptor format) */
    UCHAR   InputReport[HIDMAESTRO_MAX_REPORT_SIZE];
    ULONG   InputReportSize;
    BOOLEAN InputReportReady;

    /* Product string (returned on IOCTL_HID_GET_STRING / HID_STRING_ID_IPRODUCT) */
    WCHAR   ProductString[128];
    ULONG   ProductStringBytes; /* size in bytes including null terminator */

    /* Serial number string (returned on IOCTL_HID_GET_STRING / HID_STRING_ID_ISERIALNUMBER).
     * Built per-instance from ControllerIndex so SDL3/HIDAPI/PadForge can
     * disambiguate two virtual controllers that share VID/PID/ProductString
     * (e.g. 2× DualSense). Without a unique serial, hid_enumerate buckets
     * them as one device. */
    WCHAR   SerialString[64];
    ULONG   SerialStringBytes;

    /* Queues */
    WDFQUEUE    DefaultQueue;        /* Parallel: HID IOCTLs + our custom IOCTLs */
    WDFQUEUE    ManualQueue;         /* Manual: pending IOCTL_HID_READ_REPORT */

    /* Synchronization */
    WDFWAITLOCK InputLock;
    WDFWAITLOCK OutputLock;

    /* Shared memory data injection (bypasses upper filter drivers) */
    HANDLE  SharedMemHandle;     /* CreateFileMapping handle */
    PVOID   SharedMemPtr;        /* MapViewOfFile pointer */
    ULONG   SharedMemSeqNo;      /* Last sequence number we processed */

    /* Sequence-number gate for IOCTL_HID_READ_REPORT. The READ_REPORT
     * handler returns a cached input report immediately ONLY when there's
     * new data since the last delivery (SharedMemSeqNo > LastDeliveredSeqNo).
     * Otherwise the request is parked in ManualQueue and the worker thread
     * completes it on the next ProcessSharedInput tick. Without this gate,
     * HIDClass hammers READ_REPORT in a tight loop because every call
     * returns instantly with stale data: the original CPU saturation
     * culprit. */
    ULONG   LastDeliveredInputSeqNo;

    /* Event-driven IPC: SDK signals InputDataEvent after every seqlock write,
     * the worker thread (WorkerThread) waits on (StopEvent, InputDataEvent)
     * and processes frames. Replaces the old 1ms WdfTimer busy-poll which
     * saturated CPU cores at scale. A 50 ms safety timeout on WaitForMultiple
     * Objects keeps things moving if a signal is ever dropped. */
    HANDLE  InputDataEvent;      /* OpenEvent on Global\HIDMaestroInputEvent<N> */
    HANDLE  StopEvent;            /* Named: Global\HIDMaestroStopEvent<N>, or
                                  * unnamed when that name is unusable
                                  * (CreateHostEvent) */
    volatile LONG TearingDown;   /* issue #38: set by ReleaseIndexObjects
                                  * BEFORE it signals StopEvent. StopEvent is
                                  * a named object shared with every other
                                  * context at this index and with foreign
                                  * processes' sweeps, so a signal alone does
                                  * not mean THIS device is going away. The
                                  * worker exits only when this flag is set;
                                  * any other StopEvent wake is absorbed
                                  * (reset + recycle), so a live device can
                                  * never be left worker-less. */
    HANDLE  WorkerThread;        /* CreateThread handle */

    /* Delivery acknowledgement (issue #67). The SDK pre-creates
     * Global\HIDMaestroInputAck<N> (HIDMAESTRO_INPUT_ACK) and the auto-reset
     * Global\HIDMaestroInputAckEvent<N> beside the input section. When a
     * READ_REPORT completes with a frame, the driver notes that frame's
     * SeqNo (AckPending). When HID class sends its next READ_REPORT, which
     * it does only after its completion routine has put that frame in its
     * buffer, the driver stores the SeqNo and signals the event. A keyboard
     * or mouse writer holds the next frame until then, so a click's press,
     * a key tap and every relative delta arrive, and arrive in order:
     * acknowledging at completion let the next frame's completion overtake
     * this one's on a busy machine. Opened with the input section, best
     * effort. An SDK that does not create them gets the latest-wins
     * behavior every gamepad uses. Every access is under InputLock, which
     * the read path already holds. */
    BOOLEAN AckPending;
    ULONG   AckPendingSeqNo;
    HANDLE  InputAckMemHandle;
    volatile LONG *InputAckMemPtr;
    HANDLE  InputAckEvent;
    WCHAR   InputAckMappingName[64];  /* e.g. L"Global\\HIDMaestroInputAck0" */
    WCHAR   InputAckEventName[64];    /* e.g. L"Global\\HIDMaestroInputAckEvent0" */

    /* Owner watch (issue #67). The SDK holds the server end of
     * \\.\pipe\HIDMaestroOwner<N> for as long as the controller lives. The
     * worker connects and keeps one read pending, which completes when that
     * end closes, on Dispose or when the consumer's process ends however
     * it ends. The worker then queues the release frames from the
     * acknowledgement section. Opened and closed by the worker only. */
    HANDLE  OwnerPipe;
    HANDLE  OwnerReadEvent;               /* manual reset, the read's OVERLAPPED event */
    OVERLAPPED OwnerOverlapped;
    UCHAR   OwnerReadByte;
    WCHAR   OwnerPipeName[64];            /* e.g. L"\\\\.\\pipe\\HIDMaestroOwner0" */

    /* Version handshake (v1.13.0). The SDK creates
     * Global\HIDMaestroVersion<N> (HIDMAESTRO_VERSION_INFO) beside the input
     * section with its own half filled in. The driver writes its half each
     * time it opens the input section, then lets go of the section. An SDK
     * that predates it creates no section, and the open fails quietly. */
    WCHAR   VersionMappingName[64];       /* e.g. L"Global\\HIDMaestroVersion0" */

    /* Release frames waiting for a READ_REPORT, under InputLock. */
    UCHAR   ReleaseFrames[HIDMAESTRO_RELEASE_SLOTS][HIDMAESTRO_RELEASE_FRAME_CAP];
    ULONG   ReleaseSizes[HIDMAESTRO_RELEASE_SLOTS];
    ULONG   ReleaseCount;
    ULONG   ReleaseNext;

    WCHAR   InputEventName[64];  /* e.g. L"Global\\HIDMaestroInputEvent0" */
    WCHAR   StopEventName[64];   /* e.g. L"Global\\HIDMaestroStopEvent0" */
    WCHAR   OutputEventName[64]; /* e.g. L"Global\\HIDMaestroOutputEvent0" */

    /* Output-ring doorbell (issue #34). Auto-reset named event created at
     * EvtDeviceAdd; PublishOutput signals it after the ring Head is
     * published so the SDK's reader can block instead of polling every
     * 8 ms. NULL when creation failed; publish simply skips the signal
     * and an event-less SDK falls back to its poll cadence. While NULL the
     * worker tries again each time it maps the input section, on each idle
     * timeout and every 256 frames (TryCreateDoorbell). */
    HANDLE  OutputSignalEvent;

    /* Multi-instance: controller index (0, 1, 2, 3) */
    ULONG   ControllerIndex;
    WCHAR   ConfigRegPath[64];      /* e.g. L"SOFTWARE\\HIDMaestro\\Controller0" */
    WCHAR   SharedMappingName[64];  /* e.g. L"Global\\HIDMaestroInput0" */
    WCHAR   OutputMappingName[64];  /* e.g. L"Global\\HIDMaestroOutput0" */

    /* Output channel: host→device pass-through (rumble, haptics, FFB, LED).
     * Driver/companion are dumb pass-throughs; the consumer (PadForge or test
     * app) opens this section read-only and decodes by (Source, ReportId). */
    HANDLE  OutputMemHandle;
    PVOID   OutputMemPtr;
    ULONG   OutputWriteCount;       /* Stale-detection: writes since last re-open (#2) */

    /* PID FFB state channel. Consumer→driver state for HidD_GetFeature
     * responses on the canonical PID Report IDs (0x12 Block Load, 0x13
     * Pool, 0x14 State). Driver reads via seqlock on IOCTL_UMDF_HID_GET_
     * FEATURE; consumer writes via HMController.PublishPid* methods.
     * Lazy-mapped: NULL until the SDK creates Global\HIDMaestroPidState<N>. */
    HANDLE  PidStateMemHandle;
    PVOID   PidStateMemPtr;
    WCHAR   PidStateMappingName[64]; /* e.g. L"Global\\HIDMaestroPidState0" */

} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

/* Shared memory layout: written by user-mode, read by driver
 * Contains BOTH native HID report data AND GIP data for XUSB GET_STATE.
 * Total size: 4 + 4 + 256 + 14 = 278 bytes.
 *
 * Data[] is 256 bytes to cover every HID input report size in the profile
 * database without truncation. DualSense BT report 0x31 is 78 bytes;
 * Switch Pro standard input reports can run to ~64; gyro/accelerometer
 * passthrough on these pads writes motion values LATE in the report, so
 * the prior 64-byte pipe clipped exactly the motion fields consumers
 * (Dolphin, Cemu, yuzu/Citron, RetroArch) need. 256 matches the mirror
 * in SHARED_OUTPUT and gives headroom for custom profile descriptors. */
#pragma pack(push, 1)
typedef struct _HIDMAESTRO_SHARED_INPUT {
    volatile ULONG  SeqNo;           /* Incremented each write */
    ULONG           DataSize;        /* HID input report data size (excluding Report ID) */
    UCHAR           Data[256];       /* HID input report data (native descriptor format) */
    UCHAR           GipData[14];     /* GIP-format data for XUSB GET_STATE (always 14 bytes) */
    /* v1.3.5: vendor-blob mode-switch path. Driver passes
     * ExtendedReportData verbatim when ExtendedReportSize > 0. */
    ULONG           ExtendedReportSize;
    UCHAR           ExtendedReportData[80];
} HIDMAESTRO_SHARED_INPUT, *PHIDMAESTRO_SHARED_INPUT;
#pragma pack(pop)

/* Delivery acknowledgement, Global\HIDMaestroInputAck<N> (issue #67). A
 * separate section, so no existing layout changes: readers of the input,
 * output and PID sections never see it. The SDK creates and zeroes it, and the
 * driver sets Capabilities when it opens the section and stores each
 * delivered frame's SeqNo in DeliveredSeqNo. A writer that finds
 * Capabilities zero is talking to a driver without acknowledgement and
 * must not wait for one.
 *
 * Release frames: for a keyboard or mouse, the SDK keeps here, per input
 * report, the report that lets go of everything the last one held (keys and
 * buttons up, relative motion zero, absolute axes where they were). The
 * driver sends them when the consumer's end of the owner pipe closes,
 * whether by Dispose or because its process ended, so a key held at that
 * moment does not repeat until the device is removed. ReleaseSeq is a
 * seqlock, odd while the SDK rewrites the frames. */
#define HIDMAESTRO_INPUT_ACK_CAP_DELIVERED  0x00000001u
#define HIDMAESTRO_INPUT_ACK_CAP_RELEASE    0x00000002u  /* owner pipe is watched */
/* Driver state, not a capability. The driver sets it in Capabilities when
 * the owner watch it held ends (OwnerGone) and connects a watch only while
 * it is clear. A program that starts on the index creates this section and
 * zeroes it, which clears the bit. A program that is not elevated can only
 * open the section for reading, so it cannot. The SDK's census reads it
 * (OwnerCensus.LegacyPipeLive). No SDK tests it as a capability. */
#define HIDMAESTRO_INPUT_ACK_WATCH_ENDED    0x80000000u
#pragma pack(push, 1)
typedef struct _HIDMAESTRO_RELEASE_FRAME {
    ULONG           Size;
    UCHAR           Data[HIDMAESTRO_RELEASE_FRAME_CAP];   /* report ID first when used */
} HIDMAESTRO_RELEASE_FRAME;

typedef struct _HIDMAESTRO_INPUT_ACK {
    volatile LONG   DeliveredSeqNo;  /* SeqNo of the last frame a READ_REPORT carried */
    volatile LONG   Capabilities;    /* HIDMAESTRO_INPUT_ACK_CAP_* */
    volatile LONG   ReleaseSeq;
    volatile LONG   ReleaseCount;    /* frames in Release, at most HIDMAESTRO_RELEASE_SLOTS */
    HIDMAESTRO_RELEASE_FRAME Release[HIDMAESTRO_RELEASE_SLOTS];
} HIDMAESTRO_INPUT_ACK, *PHIDMAESTRO_INPUT_ACK;

#pragma pack(pop)

/* The version contract both drivers share: the version section, the
 * identity tag and the identity entry. */
#include "hm_contract.h"

/* Output passthrough section: written by driver/companion, read by consumer.
 * Game sends rumble/haptics/FFB → we capture bytes and publish here.
 * Latest-wins (no ring): old commands are stale and shouldn't be replayed.
 * Total size: 4 + 1 + 1 + 2 + 256 = 264 bytes
 *
 * Source enum tells the consumer which API the game used:
 *   0 = HID output report  (HidD_SetOutputReport, dinput8 PID, HIDAPI write)
 *   1 = HID feature report (HidD_SetFeature)
 *   2 = XInput rumble      (XInputSetState: companion-side)
 *
 * For Source=HID*, ReportId is the HID Report ID byte (0 if none).
 * For Source=XInputRumble, ReportId is reserved (0); Data is the 5-byte
 *   XINPUT_VIBRATION-style payload from the IOCTL_XUSB_SET_STATE input buffer.
 *
 * Consumer is expected to interpret bytes per (profile, Source, ReportId).
 * The driver does NOT classify rumble vs haptic vs adaptive trigger: that
 * distinction is semantic and lives in the consumer. */
#define HIDMAESTRO_OUTPUT_SOURCE_HID_OUTPUT       0
#define HIDMAESTRO_OUTPUT_SOURCE_HID_FEATURE      1
#define HIDMAESTRO_OUTPUT_SOURCE_XINPUT           2
/* v1.3.5: feature READ (Get_Feature). Notifies the SDK that the host
 * issued IOCTL_HID_GET_FEATURE for this report ID. Used by the
 * extendedReport.armOn watcher to flip vendor-blob emission on Sony BT. */
#define HIDMAESTRO_OUTPUT_SOURCE_HID_FEATURE_READ 3

/* Output channel: RING BUFFER as of v1.1.40.
 *
 * Pre-1.1.40 was a single slot, latest-write-wins. That coalesced
 * pid.dll's tight three-packet PID FFB write bursts (Set Effect,
 * Set Constant Force / Set Periodic, Effect Operation Start) within
 * 1-3 ms vs the SDK's 8 ms poll interval: middle packets dropped,
 * magnitude never reached the consumer. See issue #16.
 *
 * Now: 64-slot ring with monotonic seqlock per slot. Writer (this
 * driver, multiple threads via OutputLock) increments a global Head
 * counter, writes slot[(Head-1) % N], readers (the SDK's poll loop)
 * track LastSeen and process slots from LastSeen+1 to Head.
 *
 * Slot capacity 256 bytes covers DualSense reports (78 bytes) and
 * any current PID FFB Output (largest is Set Effect at 22 bytes).
 *
 * Single-producer (driver IOCTL handler thread) → single-consumer
 * (SDK poll thread) ring; OutputLock serializes producer races.
 * Reader uses per-slot SeqNo for torn-write detection within a slot.
 * If reader falls behind by more than N writes, the oldest packets
 * get overwritten: that's a real lossy edge case, but with N=64
 * and a 1-3 ms burst pattern it would take a 512 ms reader stall
 * to start losing packets. */
#define HIDMAESTRO_OUTPUT_RING_SLOTS     64u
#define HIDMAESTRO_OUTPUT_SLOT_DATA_CAP  256u

#pragma pack(push, 1)
typedef struct _HIDMAESTRO_OUTPUT_SLOT {
    volatile ULONG  SeqNo;           /* Per-slot. Equal to Head value at the time of write. */
    UCHAR           Source;          /* HIDMAESTRO_OUTPUT_SOURCE_* */
    UCHAR           ReportId;        /* HID Report ID (0 if descriptor uses no IDs) */
    USHORT          DataSize;        /* Bytes valid in Data[] */
    UCHAR           Data[HIDMAESTRO_OUTPUT_SLOT_DATA_CAP];
} HIDMAESTRO_OUTPUT_SLOT, *PHIDMAESTRO_OUTPUT_SLOT;

typedef struct _HIDMAESTRO_SHARED_OUTPUT {
    /* Monotonically-increasing total writes by the driver. The slot at
     * index ((Head - 1) % HIDMAESTRO_OUTPUT_RING_SLOTS) holds the most
     * recent write. SeqNo=0 is reserved for "never written": first
     * write is SeqNo=1. */
    volatile ULONG  Head;

    /* Reserved for future use (e.g., a layout version field if the slot
     * struct ever grows again). */
    ULONG           _Reserved;

    HIDMAESTRO_OUTPUT_SLOT Slots[HIDMAESTRO_OUTPUT_RING_SLOTS];
} HIDMAESTRO_SHARED_OUTPUT, *PHIDMAESTRO_SHARED_OUTPUT;
#pragma pack(pop)

/* PID Force-Feedback state section: written by SDK consumer
 * (HMController.PublishPid*), read by driver on IOCTL_UMDF_HID_GET_FEATURE.
 * Single-slot last-write-wins per the HID PID 1.0 spec (Block Load Report
 * is "the result of the most recent Create New Effect"). Mirrors vJoy's
 * device-extension PID storage shape, but on the user side of the shared
 * mapping because HIDMaestro is UMDF2 (driver runs in user-mode WUDFHost).
 *
 * PidEnabled gate: zero-initialized when the SDK creates the section.
 * First call to HMController.PublishPidPool flips it to 1, atomic with
 * the Pool fields write under the same seqlock cycle. Driver checks
 * PidEnabled before reading any other field: when 0, returns
 * STATUS_NO_SUCH_DEVICE for Pool (matches vJoy's "FFB not enabled"
 * convention) and STATUS_NOT_SUPPORTED for Block Load / State.
 *
 * Wire layout matches the PID 1.0 report formats so the driver can copy
 * fields straight into the IOCTL output buffer with minimal packing. */
#pragma pack(push, 1)
typedef struct _HIDMAESTRO_SHARED_PID_STATE {
    volatile ULONG  SeqNo;           /* Incremented each write: seqlock */
    UCHAR           PidEnabled;      /* 0 until first PublishPidPool */
    UCHAR           _pad0[3];

    /* Block Load Report (Report ID 0x12, per HID PID 1.0 §5.5) */
    UCHAR           BL_EffectBlockIndex;
    UCHAR           BL_LoadStatus;        /* 1=Success, 2=Full, 3=Error */
    USHORT          BL_RAMPoolAvailable;

    /* PID Pool Report (Report ID 0x13, per HID PID 1.0 §5.7) */
    USHORT          Pool_RAMPoolSize;
    UCHAR           Pool_MaxSimultaneousEffects;
    UCHAR           Pool_MemoryManagement;  /* bit0=DeviceManagedPool, bit1=SharedParamBlocks */

    /* PID State Report (Report ID 0x14, per HID PID 1.0 §5.8) */
    UCHAR           State_EffectBlockIndex;
    UCHAR           State_Flags;
    UCHAR           _pad1[2];

    /* v1.1.37: driver-side EBI free-list. Bit N (0..31) set ⇒ EBI N+1
     * is allocated. Driver atomically allocates next free EBI on
     * SetFeature(0x11 Create New Effect) inside the IOCTL handler so
     * dinput8's follow-up GetFeature(0x12) reads consistent state.
     * Mirrors vJoy's `Ffb_GetNextFreeEffect`. The 32-bit width caps
     * us at 32 simultaneous effects, well above any consumer-pool
     * value we'd realistically advertise via Pool_MaxSimultaneousEffects.
     *
     * Update protocol: driver uses InterlockedOr/And to flip bits;
     * the field is OUTSIDE the seqlock'd block intentionally so EBI
     * alloc/free never has to synchronize with PublishPid* writers.
     * BL_* fields ARE inside the seqlock and the driver writes them
     * atomically with SeqNo increment after a successful allocation. */
    volatile ULONG  EbiAllocBitmap;

    /* v1.1.37: driver tracks EBI count for fast pool-exhaustion check.
     * Updated atomically alongside EbiAllocBitmap. */
    volatile ULONG  EbiAllocatedCount;

    /* v1.3.7: descriptor-driven PID Report ID overrides. SDK populates
     * these by walking the profile's HID descriptor at controller-create
     * time (see PidReportIdExtractor) so the driver can serve PID
     * Pool/State/BlockLoad and dispatch SetFeature handlers for vendor
     * descriptors that use non-canonical Report IDs.
     *
     * Example: real Microsoft SideWinder Force Feedback 2 firmware uses
     * Pool=0x03, BlockLoad=0x02, Set Effect=0x01 (Microsoft's chosen
     * IDs from the SideWinder design pre-PID-1.0-spec); dinput8
     * enumerates by usage and queries HidD_GetFeature on those exact
     * RIDs. Pre-v1.3.7 the driver hardcoded 0x12/0x13/0x14 and any
     * non-canonical RID hit STATUS_NOT_SUPPORTED, breaking FFB
     * enumeration on the SideWinder virtual.
     *
     * Zero means "use canonical default": every existing profile built
     * via HidDescriptorBuilder.AddPidFfbBlock keeps the original 0x11/
     * 0x12/0x13/0x14/0x1B/0x1C IDs and unchanged behavior. */
    UCHAR PoolReportId;
    UCHAR StateReportId;
    UCHAR BlockLoadReportId;
    UCHAR CreateNewEffectReportId;
    UCHAR BlockFreeReportId;
    UCHAR DeviceControlReportId;
    UCHAR _pad2[2];
} HIDMAESTRO_SHARED_PID_STATE, *PHIDMAESTRO_SHARED_PID_STATE;
#pragma pack(pop)

/* Default PID Report IDs per the canonical HID PID 1.0 descriptor.
 * Profiles may use different IDs; the driver falls back to STATUS_NOT_SUPPORTED
 * for unknown IDs, preserving today's behavior. */
#define HIDMAESTRO_PID_CREATE_NEW_EFFECT_REPORT_ID  0x11
/* PID Block Free Report: 0x1B (NOT 0x1F as v1.1.37 had).
 * vJoy emits BLKFRREP+0x10*TLID = 0x0B+0x10 = 0x1B for TLID=1
 * (vJoy-Brunner/driver/sys/hidReportDescSingle.h:558). PadForge's
 * transcribed descriptor uses the same ID at HMaestroFfbDescriptor.cs:220.
 * The v1.1.37 0x1F constant was a guess and FreeEbi was dead code on
 * the hot path. */
#define HIDMAESTRO_PID_BLOCK_FREE_REPORT_ID         0x1B
/* PID Device Control Report: 0x1C. vJoy handles CTRL_DEVRST=4 by
 * resetting the entire PID state (vJoy-Brunner/driver/sys/hid.c:2849).
 * dinput8's IDirectInputDevice8::SendForceFeedbackCommand(DISFFC_RESET)
 * lands here. */
#define HIDMAESTRO_PID_DEVICE_CONTROL_REPORT_ID     0x1C
#define HIDMAESTRO_PID_BLOCK_LOAD_REPORT_ID  0x12
#define HIDMAESTRO_PID_POOL_REPORT_ID        0x13
#define HIDMAESTRO_PID_STATE_REPORT_ID       0x14

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetDeviceContext)

/* ------------------------------------------------------------------ */
/*  Function prototypes                                                */
/* ------------------------------------------------------------------ */

DRIVER_INITIALIZE                   DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD           EvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL  EvtIoDeviceControl;

#endif /* DRIVER_H */
