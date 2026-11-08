using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using PCimple.Monitoring;

namespace PCimple;

internal static class Batteries
{
    private const uint QueryTag = 0x294040, QueryInformation = 0x294044;
    private static readonly Guid BatteryInterface = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");

    public static Reading[] Read()
    {
        var guid = BatteryInterface;
        nint set = SetupDiGetClassDevsW(ref guid, 0, 0, 0x12);
        if (set == -1)
            throw new System.ComponentModel.Win32Exception();
        try
        {
            var results = new List<Reading>();
            for (uint index = 0; ; index++)
            {
                var data = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, 0, ref guid, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() != 259)
                        throw new System.ComponentModel.Win32Exception();
                    break;
                }
                SetupDiGetDeviceInterfaceDetailW(set, ref data, 0, 0, out int size, 0);
                if (size < 8 || size > 65536)
                    continue;

                nint detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, size, out _, 0))
                        continue;
                    string? path = Marshal.PtrToStringUni(detail + 4);
                    if (path is not null && ReadPack(path, results.Count + 1) is { } battery)
                        results.Add(battery);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
            if (results.Count > 0)
                return [.. results];

            var status = System.Windows.Forms.SystemInformation.PowerStatus.BatteryChargeStatus;
            return status != System.Windows.Forms.BatteryChargeStatus.Unknown &&
                !status.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                ? [new Reading("system-battery", "System battery", HardwareKind.Battery, DateTimeOffset.UtcNow,
                    Detail: "Windows reported a battery but its controller did not provide capacity details.")]
                : [];
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static Reading? ReadPack(string path, int ordinal)
    {
        using var device = DeviceFile.Open(path, read: true);
        if (device.IsInvalid)
            return null;
        byte[] tag = new byte[4];
        if (DeviceFile.Query(device, QueryTag, new byte[4], tag) != 4 || BitConverter.ToUInt32(tag) == 0)
            return null;

        byte[] request = new byte[12];
        tag.CopyTo(request, 0);
        byte[] data = new byte[36];
        if (DeviceFile.Query(device, QueryInformation, request, data) < data.Length)
            return null;
        uint capabilities = BitConverter.ToUInt32(data);
        if ((capabilities & 0xA0000000) != 0x80000000)
            return null; // System battery, excluding short-term UPS devices.

        uint design = BitConverter.ToUInt32(data, 12);
        uint full = BitConverter.ToUInt32(data, 16);
        double? percent = BatteryCapacity.Percent(design, full);
        string name = ReadName(device, request) ?? $"Battery {ordinal}";
        string explanation = percent is null
            ? "The battery controller did not report both capacities."
            : "Full-charge capacity compared with design capacity. PCimple Monitor marks capacity as reduced below 80%.";
        return new Reading(path, name, HardwareKind.Battery, DateTimeOffset.UtcNow, BatteryHealth: percent, Detail: explanation);
    }

    private static string? ReadName(Microsoft.Win32.SafeHandles.SafeFileHandle device, byte[] request)
    {
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(4), 4); // BatteryDeviceName
        byte[] bytes = new byte[512];
        int length = DeviceFile.Query(device, QueryInformation, request, bytes);
        string name = Encoding.Unicode.GetString(bytes, 0, length & ~1).TrimEnd('\0').Trim();
        return name.Length == 0 ? null : name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData
    {
        public int Size;
        public Guid ClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(ref Guid guid, nint enumerator, nint parent, uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(nint set, nint device, ref Guid guid, uint index, ref InterfaceData data);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(nint set, ref InterfaceData data, nint detail, int length, out int required, nint device);

    [DllImport("setupapi.dll", ExactSpelling = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
