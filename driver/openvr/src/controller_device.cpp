// HIDMaestro virtual VR controller device (issue #32).
//
// Grounding, per the cite-verify rule:
//   * Activate property/component setup: openvr samples simplecontroller
//     controller_device_driver.cpp Activate (property container, role hint,
//     input profile path, CreateBooleanComponent / CreateScalarComponent /
//     CreateHapticComponent).
//   * HMD-anchored default pose: the same file's GetPose verbatim shape
//     (GetRawTrackedDevicePoses on index 0, vrmath offsets), including the
//     5 ms TrackedDevicePoseUpdated thread.
//   * Haptic event filtering by component handle: the same file's
//     MyProcessEvent.
//   * SetConnected via a re-published pose with deviceIsConnected=false:
//     VRCHOTAS hotas_controller_device SetDeviceConnected (pattern only,
//     no code copied; VRCHOTAS is GPL and referenced read-only).
//   * Prop_ControllerHandSelectionPriority_Int32 at activate (issue #51):
//     opengloves knuckle_device_driver.cpp:108 and VRCHOTAS
//     hotas_controller_device.cpp:96, the two reference drivers that have
//     to win a hand assignment without lighthouse tracking. Valve's
//     Driver_API_Documentation.md "Controller roles" names this property
//     as the only lever a driver has over the runtime's hand assignment.
//   * Reading the value from the driver's own settings section:
//     opengloves device_configuration.cpp GetDriverConfiguration, which
//     reads its keys out of the same driver_<name> section that
//     resources/settings/default.vrsettings seeds.

#include "controller_device.h"

#include <windows.h>

#include <chrono>

#include "driver_log.h"
#include "vrmath.h"

HmVrControllerDevice::HmVrControllerDevice( vr::ETrackedControllerRole role )
{
    role_ = role;
    const bool left = role == vr::TrackedControllerRole_LeftHand;
    serial_number_ = left ? "HMVR-LEFT-0001" : "HMVR-RIGHT-0001";
    model_number_ = "HIDMaestro Virtual Controller";
}

int32_t HmVrControllerDevice::HandSelectionPriority( bool *fromSettings )
{
    // resources/settings/default.vrsettings seeds the key, so the normal
    // answer is VRSettingsError_None and the value comes from SteamVR
    // (default file, or steamvr.vrsettings when a machine overrides it).
    // VRSettingsError_UnsetSettingHasNoDefault means our default file did
    // not load, which is worth saying out loud in the log rather than
    // silently shipping a number nobody can trace.
    vr::EVRSettingsError err = vr::VRSettingsError_None;
    const int32_t configured = vr::VRSettings()->GetInt32(
        HMVR_SETTINGS_SECTION, HMVR_SETTINGS_KEY_HAND_PRIORITY, &err );
    const bool ok = err == vr::VRSettingsError_None;
    if ( fromSettings != nullptr )
        *fromSettings = ok;
    return ok ? configured : HMVR_DEFAULT_HAND_PRIORITY;
}

