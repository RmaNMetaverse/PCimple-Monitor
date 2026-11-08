using PCimple.Monitoring;

var origin = DateTimeOffset.UnixEpoch.AddDays(1);
DateTimeOffset At(int seconds) => origin.AddSeconds(seconds);
Reading Cpu(int seconds, double? value, string id = "cpu0") => new(id, id, HardwareKind.Cpu, At(seconds), value);
Reading Gpu(int seconds, double? value, string id) => new(id, id, HardwareKind.Gpu, At(seconds), value);
Reading Drive(int seconds, double? value, string id) => new(id, id, HardwareKind.Drive, At(seconds), value);

var session = new Session();
session.Reset(At(0));
session.Accept(HardwareKind.Cpu, [Cpu(0, 99)], At(1));
Equal(null, session.Read(At(1)).Single().Average, "Cached pre-launch value");
session.Accept(HardwareKind.Cpu, [Cpu(1, 40)], At(1));
session.Accept(HardwareKind.Cpu, [Cpu(2, 60)], At(2));
session.Accept(HardwareKind.Cpu, [Cpu(2, 160)], At(3));
Equal(50.0, session.Read(At(3)).Single().Average, "Average excludes duplicate timestamps");
Equal(60.0, session.Read(At(3)).Single().Peak, "Peak excludes duplicate timestamps");
session.Accept(HardwareKind.Cpu, [Cpu(3, double.NaN)], At(3));
Equal(null, session.Read(At(3)).Single().Average, "Invalid sample is unavailable");
session.Accept(HardwareKind.Cpu, [Cpu(4, null)], At(4));
Equal(null, session.Read(At(4)).Single().Average, "Missing sample is unavailable");
session.Accept(HardwareKind.Cpu, [Cpu(5, 50)], At(5));
Equal(50.0, session.Read(At(5)).Single().Average, "Invalid samples leave history intact");
Equal(null, session.Read(At(20)).Single().Peak, "A provider failure ages out without a new sample");

session.RequireFreshSamples(At(21));
session.Accept(HardwareKind.Cpu, [Cpu(20, 190)], At(21));
Equal(null, session.Read(At(21)).Single().Average, "Resume rejects cached samples");
session.Accept(HardwareKind.Cpu, [Cpu(22, 90)], At(22));
Equal(60.0, session.Read(At(22)).Single().Average, "Resume preserves session");
Equal(90.0, session.Read(At(22)).Single().Peak, "Resume records new peak");

session.Accept(HardwareKind.Gpu, [Gpu(22, 55, "igpu"), Gpu(22, 68, "dgpu")], At(22));
session.Accept(HardwareKind.Drive, [Drive(22, 31, "ssd"), Drive(22, 28, "hdd")], At(22));
Equal(5, session.Read(At(22)).Count(), "Multiple adapters and physical drives");
session.Accept(HardwareKind.Drive, [Drive(23, 29, "hdd")], At(23));
Equal(false, session.Read(At(23)).Any(row => row.Reading.Id == "ssd"), "Removed drive hides");
session.Accept(HardwareKind.Drive, [Drive(24, 35, "ssd"), Drive(24, 30, "hdd")], At(24));
Equal(33.0, session.Read(At(24)).Single(row => row.Reading.Id == "ssd").Average, "Reconnected drive preserves session");

session.Reset(At(24));
session.Accept(HardwareKind.Cpu, [Cpu(24, 105)], At(25));
Equal(true, session.Read(At(25)).All(row => row.Average is null && row.Peak is null), "Reset clears all views and cached data");
session.Accept(HardwareKind.Cpu, [Cpu(25, 41)], At(25));
Equal(41.0, session.Read(At(25)).First().Peak, "Only post-reset samples contribute");

Equal(80.0, BatteryCapacity.Percent(50000, 40000), "Battery capacity ratio");
Equal(100.0, BatteryCapacity.Percent(50000, 51000), "Battery health cap");
Equal(null, BatteryCapacity.Percent(0, 42000), "Missing design capacity");
Equal(null, BatteryCapacity.Percent(50000, uint.MaxValue), "Unknown full capacity");
Equal(43.0, IntelGt.DecodeTemperature(0x88390000, 0x00640000), "Graphics sensor uses its own thermal offset");
Equal(100.0, IntelGt.DecodeTemperature(0x88000000, 0x00640000), "Zero distance is a valid reading at TjMax");
Equal(null, IntelGt.DecodeTemperature(0x08390000, 0x00640000), "Graphics sensor must set its valid bit");
Equal(null, IntelGt.DecodeTemperature(0x88390000, 0), "Missing junction limit must not produce a temperature");
Equal(null, IntelGt.DecodeTemperature(uint.MaxValue, 0x00640000), "Unmapped register must not produce a temperature");
Equal(true, IntelGt.IsRaptorLakeMobile(0xA7A8), "Laptop UHD adapter identity");
Equal(false, IntelGt.IsRaptorLakeMobile(0x56A0), "Discrete Arc is not a CPU graphics sensor");
Console.WriteLine("Passed 24 monitoring assertions.");

static void Equal<T>(T expected, T actual, string test)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{test}: expected {expected}, got {actual}");
}
