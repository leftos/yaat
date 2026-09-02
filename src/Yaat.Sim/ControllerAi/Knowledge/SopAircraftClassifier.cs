using Yaat.Sim.Data;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.ControllerAi.Knowledge;

/// <summary>
/// The P / T / J aircraft class the ZOA-area SOPs branch on (NCT SOP 1-7): a jet or a four-engine turboprop is J, another
/// non-jet cruising at 180 kt or more is T, the rest are P. Resolved from the type's own performance profile (cruise TAS)
/// — type-intrinsic, never from the filed speed — with the category baseline when no profile exists.
/// </summary>
/// <remarks>
/// Missing FAA data errs toward the runway restriction: an unknown MTOW counts as over the threshold for a class-T type (the
/// heavy turboprop the rule exists for) and never for others; an unknown engine count never matches.
/// </remarks>
public static class SopAircraftClassifier
{
    public const double TurbopropCruiseKt = 180;

    public static SopAircraftClass Classify(string aircraftType)
    {
        var category = AircraftCategorization.Categorize(aircraftType);
        if (category == AircraftCategory.Jet)
        {
            return SopAircraftClass.J;
        }

        if ((category == AircraftCategory.Turboprop) && (FaaAircraftDatabase.Get(aircraftType)?.NumEngines == 4))
        {
            return SopAircraftClass.J;
        }

        double cruise = AircraftProfileDatabase.Get(aircraftType)?.CruiseSpeed ?? CategoryPerformance.BaselineProfile(category).CruiseSpeed;
        return cruise >= TurbopropCruiseKt ? SopAircraftClass.T : SopAircraftClass.P;
    }

    /// <summary>
    /// Every stated field must match. An unknown MTOW counts as over the threshold for a class-T aircraft (the heavier
    /// turboprops the rule is after), an unknown engine count never matches.
    /// </summary>
    public static bool Matches(AircraftPredicate predicate, string aircraftType)
    {
        var category = AircraftCategorization.Categorize(aircraftType);
        if ((predicate.Category is { } wantedCategory) && (wantedCategory != category))
        {
            return false;
        }

        var sopClass = Classify(aircraftType);
        if ((predicate.SopClass is { } wantedClass) && (wantedClass != sopClass))
        {
            return false;
        }

        var faa = FaaAircraftDatabase.Get(aircraftType);
        if (predicate.MtowOverLb is { } mtowOver)
        {
            bool over = faa?.MtowLb is { } mtow ? mtow > mtowOver : sopClass == SopAircraftClass.T;
            if (!over)
            {
                return false;
            }
        }

        if ((predicate.EngineCount is { } engines) && (faa?.NumEngines != engines))
        {
            return false;
        }

        return true;
    }
}
