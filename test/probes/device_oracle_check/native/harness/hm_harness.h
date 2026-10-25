/* The driver harness: driver.c and companion.c compiled natively, driven
 * through their WDF entry points by scripted and seeded scenarios, single
 * threaded and deterministic. This header is the harness's own: the object
 * model behind the WDF table and the Win32 stand-ins, the recorder, the
 * plan the probe writes, and the scenario helpers.
 *
 * One thread runs at a time (review F5). Each request completes inside the
 * scenario's call, the driver's threads are fibers that run only between
 * the scenario's steps, and a lock stand-in catches reentry and a switch
 * while held, nothing else. A replay therefore proves no locking: a change
 * that moves work in or out of a lock replays identically. The sites whose
 * order or atomicity only real threads exercise:
 *   driver.c:1307-1323   Ds3HandleSixaxisOutput drops InputLock before
 *                        PublishOutput takes OutputLock
 *   driver.c:1495-1512   SwitchQueueReply takes the manual queue under
 *                        InputLock, where ProcessSharedInput (1997) and
 *                        SwitchStreamProc (1862) take it outside
 *   driver.c:1628-1632, 1853-1859
 *                        the Switch timer byte, incremented under InputLock
 *                        from an IOCTL thread and the stream thread
 *   driver.c:1564-1565, 1613-1614, 1651-1656, 1684-1686, 1822, 1829
 *                        SwitchProtocolSeen, SwitchInputMode and
 *                        SwitchImuEnabled, written by IOCTL threads and read
 *                        by the stream thread with no lock
 *   driver.c:1336-1354   ReadSharedInput's seqlock against the SDK's writer,
 *                        which here writes whole frames between steps
 *   driver.c:1111-1123   the PID allocator's interlocked bitmap
 *   driver.c:590-596, 657-676
 *                        the doorbell's publication and the acknowledgement
 *                        mapping the read path uses under InputLock
 *   driver.c:2320-2337   cleanup's 2 s joins against a request in flight
 *   companion.c:171-174, 667, 719
 *                        GipLock across the timer, the pump and GET_STATE,
 *                        with PacketCount incremented outside it
 *   companion.c:344-351, 953-970
 *                        the doorbell's compare-exchange and the rumble
 *                        gate's cached vendor check */
#pragma once

#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <wdf.h>
#include <hidport.h>
#include <devpropdef.h>
#include <sddl.h>
#include <aclapi.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "hm_api.h"

/* ------------------------------------------------------------------ */
/*  Failure                                                            */
/* ------------------------------------------------------------------ */

/* Stops the run: the message goes to stderr, the exit code is 3, and the
 * output file keeps no end record, so the probe cannot mistake it for a
 * complete recording. */
__declspec(noreturn) void HmFail(const char *fmt, ...);

/* ------------------------------------------------------------------ */
/*  Deterministic random numbers: SplitMix64, as Rng.cs                */
/* ------------------------------------------------------------------ */

typedef struct HmRng { uint64_t state; } HmRng;
void     RngInit(HmRng *r, uint64_t seed, uint64_t stream);
uint64_t RngU64(HmRng *r);
uint32_t RngU32(HmRng *r);
int      RngNext(HmRng *r, int max);
int      RngBool(HmRng *r);
uint8_t  RngByte(HmRng *r);
void     RngFill(HmRng *r, uint8_t *dst, size_t n);

/* ------------------------------------------------------------------ */
/*  Recorder: the NativeStreamReader format of Golden.cs               */
/* ------------------------------------------------------------------ */

void RecOpen(const char *path);
void RecStream(const char *fmt, ...);
void RecOut(const char *tag, const void *data, uint32_t len);
void RecEnd(void);
/* Records every output-ring slot published since the last call, so ring
 * slots appear in the stream in the order the driver wrote them. Called
 * before every other record and on each doorbell signal. */
void RecFlushRing(void);
/* The driver stored a delivered frame's SeqNo and signaled the
 * acknowledgement event: records the SeqNo it stored. */
void RecordAck(void);
uint64_t RecOutputCount(void);
uint32_t RecStreamCount(void);

/* ------------------------------------------------------------------ */
/*  Clock                                                              */
/* ------------------------------------------------------------------ */

/* Milliseconds since the stream began. GetTickCount64 reads it, and
 * GetSystemTimeAsFileTime reads HM_FILETIME_BASE plus it. */
