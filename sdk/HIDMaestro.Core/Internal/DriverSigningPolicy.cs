using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HIDMaestro.Internal;

/// <summary>
/// Whether Windows on this machine installs a driver package signed by a
/// publisher the machine trusts, as HIDMaestro signs its own
/// (<see cref="DriverBuilder.EnsureTestCertificate()"/>), or only one that
/// Microsoft signed.
///
/// <para>The driver store import of the first kind fails with 0x800B0109
/// when all four of these hold. Each is read from the Windows binary named,
/// with its public symbols.</para>
/// <list type="number">
/// <item>drvinst.exe is in the state its log calls Enforced:
/// CODEINTEGRITY_OPTION_ENABLED set and CODEINTEGRITY_OPTION_TESTSIGN clear
/// (ValidateDriverPackage, from SystemCodeIntegrityInformation). In that
/// state a failed signature check fails the import with no prompt.</item>
/// <item>drvinst.exe makes its Authenticode check of the catalog with
/// WTD_CODE_INTEGRITY_DRIVER_MODE (SpSignVerifyInfFile). The ARM64 build
/// 10.0.28000.3365 always does: the flags are a constant. The x64 build
/// 10.0.26100.9711 does only when WldpQueryWindowsLockdownMode reports a
/// mode other than unlocked, which is Windows in S mode
/// (SpSignSkuRequiresSignedInf).</item>
/// <item>In that mode wintrust.dll reads SystemCodeIntegrityInformation,
/// and with CODEINTEGRITY_OPTION_WHQL_ENFORCEMENT_ENABLED set the signer
/// has to chain to a Microsoft root with the WHQL usage
/// (CheckValidSignature, _SatisfiesSysDevWhqlRequirement). The x64 and the
/// ARM64 build carry the same test of bit 14.</item>
/// <item>The system integrity policy is not in audit mode
/// (_SIAuditModeEnabled), in which wintrust.dll lets the signer pass.</item>
/// </list>
///
/// <para>Measured on 2026-11-17 with one catalog, signed as HIDMaestro
/// signs and its certificate in Root and TrustedPublisher. On Windows 11
/// ARM64 build 28000, options 0xF405, WinVerifyTrust returns 0 without the
/// mode flag and 0x800B0109 with it, and the import of HIDMaestro's package
/// fails with that code. On x64 builds 19044 and 26100, options 0x1 and
/// 0x2001, it returns 0 both ways and the package installs. Memory
/// integrity plays no part: on build 19044 with it running in strict mode,
/// options 0x3401, the catalog verified both ways and the driver installed
/// and ran a controller.</para>
/// </summary>
internal static partial class DriverSigningPolicy
{
    // CodeIntegrityOptions of SYSTEM_CODEINTEGRITY_INFORMATION. winternl.h
    // of the 10.0.26100.0 SDK names the first two (:371, :372). phnt's
    // ntexapi.h names all three (:4064, :4065, :4078).
    internal const uint CodeIntegrityEnabled = 0x01, CodeIntegrityTestSign = 0x02, WhqlEnforcement = 0x4000;

    // SYSTEM_INFORMATION_CLASS. winternl.h:401 gives the first. The other
    // two are their positions in phnt's ntexapi.h, and the constants
    // wintrust.dll passes: 0x67, 0x8F and 0xA4.
    private const int SystemCodeIntegrityInformation = 103,
        SystemSecureBootPolicyInformation = 143,
        SystemCodeIntegrityPolicyInformation = 164;

    /// <summary>What the decision reads. <paramref name="CodeIntegrityOptions"/>
    /// is null when the query failed.</summary>
    internal readonly record struct Inputs(uint? CodeIntegrityOptions, bool AuditMode, bool DriverMode, Architecture Architecture);

    /// <summary>The four conditions of the class summary. Options that could
    /// not be read decide nothing, so the import is tried.</summary>
    internal static bool Refuses(in Inputs i) =>
        i.CodeIntegrityOptions is uint options
        && (options & CodeIntegrityEnabled) != 0
        && (options & CodeIntegrityTestSign) == 0
        && i.DriverMode
        && (options & WhqlEnforcement) != 0
        && !i.AuditMode;

    internal static HMDriverSigningStatus StatusOf(in Inputs i) => new(
        Refuses(i) ? HMDriverSigningRequirement.MicrosoftSigned : HMDriverSigningRequirement.TrustedPublisher,
        i.CodeIntegrityOptions is uint options && (options & WhqlEnforcement) != 0,
        i.Architecture);

    private static readonly Lazy<HMDriverSigningStatus> s_status = new(() => StatusOf(Read()));

    /// <summary>This machine's status. Windows sets the options when it
    /// starts, so one read serves the process.</summary>
    internal static HMDriverSigningStatus Status => s_status.Value;

    internal static Inputs Read()
    {
        uint? options = CodeIntegrityOptions();
        return new Inputs(options, options != null && AuditMode(), DriverMode(), RuntimeInformation.OSArchitecture);
    }

    /// <summary>SYSTEM_CODEINTEGRITY_INFORMATION's CodeIntegrityOptions
    /// (winternl.h:386: ULONG Length, ULONG CodeIntegrityOptions), or null
    /// when the query fails. Any process may ask.</summary>
    internal static unsafe uint? CodeIntegrityOptions()
    {
        uint* info = stackalloc uint[2];
        info[0] = 8;
        info[1] = 0;
        return NtQuerySystemInformation(SystemCodeIntegrityInformation, info, 8, null) >= 0 ? info[1] : null;
    }

