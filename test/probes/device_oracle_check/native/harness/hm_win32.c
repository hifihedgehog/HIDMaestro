/* The Win32 stand-ins hm_fi.h maps the drivers' calls onto, and the
 * in-memory registry, kernel objects and fiber threads behind them.
 *
 * Every stand-in follows the documented contract of the call it replaces
 * for the arguments the drivers pass. The harness itself calls the real
 * kernel32 only for fibers, the heap and its output file, and advapi32
 * only for the pure SID helpers (CreateWellKnownSid, IsWellKnownSid,
 * ConvertSidToStringSidW), none of which reads or changes machine state. */
#include "hm_harness.h"
#include <stdarg.h>

uint64_t g_clockMs;
int g_locksHeld;

__declspec(noreturn) void HmFail(const char *fmt, ...)
{
    va_list ap;
    fputs("driver harness: ", stderr);
    va_start(ap, fmt);
    vfprintf(stderr, fmt, ap);
    va_end(ap);
    fputc('\n', stderr);
    fflush(stderr);
    ExitProcess(3);
}

/* ------------------------------------------------------------------ */
/*  SplitMix64 (Steele, Lea and Flood, OOPSLA 2014), as Rng.cs         */
/* ------------------------------------------------------------------ */

void RngInit(HmRng *r, uint64_t seed, uint64_t stream)
{
    r->state = seed ^ (stream * 0x9E3779B97F4A7C15ULL);
}

uint64_t RngU64(HmRng *r)
{
    uint64_t z = (r->state += 0x9E3779B97F4A7C15ULL);
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
    return z ^ (z >> 31);
}

uint32_t RngU32(HmRng *r) { return (uint32_t)(RngU64(r) >> 32); }
int RngNext(HmRng *r, int max) { return max <= 0 ? 0 : (int)((RngU64(r) >> 33) % (uint64_t)max); }
int RngBool(HmRng *r) { return (int)(RngU64(r) >> 63); }
uint8_t RngByte(HmRng *r) { return (uint8_t)(RngU64(r) >> 56); }
void RngFill(HmRng *r, uint8_t *dst, size_t n) { for (size_t i = 0; i < n; i++) dst[i] = RngByte(r); }

/* ------------------------------------------------------------------ */
/*  Registry                                                           */
/* ------------------------------------------------------------------ */

static HmRegKey *g_keys;
static HmRegKey *g_detached;

HmRegKey *KeyFind(const WCHAR *path)
{
    for (HmRegKey *k = g_keys; k; k = k->next)
        if (_wcsicmp(k->path, path) == 0) return k;
    return NULL;
}

HmRegKey *KeyMake(const WCHAR *path)
{
    HmRegKey *k = KeyFind(path);
    if (k) return k;
    k = (HmRegKey *)calloc(1, sizeof(*k));
    if (!k || wcslen(path) >= ARRAYSIZE(k->path)) HmFail("registry key path too long");
    wcscpy_s(k->path, ARRAYSIZE(k->path), path);
    k->next = g_keys;
    g_keys = k;
    return k;
}

HmRegKey *KeyNewDetached(const WCHAR *label)
{
    HmRegKey *k = (HmRegKey *)calloc(1, sizeof(*k));
    if (!k) HmFail("out of memory");
    wcscpy_s(k->path, ARRAYSIZE(k->path), label);
    k->next = g_detached;
    g_detached = k;
    return k;
}

void KeyFree(HmRegKey *k)
{
    HmRegValue *v = k->values;
    while (v) { HmRegValue *n = v->next; free(v->data); free(v); v = n; }
    free(k);
}

void KeyDrop(const WCHAR *path)
{
    for (HmRegKey **pp = &g_keys; *pp; pp = &(*pp)->next) {
        if (_wcsicmp((*pp)->path, path) == 0) {
            HmRegKey *k = *pp;
            *pp = k->next;
            KeyFree(k);
            return;
        }
    }
}

HmRegValue *ValueFind(HmRegKey *key, const WCHAR *name)
{
    for (HmRegValue *v = key->values; v; v = v->next)
        if (_wcsicmp(v->name, name ? name : L"") == 0) return v;
    return NULL;
}

void ValueSet(HmRegKey *key, const WCHAR *name, DWORD type, const void *data, DWORD size)
{
    HmRegValue *v = ValueFind(key, name);
    if (!v) {
        v = (HmRegValue *)calloc(1, sizeof(*v));
        if (!v || wcslen(name) >= ARRAYSIZE(v->name)) HmFail("registry value name too long");
        wcscpy_s(v->name, ARRAYSIZE(v->name), name);
        /* Appended, so enumeration order is the order of the writes. */
        HmRegValue **pp = &key->values;
        while (*pp) pp = &(*pp)->next;
        *pp = v;
    }
    free(v->data);
    v->data = (uint8_t *)malloc(size ? size : 1);
    if (!v->data) HmFail("out of memory");
    if (size) memcpy(v->data, data, size);
    v->size = size;
    v->type = type;
}

int ValueDrop(HmRegKey *key, const WCHAR *name)
{
    for (HmRegValue **pp = &key->values; *pp; pp = &(*pp)->next) {
        if (_wcsicmp((*pp)->name, name) == 0) {
            HmRegValue *v = *pp;
            *pp = v->next;
            free(v->data);
            free(v);
            return 1;
        }
    }
    return 0;
}

static void KeysReset(void)
{
    while (g_keys) { HmRegKey *n = g_keys->next; KeyFree(g_keys); g_keys = n; }
    while (g_detached) { HmRegKey *n = g_detached->next; KeyFree(g_detached); g_detached = n; }
}

/* ------------------------------------------------------------------ */
/*  Objects and handles                                                */
/* ------------------------------------------------------------------ */

#define HANDLE_SLOTS 4096
static HmObj *g_handles[HANDLE_SLOTS];
static HmObj *g_allObjs;
static DWORD g_lastError;

/* Handle values are multiples of four from 4, as the kernel's, and are
 * reused lowest first. None reaches a recorded output. */
