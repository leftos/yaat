using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-1438: an aircraft taxiing onto a runway hold-short bar it is already cleared across arrives at the bar itself, not up
/// to the loose node-arrival threshold (~91 ft) short of it, before it hands off to <see cref="CrossingRunwayPhase"/>.
///
/// <para>OAK, taxiway G northbound: the aircraft starts at 28L's north bar, heading 22.1° true, and is cleared
/// <c>TAXI G C CROSS 28R</c>. 28R's south bar on G lies ahead on a straight approach, with straight road after it: no stop,
/// short edge, arc or corner tightens the arrival there but the hold-short (<see cref="BarsOnG"/>).</para>
/// </summary>
[Collection("Acceptance")]
public class ClearedRunwayBarArrivalTests(ITestOutputHelper output)
{
    private const string Callsign = "N1438";
    private const string AirportId = "OAK";
    private const string TaxiClearance = "TAXI G C CROSS 28R";

    private const double NorthboundOnGDeg = 22.1;

    /// <summary>How far (deg) <see cref="NorthboundOnGDeg"/> may differ from the true bearing from the start bar to the crossing bar.</summary>
    private const double NorthboundToleranceDeg = 3.0;

    /// <summary>How far short of the bar, along G, the hand-off may still happen.</summary>
    private const double ArrivalToleranceFt = 2.0;

    private const double MovingKts = 1.0;
    private const int HandOffBudgetSeconds = 300;
    private const int SnapshotAfterMovingSeconds = 2;

    [Theory]
    [InlineData("C56X")]
    [InlineData("C172")]
    public void PreClearedCrossing_HandsOffAtTheBar(string aircraftType)
    {
        if (Stage(aircraftType) is not { } staged)
        {
            return;
        }

        AssertHandsOffAtTheBar(TickToHandOff(staged.Engine, staged.Bars, moving: false), staged.Bars);
    }

    [Theory]
    [InlineData("C56X")]
    [InlineData("C172")]
    public void PreClearedCrossing_RestoredWhileTaxiing_HandsOffAtTheBar(string aircraftType)
    {
        if (Stage(aircraftType) is not { } staged)
        {
            return;
        }

        AircraftState aircraft = Assert.IsType<AircraftState>(staged.Engine.FindAircraft(Callsign));
        Assert.True(TickUntilMovingFor(staged.Engine, aircraft, SnapshotAfterMovingSeconds), "never started moving");
        Assert.IsType<TaxiingPhase>(aircraft.Phases?.CurrentPhase);
        output.WriteLine($"snapshot: gs={aircraft.GroundSpeed:F1} along G from the bar {SignedAlongGFt(staged.Bars, aircraft.Position):F1} ft");

        SimulationEngine restored = NewEngine(staged.GroundData);
        restored.RestoreFromSnapshot(RoundTrip(staged.Engine.CaptureSnapshot()));

        AssertHandsOffAtTheBar(TickToHandOff(restored, staged.Bars, moving: true), staged.Bars);
    }

    private static void AssertHandsOffAtTheBar(HandOff? handOff, GBars bars)
    {
        HandOff atHandOff = Assert.IsType<HandOff>(handOff);
        Assert.False(atHandOff.HeldAtBar, $"a HoldingShortPhase was inserted at the cleared bar #{bars.Crossing.Id}");
        Assert.False(atHandOff.Stopped, "the aircraft stopped before it handed off to the crossing");
        Assert.True(
            atHandOff.SignedAlongFt >= -ArrivalToleranceFt,
            $"handed off to the crossing {-atHandOff.SignedAlongFt:F1} ft short of the bar at t={atHandOff.Second}"
        );
    }

    private sealed record Staged(SimulationEngine Engine, GBars Bars, TestAirportGroundData GroundData);

    /// <summary>The two bars on G: 28L's north bar, where the aircraft starts, and 28R's south bar, the cleared crossing's.</summary>
    private sealed record GBars(GroundNode Start, GroundNode Crossing);

    /// <summary>
    /// OAK's bars on G by meaning: the crossing bar is 28R's bar on G nearest 28L, the start bar is 28L's bar on G nearest
    /// that crossing bar (28L's north bar).
    /// </summary>
    private static GBars BarsOnG(AirportGroundLayout layout)
    {
        GroundNode any28LBar = TestLayoutNodes.RunwayHoldShortsOnTaxiway(layout, "28L", "G")[0];
        GroundNode crossing = GroundNodeLookup.HoldShortBar(layout, "28R", "G", any28LBar);
        GroundNode start = GroundNodeLookup.HoldShortBar(layout, "28L", "G", crossing);
        return new GBars(start, crossing);
    }

