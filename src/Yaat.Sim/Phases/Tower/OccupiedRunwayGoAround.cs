using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Training;

namespace Yaat.Sim.Phases.Tower;

/// <summary>
/// Pilot-initiated go-around for an arrival on short final whose runway is occupied. AIM 5-2-5.9: "never land on a
/// runway that is occupied by another aircraft, even if a landing clearance was issued"; AIM 5-5-5.a.1(b)/a.2: the
/// pilot goes around when a safe landing is not possible and says why. The trigger is the tower's own unit —
/// seconds to the landing threshold (§3-9-6, §3-9-7 are written in time) — so a 70-kt trainer and a 140-kt jet get
/// the same decision window (about 235 ft AGL for the trainer, 420 ft for the jet on a 3° path).
///
/// §3-10-3 is a "does not cross the landing threshold until" rule, so the occupant is judged where it will be when the
/// arrival reaches the threshold (present ground speed held), and the exception that applies depends on what the
/// occupant did on this runway: landed here → a.1 (landmark distance, none when either aircraft is Category III);
/// departed here → a.2 (airborne, or still rolling and projected past the landmark); a crossing under way → clear once
/// its tail is past the far pavement edge before the threshold (§3-7-2.g.2(a)); anything else on the pavement (lined up,
/// holding in position, parked) has no exception at any distance. An aircraft past a hold line off the pavement
/// (<see cref="RunwayUseKind.Fouling"/>) counts only with a part inside <see cref="GoAroundEdgeBandFt"/> of the centreline,
/// where a pilot on short final sees it in the landing corridor; a smaller overrun is the controller's advisory to give.
/// Simplifications, stated: a.1's exception is a daytime rule and the sim has no clock; intersecting runways (§3-10-4)
/// are not classified; a rollout is projected at constant speed; an altitude-restricted low approach (§3-10-10) is not
/// exempted because the sim's low approach is flown well below the 500 ft that paragraph requires; there is no balked
/// landing from the flare (the window closes at threshold-crossing height).
/// The solo evaluator's runway-user finding shares the crossing projection (<see cref="CrossingFarBar"/>,
/// <see cref="SecondsToCrossingClear"/>) and the occupant wording (<see cref="DescribeUse"/>).
/// </summary>
public static class OccupiedRunwayGoAround
{
    private static readonly ILogger Log = SimLog.CreateLogger("OccupiedRunwayGoAround");

    /// <summary>Decision window: the pilot commits to going around inside this many seconds from the threshold.</summary>
    public const double DecisionWindowSeconds = 30.0;

    /// <summary>Below threshold-crossing height the aircraft is landing; it no longer goes around for traffic.</summary>
    public const double MinimumAglFt = RunwayOccupancy.LandingAglCeilingFt;

    /// <summary>
    /// Go-around edge band (ft from the centreline) of a runway under 75 ft wide: the runway safety area half-width of a small
    /// A/B-I runway (AC 150/5300-13B; not in the local FAA files, owner to confirm against AC 150/5300-13B).
    /// </summary>
    public const double SmallRunwayEdgeBandFt = 60.0;

    /// <summary>
    /// Go-around edge band (ft from the centreline) of a runway 75–99 ft wide: the runway safety area half-width of a B-II runway
    /// (AC 150/5300-13B; not in the local FAA files, owner to confirm against AC 150/5300-13B).
    /// </summary>
    public const double MediumRunwayEdgeBandFt = 75.0;

    /// <summary>
    /// Go-around edge band (ft from the centreline) of a runway 100 ft wide or more: the A/B-III runway safety area half-width,
    /// used for every runway that wide as a band a crew on short final can see, because a C/D runway safety area (250 ft
    /// half-width) reaches the standard hold line (AC 150/5300-13B; not in the local FAA files, owner to confirm against
    /// AC 150/5300-13B).
    /// </summary>
    public const double LargeRunwayEdgeBandFt = 150.0;

    /// <summary>
    /// How far (ft) inside the runway's authored hold line the edge band stops at most, so a runway whose hold line is closer than
    /// its safety area still leaves an advisory-only band past the line.
    /// </summary>
    public const double EdgeBandHoldLineMarginFt = 25.0;

    /// <summary>How far ahead (ft) a crosser's heading is probed for the rate it closes on the far pavement edge.</summary>
    private const double CrossingHeadingProbeFt = 10.0;

