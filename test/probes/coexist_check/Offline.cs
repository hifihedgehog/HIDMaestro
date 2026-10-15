// Checks that need no elevation and no device: CoexistCheck.exe offline.
//
//   OF1  A directory this process creates under %TEMP%, owned by its own
//        user, may not be reset where it is: a program that owns a
//        directory keeps WRITE_DAC through a handle it opened before any
//        reset (ProtectedStaging.InPlaceRepairAllowed).
//   OF2  %ProgramData% itself may: SYSTEM owns it, and its DACL is the one
//        every folder an elevated program creates under it inherits,
//        v1.12.0's %ProgramData%\HIDMaestro included, which grants Users
//        create and write but no other principal the right to change it.
//   OF4  The refusal. A directory this process's user owns, with a file
//        under it held open so it cannot be moved aside, goes through the
//        repair the shared root takes (ProtectedStaging.EnsureProtected,
//        Repair.InPlace): it throws InvalidOperationException naming the
//        folder, its owner, why the move failed and what clears it, and the
//        folder is left as it was.
using System;
using System.IO;
using System.Reflection;

internal static partial class Program
{
    static int Offline()
    {
        Console.WriteLine("=== Coexistence checks that need no elevation ===");
        var allowed = typeof(HIDMaestro.HMContext).Assembly.GetType("HIDMaestro.Internal.ProtectedStaging")
            ?.GetMethod("InPlaceRepairAllowed", BindingFlags.NonPublic | BindingFlags.Static);
        Check("OF: ProtectedStaging.InPlaceRepairAllowed exists", allowed != null);
        if (allowed != null)
        {
            string own = Path.Combine(Path.GetTempPath(), "HIDMaestro_offline_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(own);
            try
            {
                bool ownAllowed = (bool)allowed.Invoke(null, new object[] { own })!;
                Check("OF1: a directory this process's user owns may not be reset where it is", !ownAllowed, own);
            }
            finally { Directory.Delete(own, true); }
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            bool pdAllowed = (bool)allowed.Invoke(null, new object[] { programData })!;
            Check("OF2: %ProgramData%, owned by SYSTEM with the DACL its folders inherit, may", pdAllowed, programData);
        }
        OfflineRefusal();
        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static void OfflineRefusal()
    {
        var staging = typeof(HIDMaestro.HMContext).Assembly.GetType("HIDMaestro.Internal.ProtectedStaging");
        var ensure = staging?.GetMethod("EnsureProtected", BindingFlags.NonPublic | BindingFlags.Static);
        var repair = staging?.GetNestedType("Repair", BindingFlags.NonPublic);
        var sddl = staging?.GetField("RootSddl", BindingFlags.NonPublic | BindingFlags.Static);
        if (ensure == null || repair == null || sddl == null)
        {
            Check("OF4: ProtectedStaging.EnsureProtected, Repair and RootSddl exist", false);
            return;
        }
        string dir = Path.Combine(Path.GetTempPath(), "HIDMaestro_offline_" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "held", "file");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "held");
        string owner = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        try
        {
            Exception? thrown = null;
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { ensure.Invoke(null, new object[] { dir, sddl.GetValue(null)!, Enum.Parse(repair, "InPlace") }); }
                catch (TargetInvocationException e) { thrown = e.InnerException; }
            }
            string message = thrown?.Message ?? "nothing thrown";
            Check("OF4: a folder its user owns that cannot move is refused with InvalidOperationException",
                  thrown is InvalidOperationException, $"{thrown?.GetType().Name}: {message}");
            Check("OF4: the refusal names the folder, its owner, the failed move and what clears it",
                  message.StartsWith(dir + " is owned by " + owner + ", not Administrators or SYSTEM", StringComparison.OrdinalIgnoreCase)
                  && message.Contains("could not be moved aside (", StringComparison.Ordinal)
                  && message.Contains("SteamVR", StringComparison.Ordinal)
                  && message.Contains("elevated prompt", StringComparison.Ordinal), message);
            Check("OF4: and the folder is left where it was, its file in place",
                  File.Exists(file) && Directory.GetDirectories(Path.GetTempPath(), Path.GetFileName(dir) + ".untrusted-*").Length == 0, dir);
        }
        finally { Directory.Delete(dir, true); }
    }
}
