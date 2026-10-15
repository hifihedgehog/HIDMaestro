// Which SDK code registers or removes a devnode outside the PnP lock. Read
// from the SDK's source when it sits beside the probe, as interop_decl_check
// reads it. Every call that registers a devnode (SetupDiCreateDeviceInfoW,
// SwdDeviceFactory.Create) or removes or uninstalls one (SetupDiRemoveDevice,
// CM_Uninstall_DevNode, DIF_REMOVE, SwdDeviceFactory.Remove, pnputil
// /remove-device, devcon remove) must sit in a method that takes the lock
// before it, or in one of the three helpers that only run under it
// (DifRemoveDevice, DisableAndUninstall, RemoveDeviceLocked), whose own calls
// are held to the same rule. A text check: it finds a call that moved out of
// the lock, not a lock that does nothing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

internal static class LockCoverage
{
    static readonly string[] Calls =
    {
        "SetupDiCreateDeviceInfoW(", "SwdDeviceFactory.Create(",
        "SetupDiRemoveDevice(", "CM_Uninstall_DevNode(", "DIF_REMOVE,", "SwdDeviceFactory.Remove(",
        "/remove-device", "remove \\\"@",
        "DifRemoveDevice(", "DisableAndUninstall(", "RemoveDeviceLocked(",
    };

    static readonly HashSet<string> UnderLockOnly = new() { "DifRemoveDevice", "DisableAndUninstall", "RemoveDeviceLocked" };

    // A call passed as a delegate to a helper that runs it under the lock
    // it takes itself: RemovePhantom runs each phantom's uninstall.
    static readonly string[] LockingHelpers = { "RemovePhantom(" };

    static readonly string[] LockTakers = { "HoldPnpLock()", "TryHoldPnpLock(", "EnterRegistrationGate(" };

    // Two slashes, built so this file's own text carries no comment marker
    // inside a string.
    static readonly string LineComment = new('/', 2);

    // A member of a top-level type: four spaces, a modifier, a name and an
    // opening parenthesis, and not a declaration that ends in a semicolon.
    static readonly Regex Member = new(@"^ {4}(?:public|internal|private|protected|static)\b[^=;]*?(\w+)\s*(?:<[^>]*>)?\s*\(", RegexOptions.Compiled);

    internal static string? SdkDir()
    {
        for (string? d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "sdk", "HIDMaestro.Core");
            if (File.Exists(Path.Combine(c, "HIDMaestro.Core.csproj"))) return c;
        }
        return null;
    }

    /// <summary>Every registering or removing call outside the lock, as
    /// "file:line method: text". Null when the source is not beside the
    /// probe. <paramref name="sites"/> counts the calls checked.</summary>
    internal static List<string>? Violations(out int sites)
    {
        sites = 0;
        string? sdk = SdkDir();
        if (sdk == null) return null;
        var found = new List<string>();
        foreach (string file in Directory.EnumerateFiles(sdk, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            string[] lines = File.ReadAllLines(file);
            // A locking helper must take the lock itself, before it runs the
            // call it was given.
            foreach (string helper in LockingHelpers)
            {
                int decl = Array.FindIndex(lines, l => Member.IsMatch(l) && l.Contains(" " + helper));
                if (decl < 0) continue;
                int end = Array.FindIndex(lines, decl + 1, l => Member.IsMatch(l) && !l.TrimEnd().EndsWith(";"));
                if (end < 0) end = lines.Length;
                if (!Enumerable.Range(decl, end - decl).Any(k => LockTakers.Any(t => lines[k].Contains(t))))
                    found.Add($"{Path.GetFileName(file)}:{decl + 1} {helper.TrimEnd('(')} runs a removal it is given without taking the lock");
            }
            int memberLine = -1;
            string member = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                var m = Member.Match(line);
                if (m.Success && !line.TrimEnd().EndsWith(";"))
                {
                    memberLine = i;
                    member = m.Groups[1].Value;
                }
                string code = line.TrimStart();
                if (code.StartsWith(LineComment) || code.StartsWith('*') || code.StartsWith("[LibraryImport")) continue;
                int comment = code.IndexOf(LineComment, StringComparison.Ordinal);
                if (comment >= 0 && !code.Contains("\"")) code = code.Substring(0, comment);
                if (!Calls.Any(c => code.Contains(c))) continue;
                if (code.Contains("static partial ")) continue;      // a P/Invoke declaration
                if (m.Success && UnderLockOnly.Contains(member) && code.Contains(member + "(") && memberLine == i) continue; // a helper's own declaration
                sites++;
                if (UnderLockOnly.Contains(member)) continue;
                if (LockingHelpers.Any(h => code.Contains(h))) continue;
                bool held = memberLine >= 0 && Enumerable.Range(memberLine, i - memberLine + 1)
                    .Any(k => LockTakers.Any(t => lines[k].Contains(t)));
                if (!held)
                    found.Add($"{Path.GetFileName(file)}:{i + 1} {member}: {line.Trim()}");
            }
        }
        return found;
    }
}
