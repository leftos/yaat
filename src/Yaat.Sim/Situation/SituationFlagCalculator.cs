using Yaat.Sim.ControllerAi.Rules;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Situation;

/// <summary>
/// Computes an aircraft's <see cref="SituationFlags"/> once a second in the <c>Situation</c> spine step, after the
/// situation itself. The latched flags read the previous second's bits, which <see cref="AircraftSituationState.Flags"/>
/// stores; nothing else is remembered between seconds.
/// </summary>
public static class SituationFlagCalculator
{
    /// <summary>A taxiing aircraft is nearing its departure hold line at or inside this along-route distance (enter).</summary>
    public const double NearingDepartureHoldLineFt = 1200.0;

    /// <summary>While latched, the nearing flag clears only past this along-route distance.</summary>
    public const double NearingDepartureHoldLineLeaveFt = 1300.0;

    /// <summary>While latched inside the final approach fix, the flag clears only past this cross-track from the extended centerline.</summary>
    public const double InsideFafLatchMaxCrossTrackNm = 1.0;

    /// <summary>A rollout is decelerating once ground speed is this far below the touchdown ground speed...</summary>
    public const double RolloutSlowedBelowTouchdownKts = 10.0;

    /// <summary>...or once the aircraft has rolled this far along the runway from its touchdown point.</summary>
    public const double RolloutDistanceFt = 1000.0;

    /// <summary>Returns the aircraft's situation flags for this second.</summary>
    /// <param name="ac">The aircraft.</param>
    /// <param name="situation">The situation the classifier just computed for this second.</param>
    /// <param name="previous">The flags stored last second; <see cref="SituationFlags.None"/> when none.</param>
    /// <param name="groundLayout">
    /// The aircraft's airport ground layout, as its phases see it, for the taxi and hold-short flags; null when none is
    /// loaded or the current phase does not need it (<see cref="NeedsGroundLayout"/>).
    /// </param>
    /// <param name="runwayLayout">
    /// The ground layout of the assigned runway's own airport, for the inside-FAF test's threshold displacement; null when
    /// none is loaded or the current phase is not an instrument approach phase (<see cref="IsInstrumentApproachPhase"/>).
    /// </param>
    public static SituationFlags Compute(
        AircraftState ac,
        AircraftSituation situation,
        SituationFlags previous,
        AirportGroundLayout? groundLayout,
        AirportGroundLayout? runwayLayout
    )
    {
        SituationFlags flags = SituationFlags.None;
        flags |= When(
            SituationFlags.NearingDepartureHoldLine,
            IsNearingDepartureHoldLine(ac, groundLayout, Had(previous, SituationFlags.NearingDepartureHoldLine))
        );
        flags |= When(SituationFlags.HoldShortIsDepartureRunway, IsHoldingShortOfDepartureRunway(ac, groundLayout));
        flags |= When(
            SituationFlags.InsideFinalApproachFix,
            IsInsideFinalApproachFix(ac, situation, runwayLayout, Had(previous, SituationFlags.InsideFinalApproachFix))
        );
        flags |= When(SituationFlags.RolloutDecelerating, IsRolloutDecelerating(ac, Had(previous, SituationFlags.RolloutDecelerating)));
        flags |= When(SituationFlags.HasTakeoffClearance, ac.Phases?.DepartureClearance is { Type: ClearanceType.ClearedForTakeoff });
        flags |= When(SituationFlags.PastV1, IsPastV1(ac));
        flags |= When(SituationFlags.ApproachClearedForDescent, IsClearedForApproachDescent(ac));
        return flags | ReportedInSight(ac);
    }

    /// <summary>
    /// Whether the aircraft's ground layout is read here: while holding short, and in the taxiing, holding-on-ground and
    /// rollout/exit situations, whose next-crossing test needs it to tell a runway's explicit hold-short.
    /// </summary>
    /// <param name="situation">The situation the classifier just computed for this second.</param>
    /// <param name="phase">The aircraft's current phase.</param>
    public static bool NeedsGroundLayout(AircraftSituation situation, Phase? phase) =>
        IsGroundMovementSituation(situation) || (phase is HoldingShortPhase);

    private static bool IsGroundMovementSituation(AircraftSituation situation) =>
        situation is AircraftSituation.Taxiing or AircraftSituation.HoldingOnGround or AircraftSituation.RolloutExit;

