using System.Runtime.InteropServices;

namespace PCimple;

// ABI from Intel's igcl_api.h. Temperature telemetry is a 64-bit API.
internal sealed unsafe class IntelGraphics : IDisposable
{
    private readonly nint _library;
    private readonly delegate* unmanaged<nint, uint*, nint*, int> _enumerate;
    private readonly delegate* unmanaged<nint, AdapterProperties*, int> _properties;
    private readonly delegate* unmanaged<nint, PowerTelemetry*, int> _telemetry;
    private readonly delegate* unmanaged<nint, int> _close;
    private readonly nint _api;

    private IntelGraphics(nint library, nint api)
    {
        _library = library;
        _api = api;
        _enumerate = (delegate* unmanaged<nint, uint*, nint*, int>)NativeLibrary.GetExport(library, "ctlEnumerateDevices");
        _properties = (delegate* unmanaged<nint, AdapterProperties*, int>)NativeLibrary.GetExport(library, "ctlGetDeviceProperties");
        _telemetry = (delegate* unmanaged<nint, PowerTelemetry*, int>)NativeLibrary.GetExport(library, "ctlPowerTelemetryGet");
        _close = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(library, "ctlClose");
    }

    public static IntelGraphics? TryLoad()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !NativeLibrary.TryLoad("ControlLib.dll", typeof(IntelGraphics).Assembly, DllImportSearchPath.System32, out nint library))
            return null;

        foreach (string name in new[] { "ctlInit", "ctlClose", "ctlEnumerateDevices", "ctlGetDeviceProperties", "ctlPowerTelemetryGet" })
        {
            if (!NativeLibrary.TryGetExport(library, name, out _))
            {
                NativeLibrary.Free(library);
                return null;
            }
        }

        var init = (delegate* unmanaged<InitArgs*, nint*, int>)NativeLibrary.GetExport(library, "ctlInit");
        var args = new InitArgs { Size = (uint)sizeof(InitArgs), AppVersion = 1 << 16, Flags = 1 };
        nint api = 0;
        if (init(&args, &api) == 0 && api != 0)
            return new IntelGraphics(library, api);
        NativeLibrary.Free(library);
        return null;
    }

    public Dictionary<ulong, double> Read()
    {
        var result = new Dictionary<ulong, double>();
        uint count = 0;
        if (_enumerate(_api, &count, null) != 0 || count is 0 or > 64)
            return result;

        nint* adapters = stackalloc nint[(int)count];
        uint capacity = count;
        if (_enumerate(_api, &count, adapters) != 0 || count > capacity)
            return result;

        for (int i = 0; i < count; i++)
        {
            ulong luid = 0;
            var properties = new AdapterProperties { Size = (uint)sizeof(AdapterProperties), Luid = &luid, IdSize = sizeof(ulong) };
            if (_properties(adapters[i], &properties) != 0 || properties.Vendor != 0x8086 || luid == 0)
                continue;

            var telemetry = new PowerTelemetry { Size = (uint)sizeof(PowerTelemetry), Version = 1 };
            if (_telemetry(adapters[i], &telemetry) != 0 || telemetry.Supported == 0 || telemetry.Units != 5)
                continue;

            double value = telemetry.Type switch
            {
                0 => (sbyte)telemetry.Bits,
                1 => (byte)telemetry.Bits,
                2 => (short)telemetry.Bits,
                3 => (ushort)telemetry.Bits,
                4 => (int)telemetry.Bits,
                5 => (uint)telemetry.Bits,
                6 => telemetry.Bits,
                7 => (ulong)telemetry.Bits,
                8 => BitConverter.Int32BitsToSingle((int)telemetry.Bits),
                9 => BitConverter.Int64BitsToDouble(telemetry.Bits),
                _ => double.NaN
            };
            if (value is > 0 and <= 150)
                result[luid] = value;
        }
        return result;
    }

    public void Dispose()
    {
        _close(_api);
        NativeLibrary.Free(_library);
    }

    [StructLayout(LayoutKind.Explicit, Size = 36)]
    private struct InitArgs
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(8)] public uint AppVersion;
        [FieldOffset(12)] public uint Flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 320)]
    private struct AdapterProperties
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(8)] public ulong* Luid;
        [FieldOffset(16)] public uint IdSize;
        [FieldOffset(64)] public uint Vendor;
    }

    // Offsets 104–127 are the gpuCurrentTemperature item within ctl_power_telemetry_t.
    [StructLayout(LayoutKind.Explicit, Size = 1024)]
    private struct PowerTelemetry
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public byte Version;
        [FieldOffset(104)] public byte Supported;
        [FieldOffset(108)] public int Units;
        [FieldOffset(112)] public int Type;
        [FieldOffset(120)] public long Bits;
    }
}
