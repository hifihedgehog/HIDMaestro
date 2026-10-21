// HIDMaestroUninstall removes HIDMaestro from this computer: every virtual
// controller and its records, the driver packages, the signing certificate,
// HIDMaestro's registry keys and files, and the shared Windows keys it
// changed, put back. It calls HMUninstall, the SDK's own removal, so a
// program that embeds HIDMaestro gets the same result from its uninstaller.
//
//   HIDMaestroUninstall [--preview] [--yes] [--include-usbip] [--json]
//
// With no option it lists what it would remove, asks, then removes.
//   --preview        list only, change nothing
//   --yes            remove without asking
//   --include-usbip  also remove usbip-win2 when no record shows HIDMaestro
//                    installed it (never its root-hub filter)
//   --json           write the report as JSON on stdout, with --preview or
//                    --yes. docs/UNINSTALL.md gives the schema
//
// Exit codes: 0 removed, or a preview with nothing blocking it. 3 removed,
// with items Windows deletes when it next starts. 2 blocked: other programs
// that use HIDMaestro are running, and nothing was changed. 1 failed, or did
// not run (a bad option, the question answered no, no administrator rights).
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HIDMaestro;

namespace HIDMaestro.Uninstall;

internal static class Program
{
    private const int ExitRemoved = 0, ExitFailed = 1, ExitBlocked = 2, ExitRestartPending = 3;

