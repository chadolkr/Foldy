using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Foldy.Providers.GoogleDrive;

/// <summary>
/// 현재 사용자 범위 DPAPI(CryptProtectData). 추가 NuGet 패키지 없이 crypt32를 직접 호출한다.
/// 스레드 안전.
/// </summary>
internal static class Dpapi
{
    private const uint CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static byte[] Protect(byte[] data, byte[] entropy) => Transform(data, entropy, protect: true);

    public static byte[] Unprotect(byte[] data, byte[] entropy) => Transform(data, entropy, protect: false);

    private static byte[] Transform(byte[] data, byte[] entropy, bool protect)
    {
        var input = Pin(data);
        var ent = Pin(entropy);
        try
        {
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref input, null, ref ent, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out output);
            if (!ok)
            {
                throw new CryptographicException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                _ = LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
            Marshal.FreeHGlobal(ent.pbData);
        }
    }

    private static DataBlob Pin(byte[] bytes)
    {
        var p = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return new DataBlob { cbData = bytes.Length, pbData = p };
    }
}
