// The --diag run's logs. Each program inside the sandbox runs as SYSTEM, whose
// %TEMP% is C:\Windows\SystemTemp whatever TEMP says (GetTempPath2), so two
// programs would write the SDK's one teardown_diag.log at once. Each instead
// takes the SDK's diagnostic lines through its tap and writes them to a log
// of its own in the results folder, timed as the SDK times its own.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using HIDMaestro;

internal static class DiagLog
{
    static StreamWriter? s_writer;
    static readonly object s_lock = new();

    internal static bool On => s_writer != null;

    /// <summary>HIDMAESTRO_DIAG=1 for this process, set before the SDK reads
    /// it, and every diagnostic line of the SDK to <paramref name="path"/>.</summary>
    internal static void Start(string path)
    {
        Environment.SetEnvironmentVariable("HIDMAESTRO_DIAG", "1");
        s_writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var orchestrator = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.DeviceOrchestrator", throwOnError: true)!;
        orchestrator.GetField("DiagTap", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(null, (Action<string>)Note);
        Note($"probe: pid {Environment.ProcessId}, {Environment.CommandLine}");
        Note($"probe: VC++ runtime {VcRuntime()}");
    }

    /// <summary>The VC++ 14 x64 runtime as Windows records it: the
    /// redistributable's registry entry and the two DLLs devnode.exe imports.</summary>
    internal static string VcRuntime()
    {
        string entry = "no registry entry";
        try
        {
            using var b = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var k = b.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            if (k != null) entry = $"Installed={k.GetValue("Installed")} Version={k.GetValue("Version")}";
        }
        catch (Exception ex) { entry = ex.GetType().Name; }
        string Dll(string n)
        {
            string p = Path.Combine(Environment.SystemDirectory, n);
            return File.Exists(p) ? $"{n} {FileVersionInfo.GetVersionInfo(p).FileVersion}" : $"{n} absent";
        }
        return $"{entry}, {Dll("vcruntime140.dll")}, {Dll("msvcp140.dll")}";
    }

    internal static void Note(string line)
    {
        if (s_writer == null) return;
        lock (s_lock)
        {
            try
            {
                s_writer.Write(FormattableString.Invariant(
                    $"[{DateTime.Now:HH:mm:ss.fff}] tid={Environment.CurrentManagedThreadId,-3} {line}\n"));
            }
            catch { }
        }
    }

    /// <summary>Once a second, every process an install starts, with its id
    /// and start time, when the list changes.</summary>
    internal static IDisposable SampleInstallers(string path)
    {
        var stop = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            using var w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            string last = "";
            do
            {
                var now = Process.GetProcesses()
                    .Select(p =>
                    {
                        try
                        {
                            string n = p.ProcessName;
                            if (!(n.StartsWith("USBip-", StringComparison.OrdinalIgnoreCase) || n.Equals("devnode", StringComparison.OrdinalIgnoreCase)
                                  || n.Equals("pnputil", StringComparison.OrdinalIgnoreCase) || n.Equals("drvinst", StringComparison.OrdinalIgnoreCase)
                                  || n.StartsWith("vc_redist", StringComparison.OrdinalIgnoreCase) || n.Equals("msiexec", StringComparison.OrdinalIgnoreCase)
                                  || n.Equals("hmswd", StringComparison.OrdinalIgnoreCase) || n.Equals("UsbipInstallCheck", StringComparison.OrdinalIgnoreCase)
                                  || n.Equals("certutil", StringComparison.OrdinalIgnoreCase)))
                                return null;
                            string started = "";
                            try { started = p.StartTime.ToString("HH:mm:ss.fff"); } catch { }
                            return $"{n}:{p.Id}@{started}";
                        }
                        catch { return null; }
                        finally { p.Dispose(); }
                    })
                    .Where(s => s != null).OrderBy(s => s, StringComparer.Ordinal);
                string line = string.Join(" ", now);
                if (line != last)
                {
                    w.Write($"[{DateTime.Now:HH:mm:ss.fff}] {line}\n");
                    last = line;
                }
            } while (!stop.Wait(1000));
        }) { IsBackground = true };
        t.Start();
        return new Stopper(() => { stop.Set(); t.Join(5000); });
    }

    sealed class Stopper : IDisposable
    {
        readonly Action _a;
        internal Stopper(Action a) => _a = a;
        public void Dispose() => _a();
    }

    /// <summary>Copy into the results folder Windows' device installation
    /// logs, the SDK's own diagnostic folder, the logs the installer's
    /// children write to their temporary folders (the VC++ runtime's bundle
    /// and its packages), and the Windows Installer events.</summary>
    internal static void Collect(string results)
    {
        Note($"probe: VC++ runtime at the end {VcRuntime()}");
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (string name in new[] { "setupapi.dev.log", "setupapi.app.log" })
            try { File.Copy(Path.Combine(win, "INF", name), Path.Combine(results, name), true); } catch { }
        CopyLogs(Path.Combine(win, "SystemTemp", "HIDMaestro"), Path.Combine(results, "sdk-temp"), SearchOption.TopDirectoryOnly);
        CopyLogs(Path.Combine(win, "SystemTemp"), Path.Combine(results, "system-temp"), SearchOption.TopDirectoryOnly);
        CopyLogs(Path.Combine(win, "Temp"), Path.Combine(results, "windows-temp"), SearchOption.TopDirectoryOnly);
        CopyLogs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HIDMaestro"),
                 Path.Combine(results, "staging"), SearchOption.AllDirectories);
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wevtutil.exe"))
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (string a in new[] { "qe", "Application", "/q:*[System[Provider[@Name='MsiInstaller']]]", "/f:text" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            File.WriteAllText(Path.Combine(results, "msiinstaller-events.txt"), p.StandardOutput.ReadToEnd());
            p.WaitForExit(30_000);
        }
        catch { }
    }

    static void CopyLogs(string from, string to, SearchOption depth)
    {
        try
        {
            if (!Directory.Exists(from)) return;
            foreach (string f in Directory.GetFiles(from, "*.log", depth))
            {
                try
                {
                    string dest = Path.Combine(to, Path.GetRelativePath(from, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(f, dest, true);
                }
                catch { }
            }
        }
        catch { }
    }
}
