namespace Yaat.Sim.Authoring.Spawns;

/// <summary>One control point of a workload curve: a percentage at a time from the scenario's start.</summary>
public sealed record WorkloadPoint(double TimeSec, double Percent);

/// <summary>A workload percentage sampled over a scenario's length, interpolated with a cosine ease between its control
/// points.</summary>
public sealed class WorkloadCurve
{
    /// <summary>The number of samples the curve holds.</summary>
    public const int SampleCount = 500;

    private readonly List<(double T, double W)> _keys;
    private readonly double[] _samples;

    /// <summary>A curve over <paramref name="lengthSec"/> from <paramref name="startPercent"/> to
    /// <paramref name="endPercent"/> through <paramref name="points"/>: percentages clamped to 0-100, times to the
    /// length, and the last point at a time winning.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The length, a percentage, or a point's time or percentage is not a
    /// finite number.</exception>
    public WorkloadCurve(double lengthSec, double startPercent, double endPercent, IEnumerable<WorkloadPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        ThrowIfNotFinite(lengthSec, nameof(lengthSec));
        ThrowIfNotFinite(startPercent, nameof(startPercent));
        ThrowIfNotFinite(endPercent, nameof(endPercent));
        List<WorkloadPoint> loaded = [.. points];
        for (int i = 0; i < loaded.Count; i++)
        {
            if ((!double.IsFinite(loaded[i].TimeSec)) || (!double.IsFinite(loaded[i].Percent)))
            {
                throw new ArgumentOutOfRangeException(nameof(points), loaded[i], $"Point {i} must have a finite time and percentage.");
            }
        }

        LengthSec = lengthSec;
        _keys = (lengthSec > 0) ? Keys(lengthSec, startPercent, endPercent, loaded) : [(0.0, 0.0), (1.0, 0.0)];
        _samples = new double[SampleCount];
        for (int i = 0; i < SampleCount; i++)
        {
            _samples[i] = Interpolate(_keys, i / (double)(SampleCount - 1));
        }
    }

    /// <summary>The scenario length the curve is drawn over, in seconds.</summary>
    public double LengthSec { get; }

    /// <summary>The sampled percentages, from the scenario's start to its end.</summary>
    public IReadOnlyList<double> Samples => _samples;

    /// <summary>The curve's percentage at <paramref name="fraction"/> of the length, in [0, 1].</summary>
    public double ValueAt(double fraction) => Interpolate(_keys, Math.Clamp(fraction, 0.0, 1.0));

    /// <summary>The points <paramref name="peaks"/> evenly-spaced peaks would place, with a trough between each two
    /// neighbours and a peak of 100 when <paramref name="peakPercent"/> is zero.</summary>
    /// <param name="lengthSec">The scenario's length; a length of zero or less places nothing.</param>
    /// <param name="peaks">How many peaks to place; zero or less places nothing.</param>
    /// <param name="peakPercent">The workload at each peak.</param>
    /// <param name="troughPercent">The workload at each trough.</param>
    public static IReadOnlyList<WorkloadPoint> AutoPoints(double lengthSec, int peaks, double peakPercent, double troughPercent)
    {
        var points = new List<WorkloadPoint>();
        if ((peaks <= 0) || (lengthSec <= 0))
        {
            return points;
        }

        double peak = Math.Clamp((peakPercent == 0) ? 100 : peakPercent, 0, 100);
        double trough = Math.Clamp(troughPercent, 0, 100);
        for (int index = 1; index <= peaks; index++)
        {
            double peakTime = lengthSec * index / (peaks + 1.0);
            points.Add(new WorkloadPoint(peakTime, peak));
            if (index < peaks)
            {
                double nextPeak = lengthSec * (index + 1) / (peaks + 1.0);
                points.Add(new WorkloadPoint((peakTime + nextPeak) / 2.0, trough));
            }
        }

        return points;
    }

    private static List<(double T, double W)> Keys(double lengthSec, double startPercent, double endPercent, List<WorkloadPoint> points)
    {
        var keys = new List<(double T, double W)> { (0.0, ClampPercent(startPercent)) };
        foreach (WorkloadPoint point in points)
        {
            keys.Add((Math.Clamp(point.TimeSec, 0.0, lengthSec) / lengthSec, ClampPercent(point.Percent)));
        }

        keys.Add((1.0, ClampPercent(endPercent)));
        return [.. keys.GroupBy(key => Math.Round(key.T, 6)).Select(group => group.Last()).OrderBy(key => key.T)];
    }

    private static double Interpolate(List<(double T, double W)> keys, double t)
    {
        for (int i = 0; i < keys.Count - 1; i++)
        {
            if (t <= keys[i + 1].T)
            {
                double span = keys[i + 1].T - keys[i].T;
                double fraction = (span > 0) ? (t - keys[i].T) / span : 0.0;
                double eased = (1.0 - Math.Cos(fraction * Math.PI)) / 2.0;
                return keys[i].W + (eased * (keys[i + 1].W - keys[i].W));
            }
        }

        return keys[^1].W;
    }

    private static double ClampPercent(double percent) => Math.Clamp(percent, 0, 100);

    private static void ThrowIfNotFinite(double value, string paramName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "The value must be a finite number.");
        }
    }
}
