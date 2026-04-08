using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CommandInputParseTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    [Fact]
    public void NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(CommandInputController.ParseCommandInput("", Scheme));
        Assert.Null(CommandInputController.ParseCommandInput("   ", Scheme));
    }

    [Fact]
    public void BareVerb_NoTrailingSpace_FindsVerbAtIndex0()
    {
        var result = CommandInputController.ParseCommandInput("FH", Scheme);

        Assert.NotNull(result);
        Assert.Equal(0, result.VerbIndex);
        Assert.Equal("FH", result.Verb);
        Assert.Equal(CanonicalCommandType.FlyHeading, result.CommandType);
        Assert.NotNull(result.Definition);
        Assert.False(result.HasTrailingSpace);
        Assert.Equal(0, result.ParameterIndex);
    }

    [Fact]
    public void BareVerb_WithTrailingSpace_ParamIndex0()
    {
        var result = CommandInputController.ParseCommandInput("FH ", Scheme);

        Assert.NotNull(result);
        Assert.Equal(0, result.VerbIndex);
        Assert.Equal("FH", result.Verb);
        Assert.True(result.HasTrailingSpace);
        Assert.Equal(0, result.ParameterIndex);
        Assert.Empty(result.TypedArgs);
    }

    [Fact]
    public void VerbWithArg_NoTrailingSpace_ParamIndex0()
    {
        var result = CommandInputController.ParseCommandInput("FH 270", Scheme);

        Assert.NotNull(result);
        Assert.Equal(0, result.VerbIndex);
        Assert.Equal("FH", result.Verb);
        Assert.Equal(0, result.ParameterIndex);
        Assert.Single(result.TypedArgs);
        Assert.Equal("270", result.TypedArgs[0]);
        Assert.False(result.HasTrailingSpace);
    }

    [Fact]
    public void VerbWithArg_TrailingSpace_ParamIndex1()
    {
        var result = CommandInputController.ParseCommandInput("FH 270 ", Scheme);

        Assert.NotNull(result);
        Assert.Equal(1, result.ParameterIndex);
        Assert.Single(result.TypedArgs);
        Assert.True(result.HasTrailingSpace);
    }

    [Fact]
    public void CallsignThenVerb_FindsVerbAtIndex1()
    {
        var result = CommandInputController.ParseCommandInput("SWA123 CM 5000", Scheme);

        Assert.NotNull(result);
        Assert.Equal(1, result.VerbIndex);
        Assert.Equal("CM", result.Verb);
        Assert.Equal(CanonicalCommandType.ClimbMaintain, result.CommandType);
        Assert.Single(result.TypedArgs);
        Assert.Equal("5000", result.TypedArgs[0]);
    }

    [Fact]
    public void Rwy_ResolvesToAssignRunway()
    {
        var result = CommandInputController.ParseCommandInput("RWY ", Scheme);

        Assert.NotNull(result);
        Assert.Equal(0, result.VerbIndex);
        Assert.Equal("RWY", result.Verb);
        Assert.Equal(CanonicalCommandType.AssignRunway, result.CommandType);
        Assert.NotNull(result.Definition);
        Assert.Equal("Assign Runway", result.Definition.Label);
        Assert.Equal(0, result.ParameterIndex);
    }

    [Fact]
    public void Rwy_WithRunwayArg_ParamIndex0()
    {
        var result = CommandInputController.ParseCommandInput("RWY 28R", Scheme);

        Assert.NotNull(result);
        Assert.Equal(CanonicalCommandType.AssignRunway, result.CommandType);
        Assert.Equal(0, result.ParameterIndex);
        Assert.Single(result.TypedArgs);
        Assert.Equal("28R", result.TypedArgs[0]);
    }

    [Fact]
    public void CompoundCommand_ParsesLastFragment()
    {
        var result = CommandInputController.ParseCommandInput("FH 270; CM 5000", Scheme);

        Assert.NotNull(result);
        Assert.Equal("CM", result.Verb);
        Assert.Equal(CanonicalCommandType.ClimbMaintain, result.CommandType);
        Assert.Single(result.TypedArgs);
        Assert.Equal("5000", result.TypedArgs[0]);
    }

    [Fact]
    public void ConditionPrefix_LV_StripsAndParsesVerb()
    {
        var result = CommandInputController.ParseCommandInput("LV 050 FH 270", Scheme);

        Assert.NotNull(result);
        Assert.Equal("LV", result.ConditionVerb);
        Assert.Equal("FH", result.Verb);
        Assert.Equal(CanonicalCommandType.FlyHeading, result.CommandType);
        Assert.Single(result.TypedArgs);
        Assert.Equal("270", result.TypedArgs[0]);
    }

    [Fact]
    public void ConditionPrefix_AT_StillTypingArg_ReturnsPartialResult()
    {
        var result = CommandInputController.ParseCommandInput("AT SUN", Scheme);

        Assert.NotNull(result);
        Assert.Equal("AT", result.ConditionVerb);
        Assert.Equal("", result.StrippedFragment);
        Assert.Equal(-1, result.VerbIndex);
        Assert.Null(result.Verb);
    }

    [Fact]
    public void UnknownToken_NoVerbFound()
    {
        var result = CommandInputController.ParseCommandInput("NOTAVERB", Scheme);

        Assert.NotNull(result);
        Assert.Equal(-1, result.VerbIndex);
        Assert.Null(result.Verb);
        Assert.Null(result.CommandType);
        Assert.Null(result.Definition);
    }

    [Fact]
    public void Aliases_MatchSchemeCustomization()
    {
        var result = CommandInputController.ParseCommandInput("FH ", Scheme);

        Assert.NotNull(result);
        Assert.Contains("FH", result.Aliases);
    }

    [Fact]
    public void MultipleArgs_TracksAllTypedArgs()
    {
        // PTAC takes heading, distance, approach
        var result = CommandInputController.ParseCommandInput("PTAC 180 10 ", Scheme);

        Assert.NotNull(result);
        Assert.Equal("PTAC", result.Verb);
        Assert.Equal(2, result.TypedArgs.Length);
        Assert.Equal("180", result.TypedArgs[0]);
        Assert.Equal("10", result.TypedArgs[1]);
        Assert.Equal(2, result.ParameterIndex);
    }
}