    /// <summary>wintrust.dll's _SIAuditModeEnabled. False when
    /// SYSTEM_SECUREBOOT_POLICY_INFORMATION (GUID PolicyPublisher, ULONG
    /// PolicyVersion, ULONG PolicyOptions) reads with PolicyOptions bit 0x80
    /// set. Otherwise bit 1, Audit, of the Options that open
    /// SYSTEM_CODEINTEGRITYPOLICY_INFORMATION (32 bytes), and false when that
    /// query fails. Both layouts are phnt's (ntexapi.h:5208, :5976).</summary>
    private static unsafe bool AuditMode()
    {
        uint* secureBoot = stackalloc uint[6];
        new Span<uint>(secureBoot, 6).Clear();
        if (NtQuerySystemInformation(SystemSecureBootPolicyInformation, secureBoot, 24, null) >= 0
            && (secureBoot[5] & 0x80) != 0)
            return false;
        uint* policy = stackalloc uint[8];
        new Span<uint>(policy, 8).Clear();
        return NtQuerySystemInformation(SystemCodeIntegrityPolicyInformation, policy, 32, null) >= 0
            && (policy[0] & 0x2) != 0;
    }

    /// <summary>Whether drvinst.exe checks a package's catalog in driver
    /// mode on this machine: Windows on ARM64, or Windows locked down.</summary>
    private static bool DriverMode() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 || LockedDown();

    /// <summary>WldpQueryWindowsLockdownMode's answer other than
    /// WLDP_WINDOWS_LOCKDOWN_MODE_UNLOCKED, 0 (wldp.h:212, :463). A call
    /// that fails, and a Windows without the export, count as unlocked, as
    /// drvinst.exe counts them.</summary>
    private static bool LockedDown()
    {
        try { return WldpQueryWindowsLockdownMode(out int mode) >= 0 && mode != 0; }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }

    // ── After an import that did not land ────────────────────────────────

    /// <summary>For an import that did not land on a machine whose status
    /// says Microsoft-signed only: asks Windows about the staged catalog
    /// both ways, and throws <see cref="HMDriverSigningException"/> when the
    /// catalog is trusted without the mode flag and refused with it. Any
    /// other outcome returns, and the caller reports the import as it did
    /// before.</summary>
    internal static void ThrowIfImportRefused(HMDriverSigningStatus status, string catalog)
    {
        if (status.CanInstallDriver || !File.Exists(catalog)) return;
        int plain = Verify(catalog, driverMode: false);
        int mode = Verify(catalog, driverMode: true);
        DeviceOrchestrator.LogDiag($"    deploy: {Path.GetFileName(catalog)} verifies 0x{plain:X8}, 0x{mode:X8} in driver mode");
        if (plain == 0 && mode != 0)
            throw new HMDriverSigningException(status,
                $"{status.Explanation} Windows answered 0x{mode:X8} for the signed catalog.");
    }

    // wintrust.h of the 10.0.26100.0 SDK: WTD_UI_NONE (:102), WTD_REVOKE_NONE
    // (:107), WTD_CHOICE_FILE (:111), WTD_STATEACTION_VERIFY and _CLOSE
    // (:129, :130), WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT (:147),
    // WTD_CACHE_ONLY_URL_RETRIEVAL (:152), WTD_CODE_INTEGRITY_DRIVER_MODE
    // (:155). drvinst.exe's SpSignVerifyFile passes 0x1080, or 0x9080 in
    // driver mode.
    private const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1,
        WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2,
        WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT = 0x80, WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000,
        WTD_CODE_INTEGRITY_DRIVER_MODE = 0x8000;

    /// <summary>WinVerifyTrust's answer for a signed file under
    /// WINTRUST_ACTION_GENERIC_VERIFY_V2 (softpub.h:53), the policy of
    /// drvinst.exe's Authenticode check, with its flags. 0 when Windows
    /// trusts the signer.</summary>
    internal static unsafe int Verify(string path, bool driverMode)
    {
        Guid action = new(0x00AAC56B, 0xCD44, 0x11D0, 0x8C, 0xC2, 0x00, 0xC0, 0x4F, 0xC2, 0x95, 0xEE);
        fixed (char* file = path)
        {
            var info = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = file };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = &info,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
                              | (driverMode ? WTD_CODE_INTEGRITY_DRIVER_MODE : 0),
            };
            int result = WinVerifyTrust(IntPtr.Zero, &action, &data);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, &action, &data);
            return result;
        }
    }

    // wintrust.h: WINTRUST_FILE_INFO (:220) and WINTRUST_DATA (:93), 88
    // bytes in a 64-bit process, the cbStruct drvinst.exe passes.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public IntPtr hFile;
        public Guid* pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct WINTRUST_DATA
    {
        public uint cbStruct;
        public void* pPolicyCallbackData;
        public void* pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public WINTRUST_FILE_INFO* pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public char* pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public void* pSignatureSettings;
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQuerySystemInformation(int infoClass, void* buffer, uint length, uint* returnLength);

    [LibraryImport("wldp.dll")]
    private static partial int WldpQueryWindowsLockdownMode(out int lockdownMode);

    [LibraryImport("wintrust.dll")]
    private static unsafe partial int WinVerifyTrust(IntPtr hwnd, Guid* actionId, WINTRUST_DATA* data);
}
