#include "device_provider.h"

#include "driver_log.h"

vr::EVRInitError HmVrDeviceProvider::Init(vr::IVRDriverContext *pDriverContext)
{
    /* Same context bring-up as simplecontroller device_provider.cpp:14,
     * but deliberately NO TrackedDeviceAdded here: devices appear only
     * after a consumer connects over IPC (VRCHOTAS deferred-add). */
    VR_INIT_SERVER_DRIVER_CONTEXT(pDriverContext);

    HmVrLog("[hidmaestro] OpenVR driver init");
    m_transport.Start();
    return vr::VRInitError_None;
}

const char *const *HmVrDeviceProvider::GetInterfaceVersions()
{
    return vr::k_InterfaceVersions;
}

bool HmVrDeviceProvider::ShouldBlockStandbyMode()
{
    return false;
}

void HmVrDeviceProvider::EnterStandby()
{
}

void HmVrDeviceProvider::LeaveStandby()
{
}

void HmVrDeviceProvider::EnsureDeviceRegistered(uint32_t hand,
                                                const HmVrConfigSnapshot &cfg)
{
    /* Idempotent late add, one hand at a time (VRCHOTAS
     * EnsureVirtualControllersRegistered). A failed TrackedDeviceAdded
     * is remembered so we don't hammer vrserver every frame; the next
     * SteamVR session retries from scratch.
     *
     * The profile is latched at first registration for the SteamVR
     * session: vrserver has no TrackedDeviceRemoved, so a consumer that
     * reconnects with a DIFFERENT profile on the same hand keeps the
     * original component set until SteamVR restarts. The SDK documents
     * this on HMVRController. */
    if (m_devices[hand] != nullptr || m_deviceAddFailed[hand])
        return;

    m_devices[hand] = std::make_unique<HmVrControllerDevice>(hand, cfg.Profile[hand]);
    bool added = vr::VRServerDriverHost()->TrackedDeviceAdded(
        m_devices[hand]->SerialNumber().c_str(),
        vr::TrackedDeviceClass_Controller,
        m_devices[hand].get());
    HmVrLog("[hidmaestro] TrackedDeviceAdded(%s) => %s",
            m_devices[hand]->SerialNumber().c_str(), added ? "true" : "false");
    if (!added)
    {
        m_devices[hand].reset();
        m_deviceAddFailed[hand] = true;
    }
}

void HmVrDeviceProvider::RunFrame()
{
    /* Never blocks: the transport worker owns all waits; RunFrame only
     * copies latest-wins snapshots (the spec's "reads a latest-snapshot
     * the worker publishes"). */
    HmVrConfigSnapshot cfg;
    if (m_transport.GetConfig(&cfg))
    {
        bool consumerAlive = cfg.ConsumerPid != 0 && m_transport.IsConsumerAlive();

        for (uint32_t hand = 0; hand < HMVR_HAND_COUNT; hand++)
        {
            bool wantConnected = consumerAlive
                && (cfg.ActiveHandsMask & (1u << hand)) != 0;

            if (wantConnected)
                EnsureDeviceRegistered(hand, cfg);

            if (m_devices[hand] != nullptr)
            {
                m_devices[hand]->SetConnected(wantConnected);
                /* Latch the priority only once it was actually applied.
                 * SetHandPriority no-ops before Activate; advancing the
                 * latch on a dropped call would silently discard the
                 * consumer's priority for the whole session (VRCHOTAS
                 * advances its latch after the guard passes,
                 * hotas_controller_device.cpp:137-148). */
                if (wantConnected && cfg.Priority[hand] != m_lastPriority[hand]
                    && m_devices[hand]->IsActivated())
                {
                    m_devices[hand]->SetHandPriority(cfg.Priority[hand]);
                    m_lastPriority[hand] = cfg.Priority[hand];
                }
            }
        }

        HmVrInputSnapshot input;
        if (consumerAlive && m_transport.GetInput(&input))
        {
            for (uint32_t hand = 0; hand < HMVR_HAND_COUNT; hand++)
            {
                if (m_devices[hand] != nullptr
                    && (cfg.ActiveHandsMask & (1u << hand)) != 0)
                {
                    m_devices[hand]->UpdateFromState(input.Ctrl[hand]);
                }
            }
        }
    }

    /* Event drain, simplecontroller device_provider.cpp:85-98 shape.
     * Haptic events resolve to a hand by component handle and go out
     * through the IPC ring. */
    vr::VREvent_t ev{};
    while (vr::VRServerDriverHost()->PollNextEvent(&ev, sizeof(ev)))
    {
        if (ev.eventType != vr::VREvent_Input_HapticVibration)
            continue;

        for (uint32_t hand = 0; hand < HMVR_HAND_COUNT; hand++)
        {
            float amplitude = 0, duration = 0, frequency = 0;
            if (m_devices[hand] != nullptr
                && m_devices[hand]->TryHandleHapticEvent(ev, &amplitude,
                                                         &duration, &frequency))
            {
                m_transport.PublishHaptic(hand, amplitude, duration, frequency);
                break;
            }
        }
    }
}

void HmVrDeviceProvider::Cleanup()
{
    HmVrLog("[hidmaestro] OpenVR driver cleanup");
    m_transport.Stop();

    /* Devices already deactivated by vrserver at this point; destroy
     * them last (simplecontroller device_provider.cpp:122-127). */
    for (uint32_t hand = 0; hand < HMVR_HAND_COUNT; hand++)
        m_devices[hand].reset();

    VR_CLEANUP_SERVER_DRIVER_CONTEXT();
}
