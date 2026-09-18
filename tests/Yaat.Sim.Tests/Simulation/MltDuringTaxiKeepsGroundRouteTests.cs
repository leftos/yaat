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
///
/// Fixture timing — read before re-timing this pair again (it has now been re-timed three times).
/// The subject of both tests is what MLT / CTO do to an aircraft in <see cref="CrossingRunwayPhase"/>;
/// the crossing is only the setup, and the setup has one window. The re-taxi to 28L must land while
/// the aircraft is still under <see cref="TaxiingPhase"/> and still short of the 28R holding-position
/// node: <c>GroundCommandHandler.TryTaxi</c> implicitly clears the new route's first crossing of the
/// runway the aircraft is <em>already holding short of</em>, so a re-taxi issued at the bar produces a
/// pre-cleared 28R crossing that is driven across inside <see cref="TaxiingPhase"/> — no
/// <see cref="CrossingRunwayPhase"/> is ever raised and there is nothing for MLT / CTO to hit. Issued
/// mid-taxi the crossing stays uncleared, the aircraft stops at the bar, and the fixture's own CROSS
/// raises the real crossing phase. Waiting for the aircraft to stop at the bar first is not an option
/// (it is the very state that pre-clears the crossing), and the seconds-count that used to bound this
/// window is not one either — the taxi acceleration constants move. Hence the physical trigger in
/// <see cref="TickUntilTaxiingShortOfBar"/> and the precondition assert in
/// <see cref="AssertCrossingOf28RIsUncleared"/>.
/// </summary>
public class MltDuringTaxiKeepsGroundRouteTests(ITestOutputHelper output)
{
    private const string Callsign = "N436MS";

    // N436MS's spawn-snapped position from the live session log: on taxiway B just north of the
    // 28R/10L crossing, nosed south-southeast along B (heading 162 true).
    private const double SpawnLat = 37.725690;
    private const double SpawnLon = -122.204525;
    private const double SpawnHeadingTrue = 162.0;

    /// <summary>
    /// How far short of the 28R holding-position node the aircraft must still be when the re-taxi to
    /// 28L is issued. The spawn sits 98 ft north of that bar (node #507) on B — the whole window this
    /// fixture has — so 50 ft is its usable half, measured against the aircraft's position on the tick
    /// the re-taxi goes out, never counted in seconds.
    ///
    /// 50 ft is chosen to be unreachable by any plausible acceleration change: the re-taxi goes out on
    /// the first tick under <see cref="TaxiingPhase"/>, which is the furthest point of the window (the
    /// distance only shrinks after it, and the aircraft is doing 1 kt there), so covering the 48 ft of
    /// margin inside that single second would take a breakaway to ~30 kt — against a C172 taxi
    /// acceleration of 1.0 kt/s, which moves it about 2 ft. It is also clear of everything that snaps
    /// at a bar: <see cref="TaxiingPhase"/>'s start-node hold stops 15 ft short of the node and only
    /// captures below 3 kt, and the route resolver still has intermediate B nodes ahead of the aircraft
    /// to start the rebuilt route from rather than the bar itself. The Piston/Helicopter taxi
    /// acceleration going 0.6 → 1.0 kt/s is what invalidated the previous timing.
    /// </summary>
    private const double ReTaxiMinShortOfBarFt = 50.0;

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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        AircraftState? aircraft = SpawnOnTaxiwayB(engine, layout);

        // Preset leg: taxi to the assigned runway 28R, the bar at B where DN's aircraft started.
        CommandResult taxi28R = engine.SendCommand(Callsign, "TAXI B 28R");
        Assert.True(taxi28R.Success, taxi28R.Message);
        LatLon bar28R = Bar28RPosition(aircraft, layout);

