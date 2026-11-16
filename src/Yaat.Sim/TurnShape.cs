using Microsoft.Extensions.Logging;

namespace Yaat.Sim;

/// <summary>
/// The shape of an airborne turn, shared by every turn-start decision: the radius of the arc flown at a turn rate, and
/// under <see cref="TurnModel.Ramp"/> the lead that roll-in adds before the arc. A ramped turn's heading lags an instant
/// turn by half the roll-in time (or less for a turn too small to reach the full rate), so the aircraft flies that much
/// further along the old track before it turns; a start point placed this lead earlier keeps the arc where the instant
/// model put it. The roll-out needs no lead: the ramp's easing law starts the same heading deficit before the target.
/// </summary>
public static class TurnShape
{
    private static readonly ILogger Log = SimLog.CreateLogger("TurnShape");

    /// <summary>20 pi: a ground speed in knots over (turn rate in degrees per second times this) is the radius in nm.</summary>
    private const double RadiusDivisor = 62.832;

    /// <summary>Radius (nm) of the arc flown at <paramref name="groundSpeedKt"/> and <paramref name="turnRateDegPerSec"/>.</summary>
    public static double RadiusNm(double groundSpeedKt, double turnRateDegPerSec) => groundSpeedKt / (turnRateDegPerSec * RadiusDivisor);

    /// <summary>Seconds a turn of <paramref name="category"/> takes to roll in from wings level to its full rate.</summary>
    public static double RollTimeSeconds(AircraftCategory category) => CategoryPerformance.RollInSeconds(category);

    /// <summary>
    /// Distance (nm) a turn of <paramref name="turnDeg"/> must start ahead of its instant-model start point: ground speed
    /// times the lag, with lag = T_roll / 2 for a turn of at least the full rate times T_roll, else
    /// sqrt(theta / alpha) - theta / (2 * omega) (alpha = omega / T_roll), which keeps the corner of a turn that peaks below
    /// the full rate where the instant turn put it and equals T_roll / 2 where the two meet. Zero under
    /// <see cref="TurnModel.Instant"/>, so every caller's result is unchanged there.
    /// </summary>
    public static double LeadNm(double groundSpeedKt, double turnDeg, double turnRateDegPerSec, AircraftCategory category, TurnModel turnModel) =>
        LeadNm(new TurnLeadInput(groundSpeedKt, turnDeg, turnRateDegPerSec, category, turnModel));

    /// <summary>
    /// The roll-in lead (nm) of <paramref name="turn"/>, covering only the roll-in still to come: an aircraft already
    /// turning its way at rate r has the lag (omega - r)^2 / (2 * alpha * omega) left (T_roll / 2 wings level, 0 at the
    /// full rate), capped by the whole turn's lag of <see cref="LeadNm(double, double, double, AircraftCategory, TurnModel)"/>.
    /// A rate the other way counts as wings level. Zero under <see cref="TurnModel.Instant"/>.
    /// </summary>
    public static double LeadNm(TurnLeadInput turn)
    {
        if (turn.TurnModel != TurnModel.Ramp)
        {
            return 0;
        }

        double omega = turn.TurnRateDegPerSec;
        if (omega <= 0)
        {
            Log.LogWarning("[TurnShape] lead asked for a non-positive turn rate {Rate:F3} deg/s; no lead", omega);
            return 0;
        }

        double rollSeconds = RollTimeSeconds(turn.Category);
        double thetaDeg = Math.Abs(turn.TurnDeg);
        double turnLagSeconds =
            thetaDeg >= (omega * rollSeconds) ? rollSeconds / 2.0 : Math.Sqrt(thetaDeg / (omega / rollSeconds)) - (thetaDeg / (2.0 * omega));
        double rolledInDegPerSec = Math.Clamp(turn.AppliedTurnRateDegPerSec * Math.Sign(turn.TurnDeg), 0, omega);
        double remainingLagSeconds = (omega - rolledInDegPerSec) * (omega - rolledInDegPerSec) * rollSeconds / (2.0 * omega * omega);
        return Math.Max(turn.GroundSpeedKt, 0) * Math.Min(turnLagSeconds, remainingLagSeconds) / 3600.0;
    }

    /// <summary>
    /// Cross-track distance (nm) from a course at which <paramref name="turn"/> onto it starts: the turn radius plus the
    /// cross-track component of the roll-in lead, <see cref="LeadNm(TurnLeadInput)"/> times sin(theta), since the lead is
    /// flown along the old track. Exactly the radius under <see cref="TurnModel.Instant"/>.
    /// </summary>
    public static double InterceptLeadNm(TurnLeadInput turn) =>
        RadiusNm(turn.GroundSpeedKt, turn.TurnRateDegPerSec) + (LeadNm(turn) * Math.Sin(Math.Abs(turn.TurnDeg) * Math.PI / 180.0));
}

/// <summary>
/// A turn whose roll-in lead <see cref="TurnShape"/> computes: the ground speed it is flown at, the signed heading change
/// (positive right), the full turn rate, the category (its roll-in time) and the session's turn model.
/// <see cref="AppliedTurnRateDegPerSec"/> is the signed rate the aircraft is already turning at (positive right; 0 wings
/// level and under <see cref="TurnModel.Instant"/>).
/// </summary>
public readonly record struct TurnLeadInput(
    double GroundSpeedKt,
    double TurnDeg,
    double TurnRateDegPerSec,
    AircraftCategory Category,
    TurnModel TurnModel
)
{
    /// <summary>The signed rate (deg/s, positive right) the aircraft is already turning at.</summary>
    public double AppliedTurnRateDegPerSec { get; init; }

    /// <summary>
    /// The turn <paramref name="aircraft"/> makes of <paramref name="turnDeg"/> (signed, positive right) at
    /// <paramref name="turnRateDegPerSec"/>: at its ground speed, from its applied turn rate.
    /// </summary>
    public static TurnLeadInput Of(
        AircraftState aircraft,
        double turnDeg,
        double turnRateDegPerSec,
        AircraftCategory category,
        TurnModel turnModel
    ) =>
        new(aircraft.GroundSpeed, turnDeg, turnRateDegPerSec, category, turnModel)
        {
            AppliedTurnRateDegPerSec = aircraft.AppliedTurnRateDegPerSec ?? 0,
        };
}
