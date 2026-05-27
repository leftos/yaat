using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #163: bare <c>CROSS</c> command. E2E coverage for the controller workflow
/// from the Discord thread — spawn a Cessna Citation Longitude (C700) at JSX1
/// (OAK north field), give it <c>RWY 30 TAXI C B W</c> so the route crosses
/// runway 28R/10L on the way south to runway 30, wait for HoldingShortPhase,
/// then issue <c>CROSS; HOLD</c>. Asserts:
///
/// 1. Bare <c>CROSS</c> satisfies the current 28R hold-short clearance without
///    naming the runway, and
/// 2. The chained <c>HOLD</c> fires only after the aircraft has fully exited
///    <see cref="CrossingRunwayPhase"/> (i.e. past the far-side hold bars on
///    taxiway B), not while still holding short of 28R, not mid-crossing, and
///    not after rolling onward to the next runway hold-short.
/// </summary>
public class Issue163BareCrossThenHoldTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        SimLogBuilder.CreateForTest(output).EnableCategory("GroundCommandHandler", LogLevel.Information).InitializeSimLog();
        return new SimulationEngine(groundData);
    }

    private static GroundNode? FindParking(AirportGroundLayout layout, string name) =>
        layout.Nodes.Values.FirstOrDefault(n =>
            (n.Type == GroundNodeType.Parking || n.Type == GroundNodeType.Spot) && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)
        );

    private static AircraftState SpawnC700AtJsx1(AirportGroundLayout layout, GroundNode parking)
    {
        var aircraft = new AircraftState
        {
            Callsign = "JSX163",
            AircraftType = "C700",
            Position = parking.Position,
            TrueHeading = parking.TrueHeading ?? new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
                CruiseAltitude = 1500,
            },
        };
        aircraft.Phases = new PhaseList();
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }

    private static bool TickUntil(SimulationEngine engine, AircraftState aircraft, int maxSeconds, Func<AircraftState, bool> condition)
    {
        for (int t = 1; t <= maxSeconds; t++)
        {
            engine.TickOneSecond();
            if (condition(aircraft))
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void BareCrossThenHold_CrossesRunway28RAndHaltsPastFarSideHoldBars()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var layout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(layout);

        var parking = FindParking(layout, "JSX1");
        Assert.NotNull(parking);

        var aircraft = SpawnC700AtJsx1(layout, parking);
        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "issue-163-bare-cross",
            ScenarioName = "Issue 163 bare CROSS; HOLD",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "OAK",
            // AutoCross OFF so 28R/10L remains an uncleared hold-short the
            // controller must clear with bare CROSS.
            AutoCrossRunway = false,
        };

        var taxiResult = engine.SendCommand("JSX163", "RWY 30 TAXI C B W");
        Assert.True(taxiResult.Success, $"RWY 30 TAXI C B W failed: {taxiResult.Message}");

        var route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        output.WriteLine($"Route: {route.ToSummary()}  ({route.Segments.Count} segments)");

        var crossing28R = route.HoldShortPoints.FirstOrDefault(h =>
            h.Reason == HoldShortReason.RunwayCrossing && h.TargetName is not null && h.TargetName.Contains("28R", StringComparison.OrdinalIgnoreCase)
        );
        Assert.NotNull(crossing28R);

        // Tick until aircraft enters HoldingShortPhase of 28R.
        bool reachedHs = TickUntil(
            engine,
            aircraft,
            maxSeconds: 600,
            ac =>
                ac.Phases?.CurrentPhase is HoldingShortPhase hp
                && hp.HoldShort.TargetName is not null
                && hp.HoldShort.TargetName.Contains("28R", StringComparison.OrdinalIgnoreCase)
        );
        Assert.True(
            reachedHs,
            $"JSX163 should reach HoldingShortPhase of 28R within 600s; "
                + $"final phase={aircraft.Phases?.CurrentPhase?.GetType().Name ?? "null"} pos=({aircraft.Position.Lat:F6},{aircraft.Position.Lon:F6})"
        );

        var holdShortPos = aircraft.Position;
        output.WriteLine($"Holding short of 28R at pos=({holdShortPos.Lat:F6},{holdShortPos.Lon:F6}) twy={aircraft.Ground.CurrentTaxiway ?? "?"}");

        // Send CROSS; HOLD. Bare CROSS satisfies the 28R hold-short clearance;
        // HOLD is queued with an AfterRunwayCrossing trigger and must fire only
        // after CrossingRunwayPhase completes.
        var crossResult = engine.SendCommand("JSX163", "CROSS; HOLD");
        Assert.True(crossResult.Success, $"CROSS; HOLD failed: {crossResult.Message}");
        output.WriteLine($"CROSS; HOLD dispatched: {crossResult.Message}");

        // Track the aircraft's progress through the crossing. We want to observe:
        //   (a) it enters CrossingRunwayPhase,
        //   (b) it exits CrossingRunwayPhase,
        //   (c) at that exit, the HOLD trigger fires and Hold = HoldPosition,
        //   (d) the aircraft halts past the far-side hold bars (lat < holdShortPos).
        bool sawCrossing = false;
        bool sawHoldApplied = false;
        for (int t = 1; t <= 120; t++)
        {
            engine.TickOneSecond();

            if (aircraft.Phases?.CurrentPhase is CrossingRunwayPhase)
            {
                sawCrossing = true;
            }

            if (sawCrossing && aircraft.Phases?.CurrentPhase is not CrossingRunwayPhase && aircraft.Ground.Hold == HoldDirective.HoldPosition)
            {
                sawHoldApplied = true;
                break;
            }
        }

        var finalPhase = aircraft.Phases?.CurrentPhase?.GetType().Name ?? "null";
        var hold = aircraft.Ground.Hold;
        output.WriteLine(
            $"Final: phase={finalPhase} hold={hold?.Kind.ToString() ?? "null"} gs={aircraft.GroundSpeed:F1} "
                + $"ias={aircraft.IndicatedAirspeed:F1} pos=({aircraft.Position.Lat:F6},{aircraft.Position.Lon:F6}) twy={aircraft.Ground.CurrentTaxiway ?? "?"}"
        );

        Assert.True(sawCrossing, "Aircraft should pass through CrossingRunwayPhase after bare CROSS clears the 28R hold-short");
        Assert.True(sawHoldApplied, "HOLD should fire once CrossingRunwayPhase has completed; Hold directive should be HoldPosition");

        // The 28R hold-short must be marked cleared.
        Assert.True(crossing28R.IsCleared, "Bare CROSS should mark the 28R/10L hold-short as cleared");

        // Aircraft must be south of the original holding-short position — i.e.
        // it actually rolled across the runway, not stopped pre-crossing.
        Assert.True(
            aircraft.Position.Lat < holdShortPos.Lat - 0.0001,
            $"Aircraft should be south of pre-crossing position {holdShortPos.Lat:F6}; was {aircraft.Position.Lat:F6}"
        );

        // Aircraft must actually halt within a few seconds of HOLD taking effect.
        bool halted = TickUntil(engine, aircraft, maxSeconds: 30, ac => ac.IndicatedAirspeed < 0.5);
        Assert.True(halted, $"Aircraft should decelerate to a stop within 30s of HOLD being set; final ias={aircraft.IndicatedAirspeed:F1}");
        Assert.Equal(HoldDirective.HoldPosition, aircraft.Ground.Hold);

        // Final sanity: aircraft did NOT roll onward to HoldingShortPhase of the next
        // parallel runway (28L/10R) — HOLD must have caught it on the taxiway between
        // the two runways.
        Assert.IsNotType<HoldingShortPhase>(aircraft.Phases?.CurrentPhase);
    }
}
