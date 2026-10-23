namespace Yaat.Sim.Pilot;

public enum FrequencyActivityLevel
{
    Quiet,
    Moderate,
    Busy,
    Saturated,
}

/// <summary>The three-way frequency-load band an <see cref="FrequencyActivityLevel"/> collapses to.</summary>
public enum FrequencyLoadBand
{
    Quiet,
    Normal,
    Busy,
}

public static class FrequencyActivityLevelExtensions
{
    /// <summary>Maps the four activity levels onto three bands: Moderate is Normal, Busy and Saturated are Busy.</summary>
    public static FrequencyLoadBand ToLoadBand(this FrequencyActivityLevel level) =>
        level switch
        {
            FrequencyActivityLevel.Quiet => FrequencyLoadBand.Quiet,
            FrequencyActivityLevel.Moderate => FrequencyLoadBand.Normal,
            FrequencyActivityLevel.Busy or FrequencyActivityLevel.Saturated => FrequencyLoadBand.Busy,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown frequency activity level."),
        };
}

public sealed class FrequencyActivityMeter
{
    private const double WindowSeconds = 60.0;
    private readonly Queue<double> _transmissionTimes = [];

    public FrequencyActivityLevel Level { get; private set; } = FrequencyActivityLevel.Quiet;

    public int Count => _transmissionTimes.Count;

    /// <summary>The recorded transmission times since the last trim, oldest first.</summary>
    public IReadOnlyList<double> TransmissionTimes => [.. _transmissionTimes];

    public void Record(double elapsedSeconds)
    {
        _transmissionTimes.Enqueue(elapsedSeconds);
        Trim(elapsedSeconds);
        Level = Classify(_transmissionTimes.Count);
    }

    /// <summary>
    /// Replaces the recorded transmission times from a snapshot. The window is not trimmed here: the next
    /// <see cref="Record"/> or <see cref="Trim"/> call trims it, exactly as the run being restored would have.
    /// </summary>
    public void Restore(IReadOnlyList<double> transmissionTimes)
    {
        _transmissionTimes.Clear();
        foreach (double transmissionTime in transmissionTimes)
        {
            _transmissionTimes.Enqueue(transmissionTime);
        }

        Level = Classify(_transmissionTimes.Count);
    }

    public void Trim(double elapsedSeconds)
    {
        while (_transmissionTimes.Count > 0 && (elapsedSeconds - _transmissionTimes.Peek()) >= WindowSeconds)
        {
            _transmissionTimes.Dequeue();
        }

        Level = Classify(_transmissionTimes.Count);
    }

    private static FrequencyActivityLevel Classify(int count) =>
        count switch
        {
            < 5 => FrequencyActivityLevel.Quiet,
            <= 12 => FrequencyActivityLevel.Moderate,
            <= 20 => FrequencyActivityLevel.Busy,
            _ => FrequencyActivityLevel.Saturated,
        };
}