vr::EVRInitError HmVrControllerDevice::Activate( uint32_t unObjectId )
{
    is_active_ = true;
    device_index_ = unObjectId;

    vr::PropertyContainerHandle_t container =
        vr::VRProperties()->TrackedDeviceToPropertyContainer( device_index_ );

    vr::VRProperties()->SetStringProperty( container, vr::Prop_ModelNumber_String, model_number_.c_str() );
    vr::VRProperties()->SetStringProperty( container, vr::Prop_ManufacturerName_String, "HIDMaestro" );
    vr::VRProperties()->SetInt32Property( container, vr::Prop_ControllerRoleHint_Int32, role_ );

    // The role hint is advisory. Hand assignment, which is what
    // /user/hand/left|right and every role-addressed surface read, is
    // decided by the runtime, and a driver's only influence over it is
    // this property. Measured on the rig: left unset it reads back 0,
    // the same value SteamVR's own Index profile ends up with, so which
    // device wins is an unstated runtime tiebreak. A positive value makes
    // the outcome ours to state. 1000 is VRCHOTAS's
    // kMappedHandSelectionPriority, chosen over opengloves' INT32_MAX so
    // the value stays adjustable in both directions: a machine whose real
    // controllers should keep the hands sets hand_selection_priority
    // negative in steamvr.vrsettings, which is the register Valve's own
    // oculus touch_profile.json uses with hand_priority -1.
    bool priorityFromSettings = false;
    const int32_t handPriority = HandSelectionPriority( &priorityFromSettings );
    vr::VRProperties()->SetInt32Property( container,
        vr::Prop_ControllerHandSelectionPriority_Int32, handPriority );
    vr::VRProperties()->SetStringProperty( container, vr::Prop_RegisteredDeviceType_String,
        role_ == vr::TrackedControllerRole_LeftHand ? "hidmaestro/left" : "hidmaestro/right" );
    vr::VRProperties()->SetStringProperty( container, vr::Prop_ControllerType_String, "hidmaestro_controller" );
    vr::VRProperties()->SetStringProperty( container, vr::Prop_InputProfilePath_String,
        "{hidmaestro}/input/hidmaestro_controller_profile.json" );

    // Legacy-lane axis classification (issue #55). vrserver's legacy
    // emulation does not synthesize Prop_AxisNType_Int32 for IVRDriverInput
    // drivers, so a GetControllerState consumer classifying axes by these
    // properties reads k_eControllerAxis_None unless the driver states them.
    // VRCHOTAS hotas_controller_device.cpp:80-84 sets them explicitly for
    // the same reason and the same input surface. The values mirror the
    // legacy binding: axis0 = joystick, axis1 = trigger pull, axis2 = grip
    // pull.
    vr::VRProperties()->SetInt32Property( container, vr::Prop_Axis0Type_Int32, vr::k_eControllerAxis_Joystick );
    vr::VRProperties()->SetInt32Property( container, vr::Prop_Axis1Type_Int32, vr::k_eControllerAxis_Trigger );
    vr::VRProperties()->SetInt32Property( container, vr::Prop_Axis2Type_Int32, vr::k_eControllerAxis_Trigger );

    vr::VRDriverInput()->CreateBooleanComponent( container, "/input/system/click", &input_handles_[ HmVrComponent_system_click ] );
    vr::VRDriverInput()->CreateBooleanComponent( container, "/input/a/click", &input_handles_[ HmVrComponent_a_click ] );
    vr::VRDriverInput()->CreateBooleanComponent( container, "/input/a/touch", &input_handles_[ HmVrComponent_a_touch ] );
    vr::VRDriverInput()->CreateBooleanComponent( container, "/input/b/click", &input_handles_[ HmVrComponent_b_click ] );
    vr::VRDriverInput()->CreateBooleanComponent( container, "/input/b/touch", &input_handles_[ HmVrComponent_b_touch ] );

    vr::VRDriverInput()->CreateScalarComponent( container, "/input/trigger/value",
        &input_handles_[ HmVrComponent_trigger_value ],
        vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedOneSided );
    vr::VRDriverInput()->CreateBooleanComponent( container, "/input/trigger/click", &input_handles_[ HmVrComponent_trigger_click ] );

    vr::VRDriverInput()->CreateScalarComponent( container, "/input/grip/value",
        &input_handles_[ HmVrComponent_grip_value ],
        vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedOneSided );
    vr::VRDriverInput()->CreateBooleanComponent( container, "/input/grip/click", &input_handles_[ HmVrComponent_grip_click ] );

    vr::VRDriverInput()->CreateScalarComponent( container, "/input/joystick/x",
        &input_handles_[ HmVrComponent_joystick_x ],
        vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedTwoSided );
    vr::VRDriverInput()->CreateScalarComponent( container, "/input/joystick/y",
        &input_handles_[ HmVrComponent_joystick_y ],
        vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedTwoSided );
    vr::VRDriverInput()->CreateBooleanComponent( container, "/input/joystick/click", &input_handles_[ HmVrComponent_joystick_click ] );

    vr::VRDriverInput()->CreateHapticComponent( container, "/output/haptic", &input_handles_[ HmVrComponent_haptic ] );

    pose_thread_ = std::thread( &HmVrControllerDevice::PoseUpdateThread, this );

    DriverLog( "hidmaestro: %s controller activated as device %u (hand selection priority %d, %s)",
        role_ == vr::TrackedControllerRole_LeftHand ? "left" : "right", unObjectId,
        handPriority, priorityFromSettings ? "from settings" : "built-in fallback" );
    return vr::VRInitError_None;
}

