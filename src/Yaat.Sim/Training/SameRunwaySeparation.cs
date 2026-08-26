using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Training;

/// <summary>Same-runway separation aircraft category (7110.65 §3-9-6 NOTE / P/CG AIRCRAFT CLASSES).</summary>
public enum SrsCategory
{
    /// <summary>Small single-engine propeller aircraft (≤ 12,500 lb) and all helicopters.</summary>
    I,

    /// <summary>Small twin-engine propeller aircraft (≤ 12,500 lb).</summary>
    II,

    /// <summary>Everything else.</summary>
    III,
}

/// <summary>
/// The 7110.65 same-runway separation landmark distances (§3-9-6, §3-10-3) and the category resolution
/// they key on. Shared by the solo-training evaluator (scoring) and the occupied-runway go-around
/// (pilot behaviour) so both agree on what "far enough down the runway" means.
/// </summary>
public static class SameRunwaySeparation
{
    /// <summary>§3-9-6.b: departure behind a preceding departure that has not crossed the runway end.</summary>
    public static double RequiredDepartureBehindDepartureFt(SrsCategory preceding, SrsCategory succeeding)
    {
        if ((preceding == SrsCategory.III) || (succeeding == SrsCategory.III))
        {
            return 6000.0;
        }

        if ((preceding == SrsCategory.II) || (succeeding == SrsCategory.II))
        {
            return 4500.0;
        }

        return 3000.0;
    }

    /// <summary>§3-10-3.a.2: arrival crossing the landing threshold behind an airborne departure still over the runway.</summary>
    public static double RequiredArrivalBehindDepartureFt(SrsCategory preceding, SrsCategory succeeding)
    {
        if ((preceding == SrsCategory.III) || (succeeding == SrsCategory.III))
        {
            return 6000.0;
        }

        return succeeding == SrsCategory.II ? 4500.0 : 3000.0;
    }

    /// <summary>
    /// §3-10-3.a.1: arrival crossing the landing threshold behind a landed aircraft that is not yet clear of the
    /// runway. Null when there is no exception (a Category III on either side) and the runway must be clear.
    /// </summary>
    public static double? RequiredLandingBehindLandingExceptionFt(SrsCategory preceding, SrsCategory succeeding)
    {
        if ((preceding == SrsCategory.III) || (succeeding == SrsCategory.III))
        {
            return null;
        }

        return succeeding == SrsCategory.II ? 4500.0 : 3000.0;
    }

    /// <summary>
    /// Resolves the aircraft's category from the FAA aircraft database's SRS column, falling back to weight and
    /// engine class, then to the performance category (helicopters are Category I, unknown types Category III).
    /// </summary>
    public static SrsCategory ResolveSrsCategory(AircraftState aircraft)
    {
        var record = FaaAircraftDatabase.Get(aircraft.AircraftType);
        if (record?.Srs is { Length: > 0 } srs)
        {
            if (srs.Equals("I", StringComparison.OrdinalIgnoreCase))
            {
                return SrsCategory.I;
            }

            if (srs.Equals("II", StringComparison.OrdinalIgnoreCase))
            {
                return SrsCategory.II;
            }

            if (srs.Equals("III", StringComparison.OrdinalIgnoreCase))
            {
                return SrsCategory.III;
            }
        }

        if (record is not null)
        {
            bool small = (record.MtowLb ?? double.MaxValue) <= 12500.0;
            bool prop =
                (record.PhysicalClassEngine?.Contains("Piston", StringComparison.OrdinalIgnoreCase) == true)
                || (record.PhysicalClassEngine?.Contains("Prop", StringComparison.OrdinalIgnoreCase) == true);
            bool helicopter =
                (record.Class?.Contains("Helicopter", StringComparison.OrdinalIgnoreCase) == true)
                || (record.PhysicalClassEngine?.Contains("Turboshaft", StringComparison.OrdinalIgnoreCase) == true);

            if (helicopter || (small && prop && (record.NumEngines == 1)))
            {
                return SrsCategory.I;
            }

            if (small && prop && (record.NumEngines == 2))
            {
                return SrsCategory.II;
            }
        }

        return AircraftCategorization.Categorize(aircraft.AircraftType) == AircraftCategory.Helicopter ? SrsCategory.I : SrsCategory.III;
    }
}
