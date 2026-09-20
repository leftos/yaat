using System.Text.Json;
using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// The ASDE-X Safety Logic detector pass on the bare engine: <see cref="SimulationEngine.TickAsdexAlerts"/> reads the
/// scenario's pushed configuration, runs the stateless detector over the world, and diffs the result against the
/// alerts the scenario is already holding. Only the diff leaves the engine, through
/// <see cref="IStateChangeConsumer.OnAsdexAlertsChanged"/>, because CRC's alert topic is additive with an explicit
/// delete. The standing set is snapshotted scenario state, so a replay, a rewind and a session restore all land on
/// the alert picture the live room had rather than re-announcing everything that was already up.
/// </summary>
public class AsdexAlertStepTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AsdexAlertStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    /// <summary>A point inside <see cref="RunwayArea"/> — where the scenario's one aircraft is put.</summary>
    private static readonly LatLon OnTheRunway = new(37.7213, -122.2208);

    /// <summary>Well clear of <see cref="RunwayArea"/>, on the field but off every configured footprint.</summary>
    private static readonly LatLon OffTheRunway = new(37.7350, -122.2100);

    /// <summary>The footprint ring a CRC surface display pushes for the runway: a box around <see cref="OnTheRunway"/>.</summary>
    private static readonly IReadOnlyList<LatLon> RunwayArea =
    [
        new(37.7208, -122.2215),
        new(37.7218, -122.2215),
        new(37.7218, -122.2201),
        new(37.7208, -122.2201),
    ];

    private static AsdexSafetyLogicConfig ClosedRunways(params string[] runwayIds) =>
        new([.. runwayIds.Select(id => new AsdexRunwayConfig(id, RunwayArea, IsClosed: true))], "WEST-PLAN", []);

    /// <summary>
    /// Puts the scenario's one aircraft onto the configured footprint, aligned with the runway axis (28R is 280
    /// magnetic; OAK's variation is about 13°E, and the detector's tolerance is ±35° about the axis either way).
    /// </summary>
    private static AircraftState PutOnTheRunway(SimulationEngine engine)
    {
        AircraftState ac = engine.World.GetSnapshot()[0];
        ac.Position = OnTheRunway;
        ac.TrueHeading = new TrueHeading(293);
        ac.IsOnGround = true;
        ac.Altitude = 9;
        return ac;
    }

    [Fact]
    public void Tick_WithAClosedRunwayAndAnAircraftOnIt_RaisesTheAlert_AndTellsTheConsumer()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", ClosedRunways("28R")));
        AircraftState ac = PutOnTheRunway(engine);

        // Through the whole second, not the body alone: the spine entry is what has to deliver this on every run kind.
        engine.RunSecond(spine);

        AsdexSafetyAlert standing = Assert.Single(engine.Scenario!.ActiveAsdexAlerts.Values);
        Assert.Equal(AsdexAlertKind.ClosedRunway, standing.Kind);
        Assert.Equal([ac.Callsign], standing.Callsigns);
        Assert.Equal(["28R", ac.Callsign, "CLOSED RWY"], standing.MessageLines);

        (IReadOnlyList<AsdexSafetyAlert> newAlerts, IReadOnlyList<string> clearedAlertIds) = Assert.Single(spine.AsdexAlertChanges);
        Assert.Equal(standing.Id, Assert.Single(newAlerts).Id);
        Assert.Empty(clearedAlertIds);

        // A second tick with the same picture is silent: the id is the display's identity for the alert, so a
        // standing alert is not re-announced.
        engine.RunSecond(spine);
        Assert.Single(spine.AsdexAlertChanges);
        Assert.Single(engine.Scenario!.ActiveAsdexAlerts);
    }

    [Fact]
    public void Tick_WhenTheConflictEnds_ClearsTheAlert_AndTellsTheConsumerItsId()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", ClosedRunways("28R")));
        AircraftState ac = PutOnTheRunway(engine);

        engine.TickAsdexAlerts(spine);
        string raisedId = Assert.Single(engine.Scenario!.ActiveAsdexAlerts.Keys);

        ac.Position = OffTheRunway;
        engine.TickAsdexAlerts(spine);

        Assert.Empty(engine.Scenario!.ActiveAsdexAlerts);
        Assert.Equal(2, spine.AsdexAlertChanges.Count);

        (IReadOnlyList<AsdexSafetyAlert> newAlerts, IReadOnlyList<string> clearedAlertIds) = spine.AsdexAlertChanges[1];
        Assert.Empty(newAlerts);
        Assert.Equal([raisedId], clearedAlertIds);
    }

    [Fact]
    public void AReaderHoldingTheStandingSet_IsNotDisturbedByALaterTick()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", ClosedRunways("28R")));
        AircraftState ac = PutOnTheRunway(engine);
        engine.TickAsdexAlerts(spine);

        // What the CRC initial-data build does off the tick thread: take the reference once, enumerate it later.
        IReadOnlyDictionary<string, AsdexSafetyAlert> captured = engine.Scenario!.ActiveAsdexAlerts;
        string raisedId = Assert.Single(captured.Keys);

        // The tick that clears the alert must not reach into what the reader is holding.
        ac.Position = OffTheRunway;
        engine.TickAsdexAlerts(spine);

        AsdexSafetyAlert held = Assert.Single(captured.Values);
        Assert.Equal(raisedId, held.Id);
        Assert.Equal(AsdexAlertKind.ClosedRunway, held.Kind);
        Assert.Empty(engine.Scenario!.ActiveAsdexAlerts);
    }

    [Fact]
    public void Tick_WithNoConfig_RaisesNothing()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        PutOnTheRunway(engine);

        // No display has pushed a safety-logic configuration, so there are no runway footprints to alert on — the
        // detector is not even reached, and the aircraft sitting where a closed runway would be is not an alert.
        Assert.Null(engine.Scenario!.AsdexSafetyLogicConfig);
        engine.RunSecond(spine);

        Assert.Empty(engine.Scenario!.ActiveAsdexAlerts);
        Assert.Empty(spine.AsdexAlertChanges);
    }

    [Fact]
    public void Alerts_RoundTripThroughASnapshot()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", ClosedRunways("28R")));
        PutOnTheRunway(engine);
        engine.TickAsdexAlerts(spine);

        AsdexSafetyAlert raised = Assert.Single(engine.Scenario!.ActiveAsdexAlerts.Values);

        // Through the serializer a recording writes the snapshot with, not just the in-memory DTO: the nested
        // callsign and message-line lists are the part a plain object graph would hide.
        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        StateSnapshotDto reread = JsonSerializer.Deserialize<StateSnapshotDto>(JsonSerializer.Serialize(snapshot))!;

        engine.Scenario!.ActiveAsdexAlerts = engine.Scenario!.ActiveAsdexAlerts.Clear();
        engine.RestoreFromSnapshot(reread);

        AsdexSafetyAlert restored = Assert.Single(engine.Scenario!.ActiveAsdexAlerts.Values);
        Assert.Equal(raised.Id, restored.Id);
        Assert.Equal(raised.Kind, restored.Kind);
        Assert.Equal(raised.RunwayIds, restored.RunwayIds);
        Assert.Equal(raised.Callsigns, restored.Callsigns);
        Assert.Equal(raised.MessageLines, restored.MessageLines);
        Assert.Equal(raised.PlayAuralAlert, restored.PlayAuralAlert);

        // And the restored set is the identity the next diff works against: nothing is re-announced.
        engine.TickAsdexAlerts(spine);
        Assert.Single(spine.AsdexAlertChanges);
    }

    [Fact]
    public void Alerts_AbsentFromASnapshot_RestoreAsEmpty()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        Assert.Null(snapshot.Scenario.ActiveAsdexAlerts);

        var spine = new SpineCapturingHost(engine);
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", ClosedRunways("28R")));
        PutOnTheRunway(engine);
        engine.TickAsdexAlerts(spine);
        Assert.NotEmpty(engine.Scenario!.ActiveAsdexAlerts);

        // A rewind onto a second before the alert went up: the set is replaced, never merged, so the alert that the
        // rewind has undone is gone and the next tick decides afresh whether the restored world still warrants it.
        engine.RestoreFromSnapshot(snapshot);
        Assert.Empty(engine.Scenario!.ActiveAsdexAlerts);
    }

    [Fact]
    public void ScenarioUnload_ClearsTheAlerts()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var spine = new SpineCapturingHost(engine);
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", ClosedRunways("28R")));
        PutOnTheRunway(engine);
        engine.TickAsdexAlerts(spine);
        Assert.NotEmpty(engine.Scenario!.ActiveAsdexAlerts);

        // The engine-level unload/reload: a fresh scenario replaces the state the alerts live on, and the
        // configuration they were raised against goes with it.
        engine.LoadScenario(AiTestFixture.ParkedAtOak, 7, new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc));

        Assert.Empty(engine.Scenario!.ActiveAsdexAlerts);
    }

    [Fact]
    public void AlertOrder_IsDeterministicAcrossRuns()
    {
        if ((Engine() is not { } first) || (Engine() is not { } second))
        {
            return;
        }

        // Two closed runways sharing one footprint: 10L and 28R are the same axis, so the one aircraft claims both
        // and a single tick raises two alerts. The raise order is the configuration's; the clear order is the
        // standing set's, which is ordinal by id — "ClosedRunway|10L|…" before "ClosedRunway|28R|…".
        List<string> firstRun = RunPayloads(first);
        Assert.Equal(firstRun, RunPayloads(second));

        Assert.Equal(
            ["new=ClosedRunway|28R|N152SP,ClosedRunway|10L|N152SP cleared=", "new= cleared=ClosedRunway|10L|N152SP,ClosedRunway|28R|N152SP"],
            firstRun
        );
    }

    /// <summary>Raises the two-runway alert pair and then clears it, returning every consumer payload as text.</summary>
    private static List<string> RunPayloads(SimulationEngine engine)
    {
        var spine = new SpineCapturingHost(engine);
        engine.Scenario!.ActiveAsdexAlerts = engine.Scenario!.ActiveAsdexAlerts.Clear();
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", ClosedRunways("28R", "10L")));

        AircraftState ac = PutOnTheRunway(engine);
        engine.TickAsdexAlerts(spine);

        ac.Position = OffTheRunway;
        engine.TickAsdexAlerts(spine);

        return
        [
            .. spine.AsdexAlertChanges.Select(change =>
                $"new={string.Join(",", change.NewAlerts.Select(a => a.Id))} cleared={string.Join(",", change.ClearedAlertIds)}"
            ),
        ];
    }
}
