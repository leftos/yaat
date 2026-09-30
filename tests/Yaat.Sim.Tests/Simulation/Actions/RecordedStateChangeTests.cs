using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Simulation.Strips;
using Yaat.Sim.Soak;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// The derived records a CRC handler writes for state it used to change without a trace — per-TCP STARS shared
/// state, the departure clearance, the hold annotation, an ERAM keyboard entry, a CRR group, the ASDE-X safety-logic
/// configuration — apply through the router on every run kind, each through its own engine body, on the bare engine.
/// A record whose aircraft is gone is refused with a replay-fidelity warning, the way a command whose verdict
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

        SimulationEngine engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        SimScenarioState scenario = engine.Scenario!;
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

        SimScenarioState scenario = engine.Scenario!;
        AircraftState ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        Tcp recipient = scenario.StudentTcp!;
        Tcp sender = TrackResolver.FindTcpByCode(scenario, "4U")!;
        ac.Track.Pointout = new StarsPointout(recipient, sender) { Status = StarsPointoutStatus.Accepted };

        CommandResult applied = engine.Actions.ApplyRecorded(
            new RecordedStarsSharedStateChange(0, ac.Callsign, recipient.Id, Shared(recentlyAccepted: true))
        );

        Assert.True(applied.Success, applied.Message);
        StarsTrackSharedState stored = ac.Stars.SharedState[recipient.Id];
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

        AircraftState ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Clearance.Expect = "STALE";
        var clearance = new AircraftClearanceDto
        {
            Sid = "SFO2",
            InitialAlt = "5000",
            DepFreq = "135.1",
        };

        CommandResult applied = engine.Actions.ApplyRecorded(new RecordedClearanceChange(0, ac.Callsign, clearance));

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

        AircraftState ac = engine.FindAircraft(AiTestFixture.Callsign)!;
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
        Assert.Null(ac.HoldAnnotation.Direction);
        Assert.Null(ac.HoldAnnotation.LegLength);
        Assert.False(ac.HoldAnnotation.LegLengthInNm);
        Assert.Null(ac.HoldAnnotation.Efc);
    }

    [Fact]
    public void EramEntry_ResolvesTheIdentityCode_AndAppliesThroughTheEngine()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AircraftState ac = engine.FindAircraft(AiTestFixture.Callsign)!;

        CommandResult track = engine.Actions.ApplyRecorded(new RecordedEramEntry(0, ac.Callsign, "TRACK", "3O", null, null, null, null));
        Assert.True(track.Success, track.Message);
        Assert.Equal(engine.Scenario!.StudentPosition, ac.Track.Owner);

        CommandResult heading = engine.Actions.ApplyRecorded(new RecordedEramEntry(1, ac.Callsign, "QS 270", null, null, null, null, null));
        Assert.True(heading.Success, heading.Message);
        Assert.Equal("H270", ac.Eram.AssignedHeading);
    }

    [Fact]
    public void Qt_NoAt_AnchorsAtRecordedSweptPosition()
    {
        // The controller sees the target as last swept, up to 12 s behind the live aircraft; a QT with no @ coasts from there.
        if ((Engine() is not { } engine) || (Engine() is not { } unswept))
        {
            return;
        }

        // The whole pose is the swept one: its position, stamped at its own sweep time, on its swept track.
        AircraftState ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        LatLon swept = GeoMath.ProjectPoint(ac.Position, new TrueHeading(270), 2.0);
        const double sweptAt = 4;
        const double sweptTrack = 200;
        CommandResult coast = engine.Actions.ApplyRecorded(
            new RecordedEramEntry(10, ac.Callsign, "COAST T10", "3O", swept.Lat, swept.Lon, sweptTrack, sweptAt)
        );
        Assert.True(coast.Success, coast.Message);
        Assert.Equal(swept.Lat, ac.Eram.CoastLat);
        Assert.Equal(swept.Lon, ac.Eram.CoastLon);
        Assert.Equal(sweptAt, ac.Eram.CoastStartSeconds);
        Assert.Equal(sweptTrack, ac.Eram.CoastTrueCourse!.Value, 6);

        // A record with no swept pose (unswept, or made before records carried one) keeps the live target at the entry's time.
        AircraftState live = unswept.FindAircraft(AiTestFixture.Callsign)!;
        CommandResult liveCoast = unswept.Actions.ApplyRecorded(new RecordedEramEntry(10, live.Callsign, "COAST T10", "3O", null, null, null, null));
        Assert.True(liveCoast.Success, liveCoast.Message);
        Assert.Equal(live.Position.Lat, live.Eram.CoastLat);
        Assert.Equal(live.Position.Lon, live.Eram.CoastLon);
        Assert.Equal(10, live.Eram.CoastStartSeconds);
        Assert.Equal(live.TrueTrack.Degrees, live.Eram.CoastTrueCourse!.Value, 6);
    }

    [Fact]
    public void EramCrrGroup_AppliesToTheEngine_AndNotifiesTheHost()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var group = new RecordedEramCrrGroup(0, "ABC", "Yellow", 37.5, -122.0);

        CommandResult applied = engine.Actions.ApplyRecorded(group, host);

        Assert.True(applied.Success, applied.Message);
        Assert.Equal("ABC", Assert.Single(engine.CrrGroups.Values).Label);
        Assert.Equal(1, host.EramCrrGroupChanges);
    }

    [Fact]
    public void ARoomEntryAppliesThroughTheRouterAndNotifiesTheHost()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var entry = new RecordedEramRoomEntry(0, "ZOA", "CA CA DISPLAY 44 OFF");

        CommandResult applied = engine.Actions.ApplyRecorded(entry, host);

        Assert.True(applied.Success, applied.Message);
        Assert.False(engine.EramRoomSettings.ShowsConflict("ZOA", "44", isMciPair: false));
        Assert.Equal(1, host.EramConflictSettingsChanges);
    }

    [Fact]
    public void StripRequest_PrintsInTheEngine_UnderTheRecordedId()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var request = new RecordedStripRequest(0, AiTestFixture.Callsign, null, $"STRIP_{AiTestFixture.Callsign}");

        CommandResult applied = engine.Actions.ApplyRecorded(request, host);

        Assert.True(applied.Success, applied.Message);
        StripItemRecord printed = Assert.Single(engine.Strips.Items.Values);
        Assert.Equal($"STRIP_{AiTestFixture.Callsign}", printed.Id);
        Assert.Equal([printed.Id], engine.Strips.DeparturePrinterQueue);
    }

    [Fact]
    public void AStripRequestTheEngineRefuses_IsReported_WithAReplayFidelityWarning()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        var host = new AttendanceActionHost();

        // The aircraft is gone, so the print body refuses — the same verdict the live room reached.
        CommandResult applied = engine.Actions.ApplyRecorded(new RecordedStripRequest(0, "NOPE1", null, "STRIP_NOPE1"), host);

        Assert.False(applied.Success);
        CapturedLogRecord warning = Assert.Single(tap.Drain(), r => r.Category == "ActionRouter");
        Assert.Contains("replay-fidelity", warning.Message);
        Assert.Contains("NOPE1", warning.Message);
    }

    [Fact]
    public void AsdexSafetyLogicChange_WritesTheScenariosConfig()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var config = new AsdexSafetyLogicConfig([], "WEST", []);
        var change = new RecordedAsdexSafetyLogicChange(0, "OAK", config);

        CommandResult applied = engine.Actions.ApplyRecorded(change, host);

        Assert.True(applied.Success, applied.Message);
        Assert.Same(config, engine.Scenario!.AsdexSafetyLogicConfig);
    }

    [Fact]
    public void ARecordForAMissingAircraft_IsRefused_WithAReplayFidelityWarning()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        using var tap = new CapturingSimLogProvider(LogLevel.Warning, 100);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(tap));
        SimLog.InitializeForTest(factory);

        CommandResult applied = engine.Actions.ApplyRecorded(new RecordedClearanceChange(0, "NOPE1", new AircraftClearanceDto()));

        Assert.False(applied.Success);
        CapturedLogRecord warning = Assert.Single(tap.Drain(), r => r.Category == "ActionRouter");
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

        AircraftState ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        List<RecordedAction> log = engine.Scenario!.ActionLog;
        int before = log.Count;

        CommandResult refused = engine.Actions.IssueDerived(new RecordedEramEntry(0, ac.Callsign, "QS 400", null, null, null, null, null));
        Assert.False(refused.Success);
        Assert.Equal(before, log.Count);

        CommandResult applied = engine.Actions.IssueDerived(new RecordedEramEntry(0, ac.Callsign, "QS 270", null, null, null, null, null));
        Assert.True(applied.Success, applied.Message);
        RecordedEramEntry recorded = Assert.IsType<RecordedEramEntry>(log[^1]);
        Assert.Equal("QS 270", recorded.Entry);
        Assert.Equal(before + 1, log.Count);
    }
}
