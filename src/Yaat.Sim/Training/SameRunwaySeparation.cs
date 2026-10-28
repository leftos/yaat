using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

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
    /// <summary>
    /// §3-9-6.a: departure behind a preceding departure that is airborne but has not crossed the runway end.
    /// The rule is not symmetric in the two categories — a.3 gives 4,500 ft "when either the succeeding or both
    /// are Category II", while a.2 leaves a Category I preceded by a Category II at a.1's 3,000 ft. Only a
    /// Category III on either side (a.4) raises it to 6,000 ft.
    /// </summary>
    public static double RequiredDepartureBehindDepartureFt(SrsCategory preceding, SrsCategory succeeding)
    {
        if ((preceding == SrsCategory.III) || (succeeding == SrsCategory.III))
        {
            return 6000.0;
        }

        return succeeding == SrsCategory.II ? 4500.0 : 3000.0;
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
    /// §3-9-6.a satisfied: the preceding departure has crossed the runway end, or is airborne with the required
    /// spacing ahead of the succeeding departure.
    /// </summary>
    public static bool DepartureBehindDepartureSatisfied(
        bool precedingCrossedRunwayEnd,
        bool precedingAirborne,
        double spacingFt,
        SrsCategory preceding,
        SrsCategory succeeding
    ) => precedingCrossedRunwayEnd || (precedingAirborne && (spacingFt >= RequiredDepartureBehindDepartureFt(preceding, succeeding)));

    /// <summary>
    /// §3-10-3.a.2 satisfied: the departure has crossed the runway end — the pavement end — or is airborne at least
    /// the landmark distance from the <em>landing</em> threshold. The two halves legitimately use different datums:
    /// what the exception protects is the arrival crossing that threshold.
    /// </summary>
    public static bool ArrivalBehindDepartureSatisfied(
        bool departureCrossedRunwayEnd,
        bool departureAirborne,
        double departureAlongLandingThresholdFt,
        SrsCategory preceding,
        SrsCategory succeeding
    ) =>
        departureCrossedRunwayEnd
        || (departureAirborne && (departureAlongLandingThresholdFt >= RequiredArrivalBehindDepartureFt(preceding, succeeding)));

    /// <summary>
    /// §3-10-3.a.1 satisfied: the preceding arrival is clear of the runway, or has landed and is at least the landmark
    /// distance down the runway from the landing threshold (no exception when either aircraft is Category III). The
    /// landmark runs from the landing threshold — a preceding arrival never occupied the pavement behind a displaced
    /// one, so it cannot be credited for it.
    /// </summary>
    public static bool ArrivalBehindLandingSatisfied(
        bool landerClearOfRunway,
        bool landerOnGround,
        double landerAlongLandingThresholdFt,
        SrsCategory preceding,
        SrsCategory succeeding
    )
    {
        if (landerClearOfRunway)
        {
            return true;
        }

        double? exceptionFt = RequiredLandingBehindLandingExceptionFt(preceding, succeeding);
        return exceptionFt.HasValue && landerOnGround && (landerAlongLandingThresholdFt >= exceptionFt.Value);
    }

    /// <summary>
    /// The aircraft is in the §3-10-3.a.1 landing family: its phase is landing or rollout/exit, a touch-and-go or stop-and-go
    /// on the ground before its liftoff (YAAT classifies those at liftoff; once <see cref="IsOptionOnTheGround"/> their
    /// ground interval is judged by §3-10-3.a.2, and a stop-and-go still braking is a landed aircraft), or — for a phase-less
    /// aircraft — its geometric runway use is <see cref="RunwayUseKind.Landing"/>.
    /// </summary>
    public static bool IsLandingFamilyOccupant(Phase? phase, RunwayUseKind? runwayUse) =>
        phase switch
        {
            LandingPhase or RunwayExitPhase => true,
            StopAndGoPhase or TouchAndGoPhase => !HasLiftedOff(phase),
            null => runwayUse == RunwayUseKind.Landing,
            _ => false,
        };

    /// <summary>
    /// The aircraft is a §3-10-3.a.2 "departed" occupant: takeoff roll or initial climb by phase, a touch-and-go or
    /// stop-and-go that has lifted off, a low approach past the landing threshold (<see cref="RunwayUseKind.Departing"/>
    /// by phase), or — phase-less — <see cref="RunwayUseKind.Departing"/> by geometry.
    /// </summary>
    public static bool IsDepartureFamilyOccupant(Phase? phase, RunwayUseKind? runwayUse) =>
        phase switch
        {
            TakeoffPhase or InitialClimbPhase => true,
            StopAndGoPhase or TouchAndGoPhase => HasLiftedOff(phase),
            LowApproachPhase => runwayUse == RunwayUseKind.Departing,
            null => runwayUse == RunwayUseKind.Departing,
            _ => false,
        };

    /// <summary>
    /// An option counted as a departure while still on the runway: true for a touch-and-go from its touchdown (the start of
    /// its phase) until its liftoff, and for a stop-and-go from its complete stop (<see cref="StopAndGoPhase.HasStopped"/>)
    /// until its liftoff; false for a stop-and-go still braking toward its stop, for either after liftoff, and for every other
    /// phase. 7110.65 3-8-2 makes a touch-and-go a departure from touchdown and a stop-and-go an arrival "until it … makes a
    /// complete stop … and thereafter … a departing aircraft"; YAAT classifies both at liftoff as a simplification, and judges
    /// this ground interval by §3-10-3.a.2, never by the §3-10-3.a.1 landed-distance credit. An arrival behind it is
    /// protected only while a.2 is met: as a departure when <see cref="IsOptionDepartingOnTheGround"/>, never otherwise. A
    /// stop-and-go still braking is a landed aircraft and keeps the a.1 landmark credit.
    /// </summary>
    public static bool IsOptionOnTheGround(Phase? phase) => IsOptionCountedAsDeparting(phase) && !HasLiftedOff(phase);

    /// <summary>
    /// The option counts as a departing aircraft (7110.65 3-8-2): a touch-and-go for its whole phase, from touchdown through its
    /// liftoff and climb until the phase hands off at 400 ft AGL; a stop-and-go from its complete stop
    /// (<see cref="StopAndGoPhase.HasStopped"/>) through its liftoff until the same hand-off. False for a stop-and-go still
    /// braking toward its stop (a landed aircraft, §3-10-3.a.1) and for every other phase. The solo evaluator judges an
    /// arrival behind such an option by §3-10-3.a.2 throughout, and denies it the 3-10-10 restricted-low-approach exemption.
    /// </summary>
    public static bool IsOptionCountedAsDeparting(Phase? phase) => phase is TouchAndGoPhase or StopAndGoPhase { HasStopped: true };

    /// <summary>
    /// A touch-and-go on the ground, or a stop-and-go on its second roll: judged by §3-10-3.a.2 as a departure
    /// (<see cref="ArrivalBehindDepartureSatisfied"/>). A stop-and-go before its second roll (decelerating or stopped) does
    /// not meet a.2 and blocks.
    /// </summary>
    public static bool IsOptionDepartingOnTheGround(Phase? phase) =>
        phase is TouchAndGoPhase { HasLiftedOff: false } or StopAndGoPhase { IsTakeoffRoll: true };

    /// <summary>A touch-and-go or stop-and-go past its liftoff — the phase's own state, which the sim snapshots.</summary>
    private static bool HasLiftedOff(Phase? phase) => phase is TouchAndGoPhase { HasLiftedOff: true } or StopAndGoPhase { HasLiftedOff: true };

    /// <summary>
    /// Airborne within <paramref name="seconds"/>: already airborne, or still accelerating and projected to reach
    /// rotation speed in time — the "has departed" half of §3-9-6.a and §3-10-3.a.2, both of which credit the
    /// preceding aircraft only once it is flying. A rejected takeoff (decelerating) is never "about to fly",
    /// however fast it is still rolling — that is the one case the callers must not talk themselves out of. The
    /// measured acceleration (feed history) wins; a simulated or history-less aircraft uses its type's ground
    /// acceleration. That type projection follows the spool ramp the roll itself flies, placed on the ramp by
    /// the rolling phase's own clock (or, with no such phase, by the speed the aircraft is making) — an
    /// aircraft still near a standstill is credited with the idle-thrust acceleration it actually has, not
    /// the steady rate it will reach seconds later. A simulated touch-and-go is projected through the rest of its
    /// rollout dwell and then re-accelerates from the speed it carries off it, as its phase flies it.
    /// </summary>
    public static bool WillBeFlying(AircraftState aircraft, double seconds)
    {
        if (!aircraft.IsOnGround)
        {
            return true;
        }

        AircraftCategory category = AircraftCategorization.Categorize(aircraft.AircraftType);
        double? measured = RunwayOccupancy.GroundAccelerationKtPerSec(aircraft);
        if ((measured is null) && (aircraft.Phases?.CurrentPhase is TouchAndGoPhase touchAndGo))
        {
            return TouchAndGoWillBeFlying(aircraft, touchAndGo, category, seconds);
        }

        GroundRollProfile profile = measured is { } rate ? GroundRollProfile.Constant(rate) : GroundRollProfile.For(aircraft.AircraftType, category);

        double rollElapsed = GroundRollProfile.RollClockSeconds(aircraft, profile);
        return (profile.SteadyRateKtPerSec > 0)
            && (profile.SpeedAt(rollElapsed + seconds) >= AircraftPerformance.RotationSpeed(aircraft.AircraftType, category));
    }

    /// <summary>
    /// A simulated touch-and-go on the ground, projected the way <see cref="TouchAndGoPhase"/> flies it: the rest of its rollout
    /// dwell, closing on the dwell's speed target at the ground speed-change rate the flight physics apply, then the spool ramp
    /// from the speed carried off the dwell (the roll's increments add to the speed it has, not to a standstill), rotating once
    /// the indicated airspeed that ground speed implies reaches Vr. A stop-and-go's second roll really starts from a stop and
    /// keeps the standing-start projection of <see cref="WillBeFlying"/>.
    /// </summary>
    private static bool TouchAndGoWillBeFlying(AircraftState aircraft, TouchAndGoPhase touchAndGo, AircraftCategory category, double seconds)
    {
        double dwellSeconds = touchAndGo.RolloutRemainingSeconds;
        if (seconds < dwellSeconds)
        {
            return false;
        }

        var profile = GroundRollProfile.For(aircraft.AircraftType, category);
        double rollClock = touchAndGo.IsReaccelerating ? touchAndGo.RollElapsedSeconds : 0;
        double rampSeconds = seconds - dwellSeconds;
        double speedKts = touchAndGo.IsReaccelerating ? aircraft.GroundSpeed : SpeedAfterRolloutDwell(aircraft, touchAndGo, category, dwellSeconds);
        double reachedKts = speedKts + profile.SpeedAt(rollClock + rampSeconds) - profile.SpeedAt(rollClock);
        return GroundFrame.IasForGroundSpeed(aircraft, reachedKts) >= AircraftPerformance.RotationSpeed(aircraft.AircraftType, category);
    }

    /// <summary>The ground speed a touch-and-go carries off the remaining <paramref name="dwellSeconds"/> of its rollout dwell.</summary>
    private static double SpeedAfterRolloutDwell(AircraftState aircraft, TouchAndGoPhase touchAndGo, AircraftCategory category, double dwellSeconds)
    {
        double targetKts = touchAndGo.RolloutTargetSpeedKts(aircraft.AircraftType, category);
        double nowKts = aircraft.GroundSpeed;
        return nowKts > targetKts
            ? Math.Max(targetKts, nowKts - (CategoryPerformance.TaxiDecelRate(category) * dwellSeconds))
            : Math.Min(targetKts, nowKts + (CategoryPerformance.TaxiAccelRate(category) * dwellSeconds));
    }

    /// <summary>
    /// Resolves the aircraft's category from the FAA aircraft database's SRS column, falling back to weight and
    /// engine class, then to the performance category (helicopters are Category I, unknown types Category III).
    /// </summary>
    public static SrsCategory ResolveSrsCategory(AircraftState aircraft)
    {
        FaaAircraftRecord? record = FaaAircraftDatabase.Get(aircraft.AircraftType);
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
