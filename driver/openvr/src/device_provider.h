/*
 * device_provider.h: IServerTrackedDeviceProvider for the HIDMaestro
 * OpenVR driver.
 *
 * Init opens the IPC transport and adds NO tracked devices (no phantom
 * controllers while no consumer runs). RunFrame performs the deferred,
 * idempotent TrackedDeviceAdded calls once a consumer's config appears,
 * flips connections when hands drop or the consumer dies, pushes input
 * snapshots into the devices, and routes haptic events back through the
 * IPC ring. This is the VRCHOTAS hotas_server_driver.cpp shape
 * (EnsureVirtualControllersRegistered at :47-67, driven from RunFrame at
 * :214, connected flips at :204-208) over the simplecontroller provider
 * skeleton.
 */
#pragma once

#include <memory>

#include "hm_openvr.h"

#include "controller_device.h"
#include "vr_transport.h"

class HmVrDeviceProvider : public vr::IServerTrackedDeviceProvider
{
public:
    vr::EVRInitError Init(vr::IVRDriverContext *pDriverContext) override;
    const char *const *GetInterfaceVersions() override;
    void RunFrame() override;
    bool ShouldBlockStandbyMode() override;
    void EnterStandby() override;
    void LeaveStandby() override;
    void Cleanup() override;

private:
    void EnsureDeviceRegistered(uint32_t hand, const HmVrConfigSnapshot &cfg);

    HmVrTransport m_transport;
    std::unique_ptr<HmVrControllerDevice> m_devices[HMVR_HAND_COUNT];
    bool m_deviceAddFailed[HMVR_HAND_COUNT] = { false, false };
    int32_t m_lastPriority[HMVR_HAND_COUNT] = { INT32_MIN, INT32_MIN };
};
