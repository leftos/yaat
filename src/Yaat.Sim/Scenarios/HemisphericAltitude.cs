namespace Yaat.Sim.Scenarios;

/// <summary>
/// The VFR cruising-altitude rule of 14 CFR 91.159(a) / AIM 3-1-5 TBL 3-1-2: an aircraft in level cruising
/// flight more than 3000 ft above the surface flies an odd thousand + 500 on a magnetic course of 0°–179°,
/// and an even thousand + 500 on 180°–359°.
///
/// Only <em>level</em> flight above the AGL floor is bound by the rule — a climbing or descending aircraft,
/// or one below the floor, may be at any altitude, and ATC may assign an off-hemisphere altitude for
/// separation (7110.65 §7-8-5.b).
/// </summary>
public static class HemisphericAltitude
{
    /// <summary>The rule binds only above this height above the surface.</summary>
    public const double AglFloorFt = 3000;

    /// <summary>
    /// How far outside the author's altitude band a snapped level may sit. A correct-hemisphere altitude
    /// matters more than staying exactly inside a band that is too narrow to contain one.
    /// </summary>
    public const double BandToleranceFt = 500;

    /// <summary>True when a level flying <paramref name="magneticCourseDeg"/> conforms to the rule.</summary>
    public static bool IsConforming(double magneticCourseDeg, double altitudeFt)
    {
        var thousands = (altitudeFt - 500) / 1000.0;
        if (Math.Abs(thousands - Math.Round(thousands)) > 1e-6)
        {
            return false;
        }
        return IsOdd((int)Math.Round(thousands)) == WantsOddThousands(magneticCourseDeg);
    }

    /// <summary>
    /// The hemispheric level nearest <paramref name="desiredFt"/> for the given magnetic course, preferring
    /// one inside [<paramref name="minFt"/>, <paramref name="maxFt"/>] and otherwise accepting one within
    /// <see cref="BandToleranceFt"/> of the band. Returns null when the band cannot accommodate any valid
    /// level — the caller keeps the raw altitude and warns rather than silently flying a wrong hemisphere.
    /// </summary>
    public static double? Snap(double magneticCourseDeg, double desiredFt, double minFt, double maxFt)
    {
        var wantOdd = WantsOddThousands(magneticCourseDeg);

        // Walk every candidate level spanning the band plus its tolerance, nearest-to-desired first.
        var lowestThousands = (int)Math.Floor((minFt - BandToleranceFt - 500) / 1000.0);
        var highestThousands = (int)Math.Ceiling((maxFt + BandToleranceFt - 500) / 1000.0);

        double? bestInBand = null;
        double? bestInTolerance = null;

        for (var thousands = lowestThousands; thousands <= highestThousands; thousands++)
        {
            if (thousands < 0 || IsOdd(thousands) != wantOdd)
            {
                continue;
            }

            var candidate = (thousands * 1000.0) + 500.0;
            if (candidate >= minFt && candidate <= maxFt)
            {
                bestInBand = Nearer(bestInBand, candidate, desiredFt);
            }
            else if ((candidate >= minFt - BandToleranceFt) && (candidate <= maxFt + BandToleranceFt))
            {
                bestInTolerance = Nearer(bestInTolerance, candidate, desiredFt);
            }
        }

        return bestInBand ?? bestInTolerance;
    }

    /// <summary>Magnetic course 0°–179° flies odd thousands + 500; 180°–359° flies even thousands + 500.</summary>
    private static bool WantsOddThousands(double magneticCourseDeg)
    {
        var course = magneticCourseDeg % 360.0;
        if (course < 0)
        {
            course += 360.0;
        }
        return course < 180.0;
    }

    private static bool IsOdd(int value) => (value % 2) != 0;

    private static double Nearer(double? incumbent, double candidate, double target) =>
        incumbent is not { } current || Math.Abs(candidate - target) < Math.Abs(current - target) ? candidate : current;
}
