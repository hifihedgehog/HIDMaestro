/*
 * controller_device.h: one ITrackedDeviceServerDriver per hand.
 *
 * Shape mirrors the Valve simplecontroller sample's
 * controller_device_driver.h/.cpp (Activate -> properties + component
 * handles, MyRunFrame -> component updates, MyProcessEvent -> haptic
 * routing) with the per-profile component table replacing the sample's
 * hardcoded set, and VRCHOTAS's connected-flip (SetDeviceConnected via
 * TrackedDevicePoseUpdated) replacing the sample's 5 ms pose thread.
 * v1 is NeverTracked: one valid identity pose per connection flip, no
 * pose thread at all.
 */
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "hm_openvr.h"
#include "vr_shared_layout.h"

enum class HmVrComponentKind
{
    Boolean,
    ScalarOneSided,
    ScalarTwoSided,
};

struct HmVrComponentSpec
{
    const char *Path;          /* e.g. "/input/trigger/value" */
    HmVrComponentKind Kind;
    uint32_t Index;            /* HMVR_BTN_* bit index for Boolean,
                                  HMVR_SCALAR_* slot for scalars */
};

struct HmVrProfileSpec
{
    const char *ModelNumberLeft;
    const char *ModelNumberRight;
    const char *ControllerType;      /* nullptr = leave property unset
                                        (KHR simple, grounded on the
                                        simplecontroller sample shipping
                                        no ControllerType at all) */
    const char *InputProfilePath;    /* "{hidmaestro}/input/..." */
    const char *RenderModelLeft;
    const char *RenderModelRight;
    const HmVrComponentSpec *Components;
    size_t ComponentCount;
};

const HmVrProfileSpec *HmVrGetProfileSpec(uint32_t profile);

class HmVrControllerDevice : public vr::ITrackedDeviceServerDriver
{
public:
    HmVrControllerDevice(uint32_t hand /* HMVR_HAND_* */, uint32_t profile);

    /* ITrackedDeviceServerDriver */
    vr::EVRInitError Activate(uint32_t unObjectId) override;
    void Deactivate() override;
    void EnterStandby() override;
    void *GetComponent(const char *pchComponentNameAndVersion) override;
    void DebugRequest(const char *pchRequest, char *pchResponseBuffer,
                      uint32_t unResponseBufferSize) override;
    vr::DriverPose_t GetPose() override;

    /* Driver-internal */
    const std::string &SerialNumber() const { return m_serial; }
    uint32_t Hand() const { return m_hand; }
    bool IsActivated() const { return m_objectId != vr::k_unTrackedDeviceIndexInvalid; }

    /* Push connected/disconnected. One TrackedDevicePoseUpdated per flip,
     * the VRCHOTAS SetDeviceConnected pattern; NeverTracked devices need
     * no further pose traffic. */
    void SetConnected(bool connected);

    /* Update hand-selection priority (consumer-configured). */
    void SetHandPriority(int32_t priority);

    /* Per-frame component refresh from the latest input snapshot. */
    void UpdateFromState(const HMVR_CONTROLLER_STATE &state);

    /* Returns true and fills the haptic fields if the event targets this
     * device's haptic component. */
    bool TryHandleHapticEvent(const vr::VREvent_t &ev, float *amplitude,
                              float *durationSeconds, float *frequency) const;

private:
    vr::DriverPose_t BuildPose(bool connected) const;

    uint32_t m_hand;
    uint32_t m_profile;
    std::string m_serial;
    uint32_t m_objectId = vr::k_unTrackedDeviceIndexInvalid;
    bool m_connected = false;

    std::vector<vr::VRInputComponentHandle_t> m_componentHandles;
    vr::VRInputComponentHandle_t m_hapticHandle = vr::k_ulInvalidInputComponentHandle;
};
