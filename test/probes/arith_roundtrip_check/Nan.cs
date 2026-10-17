// nan, run with roundtrip: a NaN a program writes into HMGamepadState.Axes
// is no value, so every path writes what it writes for an axis left
// unwritten, the field's rest. One stick never has two answers. Each case
// submits one frame through the SDK's own controller, built offline on a
// buffer of this probe's as device_oracle_check's case 1 builds it, once
// with an axis set to NaN and once with the axis left out, on two fresh
// controllers, and the two buffers must be equal. Then each fallback pair:
// a NaN on the key a slot reads first, beside a value on the key it reads
// when the first is absent, must write the first key's rest, never the
// fallback's value. The paths: the descriptor
// path (sticks, centered sticks, triggers), the XUSB slice, the codec
// (uint8-axis, int16-axis, stick12-pair, uint16-trigger), the Switch Pro
// packer and the Switch 2 Pro composite's packer. Then the codec and both
// packers called with a NaN slot directly, against the slot's rest value.
// Creates no device, opens no section and needs no elevation.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static class Nan
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static int s_index = 7700;

    public static int Run()
    {
        var db = ProfileDatabase.LoadEmbedded().All.ToDictionary(p => p.Id, StringComparer.Ordinal);
        int fail = 0;
        Console.WriteLine("nan: a NaN axis writes what an unwritten axis writes, through SubmitState");
        foreach (var (id, armed) in new[]
        {
            ("xbox-360-wired", false), ("dualshock-4-v2", false), ("amazon-luna-usb", false), ("amazon-luna-ble", false),
            ("dualshock-3-full", false), ("dualsense-bt", true), ("steam-controller-2", false), ("switch-pro", false),
            ("switch2-pro-controller-composite", false), ("logitech-g29", false),
        })
        {
            if (!db.TryGetValue(id, out var p)) { Console.WriteLine($"  [FAIL] the catalog has no {id}"); fail++; continue; }
            var hp = new HMProfile(p);
            var keys = hp.Sticks.SelectMany(s => new[] { s.XAxis, s.YAxis }).Concat(hp.Triggers.Select(t => t.Axis))
                .Concat(new[] { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rx, HMAxis.Ry, HMAxis.Rz })
                .Where(a => a != HMAxis.None).Distinct().ToList();
            int bad = 0;
            string first = "";
            // Each axis alone, then all of them.
            foreach (var set in keys.Select(k => new[] { k }).Append(keys.ToArray()))
            {
                var withNan = Frame(p, armed, set.ToDictionary(k => k, _ => float.NaN));
                var without = Frame(p, armed, new Dictionary<HMAxis, float>());
                if (!withNan.AsSpan().SequenceEqual(without))
                {
                    if (bad++ == 0)
                    {
                        int at = 0;
                        while (withNan[at] == without[at]) at++;
                        first = $"NaN on {string.Join(", ", set)}: byte {at} is {withNan[at]:X2}, unwritten {without[at]:X2}";
                    }
                }
            }
            if (bad != 0) fail++;
            Console.WriteLine($"  [{(bad == 0 ? "PASS" : "FAIL")}] {id}: {keys.Count + 1} sets of NaN axes write the unwritten frame{(bad == 0 ? "" : $", {bad} do not, first {first}")}");
        }

        // The fallback pairs. A slot reads a first key and, when that key
        // is absent, another: a trigger its canonical axis, then its
        // field's own axis, and the right stick of a profile that declares
        // none Rx and Ry, then Z and Rz. A NaN on the first key is a value,
        // the slot's rest, and never falls through to the second. Each case
        // writes 0.9 on the second key, so a NaN that fell through would
        // read 0.9 where the rest is written, and checks that the case can
        // tell the two apart.
        Console.WriteLine("nan: a NaN on a slot's first key takes the slot's rest, never its fallback key");
        foreach (var (id, first, restValue, fallback) in new[]
        {
            ("xbox-360-wired", HMAxis.Z, 0f, HMAxis.Vx), ("xbox-360-wired", HMAxis.Rz, 0f, HMAxis.Vy),
            ("steam-controller-2", HMAxis.Rx, 0.5f, HMAxis.Z), ("steam-controller-2", HMAxis.Ry, 0.5f, HMAxis.Rz),
        })
        {
            if (!db.TryGetValue(id, out var p)) { Console.WriteLine($"  [FAIL] the catalog has no {id}"); fail++; continue; }
            var withNan = Frame(p, false, new Dictionary<HMAxis, float> { [first] = float.NaN, [fallback] = 0.9f });
            var atRest = Frame(p, false, new Dictionary<HMAxis, float> { [first] = restValue, [fallback] = 0.9f });
            var fellThrough = Frame(p, false, new Dictionary<HMAxis, float> { [fallback] = 0.9f });
            bool discriminates = !atRest.AsSpan().SequenceEqual(fellThrough);
            bool ok = discriminates && withNan.AsSpan().SequenceEqual(atRest);
            if (!ok) fail++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {id}: {first} NaN beside {fallback} 0.9 writes the frame of {first} {restValue}"
                + (discriminates ? "" : ", but that frame equals the fall-through's, so the case shows nothing")
                + (ok || !discriminates ? "" : withNan.AsSpan().SequenceEqual(fellThrough) ? ", and it fell through" : ", and it writes neither"));
        }

        // The codec and the packers take the six slots after SubmitState
        // has resolved them, so a NaN reaches them only from a caller of
        // their own. Each slot's NaN must give its rest: 0.5 for a stick,
        // 0 for a trigger.
        Console.WriteLine("nan: a NaN slot writes the slot's rest, through the codec and the packers");
        float[] rest = { 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f };
        foreach (var id in new[] { "steam-controller-2", "dualshock-3-full", "dualsense-bt", "switch2-pro-controller" })
        {
            var spec = db[id].ExtendedReport!;
            int bad = 0;
            for (int k = 0; k < 6; k++)
            {
                var a = (float[])rest.Clone();
                a[k] = float.NaN;
                var x = new byte[spec.Size];
                var y = new byte[spec.Size];
                var s = new HMGamepadState();
                VendorBlobCodec.EncodeInput(spec, in s, a[0], a[1], a[2], a[3], a[4], a[5], x, new VendorBlobCodec.EncoderState());
                VendorBlobCodec.EncodeInput(spec, in s, rest[0], rest[1], rest[2], rest[3], rest[4], rest[5], y, new VendorBlobCodec.EncoderState());
                if (!x.AsSpan().SequenceEqual(y)) bad++;
            }
            if (bad != 0) fail++;
            Console.WriteLine($"  [{(bad == 0 ? "PASS" : "FAIL")}] codec {id}: {6 - bad} of 6 NaN slots write their rest");
        }
        {
            int bad = 0;
            for (int k = 0; k < 4; k++)
            {
                double[] a = { 0.5, 0.5, 0.5, 0.5 };
                a[k] = double.NaN;
                var x = new byte[SwitchProPacker.BodySize];
                var y = new byte[SwitchProPacker.BodySize];
                var s = new HMGamepadState();
                SwitchProPacker.BuildBody(in s, a[0], a[1], a[2], a[3], x);
                SwitchProPacker.BuildBody(in s, 0.5, 0.5, 0.5, 0.5, y);
                if (!x.AsSpan().SequenceEqual(y)) bad++;
            }
            if (bad != 0) fail++;
            Console.WriteLine($"  [{(bad == 0 ? "PASS" : "FAIL")}] switch-pro packer: {4 - bad} of 4 NaN sticks write the center");
        }
        {
            int bad = 0;
            for (int k = 0; k < 6; k++)
            {
                double[] a = { 0.5, 0.5, 0.5, 0.5, 0, 0 };
                a[k] = double.NaN;
                // v2.0.0: the composite's state block through the codec,
                // which replaced Switch2ProPacker.
                var x = new byte[Switch2Body.Size];
                var y = new byte[Switch2Body.Size];
                var s = new HMGamepadState();
                Switch2Body.Build(in s, a[0], a[1], a[2], a[3], a[4], a[5], x);
                Switch2Body.Build(in s, 0.5, 0.5, 0.5, 0.5, 0, 0, y);
                if (!x.AsSpan().SequenceEqual(y)) bad++;
            }
            if (bad != 0) fail++;
            Console.WriteLine($"  [{(bad == 0 ? "PASS" : "FAIL")}] switch2 state block: {6 - bad} of 6 NaN slots write their rest");
        }
        return fail;
    }

    /// <summary>One SubmitState of a state carrying <paramref name="axes"/>
    /// on a fresh offline controller, and the buffer it wrote.</summary>
    static byte[] Frame(ControllerProfile p, bool armed, Dictionary<HMAxis, float> axes)
    {
        if (p.ExtendedReport != null) p.ExtendedReport.IdleFrameIntervalMs = 0;
        p.Stream = null;
        int index = s_index++;
        IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
        try
        {
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
            Cache("s_inputViews")[index] = view;
            Cache("s_outputViews")[index] = IntPtr.Zero;
            var ctor = typeof(HMController).GetConstructor(Instance, new[]
            {
                typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string), typeof(UsbipBackendHandle), typeof(string),
            }) ?? throw new MissingMethodException("HMController's private constructor changed.");
            var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
            var c = (HMController)ctor.Invoke(new object?[] { context, index, new HMProfile(p), null, null, null });
            typeof(HMController).GetField("_deliverEvery", Instance)?.SetValue(c, false);
            if (armed) typeof(HMController).GetField("_extendedModeArmed", Instance)!.SetValue(c, true);
            c.SubmitState(new HMGamepadState { Axes = axes });
            var bytes = new byte[SharedMemoryIO.SHARED_INPUT_SIZE];
            Marshal.Copy(view, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Cache("s_inputViews").Remove(index);
            Cache("s_outputViews").Remove(index);
            Marshal.FreeHGlobal(view);
        }
    }

    static Dictionary<int, IntPtr> Cache(string field) =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, Static)!.GetValue(null)!;
}
