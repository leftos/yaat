namespace Yaat.Sim;

/// <summary>The pattern-leg reports armed by <c>REPORT</c>, one flag per leg (<see cref="AircraftApproachState"/>'s five leg flags).</summary>
[Flags]
public enum ArmedReportLegs
{
    None = 0,
    Crosswind = 1,
    Downwind = 2,
    Midfield = 4,
    Base = 8,
    Final = 16,
}

/// <summary>
/// The deferred reports armed on an aircraft, as the training-hub wire carries them: a projection of the armed fields on
/// <see cref="AircraftApproachState"/>, which stays their one home. <see cref="Legs"/> holds the armed pattern-leg reports,
/// <see cref="FinalMiles"/> the armed N-mile-final distance (null when none) and <see cref="Fix"/> the armed at-fix report's fix
/// name (null when none).
/// </summary>
public sealed record ArmedReportsDto(ArmedReportLegs Legs, int? FinalMiles, string? Fix)
{
    /// <summary>The reports armed on <paramref name="approach"/>; null when none is armed.</summary>
    public static ArmedReportsDto? From(AircraftApproachState approach)
    {
        ArmedReportLegs legs =
            Leg(approach.ReportArmedCrosswind, ArmedReportLegs.Crosswind)
            | Leg(approach.ReportArmedDownwind, ArmedReportLegs.Downwind)
            | Leg(approach.ReportArmedMidfield, ArmedReportLegs.Midfield)
            | Leg(approach.ReportArmedBase, ArmedReportLegs.Base)
            | Leg(approach.ReportArmedFinal, ArmedReportLegs.Final);

        bool nothingArmed = (legs == ArmedReportLegs.None) && (approach.ReportFinalMileTarget is null) && (approach.ReportAtFixName is null);
        return nothingArmed ? null : new ArmedReportsDto(legs, approach.ReportFinalMileTarget, approach.ReportAtFixName);
    }

    private static ArmedReportLegs Leg(bool armed, ArmedReportLegs leg) => armed ? leg : ArmedReportLegs.None;
}
