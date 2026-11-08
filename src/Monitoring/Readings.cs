namespace PCimple.Monitoring;

public enum HardwareKind { Cpu, Gpu, Drive, Battery }
public enum DriveHealth { Unavailable, Healthy, Warning, Critical }

public sealed record Reading(
    string Id, string Name, HardwareKind Kind, DateTimeOffset SampledAt,
    double? Celsius = null, string? DriveType = null, DriveHealth DriveHealth = DriveHealth.Unavailable,
    double? BatteryHealth = null, string? Detail = null);

public sealed record Snapshot(int Version, Reading[]? Cpu, Reading[]? Drives, IntelGtSample? IntelGraphics = null)
{
    public const int ProtocolVersion = 1;
    public const string PipeName = "PCimpleMonitor.Sensors.v1";
    public const int MaxBytes = 128 * 1024;
}

public sealed record Summary(Reading Reading, double? Average, double? Peak, bool IsFresh);

public sealed class Session
{
    private readonly Dictionary<(HardwareKind Kind, string Id), Entry> _entries = [];
    private DateTimeOffset _resetAt = DateTimeOffset.MinValue;
    private DateTimeOffset _freshAfter = DateTimeOffset.MinValue;

    public void Accept(HardwareKind kind, IEnumerable<Reading> readings, DateTimeOffset now)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reading in readings)
        {
            if (reading.Kind != kind || reading.SampledAt > now || !present.Add(reading.Id))
                continue;

            var key = (kind, reading.Id);
            if (!_entries.TryGetValue(key, out var entry))
                _entries[key] = entry = new Entry(reading);
            entry.Present = true;
            if (entry.LastSeen >= reading.SampledAt)
                continue;

            entry.Reading = reading;
            entry.LastSeen = reading.SampledAt;
            if (reading.SampledAt > _resetAt && IsFresh(reading, now) && ValidTemperature(reading.Celsius))
            {
                double value = reading.Celsius!.Value;
                entry.Count++;
                entry.Average += (value - entry.Average) / entry.Count;
                entry.Peak = Math.Max(entry.Peak, value);
            }
        }

        foreach (var (key, entry) in _entries)
            if (key.Kind == kind && !present.Contains(key.Id))
                entry.Present = false;
    }

    public void Reset(DateTimeOffset now)
    {
        _resetAt = now;
        foreach (var entry in _entries.Values)
        {
            entry.Count = 0;
            entry.Average = 0;
            entry.Peak = double.NegativeInfinity;
        }
    }

    public IEnumerable<Summary> Read(DateTimeOffset now) => _entries.Values
        .Where(entry => entry.Present)
        .OrderBy(entry => entry.Reading.Kind)
        .ThenBy(entry => entry.Reading.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(entry =>
        {
            bool fresh = IsFresh(entry.Reading, now);
            bool show = fresh && entry.Count > 0 && ValidTemperature(entry.Reading.Celsius);
            return new Summary(entry.Reading, show ? entry.Average : null, show ? entry.Peak : null, fresh);
        });

    public void RequireFreshSamples(DateTimeOffset now) => _freshAfter = now;

    private bool IsFresh(Reading reading, DateTimeOffset now) =>
        reading.SampledAt > _freshAfter && reading.SampledAt <= now &&
        now - reading.SampledAt <= TimeSpan.FromSeconds(reading.Kind switch
        {
            HardwareKind.Cpu or HardwareKind.Gpu => 8,
            HardwareKind.Drive => 50,
            _ => 90
        });

    private static bool ValidTemperature(double? value) => value is >= -50 and <= 200;

    private sealed class Entry(Reading reading)
    {
        public Reading Reading = reading;
        public bool Present;
        public DateTimeOffset LastSeen = DateTimeOffset.MinValue;
        public long Count;
        public double Average;
        public double Peak = double.NegativeInfinity;
    }
}

public static class BatteryCapacity
{
    public static double? Percent(uint design, uint full) =>
        design is 0 or uint.MaxValue || full is 0 or uint.MaxValue
            ? null : Math.Min(100, 100.0 * full / design);
}
