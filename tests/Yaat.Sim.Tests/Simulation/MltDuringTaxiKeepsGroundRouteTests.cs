using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Bug (live session 2026-08-18, OAK / S2-OAK-3 VFR Sequencing): N436MS was holding short of its
/// assigned 28R at taxiway B, got re-taxied with <c>TAXI B 28L</c> (implicit cross of 28R), and the
/// controller issued a standalone <c>MLT</c> while the aircraft was in <see cref="CrossingRunwayPhase"/>.
/// <c>PatternCommandHandler.TryChangePatternDirection</c> found no pattern phases and spliced a full
/// circuit (starting at <see cref="UpwindPhase"/>) directly after the crossing phase — ahead of the
/// remaining taxi-to-28L phases. When the crossing completed, the aircraft entered Upwind while still
/// on the ground: it accelerated toward pattern speed with no <c>TakeoffPhase</c> to rotate it and ran
/// off the far end of the runway. Every subsequent CTO failed with "Aircraft is not lined up and
/// waiting" because the current phase was a pattern leg.
///
/// Expected: MLT issued to an on-ground departure only pre-arms the traffic direction; the aircraft
/// keeps its taxi route, holds short of 28L, and departs normally on CTO.
/// </summary>
public class MltDuringTaxiKeepsGroundRouteTests(ITestOutputHelper output)
{
    private const string Callsign = "N436MS";

    // N436MS's spawn-snapped position from the live session log: on taxiway B just north of the
    // 28R/10L crossing, nosed south-southeast along B (heading 162 true).
    private const double SpawnLat = 37.725690;
    private const double SpawnLon = -122.204525;
    private const double SpawnHeadingTrue = 162.0;

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("GroundCommandHandler", LogLevel.Information)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("TakeoffPhase", LogLevel.Debug)
            .InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    [Fact]
    public void MltWhileCrossingToReTaxiedRunway_KeepsTaxiRoute_AndDepartsAfterCto()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        var aircraft = SpawnOnTaxiwayB(engine, layout);

        // Preset leg: taxi to the assigned runway 28R and hold short at B (where DN's aircraft started).
        var taxi28R = engine.SendCommand(Callsign, "TAXI B 28R");
        Assert.True(taxi28R.Success, taxi28R.Message);
        var at28RBar = TickUntilHoldingShort(engine, "28R", HoldShortReason.DestinationRunway, 240);
        Assert.NotNull(at28RBar);

        // The controller's re-taxi to the parallel: crosses 28R/10L, destination hold-short 28L.
        var taxi28L = engine.SendCommand(Callsign, "TAXI B 28L");
        Assert.True(taxi28L.Success, taxi28L.Message);

        // Reach the crossing state (clearing the 28R crossing bar if the route paused there).
        var crossing = TickUntilCrossingRunway(engine, 120);
        Assert.NotNull(crossing);

        // Standalone MLT mid-crossing: must only pre-arm the pattern direction for the eventual
        // departure — never splice airborne pattern phases into a ground aircraft's chain.
        var mlt = engine.SendCommand(Callsign, "MLT");
        Assert.True(mlt.Success, mlt.Message);
        aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.Equal(PatternDirection.Left, aircraft.Pattern.TrafficDirection);

        // The aircraft must finish the crossing and taxi to the 28L bar ON THE GROUND ROUTE.
        // With the bug, the spliced circuit makes Upwind the next phase after the crossing and the
        // aircraft accelerates down the pavement without ever rotating.
        var at28LBar = TickUntilHoldingShort(engine, "28L", HoldShortReason.DestinationRunway, 300);
        Assert.NotNull(at28LBar);

        // Normal departure from the re-taxied runway.
        var cto = engine.SendCommand(Callsign, "CTO MLT");
        Assert.True(cto.Success, cto.Message);

