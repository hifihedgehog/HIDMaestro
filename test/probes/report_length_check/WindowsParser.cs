// Windows' own HID report descriptor parser, run in this process.
//
// HID class asks the minidriver for its report descriptor when the device
// starts and hands it to hidparse.sys's HidP_GetCollectionDescription, as
// Wine's HID class does (dlls/hidclass.sys/pnp.c:198-213 at 2df1ee2), and a
// status other than success fails the start: Windows records problem code
// 10 with that status (setupapi.dev.log, "problem status: 0xc000001d" for
// ps3-remote's create in S93 at b7b19bf7). hidparse.sys is a kernel image that exports the
// function, imports from ntoskrnl.exe only, and parses with pool memory and
// no device. This loads the machine's own copy into memory the way a loader
// does (sections at their virtual addresses, DIR64 relocations, the import
// table bound by name), binds the pool calls to this process's heap and
// RtlQueryFeatureConfiguration to ntdll's export of the same name, so the
// parser reads this machine's feature configuration as user mode reads it,
// and calls the export. No header of the Windows SDK or WDK 10.0.26100.0
// declares RtlQueryFeatureConfiguration. On the devbox the parser asks for four features, each reads enabled, and the
// result for ps3-remote's descriptor at b7b19bf7 is the status Windows
// logged when that device failed its start in S93. Any other import the
// parser calls is counted, and a count other than zero makes the result
// untrustworthy, which the check reports.
//
// Learn, HidP_GetCollectionDescription: STATUS_ILLEGAL_INSTRUCTION "Failed
// to parse an item in the report descriptor", STATUS_COULD_NOT_INTERPRET
// "An error was detected in the report descriptor. See the error code in
// Dbg field". hidpddi.h (WDK 10.0.26100.0) names the Dbg codes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

internal static unsafe class WindowsParser
{
    /// <summary>HIDP_DEVICE_DESC (hidpddi.h): two arrays the parser
    /// allocates, their lengths, and HIDP_GETCOLDESC_DBG.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct DeviceDesc
    {
        public IntPtr CollectionDesc;
        public uint CollectionDescLength;
        public IntPtr ReportIDs;
        public uint ReportIDsLength;
        public uint BreakOffset;
        public uint ErrorCode;
        public fixed uint Args[6];
    }

    /// <summary>What the parser gave: its NTSTATUS, and on a failure the
    /// Dbg error code (hidpddi.h HIDP_GETCOLDESC_*) and its first
    /// argument.</summary>
    internal readonly record struct Result(uint Status, uint ErrorCode, uint Arg0)
    {
        public bool Ok => Status == 0;
        public override string ToString() => Ok ? "STATUS_SUCCESS" : $"0x{Status:X8} code 0x{ErrorCode:X2} arg 0x{Arg0:X2}";
    }

    internal const uint STATUS_ILLEGAL_INSTRUCTION = 0xC000001D;
    internal const uint STATUS_COULD_NOT_INTERPRET = 0xC00000B9;
    internal const uint HIDP_GETCOLDESC_TOP_COLLECTION_USAGE = 0x0B;
    internal const uint HIDP_GETCOLDESC_ITEM_UNKNOWN = 0x12;

    static delegate* unmanaged<byte*, uint, int, DeviceDesc*, int> s_get;
    static delegate* unmanaged<DeviceDesc*, void> s_free;
    static int s_unexpected;

    /// <summary>The number of calls the parser made to an import this
    /// loader does not bind. Zero for a result that is Windows' own.</summary>
    internal static int UnexpectedCalls => s_unexpected;

