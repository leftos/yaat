using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// #253 — the ERAM Field-E accepted indicator (Oxxx/Kxxx). When a handoff is accepted (or a Track is
/// force-taken), the previous owner is recorded on the aircraft so the CRC broadcast can show the acceptor's
/// sector on the previous owner's FDB for a transient window. Covers the shared <see cref="TrackEngine"/>
/// choke points (manual accept → not forced; force → forced; drop → cleared) and the snapshot round-trip.
/// The 30 s window itself lives in the yaat-server broadcast (<c>CrcAcceptedIndicatorTests</c>).
/// </summary>
public class TrackEngineAcceptedIndicatorTests
{
    private static TrackOwner Eram(string callsign, string sectorId) => TrackOwner.CreateEram(callsign, "ZOA", sectorId);

    private static AircraftState Aircraft() => new() { Callsign = "N123AB", AircraftType = "C172" };

    private static SimScenarioState Scenario(double elapsedSeconds) =>
        new()
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = elapsedSeconds,
            StudentPosition = Eram("ZOA_40", "40"),
        };

    private static ResolvedAtcPosition Atc(TrackOwner owner, int subset, string sectorId) =>
        new()
        {
            Source = new ScenarioAtc { Id = owner.Callsign },
            Owner = owner,
            Tcp = new Tcp(subset, sectorId, owner.Callsign, null),
        };

    [Fact]
    public void HandleAccept_RecordsPreviousOwner_NotForced_WithAcceptTime()
    {
        AircraftState ac = Aircraft();
        TrackOwner previousOwner = Eram("ZOA_40", "40");
        ac.Track.Owner = previousOwner;
        ac.Track.HandoffPeer = Eram("ZOA_36", "36");

        CommandResult result = TrackEngine.HandleAccept(ac, Scenario(elapsedSeconds: 128));

        Assert.True(result.Success, result.Message);
        Assert.Equal(previousOwner, ac.Eram.RecentHandoffPreviousOwner);
        Assert.False(ac.Eram.RecentHandoffWasForced);
        Assert.Equal(128, ac.Eram.RecentHandoffAcceptedAtSeconds);
    }

    [Fact]
    public void Retract_MarksTheInitiatorAsRecentPreviousOwner()
    {
        // The owner retracts its own outbound handoff: CRC shows O plus the owner's own sector on the initiator's FDB.
        AircraftState ac = Aircraft();
        TrackOwner initiator = Eram("ZOA_40", "40");
        ac.Track.Owner = initiator;
        ac.Track.HandoffPeer = Eram("ZOA_36", "36");

        CommandResult result = TrackEngine.HandleCancel(ac, Scenario(elapsedSeconds: 42));

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Track.HandoffPeer);
        Assert.Same(initiator, ac.Track.Owner);
        Assert.Equal(initiator, ac.Eram.RecentHandoffPreviousOwner);
        Assert.False(ac.Eram.RecentHandoffWasForced);
        Assert.Equal(42, ac.Eram.RecentHandoffAcceptedAtSeconds);
    }

    [Fact]
    public void StarsCancel_LeavesAnExistingEramIndicatorAlone()
    {
        // Sector 40 retracted a handoff (its O is up), then a STARS position took the track and retracted its own
        // handoff inside the 30 s window: the STARS retract has no ERAM indicator to raise and leaves sector 40's alone.
        AircraftState ac = Aircraft();
        TrackOwner eramInitiator = Eram("ZOA_40", "40");
        ac.Track.Owner = eramInitiator;
        ac.Track.HandoffPeer = Eram("ZOA_36", "36");
        Assert.True(TrackEngine.HandleCancel(ac, Scenario(elapsedSeconds: 10)).Success);

        ac.Track.Owner = TrackOwner.CreateStars("NCT_B", "NCT", 2, "B");
        ac.Track.HandoffPeer = TrackOwner.CreateStars("NCT_C", "NCT", 2, "C");
        CommandResult result = TrackEngine.HandleCancel(ac, Scenario(elapsedSeconds: 25));

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Track.HandoffPeer);
        Assert.Equal(eramInitiator, ac.Eram.RecentHandoffPreviousOwner);
        Assert.False(ac.Eram.RecentHandoffWasForced);
        Assert.Equal(10, ac.Eram.RecentHandoffAcceptedAtSeconds);
    }

    [Fact]
    public void MarkRecentHandoffAccepted_Forced_SetsForcedFlag()
    {
        AircraftState ac = Aircraft();
        TrackOwner previousOwner = Eram("ZOA_40", "40");

        TrackEngine.MarkRecentHandoffAccepted(ac, previousOwner, wasForced: true, Scenario(elapsedSeconds: 5));

        Assert.Equal(previousOwner, ac.Eram.RecentHandoffPreviousOwner);
        Assert.True(ac.Eram.RecentHandoffWasForced);
        Assert.Equal(5, ac.Eram.RecentHandoffAcceptedAtSeconds);
    }

    [Fact]
    public void MarkRecentHandoffAccepted_NullPreviousOwner_IsNoOp()
    {
        AircraftState ac = Aircraft();

        TrackEngine.MarkRecentHandoffAccepted(ac, previousOwner: null, wasForced: false, Scenario(elapsedSeconds: 5));

        Assert.Null(ac.Eram.RecentHandoffPreviousOwner);
        Assert.Null(ac.Eram.RecentHandoffAcceptedAtSeconds);
    }

    [Fact]
    public void ApplyForceHandoff_InterruptsOutboundHandoff_ClearsPeer_AndMarksForced()
    {
        // A owns the track and already has an outbound handoff to 44 in flight (Field-E H44); sector 36
        // steals it with /OK. The stale outbound handoff must be cleared so A shows K36, not a residual H44.
        AircraftState ac = Aircraft();
        TrackOwner previousOwner = Eram("ZOA_40", "40");
        TrackOwner stealTarget = Eram("ZOA_36", "36");
        ac.Track.Owner = previousOwner;
        ac.Track.HandoffPeer = Eram("ZOA_44", "44");
        var scenario = new SimScenarioState
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = 7,
            StudentPosition = Eram("ZOA_40", "40"),
            AtcPositions = [Atc(stealTarget, subset: 2, sectorId: "36")],
        };

        CommandResult result = TrackEngine.ApplyForceHandoff(ac, scenario, tcpCode: "236", facilityHint: null);

        Assert.True(result.Success, result.Message);
        Assert.Equal(stealTarget, ac.Track.Owner);
        Assert.Null(ac.Track.HandoffPeer);
        Assert.Equal(previousOwner, ac.Eram.RecentHandoffPreviousOwner);
        Assert.True(ac.Eram.RecentHandoffWasForced);
        Assert.Equal(7, ac.Eram.RecentHandoffAcceptedAtSeconds);
    }

    [Fact]
    public void HandleDrop_ClearsAcceptedIndicator()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Eram("ZOA_40", "40");
        ac.Eram.RecentHandoffPreviousOwner = Eram("ZOA_36", "36");
        ac.Eram.RecentHandoffWasForced = true;
        ac.Eram.RecentHandoffAcceptedAtSeconds = 12;

        CommandResult result = TrackEngine.HandleDrop(ac);

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Eram.RecentHandoffPreviousOwner);
        Assert.False(ac.Eram.RecentHandoffWasForced);
        Assert.Null(ac.Eram.RecentHandoffAcceptedAtSeconds);
    }

    [Fact]
    public void HandleDrop_AfterAccept_ClearsTheFdbOpenSectors()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Eram("ZOA_40", "40");
        ac.Track.HandoffPeer = Eram("ZOA_36", "36");
        TrackEngine.HandleAccept(ac, Scenario(elapsedSeconds: 10));
        Assert.Equal([new EramSectorKey("ZOA", "40")], ac.Eram.FdbOpenSectors);

        CommandResult result = TrackEngine.HandleDrop(ac);

        Assert.True(result.Success, result.Message);
        Assert.Empty(ac.Eram.FdbOpenSectors);
    }

    [Fact]
    public void HandleAccept_EramToEram_KeepsFdbOpenForPreviousOwner()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Eram("ZOA_40", "40");
        ac.Track.HandoffPeer = Eram("ZOA_36", "36");

        CommandResult result = TrackEngine.HandleAccept(ac, Scenario(elapsedSeconds: 10));

        Assert.True(result.Success, result.Message);
        Assert.Equal([new EramSectorKey("ZOA", "40")], ac.Eram.FdbOpenSectors);
    }

    [Fact]
    public void HandleAccept_EramToStars_KeepsFdbOpenForPreviousOwner()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Eram("ZOA_14", "14");
        ac.Track.HandoffPeer = TrackOwner.CreateStars("OAK_B_APP", "NCT", 2, "B");

        CommandResult result = TrackEngine.HandleAccept(ac, Scenario(elapsedSeconds: 10));

        Assert.True(result.Success, result.Message);
        Assert.Equal([new EramSectorKey("ZOA", "14")], ac.Eram.FdbOpenSectors);
    }

    [Fact]
    public void MarkRecentHandoffAccepted_Forced_KeepsFdbOpenForPreviousOwner()
    {
        AircraftState ac = Aircraft();

        TrackEngine.MarkRecentHandoffAccepted(ac, Eram("ZOA_40", "40"), wasForced: true, Scenario(elapsedSeconds: 5));

        Assert.Equal([new EramSectorKey("ZOA", "40")], ac.Eram.FdbOpenSectors);
    }

    [Fact]
    public void HandleAccept_StarsPreviousOwner_OpensNoFdb()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = TrackOwner.CreateStars("OAK_B_APP", "NCT", 2, "B");
        ac.Track.HandoffPeer = Eram("ZOA_40", "40");

        CommandResult result = TrackEngine.HandleAccept(ac, Scenario(elapsedSeconds: 10));

        Assert.True(result.Success, result.Message);
        Assert.Empty(ac.Eram.FdbOpenSectors);
    }

    [Fact]
    public void MarkRecentHandoffAccepted_Twice_DoesNotDuplicateFdbOpenSector()
    {
        AircraftState ac = Aircraft();
        TrackOwner previousOwner = Eram("ZOA_40", "40");

        TrackEngine.MarkRecentHandoffAccepted(ac, previousOwner, wasForced: false, Scenario(elapsedSeconds: 5));
        TrackEngine.MarkRecentHandoffAccepted(ac, previousOwner, wasForced: false, Scenario(elapsedSeconds: 50));

        Assert.Equal([new EramSectorKey("ZOA", "40")], ac.Eram.FdbOpenSectors);
    }

    [Fact]
    public void FdbToggle_AfterAccept_ClosesThePreviousOwnersFdb()
    {
        AircraftState ac = Aircraft();
        TrackOwner previousOwner = Eram("ZOA_40", "40");
        ac.Track.Owner = previousOwner;
        ac.Track.HandoffPeer = Eram("ZOA_36", "36");
        Assert.True(TrackEngine.HandleAccept(ac, Scenario(elapsedSeconds: 10)).Success);

        var context = new EramEntryContext(previousOwner, Scenario: null, Redirect: null, new EramConflictState(), SweptPosition: null);
        CommandResult toggled = EramEntryEngine.Apply(ac, "FDB ZOA 40", context);

        Assert.True(toggled.Success, toggled.Message);
        Assert.Empty(ac.Eram.FdbOpenSectors);
    }

    [Fact]
    public void AircraftEramState_AcceptedIndicator_RoundTripsThroughSnapshot()
    {
        var state = new AircraftEramState
        {
            RecentHandoffPreviousOwner = Eram("ZOA_40", "40"),
            RecentHandoffWasForced = true,
            RecentHandoffAcceptedAtSeconds = 99.5,
        };

        var restored = AircraftEramState.FromSnapshot(state.ToSnapshot());

        Assert.Equal("ZOA_40", restored.RecentHandoffPreviousOwner!.Callsign);
        Assert.Equal("40", restored.RecentHandoffPreviousOwner.SectorId);
        Assert.True(restored.RecentHandoffWasForced);
        Assert.Equal(99.5, restored.RecentHandoffAcceptedAtSeconds);
    }
}
