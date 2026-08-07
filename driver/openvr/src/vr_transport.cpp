// Driver-side IPC consumer for the HIDMaestroVR section (issue #32).
//
// Grounding:
//   * Two-phase worker (bootstrap poll gated by a 200 ms stop-event wait,
//     then a 500 ms WaitForMultipleObjects steady state that recycles on
//     any non-signal result): driver.c SharedInputWorkerProc. One
//     deliberate simplification vs driver.c: the stop event here is a
//     PRIVATE unnamed auto-reset event that only this driver's Cleanup
//     ever sets, so the shared-named-event pathologies driver.c defends
//     against (foreign sweeps signalling it, a sibling's ResetEvent
//     eating the teardown signal, issue #38) cannot occur, and the
//     signal alone is a trustworthy exit condition.
//   * 4-retry seqlock read with the odd/torn frame skipped: driver.c
//     ReadSharedInput.
//   * The 500 ms recycle timeout is kept from driver.c; the stale-seqno
//     recycle counter is dropped because this section has exactly one
//     consumer generation per handshake (consumerPid CAS) rather than
//     driver.c's shared-by-name events that can outlive their writer.

#include "vr_transport.h"

#include "driver_log.h"

HmVrTransport::~HmVrTransport()
{
    Stop();
}

void HmVrTransport::Start()
{
    if ( running_.exchange( true ) )
        return;
    stop_event_ = CreateEventW( nullptr, FALSE, FALSE, nullptr );
    worker_ = std::thread( &HmVrTransport::WorkerProc, this );
}

void HmVrTransport::Stop()
{
    if ( !running_.exchange( false ) )
        return;
    if ( stop_event_ )
        SetEvent( stop_event_ );
    if ( worker_.joinable() )
        worker_.join();
    CloseObjects();
    if ( stop_event_ )
    {
        CloseHandle( stop_event_ );
        stop_event_ = nullptr;
    }
}

bool HmVrTransport::TryOpenObjects()
{
    if ( section_ != nullptr && input_event_ != nullptr )
        return true;

    if ( input_event_ == nullptr )
    {
        input_event_ = OpenEventW( SYNCHRONIZE, FALSE, HMVR_INPUT_EVENT_NAME );
        if ( input_event_ == nullptr )
            return false;
    }

    if ( haptic_event_ == nullptr )
    {
        haptic_event_ = OpenEventW( EVENT_MODIFY_STATE, FALSE, HMVR_HAPTIC_EVENT_NAME );
        if ( haptic_event_ == nullptr )
            return false;
    }

    if ( mapping_ == nullptr )
    {
        // Write access is required: the driver produces the haptic ring
        // and the status block inside the same section.
        mapping_ = OpenFileMappingW( FILE_MAP_READ | FILE_MAP_WRITE, FALSE, HMVR_SECTION_NAME );
        if ( mapping_ == nullptr )
            return false;
    }

    if ( section_ == nullptr )
    {
        section_ = (HMVR_Section *)MapViewOfFile( mapping_, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, HMVR_SECTION_SIZE );
        if ( section_ == nullptr )
            return false;
        if ( section_->magic != HMVR_MAGIC || section_->abiVersion != HMVR_ABI_VERSION )
        {
            DriverLog( "hidmaestro: section magic/abi mismatch (%08x/%u), ignoring",
                section_->magic, section_->abiVersion );
            UnmapViewOfFile( section_ );
            section_ = nullptr;
            return false;
        }
        DriverLog( "hidmaestro: IPC section attached" );
    }
    return true;
}

void HmVrTransport::CloseObjects()
{
    if ( section_ )
    {
        UnmapViewOfFile( section_ );
        section_ = nullptr;
    }
    if ( mapping_ )
    {
        CloseHandle( mapping_ );
        mapping_ = nullptr;
    }
    if ( input_event_ )
    {
        CloseHandle( input_event_ );
        input_event_ = nullptr;
    }
    if ( haptic_event_ )
    {
        CloseHandle( haptic_event_ );
        haptic_event_ = nullptr;
    }
}