void HmVrControllerDevice::Deactivate()
{
    if ( is_active_.exchange( false ) )
    {
        pose_thread_.join();
    }
    device_index_ = vr::k_unTrackedDeviceIndexInvalid;
}

void HmVrControllerDevice::EnterStandby()
{
}

void *HmVrControllerDevice::GetComponent( const char *pchComponentNameAndVersion )
{
    return nullptr;
}

void HmVrControllerDevice::DebugRequest( const char *pchRequest, char *pchResponseBuffer, uint32_t unResponseBufferSize )
{
    if ( unResponseBufferSize >= 1 )
        pchResponseBuffer[ 0 ] = 0;
}

vr::DriverPose_t HmVrControllerDevice::GetPose()
{
    vr::DriverPose_t pose = { 0 };
    pose.qWorldFromDriverRotation.w = 1.f;
    pose.qDriverFromHeadRotation.w = 1.f;

    if ( pose_override_valid_.load( std::memory_order_acquire ) != 0 )
    {
        // Consumer-supplied pose (PadForge stick/gyro-driven hands).
        pose.qRotation.w = override_orientation_[ 0 ];
        pose.qRotation.x = override_orientation_[ 1 ];
        pose.qRotation.y = override_orientation_[ 2 ];
        pose.qRotation.z = override_orientation_[ 3 ];
        pose.vecPosition[ 0 ] = override_position_[ 0 ];
        pose.vecPosition[ 1 ] = override_position_[ 1 ];
        pose.vecPosition[ 2 ] = override_position_[ 2 ];
    }
    else
    {
        // Default: hands anchored ahead of the HMD, the simplecontroller
        // GetPose shape. With no HMD tracked yet the raw pose is identity,
        // which leaves the hands at a sane origin-relative spot instead of
        // invalidating them; important for the headless null-HMD test rig.
        vr::TrackedDevicePose_t hmd_pose{};
        vr::VRServerDriverHost()->GetRawTrackedDevicePoses( 0.f, &hmd_pose, 1 );

        const vr::HmdVector3_t hmd_position = HmdVector3_From34Matrix( hmd_pose.mDeviceToAbsoluteTracking );
        const vr::HmdQuaternion_t hmd_orientation = HmdQuaternion_FromMatrix( hmd_pose.mDeviceToAbsoluteTracking );

        const vr::HmdQuaternion_t offset_orientation = HmdQuaternion_FromEulerAngles( 0.f, DEG_TO_RAD( 90.f ), 0.f );
        pose.qRotation = hmd_orientation * offset_orientation;

        const vr::HmdVector3_t offset_position = {
            role_ == vr::TrackedControllerRole_LeftHand ? -0.15f : 0.15f,
            0.1f,
            -0.5f,
        };
        const vr::HmdVector3_t position = hmd_position + ( offset_position * hmd_orientation );
        pose.vecPosition[ 0 ] = position.v[ 0 ];
        pose.vecPosition[ 1 ] = position.v[ 1 ];
        pose.vecPosition[ 2 ] = position.v[ 2 ];
    }

    pose.poseIsValid = true;
    pose.deviceIsConnected = connected_.load( std::memory_order_relaxed );
    pose.result = pose.deviceIsConnected ? vr::TrackingResult_Running_OK
                                         : vr::TrackingResult_Uninitialized;
    return pose;
}

void HmVrControllerDevice::PoseUpdateThread()
{
    while ( is_active_ )
    {
        vr::VRServerDriverHost()->TrackedDevicePoseUpdated(
            device_index_, GetPose(), sizeof( vr::DriverPose_t ) );
        std::this_thread::sleep_for( std::chrono::milliseconds( 5 ) );
    }
}

