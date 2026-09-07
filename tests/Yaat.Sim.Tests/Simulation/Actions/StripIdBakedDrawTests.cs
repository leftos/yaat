using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// The strip id a creating strip verb draws is a baked draw like the reaction delay and the generated aircraft: the
/// host mints it, the arm writes it back onto the context, the router bakes it onto the record, and the record carries
/// it through the archive so a replay can hand it back rather than drawing again.
/// </summary>
public class StripIdBakedDrawTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public StripIdBakedDrawTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    [Fact]
    public void ARecordedCommandsStripId_RoundTripsTheArchiveSerializer()
    {
        var record = new RecordedCommand(12, "", "SEP W OAK/Ground1/1/1 HOLD LINE", "XX", "conn-1") { StripId = "SEP_a1b2c3d4", Accepted = true };

        var json = JsonSerializer.Serialize<RecordedAction>(record, RecordingJsonOptions.Default);
        var restored = Assert.IsType<RecordedCommand>(JsonSerializer.Deserialize<RecordedAction>(json, RecordingJsonOptions.Default));

        Assert.Equal("SEP_a1b2c3d4", restored.StripId);
    }

    [Fact]
    public void BakedDrawsOf_CarriesTheStripId()
    {
        var record = new RecordedCommand(3, "", "HSC OAK/Ground1/1 WORKSPACE", "XX", "conn-1") { StripId = "HSTRIP_9f8e7d6c" };

        Assert.Equal("HSTRIP_9f8e7d6c", BakedDraws.Of(record).StripId);
    }

    /// <summary>
    /// The live half of the channel: whatever id the host reports for a creating verb is what the router writes onto
    /// the command it records — no other run kind has to mint one.
    /// </summary>
    [Fact]
    public void TheStripArm_BakesTheIdTheHostMinted_OntoTheRecord()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost { MintedStripId = "SEP_deadbeef" };

        var outcome = engine.Actions.Issue(new ActionInput("", "SEP W OAK/Ground1/1/1 HOLD LINE", "conn-1", "XX", Baked: null), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("SEP_deadbeef", outcome.ToRecord?.StripId);
        Assert.Equal("SEP_deadbeef", Assert.Single(engine.Scenario!.ActionLog.OfType<RecordedCommand>()).StripId);
    }

    /// <summary>
    /// The replay half: the id on the record reaches the host, so the item is created under it rather than under one
    /// the host would have drawn.
    /// </summary>
    [Fact]
    public void ARecordedStripCommand_HandsItsBakedIdToTheHost()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost { MintedStripId = "SEP_freshdraw" };
        var record = new RecordedCommand(0, "", "SEP W OAK/Ground1/1/1 HOLD LINE", "XX", "conn-1") { StripId = "SEP_baked", Accepted = true };

        var outcome = engine.Actions.Apply(record, host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("SEP_baked", host.LastBakedStripId);
    }
}
