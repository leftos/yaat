using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Soak;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// The <c>ADD</c> and <c>TAXIALL</c> arms. An ADD is derived on every run kind — the generator and the CID draw from
/// the shared RNG and the beacon pool exactly as live did, so both advance in lockstep — and the snapshot the live run
/// baked onto the record is the authority when the derivation disagrees with it. A TAXIALL taxis every aircraft at
/// parking through the dispatcher's TAXI arm.
/// </summary>
public class AddAndTaxiAllArmTests
{
    private const string Add = "ADD V S P @NEW1";

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AddAndTaxiAllArmTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    private static ActionInput Fresh(string command) => new("", command, "conn-1", "XX", Baked: null);

    private static RecordedCommand Recorded(string command) => new(0, "", command, "XX", "conn-1");

    private static string Json(AircraftSnapshotDto dto) => JsonSerializer.Serialize(dto);

    [Fact]
    public void Issue_SpawnsTheAircraft_AndBakesItsSnapshotOntoTheRecord()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var outcome = engine.Actions.Issue(Fresh(Add));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal(new ActionTrace(RecordedCommandKind.AddAircraft, ActionScope.Global, IsHostSlot: false), outcome.Trace);
        var spawned = Assert.Single(engine.World.GetSnapshot(), ac => ac.Callsign != AiTestFixture.Callsign);
        Assert.IsType<AtParkingPhase>(spawned.Phases?.CurrentPhase);
        Assert.Contains(spawned.Callsign, outcome.Result.Message);
        var record = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));
        Assert.NotNull(record.SpawnedAircraft);
        Assert.Equal(Json(spawned.ToSnapshot()), Json(record.SpawnedAircraft));
    }

    [Fact]
    public void Apply_DerivesTheSameAircraft_AndAdvancesTheSharedRngAndBeaconPool_WithoutABakedSnapshot()
    {
        if (Engine() is not { } live || Engine() is not { } replay)
        {
            return;
        }

        var record = live.Actions.Issue(Fresh(Add)).ToRecord!;
        var callsign = record.SpawnedAircraft!.Callsign;

        var outcome = replay.Actions.Apply(record with { SpawnedAircraft = null });

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        var replayed = replay.FindAircraft(callsign);
        Assert.NotNull(replayed);
        Assert.Equal(Json(live.FindAircraft(callsign)!.ToSnapshot()), Json(replayed.ToSnapshot()));
        Assert.Equal(live.World.Rng.GetState(), replay.World.Rng.GetState());
        Assert.Equal(live.BeaconCodePool.NextCandidate, replay.BeaconCodePool.NextCandidate);
        Assert.Empty(replay.Scenario!.ActionLog);
    }

    [Fact]
    public void Apply_TheBakedSnapshotWins_WhenTheDerivationDisagrees_AndReservesItsBeacon()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        if (Engine() is not { } live || Engine() is not { } replay)
        {
            return;
        }

        var record = live.Actions.Issue(Fresh(Add)).ToRecord!;
        var derived = record.SpawnedAircraft!;
        // What the live session would have banked had its world differed: another callsign and beacon code.
        var recordedAircraft = AircraftState.FromSnapshot(derived, live.World.GroundLayout);
        recordedAircraft.Callsign = "REC1";
        recordedAircraft.Transponder.AssignCode(4321, null, null);
        var recorded = recordedAircraft.ToSnapshot();

        var outcome = replay.Actions.Apply(record with { SpawnedAircraft = recorded });

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal(Json(recorded), Json(replay.FindAircraft("REC1")!.ToSnapshot()));
        Assert.Null(replay.FindAircraft(derived.Callsign));
        Assert.True(replay.BeaconCodePool.IsAssigned(4321));
        Assert.False(replay.BeaconCodePool.IsAssigned(derived.Transponder.AssignedCode));
        // The derivation still ran, so the shared RNG stands where live's did.
        Assert.Equal(live.World.Rng.GetState(), replay.World.Rng.GetState());
        var warning = Assert.Single(tap.Drain(), r => r.Message.Contains("replay-fidelity", StringComparison.Ordinal));
        Assert.Contains("REC1", warning.Message);
    }

    [Fact]
    public void Apply_TheBakedSnapshotWins_WhenTheDerivationProducesNothing()
    {
        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        if (Engine() is not { } live || Engine() is not { } replay)
        {
            return;
        }

        var record = live.Actions.Issue(Fresh(Add)).ToRecord!;
        var recorded = record.SpawnedAircraft!;

        // The same record against a layout where its parking no longer resolves: the generator refuses, the recording still holds the aircraft.
        var outcome = replay.Actions.Apply(record with { Command = "ADD V S P @NOSUCHSPOT" });

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        var spawned = Assert.Single(replay.World.GetSnapshot(), ac => ac.Callsign != AiTestFixture.Callsign);
        Assert.Equal(Json(recorded), Json(spawned.ToSnapshot()));
        var warning = Assert.Single(tap.Drain(), r => r.Message.Contains("replay-fidelity", StringComparison.Ordinal));
        Assert.Contains("derived no aircraft", warning.Message);
    }

    [Fact]
    public void Apply_TaxiAll_TaxisEveryAircraftAtParking()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var parked = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.IsType<AtParkingPhase>(parked.Phases?.CurrentPhase);

        // A parking destination: the empty-path TAXI a TAXIALL issues routes to a parking from anywhere, while a bare
        // runway destination is adjacent-only (issue #393) and SIG1 is not adjacent to any OAK runway.
        var outcome = engine.Actions.Apply(Recorded("TAXIALL @NEW1"));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("TAXIALL: 1 aircraft taxied", outcome.Result.Message);
        Assert.Equal(new ActionTrace(RecordedCommandKind.TaxiAll, ActionScope.Global, IsHostSlot: false), outcome.Trace);
        Assert.IsType<TaxiingPhase>(parked.Phases?.CurrentPhase);
        Assert.Empty(engine.Scenario!.ActionLog);
    }
}