    /// <summary>The path of the parser this loads.</summary>
    internal static string ImagePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "hidparse.sys");

    /// <summary>Loads hidparse.sys once. False, with the reason, when the
    /// image is not one this process can run.</summary>
    internal static bool TryLoad(out string why)
    {
        why = "";
        if (s_get != null) return true;
        byte[] d;
        try { d = File.ReadAllBytes(ImagePath); }
        catch (Exception e) { why = $"{ImagePath}: {e.GetType().Name}: {e.Message}"; return false; }
        int pe = BitConverter.ToInt32(d, 0x3C);
        ushort machine = BitConverter.ToUInt16(d, pe + 4);
        ushort sections = BitConverter.ToUInt16(d, pe + 6);
        ushort optSize = BitConverter.ToUInt16(d, pe + 20);
        int opt = pe + 24;
        // IMAGE_FILE_MACHINE_AMD64 and PE32+, the form this process runs.
        if (machine != 0x8664 || BitConverter.ToUInt16(d, opt) != 0x20B || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            why = $"machine 0x{machine:X4} beside a {RuntimeInformation.ProcessArchitecture} process";
            return false;
        }
        ulong imageBase = BitConverter.ToUInt64(d, opt + 24);
        uint imageSize = BitConverter.ToUInt32(d, opt + 56);
        uint headerSize = BitConverter.ToUInt32(d, opt + 60);
        int dirs = opt + 112;
        (uint Rva, uint Size) Dir(int i) => (BitConverter.ToUInt32(d, dirs + 8 * i), BitConverter.ToUInt32(d, dirs + 8 * i + 4));

        // MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE. Released never:
        // the parser stays loaded for the process's life.
        byte* image = (byte*)VirtualAlloc(IntPtr.Zero, imageSize, 0x3000, 0x40);
        if (image == null) { why = $"VirtualAlloc failed, error {Marshal.GetLastPInvokeError()}"; return false; }
        Marshal.Copy(d, 0, (IntPtr)image, (int)headerSize);
        int sectionTable = opt + optSize;
        for (int i = 0; i < sections; i++)
        {
            int s = sectionTable + 40 * i;
            uint va = BitConverter.ToUInt32(d, s + 12), rawSize = BitConverter.ToUInt32(d, s + 16), rawAt = BitConverter.ToUInt32(d, s + 20);
            if (rawSize != 0) Marshal.Copy(d, (int)rawAt, (IntPtr)(image + va), (int)rawSize);
        }

        // IMAGE_REL_BASED_DIR64 (10) adds the move to a 64-bit address,
        // IMAGE_REL_BASED_ABSOLUTE (0) pads a block.
        long delta = (long)image - (long)imageBase;
        var (relocAt, relocSize) = Dir(5);
        for (uint p = relocAt; p < relocAt + relocSize;)
        {
            uint page = *(uint*)(image + p), blockSize = *(uint*)(image + p + 4);
            if (blockSize < 8) break;
            for (uint e = 8; e < blockSize; e += 2)
            {
                ushort entry = *(ushort*)(image + p + e);
                int type = entry >> 12;
                if (type == 10) *(long*)(image + page + (entry & 0xFFF)) += delta;
                else if (type != 0) { why = $"relocation type {type}"; return false; }
            }
            p += blockSize;
        }

        IntPtr ntdll = NativeLibrary.Load("ntdll.dll");
        var (importAt, _) = Dir(1);
        for (uint i = importAt; ; i += 20)
        {
            uint lookup = *(uint*)(image + i), nameRva = *(uint*)(image + i + 12), iat = *(uint*)(image + i + 16);
            if (nameRva == 0) break;
            for (uint t = lookup != 0 ? lookup : iat, a = iat; ; t += 8, a += 8)
            {
                ulong thunk = *(ulong*)(image + t);
                if (thunk == 0) break;
                if ((thunk >> 63) != 0) { why = "an import by ordinal"; return false; }
                string name = Marshal.PtrToStringAnsi((IntPtr)(image + (uint)thunk + 2))!;
                *(IntPtr*)(image + a) = Bind(name, ntdll);
            }
        }

        var (exportAt, _) = Dir(0);
        uint count = *(uint*)(image + exportAt + 24);
        uint functions = *(uint*)(image + exportAt + 28), names = *(uint*)(image + exportAt + 32), ordinals = *(uint*)(image + exportAt + 36);
        var exports = new Dictionary<string, IntPtr>();
        for (uint i = 0; i < count; i++)
        {
            string name = Marshal.PtrToStringAnsi((IntPtr)(image + *(uint*)(image + names + 4 * i)))!;
            ushort ordinal = *(ushort*)(image + ordinals + 2 * i);
            exports[name] = (IntPtr)(image + *(uint*)(image + functions + 4 * ordinal));
        }
        if (!exports.TryGetValue("HidP_GetCollectionDescription", out var get)
            || !exports.TryGetValue("HidP_FreeCollectionDescription", out var free))
        {
            why = "no HidP_GetCollectionDescription export";
            return false;
        }
        s_free = (delegate* unmanaged<DeviceDesc*, void>)free;
        s_get = (delegate* unmanaged<byte*, uint, int, DeviceDesc*, int>)get;
        return true;
    }

    static IntPtr Bind(string name, IntPtr ntdll) => name switch
    {
        "ExAllocatePoolWithTag" => (IntPtr)(delegate* unmanaged<int, nuint, uint, void*>)&AllocateWithTag,
        "ExAllocatePool2" => (IntPtr)(delegate* unmanaged<ulong, nuint, uint, void*>)&Allocate2,
        "ExFreePool" => (IntPtr)(delegate* unmanaged<void*, void>)&Free,
        "ExFreePoolWithTag" => (IntPtr)(delegate* unmanaged<void*, uint, void>)&FreeWithTag,
        "KeGetCurrentIrql" => (IntPtr)(delegate* unmanaged<byte>)&PassiveLevel,
        "RtlQueryFeatureConfiguration" => NativeLibrary.GetExport(ntdll, name),
        _ => (IntPtr)(delegate* unmanaged<long>)&Unexpected,
    };

    // The pool calls the parser makes, on this process's heap, zeroed as
    // ExAllocatePool2's are (Learn, ExAllocatePool2).
    [UnmanagedCallersOnly] static void* AllocateWithTag(int poolType, nuint size, uint tag) => NativeMemory.AllocZeroed(size == 0 ? 1 : size);
    [UnmanagedCallersOnly] static void* Allocate2(ulong flags, nuint size, uint tag) => NativeMemory.AllocZeroed(size == 0 ? 1 : size);
    [UnmanagedCallersOnly] static void Free(void* p) => NativeMemory.Free(p);
    [UnmanagedCallersOnly] static void FreeWithTag(void* p, uint tag) => NativeMemory.Free(p);
    [UnmanagedCallersOnly] static byte PassiveLevel() => 0;
    [UnmanagedCallersOnly] static long Unexpected() { s_unexpected++; return 0; }

    /// <summary>The parser's verdict on <paramref name="descriptor"/>, as HID
    /// class meets it at the device's start.</summary>
    internal static Result Parse(byte[] descriptor)
    {
        if (s_get == null) throw new InvalidOperationException("WindowsParser.TryLoad first");
        var desc = new DeviceDesc();
        int status;
        fixed (byte* p = descriptor)
            // PagedPool (1), as Wine's HID class passes it. It reaches only
            // the allocator above.
            status = s_get(p, (uint)descriptor.Length, 1, &desc);
        if (status == 0)
        {
            s_free(&desc);
            return new Result(0, 0, 0);
        }
        return new Result((uint)status, desc.ErrorCode, desc.Args[0]);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint allocationType, uint protect);
}
