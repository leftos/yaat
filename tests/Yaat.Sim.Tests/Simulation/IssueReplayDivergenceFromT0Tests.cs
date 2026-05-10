using Xunit;
using Xunit.Abstractions;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Replay;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Regression tests for the replay-from-t=0 divergence work.
///
/// Before the fix, <see cref="SimulationEngine.ReplayCommand"/> silently dropped
/// every track and AS-prefixed command (TRACK, ACCEPT, HO, "AS 3Y ACCEPT", etc.).
/// The applier now routes those through <see cref="TrackEngine.Dispatch"/> so
/// in-engine replay reaches the same ownership/handoff state as the live session.
///
/// The KFB7 bundle (referenced as the original case) actually predates the
/// airborne-spawn-IAS fix (commit 1873dff) — its initial speeds are stored as TAS,
/// so any current-code replay diverges in physics from t=5s forward regardless of
/// the track-command fix. The diagnostic test below uses
/// <see cref="SimulationEngine.ReplayRangeWithVerification"/> to surface that drift
/// explicitly, which is the right shape for triaging future "why doesn't replay
/// match" questions.
/// </summary>
public class IssueReplayDivergenceFromT0Tests
{
    private const string RecordingPath = "TestData/b143fc615682.zip";

    /// <summary>
    /// Direct check on the ReplayTrackApplier shim: an "AS 3Y ACCEPT" command for
    /// an aircraft with a pending handoff to 3Y must transfer ownership.
    /// Pre-fix, the applier didn't exist and the command was silently dropped.
    /// </summary>
    [Fact]
    public void ReplayTrackApplier_AsPrefixedAccept_TransfersOwnership()
    {
        var scenario = BuildMinimalScenario();
        var aircraft = new AircraftState
        {
            Callsign = "KFB7",
            AircraftType = "CL60/L",
            Cid = "913",
            Track = new AircraftTrack
            {
                HandoffPeer = new TrackOwner("REPLAY-3Y", FacilityId: "NCT", Subset: 3, SectorId: "Y", OwnerType: TrackOwnerType.Stars),
                HandoffInitiatedAt = 100,
            },
        };

        var applier = new ReplayTrackApplier();
        applier.Apply("AS 3Y ACCEPT", aircraft, connectionId: "", scenario);

        Assert.NotNull(aircraft.Track.Owner);
        Assert.Equal("3", $"{aircraft.Track.Owner!.Subset}");
        Assert.Equal("Y", aircraft.Track.Owner.SectorId);
        Assert.Null(aircraft.Track.HandoffPeer);
        Assert.True(aircraft.Track.HandoffAccepted);
    }

    /// <summary>
    /// Standalone "AS 3Y" updates the per-connection active-position map so a later
    /// bare "TRACK" from the same connection acquires under the right identity.
    /// </summary>
    [Fact]
    public void ReplayTrackApplier_StandaloneAsThenTrack_AcquiresUnderActivePosition()
    {
        var scenario = BuildMinimalScenario();
        var aircraft = new AircraftState
        {
            Callsign = "KFB7",
            AircraftType = "CL60/L",
            Cid = "913",
        };

        var applier = new ReplayTrackApplier();
        applier.Apply("AS 3Y", aircraft: null, connectionId: "conn-A", scenario);
        applier.Apply("TRACK", aircraft, connectionId: "conn-A", scenario);

        Assert.NotNull(aircraft.Track.Owner);
        Assert.Equal(3, aircraft.Track.Owner!.Subset);
        Assert.Equal("Y", aircraft.Track.Owner.SectorId);
    }

    private static SimScenarioState BuildMinimalScenario()
    {
        return new SimScenarioState
        {
            ScenarioId = "test",
            ScenarioName = "test",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            StudentPosition = new TrackOwner("OAK_TWR", FacilityId: "OAK", Subset: 3, SectorId: "T", OwnerType: TrackOwnerType.Stars),
            StudentTcp = new Tcp(3, "T", "tcp-oak-twr", null),
            AtcPositions =
            [
                new ResolvedAtcPosition
                {
                    Source = new ScenarioAtc { PositionId = "atc-norcal-y" },
                    Owner = new TrackOwner("NCT_Y_APP", FacilityId: "NCT", Subset: 3, SectorId: "Y", OwnerType: TrackOwnerType.Stars),
                    Tcp = new Tcp(3, "Y", "tcp-nct-y", null),
                },
            ],
        };
    }
}
