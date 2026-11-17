using System.Globalization;
using System.Text;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.MilitaryRoutes;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim;

/// <summary>
/// Projects an aircraft's active procedure phase and its queued holds into drawable <see cref="NavRouteShapeDto"/> shapes
/// for the radar "Show nav route" overlay. Covers the geometry the flat <see cref="NavRouteFixDto"/> route can't express: a
/// holding pattern's racetrack, a procedure turn's course reversal, the open-ended coded legs (CA/VA/CD/VD/CR/VR) of a SID,
/// a military route's corridor, the dashed racetrack of each hold queued at a fix ahead, the dashed circle of a VFR
/// (HFIX) or airspace-boundary orbit, the terminal course flown past the route's last fix, and the heading vector of a
/// heading-only departure or of a heading flown after the route ended. Pure geometry — no side effects.
/// </summary>
public static class NavRouteOverlayProjector
{
    /// <summary>Standard-rate turn: 3°/s.</summary>
    private const double StandardTurnRateDegPerSec = 3.0;

    /// <summary>Fallback holding TAS when performance data is unavailable (kt).</summary>
    private const double FallbackHoldingTasKts = 210.0;

    private const double MinTurnRadiusNm = 0.5;
    private const double MaxTurnRadiusNm = 4.0;

    /// <summary>Nominal length of an open-ended coded leg's drawn vector (nm) — enough to read the
    /// climb-out direction. The real leg ends on a condition (altitude/distance/radial), not a point.</summary>
    private const double CodedLegNominalNm = 4.0;

    /// <summary>Length of a route-end course or heading vector (nm), the client vector tail's length.</summary>
    private const double RouteEndTailNm = 5.0;

    /// <summary>
    /// Shortest final approach course line (nm), drawn back from its anchor along the reciprocal: the client's
    /// <c>ShownRouteBuilder.FacExtensionNm</c>, which an expected approach's line matches. A cleared or pending approach's
    /// line reaches further, out to the aircraft.
    /// </summary>
    private const double FinalApproachMinimumNm = 5.0;

    /// <summary>
    /// The current phase's shapes, then the route-end course or the heading vector, then the final approach course of a
    /// cleared, pending or expected approach, then one dashed racetrack per hold queued at a fix ahead, in queue order.
    /// </summary>
    public static List<NavRouteShapeDto> BuildShapes(AircraftState aircraft)
    {
        List<NavRouteShapeDto> shapes = BuildCurrentPhaseShapes(aircraft);
        if (RouteEndCourse(aircraft) is { } routeEnd)
        {
            shapes.Add(BuildVector(NavRouteShapeKind.RouteEndCourse, routeEnd.Last.Position, routeEnd.TrueCourse));
        }
        else if (HeadingVectorHeading(aircraft) is { } heading)
        {
            shapes.Add(BuildVector(NavRouteShapeKind.HeadingVector, aircraft.Position, heading));
        }

        if (FinalApproachCourse(aircraft) is { } final)
        {
            shapes.Add(BuildFinalApproachCourse(final));
        }

        foreach ((HoldingPatternCommand hold, double holdAltitudeFt) in QueuedHolds(aircraft))
        {
            shapes.Add(BuildPendingHoldRacetrack(aircraft, hold, holdAltitudeFt));
        }

        return shapes;
    }