HANDLE ObjHandle(HmObj *o)
{
    for (int i = 0; i < HANDLE_SLOTS; i++) {
        if (!g_handles[i]) {
            g_handles[i] = o;
            o->refs++;
            return (HANDLE)(uintptr_t)((i + 1) * 4);
        }
    }
    HmFail("handle table full");
}

static int HandleSlot(HANDLE h)
{
    uintptr_t v = (uintptr_t)h;
    if (v == 0 || (v & 3) || v / 4 > HANDLE_SLOTS) return -1;
    return (int)(v / 4 - 1);
}

HmObj *ObjFromHandle(HANDLE h)
{
    int i = HandleSlot(h);
    return i < 0 ? NULL : g_handles[i];
}

HmObj *ObjFindNamed(const WCHAR *name)
{
    if (!name || !*name) return NULL;
    for (HmObj *o = g_allObjs; o; o = o->nextAll)
        if (o->refs > 0 && o->name[0] && _wcsicmp(o->name, name) == 0) return o;
    return NULL;
}

static HmObj *ObjNew(size_t size, int kind, const WCHAR *name)
{
    HmObj *o = (HmObj *)calloc(1, size);
    if (!o) HmFail("out of memory");
    o->kind = kind;
    if (name) {
        if (wcslen(name) >= ARRAYSIZE(o->name)) HmFail("object name too long");
        wcscpy_s(o->name, ARRAYSIZE(o->name), name);
    }
    o->nextAll = g_allObjs;
    g_allObjs = o;
    return o;
}

/* A named object leaves the namespace when its last reference goes, as a
 * kernel object does. Its memory stays until the world resets. */
void ObjRelease(HmObj *o)
{
    if (o->refs <= 0) HmFail("object released twice");
    o->refs--;
}

void Hold(HmObj *o) { o->refs++; }

HmEvent *EventNew(const WCHAR *name, BOOL manual, BOOL initial, int owner, int hasDacl)
{
    HmEvent *e = (HmEvent *)ObjNew(sizeof(HmEvent), HK_EVENT, name);
    e->manual = manual;
    e->signaled = initial;
    e->o.owner = owner;
    e->o.hasDacl = hasDacl;
    return e;
}

void EventSet(HmEvent *e)
{
    e->signaled = TRUE;
}

HmSection *SectionNew(const WCHAR *name, size_t size)
{
    HmSection *s = (HmSection *)ObjNew(sizeof(HmSection), HK_SECTION, name);
    s->size = size;
    s->buf = (uint8_t *)calloc(1, size);
    if (!s->buf) HmFail("out of memory");
    s->o.owner = OWN_ADMINS;
    s->o.hasDacl = TRUE;
    return s;
}

HmPipeServer *PipeServerNew(const WCHAR *name, ULONG processId)
{
    HmPipeServer *p = (HmPipeServer *)ObjNew(sizeof(HmPipeServer), HK_PIPE_SERVER, name);
    p->processId = processId;
    p->o.owner = OWN_ADMINS;
    p->o.hasDacl = TRUE;
    return p;
}

/* The SDK's end of the owner pipe closes, as on Dispose or process exit:
 * the read the driver keeps pending completes with a broken pipe and sets
 * its OVERLAPPED event. */
void PipeServerClose(HmPipeServer *s)
{
    s->closed = 1;
    for (HmObj *o = g_allObjs; o; o = o->nextAll) {
        if (o->kind != HK_PIPE_CLIENT || o->refs <= 0) continue;
        HmPipeClient *c = (HmPipeClient *)o;
        if (c->server == s && c->pending) {
            c->pending->Internal = (ULONG_PTR)STATUS_PIPE_BROKEN;
            HmObj *ev = ObjFromHandle(c->pending->hEvent);
            if (ev && ev->kind == HK_EVENT) EventSet((HmEvent *)ev);
            c->pending = NULL;
        }
    }
    ObjRelease(&s->o);
}

/* ------------------------------------------------------------------ */
/*  Threads as fibers                                                  */
/* ------------------------------------------------------------------ */

static void *g_mainFiber;
static HmThread *g_current;
int HmOnDriverThread(void) { return g_current != NULL; }
int (*g_waitAnswerHook)(HmThread *t, DWORD result);

void FiberInit(void)
{
    g_mainFiber = ConvertThreadToFiber(NULL);
    if (!g_mainFiber) HmFail("ConvertThreadToFiber failed (%lu)", (unsigned long)GetLastError());
}

static VOID CALLBACK FiberMain(LPVOID p)
{
    HmThread *t = (HmThread *)p;
    t->exitCode = t->start(t->param);
    t->exited = 1;
    SwitchToFiber(g_mainFiber);
    HmFail("an exited thread was resumed");
}

/* Every wait is recorded when it begins, as "wait": the thread's index on
 * its device, the handle count and the timeout the driver asked for. Its
 * answer is recorded when the scenario resumes the thread, as "wait.answer":
 * the thread's index, the result and the milliseconds that passed on the
 * harness clock. A timeout is answered only once the clock has reached the
 * wait's own deadline, and never to an INFINITE wait (review F1): the
 * period of every loop the drivers time comes from their own waits. */
static void RecordWait(const char *tag, int thread, DWORD a, DWORD b, DWORD c)
{
    uint8_t rec[13];
    rec[0] = (uint8_t)thread;
    memcpy(rec + 1, &a, 4);
    memcpy(rec + 5, &b, 4);
    memcpy(rec + 9, &c, 4);
    RecOut(tag, rec, 13);
}

void ThreadRun(HmThread *t, DWORD result)
{
    if (t->exited) return;
    if (g_current) HmFail("a thread resumed another thread");
    if (g_locksHeld) HmFail("a lock is held across a thread switch");
    if (t->started) {
        if (result == WAIT_TIMEOUT) {
            if (t->waitTimeout == INFINITE) HmFail("thread %d: a timeout answered to an INFINITE wait", t->index);
            if (g_clockMs < t->waitStartMs + t->waitTimeout)
                HmFail("thread %d: a timeout answered %llu ms into a %lu ms wait", t->index,
                       (unsigned long long)(g_clockMs - t->waitStartMs), (unsigned long)t->waitTimeout);
        }
        RecordWait("wait.answer", t->index, result, (DWORD)(g_clockMs - t->waitStartMs), 0);
        if (g_waitAnswerHook && g_waitAnswerHook(t, result)) g_waitAnswerHook = NULL;
    }
    t->result = result;
    t->started = 1;
    g_current = t;
    SwitchToFiber(t->fiber);
    g_current = NULL;
    if (g_locksHeld) HmFail("a thread switched away holding a lock");
    if (t->exited) RecordWait("thread.exit", t->index, t->exitCode, 0, 0);
}

