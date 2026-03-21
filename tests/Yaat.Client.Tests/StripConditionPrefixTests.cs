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
}
