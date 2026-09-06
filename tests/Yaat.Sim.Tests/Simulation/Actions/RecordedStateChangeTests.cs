using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Soak;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// The derived records a CRC handler writes for state it used to change without a trace — per-TCP STARS shared
/// state, the departure clearance, the hold annotation, an ERAM keyboard entry, a CRR group — apply through the
/// router on every run kind: the Sim-owned ones on the bare engine, the room-owned CRR group through the host's
/// slot. A record whose aircraft is gone is refused with a replay-fidelity warning, the way a command whose verdict
/// changed is; a fresh derived record is recorded only when it applied.
/// </summary>
public class RecordedStateChangeTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public RecordedStateChangeTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var scenario = engine.Scenario!;
        scenario.StudentPosition = TrackOwner.CreateStars("OAK_TWR", "NCT", 3, "O");
        scenario.StudentTcp = TrackResolver.FindTcpByCode(scenario, "3O")!;
        return engine;
    }

    private static SharedStateDto Shared(bool recentlyAccepted) =>
        new()
        {
            ForceFdb = true,
            IsHighlighted = false,
            LeaderDirection = 7,
            WasPreviouslyOwned = true,
            TpaType = 1,
            TpaSize = 3.5,
            IsRecentlyAcceptedIncomingPointout = recentlyAccepted,
        };

    [Fact]
    public void SharedState_WritesThePositionsEntry_AndTheDismissalClearsTheAcceptedPointout()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var scenario = engine.Scenario!;
        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        var recipient = scenario.StudentTcp!;
        var sender = TrackResolver.FindTcpByCode(scenario, "4U")!;
        ac.Track.Pointout = new StarsPointout(recipient, sender) { Status = StarsPointoutStatus.Accepted };

        var applied = engine.Actions.ApplyRecorded(new RecordedStarsSharedStateChange(0, ac.Callsign, recipient.Id, Shared(recentlyAccepted: true)));

        Assert.True(applied.Success, applied.Message);
        var stored = ac.Stars.SharedState[recipient.Id];
        Assert.True(stored.ForceFdb);
        Assert.Equal(7, stored.LeaderDirection);
        Assert.Equal(3.5, stored.TpaSize);
        Assert.True(stored.IsRecentlyAcceptedIncomingPointout);
        Assert.NotNull(ac.Track.Pointout);

        engine.Actions.ApplyRecorded(new RecordedStarsSharedStateChange(1, ac.Callsign, recipient.Id, Shared(recentlyAccepted: false)));

        Assert.False(ac.Stars.SharedState[recipient.Id].IsRecentlyAcceptedIncomingPointout);
        Assert.Null(ac.Track.Pointout);
    }

    [Fact]
    public void Clearance_ReplacesTheAircraftsClearance()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Clearance.Expect = "STALE";
        var clearance = new AircraftClearanceDto
        {
            Sid = "SFO2",
            InitialAlt = "5000",
            DepFreq = "135.1",
        };

        var applied = engine.Actions.ApplyRecorded(new RecordedClearanceChange(0, ac.Callsign, clearance));

        Assert.True(applied.Success, applied.Message);
        Assert.Null(ac.Clearance.Expect);
        Assert.Equal("SFO2", ac.Clearance.Sid);
        Assert.Equal("5000", ac.Clearance.InitialAlt);
        Assert.Equal("135.1", ac.Clearance.DepFreq);
    }

    [Fact]
    public void HoldAnnotation_IsSet_AndANullRecordDeletesIt()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        var hold = new AircraftHoldAnnotationDto
        {
            Fix = "OAK",
            Direction = 3,
            Turns = 1,
            LegLength = 5,
            LegLengthInNm = true,
            Efc = 1730,
        };

        Assert.True(engine.Actions.ApplyRecorded(new RecordedHoldAnnotationChange(0, ac.Callsign, hold)).Success);
        Assert.Equal("OAK", ac.HoldAnnotation.Fix);
        Assert.Equal(3, ac.HoldAnnotation.Direction);
        Assert.Equal(5, ac.HoldAnnotation.LegLength);
        Assert.True(ac.HoldAnnotation.LegLengthInNm);
        Assert.Equal(1730, ac.HoldAnnotation.Efc);

        Assert.True(engine.Actions.ApplyRecorded(new RecordedHoldAnnotationChange(1, ac.Callsign, null)).Success);
        Assert.Null(ac.HoldAnnotation.Fix);
        Assert.Equal(0, ac.HoldAnnotation.Direction);
        Assert.Null(ac.HoldAnnotation.LegLength);
        Assert.False(ac.HoldAnnotation.LegLengthInNm);
        Assert.Equal(0, ac.HoldAnnotation.Efc);
    }

    [Fact]
    public void EramEntry_ResolvesTheIdentityCode_AndAppliesThroughTheEngine()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;

        var track = engine.Actions.ApplyRecorded(new RecordedEramEntry(0, ac.Callsign, "TRACK", "3O"));
        Assert.True(track.Success, track.Message);
        Assert.Equal(engine.Scenario!.StudentPosition, ac.Track.Owner);

        var heading = engine.Actions.ApplyRecorded(new RecordedEramEntry(1, ac.Callsign, "QS 270", null));
        Assert.True(heading.Success, heading.Message);
        Assert.Equal("H270", ac.Eram.AssignedHeading);
    }

    [Fact]
    public void EramCrrGroup_ReachesTheHostSlot()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var group = new RecordedEramCrrGroup(0, "ABC", "Yellow", 37.5, -122.0);

        var applied = engine.Actions.ApplyRecorded(group, host);

        Assert.True(applied.Success, applied.Message);
        Assert.Same(group, Assert.Single(host.CrrGroups));
    }

    [Fact]
    public void ARecordForAMissingAircraft_IsRefused_WithAReplayFidelityWarning()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        var applied = engine.Actions.ApplyRecorded(new RecordedClearanceChange(0, "NOPE1", new AircraftClearanceDto()));

        Assert.False(applied.Success);
        var warning = Assert.Single(tap.Drain(), r => r.Category == "ActionRouter");
        Assert.Contains("replay-fidelity", warning.Message);
        Assert.Contains("NOPE1", warning.Message);
    }

    [Fact]
    public void AFreshDerivedRecord_IsRecordedOnlyWhenItApplied()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        var log = engine.Scenario!.ActionLog;
        var before = log.Count;

        var refused = engine.Actions.IssueDerived(new RecordedEramEntry(0, ac.Callsign, "QS 400", null));
        Assert.False(refused.Success);
        Assert.Equal(before, log.Count);

        var applied = engine.Actions.IssueDerived(new RecordedEramEntry(0, ac.Callsign, "QS 270", null));
        Assert.True(applied.Success, applied.Message);
        var recorded = Assert.IsType<RecordedEramEntry>(log[^1]);
        Assert.Equal("QS 270", recorded.Entry);
        Assert.Equal(before + 1, log.Count);
    }
}
