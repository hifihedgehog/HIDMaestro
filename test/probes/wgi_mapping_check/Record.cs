// --record <out dir> [<profile>...]: what Windows.Gaming.Input and the
// GameInput mapping table give each profile, recorded for
// device_oracle_check's compare-live mode in its golden format (case
// live-wgi-gameinput). With no profile named, every catalog profile whose
// connection is Bluetooth or that gets an XUSB companion
// (ControllerProfile.RequiresXusbCompanion), in id order.
//
// Each profile is created alone and fed its rest state, as --explore feeds
// it. Then, in stream wgi/<profile>/index<N>:
//   gameInputKey        the values of its key under
//                       HKLM\SYSTEM\CurrentControlSet\Control\GameInput\Devices
//                       (<vid><pid>00010005, the key --explore reads), one
//                       "<subkey>\<name>=<kind>:<hex>" line each in ordinal
//                       order, or no bytes when the key is absent
//   rawCount, gamepads  RawGameControllers with its VID and PID that arrived
//                       after the create, within 10 s, and how many of them
//                       are Gamepads (u32 each). A RawGameController
//                       present before the create, a real pad among them,
//                       is never counted or read
//   raw                 the first one's display name, axis, button and
//                       switch counts and whether it is a Gamepad
//   then at rest and after each stimulus --explore applies: stimulus (its
//   label), axes (each a little-endian double), buttons (one byte each),
//   switches (one byte each, the GameControllerSwitchPosition) and, for a
//   Gamepad, pad (the six GamepadReading doubles, then its buttons, u32)
//   gameInputKeyAfter   the key 3 s after the dispose, as gameInputKey
//
// The control profile, xbox-360-wired, must be among the profiles (the
// default list holds it). It runs first, and WGI must list it as a
// RawGameController: a run in which WGI lists nothing at all would
// otherwise record a raw count of 0 for every profile and prove nothing
// (review finding 11). When WGI does not list the control, the step fails
// and writes nothing. Once it has, a profile WGI does not list is recorded
// with a raw count of 0.
//
// Creates controllers, so it refuses without elevation (exit 3, nothing
// created). Exit 0 recorded, 1 a step failed or the control is missing
// from the list, nothing written.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32;
using Windows.Gaming.Input;

internal static partial class Program
{
    const string RecordControlProfile = "xbox-360-wired";

