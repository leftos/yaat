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
        return flags | ReportedInSight(ac);
    }

    /// <summary>Whether a phase reads the aircraft's ground layout here: only taxiing and holding short do.</summary>
    public static bool NeedsGroundLayout(Phase? phase) => phase is TaxiingPhase or HoldingShortPhase;

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

            if (
                (bar.Reason == HoldShortReason.RunwayCrossing)
                || ((bar.Reason == HoldShortReason.ExplicitHoldShort) && (RunwayAt(bar, layout) is not null))
            )
            {
                return true;
            }
        }

        return false;
    }

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
