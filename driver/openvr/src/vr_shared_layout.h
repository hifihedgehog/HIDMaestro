/*
 * vr_shared_layout.h: HIDMaestro VR IPC section layout (ABI v1).
 *
 * One 4 KiB pagefile-backed section, Global\HIDMaestroVR, shared between
 * the consumer process (elevated, creates the section, writes input) and
 * this OpenVR driver inside vrserver.exe (interactive user, reads input,
 * writes haptic replies).
 *
 * Every offset here is mirrored byte-for-byte by the SDK's
 * Internal\VrSharedMemoryIO.cs. Changing ANY field requires bumping
 * HMVR_ABI_VERSION on both sides. The driver refuses a mismatched
 * section rather than misreading it.
 *
 * Concurrency contract (mirrors the proven HID sections):
 *   - Input frame:  seqlock, single logical writer (the SDK serializes
 *     both hands' writes behind one process lock), odd = write in
 *     progress, even = stable. Reader retries up to 4 times, same as
 *     ReadSharedInput in driver.c.
 *   - Haptic ring:  v1.1.40 ring shape from driver.c PublishOutput with
 *     roles reversed. Single producer (vrserver's RunFrame thread), so
 *     no writer lock. SeqNo 0 is reserved; first write is SeqNo 1; slot
 *     index is (SeqNo - 1) % HMVR_HAPTIC_RING_SLOTS. The reader keeps a
 *     private cursor and validates each slot's SeqNo before and after
 *     the copy for torn-write detection.
 */
#pragma once

#include <stddef.h>
#include <stdint.h>

#define HMVR_SECTION_NAME       L"Global\\HIDMaestroVR"
#define HMVR_INPUT_EVENT_NAME   L"Global\\HIDMaestroVR_InputEvent"
#define HMVR_HAPTIC_EVENT_NAME  L"Global\\HIDMaestroVR_HapticEvent"

#define HMVR_MAGIC              0x52564D48u /* 'HMVR' little-endian */
#define HMVR_ABI_VERSION        1u
#define HMVR_SECTION_SIZE       4096u

#define HMVR_HAND_LEFT          0
#define HMVR_HAND_RIGHT         1
#define HMVR_HAND_COUNT         2

#define HMVR_PROFILE_KHR_SIMPLE        0u
#define HMVR_PROFILE_VALVE_INDEX       1u
#define HMVR_PROFILE_MICROSOFT_MOTION  2u
#define HMVR_PROFILE_COUNT             3u

#define HMVR_SCALAR_COUNT       16
#define HMVR_HAPTIC_RING_SLOTS  64

/* Universal button-bit assignments for VRControllerState.ButtonBits.
 * Fixed across profiles; a profile that lacks a component simply never
 * reads its bit. Mirrored by HMVRButton in HMVRController.cs. */
#define HMVR_BTN_SYSTEM_CLICK      (1u << 0)
#define HMVR_BTN_SYSTEM_TOUCH      (1u << 1)
#define HMVR_BTN_A_CLICK           (1u << 2)
#define HMVR_BTN_A_TOUCH           (1u << 3)
#define HMVR_BTN_B_CLICK           (1u << 4)
#define HMVR_BTN_B_TOUCH           (1u << 5)
#define HMVR_BTN_TRIGGER_CLICK     (1u << 6)
#define HMVR_BTN_TRIGGER_TOUCH     (1u << 7)
#define HMVR_BTN_GRIP_CLICK        (1u << 8)
#define HMVR_BTN_GRIP_TOUCH        (1u << 9)
#define HMVR_BTN_THUMBSTICK_CLICK  (1u << 10)
#define HMVR_BTN_THUMBSTICK_TOUCH  (1u << 11)
#define HMVR_BTN_TRACKPAD_CLICK    (1u << 12)
#define HMVR_BTN_TRACKPAD_TOUCH    (1u << 13)
#define HMVR_BTN_MENU_CLICK        (1u << 14)

/* Universal scalar-slot assignments for VRControllerState.Scalars[].
 * Two-sided scalars (sticks/trackpads) carry [-1..1]; one-sided carry
 * [0..1]. Mirrored by HMVRScalar in HMVRController.cs. */
#define HMVR_SCALAR_TRIGGER_VALUE   0
#define HMVR_SCALAR_GRIP_VALUE      1
#define HMVR_SCALAR_GRIP_FORCE      2
#define HMVR_SCALAR_THUMBSTICK_X    3
#define HMVR_SCALAR_THUMBSTICK_Y    4
#define HMVR_SCALAR_TRACKPAD_X      5
#define HMVR_SCALAR_TRACKPAD_Y      6
#define HMVR_SCALAR_TRACKPAD_FORCE  7
#define HMVR_SCALAR_FINGER_INDEX    8
#define HMVR_SCALAR_FINGER_MIDDLE   9
#define HMVR_SCALAR_FINGER_RING     10
#define HMVR_SCALAR_FINGER_PINKY    11

