using System.Buffers.Binary;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using PCimple.Monitoring;

namespace PCimple.Sensors;

internal static class Drives
{
    private const uint StorageQuery = 0x2D1400;
    private const uint PredictFailure = 0x2D1100;
    private const int DeviceTemperature = 52;
    private const int DeviceProtocol = 50;
    private const string Storage = @"root\Microsoft\Windows\Storage";

    public static Reading[] Read()
    {
        var physical = Query(Storage, "SELECT * FROM MSFT_PhysicalDisk", row =>
        {
            string path = row.Path.RelativePath;
            return new PhysicalDisk(Text(row, "UniqueId"), Text(row, "SerialNumber"), Text(row, "FriendlyName"),
                Number(row, "MediaType"), Number(row, "BusType"), WindowsHealth(row), path);
        });
        var windowsDisks = Query(Storage, "SELECT Number, UniqueId, SerialNumber, BusType FROM MSFT_Disk", row =>
            new WindowsDisk(Number(row, "Number"), Text(row, "UniqueId"), Text(row, "SerialNumber"), Number(row, "BusType")));
        var legacy = Query(@"root\cimv2", "SELECT Index, Model, PNPDeviceID, SerialNumber FROM Win32_DiskDrive", row =>
            new LegacyDisk(Number(row, "Index"), Text(row, "PNPDeviceID"), Text(row, "Model"), Text(row, "SerialNumber")));

        var claimed = new HashSet<int>();
        var result = new List<Reading>();
        foreach (var disk in physical)
        {
            if (IsVirtual(disk.Bus))
                continue;

            int number = FindNumber(disk, physical, windowsDisks, legacy);
            var legacyDisk = legacy.FirstOrDefault(d => d.Number == number);
            if (number >= 0)
                claimed.Add(number);

            // Traverse the Windows association. A provider's DeviceId is not a PhysicalDrive number.
            var temperatures = Query(Storage,
                $"ASSOCIATORS OF {{{disk.Path}}} WHERE AssocClass=MSFT_PhysicalDiskToStorageReliabilityCounter",
                row => Number(row, "Temperature"));
            int? temperature = temperatures.Count == 1 && temperatures[0] is > 0 and <= 150 ? temperatures[0] : null;
            string id = StableId(disk.UniqueId, legacyDisk?.Pnp, disk.Path);
            result.Add(ReadOne(id, disk.Name, disk.Bus, disk.Media, number, temperature, disk.Health));
        }

        foreach (var disk in legacy.Where(d => d.Number >= 0 && !claimed.Contains(d.Number)))
        {
            var windows = windowsDisks.FirstOrDefault(d => d.Number == disk.Number);
            if (IsVirtual(windows?.Bus ?? -1) || string.IsNullOrWhiteSpace(disk.Pnp))
                continue;
            // A physical provider entry with unresolved identity must never inherit another disk's telemetry.
            if (physical.Any(p => !IsVirtual(p.Bus) && !string.IsNullOrEmpty(p.Name) &&
                                  string.Equals(p.Name, disk.Name, StringComparison.OrdinalIgnoreCase) &&
                                  FindNumber(p, physical, windowsDisks, legacy) < 0))
                continue;
            result.Add(ReadOne(StableId(windows?.UniqueId, disk.Pnp, null), disk.Name,
                windows?.Bus ?? -1, -1, disk.Number, null, DriveHealth.Unavailable));
        }
        return [.. result];
    }

    private static Reading ReadOne(string id, string? name, int bus, int media, int number, double? temperature, DriveHealth health)
    {
        string? temperatureSource = temperature.HasValue ? "Windows storage reliability counter" : null;
        string? healthSource = health == DriveHealth.Unavailable ? null : "Windows storage health";
        if (number >= 0)
        {
            using var device = DeviceFile.Open(@"\\.\PhysicalDrive" + number);
            if (!device.IsInvalid)
            {
                if (bus < 0)
                    bus = ReadBusType(device);
                if (media is <= 0 && bus is 1 or 3 or 8 or 11)
                    media = ReadMediaType(device);
                if (bus == 17 && ReadNvme(device) is { } nvme)
                {
                    if (nvme.Temperature.HasValue)
                    {
                        temperature = nvme.Temperature;
                        temperatureSource = "NVMe SMART";
                    }
                    MergeHealth(nvme.Health, "NVMe SMART", ref health, ref healthSource);
                }
                else if (bus is -1 or 1 or 3 or 8 or 11)
                {
                    byte[] data = new byte[516];
                    if (DeviceFile.Query(device, PredictFailure, null, data) >= data.Length)
                        MergeHealth(BitConverter.ToUInt32(data) != 0 ? DriveHealth.Critical : DriveHealth.Healthy,
                            "Drive failure prediction", ref health, ref healthSource);
                }

                if (!temperature.HasValue && ReadDeviceTemperature(device) is { } native)
                {
                    temperature = native;
                    temperatureSource = "Windows device temperature";
                }
            }
        }

        string type = bus == 17 ? "NVMe SSD" : media switch { 3 => "Hard drive", 4 => "SSD", 5 => "Storage-class memory", _ => "Drive" };
        return new Reading(id, string.IsNullOrWhiteSpace(name) ? type : name, HardwareKind.Drive,
            DateTimeOffset.UtcNow, temperature, type, health,
            Detail: $"Temperature: {temperatureSource ?? "Unavailable"}. Health: {healthSource ?? "Unavailable"}.");
    }

    private static void MergeHealth(DriveHealth candidate, string source, ref DriveHealth health, ref string? healthSource)
    {
        if (candidate >= health && candidate != DriveHealth.Unavailable)
        {
            health = candidate;
            healthSource = source;
        }
    }

