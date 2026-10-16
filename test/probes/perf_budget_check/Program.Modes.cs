// The driver a measurement session runs on: InstallDriver, then proof that
// what is installed is this SDK's own driver, so the baselines of the
// v2.0.0 conversion are 9bd6279's driver's and not a newer one the
// never-downgrade policy kept.
using System;
using System.Linq;
using System.Security.Principal;
using HIDMaestro;
using HIDMaestro.Internal;

internal static partial class Program
{
    static bool Elevated(string mode)
    {
        using var me = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        Console.WriteLine($"{mode}: [REFUSED] it needs elevation. Nothing was created");
        return false;
    }

#if !HM_BASE
    // ── The driver a baseline session runs on ────────────────────────────

    /// <summary>InstallDriver, then proof that the driver store's newest
    /// package of each INF is the one this SDK embeds and that the install
    /// record names this SDK's payload. A newer driver stays installed under
    /// the never-downgrade policy, and every measurement after it would
    /// then be of that driver: exit 4 says so, with what is installed.
    /// Returns 0, 3 without elevation, 4 when the installed driver is not
    /// this SDK's.</summary>
    static int Install()
    {
        if (!Elevated("install")) return 3;
        using var ctx = new HMContext();
        ctx.InstallDriver();
        Console.WriteLine($"  InstallDriver: {ctx.LastDriverInstall}");
        bool ok = ctx.LastDriverInstall is HMDriverInstallOutcome.AlreadyCurrent or HMDriverInstallOutcome.Installed
            or HMDriverInstallOutcome.Upgraded;
        foreach (string inf in new[] { "hidmaestro.inf", "hidmaestro_xusb.inf" })
        {
            var embedded = DriverDeploy.EmbeddedStamp(inf);
            var installed = DriverDeploy.InstalledStamps(inf);
            var newest = installed.Count == 0 ? (DriverDeploy.DriverStamp?)null : installed.Max();
            bool same = newest is { } n && n.CompareTo(embedded) == 0;
            ok &= same;
            Console.WriteLine(FormattableString.Invariant(
                $"  [{(same ? "PASS" : "FAIL")}] {inf}: embedded {embedded} of {embedded.Date:yyyy-MM-dd}, newest in the driver store {(newest is { } m ? $"{m} of {m.Date:yyyy-MM-dd}" : "none")}, all: {string.Join(", ", installed.OrderBy(s => s).Select(s => FormattableString.Invariant($"{s} of {s.Date:yyyy-MM-dd}")))}"));
        }
        string? recorded = DriverBuilder.InstalledManifestHash();
        bool manifest = string.Equals(recorded, EmbeddedManifest.Sha256Hex, StringComparison.OrdinalIgnoreCase);
        ok &= manifest;
        Console.WriteLine($"  [{(manifest ? "PASS" : "FAIL")}] the install record names payload {recorded ?? "(none)"}, this SDK's is {EmbeddedManifest.Sha256Hex}");
        Console.WriteLine($"install: {(ok ? "PASS" : "FAIL, the driver installed is not this SDK's")}");
        return ok ? 0 : 4;
    }
#endif
}