extern uint64_t g_clockMs;
/* 2026-10-15 00:00:00 UTC as a FILETIME. */
#define HM_FILETIME_BASE 0x01DD5C381F0F4000ULL

/* ------------------------------------------------------------------ */
/*  Registry                                                           */
/* ------------------------------------------------------------------ */

typedef struct HmRegValue {
    struct HmRegValue *next;
    WCHAR    name[64];
    DWORD    type;
    DWORD    size;
    uint8_t *data;
} HmRegValue;

typedef struct HmRegKey {
    struct HmRegKey *next;
    WCHAR       path[160];     /* below HKLM, e.g. SOFTWARE\HIDMaestro\Controller0 */
    HmRegValue *values;
} HmRegKey;

HmRegKey   *KeyFind(const WCHAR *path);
HmRegKey   *KeyMake(const WCHAR *path);
void        KeyDrop(const WCHAR *path);
HmRegValue *ValueFind(HmRegKey *key, const WCHAR *name);
void        ValueSet(HmRegKey *key, const WCHAR *name, DWORD type, const void *data, DWORD size);
int         ValueDrop(HmRegKey *key, const WCHAR *name);
void        KeyFree(HmRegKey *key);
/* A key that belongs to a device node (its hardware key), outside the
 * HKLM namespace. */
HmRegKey   *KeyNewDetached(const WCHAR *label);

/* ------------------------------------------------------------------ */
/*  Kernel objects and handles                                         */
/* ------------------------------------------------------------------ */

enum HmObjKind { HK_EVENT = 1, HK_SECTION, HK_THREAD, HK_KEY, HK_PIPE_SERVER, HK_PIPE_CLIENT };

/* Who owns a named object, for GetSecurityInfo. A driver host runs as
 * LocalService. The SDK runs elevated, so its objects belong to
 * Administrators. */
enum HmOwner { OWN_LOCALSERVICE = 1, OWN_SYSTEM, OWN_ADMINS, OWN_USER };

typedef struct HmObj {
    struct HmObj *nextAll;
    int      kind;
    int      refs;            /* open handles plus the scenario's holds */
    WCHAR    name[96];        /* empty for an unnamed object */
    int      owner;           /* HmOwner */
    int      hasDacl;         /* FALSE: a NULL DACL */
} HmObj;

typedef struct HmEvent {
    HmObj    o;
    BOOL     manual;
    BOOL     signaled;
    int      role;            /* HmEventRole: what a signal means to the recorder */
} HmEvent;

enum HmEventRole { EV_PLAIN = 0, EV_DOORBELL, EV_ACK };

typedef struct HmSection {
    HmObj    o;
    size_t   size;
    uint8_t *buf;
    int      views;           /* the process's mapped views of it */
} HmSection;

typedef struct HmThread {
    HmObj    o;
    LPTHREAD_START_ROUTINE start;
    void    *param;
    void    *fiber;
    int      started, exited;
    DWORD    exitCode;
    DWORD    waitCount;
    HANDLE   waitHandles[MAXIMUM_WAIT_OBJECTS];
    DWORD    waitTimeout;
    uint64_t waitStartMs;      /* the harness clock when the wait began */
    DWORD    result;
    int      index;           /* creation order on its device */
} HmThread;

typedef struct HmPipeServer {
    HmObj    o;
    int      closed;
    ULONG    processId;
} HmPipeServer;

typedef struct HmPipeClient {
    HmObj    o;
    HmPipeServer *server;
    LPOVERLAPPED  pending;    /* the read the driver keeps pending */
    int      canceled;
} HmPipeClient;

typedef struct HmKeyHandle {
    HmObj    o;
    HmRegKey *key;
} HmKeyHandle;

HANDLE   ObjHandle(HmObj *o);           /* a new handle to the object */
HmObj   *ObjFromHandle(HANDLE h);       /* NULL for no such handle */
HmObj   *ObjFindNamed(const WCHAR *name);
void     ObjRelease(HmObj *o);          /* drops one reference */
HmEvent *EventNew(const WCHAR *name, BOOL manual, BOOL initial, int owner, int hasDacl);
void     EventSet(HmEvent *e);
HmSection *SectionNew(const WCHAR *name, size_t size);
HmPipeServer *PipeServerNew(const WCHAR *name, ULONG processId);
void     PipeServerClose(HmPipeServer *s);

