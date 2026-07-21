// Diagnostic tap (issue #35 follow-up): who writes output reports to a
// freshly created virtual Switch Pro? Subscribes OutputReceived and dumps
// every captured output report with a timestamp relative to create.
// 0x01 subcommands and 0x10 rumble reach the ring; 0x80 USB commands are
// answered in the driver and NOT published, so an 0x80-only writer shows
// up indirectly (stream flips Nintendo with no ring traffic).
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HIDMaestro;

var sw = Stopwatch.StartNew();
using var ctx = new HMContext();
ctx.LoadDefaultProfiles();
ctx.InstallDriver();
Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] installed");

var profile = ctx.GetProfile("switch-pro")!;
using var ctrl = ctx.CreateController(profile);
long created = sw.ElapsedMilliseconds;
Console.WriteLine($"[{created}ms] created");

ctrl.OutputReceived += (_, e) =>
{
    string hex = BitConverter.ToString(e.Data.Slice(0, Math.Min(e.Data.Length, 16)).ToArray());
    Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] OUT report=0x{e.ReportId:X2} len={e.Data.Length} bytes={hex}");
};

var pumpStop = false;
var pump = new Thread(() =>
{
    var st = new HMGamepadState();
    while (!Volatile.Read(ref pumpStop)) { ctrl.SubmitState(st); Thread.Sleep(8); }
}) { IsBackground = true };
pump.Start();

// Read the input stream in the SAME window so layout-vs-ring evidence is
// co-timed. Raw ReadFile on the HID interface, first 8 0x30 frames.
string? path = null;
for (int i = 0; i < 50 && path == null; i++)
{
    path = HIDMaestro.Internal.HidDeviceEnumerator.Enumerate()
        .FirstOrDefault(d => d.VendorId == 0x057E && d.ProductId == 0x2009)?.DevicePath;
    if (path == null) Thread.Sleep(100);
}
Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] hid path: {(path ?? "NOT FOUND")}");
if (path != null)
{
    using var fs = new System.IO.FileStream(
        new Microsoft.Win32.SafeHandles.SafeFileHandle(
            CreateFileW(path, 0xC0000000, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero), true),
        System.IO.FileAccess.Read, 64, false);
    var buf = new byte[64];
    for (int f = 0; f < 8; f++)
    {
        int r = fs.Read(buf, 0, 64);
        if (r <= 0) break;
        if (buf[0] != 0x30) { f--; continue; }
        Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] IN 0x30 b1={buf[1]:X2} b2={buf[2]:X2} b3={buf[3]:X2} b11={buf[11]:X2} b62={buf[62]:X2} b63={buf[63]:X2}");
    }
}

Thread.Sleep(1500);
Volatile.Write(ref pumpStop, true);
pump.Join(500);
Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] done (created at {created}ms)");

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
static extern IntPtr CreateFileW(string fileName, uint access, uint share,
    IntPtr security, uint disposition, uint flags, IntPtr template);
