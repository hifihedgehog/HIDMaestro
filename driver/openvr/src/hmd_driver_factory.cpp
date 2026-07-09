/*
 * hmd_driver_factory.cpp: vrserver entry point.
 * Valve simplecontroller hmd_driver_factory.cpp shape verbatim, with the
 * provider renamed. vrserver loads bin/win64/driver_hidmaestro.dll and
 * calls HmdDriverFactory asking for IServerTrackedDeviceProvider.
 */
#include <cstring>

#include "hm_openvr.h"

#include "device_provider.h"

#if defined(_WIN32)
#define HMD_DLL_EXPORT extern "C" __declspec(dllexport)
#else
#error "HIDMaestro's OpenVR driver is Windows-only."
#endif

static HmVrDeviceProvider g_deviceProvider;

HMD_DLL_EXPORT void *HmdDriverFactory(const char *pInterfaceName, int *pReturnCode)
{
    if (0 == strcmp(vr::IServerTrackedDeviceProvider_Version, pInterfaceName))
        return &g_deviceProvider;

    if (pReturnCode)
        *pReturnCode = vr::VRInitError_Init_InterfaceNotFound;

    return nullptr;
}
