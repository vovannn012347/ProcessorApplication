using System.Runtime.InteropServices;
using System.Text;

namespace ProcessingModule.Services.Sandboxing.Windows;

/// <summary>
/// Keeps the sandbox account passwords out of the settings database in plain text: Windows DPAPI, bound to the account
/// the app runs as (another account, or a copy of the database on another machine, can't decrypt them).
/// Stored as "dpapi:" + base64. Values without the prefix are old plain-text passwords and are still accepted, so nothing
/// breaks before the next Provision (which stores them protected).
/// </summary>
public static class DpapiSecret
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ProcessorApplication/os-sandbox/v1");

    public static bool IsProtected(string? value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        return Prefix + Convert.ToBase64String(Crypt(data, protect: true));
    }

    /// <summary>The plain password, or null when the value is empty or can't be decrypted by this account.</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!IsProtected(stored)) return stored; // old plain-text value
        try
        {
            var data = Convert.FromBase64String(stored.Substring(Prefix.Length));
            return Encoding.UTF8.GetString(Crypt(data, protect: false));
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Crypt(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB();
        var entropyBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        try
        {
            inBlob = ToBlob(input);
            entropyBlob = ToBlob(Entropy);
            const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
            bool ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (entropyBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entropyBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var blob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
