using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>
/// <see cref="EngineAiCommandSink"/> / <see cref="SimulationEngine.DispatchAiCommand"/>: aviation verbs run under the
/// AI origin and are recorded with the AI connection id, track verbs run under the AI position's identity, a verb the
/// engine refuses is recorded as rejected like every routed command, the room-wide ASDE-X sweep is the engine's own,
/// and a recorded AI command replays to the same state.
/// </summary>
public class EngineAiCommandSinkTests
{
    private static readonly AiIntent Intent = new("test", "because");
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public EngineAiCommandSinkTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void AviationVerb_DispatchesUnderTheAiOrigin_AndIsRecordedWithTheAiConnectionId()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground]);
        AiTestFixture.Tick(engine, 7);
        var sink = new EngineAiCommandSink(engine);

        sink.Issue(new AiCommandRequest(ground, AiTestFixture.Callsign, "TAXIAUTO 28R", Intent));

        var outcome = Assert.Single(sink.DrainOutcomes());
        Assert.True(outcome.Success, outcome.Reason);
        Assert.Empty(sink.DrainOutcomes());
        var aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.False(aircraft.HasMadeInitialContact);
        Assert.False(aircraft.PendingPilotRequest!.IsOpen);
        var recorded = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));
        Assert.Equal("TAXIAUTO 28R", recorded.Command);
        Assert.Equal("AI", recorded.Initials);
        Assert.Equal(AiConnectionId.Format(ground.PositionId), recorded.ConnectionId);
    }

    [Fact]
    public void RejectedVerb_ReportsTheReason_AndIsRecordedAsRejected()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground]);
        var sink = new EngineAiCommandSink(engine);

        sink.Issue(new AiCommandRequest(ground, AiTestFixture.Callsign, "CTO", Intent));

        var outcome = Assert.Single(sink.DrainOutcomes());
        Assert.False(outcome.Success);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Reason));
        // Every routed command is recorded, accepted or not, so a replay can compare its verdict with live's.
        var recorded = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));
        Assert.Equal("CTO", recorded.Command);
        Assert.False(recorded.Accepted);
    }

    /// <summary>
    /// <c>ASDXALERTS</c>' alert inhibits are per-aircraft engine state, so the bare engine applies the room-wide sweep
    /// itself and records the command as accepted.
    /// </summary>
    [Fact]
    public void GlobalAsdexVerb_SweepsTheEngine_AndIsRecordedAsAccepted()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground]);
        engine.FindAircraft(AiTestFixture.Callsign)!.Stars.AsdexAlertsInhibited = true;

        var result = engine.DispatchAiCommand(ground, "", "ASDXALERTS");

        Assert.True(result.Success, result.Message);
        Assert.False(engine.FindAircraft(AiTestFixture.Callsign)!.Stars.AsdexAlertsInhibited);
        var recorded = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));
        Assert.Equal("ASDXALERTS", recorded.Command);
        Assert.True(recorded.Accepted);
    }

    [Fact]
    public void TrackVerb_RunsUnderTheAiPositionsIdentity_AndReplaysTheSame()
    {
        if (_zoa is null)
        {
            return;
        }

        var approach = TestAiPositions.NorCalApproach(_zoa);
        Assert.NotNull(approach.Tcp);
        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [approach]);
        // No AS prefix and no student position in the scenario: the AI connection id alone names the acting position.
        var result = engine.DispatchAiCommand(approach, AiTestFixture.Callsign, "TRACK");

        Assert.True(result.Success, result.Message);
        var owner = engine.FindAircraft(AiTestFixture.Callsign)!.Track.Owner;
        Assert.NotNull(owner);
        Assert.True(owner.MatchesPosition(approach.Identity));
        var recorded = Assert.IsType<RecordedCommand>(Assert.Single(engine.Scenario!.ActionLog));

        var replayEngine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        replayEngine.Actions.Apply(recorded);
        var replayedOwner = replayEngine.FindAircraft(AiTestFixture.Callsign)!.Track.Owner;
        Assert.NotNull(replayedOwner);
        Assert.True(replayedOwner.MatchesPosition(approach.Identity));
    }
}