static int HandleSignaled(HANDLE h, int consume)
{
    HmObj *o = ObjFromHandle(h);
    if (!o) return 0;
    if (o->kind == HK_EVENT) {
        HmEvent *e = (HmEvent *)o;
        if (!e->signaled) return 0;
        if (consume && !e->manual) e->signaled = FALSE;
        return 1;
    }
    if (o->kind == HK_THREAD) return ((HmThread *)o)->exited;
    return 0;
}

/* The answer the wait gets from the objects' states now: the first
 * signaled handle (an auto-reset event is reset by it), or, with none
 * signaled, the timeout, after the clock has run to the wait's deadline. */
DWORD ThreadAutoResult(HmThread *t)
{
    for (DWORD i = 0; i < t->waitCount; i++)
        if (HandleSignaled(t->waitHandles[i], 1)) return WAIT_OBJECT_0 + i;
    if (t->waitTimeout == INFINITE) HmFail("thread %d: nothing signaled an INFINITE wait it was resumed from", t->index);
    if (g_clockMs < t->waitStartMs + t->waitTimeout) g_clockMs = t->waitStartMs + t->waitTimeout;
    return WAIT_TIMEOUT;
}

void ThreadResume(HmThread *t)
{
    if (t->exited) return;
    ThreadRun(t, t->started ? ThreadAutoResult(t) : 0);
}

static DWORD FiberWait(DWORD n, const HANDLE *hs, DWORD ms)
{
    HmThread *t = g_current;
    if (n > MAXIMUM_WAIT_OBJECTS) HmFail("wait on %lu objects", (unsigned long)n);
    for (DWORD i = 0; i < n; i++) {
        if (!ObjFromHandle(hs[i])) HmFail("wait on a handle that is not open");
        t->waitHandles[i] = hs[i];
    }
    t->waitCount = n;
    t->waitTimeout = ms;
    t->waitStartMs = g_clockMs;
    RecordWait("wait", t->index, n, ms, 0);
    if (ms == 0 && g_pollHook) { void (*hook)(void) = g_pollHook; g_pollHook = NULL; hook(); }
    if (ms == 0) {
        /* A zero timeout is a poll: it returns at once from the objects'
         * states, and no other thread runs meanwhile. A stream can run
         * g_pollHook once before it, as what another process did in the
         * meantime. */
        DWORD r = ThreadAutoResult(t);
        RecordWait("wait.answer", t->index, r, 0, 0);
        t->waitCount = 0;
        return r;
    }
    if (g_locksHeld) HmFail("a thread waits holding a lock");
    g_current = NULL;
    SwitchToFiber(g_mainFiber);
    t->waitCount = 0;
    return t->result;
}

/* A wait on the scenario's own fiber: the cleanup callbacks join their
 * threads this way. A thread joined is run until it returns, at most 64
 * turns, each wait answered from the objects' states. One still running
 * then gives the join's timeout once the clock has run to its deadline,
 * as the 2 s join would. Recorded as "join" with the handle count and
 * the timeout, and its answer as "join.answer". */
static DWORD MainWait(DWORD n, const HANDLE *hs, DWORD ms)
{
    uint64_t start = g_clockMs;
    RecordWait("join", 0xFF, n, ms, 0);
    for (int turn = 0; turn < 64; turn++) {
        for (DWORD i = 0; i < n; i++)
            if (HandleSignaled(hs[i], 1)) {
                RecordWait("join.answer", 0xFF, WAIT_OBJECT_0 + i, (DWORD)(g_clockMs - start), 0);
                return WAIT_OBJECT_0 + i;
            }
        int ran = 0;
        for (DWORD i = 0; i < n; i++) {
            HmObj *o = ObjFromHandle(hs[i]);
            if (o && o->kind == HK_THREAD && !((HmThread *)o)->exited) {
                ThreadResume((HmThread *)o);
                ran = 1;
            }
        }
        if (!ran) break;
    }
    if (ms == INFINITE) HmFail("an INFINITE wait on the scenario's fiber that nothing signals");
    if (g_clockMs < start + ms) g_clockMs = start + ms;
    RecordWait("join.answer", 0xFF, WAIT_TIMEOUT, (DWORD)(g_clockMs - start), 0);
    return WAIT_TIMEOUT;
}

DWORD WINAPI HmWaitForSingleObject(HANDLE hHandle, DWORD dwMilliseconds)
{
    return g_current ? FiberWait(1, &hHandle, dwMilliseconds) : MainWait(1, &hHandle, dwMilliseconds);
}

DWORD WINAPI HmWaitForMultipleObjects(DWORD nCount, const HANDLE *lpHandles, BOOL bWaitAll, DWORD dwMilliseconds)
{
    if (bWaitAll) HmFail("WaitForMultipleObjects with bWaitAll is not modeled");
    return g_current ? FiberWait(nCount, lpHandles, dwMilliseconds) : MainWait(nCount, lpHandles, dwMilliseconds);
}

extern HmDevice *g_addingDevice;

