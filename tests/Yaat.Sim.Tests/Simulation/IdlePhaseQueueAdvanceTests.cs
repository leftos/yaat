using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
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

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
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
            Phases = new PhaseList(),
        };
        ac.Phases.Add(new AtParkingPhase());
        ac.Phases.Start(CommandDispatcher.BuildMinimalContext(ac, ActiveRunways.Empty));
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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = AddParked(engine);
        CommandResult result = engine.SendCommand(ac.Callsign, "PUSH; SQ; SQNORM; TAXI B3 HS B");
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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = AddParked(engine);
        CommandResult result = engine.SendCommand(ac.Callsign, "PUSH; FH 270");
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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = AddParked(engine);
        CommandResult result = engine.SendCommand(ac.Callsign, "PUSH; TAXI B3 HS B; HOLD");
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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = AddParked(engine);
        CommandResult result = engine.SendCommand(ac.Callsign, "PUSH; WAIT 15 TAXI B3 HS B");
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
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = AddParked(engine);
        // LV 500 can never fire on a parked aircraft (it never leaves ~9 ft), so the trailing
        // TAXI must stay queued behind it indefinitely.
        CommandResult result = engine.SendCommand(ac.Callsign, "PUSH; LV 5 SQ; TAXI B3 HS B");
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

    /// <summary>
    /// Issue #475: a `,` block whose taxi clears the idle phase carried a sibling the idle phase rejects on its own
    /// (HS while holding after pushback). The idle advance judged every command of the block against
    /// HoldingAfterPushbackPhase, so the block never applied; dispatched directly the same block taxis, because the
    /// taxi clears the phase and the hold-short is judged by the TaxiingPhase it installs.
    /// </summary>
    [Theory]
    [InlineData("SQ; SQNORM; PUSH; TAXIAUTO 30, HS B")]
    [InlineData("SQ; SQNORM; PUSH; TAXI B3, HS B")]
    public void TaxiWithParallelHoldShort_FiresAfterPushback(string command)
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = AddParked(engine);
        CommandResult result = engine.SendCommand(ac.Callsign, command);
        Assert.True(result.Success, result.Message);
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);

        TickUntil(engine, 90, () => ac.Phases?.CurrentPhase is TaxiingPhase);
        DumpQueue(ac);

        Assert.True(
            ac.Phases?.CurrentPhase is TaxiingPhase or HoldingShortPhase,
            $"Queued taxi never fired after pushback; phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "null"}"
        );
        Assert.NotNull(ac.Ground.AssignedTaxiRoute);
        Assert.Contains(ac.Ground.AssignedTaxiRoute.HoldShortPoints, h => (h.TargetName == "B") && (h.Reason == HoldShortReason.ExplicitHoldShort));
        Assert.All(ac.Queue.Blocks, b => Assert.True(b.IsApplied, $"block '{b.Description}' never applied"));
    }

    /// <summary>
    /// A block whose idle-rejected command precedes the phase-clearing one stays held: it would fail at apply time too.
    /// </summary>
    [Fact]
    public void HoldShortBeforeTaxi_StaysQueued()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = AddParked(engine);
        CommandResult result = engine.SendCommand(ac.Callsign, "PUSH; HS B, TAXIAUTO 30");
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
        Assert.Null(ac.Ground.AssignedTaxiRoute);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("HS", System.StringComparison.Ordinal));
        Assert.Equal(warningsAtIdle, ac.PendingWarnings.Count);
    }

    /// <summary>
    /// Issue #475 ("lost HS B"): <c>TAXIAUTO 30; HS B</c> left its <c>HS B</c> block queued behind the taxi; the
    /// controller's later single-block <c>TAXIAUTO 30, HS B</c> was handled by the taxiing phase and never cleared
    /// it, so the stale block outlived the clearance that replaced it and was reported lost long after. A fresh taxi
    /// clearance supersedes it, and since the new clearance carries the same hold-short it is not reported lost.
    /// </summary>
    [Fact]
    public void FreshTaxiClearanceWhileTaxiing_SupersedesTheEarlierClearancesQueuedHoldShort()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = TaxiWithQueuedChain(engine, "TAXIAUTO 30; HS B");
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied && (b.Description == "HS B"));

        CommandResult result = engine.SendCommand(ac.Callsign, "TAXIAUTO 30, HS B");
        Assert.True(result.Success, result.Message);
        DumpQueue(ac);

        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied);
        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("lost:", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half of the #475 ruling: an amendment to the taxi in progress (<c>HOLD</c>, then <c>RES</c>) is not
    /// a fresh clearance, so the chain queued behind the taxi stays in place.
    /// </summary>
    [Fact]
    public void AmendmentWhileTaxiing_KeepsTheQueuedChain()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = TaxiWithQueuedChain(engine, "TAXIAUTO 30; HS B");

        CommandResult hold = engine.SendCommand(ac.Callsign, "HOLD");
        Assert.True(hold.Success, hold.Message);
        DumpQueue(ac);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied && (b.Description == "HS B"));

        CommandResult resume = engine.SendCommand(ac.Callsign, "RES");
        Assert.True(resume.Success, resume.Message);
        DumpQueue(ac);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied && (b.Description == "HS B"));
        Assert.DoesNotContain(ac.PendingWarnings, w => w.Contains("lost:", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// A queued block whose every command the incoming compound carries again is re-issued, not lost; a queued block
    /// the incoming compound does not repeat is still reported. The incoming <c>TAXIAUTO 30, HS B; SQ</c> repeats the
    /// stale <c>HS B</c> inside a block of its own (so no whole-block description matches it) and drops the
    /// <c>HOLD</c>.
    /// </summary>
    [Fact]
    public void FreshTaxiClearanceWhileTaxiing_ReportsOnlyTheQueuedBlocksItDoesNotReissueAsLost()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = TaxiWithQueuedChain(engine, "TAXIAUTO 30; HS B; HOLD");
        int warningsBefore = ac.PendingWarnings.Count;

        CommandResult result = engine.SendCommand(ac.Callsign, "TAXIAUTO 30, HS B; SQ");
        Assert.True(result.Success, result.Message);
        DumpQueue(ac);

        string warning = Assert.Single(ac.PendingWarnings.Skip(warningsBefore), w => w.Contains("lost:", System.StringComparison.Ordinal));
        _output.WriteLine(warning);
        Assert.EndsWith("(lost: HOLD)", warning, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A heading-only <c>PUSH FACE</c> during the push-off amends the push in progress rather than issuing a fresh
    /// clearance, so the taxi queued behind the push keeps its place and nothing is reported lost.
    /// </summary>
    [Fact]
    public void PushFacingAmendmentDuringPushOff_KeepsTheQueuedTaxi()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = AddParked(engine);
        CommandResult push = engine.SendCommand(ac.Callsign, "PUSH FACE N; TAXIAUTO 30");
        Assert.True(push.Success, push.Message);
        engine.TickOneSecond();
        engine.TickOneSecond();
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        int warningsBefore = ac.PendingWarnings.Count;

        CommandResult amend = engine.SendCommand(ac.Callsign, "PUSH FACE S");
        _output.WriteLine($"amend: success={amend.Success} msg={amend.Message}");
        Assert.True(amend.Success, amend.Message);
        DumpQueue(ac);

        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied && (b.Description == "TAXIAUTO 30"));
        Assert.DoesNotContain(ac.PendingWarnings.Skip(warningsBefore), w => w.Contains("lost:", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// A fresh taxi clearance cancels a pending give-way deferral that carries an earlier taxi clearance, names it in
    /// the lost warning, and the aircraft keeps the fresh route once the give-way would have been met.
    /// </summary>
    [Fact]
    public void FreshTaxiClearance_CancelsAPendingGiveWayTaxi()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = PushedBack(engine);
        AddParked(engine, "UAL1");
        CommandResult giveWay = engine.SendCommand(ac.Callsign, "GIVEWAY UAL1 TAXI B3 HS B");
        Assert.True(giveWay.Success, giveWay.Message);
        Assert.Single(ac.DeferredDispatches);
        int warningsBefore = ac.PendingWarnings.Count;

        CommandResult taxi = engine.SendCommand(ac.Callsign, "TAXI B3 B");
        Assert.True(taxi.Success, taxi.Message);
        DumpQueue(ac);

        Assert.Empty(ac.DeferredDispatches);
        string warning = Assert.Single(ac.PendingWarnings.Skip(warningsBefore), w => w.Contains("lost:", System.StringComparison.Ordinal));
        _output.WriteLine(warning);
        Assert.Contains("UAL1", warning, System.StringComparison.Ordinal);

        for (int t = 0; t < 10; t++)
        {
            engine.TickOneSecond();
        }

        Assert.NotNull(ac.Ground.AssignedTaxiRoute);
        Assert.DoesNotContain(
            ac.Ground.AssignedTaxiRoute.HoldShortPoints,
            h => (h.TargetName == "B") && (h.Reason == HoldShortReason.ExplicitHoldShort)
        );
    }

    /// <summary>A pending WAIT whose payload has no ground axis (a frequency change) survives a fresh taxi clearance.</summary>
    [Fact]
    public void FreshTaxiClearance_KeepsAPendingWaitWithNoGroundAxis()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = PushedBack(engine);
        CommandResult wait = engine.SendCommand(ac.Callsign, "WAIT 60 CT");
        Assert.True(wait.Success, wait.Message);
        int warningsBefore = ac.PendingWarnings.Count;

        CommandResult taxi = engine.SendCommand(ac.Callsign, "TAXI B3 HS B");
        Assert.True(taxi.Success, taxi.Message);

        DeferredDispatch kept = Assert.Single(ac.DeferredDispatches);
        Assert.IsType<ContactCommand>(Assert.Single(Assert.Single(kept.Payload.Blocks).Commands));
        Assert.DoesNotContain(ac.PendingWarnings.Skip(warningsBefore), w => w.Contains("lost:", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// A pilot-reaction delay is never cancelled, even one carrying a taxi clearance, so commands issued in a row still
    /// fire in order.
    /// </summary>
    [Fact]
    public void FreshTaxiClearance_KeepsAPendingReactionDelay()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = PushedBack(engine);
        DeferredDispatch reaction = new(5, ParseCompound("TAXI B3 HS B")) { SourceText = "TAXI B3 HS B", IsReactionDelay = true };
        ac.DeferredDispatches.Add(reaction);

        CommandResult taxi = engine.SendCommand(ac.Callsign, "TAXI B3 B");
        Assert.True(taxi.Success, taxi.Message);

        Assert.Same(reaction, Assert.Single(ac.DeferredDispatches));
    }

    /// <summary>
    /// The JAL57 shape: a scenario-scripted <c>WAIT 30 TAXI M4 M1</c> expired during the pushback and is held until the
    /// push completes. A fresh taxi clearance issued meanwhile cancels it, so it can no longer fire over that clearance.
    /// </summary>
    [Fact]
    public void FreshTaxiClearanceDuringPushback_CancelsAnExpiredHeldScriptedTaxi()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = AddParked(engine);
        CommandResult push = engine.SendCommand(ac.Callsign, "PUSH");
        Assert.True(push.Success, push.Message);
        engine.TickOneSecond();
        var scripted = new DeferredDispatch(0.5, ParseCompound("TAXI M4 M1")) { SourceText = "WAIT 30 TAXI M4 M1", IsScenarioScripted = true };
        ac.DeferredDispatches.Add(scripted);
        engine.TickOneSecond();
        engine.TickOneSecond();
        Assert.IsType<PushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.Same(scripted, Assert.Single(ac.DeferredDispatches));
        Assert.True(scripted.RemainingSeconds < 0, $"the scripted taxi has not expired: {scripted.RemainingSeconds}");
        int warningsBefore = ac.PendingWarnings.Count;

        CommandResult taxi = engine.SendCommand(ac.Callsign, "TAXI B3 HS B");
        Assert.True(taxi.Success, taxi.Message);
        DumpQueue(ac);

        Assert.Empty(ac.DeferredDispatches);
        string warning = Assert.Single(ac.PendingWarnings.Skip(warningsBefore), w => w.Contains("lost:", System.StringComparison.Ordinal));
        _output.WriteLine(warning);
        Assert.Contains("TAXI M4 M1", warning, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A conditional block in the incoming compound does not re-issue an unconditional queued block: the queued
    /// <c>HS C</c> is dropped by <c>TAXIAUTO 30; AT B HS C</c>, which holds short of C only once on B, so it is lost.
    /// </summary>
    [Fact]
    public void ConditionalReissueInTheIncomingCompound_StillReportsTheUnconditionalBlockLost()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = TaxiWithQueuedChain(engine, "TAXIAUTO 30; HS C");
        int warningsBefore = ac.PendingWarnings.Count;

        CommandResult result = engine.SendCommand(ac.Callsign, "TAXIAUTO 30; AT B HS C");
        Assert.True(result.Success, result.Message);
        DumpQueue(ac);

        string warning = Assert.Single(ac.PendingWarnings.Skip(warningsBefore), w => w.Contains("lost:", System.StringComparison.Ordinal));
        _output.WriteLine(warning);
        Assert.EndsWith("(lost: HS C)", warning, System.StringComparison.Ordinal);
    }

    /// <summary>A plain fresh <c>TAXI</c> from the post-pushback hold supersedes the chain still queued there.</summary>
    [Fact]
    public void FreshPlainTaxi_SupersedesTheQueuedChain()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = PushedBackWithHeldChain(engine);
        int warningsBefore = ac.PendingWarnings.Count;

        CommandResult taxi = engine.SendCommand(ac.Callsign, "TAXI B3 HS B");
        Assert.True(taxi.Success, taxi.Message);
        DumpQueue(ac);

        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied);
        Assert.Single(ac.PendingWarnings.Skip(warningsBefore), w => w.Contains("lost:", System.StringComparison.Ordinal));
    }

    /// <summary>A fresh <c>PUSH</c> from the post-pushback hold supersedes the chain still queued there.</summary>
    [Fact]
    public void FreshPushFromHoldingAfterPushback_SupersedesTheQueuedChain()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = PushedBackWithHeldChain(engine);
        int warningsBefore = ac.PendingWarnings.Count;

        CommandResult push = engine.SendCommand(ac.Callsign, "PUSH");
        _output.WriteLine($"push: success={push.Success} msg={push.Message}");
        Assert.True(push.Success, push.Message);
        DumpQueue(ac);

        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied);
        Assert.Single(ac.PendingWarnings.Skip(warningsBefore), w => w.Contains("lost:", System.StringComparison.Ordinal));
    }

    /// <summary>A fresh <c>PUSH</c> from the stand supersedes a ground chain queued there behind a taxiway trigger.</summary>
    [Fact]
    public void FreshPushFromParking_SupersedesTheQueuedChain()
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        AircraftState ac = AddParked(engine);
        CommandResult chain = engine.SendCommand(ac.Callsign, "AT B HS C; HOLD");
        Assert.True(chain.Success, chain.Message);
        DumpQueue(ac);
        Assert.IsType<AtParkingPhase>(ac.Phases?.CurrentPhase);
        Assert.Equal(2, ac.Queue.Blocks.Count(b => !b.IsApplied));
        int warningsBefore = ac.PendingWarnings.Count;

        CommandResult push = engine.SendCommand(ac.Callsign, "PUSH");
        Assert.True(push.Success, push.Message);
        DumpQueue(ac);

        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied);
        Assert.Single(ac.PendingWarnings.Skip(warningsBefore), w => w.Contains("lost:", System.StringComparison.Ordinal));
    }

    private static CompoundCommand ParseCompound(string text)
    {
        ParseResult<CompoundCommand> parsed = CommandParser.ParseCompound(text);
        Assert.True(parsed.IsSuccess, $"parse failed for '{text}': {parsed.Reason}");
        return parsed.Value! with { SourceText = text };
    }

    /// <summary>Pushes <see cref="AddParked"/>'s aircraft back and ticks it into the idle post-pushback hold.</summary>
    private AircraftState PushedBack(SimulationEngine engine)
    {
        AircraftState ac = AddParked(engine);
        CommandResult push = engine.SendCommand(ac.Callsign, "PUSH");
        Assert.True(push.Success, push.Message);
        TickUntil(engine, 90, () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        return ac;
    }

    /// <summary>
    /// Pushes back with <c>HS B, TAXIAUTO 30</c> chained behind the push: the post-pushback hold rejects the leading
    /// hold-short, so the block stays queued there (see <see cref="HoldShortBeforeTaxi_StaysQueued"/>).
    /// </summary>
    private AircraftState PushedBackWithHeldChain(SimulationEngine engine)
    {
        AircraftState ac = AddParked(engine);
        CommandResult push = engine.SendCommand(ac.Callsign, "PUSH; HS B, TAXIAUTO 30");
        Assert.True(push.Success, push.Message);
        TickUntil(engine, 90, () => ac.Phases?.CurrentPhase is HoldingAfterPushbackPhase);
        Assert.IsType<HoldingAfterPushbackPhase>(ac.Phases?.CurrentPhase);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied);
        return ac;
    }

    /// <summary>
    /// Pushes <see cref="AddParked"/>'s aircraft back, then issues <paramref name="chain"/> from the idle
    /// post-pushback hold and ticks it two seconds into the taxi, leaving the chain's later blocks queued behind it.
    /// </summary>
    private AircraftState TaxiWithQueuedChain(SimulationEngine engine, string chain)
    {
        AircraftState ac = PushedBack(engine);

        CommandResult taxi = engine.SendCommand(ac.Callsign, chain);
        Assert.True(taxi.Success, taxi.Message);
        engine.TickOneSecond();
        engine.TickOneSecond();
        DumpQueue(ac);
        Assert.IsType<TaxiingPhase>(ac.Phases?.CurrentPhase);
        return ac;
    }
}
