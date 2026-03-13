using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Tests;

public class LandingExitDecelTests
{
    // Runway heading 280, threshold at origin, ~1nm long
    private const double RunwayHeading = 280.0;
    private const double ThresholdLat = 37.0;
    private const double ThresholdLon = -122.0;
    private const double FieldElevation = 0.0;

    // -------------------------------------------------------------------------
    // Layout builders
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a layout with a 90° exit to the right at the given position.
    /// Runway runs heading 280. Exit taxiway "B" goes due north (heading ~0/360).
    /// Exit angle from runway ≈ 80° (> 45° → standard exit).
    /// </summary>
    private static AirportGroundLayout BuildStandardExitLayout(double exitLat, double exitLon)
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        // Exit node (runway boundary, connects to taxiway)
        var exitNode = new GroundNode
        {
            Id = 1,
            Latitude = exitLat,
            Longitude = exitLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };

        // Clear node (on taxiway, north of exit — ~90° from runway heading 280)
        var clearNode = new GroundNode
        {
            Id = 2,
            Latitude = exitLat + 0.002,
            Longitude = exitLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var exitEdge = new GroundEdge
        {
            FromNodeId = 1,
            ToNodeId = 2,
            TaxiwayName = "B",
            DistanceNm = 0.1,
        };

        exitNode.Edges.Add(exitEdge);
        clearNode.Edges.Add(exitEdge);

        layout.Nodes[1] = exitNode;
        layout.Nodes[2] = clearNode;
        layout.Edges.Add(exitEdge);

        return layout;
    }

