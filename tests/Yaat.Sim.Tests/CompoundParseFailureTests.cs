using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class CompoundParseFailureTests : IDisposable
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();
    private static readonly NavigationDatabase NavDb = TestNavDbFactory.WithFixNames("KLIDE", "BRIXX");

    private readonly IDisposable _navDbScope;

    public CompoundParseFailureTests()
    {
        TestVnasData.EnsureInitialized();
        _navDbScope = NavigationDatabase.ScopedOverride(NavDb);
    }

    public void Dispose() => _navDbScope.Dispose();

    [Theory]
    [InlineData("AT KLIDE CMD", "CMD")]
    [InlineData("AT 5000 CMD", "CMD")]
    [InlineData("LV 5000 CMD", "CMD")]
    [InlineData("ATFN 5.0 CMD", "CMD")]
    [InlineData("AT BRIXX XYZ", "XYZ")]
    public void ParseCompound_InvalidCommandAfterCondition_ReportsCorrectVerb(string input, string expectedVerb)
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound(input, Scheme, out ParseFailure? failure);

        Assert.Null(result);
        Assert.NotNull(failure);
        Assert.Equal(expectedVerb, failure.Verb);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason), "ParseFailure.Reason must be non-empty");
    }

    [Theory]
    [InlineData("AT KLIDE CM 280", "AT KLIDE CM 280")]
    [InlineData("AT 5000 CM 280", "AT 5000 CM 280")]
    [InlineData("LV 5000 FH 090", "LV 5000 FH 090")]
    public void ParseCompound_ValidCommandAfterCondition_StillParses(string input, string expected)
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound(input, Scheme, out ParseFailure? failure);

        Assert.NotNull(result);
        Assert.Null(failure);
        Assert.Equal(expected, result.CanonicalString);
    }

    // --- A condition with no command of its own, followed by another block/command ---
    //
    // `AT SUNOL; DM 020` is a typo of `AT SUNOL DM 020`: the separator splits the compound before the
    // condition has a command, so block 0 is a bare condition and `DM 020` an untriggered next block.
    // A bare condition only stands as the last thing in the input.

    [Theory]
    [InlineData("AT BRIXX; DM 020", "AT BRIXX DM 020", ';')]
    [InlineData("AT BRIXX, DM 020", "AT BRIXX DM 020", ',')]
    [InlineData("AT A; SPD 10", "AT A SPD 10", ';')]
    [InlineData("CM 050; AT BRIXX; DM 020", "AT BRIXX DM 020", ';')]
    [InlineData("at brixx; dm 020", "AT BRIXX dm 020", ';')]
    public void ParseCompound_BareConditionBeforeSeparator_IsRefused(string input, string suggestion, char separator)
    {
        CompoundParseResult? result = CommandSchemeParser.ParseCompound(input, Scheme, out ParseFailure? failure);

        Assert.Null(result);
        Assert.NotNull(failure);
        Assert.Equal("AT", failure.Verb);
        Assert.Equal($"has no command before the '{separator}' — did you mean '{suggestion}'?", failure.Reason);
    }

    [Theory]
    [InlineData("AT BRIXX; DM 020", "AT BRIXX DM 020", ';')]
    [InlineData("AT BRIXX, DM 020", "AT BRIXX DM 020", ',')]
    [InlineData("AT A; SPD 10", "AT A SPD 10", ';')]
    [InlineData("CM 050; AT BRIXX; DM 020", "AT BRIXX DM 020", ';')]
    [InlineData("at brixx; dm 020", "AT BRIXX dm 020", ';')]
    public void ParseCompound_BareConditionBeforeSeparator_IsRefused_Server(string input, string suggestion, char separator)
    {
        ParseResult<CompoundCommand> result = CommandParser.ParseCompound(input);

        Assert.False(result.IsSuccess);
        Assert.Equal($"AT has no command before the '{separator}' — did you mean '{suggestion}'?", result.Reason);
    }

    [Theory]
    [InlineData("AT BRIXX; DM 020")]
    [InlineData("AT BRIXX, DM 020")]
    [InlineData("AT A; SPD 10")]
    [InlineData("CM 050; AT BRIXX; DM 020")]
    [InlineData("at brixx; dm 020")]
    public void ParseCompound_BareConditionRefusal_ClientAndServerAgree(string input)
    {
        CommandSchemeParser.ParseCompound(input, Scheme, out ParseFailure? clientFailure);
        ParseResult<CompoundCommand> server = CommandParser.ParseCompound(input);

        Assert.NotNull(clientFailure);
        Assert.False(server.IsSuccess);
        Assert.Equal($"{clientFailure.Verb} {clientFailure.Reason}", server.Reason);
    }

    [Theory]
    [InlineData("AT BRIXX")]
    [InlineData("CM 050; AT BRIXX")]
    public void ParseCompound_BareConditionAtEnd_StillAccepted(string input)
    {
        CompoundParseResult? client = CommandSchemeParser.ParseCompound(input, Scheme, out ParseFailure? failure);
        Assert.NotNull(client);
        Assert.Null(failure);

        ParseResult<CompoundCommand> server = CommandParser.ParseCompound(input);
        Assert.True(server.IsSuccess, server.Reason);
    }
}