/* ------------------------------------------------------------------ */
/*  Threads as fibers                                                  */
/* ------------------------------------------------------------------ */

void     FiberInit(void);
/* Runs the thread until it waits or returns. A thread that has not
 * started starts here. A WAIT_TIMEOUT answer stops the run when the wait
 * is INFINITE or the clock has not reached the wait's deadline. */
void     ThreadRun(HmThread *t, DWORD result);
/* The wait result the thread's current wait gets from the objects'
 * states now: the first signaled handle (an auto-reset event is reset by
 * it), else WAIT_TIMEOUT with the clock run to the wait's deadline. */
DWORD    ThreadAutoResult(HmThread *t);
void     ThreadResume(HmThread *t);    /* ThreadRun with ThreadAutoResult */
/* Locks held across a fiber switch would be a deadlock on real threads:
 * the stand-ins count them and a switch with any held stops the run. */
extern int g_locksHeld;

/* ------------------------------------------------------------------ */
/*  WDF objects                                                        */
/* ------------------------------------------------------------------ */

enum HmWdfKind { WK_DRIVER = 0x100, WK_DEVICE, WK_QUEUE, WK_REQUEST, WK_MEMORY, WK_LOCK, WK_KEY, WK_TIMER, WK_INIT };

typedef struct HmRequest HmRequest;
typedef struct HmDevice HmDevice;

typedef struct HmQueue {
    int        kind;
    HmDevice  *dev;
    WDF_IO_QUEUE_DISPATCH_TYPE type;
    PFN_WDF_IO_QUEUE_IO_DEVICE_CONTROL ioctl;
    HmRequest *head, *tail;
    int        count;
    int        powerManaged;  /* as the framework decides it (fxioqueue.cpp:397-414) */
    int        purged;        /* WdfIoQueuePurgeSynchronously ran: new requests fail */
} HmQueue;

typedef struct HmMemory {
    int       kind;
    uint8_t  *buf;
    size_t    len;
} HmMemory;

struct HmRequest {
    int        kind;
    HmRequest *next;
    HmRequest *nextAll;
    HmQueue   *queue;        /* the queue it waits in, or last left */
    int        inQueue;
    ULONG      code;
    uint8_t   *in;
    size_t     inLen;
    uint8_t   *out;
    size_t     outLen;
    int        noAccess;     /* a write request: outLen is the report id, out a no-access page */
    ULONG_PTR  info;
    int        completed;
    int        refused;      /* a purged queue failed it before the driver saw it */
    uint32_t   serial;
    char       tag[24];
    HmMemory   outMem;
};

typedef struct HmTimer {
    int        kind;
    HmDevice  *dev;
    PFN_WDF_TIMER fn;
    ULONG      period;
    int        started;
} HmTimer;

typedef struct HmWdfKey {
    int        kind;
    HmRegKey  *key;
} HmWdfKey;

typedef struct HmDeviceInit {
    int        kind;
    int        filter;
    WCHAR      instanceId[128];  /* DEVPKEY_Device_InstanceId's value */
    HmRegKey  *hwKey;            /* the device node's hardware key and its */
    HmRegKey  *hwSubKey;         /* service-name subkey (WdfFdoInitOpenRegistryKey) */
} HmDeviceInit;

/* "HM_" and the index in four digits, the token DeviceIdentity.ForIndex
 * gives a controller made without a key (DeviceIdentity.cs:94): HM_0000
 * for index 0. */
void HarnessToken(int index, WCHAR *out, size_t count);
/* The identity token of the controller at an index: the one
 * ControllerTokenSet gave it in this stream, else HarnessToken's. A keyed
 * controller's token is "HM_" and 16 uppercase hex digits
 * (DeviceIdentity.cs:94), and DeviceIdentity.ForKey gives a key of the
 * default form "index:N" the token of index N at any index (:131), so
 * neither form need name the index. */
void ControllerToken(int index, WCHAR *out, size_t count);
void ControllerTokenSet(int index, const WCHAR *token);
/* The identity tag of the design note's version contract: the 64-bit
 * FNV-1a hash of the token's UTF-16LE code units, no terminator. */
