#include "controller_device.h"

#include "driver_log.h"

namespace
{
    /* ── profile component tables ─────────────────────────────────────
     * Boolean Index = bit position in HMVR_CONTROLLER_STATE.ButtonBits.
     * Scalar Index = slot in Scalars[]. The universal assignments live
     * in vr_shared_layout.h; each table lists only what its controller
     * actually exposes, mirroring how opengloves'
     * knuckle_device_driver.cpp:130-153 creates exactly the Index set
     * and simplecontroller creates exactly its own. */

    constexpr HmVrComponentSpec kKhrSimpleComponents[] =
    {
        { "/input/system/click",           HmVrComponentKind::Boolean,        0  },
        { "/input/application_menu/click", HmVrComponentKind::Boolean,        14 },
        { "/input/trigger/click",          HmVrComponentKind::Boolean,        6  },
        { "/input/trigger/value",          HmVrComponentKind::ScalarOneSided, HMVR_SCALAR_TRIGGER_VALUE },
        { "/input/grip/click",             HmVrComponentKind::Boolean,        8  },
    };

    /* Valve Index set: opengloves knuckle_device_driver.cpp:130-153
     * verbatim minus the skeleton component (v1 ships no skeletal
     * data; skeleton is the one CreateSkeletonComponent in that list). */
    constexpr HmVrComponentSpec kValveIndexComponents[] =
    {
        { "/input/system/click",     HmVrComponentKind::Boolean,        0  },
        { "/input/system/touch",     HmVrComponentKind::Boolean,        1  },
        { "/input/a/click",          HmVrComponentKind::Boolean,        2  },
        { "/input/a/touch",          HmVrComponentKind::Boolean,        3  },
        { "/input/b/click",          HmVrComponentKind::Boolean,        4  },
        { "/input/b/touch",          HmVrComponentKind::Boolean,        5  },
        { "/input/trigger/click",    HmVrComponentKind::Boolean,        6  },
        { "/input/trigger/value",    HmVrComponentKind::ScalarOneSided, HMVR_SCALAR_TRIGGER_VALUE },
        { "/input/grip/touch",       HmVrComponentKind::Boolean,        9  },
        { "/input/grip/value",       HmVrComponentKind::ScalarOneSided, HMVR_SCALAR_GRIP_VALUE },
        { "/input/grip/force",       HmVrComponentKind::ScalarOneSided, HMVR_SCALAR_GRIP_FORCE },
        { "/input/thumbstick/click", HmVrComponentKind::Boolean,        10 },
        { "/input/thumbstick/touch", HmVrComponentKind::Boolean,        11 },
        { "/input/thumbstick/x",     HmVrComponentKind::ScalarTwoSided, HMVR_SCALAR_THUMBSTICK_X },
        { "/input/thumbstick/y",     HmVrComponentKind::ScalarTwoSided, HMVR_SCALAR_THUMBSTICK_Y },
        { "/input/trackpad/x",       HmVrComponentKind::ScalarTwoSided, HMVR_SCALAR_TRACKPAD_X },
        { "/input/trackpad/y",       HmVrComponentKind::ScalarTwoSided, HMVR_SCALAR_TRACKPAD_Y },
        { "/input/trackpad/touch",   HmVrComponentKind::Boolean,        13 },
        { "/input/trackpad/force",   HmVrComponentKind::ScalarOneSided, HMVR_SCALAR_TRACKPAD_FORCE },
        { "/input/finger/index",     HmVrComponentKind::ScalarOneSided, HMVR_SCALAR_FINGER_INDEX },
        { "/input/finger/middle",    HmVrComponentKind::ScalarOneSided, HMVR_SCALAR_FINGER_MIDDLE },
        { "/input/finger/ring",      HmVrComponentKind::ScalarOneSided, HMVR_SCALAR_FINGER_RING },
        { "/input/finger/pinky",     HmVrComponentKind::ScalarOneSided, HMVR_SCALAR_FINGER_PINKY },
    };