HANDLE WINAPI HmCreateThread(LPSECURITY_ATTRIBUTES lpThreadAttributes, SIZE_T dwStackSize,
                             LPTHREAD_START_ROUTINE lpStartAddress, LPVOID lpParameter,
                             DWORD dwCreationFlags, LPDWORD lpThreadId)
{
    (void)lpThreadAttributes; (void)dwStackSize;
    if (dwCreationFlags != 0) HmFail("CreateThread flags 0x%lx are not modeled", (unsigned long)dwCreationFlags);
    HmThread *t = (HmThread *)ObjNew(sizeof(HmThread), HK_THREAD, NULL);
    t->start = lpStartAddress;
    t->param = lpParameter;
    t->fiber = CreateFiber(0, FiberMain, t);
    if (!t->fiber) HmFail("CreateFiber failed");
    HmDevice *dev = g_addingDevice;
    if (!dev) HmFail("CreateThread outside a device add");
    if (dev->threadCount >= (int)ARRAYSIZE(dev->threads)) HmFail("too many threads");
    t->index = dev->threadCount;
    dev->threads[dev->threadCount++] = t;
    if (lpThreadId) *lpThreadId = 0x100 + (DWORD)t->index;
    /* The device's list keeps a reference, so the fiber outlives the
     * driver's handle until the world resets. */
    t->o.refs++;
    return ObjHandle(&t->o);
}

DWORD WINAPI HmGetLastError(VOID) { return g_lastError; }

/* ------------------------------------------------------------------ */
/*  Events                                                             */
/* ------------------------------------------------------------------ */

static const WCHAR *DescriptorSddl(const void *sd);

/* Records each create: whether it made the object (1) or found it (0),
 * manual reset, whether a descriptor came with it, the name's length in
 * UTF-16 units, the name (empty for an unnamed event), then the SDDL text
 * the descriptor was converted from (review F3). */
static void RecordEventCreate(int made, BOOL manual, LPSECURITY_ATTRIBUTES sa, LPCWSTR name)
{
    uint8_t rec[5 + 192 + 512];
    size_t n = name ? wcslen(name) : 0;
    const WCHAR *sddl = sa && sa->lpSecurityDescriptor ? DescriptorSddl(sa->lpSecurityDescriptor) : L"";
    size_t m = wcslen(sddl);
    if (n > 96 || m > 256) HmFail("event name or descriptor too long");
    rec[0] = (uint8_t)made;
    rec[1] = (uint8_t)(manual ? 1 : 0);
    rec[2] = (uint8_t)(sa && sa->lpSecurityDescriptor ? 1 : 0);
    rec[3] = (uint8_t)n;
    rec[4] = 0;
    if (n) memcpy(rec + 5, name, n * 2);
    if (m) memcpy(rec + 5 + n * 2, sddl, m * 2);
    RecOut("event.create", rec, (uint32_t)(5 + n * 2 + m * 2));
}

HANDLE WINAPI HmCreateEventW(LPSECURITY_ATTRIBUTES lpEventAttributes, BOOL bManualReset, BOOL bInitialState, LPCWSTR lpName)
{
    HmObj *o = ObjFindNamed(lpName);
    if (o) {
        if (o->kind != HK_EVENT) { g_lastError = ERROR_INVALID_HANDLE; return NULL; }
        /* The existing object, in its own state, and ERROR_ALREADY_EXISTS
         * (CreateEventW: "the function returns a handle to the existing
         * object"). */
        RecordEventCreate(0, bManualReset, lpEventAttributes, lpName);
        g_lastError = ERROR_ALREADY_EXISTS;
        return ObjHandle(o);
    }
    /* The driver hosts run as LocalService, so what they create is
     * LocalService's, with the descriptor they pass. */
    HmEvent *e = EventNew(lpName, bManualReset, bInitialState, OWN_LOCALSERVICE,
                          lpEventAttributes && lpEventAttributes->lpSecurityDescriptor);
    if (lpName && _wcsnicmp(lpName, L"Global\\HIDMaestroOutputEvent", 28) == 0)
        e->role = EV_DOORBELL;
    RecordEventCreate(1, bManualReset, lpEventAttributes, lpName);
    g_lastError = ERROR_SUCCESS;
    return ObjHandle(&e->o);
}

HANDLE WINAPI HmOpenEventW(DWORD dwDesiredAccess, BOOL bInheritHandle, LPCWSTR lpName)
{
    (void)dwDesiredAccess; (void)bInheritHandle;
    HmObj *o = ObjFindNamed(lpName);
    if (!o || o->kind != HK_EVENT) { g_lastError = ERROR_FILE_NOT_FOUND; return NULL; }
    return ObjHandle(o);
}

BOOL WINAPI HmSetEvent(HANDLE hEvent)
{
    HmObj *o = ObjFromHandle(hEvent);
    if (!o || o->kind != HK_EVENT) HmFail("SetEvent on a handle that is no event");
    HmEvent *e = (HmEvent *)o;
    EventSet(e);
    if (e->role == EV_DOORBELL) RecFlushRing();
    if (e->role == EV_ACK) RecordAck();
    return TRUE;
}

BOOL WINAPI HmResetEvent(HANDLE hEvent)
{
    HmObj *o = ObjFromHandle(hEvent);
    if (!o || o->kind != HK_EVENT) HmFail("ResetEvent on a handle that is no event");
    ((HmEvent *)o)->signaled = FALSE;
    return TRUE;
}

BOOL WINAPI HmCloseHandle(HANDLE hObject)
{
    int i = HandleSlot(hObject);
    if (i < 0 || !g_handles[i]) HmFail("CloseHandle on a handle that is not open");
    HmObj *o = g_handles[i];
    int pipeClient = o->kind == HK_PIPE_CLIENT;
    g_handles[i] = NULL;
    ObjRelease(o);
    if (pipeClient && g_pipeClientCloseHook) {
        void (*hook)(void) = g_pipeClientCloseHook;
        g_pipeClientCloseHook = NULL;
        hook();
    }
    return TRUE;
}
void (*g_pipeClientCloseHook)(void);

/* ------------------------------------------------------------------ */
/*  Sections                                                           */
/* ------------------------------------------------------------------ */

HANDLE WINAPI HmOpenFileMappingW(DWORD dwDesiredAccess, BOOL bInheritHandle, LPCWSTR lpName)
{
    (void)dwDesiredAccess; (void)bInheritHandle;
    HmObj *o = ObjFindNamed(lpName);
    if (!o || o->kind != HK_SECTION) { g_lastError = ERROR_FILE_NOT_FOUND; return NULL; }
    return ObjHandle(o);
}

