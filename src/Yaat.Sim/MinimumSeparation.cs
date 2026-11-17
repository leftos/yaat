using Microsoft.Extensions.Logging;

namespace Yaat.Sim;

/// <summary>One track a min-sep projection starts from: where it is, its ground track (degrees true) and its ground speed.</summary>
/// <param name="Position">The track's position now.</param>
/// <param name="TrueTrackDeg">The ground track, degrees true.</param>
/// <param name="GroundSpeedKts">The ground speed, knots.</param>
public readonly record struct MinSepTrack(LatLon Position, double TrueTrackDeg, double GroundSpeedKts);

/// <summary>What a min-sep projection found: <see cref="MinSepConverging"/>, <see cref="MinSepDiverging"/> or <see cref="MinSepInvalid"/>.</summary>
public abstract record MinSepResult;

/// <summary>A converging pair: where each track is at the last step the distance still shrank, and that distance.</summary>
/// <param name="PointA">The first track's predicted position at the closest step.</param>
/// <param name="PointB">The second track's predicted position at the closest step.</param>
/// <param name="ClosestNm">The distance between the two predicted positions, NM.</param>
public sealed record MinSepConverging(LatLon PointA, LatLon PointB, double ClosestNm) : MinSepResult;

/// <summary>A pair that does not close: the distance between the two tracks now.</summary>
/// <param name="DistanceNm">The current distance, NM.</param>
public sealed record MinSepDiverging(double DistanceNm) : MinSepResult;

/// <summary>A pair the projection cannot run on: a ground track or a ground speed out of range.</summary>
public sealed record MinSepInvalid : MinSepResult
{
    /// <summary>The one instance.</summary>
    public static readonly MinSepInvalid Instance = new();
}

/// <summary>
/// The STARS minimum separation projection, as CRC's STARS display runs it: both tracks advance at constant ground speed and
/// track in 5 s steps, at most 760 steps, until the distance between them first stops shrinking. Distances are measured in a
/// planar frame of degrees, longitude scaled by the first track's latitude, and read in NM at 60 NM a degree. A first step
/// that closes by less than 0.0001 degrees reads as diverging. Pure.
/// </summary>
public static class MinimumSeparation
{
    private static readonly ILogger Log = SimLog.CreateLogger("MinimumSeparation");

    /// <summary>Seconds one projection step advances both tracks.</summary>
    public const double StepSeconds = 5.0;

    /// <summary>The most steps a projection takes.</summary>
    public const int MaxSteps = 760;

    /// <summary>The first step must close by at least this many degrees for the pair to read as converging.</summary>
    public const double MinFirstStepClosureDeg = 0.0001;

    /// <summary>The slowest ground speed the projection runs on, knots.</summary>
    public const double MinGroundSpeedKts = 1.0;

    /// <summary>The fastest ground speed the projection runs on, knots.</summary>
    public const double MaxGroundSpeedKts = 10000.0;

    // CRC's NM per degree of longitude at the equator.
    private const double NmPerDegreeLonAtEquator = 59.998660261511354;

    private const double NmPerDegree = 60.0;

    /// <summary>Projects <paramref name="a"/> and <paramref name="b"/> forward and reads their minimum separation.</summary>
    public static MinSepResult Compute(MinSepTrack a, MinSepTrack b)
    {
        if (!IsValid(a) || !IsValid(b))
        {
            Log.LogDebug("Min sep invalid: {A} / {B}", a, b);
            return MinSepInvalid.Instance;
        }

        double scale = NmPerDegreeLonAtEquator * Math.Cos(a.Position.Lat * Math.PI / 180.0) / NmPerDegree;
        double stepA = StepDegrees(a.GroundSpeedKts);
        double stepB = StepDegrees(b.GroundSpeedKts);
        LatLon pa = a.Position;
        LatLon pb = b.Position;
        MinSepConverging? closest = null;
        for (int i = 1; i <= MaxSteps; i++)
        {
            double before = PlanarDegrees(pa, pb, scale);
            LatLon na = Advance(pa, stepA, a.TrueTrackDeg, scale);
            LatLon nb = Advance(pb, stepB, b.TrueTrackDeg, scale);
            double after = PlanarDegrees(na, nb, scale);
            if (!(after < before) || ((closest is null) && ((before - after) < MinFirstStepClosureDeg)))
            {
                break;
            }

            closest = new MinSepConverging(na, nb, after * NmPerDegree);
            pa = na;
            pb = nb;
        }

        return (closest is not null) ? closest : new MinSepDiverging(PlanarDegrees(a.Position, b.Position, scale) * NmPerDegree);
    }

    // A ground track in [0, 360] and a ground speed in [1, 10000] kt, as CRC requires.
    private static bool IsValid(MinSepTrack track) =>
        (track.TrueTrackDeg >= 0.0)
        && (track.TrueTrackDeg <= 360.0)
        && (track.GroundSpeedKts >= MinGroundSpeedKts)
        && (track.GroundSpeedKts <= MaxGroundSpeedKts);

    // The degrees of latitude a track at groundSpeedKts covers in one step.
    private static double StepDegrees(double groundSpeedKts) => groundSpeedKts / 3600.0 / NmPerDegree * StepSeconds;

    // The planar distance in degrees, longitude scaled.
    private static double PlanarDegrees(LatLon p, LatLon q, double scale)
    {
        double dLon = Math.Abs(q.Lon - p.Lon) * scale;
        double dLat = Math.Abs(q.Lat - p.Lat);
        return Math.Sqrt((dLon * dLon) + (dLat * dLat));
    }

    // p moved distanceDeg along bearingDeg (true) in the planar frame.
    private static LatLon Advance(LatLon p, double distanceDeg, double bearingDeg, double scale)
    {
        double rad = bearingDeg * Math.PI / 180.0;
        return new LatLon(p.Lat + (distanceDeg * Math.Cos(rad)), p.Lon + (distanceDeg * Math.Sin(rad) / scale));
    }
}