    /* Windows Mixed Reality motion controller set: menu + grip buttons,
     * trigger, clickable trackpad AND thumbstick, per Valve's own
     * bindings_holographic_controller.json input sources. */
    constexpr HmVrComponentSpec kMicrosoftMotionComponents[] =
    {
        { "/input/system/click",           HmVrComponentKind::Boolean,        0  },
        { "/input/application_menu/click", HmVrComponentKind::Boolean,        14 },
        { "/input/grip/click",             HmVrComponentKind::Boolean,        8  },
        { "/input/trigger/click",          HmVrComponentKind::Boolean,        6  },
        { "/input/trigger/value",          HmVrComponentKind::ScalarOneSided, HMVR_SCALAR_TRIGGER_VALUE },
        { "/input/trackpad/x",             HmVrComponentKind::ScalarTwoSided, HMVR_SCALAR_TRACKPAD_X },
        { "/input/trackpad/y",             HmVrComponentKind::ScalarTwoSided, HMVR_SCALAR_TRACKPAD_Y },
        { "/input/trackpad/click",         HmVrComponentKind::Boolean,        12 },
        { "/input/trackpad/touch",         HmVrComponentKind::Boolean,        13 },
        { "/input/thumbstick/click",       HmVrComponentKind::Boolean,        10 },
        { "/input/thumbstick/x",           HmVrComponentKind::ScalarTwoSided, HMVR_SCALAR_THUMBSTICK_X },
        { "/input/thumbstick/y",           HmVrComponentKind::ScalarTwoSided, HMVR_SCALAR_THUMBSTICK_Y },
    };

    constexpr HmVrProfileSpec kProfiles[HMVR_PROFILE_COUNT] =
    {
        /* HMVR_PROFILE_KHR_SIMPLE. ControllerType left unset, matching
         * the simplecontroller sample which ships none at all. */
        {
            "HIDMaestro VR Simple Controller",
            "HIDMaestro VR Simple Controller",
            nullptr,
            "{hidmaestro}/input/khr_simple_profile.json",
            "generic_controller", "generic_controller",
            kKhrSimpleComponents, _countof(kKhrSimpleComponents),
        },
        /* HMVR_PROFILE_VALVE_INDEX. "knuckles" per opengloves
         * knuckle_device_driver.cpp:110. */
        {
            "Knuckles Left",
            "Knuckles Right",
            "knuckles",
            "{hidmaestro}/input/valve_index_profile.json",
            "{indexcontroller}valve_controller_knu_1_0_left",
            "{indexcontroller}valve_controller_knu_1_0_right",
            kValveIndexComponents, _countof(kValveIndexComponents),
        },
        /* HMVR_PROFILE_MICROSOFT_MOTION. "holographic_controller" per
         * Valve's default WMR bindings file name/type. */
        {
            "Windows Mixed Reality Motion Controller",
            "Windows Mixed Reality Motion Controller",
            "holographic_controller",
            "{hidmaestro}/input/microsoft_motion_profile.json",
            "generic_controller", "generic_controller",
            kMicrosoftMotionComponents, _countof(kMicrosoftMotionComponents),
        },
    };
}

const HmVrProfileSpec *HmVrGetProfileSpec(uint32_t profile)
{
    if (profile >= HMVR_PROFILE_COUNT)
        return nullptr;
    return &kProfiles[profile];
}

HmVrControllerDevice::HmVrControllerDevice(uint32_t hand, uint32_t profile)
    : m_hand(hand), m_profile(profile)
{
    m_serial = (hand == HMVR_HAND_LEFT) ? "HIDMAESTRO_VR_LEFT"
                                        : "HIDMAESTRO_VR_RIGHT";
}