        // The controller's re-taxi to the parallel: crosses 28R/10L, destination hold-short 28L.
        // Issued while the aircraft is still rolling down B toward the 28R bar — see the class remarks
        // for why the crossing this pair tests only exists when the re-taxi lands inside that window.
        double? shortOfBarFt = TickUntilTaxiingShortOfBar(engine, bar28R, 60);
        Assert.NotNull(shortOfBarFt);
        CommandResult taxi28L = engine.SendCommand(Callsign, "TAXI B 28L");
        Assert.True(taxi28L.Success, taxi28L.Message);
        AssertCrossingOf28RIsUncleared(aircraft);

        // Reach the crossing state (clearing the 28R crossing bar if the route paused there).
        CrossingRunwayPhase? crossing = TickUntilCrossingRunway(engine, 120);
        Assert.NotNull(crossing);

        // Standalone MLT mid-crossing: must only pre-arm the pattern direction for the eventual
        // departure — never splice airborne pattern phases into a ground aircraft's chain.
        CommandResult mlt = engine.SendCommand(Callsign, "MLT");
        Assert.True(mlt.Success, mlt.Message);
        aircraft = engine.FindAircraft(Callsign);
        Assert.NotNull(aircraft);
        Assert.Equal(PatternDirection.Left, aircraft.Pattern.TrafficDirection);

        // The aircraft must finish the crossing and taxi to the 28L bar ON THE GROUND ROUTE.
        // With the bug, the spliced circuit makes Upwind the next phase after the crossing and the
        // aircraft accelerates down the pavement without ever rotating.
        HoldingShortPhase? at28LBar = TickUntilHoldingShort(engine, "28L", HoldShortReason.DestinationRunway, 300);
        Assert.NotNull(at28LBar);