/* Each view of a section is its one buffer. A view keeps no reference of
 * its own here: the scenario holds every section for the stream. The
 * section counts its views, so an unmap of a view no longer mapped stops
 * the run and HeldObjects lists a view still mapped. */
LPVOID WINAPI HmMapViewOfFile(HANDLE hFileMappingObject, DWORD dwDesiredAccess, DWORD dwFileOffsetHigh,
                              DWORD dwFileOffsetLow, SIZE_T dwNumberOfBytesToMap)
{
    (void)dwDesiredAccess;
    HmObj *o = ObjFromHandle(hFileMappingObject);
    if (!o || o->kind != HK_SECTION) HmFail("MapViewOfFile on a handle that is no section");
    HmSection *s = (HmSection *)o;
    if (dwFileOffsetHigh || dwFileOffsetLow || dwNumberOfBytesToMap > s->size) {
        g_lastError = ERROR_ACCESS_DENIED;
        return NULL;
    }
    s->views++;
    return s->buf;
}

HmDevice *g_releasingDevice;
static char g_unmapNotes[4096];
static size_t g_unmapNoteLen;
static int g_unmapNoteCount;

static void NoteUnmap(const WCHAR *name, const char *what, int index)
{
    g_unmapNoteCount++;
    if (g_unmapNoteLen >= sizeof(g_unmapNotes)) return;
    int n = _snprintf_s(g_unmapNotes + g_unmapNoteLen, sizeof(g_unmapNotes) - g_unmapNoteLen, _TRUNCATE,
                        "%s%ls unmapped while %s %d still runs", g_unmapNoteLen ? "; " : "", name, what, index);
    g_unmapNoteLen = n < 0 ? sizeof(g_unmapNotes) : g_unmapNoteLen + (size_t)n;
}

int UnmapsWhileRunning(char *out, size_t cap)
{
    if (cap) strncpy_s(out, cap, g_unmapNotes, _TRUNCATE);
    return g_unmapNoteCount;
}

BOOL WINAPI HmUnmapViewOfFile(LPCVOID lpBaseAddress)
{
    for (HmObj *o = g_allObjs; o; o = o->nextAll)
        if (o->kind == HK_SECTION && ((HmSection *)o)->buf == lpBaseAddress) {
            if (((HmSection *)o)->views <= 0) HmFail("UnmapViewOfFile of a view already unmapped");
            ((HmSection *)o)->views--;
            if (!g_current && g_releasingDevice) {
                HmDevice *d = g_releasingDevice;
                for (int i = 0; i < d->threadCount; i++)
                    if (!d->threads[i]->exited) NoteUnmap(o->name, "thread", i);
                for (int i = 0; i < d->timerCount; i++)
                    if (d->timers[i]->started) NoteUnmap(o->name, "timer", i);
            }
            return TRUE;
        }
    HmFail("UnmapViewOfFile of an address that is no view");
}

/* Appends one entry to out, "; " between entries, keeping it terminated. */
static void HeldLine(char *out, size_t cap, size_t *at, const char *what, const WCHAR *name)
{
    if (*at >= cap) return;
    int n = _snprintf_s(out + *at, cap - *at, _TRUNCATE, "%s%s %ls", *at ? "; " : "", what, name);
    *at = n < 0 ? cap : *at + (size_t)n;
}

int HeldObjects(char *out, size_t cap)
{
    size_t at = 0;
    int count = 0;
    if (cap) out[0] = 0;
    for (int i = 0; i < HANDLE_SLOTS; i++) {
        HmObj *o = g_handles[i];
        if (!o) continue;
        if (o->kind == HK_PIPE_CLIENT) {
            HeldLine(out, cap, &at, "pipe client of", ((HmPipeClient *)o)->server->o.name);
            count++;
        } else if (o->name[0]) {
            HeldLine(out, cap, &at, o->kind == HK_SECTION ? "section handle" : "event handle", o->name);
            count++;
        }
    }
    for (HmObj *o = g_allObjs; o; o = o->nextAll)
        if (o->kind == HK_SECTION)
            for (int v = 0; v < ((HmSection *)o)->views; v++) {
                HeldLine(out, cap, &at, "mapped view of", o->name);
                count++;
            }
    return count;
}

/* ------------------------------------------------------------------ */
/*  Registry API                                                       */
/* ------------------------------------------------------------------ */

LSTATUS WINAPI HmRegOpenKeyExW(HKEY hKey, LPCWSTR lpSubKey, DWORD ulOptions, REGSAM samDesired, PHKEY phkResult)
{
    (void)ulOptions; (void)samDesired;
    if (hKey != HKEY_LOCAL_MACHINE) HmFail("RegOpenKeyExW below a key other than HKLM is not modeled");
    HmRegKey *k = KeyFind(lpSubKey);
    if (!k) return ERROR_FILE_NOT_FOUND;
    HmKeyHandle *kh = (HmKeyHandle *)ObjNew(sizeof(HmKeyHandle), HK_KEY, NULL);
    kh->key = k;
    *phkResult = (HKEY)ObjHandle(&kh->o);
    return ERROR_SUCCESS;
}

/* RegQueryValueExW's documented contract: with lpData NULL the size goes
 * to lpcbData; with a buffer too small, ERROR_MORE_DATA and the size; an
 * absent value, ERROR_FILE_NOT_FOUND. */
LSTATUS WINAPI HmRegQueryValueExW(HKEY hKey, LPCWSTR lpValueName, LPDWORD lpReserved, LPDWORD lpType,
                                  LPBYTE lpData, LPDWORD lpcbData)
{
    (void)lpReserved;
    HmObj *o = ObjFromHandle((HANDLE)hKey);
    if (!o || o->kind != HK_KEY) HmFail("RegQueryValueExW on a handle that is no key");
    HmRegValue *v = ValueFind(((HmKeyHandle *)o)->key, lpValueName);
    if (!v) return ERROR_FILE_NOT_FOUND;
    if (lpType) *lpType = v->type;
    if (!lpData) {
        if (lpcbData) *lpcbData = v->size;
        return ERROR_SUCCESS;
    }
    if (!lpcbData) HmFail("RegQueryValueExW with data and no size");
    if (*lpcbData < v->size) {
        *lpcbData = v->size;
        return ERROR_MORE_DATA;
    }
    memcpy(lpData, v->data, v->size);
    *lpcbData = v->size;
    return ERROR_SUCCESS;
}