uint64_t IdentityTag(const WCHAR *token);
/* The device node the next DeviceAdd adds: the profile it was made for
 * (NULL for none) and the controller index whose token it carries.
 * ConfigWrite sets both, since DeviceOrchestrator makes the devnode after
 * it writes the configuration. */
extern struct PlanProfile *g_nextDevnodeProfile;
extern int g_nextDevnodeIndex;
/* The instance ID the SDK gives that device node, which
 * WdfFdoInitQueryPropertyEx returns for DEVPKEY_Device_InstanceId: for the
 * companion SWD\HIDMAESTRO\<token> (DeviceOrchestrator.cs:945-954), for
 * the HID device its profile's path from the plan's devnode line
 * (Case5Driver.DevnodePrefix), or ROOT\HIDCLASS with no profile, then the
 * token. */
void DevnodeInstanceId(struct PlanProfile *p, int index, int companion, WCHAR *out, size_t count);
/* Drops the tokens ControllerTokenSet gave and the next device node, at
 * the world's reset. */
void IdentityReset(void);
/* WdfFdoInitQueryPropertyEx on an init carrying that instance ID: a wrong
 * structure size, another key, and the instance ID at buffer lengths 0,
 * two bytes short, exact and eight over, each recorded. */
void InstanceIdProbe(const WCHAR *instanceId);
/* WdfFdoInitOpenRegistryKey on an init whose hardware key holds
 * ControllerIndex: each row of UMDF's access table, read masks inside
 * KEY_READ and GENERIC_READ, and masks outside the table, each open
 * recorded as registry.open and, when it opened, the index read through
 * the key. */
void FdoRegistryProbe(void);

struct HmDevice {
    int        kind;
    PCWDF_OBJECT_CONTEXT_TYPE_INFO typeInfo;
    void      *context;
    PFN_WDF_OBJECT_CONTEXT_CLEANUP cleanup;
    HmQueue   *defaultQueue;
    HmQueue   *queues[4];
    int        queueCount;
    HmTimer   *timers[2];
    int        timerCount;
    HmThread  *threads[4];
    int        threadCount;
    HmRegKey  *hwKey;         /* PLUGPLAY_REGKEY_DEVICE */
    HmRegKey  *hwSubKey;      /* the same with WDF_REGKEY_DEVICE_SUBKEY */
    int        removed;
    int        filter;        /* WdfFdoInitSetFilter ran before the create */
    WDF_PNPPOWER_EVENT_CALLBACKS pnp;   /* WdfDeviceInitSetPnpPowerEventCallbacks */
    int        started;       /* IRP_MN_START_DEVICE completed */
    int        stopped;       /* a rebalance stop, not yet restarted */
    int        surprised;     /* IRP_MN_SURPRISE_REMOVAL processed */
    int        smioStarted;   /* the self-managed I/O machine left Created */
};

void      WdfInit(void);
/* The device node the next device add creates: its hardware key, which
 * the SDK fills with ControllerIndex (DeviceNodeCreator). */
extern HmRegKey *g_nextHwKey;
extern HmRegKey *g_nextHwSubKey;
/* DriverEntry then the add callback, as the framework calls them. NULL
 * when the add failed. The status is recorded either way. */
