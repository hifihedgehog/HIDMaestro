// HIDMaestro VR IPC protocol (issue #32).
//
// One pagefile-backed section, created by the CONSUMER (the app using
// HIDMaestro.Core, running elevated so Global\ works), opened read/write
// by the OpenVR driver inside vrserver.exe (interactive user). The C#
// mirror of every constant and offset in this file lives in
// sdk/HIDMaestro.Core/Internal/VrSharedMemory.cs; the two are kept in
// lockstep by test/probes/vr_controller_smoke, which plays the driver's
// role against the real C# producer and fails on any drift.
//
// Direction summary:
//   consumer -> driver : config (which hands exist), input frames (seqlock)
//   driver  -> consumer: haptic events (ring), runtime status + HMD pose
//
// The input frame is latest-wins under a seqlock, the exact shape of
// SharedMemoryIO.WriteInputFrame / driver.c ReadSharedInput (odd = writing,
// even = stable, 4-retry read). The haptic ring is the v1.1.40 output ring
// with roles reversed: single producer (driver), private reader cursor on
// the consumer side, tail-wins skip when more than HMVR_HAPTIC_SLOTS behind.

#pragma once

#include <stdint.h>

#define HMVR_SECTION_NAME       L"Global\\HIDMaestroVR"
#define HMVR_INPUT_EVENT_NAME   L"Global\\HIDMaestroVR_InputEvent"
#define HMVR_HAPTIC_EVENT_NAME  L"Global\\HIDMaestroVR_HapticEvent"

#define HMVR_MAGIC              0x52564D48u   /* 'HMVR' little-endian */
#define HMVR_ABI_VERSION        1u

#define HMVR_HAND_COUNT         2             /* [0] = left, [1] = right */
#define HMVR_SCALAR_COUNT       16
#define HMVR_HAPTIC_SLOTS       64

/* Button bit assignments in VRControllerState.buttonBits. The generic
   input profile creates exactly these components; the C# HMVRButton enum
   mirrors the values. */
#define HMVR_BTN_SYSTEM         (1u << 0)     /* /input/system/click   */
#define HMVR_BTN_A_CLICK        (1u << 1)     /* /input/a/click        */
#define HMVR_BTN_A_TOUCH        (1u << 2)     /* /input/a/touch        */
#define HMVR_BTN_B_CLICK        (1u << 3)     /* /input/b/click        */
#define HMVR_BTN_B_TOUCH        (1u << 4)     /* /input/b/touch        */
#define HMVR_BTN_TRIGGER_CLICK  (1u << 5)     /* /input/trigger/click  */
#define HMVR_BTN_GRIP_CLICK     (1u << 6)     /* /input/grip/click     */
#define HMVR_BTN_STICK_CLICK    (1u << 7)     /* /input/joystick/click */

/* Scalar slot assignments in VRControllerState.scalars. Trigger and grip
   are one-sided [0..1]; the stick axes are two-sided [-1..+1], matching
   the VRScalarUnits each component is created with. */
#define HMVR_SCALAR_TRIGGER     0
#define HMVR_SCALAR_GRIP        1
#define HMVR_SCALAR_STICK_X     2
#define HMVR_SCALAR_STICK_Y     3

/* Config.handsActive flags */
#define HMVR_HAND_LEFT_ACTIVE   (1u << 0)
#define HMVR_HAND_RIGHT_ACTIVE  (1u << 1)

#pragma pack(push, 1)

typedef struct HMVR_ControllerState
{
    uint32_t buttonBits;
    float    scalars[HMVR_SCALAR_COUNT];

    /* Pose override. poseValid == 0 leaves the driver on its default
       HMD-anchored pose (the simplecontroller GetPose shape). Nonzero
       means position/orientation below are authoritative, in the
       SteamVR standing-universe frame: position in meters, orientation
       as a unit quaternion (w, x, y, z). */
    uint32_t poseValid;
    float    position[3];
    float    orientation[4];
} HMVR_ControllerState;

