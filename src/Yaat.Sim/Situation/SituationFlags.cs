namespace Yaat.Sim.Situation;

/// <summary>
/// Facts about an aircraft's situation that the <c>Situation</c> spine step computes once a second beside
/// <see cref="AircraftSituation"/> (<see cref="SituationFlagCalculator"/>). Append-only: each value is a fixed bit that
/// snapshots store as a number, so a new flag takes the next free bit and an existing one never moves.
/// </summary>
[Flags]
public enum SituationFlags
{
    None = 0,

    /// <summary>Taxiing within reach of the departure-runway hold line, with no runway to cross before it.</summary>
    NearingDepartureHoldLine = 1,

    /// <summary>Holding short of the departure runway: its route-end bar, or an explicit hold-short of it.</summary>
    HoldShortIsDepartureRunway = 2,

    /// <summary>On an instrument approach, inside the final approach fix on final.</summary>
    InsideFinalApproachFix = 4,

    /// <summary>Rolling out after a full-stop landing, slowed or well down the runway, or exiting it.</summary>
    RolloutDecelerating = 8,

    /// <summary>An IFR aircraft has reported the field in sight.</summary>
    HasReportedFieldInSight = 16,

    /// <summary>An IFR aircraft has reported the preceding traffic in sight.</summary>
    HasReportedTrafficInSight = 32,

    /// <summary>Cleared for takeoff (7110.65 §3-9-10); a line-up-and-wait clearance does not count.</summary>
    HasTakeoffClearance = 64,

    /// <summary>
    /// On the takeoff, airborne or rolling at or above V1: committed to the takeoff, so the clearance can no longer be
    /// cancelled (§3-9-11).
    /// </summary>
    PastV1 = 128,

    /// <summary>
    /// Cleared for the approach with descent on it, not a lateral intercept only (JFAC/JLOC): no further approach
    /// clearance to issue (§5-9-4).
    /// </summary>
    ApproachClearedForDescent = 256,
}