LSTATUS WINAPI HmRegCloseKey(HKEY hKey)
{
    HmObj *o = ObjFromHandle((HANDLE)hKey);
    if (!o || o->kind != HK_KEY) HmFail("RegCloseKey on a handle that is no key");
    HmCloseHandle((HANDLE)hKey);
    return ERROR_SUCCESS;
}

/* ------------------------------------------------------------------ */
/*  Owner pipe                                                         */
/* ------------------------------------------------------------------ */

HANDLE WINAPI HmCreateFileW(LPCWSTR lpFileName, DWORD dwDesiredAccess, DWORD dwShareMode,
                            LPSECURITY_ATTRIBUTES lpSecurityAttributes, DWORD dwCreationDisposition,
                            DWORD dwFlagsAndAttributes, HANDLE hTemplateFile)
{
    (void)dwDesiredAccess; (void)dwShareMode; (void)lpSecurityAttributes;
    (void)dwCreationDisposition; (void)dwFlagsAndAttributes; (void)hTemplateFile;
    HmObj *o = ObjFindNamed(lpFileName);
    if (!o || o->kind != HK_PIPE_SERVER || ((HmPipeServer *)o)->closed) {
        g_lastError = ERROR_FILE_NOT_FOUND;
        return INVALID_HANDLE_VALUE;
    }
    HmPipeClient *c = (HmPipeClient *)ObjNew(sizeof(HmPipeClient), HK_PIPE_CLIENT, NULL);
    c->server = (HmPipeServer *)o;
    c->o.owner = o->owner;
    c->o.hasDacl = TRUE;
    return ObjHandle(&c->o);
}

BOOL WINAPI HmReadFile(HANDLE hFile, LPVOID lpBuffer, DWORD nNumberOfBytesToRead,
                       LPDWORD lpNumberOfBytesRead, LPOVERLAPPED lpOverlapped)
{
    (void)lpBuffer; (void)nNumberOfBytesToRead;
    HmObj *o = ObjFromHandle(hFile);
    if (!o || o->kind != HK_PIPE_CLIENT || !lpOverlapped) HmFail("ReadFile other than an overlapped pipe read");
    HmPipeClient *c = (HmPipeClient *)o;
    if (lpNumberOfBytesRead) *lpNumberOfBytesRead = 0;
    if (c->server->closed) {
        g_lastError = ERROR_BROKEN_PIPE;
        return FALSE;
    }
    /* The SDK never writes to the pipe, so the read stays pending. */
    lpOverlapped->Internal = (ULONG_PTR)STATUS_PENDING;
    c->pending = lpOverlapped;
    c->canceled = 0;
    g_lastError = ERROR_IO_PENDING;
    return FALSE;
}

BOOL WINAPI HmCancelIoEx(HANDLE hFile, LPOVERLAPPED lpOverlapped)
{
    HmObj *o = ObjFromHandle(hFile);
    if (!o || o->kind != HK_PIPE_CLIENT) HmFail("CancelIoEx on a handle that is no pipe");
    HmPipeClient *c = (HmPipeClient *)o;
    if (!c->pending || (lpOverlapped && c->pending != lpOverlapped)) {
        g_lastError = ERROR_NOT_FOUND;
        return FALSE;
    }
    c->pending->Internal = (ULONG_PTR)STATUS_CANCELLED;
    HmObj *ev = ObjFromHandle(c->pending->hEvent);
    if (ev && ev->kind == HK_EVENT) EventSet((HmEvent *)ev);
    c->pending = NULL;
    c->canceled = 1;
    return TRUE;
}

BOOL WINAPI HmGetOverlappedResult(HANDLE hFile, LPOVERLAPPED lpOverlapped,
                                  LPDWORD lpNumberOfBytesTransferred, BOOL bWait)
{
    (void)bWait;
    HmObj *o = ObjFromHandle(hFile);
    if (!o || o->kind != HK_PIPE_CLIENT) HmFail("GetOverlappedResult on a handle that is no pipe");
    if (((HmPipeClient *)o)->pending == lpOverlapped) HmFail("GetOverlappedResult would block on a read still pending");
    if (lpNumberOfBytesTransferred) *lpNumberOfBytesTransferred = 0;
    g_lastError = lpOverlapped->Internal == (ULONG_PTR)STATUS_CANCELLED ? ERROR_OPERATION_ABORTED : ERROR_BROKEN_PIPE;
    return FALSE;
}

BOOL WINAPI HmGetNamedPipeServerProcessId(HANDLE Pipe, PULONG ServerProcessId)
{
    HmObj *o = ObjFromHandle(Pipe);
    if (!o || o->kind != HK_PIPE_CLIENT) HmFail("GetNamedPipeServerProcessId on a handle that is no pipe");
    *ServerProcessId = ((HmPipeClient *)o)->server->processId;
    return TRUE;
}

/* ------------------------------------------------------------------ */
/*  Security                                                           */
/* ------------------------------------------------------------------ */

/* Allocations the stand-ins hand out and LocalFree takes back: a
 * descriptor, a SID string, a returned descriptor. */
typedef struct HmLocal { uint32_t magic; uint32_t kind; } HmLocal;
#define HM_LOCAL_MAGIC 0x4C4D4831u
enum { LOCAL_SD = 1, LOCAL_FOUND_SD, LOCAL_STRING };

static void *LocalNew(uint32_t kind, size_t extra)
{
    HmLocal *l = (HmLocal *)calloc(1, sizeof(HmLocal) + extra);
    if (!l) HmFail("out of memory");
    l->magic = HM_LOCAL_MAGIC;
    l->kind = kind;
    return l;
}

static HmLocal *AsLocal(const void *p) { return (HmLocal *)p; }

