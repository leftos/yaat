using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The engine holds one per-connection active-position map, and two callers share it:
/// <see cref="SimulationEngine.ReplayCommand"/> (replay) and <see cref="SimulationEngine.DispatchAiCommand"/>
/// (live). An AI controller's commands are recorded under its own connection id, so a recorded
/// <c>AS</c> carrying that id lands in the same map slot the live AI resolves its identity from.
/// An AI position's identity is the position it works; a selection made on another connection —
/// or replayed from a recording — must not displace it.
/// </summary>
public class ReplayTrackApplierIdentityIsolationTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ReplayTrackApplierIdentityIsolationTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void ReplayedActivePosition_DoesNotDisplaceTheAiPositionsOwnIdentity()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestHost.Load(AiTestHost.ParkedAtOak, _zoa, 7, [ground]);
        var scenario = engine.Scenario!;

        // A student on OAK Tower, so "AS 3T" resolves to an owner that is not the AI ground position.
        var student = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "T");
        scenario.StudentPosition = student;
        scenario.StudentTcp = new Tcp(3, "T", "tcp-oak-twr", null);

        var aiConnectionId = AiConnectionId.Format(ground.PositionId);
        var aiOwner = _zoa.ResolvePosition(ground.PositionId);
        Assert.NotNull(aiOwner);
        Assert.NotEqual(student, aiOwner);

        // A recorded active-position selection carrying the AI's connection id, replayed into this engine.
        engine.ReplayCommand(new RecordedCommand(0, AiTestHost.Callsign, "AS 3T", "XX", aiConnectionId));

        var result = engine.DispatchAiCommand(ground, AiTestHost.Callsign, "TRACK");

        Assert.True(result.Success, result.Message);
        var aircraft = engine.FindAircraft(AiTestHost.Callsign)!;
        Assert.Equal(aiOwner, aircraft.Track.Owner);
    }
}
