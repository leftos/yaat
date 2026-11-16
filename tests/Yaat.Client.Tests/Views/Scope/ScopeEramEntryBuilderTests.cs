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
        ScopeEramEntryDto dto = ScopeEramEntryBuilder.Build("QQ 250 UAL238", null);

        Assert.Equal(["QQ", "250", "UAL238"], dto.Tokens);
        Assert.False(dto.InvertNumericKeypad);
    }

    [Fact]
    public void ExtraSpaces_AreCollapsed() =>
        Assert.Equal(["QS", "/250", "UAL238"], ScopeEramEntryBuilder.Build("  QS   /250  UAL238 ", null).Tokens);

    [Fact]
    public void LowercaseText_IsSentUppercase_AsCrcsKeyboardTypesIt() =>
        Assert.Equal(["QQ", "250", "UAL238"], ScopeEramEntryBuilder.Build("qq 250 ual238", null).Tokens);

    // A clicked track's callsign goes as a pick of the last token equal to it, its text kept in the tokens; no pick when the
    // entry holds no clicked track or its callsign was edited after the click.

    [Fact]
    public void AClickedTrack_IsSentAsAPick_OfItsToken()
    {
        ScopeEramEntryDto dto = ScopeEramEntryBuilder.Build("QP 44 AAL123", "AAL123");

        Assert.Equal(["QP", "44", "AAL123"], dto.Tokens);
        Assert.Equal([new ScopeEramPickDto(2, "AAL123")], dto.Picks);
    }

    [Fact]
    public void AClickedTrack_InALowercaseEntry_IsPickedOnItsUppercaseToken()
    {
        ScopeEramEntryDto dto = ScopeEramEntryBuilder.Build("qq 110 aal123", "AAL123");

        Assert.Equal(["QQ", "110", "AAL123"], dto.Tokens);
        Assert.Equal([new ScopeEramPickDto(2, "AAL123")], dto.Picks);
    }

    [Fact]
    public void AClickedTrack_WhoseCallsignWasEdited_IsNotPicked() => Assert.Null(ScopeEramEntryBuilder.Build("QP 44 AAL12", "AAL123").Picks);

    [Fact]
    public void AClickedTrack_NamedTwice_IsPickedOnTheLastToken() =>
        Assert.Equal([new ScopeEramPickDto(2, "AAL123")], ScopeEramEntryBuilder.Build("AAL123 QQ AAL123", "AAL123").Picks);

    // A clicked callsign in free text goes as plain text: in an SM, SW or WX entry, or at or after the free-text marker (the
    // backtick, or the clear-weather character the Scope maps it to).

    [Fact]
    public void AClickedTrack_InAnSmEntry_IsNotPicked() => Assert.Null(ScopeEramEntryBuilder.Build("SM 44 UAL238", "UAL238").Picks);

    [Fact]
    public void AClickedTrack_InAnSwEntry_IsNotPicked() => Assert.Null(ScopeEramEntryBuilder.Build("SW CB UAL238", "UAL238").Picks);

    [Fact]
    public void AClickedTrack_InAWxEntry_IsNotPicked() => Assert.Null(ScopeEramEntryBuilder.Build("WX UAL238", "UAL238").Picks);

    [Fact]
    public void AClickedTrack_InAnInterimAltitude_IsStillPicked() =>
        Assert.Equal([new ScopeEramPickDto(2, "UAL238")], ScopeEramEntryBuilder.Build("QQ 110 UAL238", "UAL238").Picks);

    [Fact]
    public void AClickedTrack_AfterTheMappedFreeTextMarker_IsNotPicked() =>
        Assert.Null(ScopeEramEntryBuilder.Build($"QQ {ScopeEramKeys.ClearWeather}TEXT UAL238", "UAL238").Picks);

    [Fact]
    public void AClickedTrack_BeforeTheFreeTextMarker_IsStillPicked() =>
        Assert.Equal(
            [new ScopeEramPickDto(2, "UAL238")],
            ScopeEramEntryBuilder.Build($"QQ 110 UAL238 {ScopeEramKeys.ClearWeather}TEXT", "UAL238").Picks
        );

    [Fact]
    public void AnEntryWithNoClickedTrack_HasNoPick() => Assert.Null(ScopeEramEntryBuilder.Build("QQ 110 AAL123", null).Picks);

    [Fact]
    public void AClickedTrackAlone_IsPickedOnTheOnlyToken() =>
        Assert.Equal([new ScopeEramPickDto(0, "AAL123")], ScopeEramEntryBuilder.Build("AAL123", "AAL123").Picks);

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
