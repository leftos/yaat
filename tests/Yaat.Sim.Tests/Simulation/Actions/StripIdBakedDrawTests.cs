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
/// verb mints it, the arm writes it back onto the context, the router bakes it onto the record, and the record carries
/// it through the archive so a replay hands it back rather than drawing again.
/// </summary>
public class StripIdBakedDrawTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public StripIdBakedDrawTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>The bare engine with the student position and the strip bays a creating verb resolves its destination against.</summary>
    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        engine.Scenario!.StudentPosition = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "O");
        engine.InitializeStripsAndTdlsFromArtcc();
        return engine;
    }

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
    /// The live half of the channel: whatever id the creating verb drew is what the router writes onto the command it
    /// records — no other run kind has to mint one.
    /// </summary>
    [Fact]
    public void TheStripArm_BakesTheIdTheVerbMinted_OntoTheRecord()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();

        var outcome = engine.Actions.Issue(new ActionInput("", "SEP W OAK/Ground1/1/1 HOLD LINE", "conn-1", "XX", Baked: null), host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        var created = Assert.Single(engine.Strips.Items.Values);
        Assert.StartsWith("SEP_", created.Id, StringComparison.Ordinal);
        Assert.Equal(created.Id, outcome.ToRecord?.StripId);
        Assert.Equal(created.Id, Assert.Single(engine.Scenario!.ActionLog.OfType<RecordedCommand>()).StripId);
    }

    /// <summary>
    /// The replay half: the id on the record is what the item is created under, rather than one the verb would have
    /// drawn for itself.
    /// </summary>
    [Fact]
    public void ARecordedStripCommand_CreatesTheItemUnderItsBakedId()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var host = new AttendanceActionHost();
        var record = new RecordedCommand(0, "", "SEP W OAK/Ground1/1/1 HOLD LINE", "XX", "conn-1") { StripId = "SEP_baked", Accepted = true };

        var outcome = engine.Actions.Apply(record, host);

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("SEP_baked", Assert.Single(engine.Strips.Items.Values).Id);
    }
}