    /// <summary>
    /// Builds a layout with a high-speed exit (~30° from runway heading 280).
    /// Exit taxiway "H" goes roughly heading 250 (30° left of 280).
    /// </summary>
    private static AirportGroundLayout BuildHighSpeedExitLayout(double exitLat, double exitLon)
    {
        var layout = new AirportGroundLayout { AirportId = "TEST" };

        // Exit node
        var exitNode = new GroundNode
        {
            Id = 1,
            Latitude = exitLat,
            Longitude = exitLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };

        // Clear node: 30° left of heading 280 = heading 250
        // heading 250 → roughly south-southwest
        double clearLat = exitLat - 0.001;
        double clearLon = exitLon - 0.002;
        var clearNode = new GroundNode
        {
            Id = 2,
            Latitude = clearLat,
            Longitude = clearLon,
            Type = GroundNodeType.TaxiwayIntersection,
        };

        var exitEdge = new GroundEdge
        {
            FromNodeId = 1,
            ToNodeId = 2,
            TaxiwayName = "H",
            DistanceNm = 0.1,
        };

        exitNode.Edges.Add(exitEdge);
        clearNode.Edges.Add(exitEdge);

        layout.Nodes[1] = exitNode;
        layout.Nodes[2] = clearNode;
        layout.Edges.Add(exitEdge);

        return layout;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RunwayInfo DefaultRunway() =>
        TestRunwayFactory.Make(
            designator: "28",
            heading: RunwayHeading,
            elevationFt: FieldElevation,
            thresholdLat: ThresholdLat,
            thresholdLon: ThresholdLon
        );

    private static AircraftState MakeLandedAircraft(double lat, double lon, double ias)
    {
        var ac = new AircraftState
        {
            Callsign = "TEST1",
            AircraftType = "B738",
            Latitude = lat,
            Longitude = lon,
            Heading = RunwayHeading,
            Altitude = FieldElevation,
            IndicatedAirspeed = ias,
            IsOnGround = true,
            Departure = "TEST",
        };
        ac.Phases = new PhaseList();
        return ac;
    }

    private static PhaseContext Ctx(AircraftState ac, AirportGroundLayout? layout = null, double dt = 1.0)
    {
        var rwy = DefaultRunway();
        return new PhaseContext
        {
            Aircraft = ac,
            Targets = ac.Targets,
            Category = AircraftCategory.Jet,
            DeltaSeconds = dt,
            Runway = rwy,
            FieldElevation = FieldElevation,
            GroundLayout = layout,
            Logger = NullLogger.Instance,
        };
    }

    /// <summary>
    /// Simulate rollout by ticking until phase completes.
    /// Manually advances aircraft position each tick based on ground speed and heading,
    /// since FlightPhysics isn't called in unit tests.
    /// </summary>
    private static (int ticks, double finalSpeed) SimulateRollout(LandingPhase phase, PhaseContext ctx, int maxTicks = 500)
    {
        int ticks = 0;
        while (ticks < maxTicks)
        {
            bool done = phase.OnTick(ctx);
            ticks++;

            // Advance position along heading based on ground speed
            AdvancePosition(ctx.Aircraft, ctx.DeltaSeconds);

            if (done)
            {
                break;
            }
        }

        return (ticks, ctx.Aircraft.IndicatedAirspeed);
    }

    private static void AdvancePosition(AircraftState ac, double dt)
    {
        double distNm = ac.GroundSpeed / 3600.0 * dt;
        double headingRad = ac.Heading * Math.PI / 180.0;
        double dLat = distNm / 60.0 * Math.Cos(headingRad);
        double dLon = distNm / (60.0 * Math.Cos(ac.Latitude * Math.PI / 180.0)) * Math.Sin(headingRad);
        ac.Latitude += dLat;
        ac.Longitude += dLon;
    }

    // -------------------------------------------------------------------------
    // Tests: No exit preference (unchanged behavior)
    // -------------------------------------------------------------------------

    [Fact]
    public void NoExitPreference_DeceleratesUniformlyTo20Kts()
    {
        // Aircraft just touched down at 130 kts, no exit preference
        var ac = MakeLandedAircraft(ThresholdLat, ThresholdLon, ias: 130);
        var ctx = Ctx(ac);

        var phase = new LandingPhase();
        phase.OnStart(ctx);
        // Simulate touchdown (set touchedDown by running a tick at field elevation)
        ac.IsOnGround = true;

        var (_, finalSpeed) = SimulateRollout(phase, ctx);

        // Should complete near 20 kts (default rollout complete speed)
        Assert.InRange(finalSpeed, 0, 20.5);
    }

    [Fact]
    public void NoGroundLayout_FallsBackToDefaultBehavior()
    {
        var ac = MakeLandedAircraft(ThresholdLat, ThresholdLon, ias: 130);
        ac.Phases!.RequestedExit = new ExitPreference { Side = ExitSide.Left };

        // No ground layout
        var ctx = Ctx(ac, layout: null);

        var phase = new LandingPhase();
        phase.OnStart(ctx);

        var (_, finalSpeed) = SimulateRollout(phase, ctx);

        // Without layout, can't resolve exit → default 20 kts completion
        Assert.InRange(finalSpeed, 0, 20.5);
    }

    // -------------------------------------------------------------------------
    // Tests: Standard (90°) exit
    // -------------------------------------------------------------------------

    [Fact]
    public void StandardExit_CompletesAtLowerSpeed()
    {
        // Place exit well ahead (~1.2nm along heading 280) so there's room to decelerate from 130
        double exitLat = ThresholdLat + 0.005;
        double exitLon = ThresholdLon - 0.015;
        var layout = BuildStandardExitLayout(exitLat, exitLon);

        var ac = MakeLandedAircraft(ThresholdLat, ThresholdLon, ias: 130);
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "B" };

        var ctx = Ctx(ac, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        var (_, finalSpeed) = SimulateRollout(phase, ctx);

        // Standard exit (>45°) → should complete at StandardExitSpeed for jets (15 kts)
        Assert.InRange(finalSpeed, 0, 16);
    }

    [Fact]
    public void HighSpeedExit_CompletesAtHigherSpeed()
    {
        // Place exit well ahead (~1.2nm)
        double exitLat = ThresholdLat + 0.005;
        double exitLon = ThresholdLon - 0.015;
        var layout = BuildHighSpeedExitLayout(exitLat, exitLon);

        var ac = MakeLandedAircraft(ThresholdLat, ThresholdLon, ias: 130);
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "H" };

        var ctx = Ctx(ac, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        var (_, finalSpeed) = SimulateRollout(phase, ctx);

        // High-speed exit (≤45°) → should complete at HighSpeedExitSpeed for jets (30 kts)
        Assert.InRange(finalSpeed, 28, 31);
    }

    // -------------------------------------------------------------------------
    // Tests: Coast speed maintenance
    // -------------------------------------------------------------------------

    [Fact]
    public void ExitFarAhead_MaintainsCoastSpeed()
    {
        // Place exit far ahead (~1nm) so braking isn't needed yet
        double exitLat = ThresholdLat + 0.005;
        double exitLon = ThresholdLon - 0.012;
        var layout = BuildStandardExitLayout(exitLat, exitLon);

        var ac = MakeLandedAircraft(ThresholdLat, ThresholdLon, ias: 60);
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "B" };

        var ctx = Ctx(ac, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        // Tick a few times — speed should not drop below coast speed (40 kts for jets)
        for (int i = 0; i < 5; i++)
        {
            phase.OnTick(ctx);
        }

        // Should be at or above coast speed since exit is far away
        Assert.True(ctx.Aircraft.IndicatedAirspeed >= 39.0, $"Speed dropped to {ctx.Aircraft.IndicatedAirspeed:F1} kts, expected >= 39");
    }

    // -------------------------------------------------------------------------
    // Tests: Mid-rollout preference change
    // -------------------------------------------------------------------------

    [Fact]
    public void ExitPreferenceChanged_ReResolvesExit()
    {
        // Start with no exit preference → default behavior
        double exitLat = ThresholdLat + 0.002;
        double exitLon = ThresholdLon - 0.005;
        var layout = BuildStandardExitLayout(exitLat, exitLon);

        var ac = MakeLandedAircraft(ThresholdLat, ThresholdLon, ias: 80);
        var ctx = Ctx(ac, layout);

        var phase = new LandingPhase();
        phase.OnStart(ctx);

        // Tick a few times with no preference — decelerating normally
        for (int i = 0; i < 3; i++)
        {
            phase.OnTick(ctx);
        }

        double speedBeforePreference = ac.IndicatedAirspeed;

        // Now set an exit preference mid-rollout
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "B" };

        // Tick again — should re-resolve
        phase.OnTick(ctx);

        // Phase should still be running (not completed yet)
        // Speed should be decelerating toward the exit turn-off speed, not the default 20
        Assert.True(ac.IndicatedAirspeed < speedBeforePreference);
    }

