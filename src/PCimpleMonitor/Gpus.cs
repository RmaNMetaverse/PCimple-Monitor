using System.Runtime.InteropServices;
using PCimple.Monitoring;

namespace PCimple;

internal sealed unsafe class Gpus : IDisposable
{
    private Nvidia? _nvidia;
    private DateTimeOffset _nextNvidiaProbe;
    private IntelGraphics? _intel;
    private DateTimeOffset _nextIntelProbe;

    public Reading[] Read(IntelGtSample? intelGt)
    {
        var adapters = Enumerate();
        var now = DateTimeOffset.UtcNow;
        if (_nvidia is null && now >= _nextNvidiaProbe && adapters.Any(a => a.Vendor == 0x10DE))
        {
            _nvidia = Nvidia.TryLoad();
            _nextNvidiaProbe = now.AddSeconds(30);
        }
        var nvidiaAdapters = _nvidia?.Enumerate() ?? [];
        bool onlyOneNvidia = nvidiaAdapters.Length == 1 && adapters.Count(a => a.Vendor == 0x10DE) == 1;
        if (_intel is null && now >= _nextIntelProbe &&
            adapters.Any(adapter => adapter.Vendor == IntelGt.VendorId && !adapter.WindowsTemperature.HasValue))
        {
            _intel = IntelGraphics.TryLoad();
            _nextIntelProbe = now.AddSeconds(30);
        }
        var intelTemperatures = _intel?.Read();
        var gtAdapters = adapters.Where(adapter => adapter.SinglePhysical && adapter.Vendor == IntelGt.VendorId &&
            IntelGt.IsRaptorLakeMobile(adapter.Device) &&
            (adapter.Address is null or { Bus: 0, Device: 2, Function: 0 })).ToArray();
        var gtAdapter = gtAdapters.Length == 1 ? gtAdapters[0] : null;
        return [.. adapters.Select(adapter =>
        {
            DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
            double? temperature = adapter.WindowsTemperature;
            string detail = temperature.HasValue ? "Main GPU temperature via Windows graphics driver." :
                "The graphics driver did not report a main GPU temperature.";
            if (_nvidia is not null && adapter.Vendor == 0x10DE)
            {
                var matches = nvidiaAdapters.Where(gpu => adapter.Address is { } address &&
                    gpu.Location == (address.Bus, address.Device)).ToArray();
                var match = matches.Length == 1 ? matches[0] :
                    onlyOneNvidia && (adapter.Address is null || nvidiaAdapters[0].Location is null) ? nvidiaAdapters[0] : null;
                if (match is not null && _nvidia.Temperature(match.Handle) is { } vendorTemperature)
                {
                    temperature = vendorTemperature;
                    detail = "Main GPU temperature via NVIDIA NVAPI.";
                }
            }
            if (adapter.Vendor == IntelGt.VendorId && !temperature.HasValue)
            {
                if (adapter.SinglePhysical && intelTemperatures?.TryGetValue(adapter.Luid, out double vendorTemperature) == true)
                {
                    temperature = vendorTemperature;
                    detail = "Main GPU temperature via Intel Graphics Control Library.";
                }
                else if (ReferenceEquals(adapter, gtAdapter) && intelGt is { Celsius: > 0 and <= 125 } gt &&
                    gt.SampledAt <= sampledAt && sampledAt - gt.SampledAt <= TimeSpan.FromSeconds(8))
                {
                    temperature = gt.Celsius;
                    sampledAt = gt.SampledAt;
                    detail = "Intel integrated graphics (GT) temperature via the processor's thermal sensor.";
                }
                else
                {
                    detail = "A separate Intel GPU temperature was unavailable from the graphics driver and supported hardware sensors.";
                }
            }
            return new Reading(adapter.Id, adapter.Name, HardwareKind.Gpu, sampledAt, temperature, Detail: detail);
        })];
    }