    private static (double? Temperature, DriveHealth Health)? ReadNvme(Microsoft.Win32.SafeHandles.SafeFileHandle device)
    {
        byte[] input = new byte[48 + 512];
        Write(input, 0, DeviceProtocol);
        Write(input, 8, 3);   // ProtocolTypeNvme
        Write(input, 12, 2);  // NVMeDataTypeLogPage
        Write(input, 16, 2);  // SMART / health page
        Write(input, 24, 40); // Data starts after STORAGE_PROTOCOL_SPECIFIC_DATA.
        Write(input, 28, 512);
        byte[] output = new byte[input.Length];
        int count = DeviceFile.Query(device, StorageQuery, input, output);
        if (count < 48 || BitConverter.ToInt32(output, 8) != 3 || BitConverter.ToInt32(output, 12) != 2)
            return null;
        int offset = BitConverter.ToInt32(output, 24);
        if (offset < 40 || offset > count - 8 - 512 || BitConverter.ToInt32(output, 28) < 512)
            return null;
        var log = output.AsSpan(8 + offset, 512);
        if (log.IndexOfAnyExcept((byte)0) < 0 || log.IndexOfAnyExcept(byte.MaxValue) < 0)
            return null;
        double celsius = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]) - 273.15;
        byte criticalWarning = log[0];
        var health = (criticalWarning & 0x3D) != 0 ? DriveHealth.Critical :
            criticalWarning != 0 || log[5] >= 100 ? DriveHealth.Warning : DriveHealth.Healthy;
        return (celsius is >= -50 and <= 150 ? celsius : null, health);
    }

    private static double? ReadDeviceTemperature(Microsoft.Win32.SafeHandles.SafeFileHandle device)
    {
        byte[] query = new byte[12];
        Write(query, 0, DeviceTemperature);
        byte[] output = new byte[1024];
        int count = DeviceFile.Query(device, StorageQuery, query, output);
        if (count < 40 || BitConverter.ToUInt16(output, 12) == 0 || BitConverter.ToUInt16(output, 24) != 0)
            return null;
        short celsius = BitConverter.ToInt16(output, 26);
        return celsius is >= -50 and <= 150 ? celsius : null;
    }

    private static int ReadBusType(Microsoft.Win32.SafeHandles.SafeFileHandle device)
    {
        byte[] data = new byte[1024];
        return DeviceFile.Query(device, StorageQuery, new byte[12], data) >= 36 ? BitConverter.ToInt32(data, 28) : -1;
    }

    private static int ReadMediaType(Microsoft.Win32.SafeHandles.SafeFileHandle device)
    {
        byte[] request = new byte[12], data = new byte[12];
        Write(request, 0, 7); // StorageDeviceSeekPenaltyProperty
        return DeviceFile.Query(device, StorageQuery, request, data) >= 9 ? data[8] == 0 ? 4 : 3 : -1;
    }

    private static void Write(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), value);

    private static bool IsVirtual(int bus) => bus is 14 or 15 or 16;

    private static DriveHealth WindowsHealth(ManagementObject row)
    {
        if (row["OperationalStatus"] is ushort[] states && states.Contains((ushort)5))
            return DriveHealth.Critical;
        return Number(row, "HealthStatus") switch
        {
            0 => DriveHealth.Healthy, 1 => DriveHealth.Warning, 2 => DriveHealth.Critical, _ => DriveHealth.Unavailable
        };
    }

    private static int FindNumber(PhysicalDisk disk, List<PhysicalDisk> physical, List<WindowsDisk> windows, List<LegacyDisk> legacy)
    {
        if (IsIdentity(disk.UniqueId) && physical.Count(other => Same(other.UniqueId, disk.UniqueId)) == 1)
        {
            var matches = windows.Where(other => Same(other.UniqueId, disk.UniqueId) && !IsVirtual(other.Bus)).ToArray();
            if (matches.Length == 1)
                return matches[0].Number;
        }
        if (IsIdentity(disk.Serial) && physical.Count(other => Same(other.Serial, disk.Serial)) == 1)
        {
            var matches = legacy.Where(other => Same(other.Serial, disk.Serial)).ToArray();
            if (matches.Length == 1)
                return matches[0].Number;
        }
        return -1;
    }

    private static bool IsIdentity(string? value) => !string.IsNullOrWhiteSpace(value) &&
        !value.All(c => c is '0' or ' ' or '-') && !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string StableId(string? uniqueId, string? pnp, string? instancePath)
    {
        var identifier = IsIdentity(uniqueId) ? uniqueId + "|" + (pnp ?? instancePath) : pnp ?? instancePath ?? "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier.ToUpperInvariant())));
    }

    private static List<T> Query<T>(string scope, string query, Func<ManagementObject, T> map)
    {
        var result = new List<T>();
        try
        {
            using var search = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), new System.Management.EnumerationOptions
            {
                ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(5)
            });
            using var rows = search.Get();
            foreach (ManagementObject row in rows)
                using (row)
                    result.Add(map(row));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // An unsupported or restricted provider must not block native reads from other devices.
        }
        return result;
    }

    private static int Number(ManagementObject row, string name) =>
        row[name] is { } value && Convert.ToInt64(value) is >= 0 and <= int.MaxValue and var number ? (int)number : -1;
    private static string? Text(ManagementObject row, string name) => row[name]?.ToString()?.Trim();

    private sealed record PhysicalDisk(string? UniqueId, string? Serial, string? Name, int Media, int Bus, DriveHealth Health, string Path);
    private sealed record WindowsDisk(int Number, string? UniqueId, string? Serial, int Bus);
    private sealed record LegacyDisk(int Number, string? Pnp, string? Name, string? Serial);
}
