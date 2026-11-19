// Parity for a profile that presents another profile's form on USB/IP
// (HMProfile.FormOn): a Bluetooth identity whose usbForm names its USB
// sibling. On USB/IP the device is the form's, so its descriptor, caps and
// attributes are compared with what the form presents on UMDF2. The
// identity keeps its own strings, so its product string is compared with
// the identity's own on UMDF2. The form may carry another PID, never
// another VID.

using System.Collections.Generic;
using System.Linq;
using HIDMaestro;

internal static partial class Program
{
    /// <summary>Whether <paramref name="p"/>'s USB/IP device (<paramref name="s"/>)
    /// presents what its form presents on UMDF2 and the strings its own
    /// UMDF2 device (<paramref name="u"/>) carries. Adds each difference to
    /// <paramref name="problems"/>. False when a difference was found or
    /// either backend's device did not start.</summary>
    static bool FormMatches(HMContext ctx, HMProfile p, HMProfile form, Snapshot? u, Snapshot? s, string key, List<string> problems)
    {
        int before = problems.Count;
        if (form.VendorId != p.VendorId)
            problems.Add($"the USB/IP form {form.Id} has VID {form.VendorId:X4}, the identity {p.VendorId:X4}");
        bool ownOk = WindowsParser.Parse(p.Inner.GetDescriptorBytes()!).Ok;
        var formVerdict = WindowsParser.Parse(form.Inner.GetDescriptorBytes()!);
        if (formVerdict.Ok != ownOk)
            problems.Add($"Windows' parser takes {p.Id}'s descriptor {(ownOk ? "" : "not ")}and {form.Id}'s {(formVerdict.Ok ? "" : "not ")}({formVerdict})");
        if (u == null || s == null || u.Failure != null || s.Failure != null) return false;
        var f = Take(ctx, form, HMBackend.Umdf2, key + ":form");
        if (f.Failure != null || f.Collections.Count == 0)
        {
            problems.Add($"{form.Id} on UMDF2, the reference for the USB/IP form: {f.Failure ?? "no HID collection"}");
            return false;
        }
        var fk = f.Collections.Select(c => c.ParsedKey).ToList();
        var sk = s.Collections.Select(c => c.ParsedKey).ToList();
        if (!fk.SequenceEqual(sk))
            problems.Add($"parsed fields differ from {form.Id} on UMDF2: {fk.Count} collection(s) there, USB/IP {sk.Count}");
        else if (!u.Collections.Select(c => c.Product).Distinct().SequenceEqual(s.Collections.Select(c => c.Product).Distinct()))
            problems.Add($"product string {u.Collections[0].Product} on UMDF2 against {s.Collections[0].Product} on USB/IP");
        else if (f.Output != s.Output)
            problems.Add($"output packet {form.Id} on UMDF2 '{f.Output}' against USB/IP '{s.Output}'");
        return problems.Count == before;
    }
}
