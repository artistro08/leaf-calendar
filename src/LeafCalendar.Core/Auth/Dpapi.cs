using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LeafCalendar.Core.Auth;

/// <summary>Windows DPAPI for the current user (CryptProtectData), so only this Windows account can read the data.</summary>
/// <seealso href="https://learn.microsoft.com/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata"/>
internal static partial class Dpapi
{
    private const int UiForbidden = 0x1;

    /// <summary>Encrypts <paramref name="plain"/> for the current Windows user, bound to <paramref name="entropy"/>.</summary>
    /// <exception cref="CryptographicException">Windows couldn't encrypt it.</exception>
    public static byte[] Protect(byte[] plain, byte[] entropy) => Run(plain, entropy, protect: true);

    /// <summary>Decrypts what <see cref="Protect"/> made, with the same <paramref name="entropy"/>.</summary>
    /// <exception cref="CryptographicException">The data is damaged, was made for another user, or with other entropy.</exception>
    public static byte[] Unprotect(byte[] data, byte[] entropy) => Run(data, entropy, protect: false);

    private static unsafe byte[] Run(byte[] input, byte[] entropy, bool protect)
    {
        fixed (byte* inputPtr = input)
        fixed (byte* entropyPtr = entropy)
        {
            var inBlob = new DataBlob { Size = input.Length, Data = (nint)inputPtr };
            var entropyBlob = new DataBlob { Size = entropy.Length, Data = (nint)entropyPtr };
            DataBlob outBlob;
            var ok = protect
                ? CryptProtectData(ref inBlob, 0, ref entropyBlob, 0, 0, UiForbidden, out outBlob)
                : CryptUnprotectData(ref inBlob, 0, ref entropyBlob, 0, 0, UiForbidden, out outBlob);

            // Nothing was allocated when the call failed
            if (!ok)
            {
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            }

            try
            {
                return new ReadOnlySpan<byte>((void*)outBlob.Data, outBlob.Size).ToArray();
            }
            finally
            {
                LocalFree(outBlob.Data);
            }
        }
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    // DATA_BLOB: a byte count and a pointer
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }
}
