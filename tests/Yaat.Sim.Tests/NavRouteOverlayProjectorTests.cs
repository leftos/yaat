using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;

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
}
