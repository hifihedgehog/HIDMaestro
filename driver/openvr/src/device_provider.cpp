// HIDMaestro OpenVR device provider (issue #32).
//
// Grounding:
//   * Provider skeleton (Init context macro, GetInterfaceVersions,
//     RunFrame device pump + PollNextEvent drain, Cleanup order):
//     openvr samples simplecontroller device_provider.cpp.
//   * Deferred TrackedDeviceAdded gated on a live consumer, with the
//     connected flip on consumer loss instead of removal (SteamVR has no
//     TrackedDeviceRemoved): VRCHOTAS hotas_server_driver
//     EnsureVirtualControllersRegistered / RunFrame (pattern reference
//     only; GPL, read-only, no code copied).

#include "device_provider.h"

#include <windows.h>

#include "driver_log.h"
#include "vrmath.h"

vr::EVRInitError HmVrDeviceProvider::Init( vr::IVRDriverContext *pDriverContext )
{
    VR_INIT_SERVER_DRIVER_CONTEXT( pDriverContext );
    DriverLog( "hidmaestro: provider init (abi %u)", HMVR_ABI_VERSION );

    // No TrackedDeviceAdded here: with no consumer app running, SteamVR
    // must show no phantom controllers. The transport worker watches for
    // the consumer's section; RunFrame does the deferred registration.
    transport_.Start();
    return vr::VRInitError_None;
}

const char *const *HmVrDeviceProvider::GetInterfaceVersions()
{
    return vr::k_InterfaceVersions;
}

void HmVrDeviceProvider::EnsureControllersRegistered()
{
    if ( controllers_registered_ )
        return;

    left_ = std::make_unique<HmVrControllerDevice>( vr::TrackedControllerRole_LeftHand );
    right_ = std::make_unique<HmVrControllerDevice>( vr::TrackedControllerRole_RightHand );

    const bool leftAdded = vr::VRServerDriverHost()->TrackedDeviceAdded(
        left_->SerialNumber().c_str(), vr::TrackedDeviceClass_Controller, left_.get() );
    const bool rightAdded = vr::VRServerDriverHost()->TrackedDeviceAdded(
        right_->SerialNumber().c_str(), vr::TrackedDeviceClass_Controller, right_.get() );
    DriverLog( "hidmaestro: TrackedDeviceAdded left=%d right=%d", leftAdded, rightAdded );

    controllers_registered_ = leftAdded && rightAdded;
    controllers_connected_ = controllers_registered_;
}

void HmVrDeviceProvider::PublishStatus()
{
    HMVR_Section *s = transport_.Section();
    if ( s == nullptr )
        return;

    HMVR_Status st{};
    st.driverActive = 1;
    st.controllersLive = 0;
    if ( controllers_registered_ && controllers_connected_ )
        st.controllersLive = HMVR_HAND_LEFT_ACTIVE | HMVR_HAND_RIGHT_ACTIVE;

    // Real HMD pose for the consumer (PadForge #49 lean-to-steer). Index 0
    // is the HMD by SteamVR convention, the same read the simplecontroller
    // pose path uses.
    vr::TrackedDevicePose_t hmd{};
    vr::VRServerDriverHost()->GetRawTrackedDevicePoses( 0.f, &hmd, 1 );
    if ( hmd.bPoseIsValid )
    {
        const vr::HmdVector3_t p = HmdVector3_From34Matrix( hmd.mDeviceToAbsoluteTracking );
        const vr::HmdQuaternion_t q = HmdQuaternion_FromMatrix( hmd.mDeviceToAbsoluteTracking );
        st.hmdPoseValid = 1;
        st.hmdPosition[ 0 ] = (float)p.v[ 0 ];
        st.hmdPosition[ 1 ] = (float)p.v[ 1 ];
        st.hmdPosition[ 2 ] = (float)p.v[ 2 ];
        st.hmdOrientation[ 0 ] = (float)q.w;
        st.hmdOrientation[ 1 ] = (float)q.x;
        st.hmdOrientation[ 2 ] = (float)q.y;
        st.hmdOrientation[ 3 ] = (float)q.z;
    }

    LARGE_INTEGER qpc;
    QueryPerformanceCounter( &qpc );
    st.driverHeartbeatQpc = (uint64_t)qpc.QuadPart;

    // Seqlock write into the section, WriteInputFrame's odd/even shape
    // with this driver as the single status writer: odd seqno, barrier,
    // payload, barrier, even seqno.
    volatile HMVR_Status *dst = &s->status;
    const uint32_t odd = ( dst->statusSeqNo + 1 ) | 1u;
    dst->statusSeqNo = odd;
    MemoryBarrier();
    dst->driverActive = st.driverActive;
    dst->controllersLive = st.controllersLive;
    dst->hmdPoseValid = st.hmdPoseValid;
    for ( int i = 0; i < 3; i++ ) dst->hmdPosition[ i ] = st.hmdPosition[ i ];
    for ( int i = 0; i < 4; i++ ) dst->hmdOrientation[ i ] = st.hmdOrientation[ i ];
    dst->driverHeartbeatQpc = st.driverHeartbeatQpc;
    MemoryBarrier();
    dst->statusSeqNo = odd + 1;
}

void HmVrDeviceProvider::RunFrame()
{
    const bool consumerActive = transport_.ConsumerActive();

    if ( consumerActive )
    {
        if ( !controllers_registered_ )
            EnsureControllersRegistered();
        else if ( !controllers_connected_ )
        {
            DriverLog( "hidmaestro: consumer returned, reconnecting controllers" );
            left_->SetConnected( true );
            right_->SetConnected( true );
            controllers_connected_ = true;
        }
        logged_waiting_ = false;

        HMVR_InputFrame frame;
        if ( controllers_registered_ && transport_.TryGetSnapshot( frame ) )
        {
            const HMVR_Config cfg = transport_.GetConfig();
            if ( cfg.handsActive & HMVR_HAND_LEFT_ACTIVE )
                left_->UpdateFromState( frame.hand[ 0 ] );
            if ( cfg.handsActive & HMVR_HAND_RIGHT_ACTIVE )
                right_->UpdateFromState( frame.hand[ 1 ] );
        }
    }
    else
    {
        if ( !logged_waiting_ )
        {
            DriverLog( "hidmaestro: waiting for a consumer before exposing controllers" );
            logged_waiting_ = true;
        }
        if ( controllers_registered_ && controllers_connected_ )
        {
            DriverLog( "hidmaestro: consumer lost, marking controllers disconnected" );
            left_->SetConnected( false );
            right_->SetConnected( false );
            controllers_connected_ = false;
        }
    }

    PublishStatus();

    vr::VREvent_t vrevent{};
    while ( vr::VRServerDriverHost()->PollNextEvent( &vrevent, sizeof( vr::VREvent_t ) ) )
    {
        HMVR_Section *section = transport_.Section();
        void *hapticEvent = transport_.HapticEvent();
        if ( left_ )
            left_->ProcessEvent( vrevent, section, hapticEvent );
        if ( right_ )
            right_->ProcessEvent( vrevent, section, hapticEvent );
    }
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

void HmVrDeviceProvider::Cleanup()
{
    transport_.Stop();
    left_ = nullptr;
    right_ = nullptr;
    controllers_registered_ = false;
    controllers_connected_ = false;
}