HmDevice *DeviceAdd(int companion);
/* The PnP IRPs, each as the framework turns it into driver callbacks for
 * a started FDO (Windows-Driver-Frameworks, src/framework/shared/
 * irphandlers/pnp). Only the callbacks a driver registered run. The power
 * policy owner's idle settings are not modeled: no device here idles.
 *
 * DeviceStart: IRP_MN_START_DEVICE. EvtDevicePrepareHardware, then
 * EvtDeviceD0Entry from WdfPowerDeviceD3Final, then the self-managed I/O
 * machine's Init on the first start and Restart after a stop
 * (selfmanagediostatemachine.cpp:38-60).
 *
 * DeviceStop: IRP_MN_QUERY_STOP_DEVICE and IRP_MN_STOP_DEVICE, the
 * rebalance. EvtDeviceSelfManagedIoSuspend, EvtDeviceD0ExitPreInterrupts-
 * Disabled and EvtDeviceD0Exit to WdfPowerDeviceD3Final
 * (powerstatemachine.cpp:2562-2632), EvtDeviceReleaseHardware
 * (pnpstatemachine.cpp:2542). No flush, no purge, no cleanup.
 *
 * DeviceSurpriseRemove: IRP_MN_SURPRISE_REMOVAL on a started device.
 * EvtDeviceSurpriseRemoval (pnpstatemachine.cpp:2779), the same power
 * down as a stop, EvtDeviceReleaseHardware (:2871), then
 * PnpCleanupForRemove: the power-managed queues purged and their requests
 * waited for, then EvtDeviceSelfManagedIoFlush (:2256-2264). The device
 * object stays. Nothing more runs until an IRP_MN_REMOVE_DEVICE.
 *
 * DeviceRemove: IRP_MN_REMOVE_DEVICE. On a started device not surprise
 * removed, the power down, EvtDeviceReleaseHardware and PnpCleanupForRemove
 * first (pnpstatemachine.cpp:2325-2339). Then, for every device,
 * PnpEventRemovedCommonCode (:1995-2058): the queues that are not power
 * managed purged, EvtDeviceSelfManagedIoCleanup, then the early dispose,
 * which runs every child's rundown, a timer's stop among them
 * (fxtimer.cpp:478-498), before the device's context cleanup callback
 * (fxobjectstatemachine.cpp:1183-1215). A device never started runs only
 * the purge and the cleanup, which is all a device the scenarios do not
 * start sees. The canceled requests are the framework's work, not the
 * driver's, and are not recorded. */
void      DeviceStart(HmDevice *dev);
void      DeviceStop(HmDevice *dev);
void      DeviceSurpriseRemove(HmDevice *dev);
void      DeviceRemove(HmDevice *dev);
/* A request as MsHidUmdf delivers it, dispatched to the device's default
 * queue. The output buffer starts as outInit (outLen bytes) or zeros. */
HmRequest *Ioctl(HmDevice *dev, const char *tag, ULONG code,
                 const void *in, size_t inLen, size_t outLen, const void *outInit, size_t outInitLen);
int       QueueDepth(HmQueue *q);
/* Set by a script run: completions record the whole output buffer rather
 * than its bytes up to the information. */
extern int g_fullBuffers;
/* Runs an input script (hm_script.c). */
int       ScriptRun(const char *plan, const char *script, const char *output);
void      WdfReset(void);

/* ------------------------------------------------------------------ */
/*  World                                                              */
/* ------------------------------------------------------------------ */

/* Frees every object, handle, key, device and request of the stream that
 * ended, and starts the clock at zero. */
void WorldReset(void);
/* The scenario's own hold on an object: the SDK keeps its sections and
 * events open for the controller's life. */
void Hold(HmObj *o);

/* ------------------------------------------------------------------ */
/*  The plan Case5Driver.cs writes                                     */
/* ------------------------------------------------------------------ */

typedef struct PlanValue {
    char     name[48];
    DWORD    type;
    DWORD    size;
    uint8_t *data;
} PlanValue;

typedef struct PlanProfile {
    char      group[16];       /* switch, sony, control, ds3, xusb, xinputhid, steam, corpus, corpus-xusb, stale, stale-xusb */
    char      id[128];
    char      devnode[96];     /* the HID device node's instance ID up to its token */
    PlanValue values[24];
    int       valueCount;
    uint8_t   features[64];    /* feature report ids the descriptor declares */
    int       featureCount;
    int       usesReportIds;
    int       bluetooth;       /* the profile's connection is Bluetooth: its
                                * "bluetooth" line, or without one (a plan
                                * of an SDK before v2.0.0) its Bluetooth value */
    int       bluetoothLine;
    int       noHardwareIndex; /* "nohwindex": ConfigWrite leaves the device
                                * node's hardware key without ControllerIndex,
                                * as an SWD device node's first start finds it */
    int       noIdentity;      /* "noidentity": ConfigWrite writes no identity
                                * entry under the tag parameters */
    int       noConfigKey;     /* "nokey": ConfigWrite writes no
                                * Controller<N>, as a device node a program
                                * left behind finds it */
    int       parentKey;       /* "parentkey": with nokey, the values go to
                                * SOFTWARE\HIDMaestro, where drivers before
                                * v2.0.0 looked when Controller<N> was gone */
    uint32_t  readLength;      /* the HID class's read buffer: the largest input report */
    uint8_t   pidRids[6];      /* Pool, State, BlockLoad, CreateNewEffect, BlockFree, DeviceControl */
    int       hasPidRids;
} PlanProfile;