typedef struct HMVR_InputFrame
{
    uint64_t             timestampQpc;
    HMVR_ControllerState hand[HMVR_HAND_COUNT];
} HMVR_InputFrame;

typedef struct HMVR_Config
{
    uint32_t handsActive;      /* HMVR_HAND_*_ACTIVE flags */
    uint32_t reserved[7];
} HMVR_Config;

typedef struct HMVR_HapticReply
{
    uint64_t seqNo;            /* 0 = slot never written; first write = 1 */
    uint32_t hand;             /* 0 = left, 1 = right */
    float    durationSeconds;
    float    frequency;
    float    amplitude;
    uint64_t timestampQpc;
} HMVR_HapticReply;

/* Driver -> consumer status block, written by the driver each RunFrame
   under its own seqlock. hmdPose gives the consumer the real headset
   pose (PadForge #49's lean-to-steer input source). hmdPoseValid is 0
   until vrserver reports a tracked HMD. */
typedef struct HMVR_Status
{
    uint32_t statusSeqNo;      /* seqlock: odd = writing */
    uint32_t driverActive;     /* 1 once Init completed inside vrserver */
    uint32_t controllersLive;  /* HMVR_HAND_*_ACTIVE currently registered */
    uint32_t hmdPoseValid;
    float    hmdPosition[3];
    float    hmdOrientation[4];
    uint64_t driverHeartbeatQpc;
} HMVR_Status;

typedef struct HMVR_Section
{
    uint32_t         magic;         /* HMVR_MAGIC */
    uint32_t         abiVersion;    /* HMVR_ABI_VERSION, strict match */
    uint64_t         consumerPid;   /* 0 = no consumer; CAS handshake */
    uint64_t         configSeqNo;   /* bumped by consumer after config edits */
    HMVR_Config      config;

    uint32_t         inputSeqNo;    /* seqlock for input below */
    HMVR_InputFrame  input;

    uint32_t         pad0;          /* 8-aligns hapticHead and every ring
                                       slot's seqNo: those are read and
                                       written OUTSIDE any seqlock, so
                                       they must be single-copy-atomic,
                                       which x64 guarantees only for
                                       aligned 8-byte accesses. */
    uint64_t         hapticHead;    /* monotonic; slot = (seqNo-1) % slots */
    HMVR_HapticReply haptic[HMVR_HAPTIC_SLOTS];

    HMVR_Status      status;
} HMVR_Section;

#pragma pack(pop)

#define HMVR_SECTION_SIZE  sizeof(HMVR_Section)

/* Layout pins. These are the C# mirror's constants
   (VrSharedMemory.cs); a drift on either side breaks this compile. */
#ifdef __cplusplus
static_assert( sizeof( HMVR_ControllerState ) == 100, "state size" );
static_assert( sizeof( HMVR_InputFrame ) == 208, "frame size" );
static_assert( sizeof( HMVR_Config ) == 32, "config size" );
static_assert( sizeof( HMVR_HapticReply ) == 32, "haptic slot size" );
static_assert( sizeof( HMVR_Status ) == 52, "status size" );
static_assert( offsetof( HMVR_Section, consumerPid ) == 8, "consumerPid" );
static_assert( offsetof( HMVR_Section, configSeqNo ) == 16, "configSeqNo" );
static_assert( offsetof( HMVR_Section, config ) == 24, "config" );
static_assert( offsetof( HMVR_Section, inputSeqNo ) == 56, "inputSeqNo" );
static_assert( offsetof( HMVR_Section, input ) == 60, "input" );
static_assert( offsetof( HMVR_Section, hapticHead ) == 272, "hapticHead" );
static_assert( offsetof( HMVR_Section, hapticHead ) % 8 == 0, "hapticHead alignment" );
static_assert( offsetof( HMVR_Section, haptic ) == 280, "haptic ring" );
static_assert( offsetof( HMVR_Section, haptic ) % 8 == 0, "ring alignment" );
static_assert( offsetof( HMVR_Section, status ) == 2328, "status" );
static_assert( sizeof( HMVR_Section ) == 2380, "section size" );
#endif