    /// <summary>
    /// The occupant that refuses the arrival its threshold crossing, with the §3-10-3 branch that refused it —
    /// "landing rollout, 4,700 ft down, not clear in 30 s" — for the terminal line. The pilot's transmission stays
    /// the generic "going around, traffic on the runway" (a pilot does not read out the blocker's callsign), so this
    /// is the only place the instructor is told which aircraft it was.
    /// </summary>
    public sealed record BlockingOccupant(AircraftState Aircraft, string Reason);

    /// <summary>
    /// Goes around when the session setting is on, the arrival is fixed-wing (§3-10-3.a.3 lets visual separation
    /// replace the distance minima for a helicopter), not under a forced landing, inside the decision window, above
    /// <see cref="MinimumAglFt"/>, and a blocking occupant is on its runway. Returns true when a go-around was installed.
    /// </summary>
    public static bool TryTrigger(PhaseContext ctx)
    {
        AircraftState arrival = ctx.Aircraft;
        RunwayInfo? runway = ctx.Runway;
        if (
            (!ctx.AutoGoAroundOnOccupiedRunway)
            || (ctx.ListAircraft is null)
            || (runway is null)
            || (arrival.Phases?.ForceLanding == true)
            || (ctx.Category == AircraftCategory.Helicopter)
        )
        {
            return false;
        }

        double agl = arrival.Altitude - runway.ElevationFt;
        double seconds = RunwayOccupancy.SecondsToLandingThreshold(arrival, runway, ctx.GroundLayout);
        if ((agl < MinimumAglFt) || (seconds <= 0) || (seconds > DecisionWindowSeconds))
        {
            return false;
        }

        BlockingOccupant? blocker = FindBlockingOccupant(ctx.ListAircraft(), arrival, runway, ctx.GroundLayout, seconds);
        if (blocker is null)
        {
            return false;
        }

        Log.LogDebug(
            "[OccupiedRunwayGoAround] {Callsign}: going around, {Occupant} on runway {Runway} — {Reason} ({Seconds:F0}s from threshold, {Agl:F0} ft AGL)",
            arrival.Callsign,
            blocker.Aircraft.Callsign,
            runway.Designator,
            blocker.Reason,
            seconds,
            agl
        );
        GoAroundHelper.Trigger(ctx, Pilot.PilotResponder.BuildGoingAroundTrafficOnRunway(arrival));
        arrival.PendingWarnings.Add($"{arrival.Callsign} go-around: {blocker.Aircraft.Callsign} on {runway.Designator} ({blocker.Reason})");
        return true;
    }

    /// <summary>
    /// The first aircraft on <paramref name="runway"/> the arrival may not cross the landing threshold behind,
    /// judged <paramref name="secondsToThreshold"/> from now with the occupant's present ground speed held.
    /// </summary>
    public static BlockingOccupant? FindBlockingOccupant(
        IReadOnlyList<AircraftState> aircraft,
        AircraftState arrival,
        RunwayInfo runway,
        AirportGroundLayout? layout,
        double secondsToThreshold
    )
    {
        LatLon landingThreshold = LandingThreshold.Resolve(runway, layout);
        double runwayEndFt = runway.PavementLengthFt - LandingThreshold.DisplacementFt(runway, layout);
        SrsCategory arrivalCategory = SameRunwaySeparation.ResolveSrsCategory(arrival);

        foreach (AircraftState other in aircraft)
        {
            if (ReferenceEquals(other, arrival))
            {
                continue;
            }

            RunwayUse? use = RunwayOccupancy.Classify(other, runway, layout);
            if (
                (use is null)
                || (use.Kind == RunwayUseKind.ShortFinal)
                || ((use.Kind == RunwayUseKind.Fouling) && !IsInsideEdgeBand(other, runway, layout))
            )
            {
                continue;
            }

            string? reason =
                ((layout is not null) && (CrossingFarBar(other, use.Kind, runway, layout) is { } farBar))
                    ? CrossingReason(other, runway, layout, farBar, secondsToThreshold)
                    : BlockingReason(other, use.Kind, runway, landingThreshold, runwayEndFt, arrivalCategory, secondsToThreshold);
            if (reason is not null)
            {
                return new BlockingOccupant(other, reason);
            }
        }

        return null;
    }

