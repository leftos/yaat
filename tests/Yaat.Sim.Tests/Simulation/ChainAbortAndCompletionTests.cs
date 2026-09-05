using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Chain-robustness suite: per-path coverage for how a `;`-sequenced compound advances —
/// completion of the current block, and abort of the remainder when a block fails at fire time.
/// See docs/command-chaining.md for the contract.
/// </summary>
public class ChainAbortAndCompletionTests
{
    // OAK north cargo ramp spawn (same siting as IdlePhaseQueueAdvanceTests).
    private const double SpawnLat = 37.7184;
    private const double SpawnLon = -122.2187;
    private const double SpawnHeading = 297;

    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Warnings drained from PendingWarnings each tick (via SimulationEngine.WarningEmitted) —
    /// PendingWarnings itself is emptied by the post-physics drain, so asserting on it races.
    /// </summary>
    private readonly List<string> _warnings = [];

    public ChainAbortAndCompletionTests(ITestOutputHelper output)
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
        engine.WarningEmitted += (_, warning) => _warnings.Add(warning);
        return engine;
    }

    /// <summary>A grounded aircraft with no phase — chain advancement runs in the free (regime A) path.</summary>
    private static AircraftState AddGroundedPhaseless(SimulationEngine engine, string callsign)
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
        engine.World.AddAircraft(ac);
        return ac;
    }

    private void DumpQueue(AircraftState ac)
    {
        _output.WriteLine(
            $"phase={ac.Phases?.CurrentPhase?.GetType().Name ?? "null"} tgtAlt={ac.Targets.TargetAltitude?.ToString() ?? "null"} "
                + $"queue=[{string.Join(", ", ac.Queue.Blocks.Select(b => $"{b.Description}(applied={b.IsApplied})"))}]"
        );
    }

    /// <summary>
    /// A pre-issued climb on a grounded, phase-less aircraft must not stall the chain:
    /// UpdateAltitude skips altitude resolution while on the ground, so the Altitude tracked
    /// command can only complete via the on-ground escape in UpdateBlockCompletion.
    /// </summary>
    [Fact]
    public void GroundedAltitudeCommand_DoesNotStallChain()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddGroundedPhaseless(engine, "CHN101");
        var result = engine.SendCommand(ac.Callsign, "CM 5000; SQ");
        Assert.True(result.Success, result.Message);

        for (int t = 0; t < 5; t++)
        {
            engine.TickOneSecond();
        }

        DumpQueue(ac);
        Assert.Equal(4611u, ac.Transponder.Code);
        Assert.All(ac.Queue.Blocks, b => Assert.True(b.IsApplied, $"block '{b.Description}' never applied"));
    }

    /// <summary>
    /// The on-ground completion escape must complete the tracked command only — the climb stays
    /// armed, so the aircraft still climbs to the pre-issued altitude once airborne.
    /// </summary>
    [Fact]
    public void GroundedAltitudeCommand_StaysArmedForDeparture()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddGroundedPhaseless(engine, "CHN102");
        var result = engine.SendCommand(ac.Callsign, "CM 5000; SQ");
        Assert.True(result.Success, result.Message);

        for (int t = 0; t < 5; t++)
        {
            engine.TickOneSecond();
        }

        Assert.Equal(5000, ac.Targets.TargetAltitude);

        // Hand-fly it airborne: the armed target must now drive a climb.
        ac.IsOnGround = false;
        ac.Altitude = 1000;
        ac.IndicatedAirspeed = 180;
        for (int t = 0; t < 20; t++)
        {
            engine.TickOneSecond();
        }

        Assert.True(ac.Altitude > 1200, $"Pre-armed climb never engaged after departure; altitude={ac.Altitude:F0}");
    }

    // ------------------------------------------------------------------
    // Abort-remainder matrix: a block that FAILS at fire time must discard
    // the rest of its own chain (equal SourceCommandText), warn naming the
    // discarded blocks, and leave blocks from other dispatches untouched.
    // One test per apply path (regime A / triggered lookahead / idle regime C /
    // ground-entity notify / track dispatch / parallel siblings / survivor).
    // ------------------------------------------------------------------

    /// <summary>An airborne, phase-less aircraft near OAK — regime A (free-flying) advancement.</summary>
    private static AircraftState AddAirborne(SimulationEngine engine, string callsign)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = new LatLon(37.65, -122.30),
            TrueHeading = new TrueHeading(40),
            Altitude = 5000,
            IndicatedAirspeed = 250,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "MEM" },
            Transponder = new AircraftTransponder
            {
                AssignedCode = 4611,
                Code = 7654,
                Mode = "C",
            },
        };
        engine.World.AddAircraft(ac);
        return ac;
    }

    private void TickUntil(SimulationEngine engine, int maxSeconds, Func<bool> done)
    {
        for (int t = 0; (t < maxSeconds) && !done(); t++)
        {
            engine.TickOneSecond();
        }
    }

    private static bool ChainSettled(AircraftState ac) => ac.Queue.Blocks.TrueForAll(b => b.IsApplied) || ac.PendingWarnings.Count > 0;

    /// <summary>Regime A: an untriggered mid-chain block that fails at fire time discards the rest of the chain.</summary>
    [Fact]
    public void RegimeA_MidChainFailure_DiscardsRemainder()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddAirborne(engine, "CHN201");
        // TAXI ZZ parses but fails at apply (airborne aircraft, bogus taxiway).
        var result = engine.SendCommand(ac.Callsign, "CM 5000; TAXI ZZ; SQ");
        Assert.True(result.Success, result.Message);

        TickUntil(engine, 10, () => _warnings.Count > 0);
        DumpQueue(ac);
        _output.WriteLine($"warnings=[{string.Join(" | ", _warnings)}]");

        Assert.Equal(7654u, ac.Transponder.Code);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("SQ", StringComparison.Ordinal));
        Assert.Contains(_warnings, w => w.Contains("discarded", StringComparison.OrdinalIgnoreCase) && w.Contains("SQ", StringComparison.Ordinal));
    }

    /// <summary>Triggered lookahead / fix-sequencing: an AT-fix block that fails discards its chain-mates.</summary>
    [Fact]
    public void TriggeredFixFailure_DiscardsRemainder()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddAirborne(engine, "CHN202");
        var result = engine.SendCommand(ac.Callsign, "DCT OAK; AT OAK TAXI ZZ; SQ");
        Assert.True(result.Success, result.Message);

        // ~5 nm to the OAK VOR at 250 kts — give it a generous window to sequence the fix.
        TickUntil(engine, 240, () => _warnings.Count > 0);
        DumpQueue(ac);
        _output.WriteLine($"warnings=[{string.Join(" | ", _warnings)}]");

        Assert.True(_warnings.Count > 0, "AT OAK block never fired/failed");
        Assert.Equal(7654u, ac.Transponder.Code);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("SQ", StringComparison.Ordinal));
    }

    /// <summary>Regime C: an idle-phase untriggered block that fails discards its chain-mates.</summary>
    [Fact]
    public void IdlePhaseFailure_DiscardsRemainder()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddParkedWithPhase(engine, "CHN203");
        var result = engine.SendCommand(ac.Callsign, "PUSH; TAXI ZZ; SQ");
        Assert.True(result.Success, result.Message);

        // Pushback runs ~25-30 s; the TAXI ZZ failure lands right after it settles idle.
        TickUntil(engine, 90, () => _warnings.Any(w => w.Contains("ZZ", StringComparison.OrdinalIgnoreCase)));
        DumpQueue(ac);
        _output.WriteLine($"warnings=[{string.Join(" | ", _warnings)}]");

        Assert.Equal(7654u, ac.Transponder.Code);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("SQ", StringComparison.Ordinal));
    }

    /// <summary>Ground-entity notify path: an AT-taxiway block that fails discards its chain-mates.</summary>
    [Fact]
    public void GroundEntityTriggerFailure_DiscardsRemainder()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddParkedWithPhase(engine, "CHN204");
        var result = engine.SendCommand(ac.Callsign, "PUSH; TAXI B3 HS B; AT B3 TAXI ZZ; SQ");
        Assert.True(result.Success, result.Message);

        TickUntil(engine, 180, () => _warnings.Any(w => w.Contains("ZZ", StringComparison.OrdinalIgnoreCase)));
        DumpQueue(ac);
        _output.WriteLine($"warnings=[{string.Join(" | ", _warnings)}]");

        Assert.True(_warnings.Any(w => w.Contains("ZZ", StringComparison.OrdinalIgnoreCase)), "AT B3 TAXI ZZ never fired/failed");
        Assert.Equal(7654u, ac.Transponder.Code);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("SQ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Track path: a triggered handoff that fails at TrackEngine dispatch discards its chain-mates. A typed compound
    /// containing a track verb never reaches the dispatcher whole — the action router (like the live server) splits it
    /// into units that dispatch independently, so the trailing <c>SQ</c> would apply at issue time. The one path that
    /// hands a track verb its chain-mates is a scenario preset, which dispatches straight into <c>DispatchCompound</c>;
    /// this test takes that path.
    /// </summary>
    [Fact]
    public void TrackDispatchFailure_DiscardsRemainder()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddAirborne(engine, "CHN205");
        var compound = CommandParser.ParseCompound("DCT OAK; AT OAK HO ZZ9; SQ", ac.FlightPlan.Route);
        Assert.True(compound.IsSuccess, compound.Reason);
        var presetContext = TestDispatch.Context(
            engine.World.Rng,
            groundLayout: engine.World.GroundLayout,
            findAircraft: engine.FindAircraft,
            listAircraft: () => engine.World.GetSnapshot(),
            isScenarioScripted: true
        );
        var result = CommandDispatcher.DispatchCompound(compound.Value!, ac, presetContext);
        Assert.True(result.Success, result.Message);

        TickUntil(engine, 240, () => _warnings.Count > 0);
        DumpQueue(ac);
        _output.WriteLine($"warnings=[{string.Join(" | ", _warnings)}]");

        Assert.True(_warnings.Count > 0, "AT OAK HO ZZ9 never fired/failed");
        Assert.Equal(7654u, ac.Transponder.Code);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("SQ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Parallel siblings: when one command of a `,` block fails, applied siblings stay applied,
    /// the block counts failed, and the chain remainder is discarded.
    /// </summary>
    [Fact]
    public void ParallelSiblingFailure_DiscardsRemainder_KeepsAppliedSibling()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddAirborne(engine, "CHN206");
        var result = engine.SendCommand(ac.Callsign, "CM 5000; FH 090, TAXI ZZ; SQ");
        Assert.True(result.Success, result.Message);

        TickUntil(engine, 10, () => _warnings.Count > 0);
        DumpQueue(ac);
        _output.WriteLine($"warnings=[{string.Join(" | ", _warnings)}]");

        Assert.True(_warnings.Count > 0, "FH 090, TAXI ZZ never fired/failed");
        Assert.NotNull(ac.Targets.TargetTrueHeading);
        Assert.Equal(7654u, ac.Transponder.Code);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("SQ", StringComparison.Ordinal));
    }

    /// <summary>An independent conditional queued by a separate dispatch must survive another chain's abort.</summary>
    [Fact]
    public void IndependentDispatch_SurvivesOtherChainAbort()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        var ac = AddAirborne(engine, "CHN207");
        var r0 = engine.SendCommand(ac.Callsign, "DCT OAK");
        Assert.True(r0.Success, r0.Message);
        // Dispatch 1: an unreachable-level conditional (aircraft stays at 5000) — never fires,
        // must survive dispatch 2's abort. Conditional-led compounds queue additively.
        var r1 = engine.SendCommand(ac.Callsign, "LV 100 SQ");
        Assert.True(r1.Success, r1.Message);
        // Dispatch 2: also conditional-led (additive); fails at fire time and aborts its own remainder only.
        var r2 = engine.SendCommand(ac.Callsign, "AT OAK TAXI ZZ; FH 270");
        Assert.True(r2.Success, r2.Message);

        TickUntil(engine, 240, () => _warnings.Any(w => w.Contains("ZZ", StringComparison.OrdinalIgnoreCase)));
        DumpQueue(ac);
        _output.WriteLine($"warnings=[{string.Join(" | ", _warnings)}]");

        Assert.True(_warnings.Any(w => w.Contains("ZZ", StringComparison.OrdinalIgnoreCase)), "AT OAK TAXI ZZ never fired/failed");
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("FH", StringComparison.Ordinal));
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied && (b.Description ?? "").Contains("SQ", StringComparison.Ordinal));
    }

    /// <summary>A parked aircraft with an AtParkingPhase installed (mirrors IdlePhaseQueueAdvanceTests.AddParked).</summary>
    private static AircraftState AddParkedWithPhase(SimulationEngine engine, string callsign)
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
}
