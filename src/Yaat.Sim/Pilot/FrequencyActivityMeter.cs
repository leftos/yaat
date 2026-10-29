namespace Yaat.Sim.Pilot;

/// <summary>
/// The frequency's load band, judged by the share of the last minute nobody was transmitting: Quiet at 70% dead air or
/// more, Normal from 35% up to 70%, Busy under 35%.
/// </summary>
public enum FrequencyLoadBand
{
    Quiet,
    Normal,
    Busy,
}

/// <summary>One transmission on the frequency: keyed at <see cref="Start"/> (sim seconds) for <see cref="Duration"/> seconds.</summary>
public readonly record struct FrequencyTransmission(double Start, double Duration)
{
    public double End => Start + Duration;
}

/// <summary>
/// The airtime on the active frequency over the trailing minute: every pilot and controller transmission as an interval,
/// read as the dead-air share of the window and the load band it falls in.
/// </summary>
public sealed class FrequencyActivityMeter
{
    public const double WindowSeconds = 60.0;

    /// <summary>The dead-air share at or above which the frequency is Quiet.</summary>
    public const double QuietDeadAirShare = 0.70;

    /// <summary>The dead-air share at or above which the frequency is Normal rather than Busy.</summary>
    public const double NormalDeadAirShare = 0.35;

    private readonly List<FrequencyTransmission> _transmissions = [];

    /// <summary>The recorded transmissions since the last trim, in the order they were recorded.</summary>
    public IReadOnlyList<FrequencyTransmission> Transmissions => [.. _transmissions];

    /// <summary>Records a transmission keyed at <paramref name="startSeconds"/> for <paramref name="durationSeconds"/>.</summary>
    public void Record(double startSeconds, double durationSeconds)
    {
        if (!double.IsFinite(startSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), startSeconds, "A transmission starts at a finite sim second.");
        }

        if (!double.IsFinite(durationSeconds) || (durationSeconds < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "A transmission lasts a finite, non-negative time.");
        }

        _transmissions.Add(new FrequencyTransmission(startSeconds, durationSeconds));
    }

    /// <summary>
    /// Replaces the recorded transmissions from a snapshot. The window is not trimmed here: the next <see cref="Trim"/>
    /// call trims it, exactly as the run being restored would have.
    /// </summary>
    public void Restore(IReadOnlyList<FrequencyTransmission> transmissions)
    {
        _transmissions.Clear();
        _transmissions.AddRange(transmissions);
    }

    /// <summary>
    /// The share of the <see cref="WindowSeconds"/> ending at <paramref name="elapsedSeconds"/> that no transmission
    /// occupied: one minus the length of the union of the transmissions clipped to the window, a transmission still on air
    /// counted up to <paramref name="elapsedSeconds"/>. Overlapping transmissions count once. The union is taken in a fixed
    /// order (by start, then duration), so the same transmissions always give the same share.
    /// </summary>
    public double DeadAirShare(double elapsedSeconds)
    {
        double windowStart = elapsedSeconds - WindowSeconds;
        double occupiedSeconds = 0;
        double coveredUntil = windowStart;
        foreach (FrequencyTransmission transmission in _transmissions.OrderBy(t => t.Start).ThenBy(t => t.Duration))
        {
            double start = Math.Max(transmission.Start, coveredUntil);
            double end = Math.Min(transmission.End, elapsedSeconds);
            if (end > start)
            {
                occupiedSeconds += end - start;
                coveredUntil = end;
            }
        }

        return 1.0 - (occupiedSeconds / WindowSeconds);
    }

    /// <summary>The load band of the window ending at <paramref name="elapsedSeconds"/>, read without trimming.</summary>
    public FrequencyLoadBand PeekLoadBand(double elapsedSeconds) => Classify(DeadAirShare(elapsedSeconds));

    /// <summary>Drops the transmissions that ended before the window ending at <paramref name="elapsedSeconds"/>.</summary>
    public void Trim(double elapsedSeconds)
    {
        double windowStart = elapsedSeconds - WindowSeconds;
        _transmissions.RemoveAll(t => t.End < windowStart);
    }

    private static FrequencyLoadBand Classify(double deadAirShare)
    {
        if (deadAirShare >= QuietDeadAirShare)
        {
            return FrequencyLoadBand.Quiet;
        }

        return (deadAirShare >= NormalDeadAirShare) ? FrequencyLoadBand.Normal : FrequencyLoadBand.Busy;
    }
}
