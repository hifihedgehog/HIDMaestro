// Which SDK code changes a devnode outside the PnP lock. Read from the SDK's
// source when it sits beside the probe, as interop_decl_check reads it. Every
// call that registers a devnode (SetupDiCreateDeviceInfoW, SetupDiCallClassInstaller,
// SwdDeviceFactory.Create), removes or uninstalls one (SetupDiRemoveDevice,
// CM_Uninstall_DevNode, DIF_REMOVE, SwdDeviceFactory.Remove, pnputil
// /remove-device, devcon remove), installs a driver or the null driver on
// one (pnputil /install, DiInstallDevice, DiInstallNullDriver, hmswd.exe
// force-driver), disables, starts or
// re-enumerates one, writes a property on one, or plugs a USB/IP import in or
// out, must sit in a method that takes the lock before it, or in one of the
// three helpers that only run under it (DifRemoveDevice, DisableAndUninstall,
// RemoveDeviceLocked), whose own calls are held to the same rule. A call of
// RestartDevice, which disables and enables a devnode and takes no lock
// itself, must sit inside a hold: a `using var` take whose block is open, or
// a `using (...)` take whose statement it is. Its calls on a controller's own
// devnodes are listed exclusions. No call of it names its devnode by a
// literal: a restart is of a devnode the caller made.
//
// Direct registry writes to devnode keys are watched too: in a method that
// names an Enum or DeviceClasses key, every write (a key created or opened
// for writing, a value set or deleted, a key deleted, reg.exe) must sit
// inside a hold of the lock for that devnode, a take held by a using
// declaration whose block has not closed before the write, or be listed in
// RegistryWrites with its method and the words that name it in
// AcquirePnpLock's doc comment (or none, for a key that is no devnode's).
//
// The calls that change devnodes outside the lock by design are listed in
// Exclusions, each with the one method allowed to make it and the words that
// name it in AcquirePnpLock's doc comment. One made in any other method
// outside the lock fails the check, and so does a listed method that no
// longer makes its call, or a doc comment that no longer names it. A text
// check: it finds a call that moved out of the lock, not a lock that does
// nothing.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

internal static class LockCoverage
{
    static readonly string[] Calls =
    {
        "SetupDiCreateDeviceInfoW(", "SetupDiCallClassInstaller(", "SwdDeviceFactory.Create(",
        "SetupDiRemoveDevice(", "CM_Uninstall_DevNode(", "DIF_REMOVE,", "SwdDeviceFactory.Remove(",
        "/remove-device", "remove \\\"@",
        "DifRemoveDevice(", "DisableAndUninstall(", "RemoveDeviceLocked(",
        " /install\"", "DiInstallDevice(", "DiInstallNullDriver(", "\"force-driver\"", "RunSilentInstall(exe)",
        "CM_Disable_DevNode(", "CM_Enable_DevNode(", "CM_Setup_DevNode(", "CM_Reenumerate_DevNode(",
        "CM_Query_And_Remove_SubTreeW(", "/uninstall /force",
        "CM_Set_DevNode_PropertyW(", "SetupDiSetDeviceRegistryPropertyW(", "CM_Set_DevNode_Registry_PropertyW(",
        "PLUGIN_HARDWARE_ONCE,", "PLUGOUT_HARDWARE,",
        "RestartDevice(",
    };

    /// <summary>Calls whose method takes no lock itself, so a take earlier
    /// in the caller is not enough: the call must sit inside the hold.</summary>
    static readonly string[] HeldCalls = { "RestartDevice(" };

    static readonly HashSet<string> UnderLockOnly = new() { "DifRemoveDevice", "DisableAndUninstall", "RemoveDeviceLocked" };

    // A call passed as a delegate to a helper that runs it under the lock
    // it takes itself: RemovePhantom runs each phantom's uninstall, and
    // RemoveOrphanHidChild each orphan's removal.
    static readonly string[] LockingHelpers = { "RemovePhantom(", "RemoveOrphanHidChild(" };

    static readonly string[] LockTakers = { "HoldPnpLock()", "TryHoldPnpLock(", "EnterRegistrationGate(" };