    /// <summary>The hand-off to the crossing: when, where along G from the bar (positive past it), and what led up to it.</summary>
    private sealed record HandOff(int Second, double SignedAlongFt, bool Stopped, bool HeldAtBar);

    /// <summary>An engine whose aircraft is cleared across 28R on G with the bar pre-cleared; null when navdata or the layout is missing.</summary>
    private Staged? Stage(string aircraftType)
    {
        var groundData = new TestAirportGroundData();
        TestVnasData.EnsureInitialized();
        if ((TestVnasData.NavigationDb is null) || (groundData.GetLayout(AirportId) is not { } layout))
        {
            output.WriteLine("SKIP: navdata or KOAK layout unavailable");
            return null;
        }

        GBars bars = BarsOnG(layout);
        output.WriteLine(
            $"start bar #{bars.Start.Id}, crossing bar #{bars.Crossing.Id}: "
                + $"{GeoMath.DistanceNm(bars.Start.Position, bars.Crossing.Position) * GeoMath.FeetPerNm:F1} ft apart, "
                + $"bearing {GeoMath.BearingTo(bars.Start.Position, bars.Crossing.Position):F1}° true"
        );
        SimulationEngine engine = NewEngine(groundData);
        engine.World.AddAircraft(Spawn(layout, bars, aircraftType));
        CommandResult taxi = engine.SendCommand(Callsign, TaxiClearance);
        output.WriteLine($"taxi: {taxi.Success} — {taxi.Message}");
        Assert.True(taxi.Success, taxi.Message);

        AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        string holdShorts = string.Join(", ", route.HoldShortPoints.Select(h => $"#{h.NodeId} {h.TargetName} {h.Reason} cleared={h.IsCleared}"));
        output.WriteLine($"route: {route.FormatTaxiwaySequence()}; hold shorts: {holdShorts}");
        Assert.True(route.GetHoldShortAt(bars.Crossing.Id) is { IsCleared: true }, $"no cleared hold short at #{bars.Crossing.Id}: {holdShorts}");
        return new Staged(engine, bars, groundData);
    }