static BYTE g_sidLocalService[SECURITY_MAX_SID_SIZE];
static BYTE g_sidSystem[SECURITY_MAX_SID_SIZE];
static BYTE g_sidAdmins[SECURITY_MAX_SID_SIZE];
static BYTE g_sidUser[SECURITY_MAX_SID_SIZE];
static BYTE g_daclToken[16];

static void SidsInit(void)
{
    DWORD n;
    n = sizeof(g_sidLocalService); CreateWellKnownSid(WinLocalServiceSid, NULL, g_sidLocalService, &n);
    n = sizeof(g_sidSystem); CreateWellKnownSid(WinLocalSystemSid, NULL, g_sidSystem, &n);
    n = sizeof(g_sidAdmins); CreateWellKnownSid(WinBuiltinAdministratorsSid, NULL, g_sidAdmins, &n);
    /* A user's SID: Builtin Users stands in for any account a program that
     * is not elevated runs under. */
    n = sizeof(g_sidUser); CreateWellKnownSid(WinBuiltinUsersSid, NULL, g_sidUser, &n);
}

static PSID OwnerSid(int owner)
{
    switch (owner) {
    case OWN_LOCALSERVICE: return (PSID)g_sidLocalService;
    case OWN_SYSTEM:       return (PSID)g_sidSystem;
    case OWN_ADMINS:       return (PSID)g_sidAdmins;
    default:               return (PSID)g_sidUser;
    }
}

/* A descriptor the stand-in hands out keeps the SDDL text it came from
 * after its header, and its DACL is a pointer to that text, so every later
 * use names the text (review F3). */
BOOL WINAPI HmConvertStringSecurityDescriptorToSecurityDescriptorW(LPCWSTR StringSecurityDescriptor,
    DWORD StringSDRevision, PSECURITY_DESCRIPTOR *SecurityDescriptor, PULONG SecurityDescriptorSize)
{
    if (!StringSecurityDescriptor) HmFail("a security descriptor string is required");
    size_t n = wcslen(StringSecurityDescriptor);
    if (n > 256) HmFail("a security descriptor string over 256 characters");
    HmLocal *l = (HmLocal *)LocalNew(LOCAL_SD, (n + 1) * sizeof(WCHAR));
    memcpy(l + 1, StringSecurityDescriptor, (n + 1) * sizeof(WCHAR));
    {
        uint8_t rec[4 + 512];
        memcpy(rec, &StringSDRevision, 4);
        memcpy(rec + 4, StringSecurityDescriptor, n * 2);
        RecOut("security.sddl", rec, (uint32_t)(4 + n * 2));
    }
    *SecurityDescriptor = l;
    if (SecurityDescriptorSize) *SecurityDescriptorSize = (ULONG)(sizeof(HmLocal) + (n + 1) * sizeof(WCHAR));
    return TRUE;
}

static const WCHAR *DescriptorSddl(const void *sd)
{
    const HmLocal *l = (const HmLocal *)sd;
    if (!l || l->magic != HM_LOCAL_MAGIC || l->kind != LOCAL_SD) HmFail("a descriptor the stand-ins did not hand out");
    return (const WCHAR *)(l + 1);
}

BOOL WINAPI HmGetSecurityDescriptorDacl(PSECURITY_DESCRIPTOR pSecurityDescriptor, LPBOOL lpbDaclPresent,
                                        PACL *pDacl, LPBOOL lpbDaclDefaulted)
{
    HmLocal *l = AsLocal(pSecurityDescriptor);
    if (!l || l->magic != HM_LOCAL_MAGIC || l->kind != LOCAL_SD) HmFail("GetSecurityDescriptorDacl on a foreign descriptor");
    *lpbDaclPresent = TRUE;
    *pDacl = (PACL)(l + 1);
    *lpbDaclDefaulted = FALSE;
    return TRUE;
}

DWORD WINAPI HmGetSecurityInfo(HANDLE handle, SE_OBJECT_TYPE ObjectType, SECURITY_INFORMATION SecurityInfo,
                               PSID *ppsidOwner, PSID *ppsidGroup, PACL *ppDacl, PACL *ppSacl,
                               PSECURITY_DESCRIPTOR *ppSecurityDescriptor)
{
    (void)ObjectType; (void)ppsidGroup; (void)ppSacl;
    HmObj *o = ObjFromHandle(handle);
    if (!o) HmFail("GetSecurityInfo on a handle that is not open");
    if ((SecurityInfo & OWNER_SECURITY_INFORMATION) && ppsidOwner) *ppsidOwner = OwnerSid(o->owner);
    if ((SecurityInfo & DACL_SECURITY_INFORMATION) && ppDacl) *ppDacl = o->hasDacl ? (PACL)g_daclToken : NULL;
    if (ppSecurityDescriptor) *ppSecurityDescriptor = LocalNew(LOCAL_FOUND_SD, 0);
    return ERROR_SUCCESS;
}

DWORD WINAPI HmSetSecurityInfo(HANDLE handle, SE_OBJECT_TYPE ObjectType, SECURITY_INFORMATION SecurityInfo,
                               PSID psidOwner, PSID psidGroup, PACL pDacl, PACL pSacl)
{
    (void)ObjectType; (void)psidOwner; (void)psidGroup; (void)pSacl;
    HmObj *o = ObjFromHandle(handle);
    if (!o) HmFail("SetSecurityInfo on a handle that is not open");
    if (SecurityInfo & DACL_SECURITY_INFORMATION) o->hasDacl = pDacl != NULL;
    {
        /* The information flags, the object's name and the SDDL text the
         * DACL applied came from (review F3). */
        const WCHAR *sddl = L"";
        if (pDacl) sddl = DescriptorSddl((const HmLocal *)pDacl - 1);
        size_t n = wcslen(o->name), m = wcslen(sddl);
        uint8_t rec[6 + 192 + 512];
        memcpy(rec, &SecurityInfo, 4);
        rec[4] = (uint8_t)n;
        rec[5] = 0;
        memcpy(rec + 6, o->name, n * 2);
        memcpy(rec + 6 + n * 2, sddl, m * 2);
        RecOut("event.dacl", rec, (uint32_t)(6 + n * 2 + m * 2));
    }
    return ERROR_SUCCESS;
}

