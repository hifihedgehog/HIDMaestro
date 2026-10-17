// profile_model_check: profiles as data, the SDK side of the data-driven
// design (docs/design/data-driven-devices.md, mechanisms 4 and 5, decisions
// (c) and (d), "Profiles made outside the catalog"), offline.
//
//   strict     the strict loader: the catalog loads with no issue, each kind
//              of defect a user's file can carry is refused at its JSON path
//              and listed in HMContext.ProfileLoadIssues, a refused profile
//              is not loaded, earlier tags' catalog files load but for the
//              removed spellings, and a create or a build meets one
//              exception type for a bad profile (Strict.cs)
//   values     value sources and patches: the keyedHexFile parser against
//              9bd6279's ParseStamp, every encoding and source, the file
//              cache, and each refusal at load (Values.cs)
//   resolve    device model resolution: named models and identity
//              defaults from the catalog and a loaded directory, the
//              problems a name can meet, and no catalog profile resolved
//              through an identity default (Resolve.cs)
//   answers    answer tables: per-entry matching, pages, a parameter at
//              its own offset, the Sony models, each refusal at load, the
//              blob the driver reads, identity defaults (Answers.cs)
//   contract   the SDK's half of the version contract: the identity tag,
//              protocol 4, and the configuration and identity entry each
//              create writes anew, in a hive the check owns (Contract.cs)
//   deploy     the deploy floor at 2.0.0 through the create's gate on a
//              machine the check gives, under alternatives A and B, and B's
//              predicate against a golden list (Deploy.cs)
//   builder    FromProfile's whole copy, a copy refused for a descriptor
//              its model does not fit, ClearDeviceModel, DriverPid,
//              FeatureAnswers and BuildsItsOwnReports (Builder.cs)
//   check      every mode above, the battery's run (S92), the default
//
// Creates no device, opens no section and needs no elevation. Exit 0 PASS,
// 1 FAIL.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;

internal static class Program
{
    static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        string mode = args.Length > 0 ? args[0] : "check";
        var modes = new (string Name, Func<int> Run)[]
        {
            ("strict", Strict.Run),
            ("values", Values.Run),
            ("resolve", Resolve.Run),
            ("answers", Answers.Run),
            ("contract", Contract.Run),
            ("deploy", Deploy.Run),
            ("builder", Builder.Run),
        };
        int fail = 0;
        if (mode == "check")
            foreach (var m in modes) fail += m.Run();
        else if (modes.FirstOrDefault(m => m.Name == mode) is { Run: not null } one)
            fail += one.Run();
        else
        {
            Console.WriteLine($"usage: ProfileModelCheck check | {string.Join(" | ", modes.Select(m => m.Name))}");
            return 1;
        }
        Console.WriteLine($"profile_model_check {mode}: {(fail == 0 ? "PASS" : $"FAIL, {fail}")}");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>The probe's source directory.</summary>
    internal static string ProbeDir() =>
        typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "ProbeDir").Value!;

    /// <summary>The repository's root, three levels above the probe.</summary>
    internal static string RepoRoot() => Path.GetFullPath(Path.Combine(ProbeDir(), "..", "..", ".."));
}

/// <summary>Prints each check and counts the failures of one mode. A check
/// whose body throws fails with the exception, so one missing member or
/// surprise never hides the checks after it.</summary>
internal sealed class Checks
{
    private readonly string _mode;
    public int Failures { get; private set; }

    public Checks(string mode) => _mode = mode;

    public void Check(string name, bool ok, string detail = "")
    {
        if (!ok) Failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {_mode}: {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    public void Run(string name, Func<(bool Ok, string Detail)> body)
    {
        try
        {
            var (ok, detail) = body();
            Check(name, ok, detail);
        }
        catch (Exception e)
        {
            Check(name, false, $"threw {e.GetType().Name}: {e.Message}");
        }
    }

    public void Run(string name, Func<bool> body) => Run(name, () => (body(), ""));
}

/// <summary>The SDK members this probe tests, read by reflection where the
/// member is new in v2.0.0, so the probe also runs against an SDK that lacks
/// them and reports what that SDK does instead of failing to load.</summary>
internal static class Api
{
    /// <summary>One load issue, read from an HMProfileLoadIssue.</summary>
    internal sealed record Issue(string Kind, string File, string? Id, string Path, string Reason)
    {
        public override string ToString() => $"{Kind} {System.IO.Path.GetFileName(File)} {Path} {Reason}";
    }

    public static object? Prop(object o, string name)
    {
        var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMemberException(o.GetType().Name, name);
        return p.GetValue(o);
    }

    /// <summary>The issues a member of type IReadOnlyList&lt;HMProfileLoadIssue&gt;
    /// holds.</summary>
    public static List<Issue> Issues(object owner, string member)
    {
        var list = new List<Issue>();
        foreach (object i in (System.Collections.IEnumerable)Prop(owner, member)!)
            list.Add(new Issue(Prop(i, "Kind")!.ToString()!, (string)Prop(i, "File")!, (string?)Prop(i, "ProfileId"),
                               (string)Prop(i, "JsonPath")!, (string)Prop(i, "Reason")!));
        return list;
    }

    /// <summary>The JSON path and reason of an HMProfileException, or null
    /// for any other exception.</summary>
    public static (string Path, string Reason, string ProfileId)? ProfileError(Exception e) =>
        e.GetType().FullName == "HIDMaestro.HMProfileException"
            ? ((string)Prop(e, "JsonPath")!, (string)Prop(e, "Reason")!, (string)Prop(e, "ProfileId")!)
            : null;
}
