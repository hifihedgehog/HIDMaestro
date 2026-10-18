/* perf_budget_check ds3cost: the driver's per-frame work for the DualShock 3
 * and the Switch Pro, at 9bd6279 and in this tree, compiled natively with
 * the driver build's optimizer. build.ps1 copies 9bd6279's own functions
 * out of device_oracle_check\native\oracle\driver.c, the blob that probe's
 * build checks, into oracle_frames.inc: the DualShock 3 sixaxis helpers
 * (driver.c:139-215) and SwitchScaleStick12 and SwitchBuildSimpleFrame
 * (driver.c:1733-1804). The rest of each frame is the driver's code around
 * them, cited line by line. Each export runs its frame `calls` times and
 * returns the QueryPerformanceCounter ticks it took, so no call crosses into
 * managed code inside a timing. Every frame's output lands in g_Ring, and
 * the checksum the caller reads keeps the compiler from dropping a store
 * the driver makes. */
#include <windows.h>
#include <sal.h>
#include <string.h>

#pragma pack(push, 1)
/* driver.h:328-337 at 9bd6279, the layout this tree keeps. */
typedef struct {
    volatile ULONG SeqNo;
    ULONG DataSize;
    UCHAR Data[256];
    UCHAR GipData[14];
    ULONG ExtendedReportSize;
    UCHAR ExtendedReportData[80];
} SHARED;
#pragma pack(pop)

/* What the oracle functions read of a device context. */
typedef struct {
    const SHARED *Section;
    BOOLEAN SwitchImuEnabled;
    PVOID SharedMemPtr;
} CTX, *PDEVICE_CONTEXT;

static UCHAR g_Ring[1024 * 64];
#define SLOT(i) (g_Ring + ((i) & 1023) * 64)

/* ReadSharedInput (driver.c:1328-1355 at 9bd6279, the same in this tree): a
 * seqlock copy of the whole section. */
static BOOLEAN ReadSharedInput(_In_ PDEVICE_CONTEXT ctx, _Out_ SHARED *out)
{
    const volatile SHARED *src = ctx->Section;
    ULONG seq1, seq2;
    int retries = 4;
    do {
        seq1 = src->SeqNo;
        MemoryBarrier();
        memcpy(out, (const void *)src, sizeof(*out));
        MemoryBarrier();
        seq2 = src->SeqNo;
    } while ((seq1 != seq2 || (seq1 & 1)) && --retries > 0);
    return seq1 == seq2 && !(seq1 & 1);
}

/* SwitchFillLatestState (driver.c:1451-1478 at 9bd6279). */
static VOID SwitchFillLatestState(_In_ PDEVICE_CONTEXT ctx, _Out_writes_(46) UCHAR *dst)
{
    SHARED shared;
    RtlZeroMemory(dst, 46);
    dst[3] = 0x00; dst[4] = 0x08; dst[5] = 0x80;
    dst[6] = 0x00; dst[7] = 0x08; dst[8] = 0x80;
    if (ctx->SharedMemPtr == NULL) return;
    if (ReadSharedInput(ctx, &shared) && shared.DataSize >= 11) {
        RtlCopyMemory(dst, shared.Data + 2, 9);
        if (ctx->SwitchImuEnabled && shared.DataSize >= 48)
            RtlCopyMemory(dst + 10, shared.Data + 12, 36);
    }
}

#include "oracle_frames.inc"

/* The cache the worker and the stream thread write under InputLock. */
static UCHAR g_InputReport[256];
static UCHAR g_Ds3Raw[DS3_RAW_REPORT_SIZE];
static UCHAR g_LiveStore[256];

static LONGLONG Now(void) { LARGE_INTEGER t; QueryPerformanceCounter(&t); return t.QuadPart; }

/* 9bd6279, one 0x30 tick of SwitchStreamProc (driver.c:1825-1862): the state
 * from the section, the frame, the timer, the cache. */
__declspec(dllexport) LONGLONG OldSwitchFull(const SHARED *sections, int count, int calls)
{
    CTX ctx = { 0 };
    ctx.SwitchImuEnabled = TRUE;
    LONGLONG t0 = Now();
    for (int i = 0; i < calls; i++) {
        UCHAR frame[64];
        UCHAR state[46];
        ctx.Section = &sections[i % count];
        ctx.SharedMemPtr = (PVOID)ctx.Section;
        RtlZeroMemory(frame, sizeof(frame));
        SwitchFillLatestState(&ctx, state);
        frame[0] = 0x30;
        frame[2] = 0x91;
        RtlCopyMemory(frame + 3, state, 9);
        frame[12] = 0xB0;
        RtlCopyMemory(frame + 13, state + 10, 36);
        frame[1] = (UCHAR)i;
        RtlCopyMemory(g_InputReport, frame, 49);
        RtlCopyMemory(SLOT(i), frame, 49);
    }
    return Now() - t0;
}

