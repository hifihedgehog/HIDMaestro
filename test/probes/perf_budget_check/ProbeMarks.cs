// --marks: one ETW event per timed frame of the e2e and output passes, for
// a trace that splits a frame's latency into the threads it wakes. The
// event carries the frame's own Stopwatch times (QueryPerformanceCounter
// ticks, the clock of an xperf trace at its default -clocktype
// PerfCounter) and the operating system thread IDs that started and ended
// it, and is written after the frame's end time is taken, so it adds
// nothing to the time measured. --marks registers the provider when Main
// parses it. Without --marks the provider is never registered. With
// --marks and no session listening, a frame costs one IsEnabled check
// after it ends.
//
// Provider HIDMaestro-PerfProbe, a4016e05-b2d3-4fdb-8315-2b510bccd188,
// event 1 Frame:
//   Kind         1 driver acknowledgement (AckPass), 2 a host's ReadFile or
//                Raw Input read (HostReadPass), 3 XInputGetState
//                (XInputPass), 4 an output report to OutputReceived
//                (OutputOne), 5 XInputSetState to OutputReceived
//                (XInputOutputOne)
//   Index        the frame's number in its pass
//   Start, End   the frame's Stopwatch times: the submit or send, and the
//                read, acknowledgement or callback that ended it
//   StartThread  the thread that submitted or sent
//   EndThread    the thread that saw it end: the reader, the submitter that
//                polled, or the SDK's thread that raised OutputReceived
using System;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

[EventSource(Name = "HIDMaestro-PerfProbe", Guid = "a4016e05-b2d3-4fdb-8315-2b510bccd188")]
internal sealed class ProbeMarks : EventSource
{
    /// <summary>Set by --marks before any pass starts.</summary>
    internal static bool On;

    static ProbeMarks? s_log;

    /// <summary>The provider, registered by <see cref="Register"/> when
    /// --marks is parsed.</summary>
    static ProbeMarks Log => s_log ??= new ProbeMarks();

    /// <summary>Registers the provider. Main calls it when it parses
    /// --marks, before any pass starts.</summary>
    internal static void Register() => _ = Log;

    ProbeMarks() : base(EventSourceSettings.EtwManifestEventFormat) { }

    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();

    /// <summary>One frame, after its end time is taken.</summary>
    internal static void Frame(int kind, int index, long start, long end, uint startThread, uint endThread)
    {
        if (!On) return;
        var log = Log;
        if (log.IsEnabled()) log.WriteFrame(kind, index, start, end, (int)startThread, (int)endThread);
    }

    [Event(1, Level = EventLevel.Informational)]
    unsafe void WriteFrame(int Kind, int Index, long Start, long End, int StartThread, int EndThread)
    {
        EventData* d = stackalloc EventData[6];
        d[0] = new EventData { DataPointer = (IntPtr)(&Kind), Size = 4 };
        d[1] = new EventData { DataPointer = (IntPtr)(&Index), Size = 4 };
        d[2] = new EventData { DataPointer = (IntPtr)(&Start), Size = 8 };
        d[3] = new EventData { DataPointer = (IntPtr)(&End), Size = 8 };
        d[4] = new EventData { DataPointer = (IntPtr)(&StartThread), Size = 4 };
        d[5] = new EventData { DataPointer = (IntPtr)(&EndThread), Size = 4 };
        WriteEventCore(1, 6, d);
    }
}
