using Xunit;
using Yaat.Client.Models;

namespace Yaat.Client.Tests;

/// <summary>
/// Covers the duration formula and gating predicate that drive the opt-in speech-bubble overlay
/// on the Radar and Ground views (issues #9, #170): the content-length duration scaled by the
/// user multiplier, the SAY / pilot-speech gate, and the opt-in amber WARN bubbles.
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
        var d = AircraftSpeechBubble.ComputeDuration(text, 1.0);
        Assert.InRange(d.TotalSeconds, Math.Max(4.0, expectedSeconds - 0.01), Math.Max(4.0, expectedSeconds + 0.01));
    }

    [Fact]
    public void ComputeDuration_MidRange_ScalesLinearly()
    {
        // 60 chars → 2 + 5 = 7 s
        var d = AircraftSpeechBubble.ComputeDuration(new string('x', 60), 1.0);
        Assert.Equal(7.0, d.TotalSeconds, precision: 3);
    }

    [Fact]
    public void ComputeDuration_LongText_ClampsToTwelveSeconds()
    {
        // 200 chars → 2 + 16.67 = 18.67 → ceiling 12
        var d = AircraftSpeechBubble.ComputeDuration(new string('x', 200), 1.0);
        Assert.Equal(12.0, d.TotalSeconds, precision: 3);
    }

    [Fact]
    public void ComputeDuration_VeryLongText_StaysAtCeiling()
    {
        var d = AircraftSpeechBubble.ComputeDuration(new string('x', 10_000), 1.0);
        Assert.Equal(12.0, d.TotalSeconds, precision: 3);
    }

    [Theory]
    [InlineData(60, 2.0, 14.0)] // 7 s base × 2 = 14
    [InlineData(60, 0.5, 3.5)] // 7 s base × 0.5 = 3.5
    [InlineData(0, 2.0, 8.0)] // floor 4 s × 2 = 8
    [InlineData(200, 2.0, 24.0)] // ceiling 12 s × 2 = 24
    public void ComputeDuration_Multiplier_ScalesClampedBase(int length, double multiplier, double expectedSeconds)
    {
        var d = AircraftSpeechBubble.ComputeDuration(new string('x', length), multiplier);
        Assert.Equal(expectedSeconds, d.TotalSeconds, precision: 3);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void ComputeDuration_NonPositiveMultiplier_FallsBackToOne(double multiplier)
    {
        // 60 chars → 7 s base; a non-positive multiplier must not collapse the bubble to 0.
        var d = AircraftSpeechBubble.ComputeDuration(new string('x', 60), multiplier);
        Assert.Equal(7.0, d.TotalSeconds, precision: 3);
    }

    // --- TryBuild gating ---------------------------------------------------

    private static readonly DateTime FixedNow = new(2026, 5, 23, 12, 0, 0, DateTimeKind.Utc);

    private static AircraftSpeechBubble? Build(
        bool showSpeechBubbles = true,
        bool showWarningBubbles = false,
        bool soloMode = false,
        TerminalEntryKind kind = TerminalEntryKind.Say,
        string message = "Heading 270",
        double durationMultiplier = 1.0
    ) => AircraftSpeechBubble.TryBuild(showSpeechBubbles, showWarningBubbles, soloMode, kind, message, durationMultiplier, FixedNow);

    [Fact]
    public void TryBuild_AllConditionsMet_ReturnsSpeechBubble()
    {
        var bubble = Build();

        Assert.NotNull(bubble);
        Assert.Equal("Heading 270", bubble!.Text);
        Assert.Equal(SpeechBubbleSeverity.Speech, bubble.Severity);
        Assert.Equal(FixedNow + TimeSpan.FromSeconds(4.0), bubble.ExpiresAt);
    }

    [Fact]
    public void TryBuild_PilotSpeechKind_Allowed()
    {
        var bubble = Build(kind: TerminalEntryKind.PilotSpeech, message: "Clear of 28R");

        Assert.NotNull(bubble);
        Assert.Equal(SpeechBubbleSeverity.Speech, bubble!.Severity);
    }

    [Fact]
    public void TryBuild_Multiplier_ScalesExpiry()
    {
        var bubble = Build(message: new string('x', 60), durationMultiplier: 2.0);

        Assert.NotNull(bubble);
        Assert.Equal(FixedNow + TimeSpan.FromSeconds(14.0), bubble!.ExpiresAt);
    }

    [Fact]
    public void TryBuild_PrefOff_ReturnsNull()
    {
        Assert.Null(Build(showSpeechBubbles: false));
    }

    [Fact]
    public void TryBuild_SoloMode_SuppressesSpeechBubble()
    {
        Assert.Null(Build(soloMode: true));
    }

    [Theory]
    [InlineData(TerminalEntryKind.Command)]
    [InlineData(TerminalEntryKind.Response)]
    [InlineData(TerminalEntryKind.System)]
    [InlineData(TerminalEntryKind.Error)]
    [InlineData(TerminalEntryKind.Chat)]
    public void TryBuild_NonSpeechKind_ReturnsNull(TerminalEntryKind kind)
    {
        Assert.Null(Build(showWarningBubbles: true, kind: kind, message: "anything"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void TryBuild_EmptyOrNullMessage_ReturnsNull(string? message)
    {
        Assert.Null(Build(message: message!));
    }

    // --- WARN bubbles (opt-in, amber) -------------------------------------

    [Fact]
    public void TryBuild_WarningKind_OptInOff_ReturnsNull()
    {
        Assert.Null(Build(showWarningBubbles: false, kind: TerminalEntryKind.Warning, message: "queue cleared"));
    }

    [Fact]
    public void TryBuild_WarningKind_OptInOn_ReturnsWarningBubble()
    {
        var bubble = Build(showWarningBubbles: true, kind: TerminalEntryKind.Warning, message: "queue cleared");

        Assert.NotNull(bubble);
        Assert.Equal(SpeechBubbleSeverity.Warning, bubble!.Severity);
    }

    [Fact]
    public void TryBuild_WarningKind_InSoloMode_StillShown()
    {
        // Warnings are controller-facing (not TTS'd), so they bubble even in solo-training mode.
        var bubble = Build(showWarningBubbles: true, soloMode: true, kind: TerminalEntryKind.Warning, message: "queue cleared");

        Assert.NotNull(bubble);
        Assert.Equal(SpeechBubbleSeverity.Warning, bubble!.Severity);
    }

    [Fact]
    public void TryBuild_WarningKind_MasterToggleOff_ReturnsNull()
    {
        // The WARN opt-in still requires the master "show speech bubbles" switch.
        Assert.Null(Build(showSpeechBubbles: false, showWarningBubbles: true, kind: TerminalEntryKind.Warning, message: "queue cleared"));
    }

    [Fact]
    public void TryBuild_SayKind_WarnOptInOn_StaysSpeechSeverity()
    {
        var bubble = Build(showWarningBubbles: true, kind: TerminalEntryKind.Say);

        Assert.NotNull(bubble);
        Assert.Equal(SpeechBubbleSeverity.Speech, bubble!.Severity);
    }
}
