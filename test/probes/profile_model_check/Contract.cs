// The SDK's half of the version contract (design note, decision (c)).
//
// The SDK writes 4 as its oldest driver protocol and its own, and into the
// version section the controller's identity tag: the 64-bit FNV-1a hash of
// its token's UTF-16LE code units, no terminator. Before any PnP work it
// writes the controller's configuration anew, the key deleted first, with
// FeatureAnswers (the answers compiled for the controller) and an empty
// CachedInputReportIds, and the identity entry
// SOFTWARE\HIDMaestro\Identity\<token> whose ControllerIndex names the
// index. The configuration's deletion takes every entry that names the
// index with it.
//
// WriteInstanceConfig runs against a registry hive this check owns, as
// device_oracle_check's case 5 runs it: RegLoadAppKey loads a hive file no
// other process sees, and RegOverridePredefKey maps HKEY_LOCAL_MACHINE to
// it for this process. The check stops before writing when HKLM still
// shows the machine's keys. The profile's Type is set to keyboard for the
// call, which skips the joystick OEM name and its machine-wide lock, and
// no value of the controller's key reads Type.
//
// The base has no tag, writes the key over what it held, writes neither
// value nor the entry, and takes protocols 2 to 3.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Contract
{
    public static int Run()
    {
        var c = new Checks("contract");
        Tags(c);
        Protocols(c);
        // The catalog loads before HKLM maps to the hive.
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        InHive(c, cc => Config(cc, ctx));
        return c.Failures;
    }

    static void Tags(Checks c)
    {
        var tagOf = typeof(DeviceIdentity).GetMethod("TagOf", BindingFlags.Public | BindingFlags.Static);
        foreach (var (token, want) in new[] { ("HM_0000", 0x1FD048916B8D461FUL), ("HM_0123456789ABCDEF", 0x3B86F3634086D831UL) })
            c.Run($"the identity tag of {token} is {want:X16}", () =>
            {
                if (tagOf == null) return (false, "the SDK has no DeviceIdentity.TagOf");
                ulong got = (ulong)tagOf.Invoke(null, new object[] { token })!;
                return (got == want, $"{got:X16}");
            });
        c.Run("a controller's tag is its token's", () =>
        {
            var id = DeviceIdentity.ForKey("pad-one", 3);
            var tag = typeof(DeviceIdentity).GetProperty("Tag")?.GetValue(id);
            return (tag is ulong t && tagOf != null && t == (ulong)tagOf.Invoke(null, new object[] { id.Token })!, $"{id.Token} {tag}");
        });
    }

    static void Protocols(Checks c)
    {
        int Const(string name) => (int)(typeof(SharedMemoryIO).GetField(name)?.GetValue(null) ?? -1);
        c.Run("the SDK speaks protocol 4 and runs on 4 alone", () =>
            (Const("SDK_PROTOCOL_MIN") == 4 && Const("SDK_PROTOCOL") == 4, $"{Const("SDK_PROTOCOL_MIN")} to {Const("SDK_PROTOCOL")}"));
        c.Run("the version section's v2.0.0 offsets: 36, 40, 44, 48, 56 and 60", () =>
        {
            int[] got = { Const("VERSION_OFFSET_DRIVER_PROTOCOL_MIN"), Const("VERSION_OFFSET_COMPANION_PROTOCOL"),
                          Const("VERSION_OFFSET_COMPANION_PROTOCOL_MIN"), Const("VERSION_OFFSET_IDENTITY_TAG"),
                          Const("VERSION_OFFSET_DRIVER_CONFIG_ERROR"), Const("VERSION_OFFSET_COMPANION_CONFIG_ERROR") };
            return (got.SequenceEqual(new[] { 36, 40, 44, 48, 56, 60 }), string.Join(" ", got));
        });
    }

    // ── The configuration, in a hive this check owns ─────────────────────

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegLoadAppKeyW(string file, out IntPtr key, int samDesired, int options, int reserved);
    [DllImport("advapi32.dll")]
    static extern int RegOverridePredefKey(IntPtr key, IntPtr newKey);
    [DllImport("advapi32.dll")]
    static extern int RegCloseKey(IntPtr key);

    static readonly IntPtr HKEY_LOCAL_MACHINE = unchecked((IntPtr)(int)0x80000002);
    const int KEY_ALL_ACCESS = 0xF003F;

    static void InHive(Checks c, Action<Checks> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"hm-contract-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        string hive = Path.Combine(dir, "contract.hive");
        int rc = RegLoadAppKeyW(hive, out IntPtr appKey, KEY_ALL_ACCESS, 0, 0);
        if (rc != 0) { c.Check("the check's hive loads", false, $"RegLoadAppKeyW returned {rc}"); return; }
        try
        {
            rc = RegOverridePredefKey(HKEY_LOCAL_MACHINE, appKey);
            if (rc != 0) { c.Check("HKLM maps to the check's hive", false, $"RegOverridePredefKey returned {rc}"); return; }
            try
            {
                using (var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (machine != null) { c.Check("HKLM maps to the check's hive", false, "the machine's keys show: nothing written"); return; }
                body(c);
            }
            finally { RegOverridePredefKey(HKEY_LOCAL_MACHINE, IntPtr.Zero); }
        }
        finally
        {
            RegCloseKey(appKey);
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static readonly MethodInfo s_write = typeof(DeviceOrchestrator).GetMethod("WriteInstanceConfig", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>WriteInstanceConfig for a profile at an index, under a
    /// token, as a create calls it.</summary>
    static void Write(ControllerProfile p, int index, DeviceIdentity id)
    {
        string type = p.Type;
        p.Type = "keyboard";
        try
        {
            object[] args = s_write.GetParameters().Length == 3 ? new object[] { index, p, id } : new object[] { index, p };
            s_write.Invoke(null, args);
        }
        finally { p.Type = type; }
    }

    static RegistryKey? Key(string path) => Registry.LocalMachine.OpenSubKey(path);

    static void Config(Checks c, HMContext ctx)
    {
        var sony = ctx.GetProfile("dualsense-bt")!.Inner;
        var xbox = ctx.GetProfile("xbox-360-wired")!.Inner;
        var idA = DeviceIdentity.ForIndex(2);
        var idB = DeviceIdentity.ForKey("pad-two", 2);

        c.Run("dualsense-bt at index 2: FeatureAnswers is the blob of its table for index 2 and the serial the driver serves, format 4", () =>
        {
            Write(sony, 2, idA);
            using var k = Key(@"SOFTWARE\HIDMaestro\Controller2");
            if (k?.GetValue("FeatureAnswers") is not byte[] blob) return (false, "no FeatureAnswers");
            var want = HIDMaestro.Internal.Usbip.FeatureStubTable.From(sony, 2, "HM-CTL-0002")!.ToBlob();
            return (blob.AsSpan().SequenceEqual(want) && BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4)) == 4,
                    $"{blob.Length} bytes");
        });
        c.Run("and CachedInputReportIds is empty", () =>
        {
            using var k = Key(@"SOFTWARE\HIDMaestro\Controller2");
            return (k?.GetValue("CachedInputReportIds") is byte[] { Length: 0 }, "");
        });
        c.Run("its identity entry names index 2 under its token", () =>
        {
            using var e = Key($@"SOFTWARE\HIDMaestro\Identity\{idA.Token}");
            return (e?.GetValue("ControllerIndex") is int i && i == 2 && e.GetValueKind("ControllerIndex") == RegistryValueKind.DWord, idA.Token);
        });
        c.Run("xbox-360-wired then on index 2 under another token: the empty blob, no value of dualsense-bt's left, and one entry naming the index", () =>
        {
            using (var k0 = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\HIDMaestro\Controller2"))
                k0.SetValue("LeftByAnotherProgram", 1, RegistryValueKind.DWord);
            Write(xbox, 2, idB);
            using var k = Key(@"SOFTWARE\HIDMaestro\Controller2");
            byte[]? blob = k?.GetValue("FeatureAnswers") as byte[];
            bool empty = blob != null && blob.Length == 16 && BinaryPrimitives.ReadUInt32LittleEndian(blob) == 16;
            bool left = k?.GetValue("LeftByAnotherProgram") != null;
            using var root = Key(@"SOFTWARE\HIDMaestro\Identity");
            var naming = root?.GetSubKeyNames().Where(t => root.OpenSubKey(t)?.GetValue("ControllerIndex") is int i && i == 2).ToList() ?? new();
            return (empty && !left && naming.SequenceEqual(new[] { idB.Token }),
                    $"blob {blob?.Length}, left value {(left ? "kept" : "gone")}, entries {string.Join(",", naming)}");
        });
        c.Run("deleting index 2's configuration deletes its entry and keeps index 5's", () =>
        {
            var idC = DeviceIdentity.ForIndex(5);
            Write(xbox, 5, idC);
            DeviceOrchestrator.DeleteInstanceConfig(2);
            bool gone2 = Key(@"SOFTWARE\HIDMaestro\Controller2") == null && Key($@"SOFTWARE\HIDMaestro\Identity\{idB.Token}") == null;
            bool kept5 = Key($@"SOFTWARE\HIDMaestro\Identity\{idC.Token}")?.GetValue("ControllerIndex") is int i && i == 5;
            DeviceOrchestrator.DeleteInstanceConfig(5);
            return (gone2 && kept5, $"index 2 {(gone2 ? "gone" : "left")}, index 5 {(kept5 ? "kept" : "missing")}");
        });
    }
}
