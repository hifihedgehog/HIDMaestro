// Case 1: SubmitState on every catalog profile the arithmetic change of the
// design note touches (the 184 of case1-profiles.txt, the appendix of the
// DD2 report): the codec, the descriptor path, the XUSB slice, the Switch
// Pro packer and the controller-port encoder, as HMController composes them.
// Then thirteen profiles a program makes rather than loads: eight extracted
// the way HMDeviceExtractor extracts a physical device (the Microsoft pads
// among them, 045E:02FD and 045E:0B12 included: Extract names a profile
// after its product string and sets no driver mode, so today the XUSB
// companion goes to one whose product string says "Xbox" and to no other),
// five builder copies of catalog profiles (see Synthetic below).
//
// The controller is the SDK's own, built offline: its constructor runs
// against an input section this case owns (SharedMemoryIO's per-index cache
// holds the case's buffer before the constructor asks for it), and no
// output section, so no kernel object is created, nothing is elevated and
// no driver is involved. Each submit's output is what a driver or the
// companion reads from the section: DataSize and Data, the extended lane's
// size and bytes, and the 14-byte GIP slice.
//
// Four things decide timing, not bytes, and are fixed so a run repeats:
// the idle-frame pump of a Valve profile, which Create keeps from starting
// without changing the catalog's profile (WithoutIdlePump),
// the keyboard and mouse delivery pacing (HMController._deliverEvery, which
// waits 1 ms between frames), the codec's own clock, which stamps a report
// whose state carries no sensor timestamp (each such state gets the stamp a
// consumer's clock would give it), and for a profile that arms its extended
// report on a host's feature read, the arming: HMController._extendedModeArmed
// is set as the output reader sets it when the read arrives. A field a later
// lane renames moves this file with it. On a tree whose codec reads its
// clock through a field a test can set (VendorBlobCodec.EncoderState's
// TimestampClock), each controller's encoder reads a scripted clock
// (ScriptedClock, Explain.cs) instead, so the bytes it stamps from the
// clock are an oracle too.
//
// A submit that throws records the exception's type in place of a frame.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal sealed class Case1Submit : IOracleCase
{
    public string Name => "case1-submit";

    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    /// <summary>Transport indexes the case's controllers use, far above any
    /// a program assigns. Only SharedMemoryIO's in-process dictionaries see
    /// them: no object of these names is created.</summary>
    const int FirstIndex = 7000;

    public void Record(GoldenCase golden, Options options)
    {
        var db = ProfileDatabase.LoadEmbedded();
        var byId = db.All.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var ids = ReadProfileList();
        var missing = ids.Where(id => !byId.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"case1-profiles.txt names profiles the catalog lacks: {string.Join(", ", missing)}");

        var subjects = ids.Select(id => (Stream: id, Profile: byId[id])).ToList();
        var made = Synthetic(byId).ToList();
        subjects.AddRange(made);

        Predicates(golden, db, made);

        IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
        try
        {
            var section = new byte[SharedMemoryIO.SHARED_INPUT_SIZE];
            var frame = new byte[2 + SharedMemoryIO.DATA_CAPACITY + 2 + SharedMemoryIO.EXTENDED_DATA_CAPACITY + SharedMemoryIO.GIP_DATA_LENGTH];
            var scratch = new Dictionary<HMAxis, float>();
            int n = 0;
            foreach (var (id, p) in subjects)
            {
                int index = FirstIndex + n++;
                var axes = AxesOf(p);
                foreach (var (suffix, armed) in PathsOf(p))
                {
                    Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
                    var c = Create(p, index, view);
                    if (armed) typeof(HMController).GetField("_extendedModeArmed", Instance)!.SetValue(c, true);
                    golden.Stream($"submit/{id}{suffix}");
                    golden.Explainer = new FrameExplainer(c);
                    // The inputs this stream's states move, first, so a
                    // replay whose profile reads other axes differs at
                    // output 0 rather than somewhere in the frames.
                    golden.Out("axes", AxesBytes(axes));
                    // Seeded by the catalog id, also for a profile made from one
                    // ("extracted/xbox-one-s" is seeded as "xbox-one-s").
                    var rng = new Rng(options.Seed, Fnv(id[(id.LastIndexOf('/') + 1)..]));
                    uint stamp = 0;
                    void Submit(in HMGamepadState state)
                    {
                        // A zero sensor timestamp makes the codec stamp the
                        // report from the controller's Stopwatch
                        // (VendorBlobCodec.EncoderState.SensorTicks), which
                        // is time, not an oracle. Such a state carries the
                        // stamp a consumer's clock would give it, 4 ms of
                        // 1/3 microsecond ticks per state, never 0. DD-1's
                        // own timestamp test covers the clock (plan, DD-1).
                        var s = state;
                        stamp += 12000;
                        if (stamp == 0) stamp = 12000;
                        if (s.SensorTimestamp == 0) s.SensorTimestamp = stamp;
                        golden.SetInput(in s);
                        for (int k = 0; k < States.Repeats; k++)
                        {
                            try { c.SubmitState(in s); }
                            catch (Exception e)
                            {
                                golden.Out("throw", System.Text.Encoding.ASCII.GetBytes(e.GetType().FullName ?? "?"));
                                continue;
                            }
                            Marshal.Copy(view, section, 0, section.Length);
                            golden.Out("frame", Frame(section, frame));
                        }
                    }
                    foreach (var s in States.Scripted(axes, scratch)) Submit(in s);
                    for (int i = 0; i < States.RandomCount; i++)
                    {
                        var s = States.Random(rng, axes, scratch);
                        Submit(in s);
                    }
                    Forget(index);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(view);
        }
    }

    /// <summary>The profile predicates the create path and the controller
    /// read, for every catalog profile, read by name so a property a later
    /// lane removes shows as absent rather than breaking the build: the
    /// XUSB presentation (ControllerProfile.cs:383-423), the backend and the
    /// DualShock 3 form. One output per profile, "name=value" pairs. Then
    /// the same for the profiles a program makes (Synthetic), each after its
    /// stream name: an extracted profile names no driver mode, so what the
    /// SDK gives it can differ from the catalog profile it copies.</summary>
    static void Predicates(GoldenCase golden, ProfileDatabase db, List<(string Stream, ControllerProfile Profile)> made)
    {
        string[] names =
        {
            "VendorId", "ProductId", "DriverPid", "Connection", "DriverMode", "Backend", "UsesXinputhid", "UsesXusb22",
            "UsesUpperFilter", "UpperFilterName", "IsXboxBranded", "RequiresXusbCompanion", "CompanionOnly",
            "RequiresUsbipBackend", "SixaxisCompatible", "IsKeyboardOrMouse", "HasDescriptor", "Type", "Name", "ProductString",
        };
        void Each(IEnumerable<ControllerProfile> profiles)
        {
            foreach (var p in profiles) Line(p);
        }
        golden.Stream("predicates/catalog");
        Each(db.All.OrderBy(p => p.Id, StringComparer.Ordinal));
        // Profiles a program builds, where the catalog leaves a condition
        // undecided: "xbox" in the id alone, the name alone, the product
        // string alone, in none, in another case, each at VID 045E and at
        // another vendor, and with an upper filter (ControllerProfile.cs:404-423).
        golden.Stream("predicates/code-built");
        var wired = db.All.First(p => p.Id == "xbox-360-wired");
        var built = new List<ControllerProfile>();
        foreach (ushort vid in new ushort[] { 0x045E, 0x0E6F })
            foreach (var (id, name, product) in new[]
            {
                ("xbox-pad", "Pad", "Pad"), ("pad", "Xbox Pad", "Pad"), ("pad", "Pad", "Xbox Pad"),
                ("pad", "Pad", "Pad"), ("pad", "Pad", "XBOX 360 For Windows"), ("pad", "XBOX", "Pad"),
            })
                foreach (string? mode in new[] { null, "xinputhid", "xusb22" })
                {
                    var b = new HMProfileBuilder().Id(id).Name(name).Vendor("Vendor").Vid(vid).Pid(0x028E)
                        .ProductString(product).Type("gamepad").Connection("usb").Descriptor(wired.GetDescriptorBytes()!);
                    if (mode != null) b.DriverMode(mode);
                    built.Add(b.Build().Inner);
                }
        Each(built);
        golden.Stream("predicates/made");
        foreach (var (stream, profile) in made)
        {
            golden.Out("stream", System.Text.Encoding.UTF8.GetBytes(stream));
            Line(profile);
        }

        void Line(ControllerProfile p)
        {
            var sb = new System.Text.StringBuilder(p.Id);
            foreach (var name in names)
            {
                var prop = typeof(ControllerProfile).GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                object? v = prop?.GetValue(p);
                string text = prop == null ? "absent" : v switch
                {
                    null => "null",
                    ushort u => $"0x{u:X4}",
                    bool b => b ? "1" : "0",
                    _ => v.ToString() ?? "",
                };
                sb.Append(' ').Append(name).Append('=').Append(text);
            }
            golden.Out("profile", System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        }
    }

    static readonly MethodInfo s_slugify =
        typeof(HMDeviceExtractor).GetMethod("Slugify", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("HMDeviceExtractor.Slugify");

    /// <summary>Profiles a program makes rather than loads, as the SDK makes
    /// them at 9bd6279 (design review FB9 and decision (d)):
    /// "extracted/&lt;id&gt;" is what HMDeviceExtractor.Extract builds from a
    /// physical device of that identity (HMDeviceExtractor.cs:110-153), with
    /// the catalog descriptor in place of the reconstructed one, and
    /// "builder-copy/&lt;id&gt;" is HMProfileBuilder.FromProfile of the catalog
    /// profile. Today both keep what the SDK keys on the vendor and product
    /// ID and lose what it reads from the profile's other data. Their random
    /// states come from the catalog profile's id, so each stream sees the
    /// same inputs as the catalog stream beside it.</summary>
    static IEnumerable<(string Stream, ControllerProfile Profile)> Synthetic(Dictionary<string, ControllerProfile> byId)
    {
        foreach (var id in new[] { "dualsense-bt", "dualshock-4-v2-bt", "switch-pro", "xbox-360-wired", "xbox-one-s", "xbox-one-s-bt", "xbox-series-xs", "xbox-elite-v2" })
        {
            // Extract's own calls (HMDeviceExtractor.cs:110-153): the id is
            // the slug of the product string, the name the product string,
            // the vendor the manufacturer string, the notes stamped with a
            // fixed time in place of the time of extraction.
            var c = byId[id];
            string? product = c.ProductString;
            string? manufacturer = string.IsNullOrEmpty(c.ManufacturerString) ? null : c.ManufacturerString;
            string name = product ?? $"Device {c.VendorId:X4}:{c.ProductId:X4}";
            var hp = new HMProfileBuilder()
                .Id((string)s_slugify.Invoke(null, new object[] { product ?? $"vid{c.VendorId:x4}pid{c.ProductId:x4}" })!)
                .Name(name).Vendor(manufacturer ?? "Unknown").Vid(c.VendorId).Pid(c.ProductId)
                .ProductString(product ?? name).ManufacturerString(manufacturer ?? "")
                .DeviceDescription(product ?? name).Type(c.Type).Connection(c.Connection)
                .Descriptor(c.GetDescriptorBytes()!)
                .Notes("Extracted by HMDeviceExtractor on 2026-10-15 00:00:00 UTC. " +
                       "Descriptor reconstructed from Windows preparsed data (HIDAPI algorithm); " +
                       "logically equivalent to the physical device's HID report descriptor but " +
                       "not guaranteed byte-identical.")
                .Build();
            yield return ($"extracted/{id}", hp.Inner);
        }
        foreach (var id in new[] { "dualsense-bt", "steam-controller-2", "xbox-series-xs", "switch-pro", "dualshock-3-full" })
            yield return ($"builder-copy/{id}", new HMProfileBuilder().FromProfile(new HMProfile(byId[id])).Build().Inner);
    }

    /// <summary>The streams of a profile: the submit as its controller takes
    /// it from creation, and for a profile that arms its extended report on
    /// a host's request, the submit once armed.</summary>
    static IEnumerable<(string Suffix, bool Armed)> PathsOf(ControllerProfile p)
    {
        yield return ("", false);
        if (p.ExtendedReport?.ArmOn is { Count: > 0 } && p.ExtendedReport.AlwaysArmed != true) yield return ("/armed", true);
    }

    /// <summary>Every analog input the profile reads: its descriptor's axis
    /// fields, its layout's sticks and triggers, and the six axes the
    /// controller resolves the simple slots from when a profile declares no
    /// sticks. Sorted, so the order is the same in every run.</summary>
    static IReadOnlyList<HMAxis> AxesOf(ControllerProfile p)
    {
        var set = new SortedSet<HMAxis> { HMAxis.X, HMAxis.Y, HMAxis.Z, HMAxis.Rx, HMAxis.Ry, HMAxis.Rz };
        if (p.HasDescriptor)
            foreach (var a in p.GetOrBuildReportBuilder().AxisFields.Keys) set.Add(a);
        var hp = new HMProfile(p);
        foreach (var s in hp.Sticks) { set.Add(s.XAxis); if (s.YAxis != HMAxis.None) set.Add(s.YAxis); }
        foreach (var t in hp.Triggers) set.Add(t.Axis);
        set.Remove(HMAxis.None);
        return set.ToList();
    }

    static byte[] AxesBytes(IReadOnlyList<HMAxis> axes)
    {
        var b = new byte[axes.Count * 2];
        for (int i = 0; i < axes.Count; i++) { b[2 * i] = (byte)(ushort)axes[i]; b[2 * i + 1] = (byte)((ushort)axes[i] >> 8); }
        return b;
    }

    /// <summary>The output of one submit: DataSize and Data, the extended
    /// lane's size and bytes, the GIP slice.</summary>
    internal static ReadOnlySpan<byte> Frame(byte[] s, byte[] frame)
    {
        int dataLen = BitConverter.ToInt32(s, 4);
        int extLen = BitConverter.ToInt32(s, SharedMemoryIO.EXTENDED_SIZE_OFFSET);
        if ((uint)dataLen > SharedMemoryIO.DATA_CAPACITY || (uint)extLen > SharedMemoryIO.EXTENDED_DATA_CAPACITY)
            throw new InvalidDataException($"A submit wrote DataSize {dataLen} and ExtendedReportSize {extLen}.");
        int o = 0;
        frame[o++] = (byte)dataLen; frame[o++] = (byte)(dataLen >> 8);
        Buffer.BlockCopy(s, SharedMemoryIO.DATA_OFFSET, frame, o, dataLen); o += dataLen;
        frame[o++] = (byte)extLen; frame[o++] = (byte)(extLen >> 8);
        Buffer.BlockCopy(s, SharedMemoryIO.EXTENDED_DATA_OFFSET, frame, o, extLen); o += extLen;
        Buffer.BlockCopy(s, SharedMemoryIO.GIP_DATA_OFFSET, frame, o, SharedMemoryIO.GIP_DATA_LENGTH); o += SharedMemoryIO.GIP_DATA_LENGTH;
        return frame.AsSpan(0, o);
    }

    /// <summary>The SDK's own controller on the case's section. Through the
    /// private constructor every create path ends in, with an uninitialized
    /// context, which the constructor only stores.</summary>
    internal static HMController Create(ControllerProfile p, int index, IntPtr view, IntPtr outputRing = default, UsbipBackendHandle? usbip = null)
    {
        Cache("s_inputViews")[index] = view;
        // No output section, so no output thread runs, unless a case hands
        // in a ring of its own to feed the output thread with.
        Cache("s_outputViews")[index] = outputRing;
        if (outputRing != IntPtr.Zero) SharedMemoryIO.MarkOutputStart(index);
        var ctor = typeof(HMController).GetConstructor(Instance, new[]
        {
            typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string), typeof(UsbipBackendHandle), typeof(string),
        }) ?? throw new MissingMethodException("HMController's private constructor changed: update case 1.");
        var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
        var c = (HMController)ctor.Invoke(new object?[] { context, index, new HMProfile(WithoutIdlePump(p)), null, usbip, null });
        // Keyboard and mouse pacing waits a millisecond between frames, a
        // timing rule the frames' bytes do not depend on.
        typeof(HMController).GetField("_deliverEvery", Instance)!.SetValue(c, false);
        ScriptedClock.Install(c);
        return c;
    }

    static readonly MethodInfo s_clone = typeof(object).GetMethod("MemberwiseClone", Instance)!;

    /// <summary>The profile the controller is built from: the catalog's,
    /// or for a profile with an idle-frame interval a copy of it whose
    /// extended report has none, so the constructor starts no idle pump and
    /// the case calls the repeat itself. The catalog's profile is never
    /// changed. Its report is compiled first, from the catalog's own values,
    /// and the copy shares that compiled program, so the encoder carries
    /// the real interval wherever it reads one (an accumulating timestamp's
    /// nominal step) however late a tree compiles.</summary>
    internal static ControllerProfile WithoutIdlePump(ControllerProfile p)
    {
        if (p.ExtendedReport is not { IdleFrameIntervalMs: > 0 } spec) return p;
        VendorBlobProgram.Get(spec);
        var report = (ExtendedReportSpec)s_clone.Invoke(spec, null)!;
        report.IdleFrameIntervalMs = 0;
        var copy = (ControllerProfile)s_clone.Invoke(p, null)!;
        copy.ExtendedReport = report;
        return copy;
    }

    internal static void Forget(int index)
    {
        Cache("s_inputViews").Remove(index);
        Cache("s_outputViews").Remove(index);
    }

    static Dictionary<int, IntPtr> Cache(string field) =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, Static)!.GetValue(null)!;

    static List<string> ReadProfileList()
    {
        string path = Path.Combine(ProbeDir(), "case1-profiles.txt");
        return File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    static string ProbeDir() =>
        typeof(Case1Submit).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "ProbeDir").Value!;

    /// <summary>FNV-1a over the id, a stable number per profile for its
    /// random stream. string.GetHashCode changes per process.</summary>
    public static ulong Fnv(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char ch in s) { h ^= ch; h *= 1099511628211UL; }
        return h;
    }
}