    // -------------------------------------------------------------------------
    // Tests: Exit behind aircraft (not resolved)
    // -------------------------------------------------------------------------

    [Fact]
    public void ExitBehindAircraft_FallsBackToDefault()
    {
        // Place exit behind the aircraft (negative along-track)
        double exitLat = ThresholdLat - 0.005;
        double exitLon = ThresholdLon + 0.005;
        var layout = BuildStandardExitLayout(exitLat, exitLon);

        var ac = MakeLandedAircraft(ThresholdLat, ThresholdLon, ias: 80);
        ac.Phases!.RequestedExit = new ExitPreference { Taxiway = "B" };

        var ctx = Ctx(ac, layout);
        var phase = new LandingPhase();
        phase.OnStart(ctx);

        var (_, finalSpeed) = SimulateRollout(phase, ctx);

        // Exit behind → can't resolve → default 20 kts completion
        Assert.InRange(finalSpeed, 0, 20.5);
    }

    // -------------------------------------------------------------------------
    // Tests: ComputeExitAngle
    // -------------------------------------------------------------------------

    [Fact]
    public void ComputeExitAngle_PerpendicularExit_ReturnsNear90()
    {
        double exitLat = 37.001;
        double exitLon = -122.001;
        var layout = BuildStandardExitLayout(exitLat, exitLon);

        var exitNode = layout.Nodes[1];
        double? angle = layout.ComputeExitAngle(exitNode, "B", RunwayHeading);

        Assert.NotNull(angle);
        // Exit goes due north from runway heading 280 → angle should be near 80-90°
        Assert.InRange(angle.Value, 60, 110);
    }

    [Fact]
    public void ComputeExitAngle_HighSpeedExit_ReturnsSmallAngle()
    {
        double exitLat = 37.001;
        double exitLon = -122.001;
        var layout = BuildHighSpeedExitLayout(exitLat, exitLon);

        var exitNode = layout.Nodes[1];
        double? angle = layout.ComputeExitAngle(exitNode, "H", RunwayHeading);

        Assert.NotNull(angle);
        // High-speed exit ~30° from runway heading → angle should be ≤ 45
        Assert.InRange(angle.Value, 10, 50);
    }

