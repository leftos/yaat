using Xunit;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

public class SpokenFormDrawsTests
{
    private static readonly string[] NoOthers = [];

    [Fact]
    public void CanShortenCallsign_AcknowledgedOnFrequencyWithNoSimilarCallsign_IsTrue() =>
        Assert.True(SpokenFormDraws.CanShortenCallsign("N123AB", acknowledged: true, leftStudentFrequency: false, ["N456CD", "AAL123"]));

    [Fact]
    public void CanShortenCallsign_NotAcknowledged_IsFalse() =>
        Assert.False(SpokenFormDraws.CanShortenCallsign("N123AB", acknowledged: false, leftStudentFrequency: false, NoOthers));

    [Fact]
    public void CanShortenCallsign_LeftTheFrequency_IsFalse() =>
        Assert.False(SpokenFormDraws.CanShortenCallsign("N123AB", acknowledged: true, leftStudentFrequency: true, NoOthers));

    [Theory]
    [InlineData("N456AB")]
    [InlineData("n456ab")]
    [InlineData("SWA2AB")]
    public void CanShortenCallsign_SimilarCallsignOnFrequency_IsFalse(string other) =>
        Assert.False(SpokenFormDraws.CanShortenCallsign("N123AB", acknowledged: true, leftStudentFrequency: false, [other]));

    [Fact]
    public void CanShortenCallsign_OwnCallsignInTheList_IsNotASimilarCallsign() =>
        Assert.True(SpokenFormDraws.CanShortenCallsign("N123AB", acknowledged: true, leftStudentFrequency: false, ["N123AB"]));

    [Theory]
    [InlineData("AAL123")]
    [InlineData("N12A")]
    public void CanShortenCallsign_NotAnEligibleNNumber_IsFalse(string callsign) =>
        Assert.False(SpokenFormDraws.CanShortenCallsign(callsign, acknowledged: true, leftStudentFrequency: false, NoOthers));

    // Traits at seed 1 are pinned by PilotTraitsTests.For_IsPinned: N0000A terse, N0004A normal, N0003A chatty.
    [Theory]
    [InlineData("N0000A", 70, 90)]
    [InlineData("N0004A", 40, 60)]
    [InlineData("N0003A", 8, 22)]
    public void DrawShortCallsign_ShareFollowsTheTrait(string callsign, int minPercent, int maxPercent)
    {
        const int seconds = 200;
        int shortened = Enumerable.Range(0, seconds).Count(s => SpokenFormDraws.DrawShortCallsign(callsign, rngSeed: 1, elapsedSecond: s));

        int percent = shortened * 100 / seconds;

        Assert.InRange(percent, minPercent, maxPercent);
    }

    [Fact]
    public void CanShortenCallsign_OtherCallsignSharingOnlyTheLastCharacter_StillAllowsIt() =>
        Assert.True(SpokenFormDraws.CanShortenCallsign("N123AB", acknowledged: true, leftStudentFrequency: false, ["N456CB"]));

    [Fact]
    public void DrawGroupedContactFrequency_Quiet_NeverGroups() =>
        Assert.DoesNotContain(
            Enumerable.Range(0, 200),
            s => SpokenFormDraws.DrawGroupedContactFrequency("N123AB", FrequencyLoadBand.Quiet, rngSeed: 1, elapsedSecond: s)
        );

    [Theory]
    [InlineData(FrequencyLoadBand.Busy, 65, 85)]
    [InlineData(FrequencyLoadBand.Normal, 8, 22)]
    public void DrawGroupedContactFrequency_ShareFollowsTheBand(FrequencyLoadBand band, int minPercent, int maxPercent)
    {
        const int seconds = 200;
        int grouped = Enumerable
            .Range(0, seconds)
            .Count(s => SpokenFormDraws.DrawGroupedContactFrequency("N123AB", band, rngSeed: 1, elapsedSecond: s));

        int percent = grouped * 100 / seconds;

        Assert.InRange(percent, minPercent, maxPercent);
    }

    [Fact]
    public void DrawShortCallsign_IsDeterministic()
    {
        bool[] first = [.. Enumerable.Range(0, 50).Select(s => SpokenFormDraws.DrawShortCallsign("N123AB", 7, s))];
        bool[] second = [.. Enumerable.Range(0, 50).Select(s => SpokenFormDraws.DrawShortCallsign("N123AB", 7, s))];

        Assert.Equal(first, second);
    }
}