    /// <summary>The calls that change devnodes outside the PnP lock by
    /// design: the call, the method allowed to make it, and the words that
    /// name it in AcquirePnpLock's doc comment.</summary>
    static readonly (string Call, string Member, string DocTerm)[] Exclusions =
    {
        // The naming writes on a controller's own devnodes.
        ("CM_Set_DevNode_PropertyW(", "SetBusReportedDeviceDesc", "naming writes"),
        ("CM_Set_DevNode_PropertyW(", "SetDeviceFriendlyName", "naming writes"),
        ("CM_Set_DevNode_PropertyW(", "SetAllNamingProperties", "naming writes"),
        ("CM_Set_DevNode_PropertyW(", "FixHidChildNames", "naming writes"),
        ("CM_Set_DevNode_PropertyW(", "ApplyFriendlyNameForController", "naming writes"),
        // RestartDevice's disable and enable.
        ("CM_Disable_DevNode(", "RestartDevice", "RestartDevice's CM_Disable_DevNode and CM_Enable_DevNode"),
        ("CM_Enable_DevNode(", "RestartDevice", "RestartDevice's CM_Disable_DevNode and CM_Enable_DevNode"),
        // RestartDevice on a controller's own devnodes.
        ("RestartDevice(", "CreateGamepadCompanion", "RestartDevice's CM_Disable_DevNode and CM_Enable_DevNode"),
        ("RestartDevice(", "CreateXusbCompanion", "RestartDevice's CM_Disable_DevNode and CM_Enable_DevNode"),
        ("RestartDevice(", "SetupControllerCore", "RestartDevice's CM_Disable_DevNode and CM_Enable_DevNode"),
        // The driver package removal.
        ("/uninstall /force", "DeletePackage", "/uninstall /force"),
        // The usbip-win2 host controller's repair.
        ("CM_Query_And_Remove_SubTreeW(", "RestartHostControllerDevnode", "CM_Query_And_Remove_SubTreeW"),
        ("CM_Setup_DevNode(", "RestartHostControllerDevnode", "CM_Setup_DevNode"),
        ("CM_Reenumerate_DevNode(", "RestartHostControllerDevnode", "CM_Reenumerate_DevNode"),
        // The usbip-win2 installer, the host controller's hardware id stamp
        // and a WinUSB restore's reinstall flag.
        ("RunSilentInstall(exe)", "InstallLocked", "usbip-win2 installer's own ROOT\\USB devnode"),
        ("SetupDiSetDeviceRegistryPropertyW(", "StampOne", "hardware id stamp"),
        ("CM_Set_DevNode_Registry_PropertyW(", "WriteConfigFlags", "CONFIGFLAG_REINSTALL"),
        // A USB/IP import's attach and detach.
        ("PLUGIN_HARDWARE_ONCE,", "Attach", "attach and detach"),
        ("PLUGOUT_HARDWARE,", "Detach", "attach and detach"),
        // The transport driver update.
        ("\"force-driver\"", "ReplaceLegacyTransport", "hmswd.exe force-driver"),
        // A WinUSB bind, and on Windows 10 its null driver first.
        ("DiInstallDevice(", "InstallOn", "WinUSB bind"),
        ("DiInstallNullDriver(", "InstallFromInf", "WinUSB bind"),
        ("DiInstallDevice(", "InstallFromInf", "WinUSB bind"),
        // A registration's driver install, after its hold.
        ("DiInstallDevice(", "InstallBestStoreDriver", "registration's DiInstallDevice"),
    };

