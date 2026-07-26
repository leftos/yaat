using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Data.Airspace;

/// <summary>
/// Geometry helpers for a VFR pilot's self-restriction outside Class B/C airspace: the altitude to level
/// at when the airspace is directly overhead, and which way to turn when it is ahead.
/// </summary>
public static class AirspaceAvoidance
{
    /// <summary>
    /// Lowest height above the surface a pilot-chosen level-off altitude may sit at. 14 CFR 91.119 puts the
    /// hard floor at 500 ft (1,000 ft over congested areas); Class B/C shelves sit over metropolitan areas,
    /// so the congested-area figure is the one that binds. A shelf whose floor leaves less room than this —
    /// a surface area, in practice — cannot be flown under, and the caller must turn instead.
    /// </summary>
    public const double MinimumFlyableAglFt = 1000;

    /// <summary>
    /// The altitude a VFR pilot levels at to remain clear of a volume whose floor is
    /// <paramref name="volumeFloorFtMsl"/>, or null when there is no flyable airspace beneath it.
    ///
    /// The charted floor is inclusive — 2,100 ft MSL *is* Class B — and Mode C is quantized to 100 ft with
    /// up to 125 ft of legal error (14 CFR 91.217(a)(3)), so the level is the highest round hundred strictly
    /// below the floor rather than one foot under it. Above <see cref="HemisphericAltitude.AglFloorFt"/> the
    /// VFR cruising-altitude rule (14 CFR 91.159) binds, so the level drops to the highest conforming
    /// odd/even-thousand-plus-500 for the aircraft's magnetic course.
    /// </summary>
    public static int? LevelOffCeilingFt(int volumeFloorFtMsl, double magneticCourseDeg, double surfaceElevationFt)
    {
        if (volumeFloorFtMsl <= 0)
        {
            return null;
        }

        int ceiling = (int)(Math.Floor((volumeFloorFtMsl - 1) / 100.0) * 100);

        if (ceiling - surfaceElevationFt > HemisphericAltitude.AglFloorFt)
        {
            if (HemisphericAltitude.Snap(magneticCourseDeg, ceiling, minFt: 0, maxFt: ceiling) is not { } conforming)
            {
                return null;
            }

            ceiling = (int)conforming;
        }

        return ceiling - surfaceElevationFt >= MinimumFlyableAglFt ? ceiling : null;
    }

    /// <summary>
    /// The turn that puts <paramref name="boundaryPoint"/> behind the wing rather than across the nose.
    /// Turning toward the boundary — which a fixed direction does half the time — drives the aircraft into
    /// the airspace the manoeuvre exists to avoid.
    /// </summary>
    public static TurnDirection AwayFrom(TrueHeading track, LatLon position, LatLon boundaryPoint)
    {
        double relative = GeoMath.SignedBearingDifference(track.Degrees, GeoMath.BearingTo(position, boundaryPoint));
        return relative >= 0 ? TurnDirection.Left : TurnDirection.Right;
    }
}
