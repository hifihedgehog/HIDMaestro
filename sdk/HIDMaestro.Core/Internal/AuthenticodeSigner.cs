using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace HIDMaestro.Internal;

/// <summary>
/// Signs a file in place with an Authenticode signature through
/// mssign32.dll's SignerSignEx2, in this process, with no tool from a
/// Windows kit.
///
/// <para>Through v2.2.0 the SDK carried signtool from the Windows SDK and ran
/// <c>signtool sign /a /sm /s My /n HIDMaestroTestCert /fd SHA256</c> on the
/// driver binaries and each catalog. This class makes the same signature
/// with the same certificate, found by its exact subject where that command
/// matched the name anywhere in the subject (<see cref="SelectCertificate"/>):
/// a SHA-256 digest, RSA PKCS#1 v1.5,
/// the certificate chain in the signature, the authenticated attributes
/// SpcSpOpusInfo (empty) and SpcStatementType (individual), no signing time
/// and no timestamp. Probe driver_catalog_check compares the two signatures
/// byte for byte where the Windows SDK is installed.</para>
/// </summary>
internal static unsafe partial class AuthenticodeSigner
{
    // mssign32 structures and constants, from the documentation of
    // SignerSignEx2 and the structures it takes (SIGNER_SUBJECT_INFO,
    // SIGNER_CERT, SIGNER_CERT_STORE_INFO, SIGNER_SIGNATURE_INFO,
    // SIGNER_ATTR_AUTHCODE, SIGNER_CONTEXT).
    private const uint SIGNER_SUBJECT_FILE = 1, SIGNER_CERT_STORE = 2, SIGNER_CERT_POLICY_CHAIN = 2,
        SIGNER_AUTHCODE_ATTR = 1, CALG_SHA_256 = 0x800C;

    /// <summary>Sign <paramref name="path"/> with <paramref name="certificate"/>,
    /// replacing any signature it carries.</summary>
    /// <exception cref="InvalidOperationException">SignerSignEx2 failed.</exception>
    internal static void Sign(string path, X509Certificate2 certificate)
    {
        uint index = 0;
        fixed (char* file = path)
        {
            var fileInfo = new SIGNER_FILE_INFO { cbSize = (uint)sizeof(SIGNER_FILE_INFO), pwszFileName = file };
            var subject = new SIGNER_SUBJECT_INFO
            {
                cbSize = (uint)sizeof(SIGNER_SUBJECT_INFO),
                pdwIndex = &index,
                dwSubjectChoice = SIGNER_SUBJECT_FILE,
                pSignerFileInfo = &fileInfo,
            };
            var storeInfo = new SIGNER_CERT_STORE_INFO
            {
                cbSize = (uint)sizeof(SIGNER_CERT_STORE_INFO),
                pSigningCert = certificate.Handle,
                dwCertPolicy = SIGNER_CERT_POLICY_CHAIN,
            };
            var cert = new SIGNER_CERT
            {
                cbSize = (uint)sizeof(SIGNER_CERT),
                dwCertChoice = SIGNER_CERT_STORE,
                pCertStoreInfo = &storeInfo,
            };
            var authcode = new SIGNER_ATTR_AUTHCODE
            {
                cbSize = (uint)sizeof(SIGNER_ATTR_AUTHCODE),
                fCommercial = 0,
                fIndividual = 1,
            };
            var signature = new SIGNER_SIGNATURE_INFO
            {
                cbSize = (uint)sizeof(SIGNER_SIGNATURE_INFO),
                algidHash = CALG_SHA_256,
                dwAttrChoice = SIGNER_AUTHCODE_ATTR,
                pAttrAuthcode = &authcode,
            };
            SIGNER_CONTEXT* context = null;
            int hr = SignerSignEx2(0, &subject, &cert, &signature, null, 0, null, null, null, null, &context, null, null);
            if (context != null) SignerFreeSignerContext(context);
            GC.KeepAlive(certificate);
            if (hr != 0)
                throw new InvalidOperationException(
                    $"Signing {path} failed (SignerSignEx2 0x{hr:X8}: {Marshal.GetExceptionForHR(hr)?.Message.TrimEnd('.')}).");
        }
    }