        // Normal departure from the re-taxied runway.
        CommandResult cto = engine.SendCommand(Callsign, "CTO MLT");
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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);
        AircraftState departure = SpawnOnTaxiwayB(engine, layout);

        CommandResult taxi28R = engine.SendCommand(Callsign, "TAXI B 28R");
        Assert.True(taxi28R.Success, taxi28R.Message);
        LatLon bar28R = Bar28RPosition(departure, layout);

        // Mid-taxi re-taxi, short of the 28R bar — the only window that leaves the 28R crossing
        // uncleared and so raises a real CrossingRunwayPhase to issue the CTO into. See the class remarks.
        Assert.NotNull(TickUntilTaxiingShortOfBar(engine, bar28R, 60));
        CommandResult taxi28L = engine.SendCommand(Callsign, "TAXI B 28L");
        Assert.True(taxi28L.Success, taxi28L.Message);
        AssertCrossingOf28RIsUncleared(departure);
        Assert.NotNull(TickUntilCrossingRunway(engine, 120));

        CommandResult cto = engine.SendCommand(Callsign, "CTO MLT");
        Assert.True(cto.Success, cto.Message);

        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            AircraftState? aircraft = engine.FindAircraft(Callsign);
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

    /// <summary>
    /// The 28R holding-position node on the route the preset <c>TAXI B 28R</c> just built — its
    /// destination bar, and the node the re-taxi's 28R crossing hold-short then sits on. Read from the
    /// layout so the standoff below is measured against real graph geometry.
    /// </summary>
    private static LatLon Bar28RPosition(AircraftState aircraft, Yaat.Sim.Data.Airport.AirportGroundLayout layout)
    {
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        HoldShortPoint? bar = route.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.DestinationRunway);
        Assert.NotNull(bar);
        GroundNode? node = layout.Nodes.GetValueOrDefault(bar.NodeId);
        Assert.NotNull(node);
        return node.Position;
    }

    /// <summary>
    /// Tick until the aircraft is under <see cref="TaxiingPhase"/>, then assert it is still more than
    /// <see cref="ReTaxiMinShortOfBarFt"/> from <paramref name="barPosition"/> and return that standoff
    /// — the caller issues the re-taxi on that tick. The first taxiing tick is deliberately the trigger:
    /// the distance to the bar only shrinks afterwards, so it is the widest margin the window offers.
    /// Returns null when the aircraft never starts taxiing at all.
    /// </summary>
    private double? TickUntilTaxiingShortOfBar(SimulationEngine engine, LatLon barPosition, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            AircraftState? aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);
            FailIfPatternLegOnGround(aircraft, t);
            if (aircraft.Phases?.CurrentPhase is not TaxiingPhase)
            {
                continue;
            }

            double distFt = GeoMath.DistanceNm(aircraft.Position, barPosition) * GeoMath.FeetPerNm;
            output.WriteLine($"t=+{t}: taxiing, {distFt:F0} ft short of the 28R bar (gs={aircraft.GroundSpeed:F1})");
            Assert.True(
                distFt > ReTaxiMinShortOfBarFt,
                $"the first taxiing tick is only {distFt:F0} ft short of the 28R bar (need more than {ReTaxiMinShortOfBarFt:F0} ft): "
                    + "the re-taxi window this fixture sets the crossing up in is gone — see the class remarks"
            );
            return distFt;
        }

        AircraftState? last = engine.FindAircraft(Callsign);
        output.WriteLine($"never started taxiing within {maxSeconds}s: phase={last?.Phases?.CurrentPhase?.Name} gs={last?.GroundSpeed:F1}");
        return null;
    }

    /// <summary>
    /// The precondition every assertion after the re-taxi depends on: the new route's first runway
    /// crossing is 28R and is NOT cleared, so the aircraft stops at the bar and the fixture's CROSS
    /// raises a real <see cref="CrossingRunwayPhase"/>. A pre-cleared crossing is driven across inside
    /// <see cref="TaxiingPhase"/> and would leave both tests passing while covering nothing.
    /// </summary>
    private static void AssertCrossingOf28RIsUncleared(AircraftState aircraft)
    {
        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        HoldShortPoint? firstCrossing = route.HoldShortPoints.FirstOrDefault(h => h.Reason == HoldShortReason.RunwayCrossing);
        Assert.NotNull(firstCrossing);
        Assert.True(
            firstCrossing.TargetName is { } target && RunwayIdentifier.Parse(target).Contains("28R"),
            $"the re-taxi's first runway crossing is {firstCrossing.TargetName}, expected 28R"
        );
        Assert.False(
            firstCrossing.IsCleared,
            "the re-taxi's 28R crossing came back pre-cleared: the command landed outside the mid-taxi window, "
                + "so no CrossingRunwayPhase will be raised and the MLT/CTO case below is not being tested"
        );
    }

    private HoldingShortPhase? TickUntilHoldingShort(SimulationEngine engine, string runwayDesignator, HoldShortReason reason, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            AircraftState? aircraft = engine.FindAircraft(Callsign);
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

        AircraftState? last = engine.FindAircraft(Callsign);
        output.WriteLine($"gave up after {maxSeconds}s: phase={last?.Phases?.CurrentPhase?.Name} gs={last?.GroundSpeed:F1}");
        return null;
    }

    private CrossingRunwayPhase? TickUntilCrossingRunway(SimulationEngine engine, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            AircraftState? aircraft = engine.FindAircraft(Callsign);
            Assert.NotNull(aircraft);
            FailIfPatternLegOnGround(aircraft, t);

            switch (aircraft.Phases?.CurrentPhase)
            {
                case CrossingRunwayPhase crossing:
                    output.WriteLine($"t=+{t}: crossing runway");
                    return crossing;
                case HoldingShortPhase { HoldShort.Reason: HoldShortReason.RunwayCrossing }:
                    CommandResult cross = engine.SendCommand(Callsign, "CROSS");
                    Assert.True(cross.Success, cross.Message);
                    break;
            }
        }

        AircraftState? last = engine.FindAircraft(Callsign);
        output.WriteLine($"gave up after {maxSeconds}s: phase={last?.Phases?.CurrentPhase?.Name} gs={last?.GroundSpeed:F1}");
        return null;
    }

    private static AircraftState SpawnOnTaxiwayB(SimulationEngine engine, Yaat.Sim.Data.Airport.AirportGroundLayout layout)
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
