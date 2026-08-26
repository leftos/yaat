using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A queued <see cref="Commands.CommandBlock"/> carries its effect in an <c>ApplyAction</c> closure that
/// is deliberately not snapshot-serialized. Nothing re-derives it on restore (only track blocks recover,
/// via <c>SimulationEngine.ResolveTrackCommandsForBlock</c>), so after a rewind or a bug-bundle replay a
/// queued block reaches its turn, marks itself applied, and silently does nothing — the controller's
/// queued instruction vanishes with no warning.
///
/// These tests fly the aircraft to the fix so the queue actually advances, rather than re-issuing the
/// queued command by hand.
/// </summary>
public class RestoredQueuedBlockTests(ITestOutputHelper output)
{
    private SimulationEngine? BuildEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("FlightPhysics", LogLevel.Debug).InitializeSimLog();

        // TickOneSecond early-returns with no scenario loaded; give it a minimal one.
        return new SimulationEngine(new TestAirportGroundData())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test",
                ScenarioName = "Test",
                RngSeed = 1,
                OriginalScenarioJson = "{}",
            },
        };
    }

    /// <summary>
    /// Baseline: without a restore, `DCT VPCOL; FH 090` turns the aircraft to 090 once it reaches VPCOL.
    /// Pins that the fly-to-the-fix harness below actually advances the queue.
    /// </summary>
    [Fact]
    public void QueuedBlock_WithoutRestore_AppliesAtTheFix()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSR001");
        Assert.True(engine.SendCommand("TSR001", "DCT VPCOL; FH 090").Success);

        Assert.True(FlyUntilQueueDrains(engine, "TSR001"), "aircraft never reached VPCOL");

        var ac = engine.FindAircraft("TSR001");
        Assert.NotNull(ac);
        Assert.Equal(90, ac.Targets.AssignedMagneticHeading?.ToDisplayInt());
    }

    /// <summary>
    /// The bug: the same queue, round-tripped through a snapshot before the fix is reached, drops the
    /// heading assignment entirely. The block is marked applied — so no warning fires and nothing is
    /// retried — but `ApplyAction` was null and the command never ran.
    /// </summary>
    [Fact]
    public void QueuedBlock_AfterSnapshotRestore_StillAppliesAtTheFix()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSR002");
        Assert.True(engine.SendCommand("TSR002", "DCT VPCOL; FH 090").Success);

        RestoreThroughSnapshot(engine, "TSR002");

        Assert.True(FlyUntilQueueDrains(engine, "TSR002"), "aircraft never reached VPCOL");

        var ac = engine.FindAircraft("TSR002");
        Assert.NotNull(ac);
        Assert.Equal(90, ac.Targets.AssignedMagneticHeading?.ToDisplayInt());
    }

    /// <summary>
    /// The reported shape: a pattern entry queued behind a direct-to must still build its circuit after a
    /// restore. This is the case a hybrid replay hits whenever the snapshot is taken after the setup
    /// compound was issued.
    /// </summary>
    [Fact]
    public void QueuedPatternEntry_AfterSnapshotRestore_BuildsItsCircuit()
    {
        var engine = BuildEngine();
        if (engine is null)
        {
            return;
        }

        SpawnAirborneOverOak(engine, "TSR003");
        Assert.True(engine.SendCommand("TSR003", "DCT VPCOL; ERD 28R").Success);

        RestoreThroughSnapshot(engine, "TSR003");

        Assert.True(FlyUntilQueueDrains(engine, "TSR003"), "aircraft never reached VPCOL");

        var ac = engine.FindAircraft("TSR003");
        Assert.NotNull(ac);
        Assert.NotNull(ac.Phases);
        Assert.Equal("28R", ac.Phases.AssignedRunway?.Designator);
    }

    /// <summary>Replaces the live aircraft with a snapshot round-trip of itself, as a rewind would.</summary>
    private static void RestoreThroughSnapshot(SimulationEngine engine, string callsign)
    {
        var live = engine.FindAircraft(callsign);
        Assert.NotNull(live);

        var restored = AircraftState.FromSnapshot(live.ToSnapshot(), null);
        Assert.All(restored.Queue.Blocks, b => Assert.Null(b.ParsedCommands));

        engine.World.RemoveAircraft(callsign);
        engine.World.AddAircraft(restored);
    }

    /// <summary>
    /// Ticks until every queued block has been applied, or the budget runs out. Event-bounded: it breaks
    /// as soon as the queue drains, so the budget only bounds the failure case.
    /// </summary>
    private bool FlyUntilQueueDrains(SimulationEngine engine, string callsign)
    {
        for (int t = 1; t <= 900; t++)
        {
            engine.TickOneSecond();
            var ac = engine.FindAircraft(callsign);
            if (ac is null)
            {
                return false;
            }

            if (ac.Queue.Blocks.TrueForAll(b => b.IsApplied))
            {
                output.WriteLine($"{callsign}: queue drained at t={t}");
                return true;
            }
        }

        return false;
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