vr::EVRInitError HmVrControllerDevice::Activate(uint32_t unObjectId)
{
    const HmVrProfileSpec *spec = HmVrGetProfileSpec(m_profile);
    if (spec == nullptr)
        return vr::VRInitError_Driver_Failed;

    m_objectId = unObjectId;

    vr::PropertyContainerHandle_t container =
        vr::VRProperties()->TrackedDeviceToPropertyContainer(m_objectId);

    const bool isLeft = (m_hand == HMVR_HAND_LEFT);

    /* Identity properties. Set order mirrors simplecontroller's Activate
     * (model, role, input profile) plus the opengloves identity block
     * (manufacturer, render model, controller type, hand priority). */
    vr::VRProperties()->SetStringProperty(container, vr::Prop_ModelNumber_String,
                                          isLeft ? spec->ModelNumberLeft
                                                 : spec->ModelNumberRight);
    vr::VRProperties()->SetStringProperty(container, vr::Prop_ManufacturerName_String,
                                          "HIDMaestro");
    vr::VRProperties()->SetStringProperty(container, vr::Prop_SerialNumber_String,
                                          m_serial.c_str());
    vr::VRProperties()->SetStringProperty(container, vr::Prop_RenderModelName_String,
                                          isLeft ? spec->RenderModelLeft
                                                 : spec->RenderModelRight);
    vr::VRProperties()->SetStringProperty(container, vr::Prop_RegisteredDeviceType_String,
                                          isLeft ? "hidmaestro/vr_left"
                                                 : "hidmaestro/vr_right");
    vr::VRProperties()->SetInt32Property(container, vr::Prop_ControllerRoleHint_Int32,
                                         isLeft ? vr::TrackedControllerRole_LeftHand
                                                : vr::TrackedControllerRole_RightHand);
    vr::VRProperties()->SetStringProperty(container, vr::Prop_InputProfilePath_String,
                                          spec->InputProfilePath);
    if (spec->ControllerType != nullptr)
        vr::VRProperties()->SetStringProperty(container, vr::Prop_ControllerType_String,
                                              spec->ControllerType);

    /* v1 has no tracking: NeverTracked suppresses the pose pipeline; the
     * single valid pose below keeps the device out of the "not ready"
     * icon state. */
    vr::VRProperties()->SetBoolProperty(container, vr::Prop_NeverTracked_Bool, true);
    vr::VRProperties()->SetBoolProperty(container, vr::Prop_Identifiable_Bool, true);

    /* Input components, from the profile table. Same three Create calls
     * simplecontroller uses (:90-105), driven by data. */
    m_componentHandles.assign(spec->ComponentCount,
                              vr::k_ulInvalidInputComponentHandle);
    for (size_t i = 0; i < spec->ComponentCount; i++)
    {
        const HmVrComponentSpec &c = spec->Components[i];
        switch (c.Kind)
        {
        case HmVrComponentKind::Boolean:
            vr::VRDriverInput()->CreateBooleanComponent(
                container, c.Path, &m_componentHandles[i]);
            break;
        case HmVrComponentKind::ScalarOneSided:
            vr::VRDriverInput()->CreateScalarComponent(
                container, c.Path, &m_componentHandles[i],
                vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedOneSided);
            break;
        case HmVrComponentKind::ScalarTwoSided:
            vr::VRDriverInput()->CreateScalarComponent(
                container, c.Path, &m_componentHandles[i],
                vr::VRScalarType_Absolute, vr::VRScalarUnits_NormalizedTwoSided);
            break;
        }
    }

    vr::VRDriverInput()->CreateHapticComponent(container, "/output/haptic",
                                               &m_hapticHandle);

    /* Announce connected with one valid pose. */
    m_connected = true;
    vr::VRServerDriverHost()->TrackedDevicePoseUpdated(
        m_objectId, BuildPose(true), sizeof(vr::DriverPose_t));

    HmVrLog("[hidmaestro] %s activated (profile=%u, objectId=%u)",
            m_serial.c_str(), m_profile, m_objectId);
    return vr::VRInitError_None;
}

void HmVrControllerDevice::Deactivate()
{
    /* Mirror simplecontroller: drop the object id so no further vrserver
     * calls are made against a deactivated device. */
    m_objectId = vr::k_unTrackedDeviceIndexInvalid;
}

void HmVrControllerDevice::EnterStandby()
{
}

void *HmVrControllerDevice::GetComponent(const char * /*pchComponentNameAndVersion*/)
{
    return nullptr;
}