        for (int t = 1; t <= 180; t++)
        {
            engine.TickOneSecond();
            aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);
            if (!aircraft.IsOnGround)
            {
                output.WriteLine($"airborne {t}s after CTO (ias={aircraft.IndicatedAirspeed:F0}, alt={aircraft.Altitude:F0})");
                return;
            }
        }

        Assert.Fail(
            $"never rotated within 180s of CTO; phase={engine.FindAircraft(Callsign)?.Phases?.CurrentPhase?.Name} "
                + $"ias={engine.FindAircraft(Callsign)?.IndicatedAirspeed:F1}"
        );
    }

    /// <summary>
    /// DN's literal first attempt: CTO issued while the aircraft was mid-crossing of 28R en route to
    /// the re-taxied 28L. The rolling-clearance branch only accepted <see cref="TaxiingPhase"/>, so the
    /// clearance was rejected with the misleading "Aircraft is not lined up and waiting". A crossing on
    /// the way to the destination runway is the same rolling situation — the clearance must store and
    /// apply when the taxi route ends at 28L.
    /// </summary>
    [Fact]
    public void CtoWhileCrossingToReTaxiedRunway_StoresRollingClearance_AndDeparts()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        SpawnOnTaxiwayB(engine, layout);

        var taxi28R = engine.SendCommand(Callsign, "TAXI B 28R");
        Assert.True(taxi28R.Success, taxi28R.Message);
        Assert.NotNull(TickUntilHoldingShort(engine, "28R", HoldShortReason.DestinationRunway, 240));

        var taxi28L = engine.SendCommand(Callsign, "TAXI B 28L");
        Assert.True(taxi28L.Success, taxi28L.Message);
        Assert.NotNull(TickUntilCrossingRunway(engine, 120));

        var cto = engine.SendCommand(Callsign, "CTO MLT");
        Assert.True(cto.Success, cto.Message);

        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            var aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);
            FailIfPatternLegOnGround(aircraft, t);
            if (!aircraft.IsOnGround)
            {
                output.WriteLine($"airborne {t}s after rolling CTO (ias={aircraft.IndicatedAirspeed:F0}, alt={aircraft.Altitude:F0})");
                Assert.Equal("28L", aircraft.Phases?.AssignedRunway?.Designator);
                return;
            }
        }

        Assert.Fail(
            $"never rotated within 300s of rolling CTO; phase={engine.FindAircraft(Callsign)?.Phases?.CurrentPhase?.Name} "
                + $"ias={engine.FindAircraft(Callsign)?.IndicatedAirspeed:F1}"
        );
    }

    private HoldingShortPhase? TickUntilHoldingShort(SimulationEngine engine, string runwayDesignator, HoldShortReason reason, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            var aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);
            FailIfPatternLegOnGround(aircraft, t);

            if (
                aircraft.Phases?.CurrentPhase is HoldingShortPhase hold
                && hold.HoldShort.Reason == reason
                && hold.HoldShort.TargetName is { } target
                && RunwayIdentifier.Parse(target).Contains(runwayDesignator)
            )
            {
                output.WriteLine($"t=+{t}: holding short of {target} at node #{hold.HoldShort.NodeId}");
                return hold;
            }
        }

        var last = engine.FindAircraft(Callsign);
        output.WriteLine($"gave up after {maxSeconds}s: phase={last?.Phases?.CurrentPhase?.Name} gs={last?.GroundSpeed:F1}");
        return null;
    }

    private CrossingRunwayPhase? TickUntilCrossingRunway(SimulationEngine engine, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            var aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);
            FailIfPatternLegOnGround(aircraft, t);

            switch (aircraft.Phases?.CurrentPhase)
            {
                case CrossingRunwayPhase crossing:
                    output.WriteLine($"t=+{t}: crossing runway");
                    return crossing;
                case HoldingShortPhase { HoldShort.Reason: HoldShortReason.RunwayCrossing }:
                    var cross = engine.SendCommand(Callsign, "CROSS");
                    Assert.True(cross.Success, cross.Message);
                    break;
            }
        }

        var last = engine.FindAircraft(Callsign);
        output.WriteLine($"gave up after {maxSeconds}s: phase={last?.Phases?.CurrentPhase?.Name} gs={last?.GroundSpeed:F1}");
        return null;
    }

    private static AircraftState SpawnOnTaxiwayB(SimulationEngine engine, Data.Airport.AirportGroundLayout layout)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "C172",
            Position = new LatLon(SpawnLat, SpawnLon),
            TrueHeading = new TrueHeading(SpawnHeadingTrue),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(4500),
            },
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-mlt-during-taxi",
            ScenarioName = "MLT during taxi keeps ground route",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            AutoCrossRunway = false,
        };
        return aircraft;
    }

    private static void FailIfPatternLegOnGround(AircraftState aircraft, int t)
    {
        if (aircraft.IsOnGround && aircraft.Phases?.CurrentPhase is UpwindPhase or CrosswindPhase or DownwindPhase or BasePhase)
        {
            Assert.Fail(
                $"t=+{t}: pattern leg '{aircraft.Phases.CurrentPhase.Name}' became the current phase while the aircraft "
                    + $"is still on the ground (ias={aircraft.IndicatedAirspeed:F1}) — the MLT spliced airborne pattern "
                    + "phases into a taxiing aircraft's chain"
            );
        }
    }
}
