using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="NavRouteOverlayProjector"/> — the active-phase geometry projected onto
/// the radar "Show nav route" overlay (hold racetracks; procedure turns and coded legs to follow).
/// </summary>
public class NavRouteOverlayProjectorTests
{
    private static readonly LatLon Fix = new(37.7, -122.2);

    [Fact]
    public void HoldRacetrack_IsClosedLoopThroughFix()
    {
        List<LatLon> points = NavRouteOverlayProjector.HoldRacetrackPoints(Fix, inboundCourse: 90, isRight: true, legNm: 5.0, radiusNm: 1.0);

        Assert.True(points.Count > 4, "racetrack should be densified (arcs + legs)");
        // Closed loop: starts and ends at the far end of the inbound leg.
        Assert.True(GeoMath.DistanceNm(points[0], points[^1]) < 0.02, "racetrack polyline should close");
        // Passes through the hold fix.
        Assert.True(points.Any(p => GeoMath.DistanceNm(p, Fix) < 0.01), "racetrack should pass through the fix");
    }

    [Fact]
    public void HoldRacetrack_RightTurns_ManeuveringSideIsRightOfInbound()
    {
        // Inbound course 090 (east); right turns → the pattern lies to the right of travel = south.
        List<LatLon> points = NavRouteOverlayProjector.HoldRacetrackPoints(Fix, inboundCourse: 90, isRight: true, legNm: 5.0, radiusNm: 1.0);

        // Far end of the inbound leg is west of the fix (outbound bearing 270).
        Assert.True(points[0].Lon < Fix.Lon, "inbound leg extends west of the fix");
        // The outbound leg / turns sit ~2 turn-radii south of the inbound line.
        Assert.True(points.Any(p => p.Lat < Fix.Lat - (1.5 / 60.0)), "right-turn hold on an easterly inbound should extend south");
        Assert.DoesNotContain(points, p => p.Lat > Fix.Lat + (0.5 / 60.0));
    }

    [Fact]
    public void HoldRacetrack_LeftTurns_MirrorsToOppositeSide()
    {
        List<LatLon> points = NavRouteOverlayProjector.HoldRacetrackPoints(Fix, inboundCourse: 90, isRight: false, legNm: 5.0, radiusNm: 1.0);

        // Left turns on an easterly inbound → pattern extends north.
        Assert.True(points.Any(p => p.Lat > Fix.Lat + (1.5 / 60.0)), "left-turn hold on an easterly inbound should extend north");
        Assert.DoesNotContain(points, p => p.Lat < Fix.Lat - (0.5 / 60.0));
    }

