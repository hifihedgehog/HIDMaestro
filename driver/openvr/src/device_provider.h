// HIDMaestro OpenVR device provider (issue #32).
//
// IServerTrackedDeviceProvider whose Init opens the HIDMaestroVR IPC
// section and spawns the reader worker, but adds NO tracked devices until
// a consumer is live on the section. The deferred TrackedDeviceAdded from
// RunFrame is the VRCHOTAS pattern (hotas_server_driver
// EnsureVirtualControllersRegistered); the provider skeleton itself is
// the simplecontroller device_provider shape.

#pragma once

#include <memory>

#include "controller_device.h"
#include "openvr_driver.h"
#include "vr_transport.h"

class HmVrDeviceProvider : public vr::IServerTrackedDeviceProvider
{
public:
    vr::EVRInitError Init( vr::IVRDriverContext *pDriverContext ) override;
    const char *const *GetInterfaceVersions() override;
    void RunFrame() override;
    bool ShouldBlockStandbyMode() override;
    void EnterStandby() override;
    void LeaveStandby() override;
    void Cleanup() override;

private:
    void EnsureControllersRegistered();
    void PublishStatus();

    HmVrTransport transport_;
    std::unique_ptr<HmVrControllerDevice> left_;
    std::unique_ptr<HmVrControllerDevice> right_;
    bool controllers_registered_ = false;
    bool controllers_connected_ = false;
    bool logged_waiting_ = false;
};
