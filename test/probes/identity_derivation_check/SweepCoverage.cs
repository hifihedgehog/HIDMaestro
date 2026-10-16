// Every enumerator a presentation plan creates a parent under is one the
// SDK's cleanup and rival scan cover: the 0xCA rival scan
// (PresentationPlan.IsRootParentEnumerator for ROOT), the Device Parameters
// phase of RemoveEverything (DeviceOrchestrator.SweepsDeviceParameters) and
// its HID orphan phase (DeviceOrchestrator.SweepsOrphanChildren), which
// reads a child's enumerator segment, the parent's own (ROOT\HIDCLASS\...
// has the child HID\HIDCLASS\...). Checked over the plan of every catalog
// profile, so a profile whose parent moves (the nine Microsoft profiles to
// HIDClass) stays inside every sweep. No device.

using System;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class SweepCoverage
{
    internal static void Run(Action<string, bool, string> check)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var enumerators = ctx.AllProfiles
            .Select(p => PresentationPlan.Of(p.Inner))
            .Where(plan => plan.Enumerator != null)
            .Select(plan => (plan.Path, Enumerator: plan.Enumerator!))
            .Distinct()
            .OrderBy(e => e.Enumerator, StringComparer.Ordinal)
            .ToList();
        check($"sweeps: the catalog's plans name {enumerators.Count} parent enumerators", enumerators.Count > 0, "");
        foreach (var (path, e) in enumerators)
        {
            bool root = path == PresentationPlan.CreatePath.Root;
            check($"sweeps: {e} is a parent enumerator of its kind",
                  root ? PresentationPlan.IsRootParentEnumerator(e) : PresentationPlan.IsSwdParentEnumerator(e), path.ToString());
            check($"sweeps: the Device Parameters phase walks {e}",
                  DeviceOrchestrator.SweepsDeviceParameters(e, out _), "");
            check($"sweeps: the HID orphan phase walks HID\\{e.ToUpperInvariant()}",
                  DeviceOrchestrator.SweepsOrphanChildren(e.ToUpperInvariant()), "");
        }
        check("sweeps: HIDClass's Device Parameters need the HardwareID proof",
              DeviceOrchestrator.SweepsDeviceParameters("HIDCLASS", out bool exclusive) && !exclusive, "");
        check("sweeps: an enumerator no plan makes and no build made is not walked",
              !DeviceOrchestrator.SweepsDeviceParameters("USB", out _) && !DeviceOrchestrator.SweepsOrphanChildren("VID_046D&PID_C52B&MI_00"), "");
    }
}
