using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Issue #407: untriggered blocks queued behind a phase-installing ground command stranded
/// forever. <c>PUSH; SQ; SQNORM; TAXI B5 HS B</c> was accepted, the pushback completed into
/// HoldingAfterPushbackPhase — which never completes on its own — and the queued SQ/SQNORM/TAXI
/// never dispatched, because FlightPhysics.UpdateCommandQueue never advances untriggered blocks
/// while any phase is active. The fix: phases that idle awaiting a controller command
/// (<see cref="Phase.IsIdleAwaitingCommands"/>) let the queue advance untriggered blocks in
/// strict `;` order.
/// </summary>
public class IdlePhaseQueueAdvanceTests
{
    // FDX440's parking position from the issue-407 bundle (OAK north cargo ramp);
    // PUSH backs it out onto the ramp and TAXI B3 HS B resolves from there.
    private const double SpawnLat = 37.7184;
    private const double SpawnLon = -122.2187;
    private const double SpawnHeading = 297;

    private readonly ITestOutputHelper _output;

    public IdlePhaseQueueAdvanceTests(ITestOutputHelper output)
    {
        _output = output;
        // Pin singletons before any [Fact] body runs (parallel-class race guard).
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? BuildEngine()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();

        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        engine.World.GroundLayout = layout;
        engine.World.ReactionDelayRng = new SerializableRandom(42);
        return engine;
    }

    private static AircraftState AddParked(SimulationEngine engine, string callsign = "FDX440")
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B763",
            Position = new LatLon(SpawnLat, SpawnLon),
            TrueHeading = new TrueHeading(SpawnHeading),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "MEM" },
            Transponder = new AircraftTransponder
            {
                AssignedCode = 4611,
                Code = 7654,
                Mode = "Standby",
            },
        };
        ac.Phases = new PhaseList();
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac));
        engine.World.AddAircraft(ac);
        return ac;
    }

    private void TickUntil(SimulationEngine engine, int maxSeconds, System.Func<bool> done)
    {
        for (int t = 0; (t < maxSeconds) && !done(); t++)
        {
            engine.TickOneSecond();
        }
    }

    private void DumpQueue(AircraftState ac)
    {
        _output.WriteLine(
            $"phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "null"} "
                + $"queue=[{string.Join(", ", ac.Queue.Blocks.Select(b => $"{b.Description}(applied={b.IsApplied})"))}]"
        );
    }

    /// <summary>
    /// The exact issue-407 sequence: PUSH applies immediately; SQ, SQNORM, and TAXI must fire
    /// once the pushback settles into HoldingAfterPushbackPhase (which never self-completes).
    /// </summary>
    [Fact]
    public void PushThenSqSqnormTaxi_FiresAfterPushback()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddParked(engine);
        var result = engine.SendCommand(ac.Callsign, "PUSH; SQ; SQNORM; TAXI B3 HS B");
        Assert.True(result.Success, result.Message);
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);

        // The pushback runs ~25-30 s; give the queue a generous window.
        TickUntil(engine, 90, () => ac.Phases?.CurrentPhase is TaxiingPhase);
        DumpQueue(ac);

        Assert.Equal(4611u, ac.Transponder.Code);
        Assert.Equal("C", ac.Transponder.Mode);
        Assert.True(
            ac.Phases?.CurrentPhase is TaxiingPhase or HoldingShortPhase,
            $"Queued TAXI never fired after pushback; phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "null"}"
        );
        Assert.NotNull(ac.Ground.AssignedTaxiRoute);
        Assert.All(ac.Queue.Blocks, b => Assert.True(b.IsApplied, $"block '{b.Description}' never applied"));
    }

    /// <summary>
    /// A queued block the idle phase rejects (FH 270 while holding after pushback) must stay
    /// queued — visible, unapplied, and without per-tick warning spam — not be force-applied
    /// or dropped.
    /// </summary>
    [Fact]
    public void RejectedBlock_StaysQueued_NoSpam()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddParked(engine);
        var result = engine.SendCommand(ac.Callsign, "PUSH; FH 270");
        Assert.True(result.Success, result.Message);

        TickUntil(engine, 90, () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        int warningsAtIdle = ac.PendingWarnings.Count;

        for (int t = 0; t < 10; t++)
        {
            engine.TickOneSecond();
        }

        DumpQueue(ac);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("FH", System.StringComparison.Ordinal));
        Assert.Equal(0, ac.IndicatedAirspeed);
        Assert.True(
            ac.PendingWarnings.Count <= warningsAtIdle + 1,
            $"Rejected queued block generated warning spam: {ac.PendingWarnings.Count - warningsAtIdle} new warnings in 10 s"
        );
    }

    /// <summary>
    /// Untriggered blocks must not advance while a moving (non-idle) phase is current: a HOLD
    /// queued behind TAXI stays unapplied while the aircraft is mid-route in TaxiingPhase.
    /// </summary>
    [Fact]
    public void UntriggeredBlock_DoesNotFire_WhileTaxiingMidRoute()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddParked(engine);
        var result = engine.SendCommand(ac.Callsign, "PUSH; TAXI B3 HS B; HOLD");
        Assert.True(result.Success, result.Message);

        TickUntil(engine, 90, () => ac.Phases?.CurrentPhase is TaxiingPhase);
        Assert.IsType<TaxiingPhase>(ac.Phases?.CurrentPhase);

        // A couple of seconds into the taxi the aircraft is mid-route and moving;
        // the queued HOLD must not have fired.
        engine.TickOneSecond();
        engine.TickOneSecond();
        DumpQueue(ac);
        Assert.IsType<TaxiingPhase>(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Ground.Hold);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("HOLD", System.StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An untriggered WAIT block counts down while idling before firing its payload:
    /// <c>PUSH; WAIT 15 TAXI ...</c> must not start taxiing the instant the pushback ends.
    /// </summary>
    [Fact]
    public void IdleWaitBlock_CountsDown()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddParked(engine);
        var result = engine.SendCommand(ac.Callsign, "PUSH; WAIT 15 TAXI B3 HS B");
        Assert.True(result.Success, result.Message);

        TickUntil(engine, 90, () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);

        // Well inside the 15 s wait the TAXI must not have fired.
        for (int t = 0; t < 5; t++)
        {
            engine.TickOneSecond();
        }

        DumpQueue(ac);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);

        // After the wait elapses the TAXI fires.
        TickUntil(engine, 30, () => ac.Phases?.CurrentPhase is TaxiingPhase);
        DumpQueue(ac);
        Assert.True(
            ac.Phases?.CurrentPhase is TaxiingPhase or HoldingShortPhase,
            $"Queued WAIT 15 TAXI never fired; phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "null"}"
        );
    }

    /// <summary>
    /// The `;` frontier: an untriggered block behind an unfired triggered block must not
    /// leapfrog it, even while the aircraft idles.
    /// </summary>
    [Fact]
    public void UntriggeredBlock_BehindUnmetTrigger_DoesNotLeapfrog()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddParked(engine);
        // LV 500 can never fire on a parked aircraft (it never leaves ~9 ft), so the trailing
        // TAXI must stay queued behind it indefinitely.
        var result = engine.SendCommand(ac.Callsign, "PUSH; LV 5 SQ; TAXI B3 HS B");
        Assert.True(result.Success, result.Message);

        TickUntil(engine, 90, () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);

        for (int t = 0; t < 10; t++)
        {
            engine.TickOneSecond();
        }

        DumpQueue(ac);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.Null(ac.Ground.AssignedTaxiRoute);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("TAXI", System.StringComparison.OrdinalIgnoreCase));
    }
}
