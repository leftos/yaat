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
        var ac = Aircraft();
        var previousOwner = Eram("ZOA_40", "40");
        ac.Track.Owner = previousOwner;
        ac.Track.HandoffPeer = Eram("ZOA_36", "36");

        var result = TrackEngine.HandleAccept(ac, Scenario(elapsedSeconds: 128));

        Assert.True(result.Success, result.Message);
        Assert.Equal(previousOwner, ac.Eram.RecentHandoffPreviousOwner);
        Assert.False(ac.Eram.RecentHandoffWasForced);
        Assert.Equal(128, ac.Eram.RecentHandoffAcceptedAtSeconds);
    }

    [Fact]
    public void MarkRecentHandoffAccepted_Forced_SetsForcedFlag()
    {
        var ac = Aircraft();
        var previousOwner = Eram("ZOA_40", "40");

        TrackEngine.MarkRecentHandoffAccepted(ac, previousOwner, wasForced: true, Scenario(elapsedSeconds: 5));

        Assert.Equal(previousOwner, ac.Eram.RecentHandoffPreviousOwner);
        Assert.True(ac.Eram.RecentHandoffWasForced);
        Assert.Equal(5, ac.Eram.RecentHandoffAcceptedAtSeconds);
    }

    [Fact]
    public void MarkRecentHandoffAccepted_NullPreviousOwner_IsNoOp()
    {
        var ac = Aircraft();

        TrackEngine.MarkRecentHandoffAccepted(ac, previousOwner: null, wasForced: false, Scenario(elapsedSeconds: 5));

        Assert.Null(ac.Eram.RecentHandoffPreviousOwner);
        Assert.Null(ac.Eram.RecentHandoffAcceptedAtSeconds);
    }

    [Fact]
    public void ApplyForceHandoff_InterruptsOutboundHandoff_ClearsPeer_AndMarksForced()
    {
        // A owns the track and already has an outbound handoff to 44 in flight (Field-E H44); sector 36
        // steals it with /OK. The stale outbound handoff must be cleared so A shows K36, not a residual H44.
        var ac = Aircraft();
        var previousOwner = Eram("ZOA_40", "40");
        var stealTarget = Eram("ZOA_36", "36");
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

        var result = TrackEngine.ApplyForceHandoff(ac, scenario, tcpCode: "236");

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
        var ac = Aircraft();
        ac.Track.Owner = Eram("ZOA_40", "40");
        ac.Eram.RecentHandoffPreviousOwner = Eram("ZOA_36", "36");
        ac.Eram.RecentHandoffWasForced = true;
        ac.Eram.RecentHandoffAcceptedAtSeconds = 12;

        var result = TrackEngine.HandleDrop(ac);

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Eram.RecentHandoffPreviousOwner);
        Assert.False(ac.Eram.RecentHandoffWasForced);
        Assert.Null(ac.Eram.RecentHandoffAcceptedAtSeconds);
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