void HmVrControllerDevice::UpdateFromState( const HMVR_ControllerState &state )
{
    if ( !is_active_ )
        return;

    auto &h = input_handles_;
    vr::IVRDriverInput *in = vr::VRDriverInput();

    in->UpdateBooleanComponent( h[ HmVrComponent_system_click ],   ( state.buttonBits & HMVR_BTN_SYSTEM ) != 0, 0 );
    in->UpdateBooleanComponent( h[ HmVrComponent_a_click ],        ( state.buttonBits & HMVR_BTN_A_CLICK ) != 0, 0 );
    in->UpdateBooleanComponent( h[ HmVrComponent_a_touch ],        ( state.buttonBits & HMVR_BTN_A_TOUCH ) != 0, 0 );
    in->UpdateBooleanComponent( h[ HmVrComponent_b_click ],        ( state.buttonBits & HMVR_BTN_B_CLICK ) != 0, 0 );
    in->UpdateBooleanComponent( h[ HmVrComponent_b_touch ],        ( state.buttonBits & HMVR_BTN_B_TOUCH ) != 0, 0 );
    in->UpdateBooleanComponent( h[ HmVrComponent_trigger_click ],  ( state.buttonBits & HMVR_BTN_TRIGGER_CLICK ) != 0, 0 );
    in->UpdateBooleanComponent( h[ HmVrComponent_grip_click ],     ( state.buttonBits & HMVR_BTN_GRIP_CLICK ) != 0, 0 );
    in->UpdateBooleanComponent( h[ HmVrComponent_joystick_click ], ( state.buttonBits & HMVR_BTN_STICK_CLICK ) != 0, 0 );

    in->UpdateScalarComponent( h[ HmVrComponent_trigger_value ], state.scalars[ HMVR_SCALAR_TRIGGER ], 0 );
    in->UpdateScalarComponent( h[ HmVrComponent_grip_value ],    state.scalars[ HMVR_SCALAR_GRIP ], 0 );
    in->UpdateScalarComponent( h[ HmVrComponent_joystick_x ],    state.scalars[ HMVR_SCALAR_STICK_X ], 0 );
    in->UpdateScalarComponent( h[ HmVrComponent_joystick_y ],    state.scalars[ HMVR_SCALAR_STICK_Y ], 0 );

    if ( state.poseValid )
    {
        override_orientation_[ 0 ] = state.orientation[ 0 ];
        override_orientation_[ 1 ] = state.orientation[ 1 ];
        override_orientation_[ 2 ] = state.orientation[ 2 ];
        override_orientation_[ 3 ] = state.orientation[ 3 ];
        override_position_[ 0 ] = state.position[ 0 ];
        override_position_[ 1 ] = state.position[ 1 ];
        override_position_[ 2 ] = state.position[ 2 ];
        pose_override_valid_.store( 1, std::memory_order_release );
    }
    else
    {
        pose_override_valid_.store( 0, std::memory_order_release );
    }
}

void HmVrControllerDevice::SetConnected( bool connected )
{
    connected_.store( connected, std::memory_order_relaxed );
    // The pose thread republishes within 5 ms and carries the new
    // deviceIsConnected, which is how SteamVR learns about the change.
}

void HmVrControllerDevice::ProcessEvent( const vr::VREvent_t &vrevent, HMVR_Section *section, void *hapticEvent )
{
    if ( vrevent.eventType != vr::VREvent_Input_HapticVibration )
        return;
    if ( vrevent.data.hapticVibration.componentHandle != input_handles_[ HmVrComponent_haptic ] )
        return;
    if ( section == nullptr )
        return;

    // Single producer ring, the v1.1.40 output-ring protocol with the
    // driver on the producing side: fill the slot, publish slot.seqNo,
    // then publish hapticHead, with barriers between the steps.
    const uint64_t seqNo = section->hapticHead + 1;
    HMVR_HapticReply &slot = section->haptic[ ( seqNo - 1 ) % HMVR_HAPTIC_SLOTS ];

    slot.hand = ( role_ == vr::TrackedControllerRole_LeftHand ) ? 0u : 1u;
    slot.durationSeconds = vrevent.data.hapticVibration.fDurationSeconds;
    slot.frequency = vrevent.data.hapticVibration.fFrequency;
    slot.amplitude = vrevent.data.hapticVibration.fAmplitude;

    LARGE_INTEGER qpc;
    QueryPerformanceCounter( &qpc );
    slot.timestampQpc = (uint64_t)qpc.QuadPart;

    MemoryBarrier();
    slot.seqNo = seqNo;
    MemoryBarrier();
    section->hapticHead = seqNo;
    if ( hapticEvent != nullptr )
        SetEvent( (HANDLE)hapticEvent );
}