typedef struct PlanBody {
    char     name[48];
    uint8_t  data[48];
} PlanBody;

typedef struct Plan {
    PlanProfile profiles[512];
    int         profileCount;
    PlanBody    bodies[64];
    int         bodyCount;
} Plan;

extern Plan g_plan;
void PlanLoad(const char *path);
PlanProfile *PlanFind(const char *group, const char *id);
PlanValue   *PlanValueFind(PlanProfile *p, const char *name);
uint32_t     PlanDword(PlanProfile *p, const char *name, uint32_t fallback);

/* ------------------------------------------------------------------ */
/*  The SDK side of one controller index                               */
/* ------------------------------------------------------------------ */

typedef struct Sdk {
    int         index;
    HmSection  *input, *ack, *version, *output, *pid;
    HmSection  *signal;   /* 2.1.0's signal gate, under "sdk 3" (SdkWritesSignal) */
    HmEvent    *inputEv, *ackEv, *companionEv;
    HmPipeServer *pipe;
    uint32_t    seq;
    uint32_t    pidSeq;
} Sdk;

enum SdkFlags {
    SDK_NONE      = 0,
    SDK_SECTIONS  = 1,     /* the objects SharedMemoryIO.EnsureInputMapping makes */
    SDK_PIPE      = 2,     /* the owner pipe */
    SDK_OUTPUT    = 4,     /* the output ring section */
    SDK_ALL       = 7,
};

void SdkCreate(Sdk *s, int index, int flags);

/* The version section's parameter, for the version contract of the
 * design note's decision (c). VERSION_SDK is what the SDK at 9bd6279
 * makes (SharedMemoryIO.cs:410-421), and every stream not named for a
 * parameter runs under it. The others: no section beside a live input
 * section, as an SDK before v2.0.0 makes; the section with SdkProtocolMax
 * 3 or 4; and the section at 4, cleared to zeros or closed by the SDK
 * part way through the stream (VersionMidRun). */
enum VersionParam {
    VERSION_SDK = 0,
    VERSION_ABSENT,
    VERSION_MAX3,
    VERSION_MAX4,
    VERSION_CLEARED,
    VERSION_CLOSED,
    VERSION_TAG_OWN,      /* protocols 4 to 4 and this devnode's identity tag */
    VERSION_TAG_OTHER,    /* protocols 4 to 4 and another token's tag: the
                             index's HM_<index> when the devnode carries
                             another token (SdkCreate) */
    /* The parameters above run in CaseVersion. These two only in scripts
     * (device_oracle_check's feature-contract mode): */
    VERSION_RANGE5,       /* protocols 5 to 5 and this devnode's tag */
    VERSION_RANGE45,      /* protocols 4 to 5 and this devnode's tag */
    VERSION_PARAMS
};
#define VERSION_CASE_PARAMS VERSION_RANGE5
extern int g_versionParam;
const char *VersionLabel(int param);
/* The parameters that write an identity tag and the identity entry, as
 * the v2.0.0 SDK does. */
int VersionTagged(int param);
/* The plan's "sdk 2": the default parameter then writes what this tree's
 * SDK writes, the contract's section, tag and identity entry, under the
 * same stream names. Replay's plans carry it, so every stream not named
 * for a parameter runs as the tree's SDK. The plan a recording kept, from
 * 9bd6279's SDK, does not. */
extern int g_sdkV2;
/* The plan's "sdk 3": this tree's SDK from 2.1.0, which also makes the
 * signal gate's section (SharedMemoryIO.EnsureInputMapping), counts its
 * frames from above the SeqNo a driver worker last read, and skips the
 * input event and the companion doorbell when the gate says nobody waits
 * (SharedMemoryIO.WriteInputFrame). Replay's plans carry it. */
extern int g_sdkSignal;
/* Run once when the driver closes an owner pipe client handle: a stream
 * starts the next program at that moment. */
extern void (*g_pipeClientCloseHook)(void);
/* Run once at the next completion a driver thread makes, before it is
 * recorded (hm_wdf.c). */
extern void (*g_completeHook)(void);
/* Run at each blocking wait's answer, after the answer is chosen and
 * recorded and before the thread runs on, until it returns nonzero. */
