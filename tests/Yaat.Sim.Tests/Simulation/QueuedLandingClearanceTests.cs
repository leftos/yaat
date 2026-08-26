using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A controller sets up a pattern arrival in two transmissions: <c>DCT VPCOL; ERD 28R</c> (fly direct
/// VPCOL, then enter right downwind 28R once the fix is reached) and — while the aircraft is still en
/// route to VPCOL — a separate <c>CLAND</c>. The pattern entry has not fired yet, so the aircraft has no
/// PhaseList at all and the clearance was rejected with "Aircraft has no active phase sequence". It must
/// instead be pre-issued against the queued entry and become the standing clearance when that entry
/// builds its circuit. Covers the sibling option clearances (TG/SG/LA/COPT), the runway-mismatch reject,
/// the same-compound trailing form, snapshot round-trip, and CLC cancellation.
///
/// The queued ERD must survive each of these transmissions — the clearance verbs are tower commands
/// (CommandDimension.All), so they hit the same queue-clear fast path that forced the EXT/SA/MNA reroute.
/// </summary>
public class QueuedLandingClearanceTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder
            .CreateForTest(output)
            .EnableCategory("PatternCommandHandler", LogLevel.Debug)
            .EnableCategory("CommandDispatcher", LogLevel.Debug)
            .InitializeSimLog();

        return new SimulationEngine(new TestAirportGroundData());
    }

    /// <summary>
    /// Headline: CLAND issued while an ERD sits queued behind DCT VPCOL must be accepted, and the queued
    /// ERD block must survive (CLAND is CommandDimension.All, so the All/None fast path in
    /// ClearConflictingBlocks would otherwise wipe the very entry the clearance is meant to attach to).
    /// </summary>
    [Fact]
    public void Cland_BehindQueuedErd_IsAcceptedAndPreservesEntry()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC001");

        var setup = engine.SendCommand("TSC001", "DCT VPCOL; ERD 28R");
        Assert.True(setup.Success, setup.Message);

        var ac = engine.FindAircraft("TSC001");
        Assert.NotNull(ac);
        Assert.Null(ac.Phases?.CurrentPhase); // ERD queued, not yet fired
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // the queued ERD block

        var cland = engine.SendCommand("TSC001", "CLAND");
        output.WriteLine($"CLAND: success={cland.Success} — {cland.Message}");
        Assert.True(cland.Success, $"CLAND behind a queued ERD should be accepted: {cland.Message}");

        ac = engine.FindAircraft("TSC001");
        Assert.NotNull(ac);
        Assert.Null(ac.Phases?.CurrentPhase); // still en route — CLAND did not force a phase
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // queued ERD NOT wiped by CLAND
        Assert.Equal(ClearanceType.ClearedToLand, ac.Pattern.PendingLandingClearance?.Clearance);
    }

    /// <summary>
    /// End-to-end: the pre-issued CLAND becomes the circuit's standing clearance when the entry builds,
    /// and the circuit must terminate in a full-stop LandingPhase — not a TouchAndGoPhase. This is the
    /// ordering assertion: TryEnterPattern derives touchAndGo from the standing clearance *before*
    /// PatternBuilder.BuildCircuit runs, so the pre-arm has to be folded in there and not applied after.
    /// </summary>
    [Fact]
    public void Cland_BehindQueuedErd_BuildsFullStopWhenEntryFires()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC002");

        Assert.True(engine.SendCommand("TSC002", "DCT VPCOL; ERD 28R").Success);
        Assert.True(engine.SendCommand("TSC002", "CLAND").Success, "CLAND behind a queued ERD should be accepted");

        // Fire the entry: builds the circuit and consumes the pre-issued clearance.
        Assert.True(engine.SendCommand("TSC002", "ERD 28R").Success);

        var ac = engine.FindAircraft("TSC002");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);
        Assert.Equal("28R", ac.Phases.ClearedRunwayId);
        Assert.Contains(ac.Phases.Phases, p => p is LandingPhase);
        Assert.DoesNotContain(ac.Phases.Phases, p => p is TouchAndGoPhase);
        Assert.Null(ac.Pattern.PendingLandingClearance); // single-shot
    }

    /// <summary>
    /// The queued entry's runway is knowable when the clearance is issued, so a contradicting runway is
    /// rejected up front (7110.65 §3-10-5 — a landing clearance names a runway) rather than armed and
    /// silently dropped later. The queued ERD must survive the rejection.
    /// </summary>
    [Fact]
    public void ClandWrongRunway_BehindQueuedErd_IsRejectedAndPreservesEntry()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC003");

        Assert.True(engine.SendCommand("TSC003", "DCT VPCOL; ERD 28R").Success);

        var cland = engine.SendCommand("TSC003", "CLAND 30");
        output.WriteLine($"CLAND 30: success={cland.Success} — {cland.Message}");
        Assert.False(cland.Success, "CLAND for a runway the queued entry does not name should be rejected");
        Assert.Contains("30", cland.Message);
        Assert.Contains("28R", cland.Message);

        var ac = engine.FindAircraft("TSC003");
        Assert.NotNull(ac);
        Assert.Null(ac.Pattern.PendingLandingClearance);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // queued ERD NOT wiped by the rejection
    }

    /// <summary>
    /// A clearance states its runway (7110.65 §3-10-5.a) and the pilot reads it back (AIM §4-4-7.b.4),
    /// and only a named runway can be voided by a later runway change — so arming is refused outright
    /// when neither the clearance nor the queued entry names one.
    /// </summary>
    [Fact]
    public void BareCland_BehindBareQueuedEntry_IsRejected()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC013");

        Assert.True(engine.SendCommand("TSC013", "DCT VPCOL; ERD").Success);

        var cland = engine.SendCommand("TSC013", "CLAND");
        output.WriteLine($"CLAND behind a bare ERD: success={cland.Success} — {cland.Message}");
        Assert.False(cland.Success, "A pre-issued clearance with no resolvable runway must be rejected");
        Assert.Contains("runway", cland.Message, StringComparison.OrdinalIgnoreCase);

        var ac = engine.FindAircraft("TSC013");
        Assert.NotNull(ac);
        Assert.Null(ac.Pattern.PendingLandingClearance);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // queued entry NOT wiped by the rejection
    }

    /// <summary>A bare CLAND adopts the runway the queued entry names.</summary>
    [Fact]
    public void BareCland_BehindQueuedErd_AdoptsQueuedEntryRunway()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC004");

        Assert.True(engine.SendCommand("TSC004", "DCT VPCOL; ERD 28R").Success);
        Assert.True(engine.SendCommand("TSC004", "CLAND").Success);

        var ac = engine.FindAircraft("TSC004");
        Assert.NotNull(ac);
        Assert.Equal("28R", ac.Pattern.PendingLandingClearance?.RunwayId);
    }

    /// <summary>
    /// COPT shares the identical gap. Cleared for the option builds a touch-and-go terminal, so this also
    /// proves the pre-arm reaches the touchAndGo derivation rather than only stamping LandingClearance.
    /// </summary>
    [Fact]
    public void Copt_BehindQueuedErd_BuildsTouchAndGoTerminal()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC005");

        Assert.True(engine.SendCommand("TSC005", "DCT VPCOL; ERD 28R").Success);
        var copt = engine.SendCommand("TSC005", "COPT");
        output.WriteLine($"COPT: success={copt.Success} — {copt.Message}");
        Assert.True(copt.Success, $"COPT behind a queued ERD should be accepted: {copt.Message}");

        Assert.True(engine.SendCommand("TSC005", "ERD 28R").Success);

        var ac = engine.FindAircraft("TSC005");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.Equal(ClearanceType.ClearedForOption, ac.Phases.LandingClearance);
        Assert.Contains(ac.Phases.Phases, p => p is TouchAndGoPhase);
    }

    /// <summary>
    /// SG and LA need more than the touch-and-go terminal PatternBuilder knows how to build — the
    /// consumption path has to swap in the exact terminal phase the clearance names.
    /// </summary>
    [Theory]
    [InlineData("SG", ClearanceType.ClearedStopAndGo, typeof(StopAndGoPhase))]
    [InlineData("LA", ClearanceType.ClearedLowApproach, typeof(LowApproachPhase))]
    public void OptionClearance_BehindQueuedErd_BuildsNamedTerminal(string verb, ClearanceType expected, Type terminal)
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        var callsign = $"TSC{verb}";
        SpawnAirborneOverOak(engine, callsign);

        Assert.True(engine.SendCommand(callsign, "DCT VPCOL; ERD 28R").Success);
        var clearance = engine.SendCommand(callsign, verb);
        output.WriteLine($"{verb}: success={clearance.Success} — {clearance.Message}");
        Assert.True(clearance.Success, $"{verb} behind a queued ERD should be accepted: {clearance.Message}");

        Assert.True(engine.SendCommand(callsign, "ERD 28R").Success);

        var ac = engine.FindAircraft(callsign);
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.Equal(expected, ac.Phases.LandingClearance);
        Assert.Contains(ac.Phases.Phases, p => p.GetType() == terminal);
    }

    /// <summary>
    /// TG MRT carries a pattern direction, which lives on AircraftPattern rather than the PhaseList, so
    /// it is applied at arm time rather than carried in the pending record.
    /// </summary>
    [Fact]
    public void TgMrt_BehindQueuedErd_SetsPersistentTrafficDirection()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC006");

        Assert.True(engine.SendCommand("TSC006", "DCT VPCOL; ERD 28R").Success);
        var tg = engine.SendCommand("TSC006", "TG MRT");
        output.WriteLine($"TG MRT: success={tg.Success} — {tg.Message}");
        Assert.True(tg.Success, $"TG MRT behind a queued ERD should be accepted: {tg.Message}");

        var ac = engine.FindAircraft("TSC006");
        Assert.NotNull(ac);
        Assert.Equal(PatternDirection.Right, ac.Pattern.TrafficDirection);
        Assert.Equal(ClearanceType.ClearedTouchAndGo, ac.Pattern.PendingLandingClearance?.Clearance);
    }

    /// <summary>
    /// Regression: with nothing queued to attach to, an airborne no-phase CLAND keeps its ordinary
    /// rejection. This proves the reroute is scoped to the queued-entry case and did not widen the
    /// clearance verbs generally.
    /// </summary>
    [Fact]
    public void Cland_NothingQueued_StillRejected()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC007");

        var cland = engine.SendCommand("TSC007", "CLAND");
        output.WriteLine($"CLAND (nothing queued): success={cland.Success} — {cland.Message}");
        Assert.False(cland.Success, "CLAND with no approach, no follow and nothing queued must still be rejected");
    }

    /// <summary>
    /// A pre-issued clearance names a runway, so an entry that finally builds for a different runway
    /// voids it and warns the RPO rather than auto-landing on a runway nobody cleared. This is also what
    /// makes an orphaned pre-arm safe when a vector drops the queued entry.
    /// </summary>
    [Fact]
    public void Cland_ThenEntryForDifferentRunway_VoidsClearanceAndWarns()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC008");

        Assert.True(engine.SendCommand("TSC008", "DCT VPCOL; ERD 28R").Success);
        Assert.True(engine.SendCommand("TSC008", "CLAND").Success);

        // The controller changes their mind and enters the pattern for a different runway.
        Assert.True(engine.SendCommand("TSC008", "ERD 30").Success);

        var ac = engine.FindAircraft("TSC008");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.Null(ac.Phases.LandingClearance);
        Assert.Null(ac.Pattern.PendingLandingClearance);
        Assert.Contains(ac.PendingWarnings, w => w.Contains("28R"));
    }

    /// <summary>
    /// The trailing form in a single transmission: <c>DCT VPCOL; ERD 28R; CLAND</c>. Enqueued as a third
    /// block the CLAND would strand forever (UpdateCommandQueue short-circuits once the entry installs a
    /// phase), so it is pulled out of the enqueue set and pre-issued against the compound's own entry.
    /// </summary>
    [Fact]
    public void TrailingClandInCompound_ArmsAndApplies()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC009");

        var compound = engine.SendCommand("TSC009", "DCT VPCOL; ERD 28R; CLAND");
        output.WriteLine($"DCT VPCOL; ERD 28R; CLAND: success={compound.Success} — {compound.Message}");
        Assert.True(compound.Success, compound.Message);

        var ac = engine.FindAircraft("TSC009");
        Assert.NotNull(ac);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Pattern.PendingLandingClearance?.Clearance);
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // the queued ERD survives

        Assert.True(engine.SendCommand("TSC009", "ERD 28R").Success);

        ac = engine.FindAircraft("TSC009");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);
        Assert.Contains(ac.Phases.Phases, p => p is LandingPhase);
    }

    /// <summary>
    /// The other same-transmission shape: the entry is the FIRST block, so it applies immediately and
    /// installs a phase. The trailing CLAND then has a PhaseList to clear against and is applied through
    /// the tower path — it must not be left queued behind the phase (where it would never fire).
    /// </summary>
    [Fact]
    public void TrailingClandAfterImmediateEntry_AppliesInPlace()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC012");

        var compound = engine.SendCommand("TSC012", "ERD 28R; CLAND");
        output.WriteLine($"ERD 28R; CLAND: success={compound.Success} — {compound.Message}");
        Assert.True(compound.Success, compound.Message);

        var ac = engine.FindAircraft("TSC012");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);
        Assert.Equal("28R", ac.Phases.ClearedRunwayId);
        Assert.Contains(ac.Phases.Phases, p => p is LandingPhase);
        Assert.DoesNotContain(ac.Queue.Blocks, b => !b.IsApplied); // nothing stranded behind the phase
    }

    /// <summary>
    /// The pre-issued clearance must survive a snapshot round-trip (bug bundles, replay, rewind). This
    /// also exercises the restore path where <c>CommandBlock.ParsedCommands</c> is null and the queued
    /// entry's runway can only be recovered by re-parsing the block's SourceCommandText.
    /// </summary>
    [Fact]
    public void Cland_BehindQueuedErd_SurvivesSnapshotRoundTrip()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC010");

        Assert.True(engine.SendCommand("TSC010", "DCT VPCOL; ERD 28R").Success);
        Assert.True(engine.SendCommand("TSC010", "CLAND").Success);

        var ac = engine.FindAircraft("TSC010");
        Assert.NotNull(ac);

        var restored = AircraftState.FromSnapshot(ac.ToSnapshot(), null);
        Assert.Equal(ClearanceType.ClearedToLand, restored.Pattern.PendingLandingClearance?.Clearance);
        Assert.Equal("28R", restored.Pattern.PendingLandingClearance?.RunwayId);
        Assert.All(restored.Queue.Blocks, b => Assert.Null(b.ParsedCommands));

        engine.World.RemoveAircraft("TSC010");
        engine.World.AddAircraft(restored);

        Assert.True(engine.SendCommand("TSC010", "ERD 28R").Success);

        ac = engine.FindAircraft("TSC010");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.Equal(ClearanceType.ClearedToLand, ac.Phases.LandingClearance);
        Assert.Equal("28R", ac.Phases.ClearedRunwayId);
    }

    /// <summary>CLC retracts a pre-issued clearance that has not been applied to a circuit yet.</summary>
    [Fact]
    public void Clc_WithOnlyPendingClearance_CancelsIt()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSC011");

        Assert.True(engine.SendCommand("TSC011", "DCT VPCOL; ERD 28R").Success);
        Assert.True(engine.SendCommand("TSC011", "CLAND").Success);

        var clc = engine.SendCommand("TSC011", "CLC");
        output.WriteLine($"CLC: success={clc.Success} — {clc.Message}");
        Assert.True(clc.Success, $"CLC should cancel a pre-issued clearance: {clc.Message}");

        var ac = engine.FindAircraft("TSC011");
        Assert.NotNull(ac);
        Assert.Null(ac.Pattern.PendingLandingClearance);
    }

    private static void SpawnAirborneOverOak(SimulationEngine engine, string callsign)
    {
        // A few miles east of OAK 28R on the right downwind side, slow VFR piston.
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "DA62",
            Position = new LatLon(37.66, -122.16),
            TrueHeading = new TrueHeading(280),
            TrueTrack = new TrueHeading(280),
            Altitude = 2000,
            IndicatedAirspeed = 110,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KOAK",
                Destination = "KOAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(2000),
                CruiseSpeed = 150,
            },
        };
        engine.World.AddAircraft(ac);
    }
}
