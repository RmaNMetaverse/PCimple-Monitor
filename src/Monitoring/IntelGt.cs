namespace PCimple.Monitoring;

public sealed record IntelGtSample(DateTimeOffset SampledAt, double Celsius);

public static class IntelGt
{
    public const uint VendorId = 0x8086;

    // Raptor Lake-P/H device IDs documented by Intel's compute runtime.
    public static bool IsRaptorLakeMobile(uint deviceId) =>
        deviceId is 0xA720 or 0xA721 or >= 0xA7A0 and <= 0xA7A1 or >= 0xA7A8 and <= 0xA7AD;

    public static double? DecodeTemperature(uint thermalStatus, uint temperatureTarget)
    {
        // Intel datasheets 767624 and 743844. GT reports degrees below the factory TjMax.
        if ((thermalStatus & 0x80000000) == 0 || (thermalStatus & 0x78000000) != 0x08000000)
            return null;

        int junctionLimit = (int)((temperatureTarget >> 16) & 0xff);
        int offset = (int)((thermalStatus >> 16) & 0xff);
        int celsius = junctionLimit - offset;
        return junctionLimit is >= 70 and <= 125 && celsius is > 0 and <= 125 ? celsius : null;
    }
}
