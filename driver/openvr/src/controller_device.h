// HIDMaestro virtual VR controller device (issue #32).
//
// One instance per hand, added to vrserver via TrackedDeviceAdded once a
// consumer is live on the IPC section. Shape mirrors Valve's
// simplecontroller sample (controller_device_driver.h) with the input
// component table widened to the generic hidmaestro_controller profile
// and the per-frame data sourced from the IPC snapshot instead of
// hardcoded values.

#pragma once

#include <array>
#include <atomic>
#include <string>
#include <thread>

#include "openvr_driver.h"
#include "vr_protocol.h"

// Driver settings section, seeded by resources/settings/default.vrsettings
// and overridable per machine in steamvr.vrsettings. Section naming and
// the read path follow opengloves device_configuration.cpp.
#define HMVR_SETTINGS_SECTION           "driver_hidmaestro"
#define HMVR_SETTINGS_KEY_HAND_PRIORITY "hand_selection_priority"
#define HMVR_DEFAULT_HAND_PRIORITY      1000

enum HmVrComponent
{
    HmVrComponent_system_click,
    HmVrComponent_a_click,
    HmVrComponent_a_touch,
    HmVrComponent_b_click,
    HmVrComponent_b_touch,
    HmVrComponent_trigger_value,
    HmVrComponent_trigger_click,
    HmVrComponent_grip_value,
    HmVrComponent_grip_click,
    HmVrComponent_joystick_x,
    HmVrComponent_joystick_y,
    HmVrComponent_joystick_click,
    HmVrComponent_haptic,
    HmVrComponent_MAX
};

class HmVrControllerDevice : public vr::ITrackedDeviceServerDriver
{
public:
    explicit HmVrControllerDevice( vr::ETrackedControllerRole role );

    // vr::ITrackedDeviceServerDriver
    vr::EVRInitError Activate( uint32_t unObjectId ) override;
    void Deactivate() override;
    void EnterStandby() override;
    void *GetComponent( const char *pchComponentNameAndVersion ) override;
    void DebugRequest( const char *pchRequest, char *pchResponseBuffer, uint32_t unResponseBufferSize ) override;
    vr::DriverPose_t GetPose() override;

    // Driven by the provider
    const std::string &SerialNumber() const { return serial_number_; }
    void UpdateFromState( const HMVR_ControllerState &state );
    void SetConnected( bool connected );
    void ProcessEvent( const vr::VREvent_t &vrevent, HMVR_Section *section, void *hapticEvent );

private:
    void PoseUpdateThread();
    static int32_t HandSelectionPriority( bool *fromSettings );

    vr::ETrackedControllerRole role_;
    std::string serial_number_;
    std::string model_number_;

    std::atomic<vr::TrackedDeviceIndex_t> device_index_{ vr::k_unTrackedDeviceIndexInvalid };
    std::atomic<bool> is_active_{ false };
    std::atomic<bool> connected_{ true };

    // Latest consumer pose override, published by UpdateFromState and
    // consumed by the pose thread. Guarded by pose_valid_'s release/acquire
    // ordering: writer stores the payload, then flags; reader checks the
    // flag, then loads. A torn read here costs one 5 ms frame of pose lag,
    // which is below SteamVR's own prediction noise.
    std::atomic<uint32_t> pose_override_valid_{ 0 };
    float override_position_[3] = { 0, 0, 0 };
    float override_orientation_[4] = { 1, 0, 0, 0 };

    std::array<vr::VRInputComponentHandle_t, HmVrComponent_MAX> input_handles_{};
    std::thread pose_thread_;
};
