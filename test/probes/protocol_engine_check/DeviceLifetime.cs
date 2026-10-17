using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

/// <summary>What a USB/IP device that builds its reports holds when its
/// constructor fails after the device clock is made: nothing. The clock
/// owns a waitable timer and an event.</summary>
internal static class DeviceLifetime
{
    static void Check(string what, bool ok, string detail = "") => Checks.Check(what, ok, detail);

    public static void Run()
    {
        Console.WriteLine("-- a device whose constructor fails after its clock is made");
        // The composite with an axis center the report builder refuses. The
        // builder runs after the clock is made, and nothing machine-wide
        // exists before it.
        var o = JsonNode.Parse(Device.Catalog("switch2-pro-controller-composite"))!.AsObject();
        o["id"] = "lifetime-test";
        o["axisCenters"] = JsonNode.Parse("""{ "NotAnAxis": 1 }""");
        var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()))!;
        int Handles() { using var self = Process.GetCurrentProcess(); self.Refresh(); return self.HandleCount; }
        void Attempt()
        {
            try { _ = new UsbipEmulatedDevice(p, 7900); }
            catch (InvalidOperationException) { }
        }
        // One attempt first, so handles the runtime opens once are open.
        Attempt();
        GC.Collect(); GC.WaitForPendingFinalizers();
        int before = Handles();
        const int Attempts = 64;
        int refused = 0;
        for (int i = 0; i < Attempts; i++)
        {
            try { _ = new UsbipEmulatedDevice(p, 7900); }
            catch (InvalidOperationException) { refused++; }
        }
        GC.Collect(); GC.WaitForPendingFinalizers();
        int after = Handles();
        Check($"{Attempts} constructors refuse the profile", refused == Attempts, $"{refused} refused");
        // Two handles a failed constructor would leak, so fewer than one
        // per attempt is noise from the runtime, not the device.
        Check($"{Attempts} failed constructors leave no timer or event open", after - before < Attempts / 2, $"{before} handles before, {after} after");
    }
}