    static int Record(string outDir, List<string> ids)
    {
        Console.WriteLine("=== WGI and GameInput, recorded for device_oracle_check ===");
        using (var me = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (!new System.Security.Principal.WindowsPrincipal(me)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [REFUSED] --record creates controllers and needs elevation. Nothing was created");
                return 3;
            }
        }
        if (ids.Count == 0)
            ids = ProfileDatabase.LoadEmbedded().All
                .Where(p => p.Connection == "bluetooth" || p.RequiresXusbCompanion)
                .Select(p => p.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
        if (!ids.Contains(RecordControlProfile))
        {
            Console.WriteLine($"  [FAIL] the control profile {RecordControlProfile} is not among the profiles, so a run WGI lists nothing in would pass. Nothing was created");
            return 1;
        }
        // The control first, so a run that cannot see it stops before the rest.
        ids = new[] { RecordControlProfile }.Concat(ids.Where(id => id != RecordControlProfile)).ToList();
        Console.WriteLine($"  {ids.Count} profiles: {string.Join(", ", ids)}");

        using var rec = new LiveRecorder(outDir, "live-wgi-gameinput");
        try
        {
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            foreach (string id in ids)
            {
                var profile = ctx.GetProfile(id) ?? throw new InvalidOperationException($"no profile {id}");
                int raw = RecordOne(ctx, profile, rec);
                if (raw == 0 && id == RecordControlProfile)
                    throw new InvalidOperationException($"WGI lists no RawGameController for the control profile {id}, so no profile's absence can be recorded");
                Console.WriteLine($"  [OK] {id}");
            }
            rec.Save();
            Console.WriteLine($"  wrote {outDir}");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  [FAIL] {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    /// <summary>One profile's stream. Returns its raw count: the
    /// RawGameControllers WGI listed for it.</summary>
    static int RecordOne(HMContext ctx, HMProfile profile, LiveRecorder rec)
    {
        ushort vid = profile.VendorId, pid = profile.ProductId;
        string keyName = $"{vid:X4}{pid:X4}00010005";
        var before = new HashSet<string>(RawGameController.RawGameControllers.Select(r => r.NonRoamableId), StringComparer.Ordinal);
        var seen = new List<string>();
        int rawCount = 0;

        using (var pad = ctx.CreateController(profile))
        {
            rec.Stream($"wgi/{profile.Id}/index{pad.TransportIndex}");
            bool stop = false;
            var rest = new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(profile) };
            var state = rest;
            object gate = new();
            var feeder = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    HMGamepadState s;
                    lock (gate) s = state;
                    try { pad.SubmitState(in s); } catch { }
                    Thread.Sleep(8);
                }
            }) { IsBackground = true };
            feeder.Start();
            try
            {
                rec.Out("gameInputKey", KeyText(keyName));

                // The RawGameControllers of the VID and PID that arrived
                // after the create, read again after the settle time, or none
                // after 10 s.
                var sw = Stopwatch.StartNew();
                List<RawGameController> mine = new();
                while (sw.ElapsedMilliseconds < 10000)
                {
                    mine = RawGameController.RawGameControllers
                        .Where(r => r.HardwareVendorId == vid && r.HardwareProductId == pid && !before.Contains(r.NonRoamableId))
                        .ToList();
                    if (mine.Count > 0) break;
                    Thread.Sleep(100);
                }
                if (mine.Count > 0)
                {
                    Thread.Sleep(s_settleMs);
                    mine = RawGameController.RawGameControllers
                        .Where(r => r.HardwareVendorId == vid && r.HardwareProductId == pid && !before.Contains(r.NonRoamableId))
                        .ToList();
                }
                seen.AddRange(mine.Select(r => r.NonRoamableId));
                rawCount = mine.Count;
                rec.OutU32("rawCount", (uint)mine.Count);
                rec.OutU32("gamepads", (uint)mine.Count(r => Gamepad.FromGameController(r) != null));
                if (mine.Count == 0)
                    Console.WriteLine($"  {profile.Id}: WGI lists no new RawGameController within 10 s");
                else
                    ReadAll(mine[0]);
            }
            finally
            {
                Volatile.Write(ref stop, true);
                feeder.Join(500);
            }

            void ReadAll(RawGameController raw)
            {
                var gp = Gamepad.FromGameController(raw);
                rec.Out("raw", Encoding.UTF8.GetBytes(
                    $"{raw.DisplayName}|axes={raw.AxisCount}|buttons={raw.ButtonCount}|switches={raw.SwitchCount}|gamepad={(gp != null ? 1 : 0)}"));

                void Reading(string label)
                {
                    var s = Read(raw, gp);
                    rec.Out("stimulus", Encoding.ASCII.GetBytes(label));
                    var axes = new byte[s.Axes.Length * 8];
                    for (int i = 0; i < s.Axes.Length; i++) BitConverter.TryWriteBytes(axes.AsSpan(i * 8), s.Axes[i]);
                    rec.Out("axes", axes);
                    rec.Out("buttons", s.Buttons.Select(b => b ? (byte)1 : (byte)0).ToArray());
                    rec.Out("switches", s.Switches.Select(x => (byte)x).ToArray());
                    if (s.Pad is { } r)
                    {
                        var p = new byte[6 * 8 + 4];
                        double[] v = { r.LeftThumbstickX, r.LeftThumbstickY, r.RightThumbstickX, r.RightThumbstickY, r.LeftTrigger, r.RightTrigger };
                        for (int i = 0; i < 6; i++) BitConverter.TryWriteBytes(p.AsSpan(i * 8), v[i]);
                        BitConverter.TryWriteBytes(p.AsSpan(48), (uint)r.Buttons);
                        rec.Out("pad", p);
                    }
                }

                void Stimulus(string label, HMGamepadState s)
                {
                    lock (gate) state = s;
                    Thread.Sleep(220);
                    Reading(label);
                    lock (gate) state = rest;
                    Thread.Sleep(120);
                }

                HMGamepadState Ax(float lx = .5f, float ly = .5f, float rx = .5f, float ry = .5f, float lt = 0, float rt = 0)
                    => new() { Axes = HMGamepadStateHelpers.StandardAxes(profile, lx, ly, rx, ry, lt, rt) };
                HMGamepadState Btn(HMButton b) => new() { Axes = HMGamepadStateHelpers.StandardAxes(profile), Buttons = b };

                Reading("rest");
                Stimulus("LX right", Ax(lx: 1f));
                Stimulus("LY down", Ax(ly: 1f));
                Stimulus("RX right", Ax(rx: 1f));
                Stimulus("RY down", Ax(ry: 1f));
                Stimulus("LT full", Ax(lt: 1f));
                Stimulus("RT full", Ax(rt: 1f));
                foreach (var b in new[] { HMButton.A, HMButton.B, HMButton.X, HMButton.Y, HMButton.LeftBumper, HMButton.RightBumper,
                                          HMButton.Back, HMButton.Start, HMButton.LeftStick, HMButton.RightStick, HMButton.Guide })
                    Stimulus(b.ToString(), Btn(b));
                Stimulus("Hat North", new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(profile), Hat = HMHat.North });
                Stimulus("Hat East", new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(profile), Hat = HMHat.East });
            }
        }
        // Profiles of one VID and PID share an interface path at one
        // index, so the next create must not meet this one on its way out.
        var gone = Stopwatch.StartNew();
        while (RawGameController.RawGameControllers.Any(r => seen.Contains(r.NonRoamableId)))
        {
            if (gone.ElapsedMilliseconds > 15000)
                throw new InvalidOperationException($"{profile.Id}: WGI still lists it 15 s after the dispose");
            Thread.Sleep(100);
        }
        Thread.Sleep(3000);
        rec.Out("gameInputKeyAfter", KeyText(keyName));
        return rawCount;
    }

    /// <summary>The key's values and its subkeys' values, one line each,
    /// in ordinal order, or no bytes when the key is absent.</summary>
    static byte[] KeyText(string keyName)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"{OverrideRoot}\{keyName}");
        if (k == null) return Array.Empty<byte>();
        var lines = new List<string>();
        void Walk(RegistryKey key, string at)
        {
            foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
            {
                var kind = key.GetValueKind(name);
                var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                string hex = data switch
                {
                    byte[] b => Convert.ToHexString(b),
                    int i => ((uint)i).ToString("X8"),
                    long l => ((ulong)l).ToString("X16"),
                    string s => Convert.ToHexString(Encoding.Unicode.GetBytes(s)),
                    string[] m => Convert.ToHexString(Encoding.Unicode.GetBytes(string.Join("\0", m))),
                    _ => "",
                };
                lines.Add($"{at}\\{name}={kind}:{hex}");
            }
            foreach (var sub in key.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal))
            {
                using var sk = key.OpenSubKey(sub);
                if (sk != null) Walk(sk, at + "\\" + sub);
            }
        }
        Walk(k, "");
        return Encoding.UTF8.GetBytes(string.Join("\n", lines));
    }
}
