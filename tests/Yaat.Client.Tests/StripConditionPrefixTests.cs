using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

public class StripConditionPrefixTests
{
    // -------------------------------------------------------------------------
    // AS prefix
    // -------------------------------------------------------------------------

    [Fact]
    public void As_WithPositionAndCommand_StripsToCommand()
    {
        var result = CommandInputController.StripConditionPrefix("AS 4U TRACK", out var verb);
        Assert.Equal("TRACK", result);
        Assert.Equal("AS", verb);
    }

    [Fact]
    public void As_WithPositionAndCommandWithArgs_StripsToCommandAndArgs()
    {
        var result = CommandInputController.StripConditionPrefix("AS 4U TRACK AAL123", out var verb);
        Assert.Equal("TRACK AAL123", result);
        Assert.Equal("AS", verb);
    }

    [Fact]
    public void As_StillTypingPosition_ReturnsEmpty()
    {
        var result = CommandInputController.StripConditionPrefix("AS 4U", out var verb);
        Assert.Equal("", result);
        Assert.Equal("AS", verb);
    }

    [Fact]
    public void As_JustPrefixAndSpace_ReturnsEmpty()
    {
        var result = CommandInputController.StripConditionPrefix("AS ", out var verb);
        Assert.Equal("", result);
        Assert.Equal("AS", verb);
    }

    [Fact]
    public void As_PositionWithTrailingSpace_StripsToEmpty()
    {
        var result = CommandInputController.StripConditionPrefix("AS 4U ", out var verb);
        Assert.Equal("", result.TrimStart());
        Assert.Equal("AS", verb);
    }

    [Fact]
    public void As_PartialCommand_StripsToPartial()
    {
        var result = CommandInputController.StripConditionPrefix("AS 4U T", out var verb);
        Assert.Equal("T", result);
        Assert.Equal("AS", verb);
    }

    // -------------------------------------------------------------------------
    // Existing LV/AT still work
    // -------------------------------------------------------------------------

    [Fact]
    public void Lv_WithArgAndCommand_StripsToCommand()
    {
        var result = CommandInputController.StripConditionPrefix("LV 050 C80", out var verb);
        Assert.Equal("C80", result);
        Assert.Equal("LV", verb);
    }

    [Fact]
    public void At_WithArgAndCommand_StripsToCommand()
    {
        var result = CommandInputController.StripConditionPrefix("AT SUNOL D100", out var verb);
        Assert.Equal("D100", result);
        Assert.Equal("AT", verb);
    }

    // -------------------------------------------------------------------------
    // No prefix
    // -------------------------------------------------------------------------

    [Fact]
    public void NoPrefix_ReturnsOriginal()
    {
        var result = CommandInputController.StripConditionPrefix("TRACK AAL123", out var verb);
        Assert.Equal("TRACK AAL123", result);
        Assert.Null(verb);
    }

    [Fact]
    public void As_Standalone_NoTrailingSpace_ReturnsOriginal()
    {
        // "AS" without trailing space should NOT be treated as a prefix
        // (it's the SetActivePosition verb)
        var result = CommandInputController.StripConditionPrefix("AS", out var verb);
        Assert.Equal("AS", result);
        Assert.Null(verb);
    }

    // -------------------------------------------------------------------------
    // ATFN (at-fix-nautical-miles) — numeric distance arg
    // -------------------------------------------------------------------------

    [Fact]
    public void Atfn_WithDistanceAndCommand_StripsToCommand()
    {
        var result = CommandInputController.StripConditionPrefix("ATFN 5 FH 270", out var verb);
        Assert.Equal("FH 270", result);
        Assert.Equal("ATFN", verb);
    }

    [Fact]
    public void Atfn_StillTypingDistance_ReturnsEmpty()
    {
        var result = CommandInputController.StripConditionPrefix("ATFN 5", out var verb);
        Assert.Equal("", result);
        Assert.Equal("ATFN", verb);
    }

    // -------------------------------------------------------------------------
    // ONHO (on-handoff) — zero-arg condition
    // -------------------------------------------------------------------------

    [Fact]
    public void Onho_WithCommand_StripsToCommand()
    {
        var result = CommandInputController.StripConditionPrefix("ONHO FH 270", out var verb);
        Assert.Equal("FH 270", result);
        Assert.Equal("ONHO", verb);
    }

    [Fact]
    public void Onho_BareKeyword_NoTrailingSpace_ReturnsOriginal()
    {
        var result = CommandInputController.StripConditionPrefix("ONHO", out var verb);
        Assert.Equal("ONHO", result);
        Assert.Null(verb);
    }

    [Fact]
    public void Onho_TrailingSpaceOnly_ReturnsEmpty()
    {
        var result = CommandInputController.StripConditionPrefix("ONHO ", out var verb);
        Assert.Equal("", result);
        Assert.Equal("ONHO", verb);
    }

    // -------------------------------------------------------------------------
    // GW alias for GIVEWAY
    // -------------------------------------------------------------------------

    [Fact]
    public void Gw_WithCallsignAndCommand_StripsToCommandWithGivewayVerb()
    {
        var result = CommandInputController.StripConditionPrefix("GW UAL456 FH 270", out var verb);
        Assert.Equal("FH 270", result);
        Assert.Equal("GIVEWAY", verb);
    }
}