    /// <summary>The certificate HIDMaestro signs with: in LocalMachine\My,
    /// of the certificates whose subject is exactly <c>CN=</c>
    /// <paramref name="name"/> and that can sign (<see cref="CanSign"/>),
    /// the one valid for the longest time. The subject is the one
    /// <see cref="DriverBuilder.EnsureTestCertificate()"/> creates and the
    /// removal takes, compared as the removal compares it. Through v2.2.0
    /// <c>signtool sign /a /sm /s My /n</c> chose, which matches the name
    /// anywhere in the subject, so another certificate whose subject held
    /// the name and expired later won. The caller disposes it.</summary>
    /// <exception cref="InvalidOperationException">No certificate qualifies.</exception>
    internal static X509Certificate2 SelectCertificate(string name)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        X509Certificate2? best = null;
        DateTime now = DateTime.Now;
        foreach (var c in store.Certificates)
        {
            if (CanSign(c, name, now) && (best == null || c.NotAfter > best.NotAfter))
            {
                best?.Dispose();
                best = c;
            }
            else c.Dispose();
        }
        return best ?? throw new InvalidOperationException(
            $"No certificate in LocalMachine\\My with the subject CN={name} has a private key, is valid now and allows code signing.");
    }

    /// <summary>Whether <paramref name="c"/> is a certificate HIDMaestro can
    /// sign with: subject exactly <c>CN=</c><paramref name="name"/>, its
    /// private key present, within its validity period at
    /// <paramref name="now"/>, and code signing allowed.</summary>
    internal static bool CanSign(X509Certificate2 c, string name, DateTime now) =>
        string.Equals(c.Subject, "CN=" + name, StringComparison.Ordinal)
        && c.HasPrivateKey && c.NotBefore <= now && now <= c.NotAfter && AllowsCodeSigning(c);

    // A certificate with no enhanced key usage extension allows every use.
    private static bool AllowsCodeSigning(X509Certificate2 c)
    {
        foreach (var ext in c.Extensions)
            if (ext is X509EnhancedKeyUsageExtension eku)
            {
                foreach (var oid in eku.EnhancedKeyUsages)
                    if (oid.Value == "1.3.6.1.5.5.7.3.3") return true;
                return false;
            }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_FILE_INFO
    {
        public uint cbSize;
        public char* pwszFileName;
        public IntPtr hFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_SUBJECT_INFO
    {
        public uint cbSize;
        public uint* pdwIndex;
        public uint dwSubjectChoice;
        public SIGNER_FILE_INFO* pSignerFileInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_CERT_STORE_INFO
    {
        public uint cbSize;
        public IntPtr pSigningCert;
        public uint dwCertPolicy;
        public IntPtr hCertStore;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_CERT
    {
        public uint cbSize;
        public uint dwCertChoice;
        public SIGNER_CERT_STORE_INFO* pCertStoreInfo;
        public IntPtr hwnd;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_ATTR_AUTHCODE
    {
        public uint cbSize;
        public int fCommercial;
        public int fIndividual;
        public char* pwszName;
        public char* pwszInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_SIGNATURE_INFO
    {
        public uint cbSize;
        public uint algidHash;
        public uint dwAttrChoice;
        public SIGNER_ATTR_AUTHCODE* pAttrAuthcode;
        public IntPtr psAuthenticated;
        public IntPtr psUnauthenticated;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIGNER_CONTEXT
    {
        public uint cbSize;
        public uint cbBlob;
        public byte* pbBlob;
    }

    [LibraryImport("mssign32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SignerSignEx2(uint dwFlags, SIGNER_SUBJECT_INFO* pSubjectInfo, SIGNER_CERT* pSignerCert,
        SIGNER_SIGNATURE_INFO* pSignatureInfo, void* pProviderInfo, uint dwTimestampFlags, byte* pszTimestampAlgorithmOid,
        char* pwszHttpTimeStamp, void* psRequest, void* pSipData, SIGNER_CONTEXT** ppSignerContext, void* pCryptoPolicy,
        void* pReserved);

    [LibraryImport("mssign32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SignerFreeSignerContext(SIGNER_CONTEXT* pSignerContext);
}