    /// <summary>The direct registry writes to devnode keys made outside a
    /// hold of the PnP lock in their method: the write, the
    /// method, and the words that name it in AcquirePnpLock's doc comment,
    /// or null for a write, in a method that also names a devnode key, to a
    /// key that is no devnode's.</summary>
    static readonly (string Write, string Member, string? DocTerm)[] RegistryWrites =
    {
        // A ROOT registration's orphan instance key, inside its registration's hold.
        ("writable: true", "CreateDeviceNode", "orphan instance key"),
        ("DeleteSubKeyTree(identity.Token", "CreateDeviceNode", "orphan instance key"),
        // The ParentIdPrefix of a controller's own parent.
        ("CreateSubKey(path)", "ApplyParentIdPrefix", "ParentIdPrefix"),
        ("writable: true", "ApplyParentIdPrefix", "ParentIdPrefix"),
        (".SetValue(\"ParentIdPrefix\"", "ApplyParentIdPrefix", "ParentIdPrefix"),
        // ControllerIndex in a controller's own Device Parameters.
        ("CreateSubKey(dpPath)", "CreateDeviceNode", "ControllerIndex"),
        (".SetValue(\"ControllerIndex\"", "CreateDeviceNode", "ControllerIndex"),
        ("CreateSubKey(dpPath)", "CreateGamepadCompanion", "ControllerIndex"),
        (".SetValue(\"ControllerIndex\"", "CreateGamepadCompanion", "ControllerIndex"),
        ("CreateSubKey(dpPath)", "CreateXusbCompanion", "ControllerIndex"),
        (".SetValue(\"ControllerIndex\"", "CreateXusbCompanion", "ControllerIndex"),
        ("CreateSubKey(dpPath)", "SetupControllerCore", "ControllerIndex"),
        (".SetValue(\"ControllerIndex\"", "SetupControllerCore", "ControllerIndex"),
        // The HID parent's UpperFilters, on the controller's own devnode.
        ("writable: true", "SetHidParentUpperFilterXinputhid", "UpperFilters"),
        (".SetValue(\"UpperFilters\"", "SetHidParentUpperFilterXinputhid", "UpperFilters"),
        // The Everything cleanup's Device Parameters deletes.
        ("\"reg.exe\"", "RemoveEverythingGated", "Device Parameters"),
        // The interface-class key the cleanup's deletes go through, each
        // delete under the lock.
        ("writable: true", "RemoveOwnedInterfaceEntries", null),
        // Keys that are no devnode's: the device override, the SDK's own
        // configuration under SOFTWARE\HIDMaestro.
        ("CreateSubKey(overridePath)", "CreateDeviceNode", null),
        (".SetValue(\"Removable\"", "CreateDeviceNode", null),
        ("CreateSubKey(RegPathForIndex(", "CreateGamepadCompanion", null),
        (".SetValue(\"LastSwdGamepadHResult\"", "CreateGamepadCompanion", null),
        ("CreateSubKey(RegPathForIndex(", "CreateXusbCompanion", null),
        (".SetValue(\"LastSwdCompanionHResult\"", "CreateXusbCompanion", null),
        ("DeleteSubKeyTree(@\"SOFTWARE\\HIDMaestro\"", "RemoveEverythingGated", null),
    };

    /// <summary>A method that names a devnode key.</summary>
    static readonly Regex DevnodeKey = new(@"CurrentControlSet\\+Enum|DeviceClasses", RegexOptions.Compiled);

    /// <summary>A registry write.</summary>
    static readonly Regex RegistryWrite = new(
        @"CreateSubKey\(|writable:\s*true|OpenSubKey\([^)]*,\s*true\)|\.SetValue\(|DeleteSubKeyTree\(|DeleteSubKey\(|DeleteValue\(|""reg\.exe""",
        RegexOptions.Compiled);

    /// <summary>A take of the lock for one devnode, held by a <c>using var</c>
    /// to the end of its block. The registration gate's hold ends inside its
    /// method, so it does not count here.</summary>
    static readonly string[] DevnodeLockTakers = { "HoldPnpLock()", "TryHoldPnpLock(", "holdLock()" };