void HmVrControllerDevice::DebugRequest(const char * /*pchRequest*/,
                                        char *pchResponseBuffer,
                                        uint32_t unResponseBufferSize)
{
    if (unResponseBufferSize >= 1)
        pchResponseBuffer[0] = 0;
}

vr::DriverPose_t HmVrControllerDevice::GetPose()
{
    return BuildPose(m_connected);
}

vr::DriverPose_t HmVrControllerDevice::BuildPose(bool connected) const
{
    /* Identity pose, valid quaternions required or the device never
     * appears (simplecontroller controller_device_driver.cpp:144-148).
     * NeverTracked means this is pushed only on connection flips. */
    vr::DriverPose_t pose = { 0 };
    pose.qWorldFromDriverRotation.w = 1.0;
    pose.qDriverFromHeadRotation.w = 1.0;
    pose.qRotation.w = 1.0;
    pose.poseIsValid = connected;
    pose.deviceIsConnected = connected;
    pose.result = connected ? vr::TrackingResult_Running_OK
                            : vr::TrackingResult_Uninitialized;
    return pose;
}

void HmVrControllerDevice::SetConnected(bool connected)
{
    if (!IsActivated() || m_connected == connected)
        return;
    m_connected = connected;
    vr::VRServerDriverHost()->TrackedDevicePoseUpdated(
        m_objectId, BuildPose(connected), sizeof(vr::DriverPose_t));
    HmVrLog("[hidmaestro] %s -> %s", m_serial.c_str(),
            connected ? "connected" : "disconnected");
}

void HmVrControllerDevice::SetHandPriority(int32_t priority)
{
    if (!IsActivated())
        return;
    vr::PropertyContainerHandle_t container =
        vr::VRProperties()->TrackedDeviceToPropertyContainer(m_objectId);
    vr::VRProperties()->SetInt32Property(
        container, vr::Prop_ControllerHandSelectionPriority_Int32, priority);
}

void HmVrControllerDevice::UpdateFromState(const HMVR_CONTROLLER_STATE &state)
{
    if (!IsActivated() || !m_connected)
        return;

    const HmVrProfileSpec *spec = HmVrGetProfileSpec(m_profile);
    if (spec == nullptr)
        return;

    for (size_t i = 0; i < spec->ComponentCount; i++)
    {
        const HmVrComponentSpec &c = spec->Components[i];
        vr::VRInputComponentHandle_t h = m_componentHandles[i];
        if (h == vr::k_ulInvalidInputComponentHandle)
            continue;

        if (c.Kind == HmVrComponentKind::Boolean)
        {
            bool value = (state.ButtonBits & (1u << c.Index)) != 0;
            vr::VRDriverInput()->UpdateBooleanComponent(h, value, 0.0);
        }
        else
        {
            float v = state.Scalars[c.Index];
            if (c.Kind == HmVrComponentKind::ScalarOneSided)
                v = (v < 0.0f) ? 0.0f : (v > 1.0f ? 1.0f : v);
            else
                v = (v < -1.0f) ? -1.0f : (v > 1.0f ? 1.0f : v);
            vr::VRDriverInput()->UpdateScalarComponent(h, v, 0.0);
        }
    }
}

bool HmVrControllerDevice::TryHandleHapticEvent(const vr::VREvent_t &ev,
                                                float *amplitude,
                                                float *durationSeconds,
                                                float *frequency) const
{
    /* Handle-match discipline from simplecontroller's MyProcessEvent
     * (:266-294): the event carries the component handle; only the
     * owning device consumes it. */
    if (ev.eventType != vr::VREvent_Input_HapticVibration)
        return false;
    if (m_hapticHandle == vr::k_ulInvalidInputComponentHandle
        || ev.data.hapticVibration.componentHandle != m_hapticHandle)
        return false;

    *amplitude = ev.data.hapticVibration.fAmplitude;
    *durationSeconds = ev.data.hapticVibration.fDurationSeconds;
    *frequency = ev.data.hapticVibration.fFrequency;
    return true;
}
