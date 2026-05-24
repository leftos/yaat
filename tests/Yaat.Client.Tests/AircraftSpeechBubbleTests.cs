using Xunit;
using Yaat.Client.Models;

namespace Yaat.Client.Tests;

/// <summary>
/// Covers the duration formula and gating predicate that drive the opt-in
/// speech-bubble overlay on the Radar and Ground views (issue #9).
/// </summary>
public class AircraftSpeechBubbleTests
{
    // --- ComputeDuration ---------------------------------------------------

    [Theory]
    [InlineData("", 4.0)] // empty → floor
    [InlineData("Hi", 4.0)] // 2 chars → 2 + 0.17 = 2.17 → floor 4
    [InlineData("Heading 270", 4.0)] // 11 chars → 2.92 → floor 4
    [InlineData("Heading 270, direct MENLO", 4.083)] // 25 chars → 2 + 2.083 = 4.083
    public void ComputeDuration_BelowFloor_ClampsToFourSeconds(string text, double expectedSeconds)
    {
        var d = AircraftSpeechBubble.ComputeDuration(text);
        Assert.InRange(d.TotalSeconds, Math.Max(4.0, expectedSeconds - 0.01), Math.Max(4.0, expectedSeconds + 0.01));
    }

    [Fact]
    public void ComputeDuration_MidRange_ScalesLinearly()
    {
        // 60 chars → 2 + 5 = 7 s
        var d = AircraftSpeechBubble.ComputeDuration(new string('x', 60));
        Assert.Equal(7.0, d.TotalSeconds, precision: 3);
    }

    [Fact]
    public void ComputeDuration_LongText_ClampsToTwelveSeconds()
    {
        // 200 chars → 2 + 16.67 = 18.67 → ceiling 12
        var d = AircraftSpeechBubble.ComputeDuration(new string('x', 200));
        Assert.Equal(12.0, d.TotalSeconds, precision: 3);
    }

    [Fact]
    public void ComputeDuration_VeryLongText_StaysAtCeiling()
    {
        var d = AircraftSpeechBubble.ComputeDuration(new string('x', 10_000));
        Assert.Equal(12.0, d.TotalSeconds, precision: 3);
    }

    // --- TryBuild gating ---------------------------------------------------

    private static readonly DateTime FixedNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TryBuild_AllConditionsMet_ReturnsBubble()
    {
        var bubble = AircraftSpeechBubble.TryBuild(
            showSpeechBubbles: true,
            soloMode: false,
            kind: TerminalEntryKind.Say,
            message: "Heading 270",
            nowUtc: FixedNow
        );

        Assert.NotNull(bubble);
        Assert.Equal("Heading 270", bubble!.Text);
        Assert.Equal(FixedNow + TimeSpan.FromSeconds(4.0), bubble.ExpiresAt);
    }

    [Fact]
    public void TryBuild_PilotSpeechKind_Allowed()
    {
        var bubble = AircraftSpeechBubble.TryBuild(
            showSpeechBubbles: true,
            soloMode: false,
            kind: TerminalEntryKind.PilotSpeech,
            message: "Clear of 28R",
            nowUtc: FixedNow
        );

        Assert.NotNull(bubble);
    }

    [Fact]
    public void TryBuild_PrefOff_ReturnsNull()
    {
        var bubble = AircraftSpeechBubble.TryBuild(
            showSpeechBubbles: false,
            soloMode: false,
            kind: TerminalEntryKind.Say,
            message: "Heading 270",
            nowUtc: FixedNow
        );

        Assert.Null(bubble);
    }

    [Fact]
    public void TryBuild_SoloMode_ReturnsNull()
    {
        var bubble = AircraftSpeechBubble.TryBuild(
            showSpeechBubbles: true,
            soloMode: true,
            kind: TerminalEntryKind.Say,
            message: "Heading 270",
            nowUtc: FixedNow
        );

        Assert.Null(bubble);
    }

    [Theory]
    [InlineData(TerminalEntryKind.Command)]
    [InlineData(TerminalEntryKind.Response)]
    [InlineData(TerminalEntryKind.System)]
    [InlineData(TerminalEntryKind.Warning)]
    [InlineData(TerminalEntryKind.Error)]
    [InlineData(TerminalEntryKind.Chat)]
    public void TryBuild_NonSpeechKind_ReturnsNull(TerminalEntryKind kind)
    {
        var bubble = AircraftSpeechBubble.TryBuild(showSpeechBubbles: true, soloMode: false, kind: kind, message: "anything", nowUtc: FixedNow);

        Assert.Null(bubble);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void TryBuild_EmptyOrNullMessage_ReturnsNull(string? message)
    {
        var bubble = AircraftSpeechBubble.TryBuild(
            showSpeechBubbles: true,
            soloMode: false,
            kind: TerminalEntryKind.Say,
            message: message!,
            nowUtc: FixedNow
        );

        Assert.Null(bubble);
    }
}