    /// <summary>
    /// Half-width (ft from the centreline) of the band beside <paramref name="runway"/> inside which a fouler makes the arrival go
    /// around: the runway safety area's half-width for its width class (<see cref="SmallRunwayEdgeBandFt"/>,
    /// <see cref="MediumRunwayEdgeBandFt"/>, <see cref="LargeRunwayEdgeBandFt"/>; the width classes
    /// <see cref="RunwayCrossingDetector.HoldShortDistanceForWidth"/> reads as design groups), never nearer than
    /// <see cref="EdgeBandHoldLineMarginFt"/> to the runway's hold line where <paramref name="layout"/> authors one.
    /// </summary>
    public static double GoAroundEdgeBandFt(RunwayInfo runway, AirportGroundLayout? layout)
    {
        double bandFt = runway.WidthFt switch
        {
            < 75.0 => SmallRunwayEdgeBandFt,
            < 100.0 => MediumRunwayEdgeBandFt,
            _ => LargeRunwayEdgeBandFt,
        };
        AirportGroundLayout? ownLayout = ((layout is not null) && RunwayOccupancy.IsLayoutOf(layout, runway)) ? layout : null;
        double? holdLineFt = ownLayout?.Runways.FirstOrDefault(r => r.Id.Overlaps(runway.Id))?.HoldShortDistanceFt;
        return holdLineFt is { } authoredFt ? Math.Min(bandFt, authoredFt - EdgeBandHoldLineMarginFt) : bandFt;
    }

    /// <summary>Some part of <paramref name="occupant"/> (<see cref="AircraftBody"/>) lies inside <see cref="GoAroundEdgeBandFt"/>.</summary>
    private static bool IsInsideEdgeBand(AircraftState occupant, RunwayInfo runway, AirportGroundLayout? layout)
    {
        double bandFt = GoAroundEdgeBandFt(runway, layout);
        var body = AircraftBody.Of(occupant.AircraftType);
        double headingDeg = occupant.TrueHeading.Degrees;
        return AircraftBody.AllParts.Any(part => RunwayHoldBars.CentrelineFt(runway, body.PointOf(part, occupant.Position, headingDeg)) <= bandFt);
    }

    /// <summary>
    /// The far-side bar of a fixed-wing crossing of <paramref name="runway"/> under way on its pavement
    /// (<see cref="CrossingRunwayPhase"/>, classified <see cref="RunwayUseKind.Crossing"/>), or null for any other occupant.
    /// </summary>
    public static GroundNode? CrossingFarBar(AircraftState occupant, RunwayUseKind kind, RunwayInfo runway, AirportGroundLayout layout)
    {
        if (
            (kind != RunwayUseKind.Crossing)
            || RunwayOccupancy.IsRotorcraft(occupant)
            || (occupant.Phases?.CurrentPhase is not CrossingRunwayPhase crossing)
        )
        {
            return null;
        }

        bool farBarOfRunway = RunwayOccupancy.IsLayoutOf(layout, runway) && RunwayOccupancy.IsBarOf(layout, crossing.TargetNodeId, runway);
        return farBarOfRunway ? layout.Nodes[crossing.TargetNodeId] : null;
    }

    /// <summary>
    /// §3-7-2.g.2(a): a crossing satisfies the arrival when it "has completed crossing prior to an arriving aircraft crossing the
    /// landing threshold" (REFERENCE P/CG Clear of the Runway). Null when it is projected clear in time
    /// (<see cref="SecondsToCrossingClear"/>), else why it refuses the threshold crossing.
    /// </summary>
    private static string? CrossingReason(
        AircraftState crosser,
        RunwayInfo runway,
        AirportGroundLayout layout,
        GroundNode farBar,
        double secondsToThreshold
    )
    {
        double clearSeconds = SecondsToCrossingClear(crosser, runway, layout, farBar);
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug(
                "[OccupiedRunwayGoAround] {Callsign} crossing {Runway}: tail clear of the far edge in {Clear:F1} s, threshold in {Threshold:F1} s (gs {Gs:F1} kt, target {Target} kt, {Lateral:F0} ft from the centreline, heading {Heading:F0})",
                crosser.Callsign,
                runway.Designator,
                clearSeconds,
                secondsToThreshold,
                crosser.GroundSpeed,
                crosser.Targets.TargetSpeed,
                RunwayHoldBars.CentrelineFt(runway, crosser.Position),
                crosser.TrueHeading.Degrees
            );
        }

