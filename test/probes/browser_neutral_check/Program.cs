using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

internal sealed class Program
{
    private const int Port = 18739;
    private static string? s_resultJson;

    private sealed class Emu : IDisposable
    {
        public Process Proc = default!;
        public ConcurrentQueue<string> Lines = new();

        public static Emu Start(string testExe)
        {
            var emu = new Emu();
            var psi = new ProcessStartInfo
            {
                FileName = testExe,
                Arguments = "emulate --neutral dualshock-4-v2",
                WorkingDirectory = Path.GetDirectoryName(testExe)!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            emu.Proc = new Process { StartInfo = psi };
            emu.Proc.OutputDataReceived += (_, e) => { if (e.Data != null) emu.Lines.Enqueue(e.Data); };
            emu.Proc.ErrorDataReceived += (_, e) => { if (e.Data != null) emu.Lines.Enqueue("[stderr] " + e.Data); };
            if (!emu.Proc.Start()) throw new Exception("failed to start HIDMaestroTest");
            emu.Proc.BeginOutputReadLine();
            emu.Proc.BeginErrorReadLine();
            return emu;
        }

        public void WaitReady()
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 30_000)
            {
                while (Lines.TryDequeue(out var line))
                {
                    Console.WriteLine("  [emu] " + line);
                    if (line.Contains("controller(s) ready")) return;
                }
                if (Proc.HasExited) throw new Exception("HIDMaestroTest exited early: " + Proc.ExitCode);
                Thread.Sleep(50);
            }
            throw new TimeoutException("HIDMaestroTest did not become ready");
        }

