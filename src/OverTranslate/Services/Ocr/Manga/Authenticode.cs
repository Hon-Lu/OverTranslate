using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// Whether a file carries an embedded Authenticode signature this machine trusts, made out to a
/// given organisation.
/// </summary>
/// <remarks>
/// <para>WinVerifyTrust checks the signature and its chain to a trusted root; the signer's
/// certificate is then read from the file to check whose it is. Revocation is not checked and no
/// URL is fetched: this runs before a native library is loaded, and must not wait on a network
/// that may not be there. The file's SHA-256 is pinned by the manifest as well, so this is the
/// second lock, not the only one.</para>
/// </remarks>
internal static class Authenticode
{
    /// <summary>Null when <paramref name="path"/> is signed by <paramref name="organization"/>; else why not.</summary>
    internal static unsafe string? Problem(string path, string organization)
    {
        var genericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        fixed (char* file = path)
        {
            var fileInfo = new FileInfo
            {
                Size = (uint)sizeof(FileInfo),
                FilePath = (IntPtr)file,
            };
            var data = new TrustData
            {
                Size = (uint)sizeof(TrustData),
                UiChoice = 2,                // WTD_UI_NONE
                RevocationChecks = 0,        // WTD_REVOKE_NONE
                UnionChoice = 1,             // WTD_CHOICE_FILE
                File = (IntPtr)(&fileInfo),
                StateAction = 1,             // WTD_STATEACTION_VERIFY
                // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
                ProviderFlags = 0x10 | 0x1000,
            };

            int result = WinVerifyTrust((IntPtr)(-1), ref genericVerifyV2, ref data);
            data.StateAction = 2;            // WTD_STATEACTION_CLOSE
            WinVerifyTrust((IntPtr)(-1), ref genericVerifyV2, ref data);
            if (result != 0)
                return $"the Authenticode signature is not valid (0x{result:X8})";
        }

        try
        {
#pragma warning disable SYSLIB0057 // the one API that reads the signer of an embedded signature
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var org = signer.SubjectName.EnumerateRelativeDistinguishedNames()
                .Where(name => name.GetSingleElementType().Value == "2.5.4.10")
                .Select(name => name.GetSingleElementValue())
                .FirstOrDefault();
            return org == organization ? null : $"signed by {signer.Subject}, not {organization}";
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            return $"the signer could not be read: {ex.Message}";
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

    // WINTRUST_FILE_INFO
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    // WINTRUST_DATA
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