/* 9bd6279, one 0x3F tick (driver.c:1847-1862 with SwitchBuildSimpleFrame). */
__declspec(dllexport) LONGLONG OldSwitchSimple(const SHARED *sections, int count, int calls)
{
    CTX ctx = { 0 };
    LONGLONG t0 = Now();
    for (int i = 0; i < calls; i++) {
        UCHAR frame[64];
        ctx.Section = &sections[i % count];
        ctx.SharedMemPtr = (PVOID)ctx.Section;
        SwitchBuildSimpleFrame(&ctx, frame);
        RtlCopyMemory(g_InputReport, frame, 12);
        RtlCopyMemory(SLOT(i), frame, 12);
    }
    return Now() - t0;
}

/* 9bd6279, the worker's turn on a Switch Pro frame the SDK wrote
 * (driver.c:1883-1901): the section read and the early return, since the
 * stream thread sends the frames. */
__declspec(dllexport) LONGLONG OldSwitchSubmit(const SHARED *sections, int count, int calls)
{
    CTX ctx = { 0 };
    LONGLONG t0 = Now();
    for (int i = 0; i < calls; i++) {
        SHARED shared;
        ctx.Section = &sections[i % count];
        if (!ReadSharedInput(&ctx, &shared)) continue;
        SLOT(i)[0] = (UCHAR)shared.SeqNo;
    }
    return Now() - t0;
}

/* 9bd6279, the DualShock 3's frame in ProcessSharedInput (driver.c:1957-1974
 * and 2009-2010): the section, the input report derived, the cache and the
 * raw report kept. */
__declspec(dllexport) LONGLONG OldDs3Frame(const SHARED *sections, int count, int calls)
{
    CTX ctx = { 0 };
    LONGLONG t0 = Now();
    for (int i = 0; i < calls; i++) {
        SHARED shared;
        UCHAR input[DS3_SXS_INPUT_SIZE];
        ctx.Section = &sections[i % count];
        if (!ReadSharedInput(&ctx, &shared)) continue;
        Ds3BuildSixaxisInput(shared.ExtendedReportData, input);
        RtlCopyMemory(g_InputReport, input, DS3_SXS_INPUT_SIZE);
        RtlCopyMemory(g_Ds3Raw, shared.ExtendedReportData, DS3_RAW_REPORT_SIZE);
        RtlCopyMemory(SLOT(i), input, DS3_SXS_INPUT_SIZE);
    }
    return Now() - t0;
}

/* 9bd6279, a GET of feature 0 (driver.c:3089-3099). */
__declspec(dllexport) LONGLONG OldDs3Feature(int calls)
{
    LONGLONG t0 = Now();
    for (int i = 0; i < calls; i++) {
        UCHAR feat[DS3_RAW_REPORT_SIZE];
        g_Ds3Raw[3] = (UCHAR)i;
        RtlCopyMemory(feat, g_Ds3Raw, DS3_RAW_REPORT_SIZE);
        Ds3BuildSixaxisFeature(feat);
        RtlCopyMemory(SLOT(i), feat, DS3_RAW_REPORT_SIZE);
    }
    return Now() - t0;
}

/* This tree, one frame in ProcessSharedInput: the section, the extended
 * lane's `ext` bytes to the cache and, with LiveFeatureReport, the data
 * lane's `live` bytes to the live store (KeepLiveFeatureLocked). */
__declspec(dllexport) LONGLONG NewFrame(const SHARED *sections, int count, int calls, int ext, int live)
{
    CTX ctx = { 0 };
    LONGLONG t0 = Now();
    for (int i = 0; i < calls; i++) {
        SHARED shared;
        ctx.Section = &sections[i % count];
        if (!ReadSharedInput(&ctx, &shared)) continue;
        RtlCopyMemory(g_InputReport, shared.ExtendedReportData, ext);
        if (live) RtlCopyMemory(g_LiveStore, shared.Data, live);
        RtlCopyMemory(SLOT(i), g_InputReport, ext);
    }
    return Now() - t0;
}

/* This tree, a GET of the live feature report: a copy of the store. */
__declspec(dllexport) LONGLONG NewFeature(int calls, int live)
{
    LONGLONG t0 = Now();
    for (int i = 0; i < calls; i++) {
        g_LiveStore[3] = (UCHAR)i;
        RtlCopyMemory(SLOT(i), g_LiveStore, live);
    }
    return Now() - t0;
}

__declspec(dllexport) unsigned RingSum(void)
{
    unsigned s = 0;
    for (int i = 0; i < (int)sizeof(g_Ring); i++) s = s * 31 + g_Ring[i];
    return s;
}
