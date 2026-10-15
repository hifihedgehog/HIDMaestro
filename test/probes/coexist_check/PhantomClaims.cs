// `CoexistCheck.exe phantomclaims`: the claim the phantom cleanup holds
// while it uninstalls a phantom (DeviceManager.ClaimPhantomOwner). No
// device: it holds and reads named pipes only, under an identity token no
// program makes and transport index 9999. Claims need elevation.
//
//   - a phantom of a token whose claim is free: the cleanup holds that
//     claim until it disposes it, so no create can take it in between,
//   - a token whose claim another holder has: left,
//   - a default token whose index's v1.12.0 owner pipe is held: left,
//   - a name older than identity tokens, and a HID child whose parent
//     cannot be found: no claim, removed as before.

using System;
using System.IO.Pipes;
using System.Reflection;
using HIDMaestro;
using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    static int PhantomClaims()
    {
        Console.WriteLine("=== The phantom cleanup's claims ===");
        // Claims live under ProtectedPrefix\Administrators: unelevated,
        // every TryClaim fails and half these checks would pass on nothing.
        if (!Environment.IsPrivilegedProcess)
        {
            Console.WriteLine("  [SKIP] needs elevation: a claim is a pipe under ProtectedPrefix\\Administrators.");
            return 2;
        }
        var asm = typeof(HMContext).Assembly;
        var dm = asm.GetType("HIDMaestro.Internal.DeviceManager")!;
        var mc = asm.GetType("HIDMaestro.Internal.MachineClaims")!;
        const BindingFlags any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        MethodInfo? claimOwner = dm.GetMethod("ClaimPhantomOwner", any);
        if (claimOwner == null)
        {
            Check("DeviceManager.ClaimPhantomOwner exists", false, "this build has no claim for the phantom cleanup");
            return 1;
        }
        var identityPipe = (Func<string, string>)Delegate.CreateDelegate(typeof(Func<string, string>), mc.GetMethod("IdentityPipeName", any)!);
        var tryClaim = (Func<string, SafeFileHandle?>)Delegate.CreateDelegate(typeof(Func<string, SafeFileHandle?>), mc.GetMethod("TryClaim", any)!);

        (SafeFileHandle? Claim, bool HeldElsewhere) Ask(string instanceId)
        {
            object?[] a = { instanceId, false };
            var claim = (SafeFileHandle?)claimOwner.Invoke(null, a);
            return (claim, (bool)a[1]!);
        }

        const string token = "HM_7E57C0DE0000F00D";
        string id = $@"ROOT\HIDCLASS\{token}";

        var free = Ask(id);
        Check("a phantom whose claim is free: the cleanup takes the claim", free.Claim != null && !free.HeldElsewhere);
        using (var other = tryClaim(identityPipe(token)))
            Check("while the cleanup holds it, a create cannot take it", other == null);
        free.Claim?.Dispose();
        using (var after = tryClaim(identityPipe(token)))
        {
            Check("once the cleanup disposes it, a create can", after != null);
            var held = Ask(id);
            Check("a phantom whose claim another holds is left", held.Claim == null && held.HeldElsewhere);
        }

        using (var legacy = new NamedPipeServerStream("HIDMaestroOwner9999", PipeDirection.InOut, 1))
        {
            var v112 = Ask(@"ROOT\HIDCLASS\HM_9999");
            Check("a default token whose index's v1.12.0 owner pipe is held is left", v112.Claim == null && v112.HeldElsewhere);
        }
        var noV112 = Ask(@"ROOT\HIDCLASS\HM_9999");
        Check("with that pipe gone, the cleanup takes the default token's claim", noV112.Claim != null && !noV112.HeldElsewhere);
        noV112.Claim?.Dispose();

        var old = Ask(@"ROOT\HIDCLASS\0000");
        Check("a name older than identity tokens takes no claim and is not left", old.Claim == null && !old.HeldElsewhere);
        var orphan = Ask(@"HID\HIDCLASS&COL01\1&7e57c0de&0&0000");
        Check("a HID child whose parent cannot be found takes no claim and is not left", orphan.Claim == null && !orphan.HeldElsewhere);

        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