    [Fact]
    public void BuildShapes_HoldingAircraft_ProjectsHoldRacetrack()
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Position = Fix,
            Altitude = 8000,
        };
        var hold = new HoldingPatternPhase
        {
            FixName = "HOLDR",
            FixLat = Fix.Lat,
            FixLon = Fix.Lon,
            InboundCourse = 270,
            LegLength = 1.0,
            IsMinuteBased = true,
            Direction = TurnDirection.Right,
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(hold);

        List<NavRouteShapeDto> shapes = NavRouteOverlayProjector.BuildShapes(ac);

        NavRouteShapeDto shape = Assert.Single(shapes);
        Assert.Equal(NavRouteShapeKind.HoldRacetrack, shape.Kind);
        Assert.True(shape.Points.Count > 4);
        Assert.All(shape.Points, p => Assert.Equal(2, p.Length));
    }

    [Fact]
    public void BuildShapes_NoActivePhase_ReturnsEmpty()
    {
        var ac = new AircraftState { Callsign = "TEST2", AircraftType = "B738" };
        Assert.Empty(NavRouteOverlayProjector.BuildShapes(ac));
    }

    [Fact]
    public void BuildShapes_DepartureProcedure_ProjectsChainedCodedLegVectorsWithRestrictions()
    {
        var ac = new AircraftState
        {
            Callsign = "TEST3",
            AircraftType = "B738",
            Position = new LatLon(37.6, -122.3),
            Altitude = 1500,
        };
        var departure = new DepartureProcedurePhase
        {
            Legs =
            [
                // VA: climb on heading 090 to 3000 (a fix-less altitude leg).
                new ProcedureLeg
                {
                    Type = ProcedureLegType.HeadingToAltitude,
                    CourseMagnetic = 90,
                    TargetAltitudeFt = 3000,
                },
                // CD: fly on course 090 to a DME distance, crossing between 1400 and 2000 (the KOAK
                // COAST9 case — a fix-less leg carrying an altitude window the flat route drops).
                new ProcedureLeg
                {
                    Type = ProcedureLegType.CourseToDistance,
                    CourseMagnetic = 90,
                    AltitudeRestriction = new CifpAltitudeRestriction(CifpAltitudeRestrictionType.Between, 2000, 1400),
                },
            ],
            PostRoute = [],
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(departure);

        List<NavRouteShapeDto> shapes = NavRouteOverlayProjector.BuildShapes(ac);

        Assert.Equal(2, shapes.Count);
        Assert.All(shapes, s => Assert.Equal(NavRouteShapeKind.CodedLegVector, s.Kind));

        // Climb-to altitude renders as an at-or-above floor.
        Assert.Equal(["≥3000"], shapes[0].Labels);
        // DME-arc altitude window renders as ceiling over floor — the restriction the audit flagged invisible.
        Assert.Equal(["≤2000", "≥1400"], shapes[1].Labels);

        // Vectors chain: leg 2 starts where leg 1 ends.
        Assert.Equal(shapes[0].Points[^1][0], shapes[1].Points[0][0], 9);
        Assert.Equal(shapes[0].Points[^1][1], shapes[1].Points[0][1], 9);
        // Leg 1 starts at the aircraft (no tick has advanced the phase's entry position yet).
        Assert.Equal(ac.Position.Lat, shapes[0].Points[0][0], 9);
        Assert.Equal(ac.Position.Lon, shapes[0].Points[0][1], 9);
    }

    [Fact]
    public void BuildShapes_ProcedureTurn_ProjectsBarbReturningToFix()
    {
        var ac = new AircraftState
        {
            Callsign = "TEST4",
            AircraftType = "C172",
            Position = Fix,
            Altitude = 4000,
        };
        var pt = new ProcedureTurnPhase
        {
            FixName = "PTFIX",
            FixLat = Fix.Lat,
            FixLon = Fix.Lon,
            InboundCourseDeg = 360,
            PtOutboundCourseDeg = 225,
            MaxOutboundDistanceNm = 10,
            OneEightyTurnDirection = TurnDirection.Right,
            MinAltitudeFt = 3000,
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(pt);

        List<NavRouteShapeDto> shapes = NavRouteOverlayProjector.BuildShapes(ac);

        NavRouteShapeDto shape = Assert.Single(shapes);
        Assert.Equal(NavRouteShapeKind.ProcedureTurn, shape.Kind);
        Assert.True(shape.Points.Count > 4, "PT should include the densified 180° turn");
        // Starts at the fix and returns to it.
        Assert.Equal(Fix.Lat, shape.Points[0][0], 6);
        Assert.Equal(Fix.Lat, shape.Points[^1][0], 6);
        Assert.Equal(Fix.Lon, shape.Points[^1][1], 6);
        // Labeled "PT" with the minimum altitude.
        Assert.NotNull(shape.Labels);
        Assert.Equal("PT", shape.Labels![0]);
        Assert.Contains("≥3000", shape.Labels);
    }

    // ---- Queued holds: a HOLDP behind a reach-fix trigger, drawn dashed before the aircraft reaches the fix ----

    private const string HoldFixName = "SUNOL";

    private static LatLon HoldFix()
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition(HoldFixName) ?? throw new InvalidOperationException("SUNOL missing");
        return new LatLon(lat, lon);
    }

    private static HoldingPatternCommand HoldCommand(double? legLength = null, bool isMinuteBased = true)
    {
        LatLon fix = HoldFix();
        return new HoldingPatternCommand(HoldFixName, fix.Lat, fix.Lon, 090, legLength, isMinuteBased, TurnDirection.Right, null);
    }

    private static AircraftState AircraftAt(double altitudeFt)
    {
        TestVnasData.EnsureInitialized();
        return new AircraftState
        {
            Callsign = "TEST5",
            AircraftType = "B738",
            Position = new LatLon(37.8, -122.3),
            Altitude = altitudeFt,
        };
    }

    private static CommandBlock QueuedBlock(params ParsedCommand[] commands)
    {
        LatLon fix = HoldFix();
        return new CommandBlock
        {
            Trigger = new BlockTrigger
            {
                Type = BlockTriggerType.ReachFix,
                FixName = HoldFixName,
                FixLat = fix.Lat,
                FixLon = fix.Lon,
            },
            ParsedCommands = [.. commands],
        };
    }

    /// <summary>The straight leg a minute-based hold flies at <paramref name="altitudeFt"/> in a B738.</summary>
    private static double MinuteLegNm(double minutes, double altitudeFt) =>
        WindInterpolator.IasToTas(AircraftPerformance.HoldingSpeed("B738", altitudeFt), altitudeFt) * (minutes / 60.0);

    /// <summary>The drawn inbound leg: the racetrack starts at its far end and runs into the fix.</summary>
    private static double DrawnLegNm(NavRouteShapeDto shape) =>
        GeoMath.DistanceNm(new LatLon(shape.Points[0][0], shape.Points[0][1]), new LatLon(shape.Points[1][0], shape.Points[1][1]));

    /// <summary>
    /// The drawn leg matches within 0.02 nm (the projection and distance round trip differs by a few thousandths); the
    /// candidate legs differ by far more (1 against 1.5 minutes is about 2 nm, TAS at 10,000 against 15,000 ft about 0.4 nm).
    /// </summary>
    private static void AssertLegNm(double expectedNm, double drawnNm) => Assert.InRange(drawnNm, expectedNm - 0.02, expectedNm + 0.02);

    private static NavRouteShapeDto SinglePending(AircraftState ac)
    {
        NavRouteShapeDto shape = Assert.Single(NavRouteOverlayProjector.BuildShapes(ac));
        Assert.Equal(NavRouteShapeKind.PendingHoldRacetrack, shape.Kind);
        return shape;
    }

    [Fact]
    public void BuildShapes_QueuedHoldNoLeg_PresentAltitudeAtOrBelow14000_DrawsOneMinuteLeg()
    {
        AircraftState ac = AircraftAt(10000);
        ac.Queue.Blocks.Add(QueuedBlock(HoldCommand()));

        AssertLegNm(MinuteLegNm(1.0, 10000), DrawnLegNm(SinglePending(ac)));
    }

    [Fact]
    public void BuildShapes_QueuedHoldNoLeg_AssignedAltitudeAbove14000_DrawsMinuteAndAHalfLegAtTheAssignedAltitude()
    {
        AircraftState ac = AircraftAt(10000);
        ac.Targets.AssignedAltitude = 16000;
        ac.Queue.Blocks.Add(QueuedBlock(HoldCommand()));

        AssertLegNm(MinuteLegNm(1.5, 16000), DrawnLegNm(SinglePending(ac)));
    }

    [Fact]
    public void BuildShapes_QueuedHoldNoLeg_AltitudeCommandInTheSameBlock_JudgesTheLegAtThatAltitude()
    {
        AircraftState ac = AircraftAt(10000);
        ac.Queue.Blocks.Add(QueuedBlock(new ClimbMaintainCommand(15000), HoldCommand()));

        AssertLegNm(MinuteLegNm(1.5, 15000), DrawnLegNm(SinglePending(ac)));
    }

    /// <summary>A force-altitude sets the assigned altitude the dispatch reads, so it judges the leg like a maintain.</summary>
    [Fact]
    public void BuildShapes_QueuedHoldNoLeg_ForceAltitudeInTheSameBlock_JudgesTheLegAtThatAltitude()
    {
        AircraftState ac = AircraftAt(10000);
        ac.Queue.Blocks.Add(QueuedBlock(new ForceAltitudeCommand(16000), HoldCommand()));

        AssertLegNm(MinuteLegNm(1.5, 16000), DrawnLegNm(SinglePending(ac)));
    }

    [Fact]
    public void BuildShapes_QueuedHoldNoLeg_AssignedExactly14000_DrawsOneMinuteLeg()
    {
        AircraftState ac = AircraftAt(10000);
        ac.Targets.AssignedAltitude = 14000;
        ac.Queue.Blocks.Add(QueuedBlock(HoldCommand()));

        AssertLegNm(MinuteLegNm(1.0, 14000), DrawnLegNm(SinglePending(ac)));
    }

    [Fact]
    public void BuildShapes_QueuedHoldLegInMinutes_DrawsThatManyMinutes()
    {
        AircraftState ac = AircraftAt(10000);
        ac.Queue.Blocks.Add(QueuedBlock(HoldCommand(legLength: 2.0, isMinuteBased: true)));

        AssertLegNm(MinuteLegNm(2.0, 10000), DrawnLegNm(SinglePending(ac)));
    }

    [Fact]
    public void BuildShapes_QueuedHoldLegInMiles_DrawsThatManyMiles()
    {
        AircraftState ac = AircraftAt(10000);
        ac.Queue.Blocks.Add(QueuedBlock(HoldCommand(legLength: 7.0, isMinuteBased: false)));

        AssertLegNm(7.0, DrawnLegNm(SinglePending(ac)));
    }

    /// <summary>
    /// The dashed racetrack lies exactly where the solid one will once the hold is dispatched: the magnetic inbound course
    /// is converted with the same published declination, so every point matches.
    /// </summary>
    [Fact]
    public void BuildShapes_QueuedHold_MatchesTheRacetrackTheDispatchedHoldDraws()
    {
        AircraftState queued = AircraftAt(10000);
        queued.Queue.Blocks.Add(QueuedBlock(HoldCommand()));
        NavRouteShapeDto pending = SinglePending(queued);

        AircraftState flown = AircraftAt(10000);
        Assert.True(NavigationCommandHandler.DispatchHoldingPattern(HoldCommand(), flown).Success);
        NavRouteShapeDto solid = Assert.Single(NavRouteOverlayProjector.BuildShapes(flown));
        Assert.Equal(NavRouteShapeKind.HoldRacetrack, solid.Kind);

        Assert.Equal(solid.Points.Count, pending.Points.Count);
        for (int i = 0; i < solid.Points.Count; i++)
        {
            Assert.Equal(solid.Points[i][0], pending.Points[i][0], 9);
            Assert.Equal(solid.Points[i][1], pending.Points[i][1], 9);
        }

        // The inbound leg flies the true course, not the magnetic 090 the controller issued.
        double inboundBearing = GeoMath.BearingTo(
            new LatLon(pending.Points[0][0], pending.Points[0][1]),
            new LatLon(pending.Points[1][0], pending.Points[1][1])
        );
        Assert.True(Math.Abs(inboundBearing - 90.0) > 5.0, $"inbound bearing {inboundBearing:F1} should be true, not magnetic");
    }

    [Fact]
    public void BuildShapes_AppliedBlockOrBlockWithoutParsedCommands_DrawsNothing()
    {
        AircraftState ac = AircraftAt(10000);
        CommandBlock applied = QueuedBlock(HoldCommand());
        applied.IsApplied = true;
        CommandBlock unparsed = QueuedBlock(HoldCommand());
        unparsed.ParsedCommands = null;
        ac.Queue.Blocks.Add(applied);
        ac.Queue.Blocks.Add(unparsed);

        Assert.Empty(NavRouteOverlayProjector.BuildShapes(ac));
    }

    [Fact]
    public void BuildShapes_HoldingWithAQueuedHold_DrawsTheCurrentHoldSolidAndTheQueuedOneDashed()
    {
        AircraftState ac = AircraftAt(8000);
        ac.Phases = new PhaseList();
        ac.Phases.Add(
            new HoldingPatternPhase
            {
                FixName = "HOLDR",
                FixLat = Fix.Lat,
                FixLon = Fix.Lon,
                InboundCourse = 270,
                LegLength = 1.0,
                IsMinuteBased = true,
                Direction = TurnDirection.Right,
            }
        );
        ac.Queue.Blocks.Add(QueuedBlock(HoldCommand()));

        List<NavRouteShapeDto> shapes = NavRouteOverlayProjector.BuildShapes(ac);

        Assert.Equal([NavRouteShapeKind.HoldRacetrack, NavRouteShapeKind.PendingHoldRacetrack], shapes.Select(s => s.Kind));
    }

    // ---- VFR and airspace-boundary orbits: a dashed standard-rate circle centred on the orbit point ----

    private static readonly LatLon OrbitPoint = new(37.75, -122.1);

    /// <summary>The projector's standard-rate radius (3°/s at holding TAS, clamped 0.5-4 nm) for a B738 at <paramref name="altitudeFt"/>.</summary>
    private static double StandardRateRadiusNm(double altitudeFt)
    {
        double tas = WindInterpolator.IasToTas(AircraftPerformance.HoldingSpeed("B738", altitudeFt), altitudeFt);
        return Math.Clamp((tas / 3600.0) / (3.0 * Math.PI / 180.0), 0.5, 4.0);
    }

    private static AircraftState InPhase(Phase phase, LatLon position)
    {
        AircraftState ac = AircraftAt(5000);
        ac.Position = position;
        ac.Phases = new PhaseList();
        ac.Phases.Add(phase);
        return ac;
    }

    private static void AssertCircleAround(LatLon centre, NavRouteShapeDto shape)
    {
        Assert.Equal(NavRouteShapeKind.OrbitCircle, shape.Kind);
        Assert.True(shape.Points.Count > 60, "the circle should be densified");
        double radiusNm = StandardRateRadiusNm(5000);
        Assert.All(shape.Points, p => Assert.InRange(GeoMath.DistanceNm(centre, new LatLon(p[0], p[1])), radiusNm - 0.01, radiusNm + 0.01));
        // Closed: it ends where it starts.
        Assert.True(
            GeoMath.DistanceNm(new LatLon(shape.Points[0][0], shape.Points[0][1]), new LatLon(shape.Points[^1][0], shape.Points[^1][1])) < 0.01
        );
    }

    [Fact]
    public void BuildShapes_HfixProceedingToTheFix_DrawsCircleCentredOnTheFix()
    {
        var hfix = new VfrHoldPhase
        {
            FixName = "ORBIT",
            FixLat = OrbitPoint.Lat,
            FixLon = OrbitPoint.Lon,
            OrbitDirection = TurnDirection.Left,
        };
        AircraftState ac = InPhase(hfix, new LatLon(37.9, -122.4));
        Assert.Equal("ProceedToFix", hfix.Name);

        AssertCircleAround(OrbitPoint, Assert.Single(NavRouteOverlayProjector.BuildShapes(ac)));
    }

    [Fact]
    public void BuildShapes_HfixOrbitingAtTheFix_DrawsCircleCentredOnTheFix()
    {
        var hfix = VfrHoldPhase.FromSnapshot(
            new VfrHoldPhaseDto
            {
                Status = (int)PhaseStatus.Active,
                ElapsedSeconds = 60,
                FixName = "ORBIT",
                FixLat = OrbitPoint.Lat,
                FixLon = OrbitPoint.Lon,
                OrbitDirection = (int)TurnDirection.Right,
                AtFix = true,
                CumulativeTurn = 90,
                LastHeadingDeg = 180,
            }
        );
        AircraftState ac = InPhase(hfix, OrbitPoint);
        Assert.Equal("HoldingAtFix", hfix.Name);

        AssertCircleAround(OrbitPoint, Assert.Single(NavRouteOverlayProjector.BuildShapes(ac)));
    }

    [Fact]
    public void BuildShapes_HelicopterHoverAtAFix_DrawsNothing()
    {
        var hover = new VfrHoldPhase
        {
            FixName = "ORBIT",
            FixLat = OrbitPoint.Lat,
            FixLon = OrbitPoint.Lon,
        };

        Assert.Empty(NavRouteOverlayProjector.BuildShapes(InPhase(hover, OrbitPoint)));
    }

    /// <summary>HPP orbits wherever the aircraft was when it began, and the phase keeps no record of that point.</summary>
    [Fact]
    public void BuildShapes_HoldPresentPosition_DrawsNothingSinceThePhaseKeepsNoOrbitPoint()
    {
        var hpp = new VfrHoldPhase { OrbitDirection = TurnDirection.Right };

        Assert.Empty(NavRouteOverlayProjector.BuildShapes(InPhase(hpp, OrbitPoint)));
    }

    [Fact]
    public void BuildShapes_AirspaceBoundaryOrbit_DrawsCircleCentredOnTheReferencePosition()
    {
        var orbit = new AirspaceBoundaryHoldPhase
        {
            ReferencePosition = OrbitPoint,
            OrbitDirection = TurnDirection.Left,
            Mode = AirspaceHoldMode.Orbit,
        };

        AssertCircleAround(OrbitPoint, Assert.Single(NavRouteOverlayProjector.BuildShapes(InPhase(orbit, new LatLon(37.76, -122.11)))));
    }

    [Fact]
    public void BuildShapes_AirspaceBoundaryLevelOff_DrawsNothing()
    {
        var levelOff = new AirspaceBoundaryHoldPhase
        {
            ReferencePosition = OrbitPoint,
            Mode = AirspaceHoldMode.LevelOff,
            LevelOffCeilingFtMsl = 4500,
        };

        Assert.Empty(NavRouteOverlayProjector.BuildShapes(InPhase(levelOff, OrbitPoint)));
    }

    // ---- Route-end course and heading vector: what the sim flies past the route, from the sim's own state ----

    /// <summary>The model date the declination of these fixtures is taken at.</summary>
    private static readonly DateTime DeclinationDate = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>OAKES3 RW12 as the bundled CIFP resolves it, ending at HIRMO with its FM course.</summary>
    private static List<NavigationTarget> Oakes3Rw12Targets()
    {
        TestVnasData.EnsureInitialized();
        CifpStarProcedure star = Assert.IsType<CifpStarProcedure>(NavigationDatabase.Instance.GetStar("KOAK", "OAKES3"));
        Assert.True(star.RunwayTransitions.TryGetValue("RW12", out CifpTransition? rw12));
        List<NavigationTarget> targets = DepartureClearanceHandler.ResolveLegsToTargets([.. star.CommonLegs, .. rw12.Legs]);
        Assert.Equal("HIRMO", targets[^1].Name);
        Assert.NotNull(targets[^1].TerminalCourseMagnetic);
        return targets;
    }

    private static LatLon KoakPosition()
    {
        (double lat, double lon) = NavigationDatabase.Instance.GetAirportPosition("KOAK") ?? throw new InvalidOperationException("KOAK missing");
        return new LatLon(lat, lon);
    }

    /// <summary>An airborne aircraft at the last of <paramref name="route"/>, flying it, cleared to KOAK.</summary>
    private static AircraftState Routed(IEnumerable<NavigationTarget> route, string flightRules = "IFR")
    {
        TestVnasData.EnsureInitialized();
        List<NavigationTarget> targets = [.. route];
        var ac = new AircraftState
        {
            Callsign = "SWA22",
            AircraftType = "B738",
            Position = targets[^1].Position,
            TrueHeading = new TrueHeading(90),
            Altitude = 11000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            Declination = MagneticDeclination.GetDeclination(targets[^1].Position, DeclinationDate),
            FlightPlan = new AircraftFlightPlan
            {
                HasFlightPlan = true,
                Departure = "KSLC",
                Destination = "KOAK",
                Route = "OAKES3",
                Altitude = flightRules == "IFR" ? PlannedAltitude.Ifr(35000) : PlannedAltitude.Vfr(9500),
                FlightRules = flightRules,
            },
            ClearedDestination = "KOAK",
        };
        ac.Targets.TargetAltitude = 9000;
        ac.Targets.AssignedAltitude = 9000;
        ac.Targets.NavigationRoute.AddRange(targets);
        return ac;
    }

    private static LatLon PointOf(double[] point) => new(point[0], point[1]);

    /// <summary>A two-point shape from <paramref name="origin"/> along <paramref name="trueCourse"/> for 5 nm.</summary>
    private static void AssertVectorFrom(LatLon origin, TrueHeading trueCourse, NavRouteShapeDto shape)
    {
        Assert.Equal(2, shape.Points.Count);
        LatLon start = PointOf(shape.Points[0]);
        LatLon end = PointOf(shape.Points[1]);
        Assert.Equal(origin.Lat, start.Lat, 9);
        Assert.Equal(origin.Lon, start.Lon, 9);
        var bearing = new TrueHeading(GeoMath.BearingTo(start, end));
        Assert.True(bearing.AbsAngleTo(trueCourse) < 0.5, $"bearing {bearing.Degrees:F1} should be {trueCourse.Degrees:F1}");
        Assert.InRange(GeoMath.DistanceNm(start, end), 4.95, 5.05);
        Assert.Null(shape.Labels);
    }

    /// <summary>
    /// CURLO6's runway 31L transition carries a VM course on a fix with more of the SID after it: the course sits mid-route,
    /// the route end flies none of it, so no route-end course is drawn.
    /// </summary>
    [Fact]
    public void Sid_VmCourseFollowedByMoreFixes_DrawsNoRouteEndCourse()
    {
        TestVnasData.EnsureInitialized();
        CifpSidProcedure sid = Assert.IsType<CifpSidProcedure>(NavigationDatabase.Instance.GetSid("KDAL", "CURLO6"));
        Assert.True(sid.RunwayTransitions.TryGetValue("RW31L", out CifpTransition? rw31L));
        List<NavigationTarget> targets = DepartureClearanceHandler.ResolveLegsToTargets([.. rw31L.Legs, .. sid.CommonLegs]);
        Assert.Contains(targets.Take(targets.Count - 1), t => t.TerminalCourseMagnetic is not null);
        Assert.Null(targets[^1].TerminalCourseMagnetic);

        AircraftState ac = Routed(targets);
        ac.Position = targets[0].Position;

        Assert.DoesNotContain(NavRouteOverlayProjector.BuildShapes(ac), s => s.Kind == NavRouteShapeKind.RouteEndCourse);
    }

    [Fact]
    public void Sid_FmTerminatorAtTheLastFix_DrawsTheRouteEndCourseFromThatFix()
    {
        List<NavigationTarget> targets = Oakes3Rw12Targets();
        NavigationTarget hirmo = targets[^1];
        AircraftState ac = Routed([hirmo]);

        NavRouteShapeDto shape = Assert.Single(NavRouteOverlayProjector.BuildShapes(ac), s => s.Kind == NavRouteShapeKind.RouteEndCourse);

        AssertVectorFrom(hirmo.Position, new MagneticHeading(hirmo.TerminalCourseMagnetic!.Value).ToTrue(ac.Declination), shape);
    }

    /// <summary>A pending approach clearance activates at route end instead of the terminal course, so none is drawn.</summary>
    [Fact]
    public void PendingApproachClearance_DrawsNoRouteEndCourse()
    {
        AircraftState ac = Routed([Oakes3Rw12Targets()[^1]]);
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("KOAK", "30"));
        ac.Approach.PendingClearance = new PendingApproachInfo
        {
            Clearance = new ApproachClearance
            {
                TransitionName = null,
                ApproachId = "I30",
                AirportCode = "KOAK",
                RunwayId = "30",
                FinalApproachCourse = runway.TrueHeading,
            },
            AssignedRunway = runway,
            ConnectingFix = null,
        };

        Assert.DoesNotContain(NavRouteOverlayProjector.BuildShapes(ac), s => s.Kind == NavRouteShapeKind.RouteEndCourse);
    }

    /// <summary>
    /// A takeoff clearance's heading, flown by the initial climb as the phase itself sets it once past its turn gate: the
    /// vector is drawn along that heading, so a phase that set an assigned heading instead would draw none.
    /// </summary>
    [Fact]
    public void HeadingOnlyDeparture_DrawsAHeadingVectorFromTheAircraft()
    {
        TestVnasData.EnsureInitialized();
        RunwayInfo runway = Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("KOAK", "30"));
        LatLon pastDer = GeoMath.ProjectPoint(new LatLon(runway.EndLatitude, runway.EndLongitude), runway.TrueHeading, 0.5);
        var ac = new AircraftState
        {
            Callsign = "SWA22",
            AircraftType = "B738",
            Position = pastDer,
            TrueHeading = runway.TrueHeading,
            Altitude = runway.AirportElevationFt + 1500,
            IsOnGround = false,
            Declination = MagneticDeclination.GetDeclination(pastDer, DeclinationDate),
            Phases = new PhaseList { AssignedRunway = runway },
        };
        var climb = new InitialClimbPhase { Departure = new FlyHeadingDeparture(new MagneticHeading(270), null) };
        ac.Phases.Add(climb);
        var ctx = new PhaseContext
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Runway = runway,
            FieldElevation = runway.AirportElevationFt,
            Logger = NullLogger.Instance,
        };

        climb.OnStart(ctx);
        climb.OnTick(ctx);

        Assert.Same(climb, ac.Phases.CurrentPhase);
        TrueHeading flown = Assert.NotNull(ac.Targets.TargetTrueHeading);
        NavRouteShapeDto shape = Assert.Single(NavRouteOverlayProjector.BuildShapes(ac));
        Assert.Equal(NavRouteShapeKind.HeadingVector, shape.Kind);
        AssertVectorFrom(ac.Position, flown, shape);
    }

    /// <summary>The route-end key carries the true course to a tenth of a degree: a declination refresh does not re-send it.</summary>
    [Fact]
    public void RouteEndCourseKey_IgnoresADeclinationRefreshButNotATenthOfADegree()
    {
        NavigationTarget hirmo = Oakes3Rw12Targets()[^1];
        string KeyAt(double declination)
        {
            var ac = new AircraftState
            {
                Callsign = "SWA22",
                AircraftType = "B738",
                Declination = declination,
            };
            ac.Targets.NavigationRoute.Add(hirmo);
            return NavRouteOverlayProjector.BuildPendingShapeKey(ac);
        }

        string key = KeyAt(13.40);

        Assert.Contains("|EHIRMO:", key);
        Assert.Equal(key, KeyAt(13.41));
        Assert.NotEqual(key, KeyAt(13.60));
    }

    /// <summary>An FH heading is drawn by the client's own vector tail, which follows the interpolated symbol.</summary>
    [Fact]
    public void FhHeading_DrawsNoServerHeadingVector()
    {
        AircraftState ac = AircraftAt(5000);
        ac.IsOnGround = false;
        ac.Targets.AssignedMagneticHeading = new MagneticHeading(270);
        ac.Targets.TargetTrueHeading = new MagneticHeading(270).ToTrue(ac.Declination);

        Assert.DoesNotContain(NavRouteOverlayProjector.BuildShapes(ac), s => s.Kind == NavRouteShapeKind.HeadingVector);
    }

    /// <summary>
    /// The projector's judge of the route end is the one the sim flies: each branch the classifier names is the branch
    /// <see cref="FlightPhysics.Update"/> takes when the aircraft sequences that last fix.
    /// </summary>
    [Theory]
    [InlineData("ClearanceLimitHold")]
    [InlineData("TerminalCourse")]
    [InlineData("AirportHold")]
    [InlineData("DirectToDestination")]
    [InlineData("Straight")]
    public void ClassifyRouteEnd_MatchesApplyRouteEndLateral(string branch)
    {
        FlightPhysics.RouteEndLateral expected = Enum.Parse<FlightPhysics.RouteEndLateral>(branch);
        AircraftState ac = RouteEndFixture(expected);
        NavigationTarget last = ac.Targets.NavigationRoute[^1];

        Assert.Equal(expected, FlightPhysics.ClassifyRouteEnd(ac, last));

        FlightPhysics.Update(ac, 1.0);
        AssertFlew(expected, ac, last);
    }

    private static AircraftState RouteEndFixture(FlightPhysics.RouteEndLateral branch)
    {
        NavigationTarget hirmo = Oakes3Rw12Targets()[^1];
        var plainHirmo = new NavigationTarget { Name = "HIRMO", Position = hirmo.Position };
        return branch switch
        {
            FlightPhysics.RouteEndLateral.ClearanceLimitHold => Routed([
                new NavigationTarget
                {
                    Name = "KOAK",
                    Position = KoakPosition(),
                    IsClearanceLimitDirect = true,
                },
            ]),
            FlightPhysics.RouteEndLateral.TerminalCourse => Routed([hirmo]),
            FlightPhysics.RouteEndLateral.AirportHold => Routed([new NavigationTarget { Name = "KOAK", Position = KoakPosition() }]),
            FlightPhysics.RouteEndLateral.DirectToDestination => Routed([plainHirmo]),
            _ => Routed([plainHirmo], "VFR"),
        };
    }

    private static void AssertFlew(FlightPhysics.RouteEndLateral branch, AircraftState ac, NavigationTarget last)
    {
        switch (branch)
        {
            case FlightPhysics.RouteEndLateral.ClearanceLimitHold:
            case FlightPhysics.RouteEndLateral.AirportHold:
                Assert.True(Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase).IsClearanceLimitHold);
                break;
            case FlightPhysics.RouteEndLateral.TerminalCourse:
                Assert.Empty(ac.Targets.NavigationRoute);
                TrueHeading course = new MagneticHeading(last.TerminalCourseMagnetic!.Value).ToTrue(ac.Declination);
                Assert.True(Assert.NotNull(ac.Targets.TargetTrueHeading).AbsAngleTo(course) < 1.0);
                break;
            case FlightPhysics.RouteEndLateral.DirectToDestination:
                NavigationTarget direct = Assert.Single(ac.Targets.NavigationRoute);
                Assert.True(direct.IsClearanceLimitDirect);
                Assert.Equal("KOAK", direct.Name);
                break;
            default:
                Assert.Empty(ac.Targets.NavigationRoute);
                Assert.Null(ac.Targets.TargetTrueHeading);
                break;
        }
    }

    // ---- Final approach course: a cleared, pending or expected approach's final, from the sim's stored clearance ----

    /// <summary>The shapes that draw a final approach course, cleared or expected.</summary>
    private static bool IsFinal(NavRouteShapeDto shape) =>
        shape.Kind is NavRouteShapeKind.FinalApproachCourse or NavRouteShapeKind.ExpectedFinalApproachCourse;

    /// <summary>A clearance's lateral anchor, else its runway's threshold: the near end of its final.</summary>
    private static LatLon AnchorOf(ApproachClearance clearance, RunwayInfo runway) =>
        ((clearance.FinalApproachAnchorLat is { } lat) && (clearance.FinalApproachAnchorLon is { } lon))
            ? new LatLon(lat, lon)
            : new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);

    /// <summary>
    /// A two-point final, far end first: the far end <paramref name="farNm"/> (± <paramref name="toleranceNm"/>) back from
    /// <paramref name="near"/> along the reciprocal of <paramref name="course"/>, then <paramref name="near"/> itself.
    /// </summary>
    private static void AssertFinalLine(LatLon near, TrueHeading course, double farNm, double toleranceNm, NavRouteShapeDto shape)
    {
        Assert.Equal(2, shape.Points.Count);
        LatLon far = PointOf(shape.Points[0]);
        Assert.Equal(near.Lat, shape.Points[1][0], 9);
        Assert.Equal(near.Lon, shape.Points[1][1], 9);
        Assert.InRange(GeoMath.DistanceNm(near, far), farNm - toleranceNm, farNm + toleranceNm);
        var bearing = new TrueHeading(GeoMath.BearingTo(near, far));
        TrueHeading reciprocal = course.ToReciprocal();
        Assert.True(bearing.AbsAngleTo(reciprocal) < 0.1, $"bearing {bearing.Degrees:F2} should be {reciprocal.Degrees:F2}");
        Assert.Null(shape.Labels);
    }

    /// <summary>
    /// A jet vectored onto KDCA LDA-X 19 (X19, a final offset from the runway) 12 nm out, cleared for it on its heading
    /// with no route: the implied PTAC activates at once, installs the intercept, clears the route and the assigned heading.
    /// </summary>
    private static AircraftState VectoredOntoKdcaX19()
    {
        TestVnasData.EnsureInitialized();
        NavigationDatabase navDb = NavigationDatabase.Instance;
        RunwayInfo runway = Assert.IsType<RunwayInfo>(navDb.GetRunway("DCA", "19"));
        CifpApproachProcedure procedure = Assert.IsType<CifpApproachProcedure>(navDb.GetApproach("DCA", "X19"));
        TrueHeading course = FinalApproachCourseExtractor.Extract(procedure, runway, navDb).Course;
        LatLon onFinal = GeoMath.ProjectPoint(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), course.ToReciprocal(), 12.0);
        LatLon position = GeoMath.ProjectPoint(onFinal, new TrueHeading(course.Degrees - 90.0), 2.0);
        var ac = new AircraftState
        {
            Callsign = "AAL1",
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(course.Degrees + 30.0),
            Altitude = 3000,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            Declination = MagneticDeclination.GetDeclination(position, DeclinationDate),
            FlightPlan = new AircraftFlightPlan { Destination = "KDCA" },
        };
        ac.Targets.AssignedMagneticHeading = ac.MagneticHeading;

        var cmd = new ClearedApproachCommand("X19", "KDCA", true, null, null, null, null, null, null, null, null);
        CommandResult result = ApproachCommandHandler.TryClearedApproach(cmd, ac);

        Assert.True(result.Success, result.Message);
        Assert.IsType<InterceptCoursePhase>(ac.Phases?.CurrentPhase);
        Assert.Empty(ac.Targets.NavigationRoute);
        Assert.Null(ac.Targets.AssignedMagneticHeading);
        return ac;
    }

    /// <summary>
    /// A jet on OAKES3 with EMZOH and HIRMO (the STAR's FM fix) ahead, expecting H12-Z and cleared for it: the clearance
    /// defers to HIRMO, pending.
    /// </summary>
    private static AircraftState DeferredH12Z()
    {
        NavigationTarget hirmo = Oakes3Rw12Targets()[^1];
        (double lat, double lon) = NavigationDatabase.Instance.GetFixPosition("EMZOH") ?? throw new InvalidOperationException("EMZOH missing");
        AircraftState ac = Routed([new NavigationTarget { Name = "EMZOH", Position = new LatLon(lat, lon) }, hirmo]);
        ac.Position = new LatLon(37.5, -121.8);
        ac.Approach.Expected = "H12-Z";
        ac.Procedure.DestinationRunway = "12";

        var cmd = new ClearedApproachCommand("H12-Z", "KOAK", false, null, null, null, null, null, null, null, null);
        CommandResult result = ApproachCommandHandler.TryClearedApproach(cmd, ac);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Approach.PendingClearance);
        return ac;
    }

    /// <summary>An airborne aircraft bound for KOAK with no route, no clearance and no expected approach.</summary>
    private static AircraftState AirborneToKoak()
    {
        TestVnasData.EnsureInitialized();
        return new AircraftState
        {
            Callsign = "SWA22",
            AircraftType = "B738",
            Position = new LatLon(37.85, -122.05),
            Altitude = 5000,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Destination = "KOAK" },
        };
    }

    private static ApproachClearance KoakClearance(string approachId, RunwayInfo runway) =>
        new()
        {
            ApproachId = approachId,
            AirportCode = "KOAK",
            RunwayId = runway.Designator,
            FinalApproachCourse = runway.TrueHeading,
            TransitionName = null,
        };

    private static RunwayInfo KoakRunway(string id) => Assert.IsType<RunwayInfo>(NavigationDatabase.Instance.GetRunway("KOAK", id));

    /// <summary>
    /// The cleared final runs from the aircraft's distance out along the course (its distance from the anchor projected onto
    /// the reciprocal), never shorter than 5 nm, to the anchor.
    /// </summary>
    [Theory]
    [InlineData(12.0, 0.0, 12.0)]
    [InlineData(12.0, 2.0, 12.0)]
    [InlineData(2.0, 0.0, 5.0)]
    public void ClearedFinal_ReachesOutToTheAircraft_NeverShorterThanFiveMiles(double outNm, double offsetNm, double farNm)
    {
        AircraftState ac = VectoredOntoKdcaX19();
        ApproachClearance clearance = Assert.IsType<ApproachClearance>(ac.Phases!.ActiveApproach);
        LatLon anchor = AnchorOf(clearance, Assert.IsType<RunwayInfo>(ac.Phases.AssignedRunway));
        LatLon onCourse = GeoMath.ProjectPoint(anchor, clearance.FinalApproachCourse.ToReciprocal(), outNm);
        ac.Position = GeoMath.ProjectPoint(onCourse, new TrueHeading(clearance.FinalApproachCourse.Degrees - 90.0), offsetNm);

        List<NavRouteShapeDto> shapes = NavRouteOverlayProjector.BuildShapes(ac);

        AssertFinalLine(
            anchor,
            clearance.FinalApproachCourse,
            farNm,
            0.1,
            Assert.Single(shapes, s => s.Kind == NavRouteShapeKind.FinalApproachCourse)
        );
        Assert.DoesNotContain(shapes, s => s.Kind == NavRouteShapeKind.ExpectedFinalApproachCourse);
    }

    /// <summary>
    /// A clearance deferred to the route's end draws its final solid, reaching out to the aircraft like a cleared one, and
    /// neither the STAR's route-end course nor the expected approach's dashed final.
    /// </summary>
    [Theory]
    [InlineData(12.0, 12.0)]
    [InlineData(2.0, 5.0)]
    public void DeferredCapp_DrawsTheFinalOutToTheAircraft_AndNoRouteEndTail(double outNm, double farNm)
    {
        AircraftState ac = DeferredH12Z();
        PendingApproachInfo pending = Assert.IsType<PendingApproachInfo>(ac.Approach.PendingClearance);
        LatLon anchor = AnchorOf(pending.Clearance, pending.AssignedRunway);
        ac.Position = GeoMath.ProjectPoint(anchor, pending.Clearance.FinalApproachCourse.ToReciprocal(), outNm);

        List<NavRouteShapeDto> shapes = NavRouteOverlayProjector.BuildShapes(ac);

        AssertFinalLine(
            anchor,
            pending.Clearance.FinalApproachCourse,
            farNm,
            0.1,
            Assert.Single(shapes, s => s.Kind == NavRouteShapeKind.FinalApproachCourse)
        );
        Assert.DoesNotContain(shapes, s => s.Kind == NavRouteShapeKind.RouteEndCourse);
        Assert.DoesNotContain(shapes, s => s.Kind == NavRouteShapeKind.ExpectedFinalApproachCourse);
    }

    /// <summary>
    /// The expected approach's final is the client's: 5 nm back from the published anchor (else the first common-leg fix)
    /// along the reciprocal of the published course, to the anchor (else the threshold).
    /// </summary>
    [Fact]
    public void ExpectedApproachOnly_DrawsADashedExpectedFinal()
    {
        AircraftState ac = Routed([Oakes3Rw12Targets()[^1]]);
        ac.Approach.Expected = "I30";
        NavigationDatabase navDb = NavigationDatabase.Instance;
        string id = navDb.ResolveApproachCandidates("KOAK", "I30")[0];
        CifpApproachProcedure procedure = Assert.IsType<CifpApproachProcedure>(navDb.GetApproach("KOAK", id));
        RunwayInfo runway = Assert.IsType<RunwayInfo>(navDb.GetRunway("KOAK", Assert.IsType<string>(procedure.Runway)));
        FinalApproachCourseResult fac = FinalApproachCourseExtractor.Extract(procedure, runway, navDb);
        LatLon? anchor = ((fac.AnchorLat is { } lat) && (fac.AnchorLon is { } lon)) ? new LatLon(lat, lon) : null;
        LatLon farOrigin =
            anchor
            ?? procedure
                .CommonLegs.Where(l => !string.IsNullOrEmpty(l.FixIdentifier))
                .Select(l => l.ResolveFixPosition(navDb))
                .Where(p => p is not null)
                .Select(p => new LatLon(p!.Value.Lat, p.Value.Lon))
                .First();
        LatLon near = anchor ?? new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude);
        LatLon far = GeoMath.ProjectPoint(farOrigin, fac.Course.ToReciprocal(), 5.0);

        List<NavRouteShapeDto> shapes = NavRouteOverlayProjector.BuildShapes(ac);

        NavRouteShapeDto shape = Assert.Single(shapes, s => s.Kind == NavRouteShapeKind.ExpectedFinalApproachCourse);
        Assert.Equal(2, shape.Points.Count);
        Assert.Equal(far.Lat, shape.Points[0][0], 9);
        Assert.Equal(far.Lon, shape.Points[0][1], 9);
        Assert.Equal(near.Lat, shape.Points[1][0], 9);
        Assert.Equal(near.Lon, shape.Points[1][1], 9);
        Assert.Null(shape.Labels);
        Assert.DoesNotContain(shapes, s => s.Kind == NavRouteShapeKind.FinalApproachCourse);
    }

    [Fact]
    public void LandedAircraft_DrawsNoFinalApproachCourse()
    {
        AircraftState ac = VectoredOntoKdcaX19();
        ac.IsOnGround = true;

        Assert.NotNull(ac.Phases?.ActiveApproach);
        Assert.DoesNotContain(NavRouteOverlayProjector.BuildShapes(ac), IsFinal);
    }

    /// <summary>
    /// A clearance restored from a snapshot carries no procedure, and draws its final from its stored fields: a stored
    /// course 7° off the published one draws the line on the stored course.
    /// </summary>
    [Fact]
    public void RestoredClearanceWithoutProcedure_DrawsTheStoredCourse()
    {
        AircraftState ac = VectoredOntoKdcaX19();
        ApproachClearance live = Assert.IsType<ApproachClearance>(ac.Phases!.ActiveApproach);
        Assert.NotNull(live.Procedure);
        var shifted = new TrueHeading(live.FinalApproachCourse.Degrees + 7.0);
        ac.Phases.ActiveApproach = new ApproachClearance
        {
            ApproachId = live.ApproachId,
            AirportCode = live.AirportCode,
            RunwayId = live.RunwayId,
            FinalApproachCourse = shifted,
            TransitionName = live.TransitionName,
            FinalApproachAnchorLat = live.FinalApproachAnchorLat,
            FinalApproachAnchorLon = live.FinalApproachAnchorLon,
        };
        Assert.Null(ac.Phases.ActiveApproach.Procedure);
        LatLon anchor = AnchorOf(live, Assert.IsType<RunwayInfo>(ac.Phases.AssignedRunway));
        ac.Position = GeoMath.ProjectPoint(anchor, shifted.ToReciprocal(), 2.0);

        NavRouteShapeDto shape = Assert.Single(NavRouteOverlayProjector.BuildShapes(ac), s => s.Kind == NavRouteShapeKind.FinalApproachCourse);

        AssertFinalLine(anchor, shifted, 5.0, 0.1, shape);
    }

    /// <summary>An IFR visual approach, cleared, pending or expected, draws no final, and its shape key adds none.</summary>
    [Theory]
    [InlineData('C')]
    [InlineData('P')]
    [InlineData('X')]
    public void VisualApproach_DrawsNoFinal(char state)
    {
        AircraftState ac = AirborneToKoak();
        RunwayInfo rw30 = KoakRunway("30");
        ApproachClearance visual = KoakClearance(ApproachIds.Visual("30"), rw30);
        switch (state)
        {
            case 'C':
                ac.Phases = new PhaseList { AssignedRunway = rw30, ActiveApproach = visual };
                break;
            case 'P':
                ac.Approach.PendingClearance = new PendingApproachInfo
                {
                    Clearance = visual,
                    AssignedRunway = rw30,
                    ConnectingFix = null,
                };
                break;
            default:
                ac.Approach.Expected = ApproachIds.Visual("30");
                break;
        }

        Assert.DoesNotContain(NavRouteOverlayProjector.BuildShapes(ac), IsFinal);
        Assert.DoesNotContain("|A", NavRouteOverlayProjector.BuildPendingShapeKey(ac));
    }

    /// <summary>A pattern entry flies no approach clearance, and draws no final.</summary>
    [Fact]
    public void PatternEntry_DrawsNoFinal()
    {
        AircraftState ac = AirborneToKoak();
        RunwayInfo rw30 = KoakRunway("30");
        ac.Phases = new PhaseList { AssignedRunway = rw30 };
        ac.Phases.Add(
            new PatternEntryPhase
            {
                EntryLat = rw30.ThresholdLatitude,
                EntryLon = rw30.ThresholdLongitude,
                PatternAltitude = 1000,
                Kind = PatternEntryKind.FortyFive,
            }
        );

        Assert.DoesNotContain(NavRouteOverlayProjector.BuildShapes(ac), IsFinal);
        Assert.DoesNotContain("|A", NavRouteOverlayProjector.BuildPendingShapeKey(ac));
    }

    private static Phase HidingPhase(string name) =>
        name switch
        {
            "GoAround" => new GoAroundPhase(),
            "LowApproach" => new LowApproachPhase(),
            "TouchAndGo" => new TouchAndGoPhase(),
            "StopAndGo" => new StopAndGoPhase(),
            "MissedApproach" => new ApproachNavigationPhase { Fixes = [], IsMissedApproach = true },
            _ => new HoldingPatternPhase
            {
                FixName = "HOLDR",
                FixLat = Fix.Lat,
                FixLon = Fix.Lon,
                InboundCourse = 270,
                LegLength = 1.0,
                IsMinuteBased = true,
                Direction = TurnDirection.Right,
                IsMissedApproachHold = true,
            },
        };

    /// <summary>
    /// The final hides once a go-around or missed approach starts and during a low approach or touch-and-go climbout, though
    /// the clearance is still held; the shape key adds none either.
    /// </summary>
    [Theory]
    [InlineData("GoAround")]
    [InlineData("LowApproach")]
    [InlineData("TouchAndGo")]
    [InlineData("StopAndGo")]
    [InlineData("MissedApproach")]
    [InlineData("MissedApproachHold")]
    public void GoAroundOrClimbout_HidesTheFinal(string phase)
    {
        AircraftState ac = VectoredOntoKdcaX19();
        ApproachClearance clearance = Assert.IsType<ApproachClearance>(ac.Phases!.ActiveApproach);
        ac.Phases = new PhaseList { AssignedRunway = ac.Phases.AssignedRunway, ActiveApproach = clearance };
        ac.Phases.Add(HidingPhase(phase));

        Assert.DoesNotContain(NavRouteOverlayProjector.BuildShapes(ac), IsFinal);
        Assert.DoesNotContain("|A", NavRouteOverlayProjector.BuildPendingShapeKey(ac));
    }

    /// <summary>A phase that does nothing: what follows a completed hiding phase in these lists.</summary>
    private sealed class IdlePhase : Phase
    {
        public override string Name => "Idle";

        public override PhaseDto ToSnapshot() => throw new NotSupportedException();

        public override void OnStart(PhaseContext ctx) { }

        public override bool OnTick(PhaseContext ctx) => false;
    }

    private static PhaseContext ContextFor(AircraftState ac) =>
        new()
        {
            ActiveRunways = ActiveRunways.Empty,
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = 1.0,
            Logger = NullLogger.Instance,
        };

    /// <summary>
    /// Gives <paramref name="ac"/> a new list holding <paramref name="clearance"/>: <paramref name="hider"/> completed, then an
    /// idle phase made current, or the list run out past both.
    /// </summary>
    private static void AfterCompleted(AircraftState ac, ApproachClearance clearance, Phase hider, bool runOut)
    {
        ac.Phases = new PhaseList { AssignedRunway = ac.Phases!.AssignedRunway, ActiveApproach = clearance };
        ac.Phases.Add(hider);
        ac.Phases.Add(new IdlePhase());
        hider.Status = PhaseStatus.Completed;
        PhaseContext ctx = ContextFor(ac);
        if (runOut)
        {
            ac.Phases.Clear(ctx);
        }
        else
        {
            ac.Phases.SkipTo<IdlePhase>(ctx);
            Assert.IsType<IdlePhase>(ac.Phases.CurrentPhase);
        }
    }

    /// <summary>
    /// The clearance survives a go-around, but the final stays hidden once the hiding phase is over, with the next phase
    /// current or the list run out; an expected approach still stored draws no dashed final either, and the key adds none.
    /// </summary>
    [Theory]
    [InlineData("GoAround", false)]
    [InlineData("GoAround", true)]
    [InlineData("LowApproach", false)]
    [InlineData("LowApproach", true)]
    [InlineData("TouchAndGo", false)]
    [InlineData("TouchAndGo", true)]
    [InlineData("StopAndGo", false)]
    [InlineData("StopAndGo", true)]
    [InlineData("MissedApproach", false)]
    [InlineData("MissedApproach", true)]
    [InlineData("MissedApproachHold", false)]
    [InlineData("MissedApproachHold", true)]
    public void AfterTheHidingPhase_TheFinalStaysHidden(string phase, bool runOut)
    {
        AircraftState ac = VectoredOntoKdcaX19();
        ac.Approach.Expected = "X19";
        AfterCompleted(ac, Assert.IsType<ApproachClearance>(ac.Phases!.ActiveApproach), HidingPhase(phase), runOut);

        Assert.NotNull(ac.Phases!.ActiveApproach);
        Assert.DoesNotContain(NavRouteOverlayProjector.BuildShapes(ac), IsFinal);
        Assert.DoesNotContain("|A", NavRouteOverlayProjector.BuildPendingShapeKey(ac));
    }

    /// <summary>A cleared option approach still draws its final before touchdown: a pending touch-and-go does not hide it.</summary>
    [Fact]
    public void PendingTouchAndGoAfterTheFinal_DrawsTheFinal()
    {
        AircraftState ac = VectoredOntoKdcaX19();
        ApproachClearance clearance = Assert.IsType<ApproachClearance>(ac.Phases!.ActiveApproach);
        ac.Phases = new PhaseList { AssignedRunway = ac.Phases.AssignedRunway, ActiveApproach = clearance };
        ac.Phases.Add(new FinalApproachPhase());
        ac.Phases.Add(new TouchAndGoPhase());
        Assert.IsType<FinalApproachPhase>(ac.Phases.CurrentPhase);

        Assert.Single(NavRouteOverlayProjector.BuildShapes(ac), s => s.Kind == NavRouteShapeKind.FinalApproachCourse);
        Assert.Contains("|AC:X19:", NavRouteOverlayProjector.BuildPendingShapeKey(ac));
    }

    /// <summary>A snapshot-restored clearance (no procedure) after a completed go-around draws no final.</summary>
    [Fact]
    public void RestoredClearanceAfterACompletedGoAround_DrawsNoFinal()
    {
        AircraftState ac = VectoredOntoKdcaX19();
        var restored = ApproachClearance.FromSnapshot(Assert.IsType<ApproachClearance>(ac.Phases!.ActiveApproach).ToSnapshot());
        Assert.Null(restored.Procedure);
        AfterCompleted(ac, restored, new GoAroundPhase(), runOut: false);

        Assert.DoesNotContain(NavRouteOverlayProjector.BuildShapes(ac), IsFinal);
        Assert.DoesNotContain("|A", NavRouteOverlayProjector.BuildPendingShapeKey(ac));
    }

    /// <summary>A hold at the clearance's missed-approach holding fix is the missed approach's, though not marked as one.</summary>
    [Fact]
    public void HoldAtTheMapHoldFix_DrawsNoFinal()
    {
        AircraftState ac = VectoredOntoKdcaX19();
        ApproachClearance live = Assert.IsType<ApproachClearance>(ac.Phases!.ActiveApproach);
        var clearance = new ApproachClearance
        {
            ApproachId = live.ApproachId,
            AirportCode = live.AirportCode,
            RunwayId = live.RunwayId,
            FinalApproachCourse = live.FinalApproachCourse,
            TransitionName = live.TransitionName,
            FinalApproachAnchorLat = live.FinalApproachAnchorLat,
            FinalApproachAnchorLon = live.FinalApproachAnchorLon,
            MapHold = new MissedApproachHold("HOLDR", Fix.Lat, Fix.Lon, 270, 1.0, true, TurnDirection.Right),
        };
        ac.Phases = new PhaseList { AssignedRunway = ac.Phases.AssignedRunway, ActiveApproach = clearance };
        var hold = (HoldingPatternPhase)HidingPhase("MissedApproachHold");
        ac.Phases.Add(
            new HoldingPatternPhase
            {
                FixName = "holdr",
                FixLat = hold.FixLat,
                FixLon = hold.FixLon,
                InboundCourse = hold.InboundCourse,
                LegLength = hold.LegLength,
                IsMinuteBased = hold.IsMinuteBased,
                Direction = hold.Direction,
                IsMissedApproachHold = false,
            }
        );

        Assert.DoesNotContain(NavRouteOverlayProjector.BuildShapes(ac), IsFinal);
        Assert.DoesNotContain("|A", NavRouteOverlayProjector.BuildPendingShapeKey(ac));
    }

    /// <summary>The expected approach's key is its stored expectation alone: swapping the navdata does not change it.</summary>
    [Theory]
    [InlineData('X', "|AX:I30")]
    [InlineData('C', null)]
    public void ExpectedFinalKey_IsTheStoredExpectation_WhateverTheNavdata(char state, string? expectedKey)
    {
        AircraftState ac = AirborneToKoak();
        ac.Approach.Expected = "I30";
        if (state == 'C')
        {
            // Cleared with no assigned runway: the key must not resolve the runway from navdata either.
            RunwayInfo rw30 = KoakRunway("30");
            ac.Phases = new PhaseList { ActiveApproach = KoakClearance("I30", rw30) };
            expectedKey = string.Create(CultureInfo.InvariantCulture, $"|AC:I30:{rw30.TrueHeading.Degrees:F1}");
        }

        string key = NavRouteOverlayProjector.BuildPendingShapeKey(ac);
        string swapped;
        using (NavigationDatabase.ScopedOverride(NavigationDatabase.ForTesting()))
        {
            swapped = NavRouteOverlayProjector.BuildPendingShapeKey(ac);
        }

        Assert.Equal(expectedKey, key);
        Assert.Equal(key, swapped);
    }

    /// <summary>A cleared or pending approach's key is its state, approach id and stored true course to a tenth.</summary>
    [Theory]
    [InlineData('C')]
    [InlineData('P')]
    public void ClearedFinalKey_IsTheStateApproachAndStoredCourse(char state)
    {
        AircraftState ac = AirborneToKoak();
        RunwayInfo rw30 = KoakRunway("30");
        ApproachClearance clearance = KoakClearance("I30", rw30);
        if (state == 'C')
        {
            ac.Phases = new PhaseList { AssignedRunway = rw30, ActiveApproach = clearance };
        }
        else
        {
            ac.Approach.PendingClearance = new PendingApproachInfo
            {
                Clearance = clearance,
                AssignedRunway = rw30,
                ConnectingFix = null,
            };
        }

        Assert.Equal(
            string.Create(CultureInfo.InvariantCulture, $"|A{state}:I30:{rw30.TrueHeading.Degrees:F1}"),
            NavRouteOverlayProjector.BuildPendingShapeKey(ac)
        );
    }
}