BOOL WINAPI HmIsWellKnownSid(PSID pSid, WELL_KNOWN_SID_TYPE WellKnownSidType)
{
    return IsWellKnownSid(pSid, WellKnownSidType);
}

BOOL WINAPI HmConvertSidToStringSidW(PSID Sid, LPWSTR *StringSid)
{
    LPWSTR real = NULL;
    if (!ConvertSidToStringSidW(Sid, &real)) return FALSE;
    size_t n = wcslen(real) + 1;
    HmLocal *l = (HmLocal *)LocalNew(LOCAL_STRING, n * sizeof(WCHAR));
    memcpy(l + 1, real, n * sizeof(WCHAR));
    LocalFree(real);
    *StringSid = (LPWSTR)(l + 1);
    return TRUE;
}

HLOCAL WINAPI HmLocalFree(HLOCAL hMem)
{
    if (!hMem) return NULL;
    HmLocal *l = AsLocal(hMem);
    if (l->magic != HM_LOCAL_MAGIC) {
        /* A SID string points past its header. */
        l = (HmLocal *)hMem - 1;
        if (l->magic != HM_LOCAL_MAGIC || l->kind != LOCAL_STRING) HmFail("LocalFree of memory the stand-ins did not hand out");
    }
    l->magic = 0;
    free(l);
    return NULL;
}

/* ------------------------------------------------------------------ */
/*  Locks, clock, Rtl                                                  */
/* ------------------------------------------------------------------ */

/* A critical section here is a hold count in its own RecursionCount.
 * Nothing contends on one thread, and a fiber switch while it is held
 * stops the run (ThreadRun, FiberWait). A deleted one is marked in its
 * SpinCount, and any later use of it stops the run. */
#define HM_CS_DELETED ((ULONG_PTR)0xDE1E7EDu)
VOID WINAPI HmInitializeCriticalSection(LPCRITICAL_SECTION cs) { memset(cs, 0, sizeof(*cs)); }
VOID WINAPI HmEnterCriticalSection(LPCRITICAL_SECTION cs)
{
    if (cs->SpinCount == HM_CS_DELETED) HmFail("EnterCriticalSection after DeleteCriticalSection");
    cs->RecursionCount++;
    g_locksHeld++;
}
VOID WINAPI HmLeaveCriticalSection(LPCRITICAL_SECTION cs)
{
    if (cs->RecursionCount <= 0) HmFail("LeaveCriticalSection without Enter");
    cs->RecursionCount--;
    g_locksHeld--;
}
VOID WINAPI HmDeleteCriticalSection(LPCRITICAL_SECTION cs)
{
    if (cs->SpinCount == HM_CS_DELETED) HmFail("DeleteCriticalSection twice");
    if (cs->RecursionCount != 0) HmFail("DeleteCriticalSection while held");
    cs->SpinCount = HM_CS_DELETED;
}

/* A slim reader/writer lock, taken exclusive, is a held mark in its own
 * word. SRWLOCK_INIT is all zero, so a zeroed device context holds free
 * ones, and an SRW lock is never deleted. Taking one already held would
 * deadlock a real thread, and stops the run here. */
void (*g_srwHook)(void);
void (*g_pollHook)(void);
VOID WINAPI HmAcquireSRWLockExclusive(PSRWLOCK l)
{
    if (g_srwHook && g_current) { void (*hook)(void) = g_srwHook; g_srwHook = NULL; hook(); }
    if (l->Ptr != NULL) HmFail("AcquireSRWLockExclusive on a lock already held");
    l->Ptr = (PVOID)1;
    g_locksHeld++;
}
VOID WINAPI HmReleaseSRWLockExclusive(PSRWLOCK l)
{
    if (l->Ptr == NULL) HmFail("ReleaseSRWLockExclusive on a free lock");
    l->Ptr = NULL;
    g_locksHeld--;
}

ULONGLONG WINAPI HmGetTickCount64(VOID) { return g_clockMs; }

VOID WINAPI HmGetSystemTimeAsFileTime(LPFILETIME ft)
{
    ULONGLONG t = HM_FILETIME_BASE + g_clockMs * 10000ULL;
    ft->dwLowDateTime = (DWORD)t;
    ft->dwHighDateTime = (DWORD)(t >> 32);
}

VOID NTAPI HmRtlInitUnicodeString(PUNICODE_STRING d, PCWSTR s)
{
    size_t n = s ? wcslen(s) : 0;
    d->Buffer = (PWCH)s;
    d->Length = (USHORT)(n * sizeof(WCHAR));
    d->MaximumLength = (USHORT)(s ? (n + 1) * sizeof(WCHAR) : 0);
}

SIZE_T NTAPI HmRtlCompareMemory(const VOID *a, const VOID *b, SIZE_T n)
{
    const uint8_t *x = (const uint8_t *)a, *y = (const uint8_t *)b;
    SIZE_T i = 0;
    while (i < n && x[i] == y[i]) i++;
    return i;
}

/* ------------------------------------------------------------------ */
/*  World                                                              */
/* ------------------------------------------------------------------ */

void WorldReset(void)
{
    static int sids;
    if (!sids) { SidsInit(); sids = 1; }
    if (g_current) HmFail("world reset inside a thread");
    WdfReset();
    g_releasingDevice = NULL;
    g_unmapNotes[0] = 0;
    g_unmapNoteLen = 0;
    g_unmapNoteCount = 0;
    while (g_allObjs) {
        HmObj *n = g_allObjs->nextAll;
        if (g_allObjs->kind == HK_THREAD && ((HmThread *)g_allObjs)->fiber)
            DeleteFiber(((HmThread *)g_allObjs)->fiber);
        if (g_allObjs->kind == HK_SECTION) free(((HmSection *)g_allObjs)->buf);
        free(g_allObjs);
        g_allObjs = n;
    }
    memset(g_handles, 0, sizeof(g_handles));
    KeysReset();
    g_lastError = 0;
    g_clockMs = 0;
    g_locksHeld = 0;
}
