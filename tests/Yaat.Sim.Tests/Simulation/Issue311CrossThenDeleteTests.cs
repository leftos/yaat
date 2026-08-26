using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// E2E tests for GitHub issue #311: <c>CROSS &lt;rwy&gt;; DEL</c> — cross the runway the aircraft is
/// short of, then remove it from the scope, without the controller having to invent a taxi route
/// just to get the aircraft across.
///
/// Scenario: a B738 lands SFO 19L and exits at G. With <c>AutoPullUpToParallel</c> on (the default,
/// issue #175) it pulls up between the parallels and stops short of 19R. <c>ONHS DEL</c> is no help
/// here — it fires on <see cref="HoldingAfterExitPhase"/>, which is *before* the parallel crossing.
///
/// The chained <c>DEL</c> must fire only once <see cref="CrossingRunwayPhase"/> has run and
/// completed, both when the command is issued at the hold-short and when it is issued earlier as a
/// pre-clear while the aircraft is still taxiing toward the hold-short. <c>NODEL</c> must be able to
/// cancel the queued delete in either case.
/// </summary>
public class Issue311CrossThenDeleteTests(ITestOutputHelper output)
{
    private const string Callsign = "TST738";

    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        return new SimulationEngine(new TestAirportGroundData());
    }

    /// <summary>
    /// Spawns a B738 on 1 nm final to SFO 19L, drives it through landing and the taxiway G exit, and
    /// returns the live engine + aircraft with the auto-pull-up toward 19R armed. Returns null
    /// (silent skip) when navdata or the SFO layout is unavailable.
    /// </summary>
    private (SimulationEngine Engine, AircraftState Aircraft)? SetupLandingAtSfo19L()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return null;
        }

        var runway = NavigationDatabase.Instance.GetRunway("SFO", "19L");
        var layout = new TestAirportGroundData().GetLayout("SFO");
        if (runway is null || layout is null)
        {
            return null;
        }

        double reciprocal = (runway.TrueHeading.Degrees + 180) % 360;
        var (acLat, acLon) = GeoMath.ProjectPointRaw(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 1.0);

        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = new LatLon(acLat, acLon),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt + 318,
            IndicatedAirspeed = 145,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "SFO",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(3000),
            },
        };

        aircraft.Phases = new PhaseList { AssignedRunway = runway };
        aircraft.Phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        aircraft.Phases.Add(new LandingPhase());
        aircraft.Phases.Add(new RunwayExitPhase());
        aircraft.Phases.Add(new HoldingAfterExitPhase());
        aircraft.Ground.Layout = layout;

        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));

        engine.World.AddAircraft(aircraft);
        engine.Scenario = new SimScenarioState
        {
            ScenarioId = "test-311",
            ScenarioName = "Cross then delete",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
            PrimaryAirportId = "SFO",
            AutoPullUpToParallel = true,
        };

        Assert.True(engine.SendCommand(Callsign, "CLAND").Success);
        Assert.True(engine.SendCommand(Callsign, "EXIT G").Success);
        return (engine, aircraft);
    }

    /// <summary>
    /// Ticks until the aircraft reaches the auto-pull-up taxi leg toward the parallel — i.e. it is
    /// taxiing with an uncleared 19R crossing still ahead of it. Returns false if that never happens.
    /// </summary>
    private bool TickToTaxiTowardParallel(SimulationEngine engine, AircraftState ac)
    {
        for (int t = 1; t <= 500; t++)
        {
            engine.TickOneSecond();
            if (ac.Phases?.CurrentPhase is TaxiingPhase && ac.Ground.AssignedTaxiRoute is { } route)
            {
                if (route.HoldShortPoints.Exists(hs => !hs.IsCleared && (hs.TargetName ?? "").Contains("19R")))
                {
                    output.WriteLine($"t={t}: taxiing toward 19R, route={route.ToSummary()}");
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Ticks until the aircraft is stopped at the 19R hold-short. Returns the phase, or null.</summary>
    private HoldingShortPhase? TickToHoldShortOfParallel(SimulationEngine engine, AircraftState ac)
    {
        for (int t = 1; t <= 500; t++)
        {
            engine.TickOneSecond();
            if (ac.Phases?.CurrentPhase is HoldingShortPhase h && (h.HoldShort.TargetName ?? "").Contains("19R"))
            {
                output.WriteLine($"t={t}: holding short of {h.HoldShort.TargetName} at gs={ac.GroundSpeed:F2}");
                return h;
            }
        }

        return null;
    }

    /// <summary>
    /// Drives the sim forward looking for the auto-delete. Asserts the aircraft is never removed
    /// before <see cref="CrossingRunwayPhase"/> has both started and finished. Returns the tick the
    /// aircraft disappeared on, or null if it survived the whole window.
    /// </summary>
    private int? TickUntilDeleted(SimulationEngine engine, int maxTicks)
    {
        bool sawCrossing = false;

        for (int t = 1; t <= maxTicks; t++)
        {
            engine.TickOneSecond();

            // Inspect the phase BEFORE the sweep: the trigger fires during physics, so the aircraft
            // can leave CrossingRunwayPhase and be queued for delete within the same tick.
            var beforeSweep = engine.FindAircraft(Callsign);
            if (beforeSweep?.Phases?.CurrentPhase is CrossingRunwayPhase)
            {
                sawCrossing = true;
            }

            engine.SweepPendingAutoDeletes();

            if (engine.FindAircraft(Callsign) is null)
            {
                Assert.True(sawCrossing, $"{Callsign} was deleted at +{t}s without ever entering CrossingRunwayPhase");
                output.WriteLine($"auto-deleted at +{t}s after crossing 19R");
                return t;
            }
        }

        return null;
    }

    /// <summary>
    /// The separator must survive CROSS's repeatable <c>runway</c> parameter: <c>CROSS 19R; DEL</c>
    /// is two blocks, not a CROSS that swallowed "DEL" as a second runway.
    /// </summary>
    [Fact]
    public void CrossThenDelete_ParsesAsTwoBlocks()
    {
        var compound = CommandParser.ParseCompound("CROSS 19R; DEL");
        Assert.True(compound.IsSuccess, compound.Reason);

        Assert.Equal(2, compound.Value!.Blocks.Count);
        var cross = Assert.IsType<CrossRunwayCommand>(compound.Value.Blocks[0].Commands[0]);
        Assert.Equal("19R", Assert.Single(cross.RunwayIds));
        Assert.IsType<DeleteCommand>(compound.Value.Blocks[1].Commands[0]);
    }

    /// <summary>
    /// Baseline: the aircraft is already stopped short of 19R when the compound is issued.
    /// </summary>
    [Fact]
    public void CrossThenDelete_FromHoldShort_DeletesAfterCrossing()
    {
        var setup = SetupLandingAtSfo19L();
        if (setup is null)
        {
            return;
        }

        var (engine, ac) = setup.Value;

        var holding = TickToHoldShortOfParallel(engine, ac);
        Assert.NotNull(holding);

        var result = engine.SendCommand(Callsign, "CROSS 19R; DEL");
        Assert.True(result.Success, $"CROSS 19R; DEL should dispatch: {result.Message}");
        Assert.False(ac.Ground.PendingAutoDelete, "DEL must not apply at dispatch time — it is chained behind the crossing");

        Assert.NotNull(TickUntilDeleted(engine, 300));
    }

    /// <summary>
    /// The pre-clear form: the compound is issued while the aircraft is still taxiing toward the
    /// 19R hold-short. The CROSS pre-clears the crossing (so the aircraft never stops) and the
    /// chained DEL must still wait for the crossing to complete rather than hanging in the queue.
    /// </summary>
    [Fact]
    public void CrossThenDelete_PreClearedWhileTaxiing_DeletesAfterCrossing()
    {
        var setup = SetupLandingAtSfo19L();
        if (setup is null)
        {
            return;
        }

        var (engine, ac) = setup.Value;

        Assert.True(TickToTaxiTowardParallel(engine, ac), "aircraft never reached the auto-pull-up taxi leg toward 19R");

        var result = engine.SendCommand(Callsign, "CROSS 19R; DEL");
        Assert.True(result.Success, $"CROSS 19R; DEL should dispatch: {result.Message}");
        Assert.False(ac.Ground.PendingAutoDelete, "DEL must not apply at dispatch time — it is chained behind the crossing");

        Assert.NotNull(TickUntilDeleted(engine, 400));
    }

    /// <summary>
    /// The queued delete must be visible as a block carrying a <see cref="DeleteCommand"/> — the
    /// datablock "*" marker and NODEL both key off that — and must survive a snapshot round-trip,
    /// since a restored queue has no <c>ParsedCommands</c> to re-derive it from.
    /// </summary>
    [Fact]
    public void CrossThenDelete_QueuedBlockIsMarkedAndSurvivesSnapshotRoundTrip()
    {
        var setup = SetupLandingAtSfo19L();
        if (setup is null)
        {
            return;
        }

        var (engine, ac) = setup.Value;

        Assert.NotNull(TickToHoldShortOfParallel(engine, ac));
        Assert.True(engine.SendCommand(Callsign, "CROSS 19R; DEL").Success);

        var queued = Assert.Single(ac.Queue.Blocks);
        Assert.True(queued.HasDeleteCommand, "the queued DEL block must be flagged so NODEL and the datablock marker can find it");
        Assert.Equal(BlockTriggerType.AfterRunwayCrossing, queued.Trigger?.Type);

        var restored = CommandQueue.FromSnapshot(ac.Queue.ToSnapshot());
        Assert.Contains(restored.Blocks, b => b.HasDeleteCommand);
    }

    /// <summary>
    /// <c>NODEL</c> must strip a queued <c>CROSS; DEL</c>. Without this the block survives, re-raises
    /// <c>PendingAutoDelete</c> after the crossing, and that flag deliberately bypasses
    /// <c>AutoDeleteExempt</c> — so the aircraft would vanish despite the cancel.
    /// </summary>
    [Fact]
    public void Nodel_CancelsQueuedCrossDelete()
    {
        var setup = SetupLandingAtSfo19L();
        if (setup is null)
        {
            return;
        }

        var (engine, ac) = setup.Value;

        Assert.NotNull(TickToHoldShortOfParallel(engine, ac));
        Assert.True(engine.SendCommand(Callsign, "CROSS 19R; DEL").Success);

        var nodel = engine.SendCommand(Callsign, "NODEL");
        Assert.True(nodel.Success, $"NODEL should dispatch: {nodel.Message}");
        Assert.DoesNotContain(ac.Queue.Blocks, b => b.HasDeleteCommand);

        bool sawCrossing = false;
        for (int t = 1; t <= 300; t++)
        {
            engine.TickOneSecond();
            var live = engine.FindAircraft(Callsign);
            if (live?.Phases?.CurrentPhase is CrossingRunwayPhase)
            {
                sawCrossing = true;
            }

            engine.SweepPendingAutoDeletes();
            if (engine.FindAircraft(Callsign) is null)
            {
                Assert.Fail($"{Callsign} was deleted at +{t}s after NODEL — the cancel should have suppressed it");
            }
        }

        Assert.True(sawCrossing, "aircraft must have crossed 19R during the loop for this test to be meaningful");
        Assert.False(ac.Ground.PendingAutoDelete);
    }
}
