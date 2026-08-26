using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A <c>CROSS</c> clearance authorises one runway crossing, not a stop on the far side: after
/// crossing, the aircraft keeps taxiing its assigned route until the next binding hold-short.
///
/// OAK taxiway B crosses 28R/10L and then 28L/10R on its way to runway 30, so a
/// <c>TAXI C B W 30</c> route holds short of 28R, crosses on clearance, and must run on to the
/// 28L bar rather than stopping just past 28R's far-side bar.
/// </summary>
public class OakCrossThenContinueTests(ITestOutputHelper output)
{
    private const string Callsign = "N346G";
    private const string AircraftType = "C560";
    private const string AirportId = "OAK";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("TaxiingPhase", LogLevel.Debug).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    [Fact]
    public void CrossAt28R_ContinuesToThe28LHoldShort()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout(AirportId);
        if (layout is null)
        {
            return;
        }

        var bar28R = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28R", "B");
        var bar28L = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28L", "B");
        Assert.Equal(2, bar28R.Count);
        Assert.Equal(2, bar28L.Count);

        // Approach 28R from the side away from 28L, so the crossing runs 28R first and 28L second.
        var nearBar28R = bar28R.OrderByDescending(node => GeoMath.DistanceNm(node.Position, bar28L[0].Position)).First();
        var start = nearBar28R
            .Edges.Where(edge => string.Equals(edge.TaxiwayName, "B", StringComparison.OrdinalIgnoreCase))
            .Select(edge => edge.OtherNode(nearBar28R))
            .OrderByDescending(node => GeoMath.DistanceNm(node.Position, bar28L[0].Position))
            .First();

        var aircraft = Spawn(start, new TrueHeading(GeoMath.BearingTo(start.Position, nearBar28R.Position)), layout);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-oak-cross-then-continue",
            ScenarioName = "OAK cross then continue",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = AirportId,
            AutoCrossRunway = false,
        };

        var taxi = engine.SendCommand(Callsign, "TAXI B W 30");
        Assert.True(taxi.Success, taxi.Message);

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"route={route.ToSummary()} ({route.Segments.Count} segments)");
        foreach (var point in route.HoldShortPoints)
        {
            output.WriteLine($"  hold-short #{point.NodeId} {point.TargetName} {point.Reason}");
        }

        var holdAt28R = TickUntilHoldingShort(engine, "28R", 300);
        Assert.NotNull(holdAt28R);
        Assert.Contains(bar28R, node => node.Id == holdAt28R.HoldShort.NodeId);

        var cross = engine.SendCommand(Callsign, "CROSS");
        Assert.True(cross.Success, cross.Message);

        var holdAt28L = TickUntilHoldingShort(engine, "28L", 300);
        Assert.NotNull(holdAt28L);
        Assert.Contains(bar28L, node => node.Id == holdAt28L.HoldShort.NodeId);
    }

    private HoldingShortPhase? TickUntilHoldingShort(SimulationEngine engine, string runwayDesignator, int maxSeconds)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            var aircraft = engine.FindAircraft(Callsign);
            if (aircraft is null)
            {
                return null;
            }

            if (t % 20 == 0)
            {
                output.WriteLine(
                    $"t={t}: phase={aircraft.Phases?.CurrentPhase?.GetType().Name} gs={aircraft.GroundSpeed:F1}"
                        + $" twy={aircraft.Ground.CurrentTaxiway ?? "(none)"} seg={aircraft.Ground.AssignedTaxiRoute?.CurrentSegmentIndex}"
                );
            }

            if (
                aircraft.Phases?.CurrentPhase is HoldingShortPhase hold
                && hold.HoldShort.TargetName is { } target
                && RunwayIdentifier.Parse(target).Contains(runwayDesignator)
            )
            {
                output.WriteLine($"t={t}: holding short of {target} at node #{hold.HoldShort.NodeId}");
                return hold;
            }
        }

        var last = engine.FindAircraft(Callsign);
        output.WriteLine(
            $"gave up after {maxSeconds}s: phase={last?.Phases?.CurrentPhase?.GetType().Name} gs={last?.GroundSpeed:F1}"
                + $" seg={last?.Ground.AssignedTaxiRoute?.CurrentSegmentIndex}/{last?.Ground.AssignedTaxiRoute?.Segments.Count}"
        );
        return null;
    }

    private static AircraftState Spawn(GroundNode node, TrueHeading heading, AirportGroundLayout layout)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = node.Position,
            TrueHeading = heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = AirportId,
                Destination = AirportId,
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(1500),
            },
        };

        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}
