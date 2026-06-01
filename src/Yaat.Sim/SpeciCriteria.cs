namespace Yaat.Sim;

/// <summary>
/// Decides whether current conditions warrant an off-cycle SPECI relative to the last issued
/// report. Implements a basic-but-realistic subset of the FMH-1 / AIM TBL 7-1-1 SPECI criteria:
/// wind shift, visibility threshold crossings, ceiling threshold crossings, and precipitation
/// onset/cessation. Comparing against the last <em>issued</em> report (which re-baselines on each
/// issue) provides the hysteresis that prevents repeated SPECIs while a value sits on a boundary.
/// </summary>
public static class SpeciCriteria
{
    // AIM TBL 7-1-1: visibility decreases to less than / increases to equal-or-above these values.
    private static readonly double[] VisibilityThresholdsSm = [3.0, 2.0, 1.0, 0.5, 0.25];

    // AIM TBL 7-1-1: ceiling forms/dissipates/crosses these heights (lowest BKN/OVC layer).
    private static readonly int[] CeilingThresholdsFt = [3000, 1500, 1000, 500];

    private const int WindShiftDegrees = 45;
    private const int WindShiftMinSpeedKt = 10;

    public static bool IsSpeciWorthy(ReportedConditions lastIssued, ReportedConditions current)
    {
        if (IsWindShift(lastIssued, current))
        {
            return true;
        }

        if (CrossedThreshold(lastIssued.VisibilityStatuteMiles, current.VisibilityStatuteMiles, VisibilityThresholdsSm))
        {
            return true;
        }

        if (CeilingCrossed(lastIssued.CeilingFeetAgl, current.CeilingFeetAgl))
        {
            return true;
        }

        return lastIssued.Precipitation != current.Precipitation;
    }

    /// <summary>
    /// FMH-1 wind shift: direction change of 45 degrees or more with the wind speed 10 kt or more
    /// throughout (approximated here by both endpoints being 10 kt or more).
    /// </summary>
    private static bool IsWindShift(ReportedConditions a, ReportedConditions b)
    {
        if (a.Calm || b.Calm)
        {
            return false;
        }

        if (a.WindSpeedKt < WindShiftMinSpeedKt || b.WindSpeedKt < WindShiftMinSpeedKt)
        {
            return false;
        }

        return AngularDifference(a.WindDirTrueDeg, b.WindDirTrueDeg) >= WindShiftDegrees;
    }

    private static int AngularDifference(int a, int b)
    {
        int diff = Math.Abs(a - b) % 360;
        return diff > 180 ? 360 - diff : diff;
    }

    private static bool CrossedThreshold(double? last, double? current, double[] thresholds)
    {
        if (last is null || current is null)
        {
            return false;
        }

        foreach (var threshold in thresholds)
        {
            // "below" = decreased to less than the threshold; the at-or-above side is its complement,
            // so a sign change of (value < threshold) captures a crossing in either direction.
            if ((last.Value < threshold) != (current.Value < threshold))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CeilingCrossed(int? last, int? current)
    {
        // No ceiling is treated as infinitely high, so forming/dissipating a ceiling around a
        // threshold registers as a crossing.
        double lastFt = last ?? double.PositiveInfinity;
        double currentFt = current ?? double.PositiveInfinity;

        foreach (var threshold in CeilingThresholdsFt)
        {
            if ((lastFt < threshold) != (currentFt < threshold))
            {
                return true;
            }
        }

        return false;
    }
}
