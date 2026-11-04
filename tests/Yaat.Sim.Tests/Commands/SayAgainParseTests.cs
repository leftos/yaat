using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// The student's "say again" verb: <c>AGAIN</c> asks the pilot to repeat its last transmission, <c>AGAIN CS</c> only its
/// callsign. It takes no other argument, collides with no alias in the registry or the imported ATCTrainer/VICE sets, leaves
/// <c>SA</c> to make-short-approach, and is never deferred by the pilot-reaction draw.
/// </summary>
public sealed class SayAgainParseTests
{
    public SayAgainParseTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Theory]
    [InlineData("AGAIN")]
    [InlineData("again")]
    public void Parse_Again_IsSayAgainWholeTransmission(string text)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(text);

        SayAgainCommand command = Assert.IsType<SayAgainCommand>(parsed.Value);
        Assert.False(command.CallsignOnly);
        Assert.Equal(CanonicalCommandType.SayAgain, CommandDescriber.ToCanonicalType(command));
        Assert.Equal("AGAIN", CommandDescriber.DescribeCommand(command));
    }

    [Theory]
    [InlineData("AGAIN CS")]
    [InlineData("again cs")]
    public void Parse_AgainCs_IsCallsignOnly(string text)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(text);

        SayAgainCommand command = Assert.IsType<SayAgainCommand>(parsed.Value);
        Assert.True(command.CallsignOnly);
        Assert.Equal("AGAIN CS", CommandDescriber.DescribeCommand(command));
    }

    [Theory]
    [InlineData("AGAIN C")]
    [InlineData("AGAIN CSX")]
    [InlineData("AGAIN CS CS")]
    [InlineData("AGAIN 270")]
    [InlineData("AGAIN ALL")]
    public void Parse_AgainWithAnyOtherArgument_IsRejected(string text)
    {
        ParseResult<ParsedCommand> parsed = CommandParser.Parse(text);

        Assert.IsNotType<SayAgainCommand>(parsed.Value);
    }

    [Fact]
    public void Parse_AgainU_And_AgainUCs()
    {
        foreach (
            (string text, bool callsignOnly, string canonical) in new[]
            {
                ("AGAINU", false, "AGAINU"),
                ("AGAINU CS", true, "AGAINU CS"),
                ("againu cs", true, "AGAINU CS"),
            }
        )
        {
            ParseResult<ParsedCommand> parsed = CommandParser.Parse(text);

            SayAgainUnaddressedCommand command = Assert.IsType<SayAgainUnaddressedCommand>(parsed.Value);
            Assert.Equal(callsignOnly, command.CallsignOnly);
            Assert.Equal(CanonicalCommandType.SayAgainUnaddressed, CommandDescriber.ToCanonicalType(command));
            Assert.Equal(canonical, CommandDescriber.DescribeCommand(command));
            Assert.Equal(command, CommandParser.Parse(CommandDescriber.DescribeCommand(command)).Value);
        }

        ParseResult<ParsedCommand> rejected = CommandParser.Parse("AGAINU X");
        Assert.False(rejected.IsSuccess);
        // The parser frames every rejection with the quoted verb and the verb's accepted forms.
        Assert.Contains("AGAINU takes no argument, or CS for the callsign only", rejected.Reason, StringComparison.Ordinal);
        Assert.Equal(CanonicalCommandType.SayAgainUnaddressed, CommandRegistry.AliasToCanonicType["AGAINU"]);
        Assert.Equal(CanonicalCommandType.SayAgain, CommandRegistry.AliasToCanonicType["AGAIN"]);
    }

    [Fact]
    public void Registry_Again_HasNoAliasCollision()
    {
        CommandDefinition definition = Assert.IsType<CommandDefinition>(CommandRegistry.Get(CanonicalCommandType.SayAgain));
        Assert.Equal(["AGAIN"], definition.DefaultAliases);

        // No other registry row claims the verb.
        foreach ((CanonicalCommandType type, CommandDefinition other) in CommandRegistry.All)
        {
            if (type == CanonicalCommandType.SayAgain)
            {
                continue;
            }

            Assert.DoesNotContain("AGAIN", other.DefaultAliases, StringComparer.OrdinalIgnoreCase);
        }

        Assert.Equal(CanonicalCommandType.SayAgain, CommandRegistry.AliasToCanonicType["AGAIN"]);

        // Neither imported alias set (ATCTrainer, VICE) names AGAIN as a verb or an alias.
        string aliasDir = Path.Combine(RepoRoot(), "docs", "command-aliases");
        foreach (string file in new[] { "atctrainer-commands.json", "vice-commands.json" })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(aliasDir, file)));
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                Assert.False(Names(row).Contains("AGAIN", StringComparer.OrdinalIgnoreCase), $"{file} already uses AGAIN");
            }
        }
    }

    [Fact]
    public void Registry_Sa_StillMeansMakeShortApproach()
    {
        Assert.Equal(CanonicalCommandType.MakeShortApproach, CommandRegistry.AliasToCanonicType["SA"]);
        Assert.IsType<MakeShortApproachCommand>(CommandParser.Parse("SA").Value);
    }

    [Theory]
    [InlineData("AGAIN")]
    [InlineData("AGAIN CS")]
    public void ReactionDelay_SayAgain_IsNotDeferred(string text)
    {
        var engine = new SimulationEngine(new NullGroundData(), PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                CommandRunDelayMinSeconds = 5,
                CommandRunDelayMaxSeconds = 5,
            },
        };
        var aircraft = new AircraftState
        {
            Callsign = "N123AB",
            AircraftType = "C172",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(090),
            Altitude = 3000,
            IndicatedAirspeed = 100,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan(),
        };
        engine.World.AddAircraft(aircraft);

        CompoundCommand again = CommandParser.ParseCompound(text).Value!;
        CompoundCommand climb = CommandParser.ParseCompound("CM 50").Value!;

        Assert.Null(ReactionDelayPolicy.Decide(engine.Scenario!, engine.World, aircraft, again, baked: null));
        // The same scenario does defer a manoeuvre, so the null above is the say-again exemption, not a disabled delay.
        Assert.Equal(5, ReactionDelayPolicy.Decide(engine.Scenario!, engine.World, aircraft, climb, baked: null));
    }

    private static IEnumerable<string> Names(JsonElement row)
    {
        if (row.TryGetProperty("primary", out JsonElement primary) && (primary.ValueKind == JsonValueKind.String))
        {
            yield return primary.GetString()!;
        }

        if (row.TryGetProperty("aliases", out JsonElement aliases) && (aliases.ValueKind == JsonValueKind.Array))
        {
            foreach (JsonElement alias in aliases.EnumerateArray())
            {
                if (alias.ValueKind == JsonValueKind.String)
                {
                    yield return alias.GetString()!;
                }
            }
        }
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while ((dir is not null) && !File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("yaat.slnx not found above the test binary");
    }
}