    private static List<NavRouteShapeDto> BuildCurrentPhaseShapes(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase switch
        {
            HoldingPatternPhase hold => [BuildHoldRacetrack(aircraft, hold)],
            ProcedureTurnPhase pt => [BuildProcedureTurn(aircraft, pt)],
            DepartureProcedurePhase departure => BuildCodedLegVectors(aircraft, departure),
            MilitaryRoutePhase route => BuildMilitaryRouteCorridor(route),
            _ => CurrentOrbit(aircraft) is { } orbit ? [BuildOrbitCircle(aircraft, orbit.Centre, orbit.Direction)] : [],
        };

    /// <summary>
    /// The last route target and the true course the sim will fly past it (<see cref="FlightPhysics.ClassifyRouteEnd"/>
    /// judges it a terminal course, converted as the route end converts it), or null: no route, a pending approach
    /// clearance (activated at route end instead), or any other route-end branch.
    /// </summary>
    private static (NavigationTarget Last, TrueHeading TrueCourse)? RouteEndCourse(AircraftState aircraft)
    {
        List<NavigationTarget> route = aircraft.Targets.NavigationRoute;
        if ((route.Count == 0) || (aircraft.Approach.PendingClearance is not null))
        {
            return null;
        }

        NavigationTarget last = route[^1];
        if (
            (last.TerminalCourseMagnetic is not { } course)
            || (FlightPhysics.ClassifyRouteEnd(aircraft, last) != FlightPhysics.RouteEndLateral.TerminalCourse)
        )
        {
            return null;
        }

        return (last, new MagneticHeading(course).ToTrue(aircraft.Declination));
    }

    /// <summary>
    /// The heading an airborne aircraft flies with no route and no assigned heading (an assigned one is drawn by the
    /// client's own vector tail) while in the departure climb or in no phase: a heading-only departure, or the heading
    /// flown after the route ended. Null otherwise.
    /// </summary>
    private static TrueHeading? HeadingVectorHeading(AircraftState aircraft)
    {
        ControlTargets targets = aircraft.Targets;
        bool headingOnly = (targets.NavigationRoute.Count == 0) && !aircraft.IsOnGround && (targets.AssignedMagneticHeading is null);
        return headingOnly && (aircraft.Phases?.CurrentPhase is null or InitialClimbPhase) ? targets.TargetTrueHeading : null;
    }

    /// <summary>A two-point line from <paramref name="origin"/> along <paramref name="trueCourse"/> for <see cref="RouteEndTailNm"/>.</summary>
    private static NavRouteShapeDto BuildVector(NavRouteShapeKind kind, LatLon origin, TrueHeading trueCourse)
    {
        LatLon end = GeoMath.ProjectPoint(origin, trueCourse, RouteEndTailNm);
        return new NavRouteShapeDto(
            kind,
            [
                [origin.Lat, origin.Lon],
                [end.Lat, end.Lon],
            ],
            null,
            null,
            null
        );
    }

    /// <summary>
    /// The approach whose final an airborne aircraft draws, from stored state alone (no navdata): the first that holds of
    /// the approach it is cleared for and flying (until it lands), the approach cleared to begin at its route's end, or the
    /// approach it expects (EA). Null on the ground, with none of them, for an IFR visual approach, and from the start of a
    /// go-around, a missed approach, a low approach or a touch-and-go or stop-and-go until a new clearance
    /// (<see cref="HidesFinal"/>).
    /// </summary>
    private static FinalSource? FinalApproachSource(AircraftState aircraft)
    {
        if (aircraft.IsOnGround || (StoredFinal(aircraft) is not { } source))
        {
            return null;
        }

        return (HidesFinal(aircraft, source) || ApproachIds.TryParseVisual(source.ApproachId, out _)) ? null : source;
    }

    /// <summary>The cleared and flown (C), pending (P) or expected (X) approach, in that order of precedence.</summary>
    private static FinalSource? StoredFinal(AircraftState aircraft)
    {
        if (aircraft.Phases?.ActiveApproach is { } active)
        {
            return new FinalSource('C', active.ApproachId, active, aircraft.Phases.AssignedRunway);
        }

        if (aircraft.Approach.PendingClearance is { } pending)
        {
            return new FinalSource('P', pending.Clearance.ApproachId, pending.Clearance, pending.AssignedRunway);
        }

        return (aircraft.Approach.Expected is { Length: > 0 } expected) ? new FinalSource('X', expected, null, null) : null;
    }

    /// <summary>
    /// The approach flown in the current phase list has ended in the air (<see cref="AirborneFollowHelper.HasEndedTheApproach"/>):
    /// from then the cleared final, and an expected one behind it, stay hidden until a new clearance starts a new list.
    /// A pending clearance is one issued since, and is not hidden: it draws until it activates into a new phase list.
    /// </summary>
    private static bool HidesFinal(AircraftState aircraft, FinalSource source) =>
        (source.State != 'P') && (aircraft.Phases is { } phases) && AirborneFollowHelper.HasEndedTheApproach(phases, phases.ActiveApproach);

    /// <summary>The final approach course to draw for <see cref="FinalApproachSource"/>, or null when it cannot be placed.</summary>
    private static FinalCourse? FinalApproachCourse(AircraftState aircraft) =>
        FinalApproachSource(aircraft) switch
        {
            { Clearance: { } clearance } source => ClearedFinal(aircraft, clearance, source.Runway),
            { } source => ExpectedFinal(aircraft.FlightPlan.Destination, source.ApproachId),
            null => null,
        };

    /// <summary>
    /// A cleared approach's final from the clearance as stored: its course, from its lateral anchor (else the runway's
    /// threshold) out along the reciprocal to the aircraft's distance along it, never shorter than
    /// <see cref="FinalApproachMinimumNm"/>. Reads neither <see cref="ApproachClearance.Procedure"/> (null after a snapshot
    /// restore) nor the navdata's course, so a restored or replayed clearance draws the same line.
    /// </summary>
    private static FinalCourse? ClearedFinal(AircraftState aircraft, ApproachClearance clearance, RunwayInfo? runway)
    {
        LatLon? anchor =
            ((clearance.FinalApproachAnchorLat is { } lat) && (clearance.FinalApproachAnchorLon is { } lon)) ? new LatLon(lat, lon)
            : ((runway ?? ClearanceRunway(clearance)) is { } threshold) ? new LatLon(threshold.ThresholdLatitude, threshold.ThresholdLongitude)
            : null;
        if (anchor is not { } near)
        {
            return null;
        }

        TrueHeading reciprocal = clearance.FinalApproachCourse.ToReciprocal();
        double alongNm = GeoMath.AlongTrackDistanceNm(aircraft.Position, near, reciprocal);
        LatLon far = GeoMath.ProjectPoint(near, reciprocal, Math.Max(FinalApproachMinimumNm, alongNm));
        return new FinalCourse(NavRouteShapeKind.FinalApproachCourse, far, near);
    }

    private static RunwayInfo? ClearanceRunway(ApproachClearance clearance) =>
        NavigationDatabase.InstanceOrNull?.GetRunway(clearance.AirportCode, clearance.RunwayId);

    /// <summary>
    /// The expected approach's final, resolved as <see cref="Situation.ExpectedFinalGeometry"/> resolves it (the first
    /// candidate the expectation names at the destination that has a runway) and drawn as the client's
    /// <c>ShownRouteBuilder</c> draws it: from <see cref="FinalApproachMinimumNm"/> back along the published course's
    /// reciprocal from the lateral anchor (else the first common-leg fix), to the anchor (else the threshold). Null with no
    /// destination, no navdata or no such candidate.
    /// </summary>
    private static FinalCourse? ExpectedFinal(string destination, string expected)
    {
        if (string.IsNullOrEmpty(destination) || (NavigationDatabase.InstanceOrNull is not { } navDb))
        {
            return null;
        }

        string airport = navDb.TryResolveAirport(destination, out string canonical) ? canonical : destination;
        foreach (string id in navDb.ResolveApproachCandidates(airport, expected))
        {
            if ((navDb.GetApproach(airport, id) is { Runway: { } runwayId } procedure) && (navDb.GetRunway(airport, runwayId) is { } runway))
            {
                return ExpectedCourse(procedure, runway, navDb);
            }
        }

        return null;
    }

    /// <summary>
    /// An expected approach's line for <paramref name="procedure"/> to <paramref name="runway"/>: from
    /// <see cref="FinalApproachMinimumNm"/> back along the published course's reciprocal from the lateral anchor (else the
    /// first common-leg fix, else the threshold), to the anchor (else the threshold).
    /// </summary>
    private static FinalCourse ExpectedCourse(CifpApproachProcedure procedure, RunwayInfo runway, NavigationDatabase navDb)
    {
        FinalApproachCourseResult fac = FinalApproachCourseExtractor.Extract(procedure, runway, navDb);
        LatLon near = OriginOf(fac, runway);
        LatLon farOrigin = AnchorOf(fac) ?? FirstCommonLegFix(procedure, navDb) ?? near;
        LatLon far = GeoMath.ProjectPoint(farOrigin, fac.Course.ToReciprocal(), FinalApproachMinimumNm);
        return new FinalCourse(NavRouteShapeKind.ExpectedFinalApproachCourse, far, near);
    }

    /// <summary>The final approach course's lateral anchor, or null when the extractor found none.</summary>
    private static LatLon? AnchorOf(FinalApproachCourseResult fac) =>
        ((fac.AnchorLat is { } lat) && (fac.AnchorLon is { } lon)) ? new LatLon(lat, lon) : null;

    /// <summary>The near end of an expected final: the lateral anchor, else the runway's threshold.</summary>
    private static LatLon OriginOf(FinalApproachCourseResult fac, RunwayInfo runway) =>
        AnchorOf(fac) ?? new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);

