using Xunit;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

public class CommandInputParseTests
{
    private static readonly CommandScheme Scheme = CommandScheme.Default();

    private static CommandInputParseResult? Parse(string text) => CommandInputController.ParseCommandInput(text, text.Length, Scheme);

    private static CommandInputParseResult? ParseAt(string text, int caret) => CommandInputController.ParseCommandInput(text, caret, Scheme);

    [Fact]
    public void NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(Parse(""));
        Assert.Null(Parse("   "));
    }

    [Fact]
    public void BareVerb_NoTrailingSpace_FindsVerbAtIndex0()
    {
        var result = Parse("FH");

        Assert.NotNull(result);
        Assert.Equal(0, result.VerbIndex);
        Assert.Equal("FH", result.Verb);
        Assert.Equal(CanonicalCommandType.FlyHeading, result.CommandType);
        Assert.NotNull(result.Definition);
        Assert.False(result.HasTrailingSpace);
        // Cursor is on the verb token itself — paramIndex is -1 (caret on verb, not on an arg)
        Assert.Equal(-1, result.ParameterIndex);
    }

    [Fact]
    public void BareVerb_WithTrailingSpace_ParamIndex0()
    {
        var result = Parse("FH ");

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
        var result = Parse("FH 270");

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
        var result = Parse("FH 270 ");

        Assert.NotNull(result);
        Assert.Equal(1, result.ParameterIndex);
        Assert.Single(result.TypedArgs);
        Assert.True(result.HasTrailingSpace);
    }

    [Fact]
    public void CallsignThenVerb_FindsVerbAtIndex1()
    {
        var result = Parse("SWA123 CM 5000");

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
        var result = Parse("RWY ");

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
        var result = Parse("RWY 28R");

        Assert.NotNull(result);
        Assert.Equal(CanonicalCommandType.AssignRunway, result.CommandType);
        Assert.Equal(0, result.ParameterIndex);
        Assert.Single(result.TypedArgs);
        Assert.Equal("28R", result.TypedArgs[0]);
    }

    [Fact]
    public void CompoundCommand_ParsesLastFragment()
    {
        var result = Parse("FH 270; CM 5000");

        Assert.NotNull(result);
        Assert.Equal("CM", result.Verb);
        Assert.Equal(CanonicalCommandType.ClimbMaintain, result.CommandType);
        Assert.Single(result.TypedArgs);
        Assert.Equal("5000", result.TypedArgs[0]);
    }

    [Fact]
    public void ConditionPrefix_LV_StripsAndParsesVerb()
    {
        var result = Parse("LV 050 FH 270");

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
        var result = Parse("AT SUN");

        Assert.NotNull(result);
        Assert.Equal("AT", result.ConditionVerb);
        Assert.Equal("", result.StrippedFragment);
        Assert.Equal(-1, result.VerbIndex);
        Assert.Null(result.Verb);
    }

    [Fact]
    public void UnknownToken_NoVerbFound()
    {
        var result = Parse("NOTAVERB");

        Assert.NotNull(result);
        Assert.Equal(-1, result.VerbIndex);
        Assert.Null(result.Verb);
        Assert.Null(result.CommandType);
        Assert.Null(result.Definition);
    }

    [Fact]
    public void Aliases_MatchSchemeCustomization()
    {
        var result = Parse("FH ");

        Assert.NotNull(result);
        Assert.Contains("FH", result.Aliases);
    }

    [Fact]
    public void MultipleArgs_TracksAllTypedArgs()
    {
        // PTAC takes heading, distance, approach
        var result = Parse("PTAC 180 10 ");

        Assert.NotNull(result);
        Assert.Equal("PTAC", result.Verb);
        Assert.Equal(2, result.TypedArgs.Length);
        Assert.Equal("180", result.TypedArgs[0]);
        Assert.Equal("10", result.TypedArgs[1]);
        Assert.Equal(2, result.ParameterIndex);
    }

    // --- Cursor-aware tests ---

    [Fact]
    public void Caret_AtEndOfVerb_ActiveTokenSpansVerb()
    {
        // "FH" with caret at 2 (end of "FH")
        var result = ParseAt("FH", 2);

        Assert.NotNull(result);
        Assert.Equal(0, result.ActiveTokenIndex);
        Assert.Equal(0, result.ActiveTokenStart);
        Assert.Equal(2, result.ActiveTokenEnd);
        Assert.Equal(2, result.CaretIndex);
    }

    [Fact]
    public void Caret_InMiddleOfFirstArg_ParamIndex0()
    {
        // "FH 270" caret at 4 (between '2' and '7')
        var result = ParseAt("FH 270", 4);

        Assert.NotNull(result);
        Assert.Equal(0, result.VerbIndex);
        Assert.Equal(1, result.ActiveTokenIndex);
        Assert.Equal(3, result.ActiveTokenStart);
        Assert.Equal(6, result.ActiveTokenEnd);
        Assert.Equal(0, result.ParameterIndex);
        Assert.False(result.HasTrailingSpace);
    }

    [Fact]
    public void Caret_OnVerb_WhenMultipleTokens_ActiveTokenIsVerb()
    {
        // "FH 270 D5L" caret at 1 (in "FH")
        var result = ParseAt("FH 270 D5L", 1);

        Assert.NotNull(result);
        Assert.Equal(0, result.VerbIndex);
        Assert.Equal(0, result.ActiveTokenIndex);
        Assert.Equal(0, result.ActiveTokenStart);
        Assert.Equal(2, result.ActiveTokenEnd);
        // ParamIndex is -1: cursor is on the verb itself, not on any argument.
        Assert.Equal(-1, result.ParameterIndex);
    }

    [Fact]
    public void Caret_InWhitespaceBetweenTokens_HasTrailingSpaceTrue()
    {
        // "FH 270 D" caret at 7 (right after the space, before 'D')
        var result = ParseAt("FH 270 D", 7);

        Assert.NotNull(result);
        // Cursor at start of "D" — IDE-style: on the next token (D)
        Assert.Equal(2, result.ActiveTokenIndex);
        Assert.Equal(7, result.ActiveTokenStart);
        Assert.Equal(8, result.ActiveTokenEnd);
        Assert.False(result.HasTrailingSpace);
        Assert.Equal(1, result.ParameterIndex);
    }

    [Fact]
    public void Caret_InTrueWhitespace_GapBetweenTokens_HasTrailingSpaceTrue()
    {
        // "FH  270" with TWO spaces, caret at 3 (in middle of double-space)
        var result = ParseAt("FH  270", 3);

        Assert.NotNull(result);
        // The caret sits on whitespace — between verb and first arg.
        Assert.True(result.HasTrailingSpace);
        Assert.Equal(0, result.ParameterIndex);
    }

    [Fact]
    public void Caret_BeforeFragmentSeparator_DoesNotLeakIntoNextFragment()
    {
        // "AAL FH 270; CM 5000" caret at 8 (in "270" of first fragment)
        var result = ParseAt("AAL FH 270; CM 5000", 8);

        Assert.NotNull(result);
        Assert.Equal("FH", result.Verb);
        Assert.Equal(2, result.ActiveTokenIndex);
        // Active token is "270" at positions 7-10 in full text
        Assert.Equal(7, result.ActiveTokenStart);
        Assert.Equal(10, result.ActiveTokenEnd);
    }

    [Fact]
    public void Caret_AfterFragmentSeparator_ParsesNextFragment()
    {
        // "FH 270; CM 5000" caret at 11 (in "CM")
        var result = ParseAt("FH 270; CM 5000", 11);

        Assert.NotNull(result);
        Assert.Equal("CM", result.Verb);
    }

    [Fact]
    public void Caret_AtTokenStart_OnSpaceBoundary_TreatsAsEditingNextToken()
    {
        // "FH 270 D5L" caret at 7 (right after space, at start of "D5L")
        var result = ParseAt("FH 270 D5L", 7);

        Assert.NotNull(result);
        Assert.Equal(2, result.ActiveTokenIndex);
        Assert.Equal(7, result.ActiveTokenStart);
        Assert.Equal(10, result.ActiveTokenEnd);
        Assert.False(result.HasTrailingSpace);
    }

    [Fact]
    public void Caret_InConditionArg_FallsBackToPartialResult()
    {
        // "AT SUN" caret at 5 (in middle of "SUN")
        var result = ParseAt("AT SUN", 5);

        Assert.NotNull(result);
        Assert.Equal("AT", result.ConditionVerb);
        // Active token spans "SUN"
        Assert.Equal(3, result.ActiveTokenStart);
        Assert.Equal(6, result.ActiveTokenEnd);
    }

    [Fact]
    public void TypedArgs_IncludesAllArgsRegardlessOfCursor()
    {
        // "FH 270 D5L" caret in middle of "270" — TypedArgs should still include both
        var result = ParseAt("FH 270 D5L", 4);

        Assert.NotNull(result);
        Assert.Equal(2, result.TypedArgs.Length);
        Assert.Equal("270", result.TypedArgs[0]);
        Assert.Equal("D5L", result.TypedArgs[1]);
    }
}
