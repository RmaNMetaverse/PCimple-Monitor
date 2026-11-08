using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PCimple.Monitoring;

public static class DeviceFile
{
    public static SafeFileHandle Open(string path, bool read = false) =>
        CreateFileW(path, read ? 0x80000000u : 0, 3, 0, 3, 0, 0);

    public static int Query(SafeFileHandle handle, uint code, byte[]? request, byte[] result) =>
        DeviceIoControl(handle, code, request, request?.Length ?? 0, result, result.Length, out int length, 0)
            ? length : 0;

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize,
        [Out] byte[] output, int outputSize, out int returned, nint overlapped);
}
