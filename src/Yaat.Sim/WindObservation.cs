namespace Yaat.Sim;

/// <summary>
/// A wind observation assembled from the simulated instantaneous field, mirroring how an
/// ASOS forms one: mean direction/speed over the trailing 2-minute window, peak/lull over
/// the trailing 10-minute window, on a 5-second sample grid. Directions are degrees
/// MAGNETIC (the wind-layer convention); the METAR issuer converts to true at encoding
/// time. The direction-variability envelope is not part of the observation — the bounded
/// noise's envelope IS the authored layer arc, which a finite sample only ever
/// under-estimates, so the issuer codes dddVddd/VRB from the authored amplitudes instead.
/// </summary>
public sealed record ObservedWind(
    double MeanDirectionMagDeg,
    double MeanSpeedKts,
    double PeakSpeedKts,
    double PeakDirectionMagDeg,
    double PeakElapsedSeconds,
    double LullSpeedKts
);

/// <summary>
/// Samples the simulated surface wind the way a real observing system does, so every coded
/// group of a re-issued METAR (VRB, dddVddd, gusts, calm) emerges from one observation of
/// the same field physics flies in, at phase 0. Pure function of (weather, elapsed sim
/// time): reports reproduce identically on replay, and the trailing windows are evaluable
/// at negative sample times right after scenario start.
/// </summary>
public static class WindObservation
{
    /// <summary>ASOS updates its running averages on a 5-second grid.</summary>
    private const double SampleIntervalSeconds = 5.0;

    /// <summary>Direction/speed are 2-minute averages (FMH-1); 24 samples on the 5-s grid.</summary>
    private const int MeanWindowSamples = 24;

    /// <summary>Gusts code the peak in the trailing 10 minutes (FMH-1); 120 samples on the 5-s grid.</summary>
    private const int PeakWindowSamples = 120;

    /// <summary>FMH-1: an observation is calm below 1 kt (the AIM's ≤3 kt figure is the TAF forecast rule).</summary>
    public const double CalmBelowKt = 1.0;

    /// <summary>FMH-1/AIM 7-1-28: direction variation of 60° or more is what the coding rules key on.</summary>
    public const double MinVariableSpreadDeg = 60.0;

    /// <summary>At or below this mean speed, a ≥60° spread codes VRB rather than a dddVddd group.</summary>
    public const double VrbMaxMeanSpeedKt = 6.0;

    /// <summary>FMH-1: variation of 180° or more (or indeterminate) codes VRB regardless of speed, with no dddVddd group.</summary>
    public const double VrbForcedSpreadDeg = 180.0;

    /// <summary>A coded gust within this of the mean is meaningless; drop the group.</summary>
    public const double MinGustExcessKt = 3.0;

    /// <summary>AIM 7-1-28: a PK WND remark is reported when the peak exceeds 25 kt.</summary>
    public const double PeakWindRemarkAboveKt = 25.0;

    /// <summary>
    /// Observes the surface wind (lowest layer) of <paramref name="weather"/> at
    /// <paramref name="elapsedSeconds"/>. Returns null when the profile has no wind layers
    /// (text-only weather falls back to the base METAR's own wind group).
    /// </summary>
    public static ObservedWind? Observe(WeatherProfile weather, double elapsedSeconds)
    {
        if (weather.WindLayers.Count == 0)
        {
            return null;
        }

        // Sample at the field-elevation datum (an anemometer stands on the ground), so the
        // observation sees the same full-amplitude wind a runway-level aircraft flies in.
        double surfaceAltitude = weather.SurfaceElevationFt();

        // One pass over the 10-minute window for peak/lull speeds (plus the peak's
        // direction and time for the PK WND remark); the first 24 samples (2 minutes)
        // also feed the scalar mean speed (ASOS reports scalar, not vector, mean speed)
        // and unit-vector mean direction.
        double speedSum = 0;
        double northSum = 0;
        double eastSum = 0;
        double peak = double.MinValue;
        double lull = double.MaxValue;
        double peakDirection = 0;
        double peakElapsed = elapsedSeconds;
        for (int k = 0; k < PeakWindowSamples; k++)
        {
            double sampleTime = elapsedSeconds - (k * SampleIntervalSeconds);
            var sample = SampleAt(weather, surfaceAltitude, sampleTime);

            if (sample.SpeedKts > peak)
            {
                peak = sample.SpeedKts;
                peakDirection = sample.DirectionDeg;
                peakElapsed = sampleTime;
            }

            lull = Math.Min(lull, sample.SpeedKts);

            if (k < MeanWindowSamples)
            {
                speedSum += sample.SpeedKts;
                double rad = sample.DirectionDeg * Math.PI / 180.0;
                northSum += Math.Cos(rad);
                eastSum += Math.Sin(rad);
            }
        }

        double meanSpeed = speedSum / MeanWindowSamples;
        double meanDirection = Math.Atan2(eastSum, northSum) * 180.0 / Math.PI;
        if (meanDirection < 0)
        {
            meanDirection += 360.0;
        }

        return new ObservedWind(meanDirection, meanSpeed, peak, peakDirection, peakElapsed, lull);
    }

    private static WindAtAltitude SampleAt(WeatherProfile weather, double surfaceAltitude, double sampleTime)
    {
        // Phase 0 is the observation instrument's phase; aircraft sample the same field at
        // their own callsign phase.
        return WindInterpolator.GetWindAt(weather, surfaceAltitude, sampleTime, 0);
    }
}