        public void Send(string command)
        {
            Console.WriteLine("  [stdin] " + command);
            Proc.StandardInput.WriteLine(command);
            Proc.StandardInput.Flush();

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 20_000)
            {
                while (Lines.TryDequeue(out var line))
                {
                    Console.WriteLine("  [emu] " + line);
                    if (line.Trim() == "[ACK]") return;
                }
                if (Proc.HasExited) throw new Exception("HIDMaestroTest exited while waiting for ACK");
                Thread.Sleep(50);
            }
            throw new TimeoutException("timed out waiting for ACK after " + command);
        }

        public void Dispose()
        {
            try
            {
                if (!Proc.HasExited)
                {
                    try { Proc.StandardInput.WriteLine("quit"); Proc.StandardInput.Flush(); } catch { }
                    if (!Proc.WaitForExit(60_000)) Proc.Kill(entireProcessTree: true);
                }
            }
            catch { }
            finally { Proc.Dispose(); }
        }
    }

    private static string Html => """
<!doctype html>
<html><head><meta charset="utf-8"><title>HM browser neutral check</title></head>
<body>
<pre id="out">sampling...</pre>
<script>
const samples = [];
function snap() {
  const pads = [];
  for (const p of navigator.getGamepads()) {
    if (!p) continue;
    pads.push({
      index: p.index,
      id: p.id,
      connected: p.connected,
      mapping: p.mapping,
      timestamp: p.timestamp,
      axes: Array.from(p.axes),
      buttons: p.buttons.map(b => ({ pressed: b.pressed, value: b.value }))
    });
  }
  samples.push({ t: performance.now(), pads });
  document.getElementById('out').textContent = JSON.stringify(pads, null, 2);
  if (samples.length < 40) setTimeout(snap, 100);
  else fetch('/result', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ samples, finalSnapshot: samples[samples.length - 1].pads }) })
    .finally(() => document.title = 'DONE');
}
setTimeout(snap, 250);
</script>
</body></html>
""";

    private static HttpListener StartServer()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = listener.GetContext();
                    if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url?.AbsolutePath == "/result")
                    {
                        using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                        s_resultJson = sr.ReadToEnd();
                        byte[] ok = Encoding.UTF8.GetBytes("ok");
                        ctx.Response.OutputStream.Write(ok);
                        ctx.Response.Close();
                    }
                    else
                    {
                        byte[] data = Encoding.UTF8.GetBytes(Html);
                        ctx.Response.ContentType = "text/html; charset=utf-8";
                        ctx.Response.OutputStream.Write(data);
                        ctx.Response.Close();
                    }
                }
                catch { break; }
            }
        });
        return listener;
    }

    private static string FindBrowser()
    {
        string[] candidates =
        {
            @"C:\Users\Arthur\AppData\Local\imput\Helium\Application\chrome.exe",
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\imput\Helium\Application\helium.exe"),
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\imput\Helium\Application\chrome.exe"),
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\imput\Helium\Application\msedge.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Edge/Chrome not found");
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static Process StartBrowser()
    {
        string browser = FindBrowser();
        string profile = Path.Combine(Path.GetTempPath(), "hm_browser_neutral_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var psi = new ProcessStartInfo
        {
            FileName = browser,
            Arguments = $"--app=http://127.0.0.1:{Port}/ --user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows --window-size=500,400 --window-position=100,100",
            UseShellExecute = false,
        };
        var p = Process.Start(psi) ?? throw new Exception("failed to start browser");
        Thread.Sleep(2500);
        try
        {
            p.Refresh();
            if (p.MainWindowHandle != IntPtr.Zero)
            {
                ShowWindow(p.MainWindowHandle, 5);
                SetForegroundWindow(p.MainWindowHandle);
            }
        }
        catch { }
        return p;
    }

    private static string ResolveTestExe()
    {
        string tfm = "net10.0-windows10.0.26100.0";
        string baseDir = AppContext.BaseDirectory;
        return Path.GetFullPath(Path.Combine(baseDir,
            "..", "..", "..", "..", "..", "..",
            "bin", "Release", tfm, "win-x64", "HIDMaestroTest.exe"));
    }

    private static JsonElement? FindDs4(JsonElement snapshot)
    {
        foreach (var pad in snapshot.EnumerateArray())
        {
            string id = pad.GetProperty("id").GetString()?.ToLowerInvariant() ?? "";
            if ((id.Contains("054c") && id.Contains("09cc")) || id.Contains("wireless controller"))
                return pad;
        }
        return null;
    }

    private static JsonElement? RunBrowserSample()
    {
        s_resultJson = null;
        using var browser = StartBrowser();
        var sw = Stopwatch.StartNew();
        while (s_resultJson == null && sw.ElapsedMilliseconds < 30_000)
            Thread.Sleep(100);
        try { browser.Kill(entireProcessTree: true); } catch { }
        if (s_resultJson == null) return null;
        return JsonDocument.Parse(s_resultJson).RootElement.Clone();
    }

    private static List<string> NeutralErrors(JsonElement pad, string prefix)
    {
        var errors = new List<string>();
        int i = 0;
        foreach (var axis in pad.GetProperty("axes").EnumerateArray())
        {
            double v = axis.GetDouble();
            if (Math.Abs(v) > 0.08) errors.Add($"{prefix}: axis[{i}]={v:F5}");
            i++;
        }
        i = 0;
        foreach (var b in pad.GetProperty("buttons").EnumerateArray())
        {
            double v = b.GetProperty("value").GetDouble();
            bool pressed = b.GetProperty("pressed").GetBoolean();
            if (v > 0.08 || pressed) errors.Add($"{prefix}: button[{i}] value={v:F2} pressed={pressed}");
            i++;
        }
        return errors;
    }

    private static int Main()
    {
        Console.WriteLine("=== Browser neutral DS4 check ===");
        string testExe = ResolveTestExe();
        Console.WriteLine("HIDMaestroTest: " + testExe);
        if (!File.Exists(testExe)) { Console.WriteLine("FAIL: HIDMaestroTest not found"); return 2; }

        using var server = StartServer();
        using var emu = Emu.Start(testExe);
        try
        {
            emu.WaitReady();
            Thread.Sleep(1000);

            var root = RunBrowserSample();
            if (root == null)
            {
                Console.WriteLine("FAIL: browser did not post gamepad result");
                return 2;
            }

            var snapshot = root.Value.GetProperty("finalSnapshot");
            var ds4 = FindDs4(snapshot);
            if (ds4 == null)
            {
                Console.WriteLine("FAIL: DS4 not found in browser snapshot");
                Console.WriteLine(root.Value.ToString());
                return 1;
            }

            Console.WriteLine("Neutral DS4: " + ds4.Value.GetProperty("id").GetString());
            var errors = new List<string>();
            int i = 0;
            foreach (var axis in ds4.Value.GetProperty("axes").EnumerateArray())
            {
                double v = axis.GetDouble();
                Console.WriteLine($"  axis[{i}]={v:F5}");
                if (Math.Abs(v) > 0.08) errors.Add($"axis[{i}]={v:F5}");
                i++;
            }
            i = 0;
            foreach (var b in ds4.Value.GetProperty("buttons").EnumerateArray())
            {
                double v = b.GetProperty("value").GetDouble();
                bool pressed = b.GetProperty("pressed").GetBoolean();
                if (v > 0.001 || pressed) Console.WriteLine($"  button[{i}] value={v:F2} pressed={pressed}");
                if (v > 0.08 || pressed) errors.Add($"button[{i}] value={v:F2} pressed={pressed}");
                i++;
            }

            int sampleIndex = 0;
            foreach (var sample in root.Value.GetProperty("samples").EnumerateArray())
            {
                var samplePad = FindDs4(sample.GetProperty("pads"));
                if (samplePad != null)
                    errors.AddRange(NeutralErrors(samplePad.Value, $"sample[{sampleIndex}]"));
                sampleIndex++;
            }

            if (errors.Count > 0)
            {
                Console.WriteLine("FAIL: browser sees non-neutral DS4 while neutral is ON:");
                foreach (var e in errors) Console.WriteLine("  " + e);
                return 1;
            }

            Console.WriteLine($"PASS: browser sees neutral DS4 across {sampleIndex} samples");

            emu.Send("neutral off all");
            Thread.Sleep(500);
            root = RunBrowserSample();
            if (root == null)
            {
                Console.WriteLine("FAIL: browser did not post gamepad result after neutral off");
                return 2;
            }
            snapshot = root.Value.GetProperty("finalSnapshot");
            ds4 = FindDs4(snapshot);
            if (ds4 == null)
            {
                Console.WriteLine("FAIL: DS4 not found after neutral off");
                return 1;
            }

            var activeErrors = NeutralErrors(ds4.Value, "neutral-off final");
            Console.WriteLine("Neutral OFF final active fields:");
            foreach (var e in activeErrors.Take(12)) Console.WriteLine("  " + e);
            if (activeErrors.Count == 0)
            {
                Console.WriteLine("FAIL: neutral off did not expose HIDMaestroTest active pattern");
                return 1;
            }

            Console.WriteLine("PASS: neutral off exposes HIDMaestroTest active pattern (expected for emulate)");
            return 0;
        }
        finally
        {
            try { server.Stop(); } catch { }
        }
    }
}
