using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>
/// <see cref="EngineAiCommandSink"/> / <see cref="SimulationEngine.DispatchAiCommand"/>: aviation verbs run under the
/// AI origin and are recorded with the AI connection id, track verbs run under the AI position's identity, server-only
/// verbs are refused, and a recorded AI command replays to the same state.
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
    public void RejectedVerb_ReportsTheReason_AndRecordsNothing()
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
        Assert.Empty(engine.Scenario!.ActionLog);
    }

    [Fact]
    public void ServerOnlyVerb_IsRefused()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground]);

        var result = engine.DispatchAiCommand(ground, AiTestFixture.Callsign, "RDACK");

        Assert.False(result.Success);
        Assert.Contains("live server", result.Message);
        Assert.Empty(engine.Scenario!.ActionLog);
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
        replayEngine.ReplayCommand(recorded);
        var replayedOwner = replayEngine.FindAircraft(AiTestFixture.Callsign)!.Track.Owner;
        Assert.NotNull(replayedOwner);
        Assert.True(replayedOwner.MatchesPosition(approach.Identity));
    }
}
