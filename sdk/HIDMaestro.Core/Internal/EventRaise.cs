using System;

namespace HIDMaestro.Internal;

/// <summary>Raises the public events that fire on the SDK's own threads:
/// the output reader, a USB/IP connection, the audio pump and the VR haptic
/// reader. Each handler runs in turn. An exception one throws is caught,
/// written to the diagnostic log under HIDMAESTRO_DIAG=1 and dropped. It
/// ends neither the thread nor the device's connection, and the other
/// handlers and the SDK's own handling of the same event still run. Walking
/// the handlers allocates nothing.</summary>
internal static class EventRaise
{
    public static void Each<T1, T2>(Action<T1, T2>? handler, T1 a, T2 b, string eventName)
    {
        foreach (var h in Delegate.EnumerateInvocationList(handler))
        {
            try { h(a, b); }
            catch (Exception ex) { Dropped(eventName, ex); }
        }
    }

    public static void Each<TArgs>(EventHandler<TArgs>? handler, object sender, TArgs args, string eventName)
    {
        foreach (var h in Delegate.EnumerateInvocationList(handler))
        {
            try { h(sender, args); }
            catch (Exception ex) { Dropped(eventName, ex); }
        }
    }

    private static void Dropped(string eventName, Exception ex)
    {
        if (DeviceOrchestrator.DiagEnabled)
            DeviceOrchestrator.LogDiag($"a {eventName} handler threw {ex.GetType().FullName}: {ex.Message}");
    }
}
