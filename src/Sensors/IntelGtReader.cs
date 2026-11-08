using PCimple.Monitoring;
#if PAWNIO
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using LibreHardwareMonitor.PawnIo;
using Microsoft.Win32.SafeHandles;
#endif

namespace PCimple.Sensors;

internal sealed class IntelGtReader : IDisposable
{
#if PAWNIO
    private const uint LoadModule = 0xA1B22084;
    private const uint ExecuteFunction = 0xA1B22104;
    private const int GraphicsThermalStatus = 0x59C0;
    private const int TemperatureTargetMsr = 0x1A2;
    private const string GraphicsModule = "PCimple.IntelMCHBAR.bin";
    private readonly bool _supported = IsRaptorLakeMobileCpu();
    private SafeFileHandle? _graphics;
    private uint _target;
    private long _retryAfter;
#endif

    public IntelGtSample? Read()
    {
#if PAWNIO
        if (!_supported)
            return null;

        if (_graphics is null)
        {
            long now = Environment.TickCount64;
            if (now < _retryAfter)
                return null;
            _retryAfter = now + 30_000;

            using Stream? graphics = typeof(IntelGtReader).Assembly.GetManifestResourceStream(GraphicsModule);
            if (graphics is null || !TryReadTarget(out _target) || OpenModule(graphics) is not { } device)
                return null;
            _graphics = device;
        }

        if (TryReadGraphicsRegister(_graphics, out uint raw))
        {
            return IntelGt.DecodeTemperature(raw, _target) is double value
                ? new IntelGtSample(DateTimeOffset.UtcNow, value) : null;
        }

        Dispose();
#endif
        return null;
    }

    public void Dispose()
    {
#if PAWNIO
        _graphics?.Dispose();
        _graphics = null;
#endif
    }

#if PAWNIO
    private static bool IsRaptorLakeMobileCpu()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || !X86Base.IsSupported)
            return false;

        var vendor = X86Base.CpuId(0, 0);
        if (vendor.Ebx != 0x756E6547 || vendor.Edx != 0x49656E69 || vendor.Ecx != 0x6C65746E)
            return false;

        int identity = X86Base.CpuId(1, 0).Eax;
        int family = (identity >> 8) & 0xf;
        int model = ((identity >> 12) & 0xf0) | ((identity >> 4) & 0xf);
        return family == 6 && model == 0xBA;
    }

    private static bool TryReadTarget(out uint target)
    {
        var msr = new IntelMsr();
        try { return msr.ReadMsr(TemperatureTargetMsr, out target, out _); }
        finally { msr.Close(); }
    }

    private static SafeFileHandle? OpenModule(Stream module)
    {
        using var buffer = new MemoryStream();
        module.CopyTo(buffer);
        var device = CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, 0, 3, 0x80, 0);
        if (!device.IsInvalid && DeviceIoControl(device, LoadModule, buffer.ToArray(), (int)buffer.Length, null, 0, out _, 0))
            return device;

        device.Dispose();
        return null;
    }

    private static bool TryReadGraphicsRegister(SafeFileHandle device, out uint value)
    {
        // PawnIO expects a 32-byte function name followed by one 64-bit cell.
        byte[] input = new byte[40], output = new byte[8];
        "ioctl_read_dword"u8.CopyTo(input);
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(32), GraphicsThermalStatus);
        value = 0;
        if (!DeviceIoControl(device, ExecuteFunction, input, input.Length, output, output.Length, out int returned, 0) ||
            returned != output.Length)
            return false;

        value = BinaryPrimitives.ReadUInt32LittleEndian(output);
        return true;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
        [Out] byte[]? output, int outputSize, out int returned, nint overlapped);
#endif
}
