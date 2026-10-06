namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// How an aircraft on the ground brakes to a stop at a point ahead of it, shared by its two users: a <c>FOLLOWG</c>
/// follower stopping its nose at a runway hold line (<see cref="FollowingPhase"/>), and a taxiing aircraft told
/// <c>GIVEWAY</c> stopping at its give-way point (<see cref="TaxiingPhase"/>).
/// </summary>
internal static class GroundStopBraking
{
    internal const double FeetPerSecondPerKt = GeoMath.FeetPerNm / 3600.0;

    /// <summary>How an aircraft stops at a point ahead: the follower at a runway bar, a GIVEWAY at its give-way point.</summary>
    internal enum StopBraking
    {
        /// <summary>The taxi brake rate stops it at the point.</summary>
        Routine,

        /// <summary>Only the category's max-effort rate, <see cref="CategoryPerformance.ExpediteExitDecelRate"/>, does.</summary>
        MaxEffort,

        /// <summary>
        /// Not even the max-effort rate does: brake at it. The follower stops dead short of the hold line as the last resort;
        /// a GIVEWAY, with no marking to protect, stops where the max-effort rate takes it.
        /// </summary>
        Backstop,
    }

    /// <summary>
    /// The gentlest braking that stops the aircraft at a point <paramref name="toStopFt"/> ahead (a follower's runway hold
    /// line, a GIVEWAY's give-way point) from the current speed: the taxi brake rate when its stopping distance fits, the
    /// max-effort rate when only that fits, else the backstop.
    /// </summary>
    internal static StopBraking ChooseStopBraking(PhaseContext ctx, double toStopFt)
    {
        double speedKts = ctx.Aircraft.GroundSpeed;
        if (StoppingDistanceFt(speedKts, CategoryPerformance.TaxiDecelRate(ctx.Category)) <= toStopFt)
        {
            return StopBraking.Routine;
        }

        return StoppingDistanceFt(speedKts, CategoryPerformance.ExpediteExitDecelRate(ctx.Category)) <= toStopFt
            ? StopBraking.MaxEffort
            : StopBraking.Backstop;
    }

    private static double StoppingDistanceFt(double speedKts, double decelKtsPerSec) =>
        (speedKts * speedKts) / (2.0 * decelKtsPerSec) * FeetPerSecondPerKt;

    /// <summary>How far (ft) the aircraft rolls this tick braking at its category's max-effort rate.</summary>
    internal static double MaxEffortBrakingTravelThisTickFt(PhaseContext ctx)
    {
        double speedKts = ctx.Aircraft.GroundSpeed;
        double rate = CategoryPerformance.ExpediteExitDecelRate(ctx.Category);
        double dt = ctx.DeltaSeconds;
        double travelKtSeconds = speedKts <= (rate * dt) ? (speedKts * speedKts) / (2.0 * rate) : (speedKts * dt) - (0.5 * rate * dt * dt);
        return travelKtSeconds * FeetPerSecondPerKt;
    }

    /// <summary>
    /// The speed on the braking curve at <paramref name="decelKtsPerSec"/> that reaches zero
    /// <see cref="GroundNavigator.SetBackStopMarginFt"/> short of a stop point <paramref name="toStopFt"/> ahead (a
    /// follower's hold line, a GIVEWAY's give-way point) — the curve a taxiing aircraft is held to into a set-back stop.
    /// Physics brakes onto it at the published rate; the phase only publishes the speed. The curve is read where this
    /// tick's travel leaves the aircraft: read where it stands, the target trails the aircraft by a tick, and since the
    /// curve is flown at the full brake rate the speed that lag leaves over is never lost again — 8 ft past the stop from a
    /// piston's 20 kt.
    /// </summary>
    internal static double StopCurveKts(PhaseContext ctx, double toStopFt, double decelKtsPerSec)
    {
        double thisTickFt = ctx.Aircraft.GroundSpeed * FeetPerSecondPerKt * ctx.DeltaSeconds;
        double toZeroNm = Math.Max(0.0, toStopFt - GroundNavigator.SetBackStopMarginFt - thisTickFt) / GeoMath.FeetPerNm;
        return Math.Sqrt(2.0 * decelKtsPerSec * toZeroNm * 3600.0);
    }
}