    /// <summary>
    /// The first common-leg fix the client's <c>ShownRouteBuilder</c> draws: the first named leg's fix that resolves, skipping
    /// course reversals and holds, and none at or past the missed approach point.
    /// </summary>
    private static LatLon? FirstCommonLegFix(CifpApproachProcedure procedure, NavigationDatabase navDb)
    {
        foreach (CifpLeg leg in procedure.CommonLegs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier)))
        {
            if (leg.FixRole == CifpFixRole.MAP)
            {
                return null;
            }

            if (!IsReversalOrHold(leg) && (leg.ResolveFixPosition(navDb) is { } position))
            {
                return new LatLon(position.Lat, position.Lon);
            }
        }

        return null;
    }

    /// <summary>A procedure-turn or hold leg, which the client's drawn approach skips.</summary>
    private static bool IsReversalOrHold(CifpLeg leg) =>
        leg.PathTerminator is CifpPathTerminator.PI or CifpPathTerminator.HM or CifpPathTerminator.HF or CifpPathTerminator.HA;

    /// <summary>The final approach course as a two-point line: from its far end in along the course to its anchor.</summary>
    private static NavRouteShapeDto BuildFinalApproachCourse(FinalCourse final) =>
        new(
            final.Kind,
            [
                [final.Far.Lat, final.Far.Lon],
                [final.Near.Lat, final.Near.Lon],
            ],
            null,
            null,
            null
        );

    /// <summary>
    /// The approach a final is drawn for: its key state (C cleared and flown, P pending, X expected), its approach id (the
    /// expectation as stored for X), and for C and P the clearance and the runway it is drawn to.
    /// </summary>
    private readonly record struct FinalSource(char State, string ApproachId, ApproachClearance? Clearance, RunwayInfo? Runway);

    /// <summary>A final approach course to draw: its kind, its far end and its anchor (the near end).</summary>
    private readonly record struct FinalCourse(NavRouteShapeKind Kind, LatLon Far, LatLon Near);

    /// <summary>
    /// The orbit the current phase flies, if any: a VFR hold's fix (HFIX), also while still proceeding to it, or an
    /// airspace-boundary orbit's reference position. A hover has no orbit, a hold at present position (HPP) keeps no record
    /// of where its orbit began, and a level-off under a shelf does not turn, so none of them has one.
    /// </summary>
    private static (LatLon Centre, TurnDirection Direction)? CurrentOrbit(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase switch
        {
            VfrHoldPhase { OrbitDirection: { } dir, FixLat: { } lat, FixLon: { } lon } => (new LatLon(lat, lon), dir),
            AirspaceBoundaryHoldPhase { Mode: AirspaceHoldMode.Orbit } boundary => (boundary.ReferencePosition, boundary.OrbitDirection),
            _ => null,
        };

    /// <summary>
    /// A closed circle of standard-rate radius at the aircraft's present altitude, centred on <paramref name="centre"/>,
    /// traced in the orbit's turn direction.
    /// </summary>
    private static NavRouteShapeDto BuildOrbitCircle(AircraftState aircraft, LatLon centre, TurnDirection direction)
    {
        double radiusNm = TurnRadiusNm(aircraft);
        var points = new List<LatLon> { GeoMath.ProjectPoint(centre, new TrueHeading(0.0), radiusNm) };
        // A full sweep from north back to north: excludes the start, includes the end, closing the loop.
        points.AddRange(GeoMath.GenerateArcPoints(centre, radiusNm, 0.0, 0.0, direction == TurnDirection.Right));
        return new NavRouteShapeDto(NavRouteShapeKind.OrbitCircle, [.. points.Select(p => new[] { p.Lat, p.Lon })], null, null, null);
    }

    /// <summary>
    /// Every hold queued at a fix ahead — a <see cref="HoldingPatternCommand"/> in an unapplied block behind a reach-fix
    /// trigger — with the altitude it will be held at. A block whose parsed commands were not recovered is skipped.
    /// </summary>
    private static IEnumerable<(HoldingPatternCommand Hold, double HoldAltitudeFt)> QueuedHolds(AircraftState aircraft)
    {
        foreach (CommandBlock block in aircraft.Queue.Blocks)
        {
            if (block.IsApplied || (block.Trigger is not { Type: BlockTriggerType.ReachFix }) || (block.ParsedCommands is not { } commands))
            {
                continue;
            }

            double holdAltitudeFt = QueuedHoldAltitudeFt(aircraft, commands);
            foreach (HoldingPatternCommand hold in commands.OfType<HoldingPatternCommand>())
            {
                yield return (hold, holdAltitudeFt);
            }
        }
    }

    /// <summary>
    /// A key covering every input of the queued-hold racetracks and the orbit circle that the aircraft's position does not
    /// already cover: each queued hold's fix, course, leg, direction and hold altitude, the orbit's centre and direction, and
    /// the route-end course's fix and true course, and the final approach course's stored state (<see cref="FinalApproachKey"/>).
    /// Empty when there are none, so it leaves an existing fingerprint unchanged.
    /// The heading vector adds nothing: its origin is the aircraft's position and its heading the target heading, both in
    /// the training fingerprint already.
    /// </summary>
    internal static string BuildPendingShapeKey(AircraftState aircraft)
    {
        StringBuilder? sb = null;
        foreach ((HoldingPatternCommand hold, double holdAltitudeFt) in QueuedHolds(aircraft))
        {
            sb ??= new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"|H{hold.FixName}:{hold.FixLat:R}:{hold.FixLon:R}:{hold.InboundCourse}");
            sb.Append(CultureInfo.InvariantCulture, $":{hold.LegLength?.ToString("R", CultureInfo.InvariantCulture) ?? "-"}:{hold.IsMinuteBased}");
            sb.Append(CultureInfo.InvariantCulture, $":{hold.Direction}:{holdAltitudeFt:R}");
        }

        if (CurrentOrbit(aircraft) is { } orbit)
        {
            sb ??= new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"|O{orbit.Centre.Lat:R}:{orbit.Centre.Lon:R}:{orbit.Direction}");
        }

        if (RouteEndCourse(aircraft) is { } routeEnd)
        {
            sb ??= new StringBuilder();
            // To a tenth of a degree: the cached declination refreshes every few hundredths of a degree as the aircraft
            // moves, which must not re-send a course the scope cannot draw differently.
            sb.Append(CultureInfo.InvariantCulture, $"|E{routeEnd.Last.Name}:{routeEnd.TrueCourse.Degrees:F1}");
        }

        return (sb?.ToString() ?? "") + FinalApproachKey(aircraft);
    }

    /// <summary>
    /// The final approach course's key, from stored state only (never navdata, which a tick must not read): the cleared and
    /// pending approach's id and stored true course to a tenth of a degree, the expected approach's expectation as stored.
    /// Empty when no final is drawn for a stored reason (none, visual, hidden phase). The cleared line's far end follows the
    /// aircraft's position, which the fingerprint already covers.
    /// </summary>
    private static string FinalApproachKey(AircraftState aircraft) =>
        FinalApproachSource(aircraft) switch
        {
            { Clearance: { } clearance } source => string.Create(
                CultureInfo.InvariantCulture,
                $"|A{source.State}:{source.ApproachId}:{clearance.FinalApproachCourse.Degrees:F1}"
            ),
            { } source => $"|A{source.State}:{source.ApproachId}",
            null => "",
        };

    /// <summary>
    /// The altitude a queued hold will be held at: the last altitude assignment in its own block, else the assigned
    /// altitude, else the present one. Crossing restrictions are not read.
    /// </summary>
    private static double QueuedHoldAltitudeFt(AircraftState aircraft, List<ParsedCommand> blockCommands)
    {
        int? blockAltitude = null;
        foreach (ParsedCommand command in blockCommands)
        {
            blockAltitude = command switch
            {
                ClimbMaintainCommand climb => climb.Altitude,
                DescendMaintainCommand descend => descend.Altitude,
                ForceAltitudeCommand force => force.Altitude,
                _ => blockAltitude,
            };
        }

        return blockAltitude ?? aircraft.Targets.AssignedAltitude ?? aircraft.Altitude;
    }

    /// <summary>
    /// A queued hold's racetrack, built from the command exactly as <see cref="NavigationCommandHandler.DispatchHoldingPattern"/>
    /// will build the phase (same leg rule, same magnetic-to-true conversion), judged at the altitude it will be held at.
    /// </summary>
    private static NavRouteShapeDto BuildPendingHoldRacetrack(AircraftState aircraft, HoldingPatternCommand hold, double holdAltitudeFt)
    {
        double radiusNm = TurnRadiusNm(aircraft, holdAltitudeFt);
        (double legLength, bool isMinuteBased) = NavigationCommandHandler.ResolveHoldLeg(hold, holdAltitudeFt);
        double legNm = HoldStraightLegNm(aircraft, legLength, isMinuteBased, holdAltitudeFt, radiusNm);
        List<LatLon> points = HoldRacetrackPoints(
            new LatLon(hold.FixLat, hold.FixLon),
            NavigationCommandHandler.HoldInboundTrueCourse(hold),
            hold.Direction == TurnDirection.Right,
            legNm,
            radiusNm
        );
        return new NavRouteShapeDto(NavRouteShapeKind.PendingHoldRacetrack, [.. points.Select(p => new[] { p.Lat, p.Lon })], null, null, null);
    }

    /// <summary>
    /// The protected corridor around the cleared span of a military training route, as one closed
    /// polygon: up the left edge, back down the right.
    ///
    /// FAA JO 7110.65 §9-2-6.d makes the corridor the controller's continuing responsibility, and
    /// AP/1B publishes it per span and asymmetrically about the centerline — so it cannot be drawn
    /// as a uniform buffer, and each point is offset by the width published where it sits. Interior
    /// points bisect the two legs meeting there so the edge stays continuous around a turn.
    /// </summary>
    private static List<NavRouteShapeDto> BuildMilitaryRouteCorridor(MilitaryRoutePhase phase)
    {
        MilitaryRoute? route = NavigationDatabase.Instance.GetMilitaryRoute(phase.Designator);
        if (route is null || route.Widths.Count == 0)
        {
            return [];
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        var centerline = new List<(LatLon Position, string Id)>();
        foreach (string name in phase.PointNames)
        {
            int index = route.IndexOf(NamePointId(route.Designator, name));
            if (navDb.GetFixPosition(name) is { } position && index >= 0)
            {
                centerline.Add((new LatLon(position.Lat, position.Lon), route.Points[index].Id));
            }
        }

        if (centerline.Count < 2)
        {
            return [];
        }

        var left = new List<LatLon>();
        var right = new List<LatLon>();
        for (int i = 0; i < centerline.Count; i++)
        {
            MilitaryRouteWidthSpan? span = route.WidthAt(centerline[i].Id);
            if (span is null)
            {
                return [];
            }

            double course = EdgeCourse(centerline, i);
            left.Add(GeoMath.ProjectPoint(centerline[i].Position, new TrueHeading(course - 90.0), span.LeftNm));
            right.Add(GeoMath.ProjectPoint(centerline[i].Position, new TrueHeading(course + 90.0), span.RightNm));
        }

        right.Reverse();
        var polygon = new List<LatLon>(left);
        polygon.AddRange(right);
        polygon.Add(left[0]);

        return [new NavRouteShapeDto(NavRouteShapeKind.MilitaryRouteCorridor, [.. polygon.Select(p => new[] { p.Lat, p.Lon })], null, null, null)];
    }

    /// <summary>
    /// The course to offset a corridor edge from at one centerline point: the leg's own course at
    /// either end, and the bisector of the two legs meeting at an interior point so the edge turns
    /// the corner without a gap or an overlap.
    /// </summary>
    private static double EdgeCourse(List<(LatLon Position, string Id)> centerline, int index)
    {
        if (index == 0)
        {
            return GeoMath.BearingTo(centerline[0].Position, centerline[1].Position);
        }

        double inbound = GeoMath.BearingTo(centerline[index - 1].Position, centerline[index].Position);
        if (index == centerline.Count - 1)
        {
            return inbound;
        }

        double outbound = GeoMath.BearingTo(centerline[index].Position, centerline[index + 1].Position);
        double delta = ((outbound - inbound + 540.0) % 360.0) - 180.0;
        return (inbound + (delta / 2.0) + 360.0) % 360.0;
    }

    /// <summary>The AP/1B point label inside a synthetic fix name ("IR149A" → "A").</summary>
    private static string NamePointId(string designator, string name) =>
        name.StartsWith(designator, StringComparison.OrdinalIgnoreCase) ? name[designator.Length..] : name;

    /// <summary>
    /// Builds a procedure-turn course reversal (AIM 5-4-9): outbound leg on the final-approach-course
    /// reciprocal, the published 45° barb, a 180° turn, then the return leg intercepting the inbound
    /// course back to the fix. Labeled "PT" with the minimum altitude. All headings are true, matching
    /// how <see cref="ProcedureTurnPhase"/> flies it.
    /// </summary>
    private static NavRouteShapeDto BuildProcedureTurn(AircraftState aircraft, ProcedureTurnPhase pt)
    {
        var fix = new LatLon(pt.FixLat, pt.FixLon);
        double outboundNm = Math.Clamp(pt.MaxOutboundDistanceNm * 0.4, 2.0, 6.0);
        double barbNm = Math.Clamp(pt.MaxOutboundDistanceNm * 0.3, 2.0, 4.0);
        double radiusNm = TurnRadiusNm(aircraft);
        bool turnRight = pt.OneEightyTurnDirection == TurnDirection.Right;

        LatLon outbound = GeoMath.ProjectPoint(fix, new TrueHeading(pt.InboundCourseDeg + 180.0), outboundNm);
        LatLon barbEnd = GeoMath.ProjectPoint(outbound, new TrueHeading(pt.PtOutboundCourseDeg), barbNm);

        double centerBearing = pt.PtOutboundCourseDeg + (turnRight ? 90.0 : -90.0);
        LatLon turnCenter = GeoMath.ProjectPoint(barbEnd, new TrueHeading(centerBearing), radiusNm);
        LatLon barbExit = GeoMath.ProjectPoint(barbEnd, new TrueHeading(centerBearing), 2.0 * radiusNm);

        var points = new List<LatLon> { fix, outbound, barbEnd };
        // 180° turn: barbEnd → barbExit (excludes barbEnd, includes barbExit).
        points.AddRange(GeoMath.GenerateArcPoints(turnCenter, radiusNm, centerBearing + 180.0, centerBearing, turnRight));
        // Return leg on the 45°-reciprocal heading intercepts the inbound course, then flies to the fix.
        if (TryInterceptInbound(fix, pt.InboundCourseDeg, barbExit, pt.PtOutboundCourseDeg + 180.0) is { } intercept)
        {
            points.Add(intercept);
        }
        points.Add(fix);

        var labels = new List<string> { "PT" };
        if (pt.MinAltitudeFt > 0)
        {
            labels.AddRange(
                CrossingRestrictionLabel.BuildLines(new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, pt.MinAltitudeFt), null)
            );
        }

        return new NavRouteShapeDto(NavRouteShapeKind.ProcedureTurn, [.. points.Select(p => new[] { p.Lat, p.Lon })], labels, fix.Lat, fix.Lon);
    }

    /// <summary>
    /// Intersects the return leg (through <paramref name="from"/> on <paramref name="returnBearingDeg"/>)
    /// with the inbound course line (through <paramref name="fix"/> on <paramref name="inboundCourseDeg"/>),
    /// in a local flat-earth frame around the fix. Returns null when the lines are near-parallel or the
    /// intercept is implausibly far.
    /// </summary>
    private static LatLon? TryInterceptInbound(LatLon fix, double inboundCourseDeg, LatLon from, double returnBearingDeg)
    {
        const double DegToRad = Math.PI / 180.0;
        double cosLat = Math.Cos(fix.Lat * DegToRad);
        if (Math.Abs(cosLat) < 1e-9)
        {
            return null;
        }

        double qe = (from.Lon - fix.Lon) * 60.0 * cosLat;
        double qn = (from.Lat - fix.Lat) * 60.0;
        double ae = Math.Sin(inboundCourseDeg * DegToRad);
        double an = Math.Cos(inboundCourseDeg * DegToRad);
        double be = Math.Sin(returnBearingDeg * DegToRad);
        double bn = Math.Cos(returnBearingDeg * DegToRad);

        double det = (-ae * bn) + (be * an);
        if (Math.Abs(det) < 1e-6)
        {
            return null;
        }

        double t = ((-qe * bn) + (be * qn)) / det;
        double pe = t * ae;
        double pn = t * an;
        if (Math.Sqrt((pe * pe) + (pn * pn)) > 50.0)
        {
            return null;
        }

        return new LatLon(fix.Lat + (pn / 60.0), fix.Lon + (pe / (60.0 * cosLat)));
    }

    /// <summary>
    /// Projects the remaining coded departure legs (from the active leg on) as chained vectors, each
    /// labeled with its crossing restriction. Fix-terminated legs end at their fix; open-ended legs
    /// (CA/VA/CD/VD/CR/VR) draw a nominal-length vector along the leg course, since their real endpoint
    /// is a condition, not a point. A CA/VA "climb to" altitude is shown as an at-or-above floor.
    /// </summary>
    private static List<NavRouteShapeDto> BuildCodedLegVectors(AircraftState aircraft, DepartureProcedurePhase departure)
    {
        var shapes = new List<NavRouteShapeDto>();
        LatLon anchor = departure.LegEntryPosition ?? aircraft.Position;
        int start = Math.Clamp(departure.ActiveLegIndex, 0, departure.Legs.Count);

        for (int i = start; i < departure.Legs.Count; i++)
        {
            ProcedureLeg leg = departure.Legs[i];

            LatLon end;
            if (leg.FixPosition is { } fixPos)
            {
                end = fixPos;
            }
            else if (leg.CourseMagnetic is { } course)
            {
                end = GeoMath.ProjectPoint(anchor, new MagneticHeading(course).ToTrue(aircraft.Declination), CodedLegNominalNm);
            }
            else
            {
                // No fix and no course to place the leg — stop rather than chain from a stale anchor.
                break;
            }

            List<string> labels = BuildCodedLegLabels(leg);
            shapes.Add(
                new NavRouteShapeDto(
                    NavRouteShapeKind.CodedLegVector,
                    [
                        [anchor.Lat, anchor.Lon],
                        [end.Lat, end.Lon],
                    ],
                    labels.Count > 0 ? labels : null,
                    labels.Count > 0 ? end.Lat : null,
                    labels.Count > 0 ? end.Lon : null
                )
            );

            anchor = end;
        }

        return shapes;
    }

    private static List<string> BuildCodedLegLabels(ProcedureLeg leg)
    {
        CifpAltitudeRestriction? altitude = leg.AltitudeRestriction;
        if (altitude is null && leg.TargetAltitudeFt is { } target)
        {
            // CA/VA/FA "climb to X" — a floor the aircraft climbs to reach.
            altitude = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, (int)Math.Round(target));
        }
        return [.. CrossingRestrictionLabel.BuildLines(altitude, leg.SpeedRestriction)];
    }

    /// <summary>
    /// Builds a holding-pattern racetrack: inbound leg (into the fix), the two 180° turns, and the
    /// parallel outbound leg, on the maneuvering side set by the turn direction. Densified so the
    /// turns render as curves. Matches how <see cref="HoldingPatternPhase"/> flies the pattern
    /// (inbound course is treated as a true heading).
    /// </summary>
    private static NavRouteShapeDto BuildHoldRacetrack(AircraftState aircraft, HoldingPatternPhase hold)
    {
        double radiusNm = TurnRadiusNm(aircraft);
        double legNm = HoldStraightLegNm(aircraft, hold, radiusNm);
        List<LatLon> points = HoldRacetrackPoints(
            new LatLon(hold.FixLat, hold.FixLon),
            hold.InboundCourse,
            hold.Direction == TurnDirection.Right,
            legNm,
            radiusNm
        );
        return new NavRouteShapeDto(NavRouteShapeKind.HoldRacetrack, [.. points.Select(p => new[] { p.Lat, p.Lon })], null, null, null);
    }

    /// <summary>
    /// Builds the closed racetrack polyline (starting and ending at the far end of the inbound leg)
    /// for a hold at <paramref name="fix"/> with the given true <paramref name="inboundCourse"/>, turn
    /// side, straight-leg length, and turn radius. The two 180° turns are densified into arc points.
    /// </summary>
    internal static List<LatLon> HoldRacetrackPoints(LatLon fix, int inboundCourse, bool isRight, double legNm, double radiusNm)
    {
        double sideDeg = isRight ? 90.0 : -90.0;

        // Inbound leg runs from A (far end) into the fix along the inbound course; the outbound leg is
        // parallel, offset by the turn diameter on the maneuvering side.
        var outboundBearing = new TrueHeading(inboundCourse + 180.0);
        var offsetBearing = new TrueHeading(inboundCourse + sideDeg);

        LatLon a = GeoMath.ProjectPoint(fix, outboundBearing, legNm);
        LatLon outboundFar = GeoMath.ProjectPoint(a, offsetBearing, 2.0 * radiusNm);
        LatLon turn1Center = GeoMath.ProjectPoint(fix, offsetBearing, radiusNm);
        LatLon turn2Center = GeoMath.ProjectPoint(a, offsetBearing, radiusNm);

        double nearBearing = inboundCourse + sideDeg; // center → outbound-near / abeam point
        double farBearing = inboundCourse + sideDeg + 180.0; // center → inbound-leg point (fix / A)

        var points = new List<LatLon> { a, fix };
        // Turn 1 at the fix: fix → outbound-near (excludes fix, includes outbound-near).
        points.AddRange(GeoMath.GenerateArcPoints(turn1Center, radiusNm, farBearing, nearBearing, isRight));
        points.Add(outboundFar);
        // Turn 2 at the far end: outbound-far → A (excludes outbound-far, includes A, closing the loop).
        points.AddRange(GeoMath.GenerateArcPoints(turn2Center, radiusNm, nearBearing, farBearing, isRight));
        return points;
    }

    private static double TurnRadiusNm(AircraftState aircraft) => TurnRadiusNm(aircraft, aircraft.Altitude);

    /// <summary>The standard-rate turn radius at holding speed at <paramref name="altitudeFt"/>, clamped.</summary>
    private static double TurnRadiusNm(AircraftState aircraft, double altitudeFt)
    {
        double tas = HoldingTasKts(aircraft, altitudeFt);
        double radius = (tas / 3600.0) / (StandardTurnRateDegPerSec * Math.PI / 180.0);
        return Math.Clamp(radius, MinTurnRadiusNm, MaxTurnRadiusNm);
    }

    private static double HoldStraightLegNm(AircraftState aircraft, HoldingPatternPhase hold, double radiusNm) =>
        HoldStraightLegNm(aircraft, hold.LegLength, hold.IsMinuteBased, aircraft.Altitude, radiusNm);

    /// <summary>
    /// The straight leg in nm: a minute-based leg at holding TAS at <paramref name="altitudeFt"/>, else the leg as given.
    /// </summary>
    private static double HoldStraightLegNm(AircraftState aircraft, double legLength, bool isMinuteBased, double altitudeFt, double radiusNm)
    {
        double nm = isMinuteBased ? HoldingTasKts(aircraft, altitudeFt) * (legLength / 60.0) : legLength;
        // Keep the straight leg at least a turn-diameter long so the racetrack never collapses.
        return Math.Max(nm, 2.0 * radiusNm);
    }

    private static double HoldingTasKts(AircraftState aircraft, double altitudeFt)
    {
        double ias = AircraftPerformance.HoldingSpeed(aircraft.AircraftType, altitudeFt);
        double tas = WindInterpolator.IasToTas(ias, altitudeFt);
        return tas > 0 ? tas : FallbackHoldingTasKts;
    }
}
