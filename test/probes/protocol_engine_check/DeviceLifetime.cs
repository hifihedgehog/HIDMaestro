using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

/// <summary>What a USB/IP device that builds its reports holds when its
/// constructor fails after the device clock is made: nothing. The clock
/// owns a waitable timer and an event. The constructor fails through
/// <c>UsbipEmulatedDevice.AfterInputPumpStart</c>, the hook that runs right
/// after the input pump starts, past the clock, the sections, the events
/// and the audio engine, so the constructor's catch releases all of them.
/// The device runs offline on process memory, its sections handed in
/// through SharedMemoryIO's view caches as device_oracle_check's cases do.</summary>
internal static class DeviceLifetime
{
    static void Check(string what, bool ok, string detail = "") => Checks.Check(what, ok, detail);
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    const int Index = 7900;

    sealed class Planted : Exception { public Planted() : base("planted failure after the input pump starts") { } }

    public static void Run()
    {
        Console.WriteLine("-- a device whose constructor fails after its clock is made");
        var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(Device.Catalog("switch2-pro-controller-composite")))!;
        IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
        IntPtr ring = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_OUTPUT_SIZE);
        Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
        Marshal.Copy(new byte[SharedMemoryIO.SHARED_OUTPUT_SIZE], 0, ring, SharedMemoryIO.SHARED_OUTPUT_SIZE);
        Cache("s_inputViews")[Index] = view;
        Cache("s_outputViews")[Index] = ring;
        // The output event too: a named Global event needs the SDK's
        // elevation, and the check runs without it.
        using var outputEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset);
        Cache("s_outputEvents")[Index] = outputEvent.SafeWaitHandle.DangerousGetHandle();
        var hook = typeof(UsbipEmulatedDevice).GetField("AfterInputPumpStart", Static)
            ?? throw new MissingFieldException(nameof(UsbipEmulatedDevice), "AfterInputPumpStart");
        try
        {
            hook.SetValue(null, (Action<int>)(i => { if (i == Index) throw new Planted(); }));
            int Handles() { using var self = Process.GetCurrentProcess(); self.Refresh(); return self.HandleCount; }
            int Threads() { using var self = Process.GetCurrentProcess(); self.Refresh(); return self.Threads.Count; }
            bool Attempt()
            {
                try { _ = new UsbipEmulatedDevice(p, Index); return false; }
                catch (Planted) { return true; }
            }
            // One attempt first, so handles the runtime and the output event
            // cache open once are open.
            Attempt();
            GC.Collect(); GC.WaitForPendingFinalizers();
            int before = Handles(), threadsBefore = Threads();
            const int Attempts = 64;
            int refused = 0;
            for (int i = 0; i < Attempts; i++) if (Attempt()) refused++;
            GC.Collect(); GC.WaitForPendingFinalizers();
            int after = Handles(), threadsAfter = Threads();
            Check($"{Attempts} constructors fail after the input pump starts", refused == Attempts, $"{refused} failed there");
            // Two handles a failed constructor would leak with its clock, so
            // fewer than one per attempt is noise from the runtime, not the
            // device.
            Check($"{Attempts} failed constructors leave no timer or event open", after - before < Attempts / 2, $"{before} handles before, {after} after");
            Check($"{Attempts} failed constructors leave no clock or pump thread running", threadsAfter - threadsBefore < Attempts / 2, $"{threadsBefore} threads before, {threadsAfter} after");
        }
        finally
        {
            hook.SetValue(null, null);
            Cache("s_inputViews").Remove(Index);
            Cache("s_outputViews").Remove(Index);
            Cache("s_outputEvents").Remove(Index);
            Marshal.FreeHGlobal(view);
            Marshal.FreeHGlobal(ring);
        }
    }

    static Dictionary<int, IntPtr> Cache(string field) =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, Static)!.GetValue(null)!;
}
