// The protocol part of a UsbipEmulatedDevice a case builds without its
// constructor: v2.0.0 compiles a profile's protocol into an engine, as the
// constructor does (UsbipEmulatedDevice.cs, the constructor), and the cases
// that build devices field by field set it the same way. A protocol that
// sends on a clock of its own is not built here: cases 4 and 6 drive only
// the control and input paths.
using System;
using System.Reflection;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static class OracleProtocol
{
    /// <summary>Sets the engine of <paramref name="profile"/>'s protocol on
    /// <paramref name="dev"/>, and the protocol part's field initializers.</summary>
    public static void Set(UsbipEmulatedDevice dev, ControllerProfile profile, UsbDescriptorSet descriptors, int index)
    {
        var t = typeof(UsbipEmulatedDevice);
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
        void Field(string name, object? value) =>
            (t.GetField(name, Instance) ?? throw new MissingFieldException(t.Name, name)).SetValue(dev, value);
        Field("_protocolReads", Activator.CreateInstance(t.GetField("_protocolReads", Instance)!.FieldType));
        Field("_spareReads", Activator.CreateInstance(t.GetField("_spareReads", Instance)!.FieldType));
        var compiled = CompiledProtocol.Compile(profile, descriptors);
        if (compiled == null) return;
        if (compiled.BuildsReports || compiled.InputEndpoint != 0)
            throw new InvalidOperationException($"{profile.Id} declares a protocol that sends on its own clock, which this case does not build.");
        Field("_engine", new ProtocolEngine(compiled, descriptors.SerialString, index));
    }
}
