using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A controller sets up a pattern arrival in two transmissions:
/// <c>DCT VPCOL; ERD 28R</c> (aircraft flies direct VPCOL, then enters right
/// downwind 28R once it reaches the fix), and — while still en route to VPCOL —
/// a separate <c>EXT DOWNWIND</c> / <c>SA</c> / <c>MNA</c>. The pattern-entry has
/// not fired yet (no DownwindPhase exists), so the modifier must be pre-armed
/// against the queued entry and applied when the entry finally builds its circuit.
/// The queued ERD must survive the second transmission (it must not be wiped by the
/// None-dimension queue-clear fast path).
/// </summary>
public class QueuedPatternModifierTests(ITestOutputHelper output)
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
    /// Headline: EXT DOWNWIND issued while an ERD sits queued behind DCT VPCOL must
    /// be accepted (today it is rejected with "requires an active runway assignment"),
    /// and the queued ERD block must be preserved.
    /// </summary>
    [Fact]
    public void ExtDownwind_BehindQueuedErd_IsAcceptedAndPreservesEntry()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST001");

        var setup = engine.SendCommand("TST001", "DCT VPCOL; ERD 28R");
        Assert.True(setup.Success, setup.Message);

        var ac = engine.FindAircraft("TST001");
        Assert.NotNull(ac);
        Assert.Null(ac.Phases?.CurrentPhase); // ERD queued, not yet fired — free-flying to VPCOL
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // the queued ERD block

        var ext = engine.SendCommand("TST001", "EXT DOWNWIND");
        output.WriteLine($"EXT DOWNWIND: success={ext.Success} — {ext.Message}");
        Assert.True(ext.Success, $"EXT DOWNWIND behind a queued ERD should be accepted: {ext.Message}");

        ac = engine.FindAircraft("TST001");
        Assert.NotNull(ac);
        Assert.Null(ac.Phases?.CurrentPhase); // still en route — EXT did not force a phase
        Assert.Contains(ac.Queue.Blocks, b => !b.IsApplied); // queued ERD NOT wiped by EXT
    }

    /// <summary>
    /// End-to-end: after arming EXT DOWNWIND behind the queued ERD, the downwind the
    /// entry builds must come out already extended. The queued entry is fired here by
    /// re-issuing ERD 28R (TryEnterPattern is the single consumption site, whether the
    /// entry fires from the queue or as a fresh command).
    /// </summary>
    [Fact]
    public void ExtDownwind_BehindQueuedErd_ExtendsDownwindWhenEntryBuilds()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST002");

        Assert.True(engine.SendCommand("TST002", "DCT VPCOL; ERD 28R").Success);
        Assert.True(engine.SendCommand("TST002", "EXT DOWNWIND").Success, "EXT DOWNWIND behind a queued ERD should be accepted");

        // Fire the entry: builds the circuit and consumes the pre-armed extend.
        Assert.True(engine.SendCommand("TST002", "ERD 28R").Success);

        var ac = engine.FindAircraft("TST002");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        var downwind = ac.Phases.Phases.OfType<DownwindPhase>().FirstOrDefault();
        Assert.NotNull(downwind);
        Assert.True(downwind.IsExtended, "The pre-armed EXT DOWNWIND must extend the downwind the entry builds");
    }

    /// <summary>
    /// SA (make short approach) shares the identical gap and must also pre-arm.
    /// </summary>
    [Fact]
    public void ShortApproach_BehindQueuedErd_ArmsBuiltDownwind()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST003");

        Assert.True(engine.SendCommand("TST003", "DCT VPCOL; ERD 28R").Success);
        Assert.True(engine.SendCommand("TST003", "SA").Success, "SA behind a queued ERD should be accepted");

        Assert.True(engine.SendCommand("TST003", "ERD 28R").Success);

        var ac = engine.FindAircraft("TST003");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        var downwind = ac.Phases.Phases.OfType<DownwindPhase>().FirstOrDefault();
        Assert.NotNull(downwind);
        Assert.True(downwind.ShortApproachArmed, "The pre-armed SA must arm the downwind the entry builds");
    }

    /// <summary>
    /// The triggered entry form (AT VPCOL ERD 28R) is also detected in the queue.
    /// </summary>
    [Fact]
    public void ExtDownwind_BehindTriggeredErd_IsAccepted()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST004");

        Assert.True(engine.SendCommand("TST004", "AT VPCOL ERD 28R").Success);

        var ext = engine.SendCommand("TST004", "EXT DOWNWIND");
        output.WriteLine($"EXT DOWNWIND: success={ext.Success} — {ext.Message}");
        Assert.True(ext.Success, $"EXT behind a triggered ERD should be accepted: {ext.Message}");
    }

    /// <summary>
    /// Regression: EXT DOWNWIND with no pattern-entry queued or active is still rejected.
    /// </summary>
    [Fact]
    public void ExtDownwind_NothingQueued_IsRejected()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST005");

        var ext = engine.SendCommand("TST005", "EXT DOWNWIND");
        output.WriteLine($"EXT DOWNWIND (nothing queued): success={ext.Success} — {ext.Message}");
        Assert.False(ext.Success, "EXT DOWNWIND with nothing to extend should be rejected");
    }

    /// <summary>
    /// Regression: EXT DOWNWIND after ERD has already fired (aircraft on PatternEntry,
    /// DownwindPhase pending) still arms the live pending downwind — the existing layer-1 path.
    /// </summary>
    [Fact]
    public void ExtDownwind_AfterErdFired_StillArmsPendingDownwind()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TST006");

        Assert.True(engine.SendCommand("TST006", "ERD 28R").Success);
        var ac = engine.FindAircraft("TST006");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);

        var ext = engine.SendCommand("TST006", "EXT DOWNWIND");
        Assert.True(ext.Success, ext.Message);

        ac = engine.FindAircraft("TST006");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        var downwind = ac.Phases.Phases.OfType<DownwindPhase>().FirstOrDefault();
        Assert.NotNull(downwind);
        Assert.True(downwind.IsExtended);
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
