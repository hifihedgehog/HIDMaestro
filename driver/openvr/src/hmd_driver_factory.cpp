// HIDMaestro OpenVR driver entry point (issue #32).
//
// The exact simplecontroller hmd_driver_factory.cpp shape with the
// provider renamed: vrserver loads this DLL from the vrpathreg-registered
// folder and asks for IServerTrackedDeviceProvider.

#include <cstring>

#include "device_provider.h"
#include "openvr_driver.h"

#if defined( _WIN32 )
#define HMD_DLL_EXPORT extern "C" __declspec( dllexport )
#else
#error "The HIDMaestro OpenVR driver is Windows-only."
#endif

static HmVrDeviceProvider g_deviceProvider;

HMD_DLL_EXPORT void *HmdDriverFactory( const char *pInterfaceName, int *pReturnCode )
{
    if ( 0 == strcmp( vr::IServerTrackedDeviceProvider_Version, pInterfaceName ) )
    {
        return &g_deviceProvider;
    }

    if ( pReturnCode )
        *pReturnCode = vr::VRInitError_Init_InterfaceNotFound;
    return nullptr;
}