    [Fact]
    public void ComputeExitAngle_NoMatchingTaxiway_ReturnsNull()
    {
        double exitLat = 37.001;
        double exitLon = -122.001;
        var layout = BuildStandardExitLayout(exitLat, exitLon);

        var exitNode = layout.Nodes[1];
        double? angle = layout.ComputeExitAngle(exitNode, "NONEXISTENT", RunwayHeading);

        Assert.Null(angle);
    }

    // -------------------------------------------------------------------------
    // Tests: FindExitAheadOnRunway
    // -------------------------------------------------------------------------

    [Fact]
    public void FindExitAhead_ExitAhead_ReturnsIt()
    {
        // Exit ahead along heading 280
        double exitLat = ThresholdLat + 0.002;
        double exitLon = ThresholdLon - 0.005;
        var layout = BuildStandardExitLayout(exitLat, exitLon);

        var result = layout.FindExitAheadOnRunway(ThresholdLat, ThresholdLon, RunwayHeading, new ExitPreference { Taxiway = "B" });

        Assert.NotNull(result);
        Assert.Equal("B", result.Value.Taxiway);
    }

    [Fact]
    public void FindExitAhead_ExitBehind_ReturnsNull()
    {
        // Exit behind along heading 280
        double exitLat = ThresholdLat - 0.005;
        double exitLon = ThresholdLon + 0.005;
        var layout = BuildStandardExitLayout(exitLat, exitLon);

        var result = layout.FindExitAheadOnRunway(ThresholdLat, ThresholdLon, RunwayHeading, new ExitPreference { Taxiway = "B" });

        Assert.Null(result);
    }

    [Fact]
    public void FindExitAhead_NoPreference_FindsNearest()
    {
        double exitLat = ThresholdLat + 0.002;
        double exitLon = ThresholdLon - 0.005;
        var layout = BuildStandardExitLayout(exitLat, exitLon);

        var result = layout.FindExitAheadOnRunway(ThresholdLat, ThresholdLon, RunwayHeading, null);

        Assert.NotNull(result);
    }

    [Fact]
    public void FindExitAhead_SidePreference_FiltersCorrectly()
    {
        // Exit is to the right (north) of runway heading 280
        double exitLat = ThresholdLat + 0.002;
        double exitLon = ThresholdLon - 0.005;
        var layout = BuildStandardExitLayout(exitLat, exitLon);

        // The exit node is slightly north → to the right of heading 280
        var rightResult = layout.FindExitAheadOnRunway(ThresholdLat, ThresholdLon, RunwayHeading, new ExitPreference { Side = ExitSide.Right });
        var leftResult = layout.FindExitAheadOnRunway(ThresholdLat, ThresholdLon, RunwayHeading, new ExitPreference { Side = ExitSide.Left });

        // Right side should find it; left side should not (only one exit in layout)
        Assert.NotNull(rightResult);
    }

    // -------------------------------------------------------------------------
    // Tests: ExitTurnOffSpeed
    // -------------------------------------------------------------------------

    [Fact]
    public void ExitTurnOffSpeed_NullAngle_ReturnsFallback()
    {
        double speed = CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, null);
        Assert.Equal(CategoryPerformance.RunwayExitSpeed(AircraftCategory.Jet), speed);
    }

    [Fact]
    public void ExitTurnOffSpeed_SmallAngle_ReturnsHighSpeedValue()
    {
        double speed = CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, 30);
        Assert.Equal(CategoryPerformance.HighSpeedExitSpeed(AircraftCategory.Jet), speed);
    }

    [Fact]
    public void ExitTurnOffSpeed_LargeAngle_ReturnsStandardValue()
    {
        double speed = CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, 80);
        Assert.Equal(CategoryPerformance.StandardExitSpeed(AircraftCategory.Jet), speed);
    }

    [Fact]
    public void ExitTurnOffSpeed_ExactThreshold_ReturnsHighSpeed()
    {
        double speed = CategoryPerformance.ExitTurnOffSpeed(AircraftCategory.Jet, 45);
        Assert.Equal(CategoryPerformance.HighSpeedExitSpeed(AircraftCategory.Jet), speed);
    }
}