#pragma pack(push, 4)

typedef struct HMVR_CONTROLLER_STATE
{
    uint32_t ButtonBits;
    float    Scalars[HMVR_SCALAR_COUNT];
} HMVR_CONTROLLER_STATE; /* 68 bytes */

typedef struct HMVR_HAPTIC_SLOT
{
    /* SeqNo published LAST by the producer (after the payload + barrier),
     * validated first AND re-checked after the copy by the consumer. */
    volatile uint64_t SeqNo;
    uint32_t ControllerIdx;   /* HMVR_HAND_LEFT / HMVR_HAND_RIGHT */
    uint32_t ComponentIdx;    /* always 0. One haptic component per device */
    float    Amplitude;       /* 0..1 */
    float    DurationSeconds;
    float    Frequency;       /* Hz */
    uint32_t Reserved;
    uint64_t TimestampQpc;
} HMVR_HAPTIC_SLOT; /* 40 bytes */

typedef struct HMVR_SHARED_SECTION
{
    uint32_t Magic;                        /* @0   HMVR_MAGIC              */
    uint32_t AbiVersion;                   /* @4   HMVR_ABI_VERSION        */

    /* Handshake. ConsumerPid is CAS'd 0 -> pid by the first hand's
     * Connect() and back to 0 by the last Disconnect(). DriverPid is
     * written by the driver worker once it has validated Magic +
     * AbiVersion, and zeroed on Cleanup. The SDK's DriverConnected
     * property reads it. */
    volatile uint64_t ConsumerPid;         /* @8  */
    volatile uint64_t DriverPid;           /* @16 */

    /* Config block, guarded by ConfigSeqNo (bumped AFTER the fields are
     * written; the driver treats any change as "re-read everything").
     * ActiveHandsMask: bit0 = left active, bit1 = right active. */
    volatile uint64_t ConfigSeqNo;         /* @24 */
    volatile uint32_t ActiveHandsMask;     /* @32 */
    uint32_t Profile[HMVR_HAND_COUNT];     /* @36, @40  HMVR_PROFILE_*     */
    int32_t  Priority[HMVR_HAND_COUNT];    /* @44, @48  hand-selection     */

    /* Input frame, seqlock-guarded by InputSeqNo. */
    volatile uint32_t InputSeqNo;          /* @52 */
    uint64_t TimestampQpc;                 /* @56 */
    HMVR_CONTROLLER_STATE Ctrl[HMVR_HAND_COUNT]; /* @64 (L), @132 (R)      */

    /* Haptic reply ring, driver -> SDK. */
    volatile uint64_t HapticHead;          /* @200 */
    HMVR_HAPTIC_SLOT Haptic[HMVR_HAPTIC_RING_SLOTS]; /* @208 .. 2768       */
} HMVR_SHARED_SECTION;

#pragma pack(pop)

#ifdef __cplusplus
static_assert(sizeof(HMVR_CONTROLLER_STATE) == 68, "HMVR_CONTROLLER_STATE ABI drift");
static_assert(sizeof(HMVR_HAPTIC_SLOT) == 40, "HMVR_HAPTIC_SLOT ABI drift");
static_assert(offsetof(HMVR_SHARED_SECTION, ConsumerPid) == 8, "ABI drift: ConsumerPid");
static_assert(offsetof(HMVR_SHARED_SECTION, DriverPid) == 16, "ABI drift: DriverPid");
static_assert(offsetof(HMVR_SHARED_SECTION, ConfigSeqNo) == 24, "ABI drift: ConfigSeqNo");
static_assert(offsetof(HMVR_SHARED_SECTION, ActiveHandsMask) == 32, "ABI drift: ActiveHandsMask");
static_assert(offsetof(HMVR_SHARED_SECTION, Profile) == 36, "ABI drift: Profile");
static_assert(offsetof(HMVR_SHARED_SECTION, Priority) == 44, "ABI drift: Priority");
static_assert(offsetof(HMVR_SHARED_SECTION, InputSeqNo) == 52, "ABI drift: InputSeqNo");
static_assert(offsetof(HMVR_SHARED_SECTION, TimestampQpc) == 56, "ABI drift: TimestampQpc");
static_assert(offsetof(HMVR_SHARED_SECTION, Ctrl) == 64, "ABI drift: Ctrl");
static_assert(offsetof(HMVR_SHARED_SECTION, HapticHead) == 200, "ABI drift: HapticHead");
static_assert(offsetof(HMVR_SHARED_SECTION, Haptic) == 208, "ABI drift: Haptic");
static_assert(sizeof(HMVR_SHARED_SECTION) <= HMVR_SECTION_SIZE, "section overflow");
#endif