    private static AircraftState Spawn(AirportGroundLayout layout, GBars bars, string aircraftType)
    {
        GroundNode startBar = bars.Start;
        Assert.Equal(GroundNodeType.RunwayHoldShort, startBar.Type);
        double towardCrossingDeg = GeoMath.BearingTo(startBar.Position, bars.Crossing.Position);
        Assert.True(
            GeoMath.AbsBearingDifference(NorthboundOnGDeg, towardCrossingDeg) <= NorthboundToleranceDeg,
            $"start bar #{startBar.Id} to crossing bar #{bars.Crossing.Id} bears {towardCrossingDeg:F1}° true, "
                + $"not northbound on G ({NorthboundOnGDeg}°)"
        );
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = aircraftType,
            // On 28L's north bar node on purpose: an off-node start currently skips the crossing hand-off (booked separately).
            Position = startBar.Position,
            TrueHeading = new TrueHeading(NorthboundOnGDeg),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Destination = AirportId },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingInPositionPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        aircraft.Ground.CurrentTaxiway = "G";
        return aircraft;
    }

    /// <summary>Ticks until the aircraft has been moving for <paramref name="seconds"/>; false when it never moves within the budget.</summary>
    private static bool TickUntilMovingFor(SimulationEngine engine, AircraftState aircraft, int seconds)
    {
        int movingSeconds = -1;
        for (int second = 1; second <= HandOffBudgetSeconds; second++)
        {
            engine.TickOneSecond();
            if ((movingSeconds < 0) && (aircraft.GroundSpeed >= MovingKts))
            {
                movingSeconds = 0;
            }
            else if (movingSeconds >= 0)
            {
                movingSeconds++;
            }

            if (movingSeconds >= seconds)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ticks until the aircraft's current phase is the crossing, sampling after every physics sub-tick so the hand-off
    /// position is the one at the sub-tick the crossing took over; null when it never gets there within the budget.
    /// </summary>
    private HandOff? TickToHandOff(SimulationEngine engine, GBars bars, bool moving)
    {
        bool stopped = false;
        bool heldAtBar = false;
        HandOff? handOff = null;
        string? lastPhase = null;
        for (int second = 1; (second <= HandOffBudgetSeconds) && (handOff is null); second++)
        {
            int atSecond = second;
            TickOneSecondSampling(
                engine,
                subTick =>
                {
                    if (handOff is not null)
                    {
                        return;
                    }

                    AircraftState aircraft = Assert.IsType<AircraftState>(engine.FindAircraft(Callsign));
                    if (aircraft.Phases?.CurrentPhase?.Name != lastPhase)
                    {
                        lastPhase = aircraft.Phases?.CurrentPhase?.Name;
                        output.WriteLine(
                            $"t={atSecond}.{subTick}: phase {lastPhase} at {SignedAlongGFt(bars, aircraft.Position):F1} ft along G, "
                                + $"gs={aircraft.GroundSpeed:F1}"
                        );
                    }

                    heldAtBar |=
                        aircraft.Phases?.Phases.Any(p => p is HoldingShortPhase { HoldShort.NodeId: var nodeId } && (nodeId == bars.Crossing.Id))
                        == true;
                    if (moving && (aircraft.GroundSpeed < MovingKts))
                    {
                        stopped = true;
                        output.WriteLine($"t={atSecond}.{subTick}: slowed to {aircraft.GroundSpeed:F2} kt in {aircraft.Phases?.CurrentPhase?.Name}");
                    }

                    moving |= aircraft.GroundSpeed >= MovingKts;
                    if (aircraft.Phases?.CurrentPhase is CrossingRunwayPhase)
                    {
                        double alongFt = SignedAlongGFt(bars, aircraft.Position);
                        output.WriteLine(
                            $"t={atSecond}.{subTick}: handed off to the crossing {alongFt:F1} ft along G from the bar, gs={aircraft.GroundSpeed:F1}"
                        );
                        handOff = new HandOff(atSecond, alongFt, stopped, heldAtBar);
                    }
                }
            );
        }

        if (handOff is null)
        {
            output.WriteLine($"never handed off to the crossing within {HandOffBudgetSeconds}s");
        }

        return handOff;
    }

    /// <summary>
    /// One sim-second through the same spine <see cref="SimulationEngine.TickOneSecond"/> runs, calling
    /// <paramref name="afterSubTick"/> with the sub-tick's index after each physics sub-tick.
    /// </summary>
    private static void TickOneSecondSampling(SimulationEngine engine, Action<int> afterSubTick)
    {
        engine.BeginSecond();
        engine.OpenSecond(engine.BareHost);
        engine.RunPrePhysics(engine.BareHost);
        double subDelta = 1.0 / SimulationEngine.PhysicsSubTickRate;
        for (int subTick = 0; subTick < SimulationEngine.PhysicsSubTickRate; subTick++)
        {
            engine.RunPhysicsSubTick(subDelta, subTick);
            afterSubTick(subTick);
        }

        engine.RunPostPhysics(engine.BareHost);
        engine.RunEndOfSecond(engine.BareHost);
    }

    /// <summary>The position's distance from the crossing bar along G's northbound bearing: negative short of it, positive past it.</summary>
    private static double SignedAlongGFt(GBars bars, LatLon position)
    {
        GroundNode start = bars.Start;
        GroundNode bar = bars.Crossing;
        double distNm = GeoMath.DistanceNm(bar.Position, position);
        if (distNm < 1e-9)
        {
            return 0.0;
        }

        double offDeg = GeoMath.AbsBearingDifference(GeoMath.BearingTo(start.Position, bar.Position), GeoMath.BearingTo(bar.Position, position));
        return distNm * Math.Cos(offDeg * Math.PI / 180.0) * GeoMath.FeetPerNm;
    }

    private static StateSnapshotDto RoundTrip(StateSnapshotDto snapshot) =>
        Assert.IsType<StateSnapshotDto>(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(snapshot, RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )
        );

    private SimulationEngine NewEngine(TestAirportGroundData groundData)
    {
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-yaat-1438-cleared-bar-arrival",
                ScenarioName = "OAK G across 28R, pre-cleared",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = AirportId,
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
    }
}