    /// <summary>
    /// Cleared for an approach with descent on it (not a JFAC/JLOC lateral intercept), and not since gone around or into the
    /// missed approach or its published hold: a second approach needs a new clearance (§4-8-9.a).
    /// </summary>
    private static bool IsClearedForApproachDescent(AircraftState ac) =>
        (ac.Phases is { ActiveApproach: { LateralInterceptOnly: false } clearance } phases) && (!HasEndedTheApproach(phases, clearance));

    /// <summary>
    /// A go-around flown since the clearance, the missed approach, or the hold at the clearance's missed-approach holding fix.
    /// The go-around keeps the clearance on the aircraft (it authorizes the missed approach), so it is read from the phase
    /// list instead: every approach clearance starts a new list, so a go-around in the list came after it, and stays there,
    /// completed or skipped, through whatever follows it. The hold is recognised by its fix: holding there with the
    /// clearance still on the aircraft is the published missed-approach hold.
    /// </summary>
    private static bool HasEndedTheApproach(PhaseList phases, ApproachClearance clearance) =>
        HasGoneAround(phases)
        || phases.CurrentPhase switch
        {
            ApproachNavigationPhase { IsMissedApproach: true } => true,
            HoldingPatternPhase hold => (clearance.MapHold is { } mapHold)
                && string.Equals(hold.FixName, mapHold.FixName, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>Going around now, or a go-around already flown (completed, or skipped by a later command) in this phase list.</summary>
    private static bool HasGoneAround(PhaseList phases) =>
        (phases.CurrentPhase is GoAroundPhase) || phases.Phases.Any(phase => phase is GoAroundPhase { Status: not PhaseStatus.Pending });

    /// <summary>The phases on which the inside-FAF flag is computed: an approach (not the missed approach), its intercept, a procedure turn, the final.</summary>
    public static bool IsInstrumentApproachPhase(Phase? phase) =>
        phase is ApproachNavigationPhase { IsMissedApproach: false } or InterceptCoursePhase or ProcedureTurnPhase or FinalApproachPhase;

    private static bool Had(SituationFlags previous, SituationFlags flag) => (previous & flag) == flag;

    private static SituationFlags When(SituationFlags flag, bool isSet) => isSet ? flag : SituationFlags.None;

    /// <summary>
    /// Taxiing, with the departure-runway bar uncleared and close along the route, and every runway on the way crossed
    /// (7110.65 §3-9-10.d): no runway bar left before it and not on another runway's pavement, past its bar (the route
    /// carries no far-side bar). The along-route distance stops at the first uncleared bar, so a cleared crossing is
    /// found by its own walk.
    /// </summary>
    private static bool IsNearingDepartureHoldLine(AircraftState ac, AirportGroundLayout? layout, bool latched)
    {
        if ((ac.Phases?.CurrentPhase is not TaxiingPhase) || (TaxiRouteProgress.DistanceToDestinationBarFt(ac, layout) is not { } distanceFt))
        {
            return false;
        }

        double limitFt = latched ? NearingDepartureHoldLineLeaveFt : NearingDepartureHoldLineFt;
        return (distanceFt <= limitFt)
            && (!HasRunwayBarBeforeDestinationBar(ac.Ground.AssignedTaxiRoute, layout))
            && (!IsOnAnotherRunway(ac, layout));
    }

    /// <summary>On the pavement of a runway at the departure airport other than the assigned departure runway.</summary>
    private static bool IsOnAnotherRunway(AircraftState ac, AirportGroundLayout? layout)
    {
        RunwayInfo? assigned = ac.Phases?.AssignedRunway;
        string? airportId = assigned?.AirportId ?? layout?.AirportId;
        if ((airportId is null) || (NavigationDatabase.InstanceOrNull is not { } navDb))
        {
            return false;
        }

        foreach (RunwayInfo runway in navDb.GetRunways(airportId))
        {
            if (((assigned is null) || (!assigned.Id.Overlaps(runway.Id))) && RunwayOccupancy.IsOnPavement(ac, runway))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A runway-crossing bar, cleared or not, on the remaining route before its first departure-runway bar.</summary>
    private static bool HasRunwayBarBeforeDestinationBar(TaxiRoute? route, AirportGroundLayout? layout)
    {
        foreach (HoldShortPoint bar in RemainingBars(route))
        {
            if (bar.Reason == HoldShortReason.DestinationRunway)
            {
                return false;
            }

            if (IsRunwayCrossingBar(bar, layout))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The runway to cross next (7110.65 §3-7-2), computed once a second beside the flags: while taxiing, holding on the
    /// ground or rolling out, the runway whose bar is the first uncleared one on the remaining route. None is offered while
    /// the previous runway is not yet crossed (§3-7-2.c: one crossing at a time): while a runway already cleared to cross
    /// lies ahead of that bar, and while the aircraft stands on the pavement of a runway whose bar lies behind it on the
    /// route, past the near-side bar and still on the runway. A cleared bar of the runway the aircraft is on (rolling out,
    /// exiting, clearing, or standing on it with a route that starts there) is ahead of it, so leaving that runway is not
    /// a crossing of another. An explicit hold-short of the assigned runway with no departure-runway bar after it is an
    /// intersection departure, not a crossing.
    /// </summary>
    /// <param name="ac">The aircraft.</param>
    /// <param name="situation">The situation the classifier just computed for this second.</param>
    /// <param name="groundLayout">The aircraft's airport ground layout (<see cref="NeedsGroundLayout"/>); null when none is loaded.</param>
    /// <returns>
    /// The display designator of the end to name in <c>CROSS</c> (<see cref="RunwayCrossingEnd.Nearest"/>); null when the
    /// next uncleared bar is the departure runway's or a taxiway's, when every bar is cleared, with no taxi route, or in
    /// any other situation.
    /// </returns>
    public static string? NextCrossingRunway(AircraftState ac, AircraftSituation situation, AirportGroundLayout? groundLayout)
    {
        if (
            (!IsGroundMovementSituation(situation))
            || IsStillCrossingAPassedRunwayBar(ac, groundLayout)
            || (FirstUnclearedBarWithNoCrossingBefore(ac, groundLayout) is not { } next)
            || (!IsRunwayCrossingBar(next, groundLayout))
            || IsIntersectionDepartureBar(ac, next, groundLayout)
            || (CrossedRunwayTarget(next, groundLayout) is not { } target)
        )
        {
            return null;
        }

        return RunwayCrossingEnd.Nearest(ac, target, groundLayout);
    }

    /// <summary>
    /// The first uncleared bar on the remaining route; null when none, or when a cleared crossing of another runway comes
    /// first. A cleared bar of the runway the aircraft is on is passed over: leaving that runway is not crossing another.
    /// </summary>
    private static HoldShortPoint? FirstUnclearedBarWithNoCrossingBefore(AircraftState ac, AirportGroundLayout? layout)
    {
        foreach (HoldShortPoint bar in RemainingBars(ac.Ground.AssignedTaxiRoute))
        {
            if (!bar.IsCleared)
            {
                return bar;
            }

            if (IsRunwayCrossingBar(bar, layout) && (!IsBarOfRunwayUnderfoot(ac, bar, layout)))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Mid-crossing: on the pavement of a runway whose runway bar lies behind the aircraft on its taxi route (a segment
    /// before the current one ends at it), so it has passed that runway's near-side bar and not yet left the runway.
    /// </summary>
    private static bool IsStillCrossingAPassedRunwayBar(AircraftState ac, AirportGroundLayout? layout)
    {
        foreach (HoldShortPoint bar in PassedBars(ac.Ground.AssignedTaxiRoute))
        {
            if (IsRunwayCrossingBar(bar, layout) && IsOnPavementOfBarRunway(ac, bar, layout))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the aircraft stands on the pavement of a runway at the airport that <paramref name="bar"/> protects.</summary>
    private static bool IsOnPavementOfBarRunway(AircraftState ac, HoldShortPoint bar, AirportGroundLayout? layout)
    {
        string? airportId = ac.Phases?.AssignedRunway?.AirportId ?? layout?.AirportId;
        if ((CrossedRunwayTarget(bar, layout) is not { } target) || (airportId is null) || (NavigationDatabase.InstanceOrNull is not { } navDb))
        {
            return false;
        }

        var barRunway = RunwayIdentifier.Parse(target);
        return navDb.GetRunways(airportId).Any(runway => runway.Id.Overlaps(barRunway) && RunwayOccupancy.IsOnPavement(ac, runway));
    }

    /// <summary>
    /// A runway bar of the runway the aircraft is on: the landing runway on the rollout and in the runway exit, else a runway
    /// at the airport whose pavement the aircraft stands on.
    /// </summary>
    private static bool IsBarOfRunwayUnderfoot(AircraftState ac, HoldShortPoint bar, AirportGroundLayout? layout)
    {
        if (CrossedRunwayTarget(bar, layout) is not { } target)
        {
            return false;
        }

        var barRunway = RunwayIdentifier.Parse(target);
        if ((ac.Phases is { CurrentPhase: LandingPhase or RunwayExitPhase, AssignedRunway: { } landing }) && landing.Id.Overlaps(barRunway))
        {
            return true;
        }

        return IsOnPavementOfBarRunway(ac, bar, layout);
    }

    /// <summary>An explicit hold-short of the assigned runway with no departure-runway bar after it (§3-9-10.b).</summary>
    private static bool IsIntersectionDepartureBar(AircraftState ac, HoldShortPoint bar, AirportGroundLayout? layout) =>
        (bar.Reason == HoldShortReason.ExplicitHoldShort)
        && (ac.Phases?.AssignedRunway is { } assigned)
        && IsBarOfRunway(bar, layout, assigned)
        && (!RemainingBars(ac.Ground.AssignedTaxiRoute).Any(b => b.Reason == HoldShortReason.DestinationRunway));

    /// <summary>The combined id of the runway a runway bar protects: the crossing bar's target, else the runway its node stands on.</summary>
    private static string? CrossedRunwayTarget(HoldShortPoint bar, AirportGroundLayout? layout) =>
        ((bar.Reason == HoldShortReason.RunwayCrossing) && (!string.IsNullOrEmpty(bar.TargetName)))
            ? bar.TargetName
            : RunwayAt(bar, layout)?.ToString();

    /// <summary>A bar that holds short of a runway to cross: a crossing bar, or an explicit hold-short standing on a runway's hold line.</summary>
    private static bool IsRunwayCrossingBar(HoldShortPoint bar, AirportGroundLayout? layout) =>
        (bar.Reason == HoldShortReason.RunwayCrossing) || ((bar.Reason == HoldShortReason.ExplicitHoldShort) && (RunwayAt(bar, layout) is not null));

    /// <summary>On the takeoff and committed to it: airborne, or rolling at or above V1 (§3-9-11).</summary>
    private static bool IsPastV1(AircraftState ac) =>
        (ac.Phases?.CurrentPhase is TakeoffPhase) && ((!ac.IsOnGround) || RejectedTakeoff.IsAtOrPastV1(ac));

    /// <summary>
    /// Holding short of the departure runway: at the route's departure-runway bar, or at an explicit hold-short of the
    /// same physical runway as the one assigned with no departure-runway bar further on (an intersection departure,
    /// §3-9-10.b). No runway assigned reads false.
    /// </summary>
    private static bool IsHoldingShortOfDepartureRunway(AircraftState ac, AirportGroundLayout? layout)
    {
        if (ac.Phases is not { CurrentPhase: HoldingShortPhase holding, AssignedRunway: { } assigned })
        {
            return false;
        }

        HoldShortPoint bar = holding.HoldShort;
        return bar.Reason switch
        {
            HoldShortReason.DestinationRunway => true,
            HoldShortReason.ExplicitHoldShort => IsBarOfRunway(bar, layout, assigned)
                && (!RemainingBars(ac.Ground.AssignedTaxiRoute).Any(b => b.Reason == HoldShortReason.DestinationRunway)),
            _ => false,
        };
    }

    private static bool IsBarOfRunway(HoldShortPoint bar, AirportGroundLayout? layout, RunwayInfo runway) =>
        (layout is not null)
        && NavigationDatabase.AirportIdsMatch(runway.AirportId, layout.AirportId)
        && (RunwayAt(bar, layout) is { } barRunway)
        && runway.Id.Overlaps(barRunway);

    /// <summary>The runway a bar protects, when it stands on a runway hold-short node of the layout.</summary>
    private static RunwayIdentifier? RunwayAt(HoldShortPoint bar, AirportGroundLayout? layout) =>
        ((layout is not null) && layout.Nodes.TryGetValue(bar.NodeId, out GroundNode? node) && (node.Type == GroundNodeType.RunwayHoldShort))
            ? node.RunwayId
            : null;

    /// <summary>The hold-short bars at the far ends of the route's remaining segments, in route order, cleared or not.</summary>
    private static IEnumerable<HoldShortPoint> RemainingBars(TaxiRoute? route)
    {
        if (route is null)
        {
            yield break;
        }

        for (int i = route.CurrentSegmentIndex; i < route.Segments.Count; i++)
        {
            if (route.GetHoldShortAt(route.Segments[i].ToNodeId) is { } bar)
            {
                yield return bar;
            }
        }
    }

    /// <summary>The hold-short bars at the far ends of the route's segments already passed, in route order, cleared or not.</summary>
    private static IEnumerable<HoldShortPoint> PassedBars(TaxiRoute? route)
    {
        if (route is null)
        {
            yield break;
        }

        for (int i = 0; i < Math.Min(route.CurrentSegmentIndex, route.Segments.Count); i++)
        {
            if (route.GetHoldShortAt(route.Segments[i].ToNodeId) is { } bar)
            {
                yield return bar;
            }
        }
    }

    /// <summary>
    /// On an instrument approach phase (not the missed approach) and inside the final approach fix against the
    /// assigned runway. Once set it holds while the situation stays Approach or Final and the aircraft is within
    /// <see cref="InsideFafLatchMaxCrossTrackNm"/> of the extended centerline.
    /// </summary>
    private static bool IsInsideFinalApproachFix(AircraftState ac, AircraftSituation situation, AirportGroundLayout? layout, bool latched)
    {
        if ((!IsInstrumentApproachPhase(ac.Phases?.CurrentPhase)) || (ac.Phases?.AssignedRunway is not { } runway))
        {
            return false;
        }

        if (FinalApproachFix.IsInside(ac, runway, layout))
        {
            return true;
        }

        return latched
            && (situation is AircraftSituation.Approach or AircraftSituation.Final)
            && (FinalApproachFix.CrossTrackNm(ac, runway) <= InsideFafLatchMaxCrossTrackNm);
    }

    /// <summary>
    /// A full-stop landing's rollout once it has slowed or rolled well down the runway, or the runway exit. Touch-and-go
    /// and stop-and-go landings fly their own phases, and a helicopter lands in its own, so none of them reads true.
    /// Once set it holds for the rest of the rollout and the exit.
    /// </summary>
    private static bool IsRolloutDecelerating(AircraftState ac, bool latched)
    {
        Phase? phase = ac.Phases?.CurrentPhase;
        if (phase is RunwayExitPhase)
        {
            return true;
        }

        if (
            phase
            is not LandingPhase
            {
                CurrentState: LandingPhase.State.Rollout or LandingPhase.State.Handoff or LandingPhase.State.Unable or LandingPhase.State.FullStop
            } landing
        )
        {
            return false;
        }

        return latched || HasSlowedOrRolled(ac, landing);
    }

    private static bool HasSlowedOrRolled(AircraftState ac, LandingPhase landing)
    {
        if ((landing.TouchdownGroundSpeedKts is not { } touchdownKts) || (landing.TouchdownPosition is not { } touchdown))
        {
            return false;
        }

        return (ac.GroundSpeed <= (touchdownKts - RolloutSlowedBelowTouchdownKts)) || (RolledFt(ac, touchdown) >= RolloutDistanceFt);
    }

    /// <summary>Distance rolled from the touchdown point along the assigned runway; straight-line when none is assigned.</summary>
    private static double RolledFt(AircraftState ac, LatLon touchdown)
    {
        double rolledNm = ac.Phases?.AssignedRunway is { } runway
            ? GeoMath.AlongTrackDistanceNm(ac.Position, touchdown, runway.TrueHeading)
            : GeoMath.DistanceNm(ac.Position, touchdown);
        return rolledNm * GeoMath.FeetPerNm;
    }

    /// <summary>The field and traffic in-sight reports, for an IFR aircraft only; mirrors its approach state exactly.</summary>
    private static SituationFlags ReportedInSight(AircraftState ac)
    {
        if (ac.FlightPlan.IsVfr)
        {
            return SituationFlags.None;
        }

        return When(SituationFlags.HasReportedFieldInSight, ac.Approach.HasReportedFieldInSight)
            | When(SituationFlags.HasReportedTrafficInSight, ac.Approach.HasReportedTrafficInSight);
    }
}