void HmVrTransport::WorkerProc()
{
    for ( ;; )
    {
        // Phase 1: bootstrap. Poll for the consumer-created objects,
        // staying responsive to Stop on every 200 ms tick.
        while ( running_ && !TryOpenObjects() )
        {
            if ( WaitForSingleObject( stop_event_, 200 ) == WAIT_OBJECT_0 )
                return;
            if ( !running_ )
                return;
        }
        if ( !running_ )
            return;

        // Phase 2: steady state.
        HANDLE waits[ 2 ] = { stop_event_, input_event_ };
        for ( ;; )
        {
            DWORD rc = WaitForMultipleObjects( 2, waits, FALSE, 500 );
            if ( !running_ || rc == WAIT_OBJECT_0 )
                return;

            if ( rc == WAIT_OBJECT_0 + 1 )
            {
                // Seqlock read, driver.c ReadSharedInput shape. A torn or
                // in-progress frame is skipped; the consumer's per-frame
                // SetEvent redelivers within a frame interval.
                volatile HMVR_Section *src = section_;
                HMVR_InputFrame frame;
                uint32_t seq1, seq2;
                int retries = 4;
                do
                {
                    seq1 = src->inputSeqNo;
                    MemoryBarrier();
                    memcpy( &frame, (const void *)&src->input, sizeof( frame ) );
                    MemoryBarrier();
                    seq2 = src->inputSeqNo;
                } while ( ( seq1 != seq2 || ( seq1 & 1 ) ) && --retries > 0 );

                if ( seq1 == seq2 && !( seq1 & 1 ) )
                {
                    // Publish under our private seqlock for RunFrame.
                    snap_seq_.fetch_add( 1, std::memory_order_acq_rel );  // odd
                    snap_ = frame;
                    snap_seq_.fetch_add( 1, std::memory_order_acq_rel );  // even
                    last_frame_qpc_.store( frame.timestampQpc, std::memory_order_relaxed );
                    have_snapshot_.store( true, std::memory_order_release );
                }
                continue;
            }

            // Timeout or failure: recycle handles through phase 1, the
            // driver.c recovery posture for every non-signal wake.
            if ( rc != WAIT_TIMEOUT )
            {
                CloseObjects();
                break;
            }
        }
    }
}

bool HmVrTransport::ConsumerActive()
{
    HMVR_Section *s = section_;
    if ( s == nullptr )
        return false;

    const uint64_t pid = s->consumerPid;
    if ( pid == 0 )
        return false;

    // Liveness: the frame heartbeat first (cheap), then the process
    // itself. VRCHOTAS gates on a heartbeat tick the same way; the
    // OpenProcess check catches a consumer that died between frames
    // without clearing consumerPid.
    LARGE_INTEGER now, freq;
    QueryPerformanceCounter( &now );
    QueryPerformanceFrequency( &freq );
    const uint64_t last = last_frame_qpc_.load( std::memory_order_relaxed );
    if ( last != 0 )
    {
        const double ageSec = (double)( (uint64_t)now.QuadPart - last ) / (double)freq.QuadPart;
        if ( ageSec < 5.0 )
            return true;
    }

    HANDLE h = OpenProcess( PROCESS_QUERY_LIMITED_INFORMATION, FALSE, (DWORD)pid );
    if ( h == nullptr )
        return false;
    DWORD code = 0;
    const bool alive = GetExitCodeProcess( h, &code ) && code == STILL_ACTIVE;
    CloseHandle( h );
    return alive;
}

bool HmVrTransport::TryGetSnapshot( HMVR_InputFrame &out )
{
    if ( !have_snapshot_.load( std::memory_order_acquire ) )
        return false;

    uint32_t s1, s2;
    int retries = 4;
    do
    {
        s1 = snap_seq_.load( std::memory_order_acquire );
        out = snap_;
        s2 = snap_seq_.load( std::memory_order_acquire );
    } while ( ( s1 != s2 || ( s1 & 1 ) ) && --retries > 0 );
    return s1 == s2 && !( s1 & 1 );
}

HMVR_Config HmVrTransport::GetConfig()
{
    HMVR_Config cfg{};
    if ( section_ != nullptr )
        cfg = section_->config;
    return cfg;
}
