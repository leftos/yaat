using Xunit;
using Yaat.Sim.Speech.Context;
using Yaat.SpeechSandbox;

namespace Yaat.Client.Tests;

/// <summary>Which drawn callsigns may pad a feed's room: never one already there, one with a real callsign's digits, or one without telephony.</summary>
public sealed class DistractorRulesTests
{
    private static readonly HashSet<string> Existing = new(StringComparer.Ordinal) { "SWA2495" };
    private static readonly HashSet<string> RealDigits = new(StringComparer.Ordinal) { "2495" };

    [Theory]
    [InlineData("SWA2495")]
    [InlineData("swa2495")]
    public void Acceptable_RejectsAnExistingCallsignInAnyCase(string candidate) => Assert.False(Acceptable(candidate));

    [Theory]
    [InlineData("UAL2495")]
    [InlineData("N2495")]
    public void Acceptable_RejectsARealCallsignsDigits(string candidate) => Assert.False(Acceptable(candidate));

    [Fact]
    public void Acceptable_RejectsAnAirlineWithoutTelephony() => Assert.False(Acceptable("QQQ123"));

    [Fact]
    public void Acceptable_AcceptsANewAirlineCallsign() => Assert.True(Acceptable("UAL1234"));

    private static bool Acceptable(string candidate) =>
        DistractorRules.Acceptable(candidate, Existing, RealDigits, new Dictionary<string, string>(), DecoderSettings.Default);
}
