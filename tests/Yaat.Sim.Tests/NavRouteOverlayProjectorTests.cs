using Xunit;
using Yaat.Sim;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Phases.Tower;

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
        var points = NavRouteOverlayProjector.HoldRacetrackPoints(Fix, inboundCourse: 90, isRight: true, legNm: 5.0, radiusNm: 1.0);

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
        var points = NavRouteOverlayProjector.HoldRacetrackPoints(Fix, inboundCourse: 90, isRight: true, legNm: 5.0, radiusNm: 1.0);

        // Far end of the inbound leg is west of the fix (outbound bearing 270).
        Assert.True(points[0].Lon < Fix.Lon, "inbound leg extends west of the fix");
        // The outbound leg / turns sit ~2 turn-radii south of the inbound line.
        Assert.True(points.Any(p => p.Lat < Fix.Lat - (1.5 / 60.0)), "right-turn hold on an easterly inbound should extend south");
        Assert.DoesNotContain(points, p => p.Lat > Fix.Lat + (0.5 / 60.0));
    }

    [Fact]
    public void HoldRacetrack_LeftTurns_MirrorsToOppositeSide()
    {
        var points = NavRouteOverlayProjector.HoldRacetrackPoints(Fix, inboundCourse: 90, isRight: false, legNm: 5.0, radiusNm: 1.0);

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

        var shapes = NavRouteOverlayProjector.BuildShapes(ac);

        var shape = Assert.Single(shapes);
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

        var shapes = NavRouteOverlayProjector.BuildShapes(ac);

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

        var shapes = NavRouteOverlayProjector.BuildShapes(ac);

        var shape = Assert.Single(shapes);
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
}
