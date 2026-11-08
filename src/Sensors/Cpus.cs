using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PCimple.Monitoring;
#if PAWNIO
using LibreHardwareMonitor.Hardware;
#endif

namespace PCimple.Sensors;

internal sealed class Cpus : IDisposable
{
    private Reading[] _inventory = [];
    private DateTimeOffset _nextInventory;
#if PAWNIO
    private Computer? _computer;
#endif

    public Reading[] Read()
    {
        var now = DateTimeOffset.UtcNow;
        if (now >= _nextInventory)
        {
            _inventory = ReadInventory();
            _nextInventory = now.AddMinutes(1);
        }
        var devices = _inventory.Select(device => device with { SampledAt = now }).ToArray();
#if PAWNIO
        if (RuntimeInformation.OSArchitecture == Architecture.X64 && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            try
            {
                var readings = ReadPawnIo();
                if (readings.Length > 0)
                {
                    // The library enumerates the installed sockets. Keep Windows inventory visible if it reports more.
                    return [.. readings, .. devices.Skip(readings.Length)];
                }
            }
            catch (Exception)
            {
                Dispose();
            }
        }
#endif
        return devices;
    }

    private static Reading[] ReadInventory()
    {
        string detail = CpuUnavailableReason();
        var devices = new List<Reading>();
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var query = new ManagementObjectSearcher("SELECT DeviceID, Name FROM Win32_Processor");
            using var rows = query.Get();
            foreach (ManagementObject row in rows)
            {
                using (row)
                    devices.Add(new Reading("cpu:" + row["DeviceID"], row["Name"]?.ToString()?.Trim() ?? "CPU",
                        HardwareKind.Cpu, now, Detail: detail));
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (COMException) { }

        return devices.Count > 0 ? [.. devices] : [new Reading("cpu", "CPU", HardwareKind.Cpu, now, Detail: detail)];
    }

    private static string CpuUnavailableReason()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            return "Windows did not expose a verified CPU package temperature for this device.";

        const string key = @"HKEY_LOCAL_MACHINE\SOFTWARE\PCimple Monitor";
        return Registry.GetValue(key, "PawnIOStatus", null) switch
        {
            "reboot" => "Restart Windows to finish enabling the CPU temperature sensor.",
            "failed" => "PawnIO setup failed. Run PCimple Monitor setup again to enable compatible CPU sensors.",
            "payload-error" or "install-error" => Registry.GetValue(key, "PawnIOError", null) as string
                ?? "PawnIO setup failed. See the PCimple Monitor setup log for details.",
            "skipped" => "CPU sensor access was skipped during setup.",
            _ => "CPU package temperature is unavailable. Check the PawnIO driver and restart Windows if setup requested it."
        };
    }

#if PAWNIO
    private Reading[] ReadPawnIo()
    {
        if (_computer is null)
        {
            _computer = new Computer { IsCpuEnabled = true };
            _computer.Open();
        }

        var readings = new List<Reading>();
        foreach (var cpu in _computer.Hardware.Where(hardware => hardware.HardwareType == HardwareType.Cpu))
        {
            // Some AMD updates retain the last Value if the sensor cannot be read.
            // Min is set again only when the provider writes a new value.
            foreach (var sensor in cpu.Sensors)
            {
                sensor.ValuesTimeWindow = TimeSpan.Zero;
                if (IsPackageSensor(sensor))
                    sensor.ResetMin();
            }
            cpu.Update();
            var fresh = cpu.Sensors.Where(sensor => IsPackageSensor(sensor) && sensor.Min.HasValue).ToArray();
            var package = fresh.FirstOrDefault(s => s.Name == "CPU Package")
                ?? fresh.FirstOrDefault(s => s.Name == "Core (Tdie)")
                ?? fresh.FirstOrDefault(s => s.Name == "Core (Tctl/Tdie)");
            double? value = package?.Value is >= -50 and <= 200 ? package.Value : null;
            readings.Add(new Reading(cpu.Identifier.ToString(), cpu.Name, HardwareKind.Cpu, DateTimeOffset.UtcNow,
                value, Detail: value is null ? CpuUnavailableReason() : "CPU package temperature via PawnIO."));
        }
        return [.. readings];
    }

    private static bool IsPackageSensor(ISensor sensor) =>
        sensor.SensorType == SensorType.Temperature && sensor.Name is "CPU Package" or "Core (Tdie)" or "Core (Tctl/Tdie)";
#endif

    public void Dispose()
    {
#if PAWNIO
        _computer?.Close();
        _computer = null;
#endif
    }
}