extern int (*g_waitAnswerHook)(HmThread *t, DWORD result);
/* Run once at the next SRW lock a driver thread takes, before it is taken
 * (hm_win32.c). */
extern void (*g_srwHook)(void);
/* Run once when a driver thread next releases an SRW lock or a wait lock
 * and holds no other, after the release (hm_win32.c, hm_wdf.c). */
extern void (*g_lockReleaseHook)(void);
/* Run once at the next zero-timeout poll a driver thread makes, before it
 * is answered (hm_win32.c FiberWait). */
extern void (*g_pollHook)(void);
/* The code runs on a driver thread, not on the scenario's fiber. */
int HmOnDriverThread(void);
/* FIELD_OFFSET(DEVICE_CONTEXT, TearingDown) in the flavor's driver.h
 * (build.ps1's driver_tag.c). */
extern unsigned HmTearingDownOffset;
/* This stream's SDK writes the version contract: a tagged parameter, or
 * the default one under "sdk 2". */
int SdkWritesContract(void);
/* This stream's SDK makes the signal section: the default parameter under
 * "sdk 3". */
int SdkWritesSignal(void);
/* Under a parameter: the section's bytes, then the clear or the close. */
void SdkVersionMidRun(Sdk *s);
/* SharedMemoryIO.WriteInputFrame: odd SeqNo, DataSize and Data, the GIP
 * slice when one is given, the extended lane or its size cleared, even
 * SeqNo, then the input event and, with a GIP slice, the companion's, each
 * skipped under the signal gate when its capability is set and its count
 * is zero. */
void SdkWriteFrame(Sdk *s, const uint8_t *data, uint32_t dataLen, const uint8_t *gip,
                   const uint8_t *ext, uint32_t extLen);
/* HMController.EnsurePidStateViewLocked and PublishPidPool. */
void SdkPidCreate(Sdk *s, const uint8_t rids[6]);
void SdkPidPool(Sdk *s, uint16_t ramPoolSize, uint8_t maxSim, uint8_t memMgmt);
/* Writes the profile's configuration values into
 * SOFTWARE\HIDMaestro\Controller<index>, and ControllerIndex into the next
 * device node's hardware key. */
void ConfigWrite(PlanProfile *p, int index);
void ConfigOverrideDword(int index, const char *name, DWORD value);
void ConfigDropValue(int index, const char *name);

/* Records the version section and the acknowledgement's first 16 bytes. */
void RecordSections(Sdk *s);

/* ------------------------------------------------------------------ */
/*  Scenarios                                                          */
/* ------------------------------------------------------------------ */

extern uint64_t g_seed;
void CaseSwitch(void);
void CaseSony(void);
void CaseDs3(void);
void CaseXusb(void);
void CaseStartup(void);
void CaseThreads(void);
void CaseVersion(void);
void CaseCorpus(void);
void CaseStale(void);
void CaseIdentity(void);
void CaseExtendedLengths(void);
void CaseSonyVersion(void);
void CaseSwitchVersion(void);
void CaseDs3Version(void);
void CaseXusbVersion(void);
/* The --lifecycle mode (hm_case_lifecycle.c): surprise removal with no
 * removal after it, a rebalance stop and restart, and a removal after a
 * surprise removal, each checked against what the driver holds. Returns
 * the number of checks that failed. */
int  CaseLifecycle(void);
/* What the process holds, for the lifecycle checks: one entry per handle to
 * a named object (a pipe client by its server's name) and per mapped view
 * of a section, written to out. Returns the count. */
int  HeldObjects(char *out, size_t cap);
/* The device whose PnP or cleanup callbacks run now, on the scenario's
 * fiber (hm_wdf.c), else NULL. An unmap from that context while a thread
 * of the device has not returned or a timer of it is started is the
 * release order broken: a reader could still touch the view. Each such
 * unmap is noted, and UnmapsWhileRunning writes them to out ("; " between
 * entries) and returns the count since the world's reset. The view's
 * buffer itself stays readable, since the SDK's side of the section shares
 * it. */
extern HmDevice *g_releasingDevice;
int  UnmapsWhileRunning(char *out, size_t cap);

/* Companion entry point: companion.c is compiled with
 * /DDriverEntry=CompanionDriverEntry so both drivers link into one exe. */
DRIVER_INITIALIZE CompanionDriverEntry;
DRIVER_INITIALIZE DriverEntry;
