using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope.Entries;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// What an ERAM entry typed on the Scope sends to the server (one token per whitespace run, as CRC's keyboard sends it), and
/// how the server's answer reads in the Scope's response: the feedback lines as CRC shows them, a readout's response text, or
/// the refusal in red.
/// </summary>
public class ScopeEramEntryBuilderTests
{
    [Fact]
    public void AnEntry_IsOneTokenPerWord()
    {
        ScopeEramEntryDto dto = ScopeEramEntryBuilder.Build("QQ 250 UAL238");

        Assert.Equal(["QQ", "250", "UAL238"], dto.Tokens);
        Assert.False(dto.InvertNumericKeypad);
    }

    [Fact]
    public void ExtraSpaces_AreCollapsed() => Assert.Equal(["QS", "/250", "UAL238"], ScopeEramEntryBuilder.Build("  QS   /250  UAL238 ").Tokens);

    [Fact]
    public void LowercaseText_IsSentUppercase_AsCrcsKeyboardTypesIt() =>
        Assert.Equal(["QQ", "250", "UAL238"], ScopeEramEntryBuilder.Build("qq 250 ual238").Tokens);

    [Fact]
    public void AnAccept_ShowsEveryFeedbackLine()
    {
        ScopeEntryResponse response = ScopeEramEntryBuilder.ResponseTo(
            new ScopeEramEntryResultDto(true, ["ACCEPT", "INTERIM ALT", "UAL238"], null),
            "QQ 250 UAL238"
        );

        Assert.Equal(ScopeResponseKind.Accept, response.Kind);
        Assert.Equal("ACCEPT", response.Message);
        Assert.Equal(["INTERIM ALT", "UAL238"], response.Detail);
        Assert.Equal("QQ 250 UAL238", response.Echo);
    }

    [Fact]
    public void AReadout_ShowsItsResponseText()
    {
        ScopeEntryResponse response = ScopeEramEntryBuilder.ResponseTo(
            new ScopeEramEntryResultDto(true, ["ACCEPT", "FLIGHT PLAN READOUT"], "UAL238 B738/L\nKSFO SAC"),
            "QF UAL238"
        );

        Assert.Equal(ScopeResponseKind.Text, response.Kind);
        Assert.Equal("UAL238 B738/L", response.Message);
        Assert.Equal(["KSFO SAC"], response.Detail);
    }

    [Fact]
    public void ARefusal_IsAnError_WithItsLine()
    {
        ScopeEntryResponse response = ScopeEramEntryBuilder.ResponseTo(
            new ScopeEramEntryResultDto(false, ["NOT YOUR CONTROL"], null),
            "QQ 250 UAL238"
        );

        Assert.Equal(ScopeResponseKind.Error, response.Kind);
        Assert.Equal("NOT YOUR CONTROL", response.Message);
        Assert.Equal("QQ 250 UAL238", response.Echo);
        Assert.Empty(response.Detail);
    }

    [Fact]
    public void ARefusalWithNoLine_StillReadsAsAnError() =>
        Assert.Equal(ScopeResponseKind.Error, ScopeEramEntryBuilder.ResponseTo(new ScopeEramEntryResultDto(false, [], null), "QT").Kind);
}