    /// <summary>Whether line <paramref name="i"/> of the member that starts
    /// at <paramref name="from"/> lies inside a hold of the lock: a
    /// <c>using var</c> take earlier in the member whose block has not closed
    /// before the line.</summary>
    static bool InsideHold(string[] lines, int from, int i)
    {
        int depth = 0;
        var holds = new Stack<int>();
        for (int k = from; k < i; k++)
        {
            string code = CodeOf(lines[k]);
            if (code.TrimStart().StartsWith("using var ", StringComparison.Ordinal) && DevnodeLockTakers.Any(t => code.Contains(t)))
                holds.Push(depth);
            foreach (char c in code)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    while (holds.Count > 0 && holds.Peek() > depth) holds.Pop();
                }
            }
        }
        return holds.Count > 0;
    }

    /// <summary>Whether line <paramref name="i"/> is the statement of a
    /// <c>using (...)</c> take of the lock on the code line before it.</summary>
    static bool UnderUsingStatement(string[] lines, int i)
    {
        for (int k = i - 1; k >= 0; k--)
        {
            string code = CodeOf(lines[k]).Trim();
            if (code.Length == 0) continue;
            return code.StartsWith("using (", StringComparison.Ordinal)
                   && DevnodeLockTakers.Any(t => code.Contains(t))
                   && !code.EndsWith(";");
        }
        return false;
    }

    /// <summary>A line without its comment and without the text of its
    /// string and character literals, for counting braces.</summary>
    static string CodeOf(string line)
    {
        var code = new System.Text.StringBuilder();
        for (int j = 0; j < line.Length; j++)
        {
            char c = line[j];
            if (c == '/' && j + 1 < line.Length && line[j + 1] == '/') break;
            if (c != '"' && c != '\'')
            {
                code.Append(c);
                continue;
            }
            bool verbatim = c == '"' && j > 0 && (line[j - 1] == '@' || (line[j - 1] == '$' && j > 1 && line[j - 2] == '@'));
            for (j++; j < line.Length; j++)
            {
                if (!verbatim && line[j] == '\\') { j++; continue; }
                if (line[j] != c) continue;
                if (verbatim && j + 1 < line.Length && line[j + 1] == c) { j++; continue; }
                break;
            }
        }
        return code.ToString();
    }

    /// <summary>The number of registry writes the check lists.</summary>
    internal static int RegistryWriteCount => RegistryWrites.Length;

    const string LockMethod = "internal static Mutex? AcquirePnpLock(int timeoutMs)";

    // Two slashes, built so this file's own text carries no comment marker
    // inside a string.
    static readonly string LineComment = new('/', 2);

    // A member of a top-level type: four spaces, a modifier, a name and an
    // opening parenthesis, and not a declaration that ends in a semicolon.
    static readonly Regex Member = new(@"^ {4}(?:public|internal|private|protected|static)\b[^=;]*?(\w+)(?:<[^>]*>)?\(", RegexOptions.Compiled);

    internal static string? SdkDir()
    {
        for (string? d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "sdk", "HIDMaestro.Core");
            if (File.Exists(Path.Combine(c, "HIDMaestro.Core.csproj"))) return c;
        }
        return null;
    }

    /// <summary>The number of exclusions the check knows.</summary>
    internal static int ExclusionCount => Exclusions.Length;

    /// <summary>Every registering or removing call outside the lock that is
    /// not a listed exclusion, every exclusion no longer found, and every
    /// exclusion AcquirePnpLock's doc comment does not name, as
    /// "file:line method: text". Null when the source is not beside the
    /// probe. <paramref name="sites"/> counts the calls checked.</summary>
    internal static List<string>? Violations(out int sites)
    {
        sites = 0;
        string? sdk = SdkDir();
        if (sdk == null) return null;
        var found = new List<string>();
        var used = new bool[Exclusions.Length];
        var usedWrites = new bool[RegistryWrites.Length];
        string? lockDoc = null;
        foreach (string file in Directory.EnumerateFiles(sdk, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            string[] lines = File.ReadAllLines(file);
            lockDoc ??= DocBefore(lines, LockMethod);
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
                if (m.Success && memberLine == i && HeldCalls.Any(c => code.Contains(" " + c))) continue; // its own declaration
                sites++;
                // A restart takes the instance ID of a devnode the caller
                // made. One named by a literal is a devnode no create makes,
                // another program's or residue, which this program has no
                // business disabling.
                if (code.Contains("RestartDevice(@\"") || code.Contains("RestartDevice(\""))
                    found.Add($"{Path.GetFileName(file)}:{i + 1} {member}: restarts a devnode named by a literal, which no create makes: {line.Trim()}");
                if (UnderLockOnly.Contains(member)) continue;
                if (LockingHelpers.Any(h => code.Contains(h))) continue;
                bool held = HeldCalls.Any(c => code.Contains(c))
                    ? memberLine >= 0 && (InsideHold(lines, memberLine, i) || UnderUsingStatement(lines, i))
                    : memberLine >= 0 && Enumerable.Range(memberLine, i - memberLine + 1)
                        .Any(k => LockTakers.Any(t => lines[k].Contains(t)));
                if (held) continue;
                int allowed = Array.FindIndex(Exclusions, e => e.Member == member && code.Contains(e.Call));
                if (allowed >= 0)
                {
                    used[allowed] = true;
                    continue;
                }
                found.Add($"{Path.GetFileName(file)}:{i + 1} {member}: {line.Trim()}");
            }
            // Direct registry writes to devnode keys, method by method.
            var starts = Enumerable.Range(0, lines.Length)
                .Where(k => Member.IsMatch(lines[k]) && !lines[k].TrimEnd().EndsWith(";")).ToList();
            for (int s = 0; s < starts.Count; s++)
            {
                int from = starts[s], to = s + 1 < starts.Count ? starts[s + 1] : lines.Length;
                string name = Member.Match(lines[from]).Groups[1].Value;
                if (!Enumerable.Range(from, to - from).Any(k => DevnodeKey.IsMatch(lines[k]))) continue;
                for (int i = from; i < to; i++)
                {
                    string code = lines[i].TrimStart();
                    if (code.StartsWith(LineComment) || code.StartsWith('*')) continue;
                    if (!RegistryWrite.IsMatch(code)) continue;
                    sites++;
                    if (InsideHold(lines, from, i)) continue;
                    int listed = Array.FindIndex(RegistryWrites, w => w.Member == name && code.Contains(w.Write));
                    if (listed >= 0)
                    {
                        usedWrites[listed] = true;
                        continue;
                    }
                    found.Add($"{Path.GetFileName(file)}:{i + 1} {name}: a registry write to a devnode key outside the lock, not listed: {lines[i].Trim()}");
                }
            }
        }
        for (int k = 0; k < RegistryWrites.Length; k++)
        {
            var (write, member, docTerm) = RegistryWrites[k];
            if (!usedWrites[k])
                found.Add($"the registry write {member}: {write} is no longer made outside the lock, so the list is stale");
            if (docTerm != null && (lockDoc == null || !lockDoc.Contains(docTerm, StringComparison.Ordinal)))
                found.Add($"AcquirePnpLock's doc comment does not name the registry write {member}: {write} (\"{docTerm}\")");
        }
        for (int k = 0; k < Exclusions.Length; k++)
        {
            var (call, member, docTerm) = Exclusions[k];
            if (!used[k])
                found.Add($"the exclusion {member}: {call} is no longer made outside the lock, so the list and the lock's doc comment are stale");
            if (lockDoc == null || !lockDoc.Contains(docTerm, StringComparison.Ordinal))
                found.Add($"AcquirePnpLock's doc comment does not name the exclusion {member}: {call} (\"{docTerm}\")");
        }
        return found;
    }

    /// <summary>The doc comment right above the line that starts with
    /// <paramref name="declaration"/>, its lines joined with single spaces,
    /// or null when no such line is in <paramref name="lines"/>.</summary>
    static string? DocBefore(string[] lines, string declaration)
    {
        int decl = Array.FindIndex(lines, l => l.Trim().StartsWith(declaration, StringComparison.Ordinal));
        if (decl < 0) return null;
        string doc = LineComment + "/";
        int first = decl;
        while (first > 0 && lines[first - 1].TrimStart().StartsWith(doc, StringComparison.Ordinal)) first--;
        return string.Join(" ", Enumerable.Range(first, decl - first)
            .Select(k => lines[k].TrimStart().Substring(doc.Length).Trim()));
    }
}