    private static int Main(string[] args)
    {
        bool preview = false, yes = false, usbip = false, json = false;
        foreach (string a in args)
        {
            switch (a.ToLowerInvariant())
            {
                case "--preview": preview = true; break;
                case "--yes": yes = true; break;
                case "--include-usbip": usbip = true; break;
                case "--json": json = true; break;
                case "--help" or "-h" or "/?":
                    Console.WriteLine(Usage);
                    return ExitRemoved;
                default:
                    Console.Error.WriteLine($"Unknown option: {a}");
                    Console.Error.WriteLine(Usage);
                    return ExitFailed;
            }
        }
        if (json && !preview && !yes)
        {
            Console.Error.WriteLine("--json needs --preview or --yes, since it asks no question.");
            return ExitFailed;
        }

        var options = new HMUninstallOptions { IncludeUsbip = usbip };
        try
        {
            if (preview || !yes)
            {
                var found = HMUninstall.Preview(options);
                if (preview)
                {
                    Write(found, json);
                    return ExitCode(found);
                }
                Write(found, json: false);
                if (found.Status == HMUninstallStatus.Blocked) return ExitBlocked;
                if (!found.Entries.Any(e => e.Outcome == HMUninstallOutcome.WouldRemove))
                {
                    Console.WriteLine("Nothing of HIDMaestro is left to remove.");
                    return ExitRemoved;
                }
                Console.Write("Remove HIDMaestro from this computer? [y/N] ");
                string? answer = Console.ReadLine()?.Trim();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Nothing was changed.");
                    return ExitFailed;
                }
                Console.WriteLine();
            }
            var report = HMUninstall.Run(options);
            Write(report, json);
            return ExitCode(report);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Removing HIDMaestro needs administrator rights. Run this from an elevated prompt.");
            return ExitFailed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"The removal stopped: {ex.GetType().Name}: {ex.Message}");
            return ExitFailed;
        }
    }

    private const string Usage =
        "Usage: HIDMaestroUninstall [--preview] [--yes] [--include-usbip] [--json]\n" +
        "  --preview        list what would be removed, change nothing\n" +
        "  --yes            remove without asking\n" +
        "  --include-usbip  also remove usbip-win2 when no record shows HIDMaestro installed it\n" +
        "  --json           write the report as JSON (needs --preview or --yes)\n" +
        "Exit codes: 0 removed, 3 removed with items deleted at the next restart,\n" +
        "2 blocked by running HIDMaestro programs, 1 failed or did not run.";

    internal static int ExitCode(HMUninstallReport r) => r.Status switch
    {
        HMUninstallStatus.Removed or HMUninstallStatus.Previewed => ExitRemoved,
        HMUninstallStatus.RemovedRestartPending => ExitRestartPending,
        HMUninstallStatus.Blocked => ExitBlocked,
        _ => ExitFailed,
    };

    private static void Write(HMUninstallReport r, bool json)
    {
        if (json) WriteJson(r);
        else WriteText(r);
    }

    private static void WriteText(HMUninstallReport r)
    {
        Console.WriteLine(r.IsPreview ? "HIDMaestro removal preview" : "HIDMaestro removal");
        if (r.RunningPrograms.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("These programs use HIDMaestro and are running. Close them and run this again:");
            foreach (var p in r.RunningPrograms)
                Console.WriteLine($"  process {(p.ProcessId == 0 ? "unknown" : p.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture))}  " +
                                  $"{p.ImagePath ?? "(path unknown)"}  HIDMaestro {p.SdkVersion ?? "unknown"}");
        }
        Group(r, HMUninstallOutcome.WouldRemove, "Would remove");
        Group(r, HMUninstallOutcome.Removed, "Removed");
        Group(r, HMUninstallOutcome.RemovedAtRestart, "Deleted when Windows next starts");
        Group(r, HMUninstallOutcome.Kept, "Kept");
        Group(r, HMUninstallOutcome.Failed, "Failed");
        Console.WriteLine();
        Console.WriteLine(r.Status switch
        {
            HMUninstallStatus.Previewed => "Nothing was changed.",
            HMUninstallStatus.Blocked => "Nothing was changed.",
            HMUninstallStatus.Removed => "HIDMaestro was removed.",
            HMUninstallStatus.RemovedRestartPending => "HIDMaestro was removed. Some files are deleted when Windows next starts.",
            _ => "The removal did not finish. The failed items say why.",
        });
    }

    private static void Group(HMUninstallReport r, HMUninstallOutcome outcome, string title)
    {
        var items = r.Entries.Where(e => e.Outcome == outcome).ToList();
        if (items.Count == 0) return;
        Console.WriteLine();
        Console.WriteLine($"{title} ({items.Count}):");
        int width = Math.Min(26, items.Max(e => e.Kind.Length) + 2);
        foreach (var e in items)
        {
            Console.WriteLine($"  {e.Kind.PadRight(width)}{e.Location}");
            if (e.Reason != null) Console.WriteLine($"  {new string(' ', width)}  {e.Reason}");
        }
    }

    /// <summary>The report as JSON, written with Utf8JsonWriter, which needs
    /// no reflection under NativeAOT. The schema is in docs/UNINSTALL.md.</summary>
    private static void WriteJson(HMUninstallReport r)
    {
        using var stdout = Console.OpenStandardOutput();
        using (var w = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schemaVersion", 1);
            w.WriteBoolean("preview", r.IsPreview);
            w.WriteString("status", r.Status.ToString());
            w.WriteNumber("exitCode", ExitCode(r));
            w.WriteStartArray("runningPrograms");
            foreach (var p in r.RunningPrograms)
            {
                w.WriteStartObject();
                w.WriteNumber("processId", p.ProcessId);
                if (p.ImagePath != null) w.WriteString("imagePath", p.ImagePath); else w.WriteNull("imagePath");
                if (p.SdkVersion != null) w.WriteString("sdkVersion", p.SdkVersion); else w.WriteNull("sdkVersion");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("entries");
            foreach (var e in r.Entries)
            {
                w.WriteStartObject();
                w.WriteString("kind", e.Kind);
                w.WriteString("location", e.Location);
                w.WriteString("outcome", e.Outcome.ToString());
                if (e.Reason != null) w.WriteString("reason", e.Reason); else w.WriteNull("reason");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        stdout.Write("\n"u8);
    }
}