    private static List<Adapter> Enumerate()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in iid, out nint factory));
        var adapters = new List<Adapter>();
        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)VTable(factory)[12];
            for (uint i = 0; ; i++)
            {
                nint adapter;
                int status = enumAdapters(factory, i, &adapter);
                if (status == unchecked((int)0x887A0002)) // DXGI_ERROR_NOT_FOUND
                    break;
                Marshal.ThrowExceptionForHR(status);
                try
                {
                    var desc = new AdapterDescription();
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)VTable(adapter)[10];
                    if (getDescription(adapter, &desc) < 0 || (desc.Flags & 3) != 0)
                        continue; // Skip remote and software adapters.

                    string name = new string(desc.Description).Trim();
                    var open = new OpenAdapter { Luid = desc.Luid };
                    string id = $"{desc.Luid.High:X8}:{desc.Luid.Low:X8}";
                    ulong luid = ((ulong)(uint)desc.Luid.High << 32) | desc.Luid.Low;
                    if (D3DKMTOpenAdapterFromLuid(ref open) != 0)
                    {
                        adapters.Add(new Adapter(id, name, desc.Vendor, desc.Device, luid, true, null, null));
                        continue;
                    }

                    try
                    {
                        uint count = 1;
                        if (!Query(open.Handle, 30, &count, sizeof(uint)) || count is 0 or > 32)
                            count = 1;

                        var address = new AdapterAddress();
                        bool hasAddress = count == 1 && Query(open.Handle, 6, &address, sizeof(AdapterAddress));
                        for (uint index = 0; index < count; index++)
                        {
                            var performance = new AdapterPerformance { Index = index };
                            bool valid = Query(open.Handle, 62, &performance, sizeof(AdapterPerformance)) &&
                                performance.Temperature is > 0 and <= 2000;
                            adapters.Add(new Adapter(count == 1 ? id : $"{id}:{index}",
                                count == 1 ? name : $"{name} ({index + 1})", desc.Vendor, desc.Device, luid, count == 1,
                                hasAddress ? address : null,
                                valid ? performance.Temperature / 10.0 : null));
                        }
                    }
                    finally { D3DKMTCloseAdapter(ref open.Handle); }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return adapters;
    }

    private static nint* VTable(nint instance) => *(nint**)instance;

    private static bool Query(uint handle, int kind, void* output, int bytes)
    {
        var query = new AdapterQuery { Handle = handle, Kind = kind, Data = output, Size = bytes };
        return D3DKMTQueryAdapterInfo(ref query) == 0;
    }

    public void Dispose()
    {
        _nvidia?.Dispose();
        _intel?.Dispose();
    }

    private sealed record Adapter(string Id, string Name, uint Vendor, uint Device, ulong Luid, bool SinglePhysical,
        AdapterAddress? Address, double? WindowsTemperature);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        public fixed char Description[128];
        public uint Vendor, Device, Subsystem, Revision;
        public nuint VideoMemory, SystemMemory, SharedMemory;
        public Luid Luid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapter { public Luid Luid; public uint Handle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterAddress { public uint Bus, Device, Function; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct AdapterPerformance
    {
        [FieldOffset(0)] public uint Index;
        [FieldOffset(56)] public uint Temperature;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterQuery { public uint Handle; public int Kind; public void* Data; public int Size; }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter adapter);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTQueryAdapterInfo(ref AdapterQuery query);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int D3DKMTCloseAdapter(ref uint handle);

    private sealed class Nvidia(nint library, delegate* unmanaged[Cdecl]<uint, nint> query) : IDisposable
    {
        public static Nvidia? TryLoad()
        {
            string name = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "nvapia64.dll",
                Architecture.X86 => "nvapi.dll",
                _ => "nvapi64.dll"
            };
            if (!NativeLibrary.TryLoad(name, typeof(Nvidia).Assembly, DllImportSearchPath.System32, out nint library))
                return null;

            if (NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out nint export))
            {
                var query = (delegate* unmanaged[Cdecl]<uint, nint>)export;
                var initialize = (delegate* unmanaged[Cdecl]<int>)query(0x0150E828);
                if (initialize != null && initialize() == 0)
                    return new Nvidia(library, query);
            }

            NativeLibrary.Free(library);
            return null;
        }

        public PhysicalGpu[] Enumerate()
        {
            var enumerate = (delegate* unmanaged[Cdecl]<nint*, int*, int>)query(0xE5AC921F);
            var getBus = (delegate* unmanaged[Cdecl]<nint, uint*, int>)query(0x1BE0B8E5);
            var getSlot = (delegate* unmanaged[Cdecl]<nint, uint*, int>)query(0x2A0A350F);
            nint* handles = stackalloc nint[64];
            int count = 0;
            if (enumerate == null || enumerate(handles, &count) != 0 || count is < 1 or > 64)
                return [];

            var devices = new PhysicalGpu[count];
            for (int i = 0; i < count; i++)
            {
                uint bus = 0, slot = 0;
                bool found = getBus != null && getSlot != null && getBus(handles[i], &bus) == 0 &&
                    getSlot(handles[i], &slot) == 0 && bus <= 255 && slot <= 31;
                devices[i] = new PhysicalGpu(handles[i], found ? (bus, slot) : null);
            }
            return devices;
        }

        public double? Temperature(nint handle)
        {
            var thermal = (delegate* unmanaged[Cdecl]<nint, uint, int*, int>)query(0xE3640A56);
            if (thermal == null)
                return null;

            const int words = 17;
            int* settings = stackalloc int[words];
            new Span<int>(settings, words).Clear();
            settings[0] = (2 << 16) | (words * sizeof(int));
            if (thermal(handle, 15, settings) != 0 || settings[1] is < 1 or > 3)
                return null;

            for (int i = 0; i < settings[1]; i++)
            {
                int offset = 2 + i * 5;
                if (settings[offset + 4] == 1 && settings[offset + 3] is > 0 and <= 200)
                    return settings[offset + 3];
            }
            return null;
        }

        public void Dispose()
        {
            var unload = (delegate* unmanaged[Cdecl]<int>)query(0xD22BDD7E);
            if (unload != null)
                unload();
            NativeLibrary.Free(library);
        }

        public sealed record PhysicalGpu(nint Handle, (uint Bus, uint Slot)? Location);
    }
}