        return (clearSeconds <= secondsToThreshold) ? null : $"{DescribeUse(RunwayUseKind.Crossing)}, not clear in {secondsToThreshold:F0} s";
    }

    /// <summary>
    /// Seconds until the crosser's tail is past <paramref name="runway"/>'s far-side pavement edge — the
    /// <see cref="RunwayOccupancy.IsWithinPavement"/> boundary (P/CG "Clear of the Runway" b; AIM 4-3-21.b) — on its present
    /// heading, from its present ground speed to its commanded taxi speed (<see cref="TaxiSecondsToCover"/>: accelerating at the
    /// taxi rate, <see cref="CategoryPerformance.TaxiAccelRate"/>, or braking at <see cref="CategoryPerformance.TaxiDecelRate"/>).
    /// Infinite — never clear — when it is restricted short of the far marking
    /// (<see cref="RunwayOccupancy.IsRestrictedShortOfTheMarking"/>: a hold, a give-way in effect, a ground-conflict yield, or a
    /// route stop that leaves part of it inside), commanded to a stop, or not heading toward the far side.
    /// </summary>
    public static double SecondsToCrossingClear(AircraftState crosser, RunwayInfo runway, AirportGroundLayout layout, GroundNode farBar)
    {
        if (RunwayOccupancy.IsRestrictedShortOfTheMarking(crosser, runway, layout))
        {
            return double.PositiveInfinity;
        }

        bool farRight = RunwayHoldBars.IsRightOfCentreline(runway, farBar.Position);
        double TowardFarSideFt(LatLon point) =>
            ((RunwayHoldBars.IsRightOfCentreline(runway, point) == farRight) ? 1.0 : -1.0) * RunwayHoldBars.CentrelineFt(runway, point);

        double nowFt = TowardFarSideFt(crosser.Position);
        LatLon ahead = GeoMath.ProjectPoint(crosser.Position, crosser.TrueHeading, CrossingHeadingProbeFt / GeoMath.FeetPerNm);
        double acrossPerFt = (TowardFarSideFt(ahead) - nowFt) / CrossingHeadingProbeFt;
        if (acrossPerFt <= 0.0)
        {
            return double.PositiveInfinity;
        }

        double tailOffsetFt = AircraftBody.Of(crosser.AircraftType).LengthFt / 2.0 * acrossPerFt;
        double edgeFt = (runway.WidthFt / 2.0) + RunwayOccupancy.LateralSlackFt;
        double pathFt = Math.Max(0.0, edgeFt + tailOffsetFt - nowFt) / acrossPerFt;
        AircraftCategory category = AircraftCategorization.Categorize(crosser.AircraftType);
        double commandedKts = crosser.Targets.TargetSpeed ?? CategoryPerformance.TaxiSpeed(category);
        return TaxiSecondsToCover(
            pathFt,
            crosser.GroundSpeed,
            commandedKts,
            CategoryPerformance.TaxiAccelRate(category),
            CategoryPerformance.TaxiDecelRate(category)
        );
    }

    /// <summary>
    /// Seconds to cover <paramref name="distanceFt"/> from <paramref name="fromKts"/> at the commanded speed
    /// <paramref name="commandedKts"/>: accelerating up to it at <paramref name="accelKtsPerSecond"/>, or braking down to it at
    /// <paramref name="decelKtsPerSecond"/>, then holding it — never a speed above the commanded one for longer than braking to it
    /// takes. Infinite when the commanded speed is at or below <see cref="RunwayOccupancy.StoppedGroundSpeedKts"/>: the aircraft is
    /// being brought to a stop.
    /// </summary>
    internal static double TaxiSecondsToCover(
        double distanceFt,
        double fromKts,
        double commandedKts,
        double accelKtsPerSecond,
        double decelKtsPerSecond
    )
    {
        const double FtPerSecondPerKt = GeoMath.FeetPerNm / 3600.0;
        if (commandedKts <= RunwayOccupancy.StoppedGroundSpeedKts)
        {
            return double.PositiveInfinity;
        }

        double startKts = Math.Max(0.0, fromKts);
        double rateKtsPerSecond = (startKts < commandedKts) ? accelKtsPerSecond : -decelKtsPerSecond;
        double changeSeconds = (commandedKts - startKts) / rateKtsPerSecond;
        double changeFt = (startKts + commandedKts) / 2.0 * changeSeconds * FtPerSecondPerKt;
        if (changeFt >= distanceFt)
        {
            // The path ends before the commanded speed is reached: the smaller root of v0·t + a·t²/2 = d.
            double v0 = startKts * FtPerSecondPerKt;
            double a = rateKtsPerSecond * FtPerSecondPerKt;
            return (Math.Sqrt((v0 * v0) + (2.0 * a * distanceFt)) - v0) / a;
        }

        return changeSeconds + ((distanceFt - changeFt) / (commandedKts * FtPerSecondPerKt));
    }

    /// <summary>
    /// Why <paramref name="occupant"/> refuses the arrival its threshold crossing, or null when §3-10-3 is satisfied
    /// and it may land behind it.
    /// </summary>
    private static string? BlockingReason(
        AircraftState occupant,
        RunwayUseKind kind,
        RunwayInfo runway,
        LatLon landingThreshold,
        double runwayEndFt,
        SrsCategory arrivalCategory,
        double secondsToThreshold
    )
    {
        // §3-10-3.a.1/a.2's exceptions are written in SRS Categories I–III — fixed-wing classes — and a.3's helicopter
        // relief is for the succeeding aircraft only. A preceding rotorcraft (hovering, descending, air-taxiing) has no
        // codified exception at any distance: the runway must be clear.
        if (RunwayOccupancy.IsRotorcraft(occupant))
        {
            return "rotorcraft on the runway";
        }

        SrsCategory occupantCategory = SameRunwaySeparation.ResolveSrsCategory(occupant);
        double downfieldNowFt = GeoMath.AlongTrackDistanceNm(occupant.Position, landingThreshold, runway.TrueHeading) * GeoMath.FeetPerNm;
        double projectedFt = downfieldNowFt + ((occupant.GroundSpeed * GeoMath.FeetPerNm / 3600.0) * secondsToThreshold);

        // The occupant is judged where it will be when the arrival crosses the threshold: still on the pavement then
        // (the classifier already said so now) and, for a departure, flying by then if it is airborne or rolling now. A
        // landed aircraft past its hold line is stopped or restricted short of the marking, so it is never projected clear;
        // the landmark distance still applies to it.
        if (LandedHere(occupant, kind, runway))
        {
            bool satisfied = SameRunwaySeparation.ArrivalBehindLandingSatisfied(
                (kind != RunwayUseKind.Fouling) && WillBeClearOfRunway(occupant, secondsToThreshold),
                landerOnGround: true,
                projectedFt,
                occupantCategory,
                arrivalCategory
            );
            return satisfied ? null : $"landing rollout, {RoundedFeet(downfieldNowFt)} ft down, not clear in {secondsToThreshold:F0} s";
        }

        if (DepartedHere(occupant, kind, runway))
        {
            bool satisfied = SameRunwaySeparation.ArrivalBehindDepartureSatisfied(
                departureCrossedRunwayEnd: projectedFt >= runwayEndFt,
                SameRunwaySeparation.WillBeFlying(occupant, secondsToThreshold),
                projectedFt,
                occupantCategory,
                arrivalCategory
            );
            return satisfied ? null : $"departing, {RoundedFeet(downfieldNowFt)} ft down";
        }

        // Everything else on the pavement — lined up, holding in position, parked, a crossing with no crossing phase — or past
        // its hold line has no §3-10-3 exception at any distance, and is refused without projecting where it will be. That is
        // also what keeps the §3-10-6.b carve-out satisfied: anticipating separation must not be applied to LUAW operations,
        // and a lined-up occupant classifies OnSurface and reaches this branch instead of WillBeClearOfRunway. Extending
        // the projection here would break that.
        return $"{DescribeUse(kind)}, {RoundedFeet(downfieldNowFt)} ft down";
    }

    /// <summary>
    /// Will the occupant be clear of the runway by the time the arrival crosses the threshold? §3-10-3.a.1 is a
    /// threshold-crossing rule — "the arriving aircraft does not cross the landing threshold until … the other
    /// aircraft has landed and is clear of the runway" — so the question is asked about the moment of the crossing,
    /// not about now, <see cref="DecisionWindowSeconds"/> earlier. §3-10-6.a is what authorises <em>anticipating</em>
    /// that separation rather than waiting to observe it: "landing clearance to succeeding aircraft in a landing
    /// sequence need not be withheld if you observe the positions of the aircraft and determine that prescribed
    /// runway separation will exist when the aircraft crosses the landing threshold". Requiring the leader to be
    /// physically clear at the decision point is stricter than both and than tower practice, where the arrival is
    /// told to continue and the landing clearance comes late.
    ///
    /// <para>The estimate comes from <see cref="SameRunwayArrivalProtection.TryBuildRollout"/> — the same arithmetic
    /// the simulated-TRACON spacing pass uses to decide when a leader is off the runway, so the two cannot disagree.
    /// Fail closed: an occupant with no resolved exit, no ground layout to resolve one from, or no ground speed to
    /// cover the distance is <em>not</em> clear, and the go-around fires exactly as it did before. An occupant stopped
    /// (<see cref="RunwayOccupancy.StoppedGroundSpeedKts"/>) is never projected clear, whatever its exit plan says: the
    /// vacate arithmetic already returns "never" for an aircraft with distance left and no speed, and this closes the one
    /// case it cannot — an aircraft standing still past its exit's branch point, where only the exit leg is left and that
    /// leg is flown at the exit's planned turn-off speed.</para>
    /// </summary>
    private static bool WillBeClearOfRunway(AircraftState occupant, double secondsToThreshold) =>
        (occupant.GroundSpeed > RunwayOccupancy.StoppedGroundSpeedKts)
        && (SameRunwayArrivalProtection.TryBuildRollout(occupant, elapsedSinceThresholdSeconds: 0.0) is { } rollout)
        && (SameRunwayArrivalProtection.SecondsToRunwayClear(rollout) <= secondsToThreshold);

    /// <summary>
    /// The occupant's runway use in the instructor's words, for an occupant with no §3-10-3 exception at all; the solo
    /// evaluator's runway-user finding uses the same words.
    /// </summary>
    internal static string DescribeUse(RunwayUseKind kind) =>
        kind switch
        {
            RunwayUseKind.Departing => "departure roll",
            RunwayUseKind.Landing => "over the runway",
            RunwayUseKind.Crossing => "crossing the runway",
            RunwayUseKind.Fouling => "past the hold line",
            _ => "on the runway",
        };

    /// <summary>Distance down the runway to the nearest 100 ft — the terminal line carries no false precision.</summary>
    private static string RoundedFeet(double feet) =>
        (Math.Round(feet / 100.0) * 100.0).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// §3-10-3.a.1 applies: the occupant landed on this runway and is rolling out, exiting, stopped after landing, or held after
    /// its exit (<see cref="HoldingAfterExitPhase"/>, here only: the solo evaluator's pair rules read "not landing family" as
    /// clear, and its runway-user finding scores such an aircraft as an ordinary runway user, sharing <see cref="DescribeUse"/>).
    /// </summary>
    private static bool LandedHere(AircraftState occupant, RunwayUseKind kind, RunwayInfo runway) =>
        (
            SameRunwaySeparation.IsLandingFamilyOccupant(occupant.Phases?.CurrentPhase, kind)
            || (occupant.Phases?.CurrentPhase is HoldingAfterExitPhase)
        ) && ((occupant.Phases is null) || UsesThisRunway(occupant, runway));

    /// <summary>§3-10-3.a.2 applies: the occupant is departing from this runway.</summary>
    private static bool DepartedHere(AircraftState occupant, RunwayUseKind kind, RunwayInfo runway) =>
        SameRunwaySeparation.IsDepartureFamilyOccupant(occupant.Phases?.CurrentPhase, kind)
        && ((occupant.Phases is null) || UsesThisRunway(occupant, runway));

    /// <summary>
    /// The occupant's phase runway (departure, else assigned) is this pavement — an exit from a crossing runway earns no credit
    /// here.
    /// </summary>
    private static bool UsesThisRunway(AircraftState occupant, RunwayInfo runway)
    {
        RunwayInfo? own = occupant.Phases?.DepartureRunway ?? occupant.Phases?.AssignedRunway;
        return (own is not null) && Data.NavigationDatabase.AirportIdsMatch(own.AirportId, runway.AirportId) && own.Id.Overlaps(runway.Id);
    }
}
