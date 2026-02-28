namespace Yaat.Sim.Data.Vnas;

/// <summary>
/// AIRAC (Aeronautical Information Regulation And
/// Control) cycle calculator. Cycles are 28 days apart
/// and numbered YYNN (2-digit year + cycle within year).
/// </summary>
public static class AiracCycle
{
    // First AIRAC date of 2025: January 23, 2025
    private static readonly DateOnly Epoch = new(2025, 1, 23);

    private const int CycleDays = 28;
    private const int CyclesPerYear = 13;

    /// <summary>
    /// Returns the AIRAC cycle identifier (e.g. "2501")
    /// that is effective on the given date.
    /// </summary>
    public static string GetCycleId(DateOnly date)
    {
        int totalDays = date.DayNumber - Epoch.DayNumber;
        if (totalDays < 0)
        {
            return "2501";
        }

        int cycleIndex = totalDays / CycleDays;
        int year = 2025 + cycleIndex / CyclesPerYear;
        int cycleInYear = cycleIndex % CyclesPerYear + 1;

        int yearSuffix = year % 100;
        return $"{yearSuffix:D2}{cycleInYear:D2}";
    }

    /// <summary>
    /// Returns the effective date of the given AIRAC cycle.
    /// </summary>
    public static DateOnly GetCycleDate(string cycleId)
    {
        if (cycleId.Length != 4
            || !int.TryParse(cycleId[..2], out int yy)
            || !int.TryParse(cycleId[2..], out int nn))
        {
            return Epoch;
        }

        int year = 2000 + yy;
        int yearsFromEpoch = year - 2025;
        int totalCycles = yearsFromEpoch * CyclesPerYear + (nn - 1);

        return Epoch.AddDays(totalCycles * CycleDays);
    }

    /// <summary>
    /// Returns the current AIRAC cycle identifier.
    /// </summary>
    public static string GetCurrentCycleId()
    {
        return GetCycleId(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    /// <summary>
    /// Returns the effective date of the next AIRAC cycle
    /// after the given date.
    /// </summary>
    public static DateOnly GetNextCycleDate(DateOnly date)
    {
        int totalDays = date.DayNumber - Epoch.DayNumber;
        int cycleIndex = totalDays < 0 ? 0 : totalDays / CycleDays + 1;

        return Epoch.AddDays(cycleIndex * CycleDays);
    }
}
