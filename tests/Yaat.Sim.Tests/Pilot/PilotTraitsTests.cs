using Xunit;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

public class PilotTraitsTests
{
    private const string Body = "climb and maintain five thousand";

    private static string Callsign(int i) => $"N{i:D4}A";

    [Fact]
    public void For_SplitsTraitsAboutQuarterHalfQuarter()
    {
        const int total = 4000;
        int terse = 0;
        int normal = 0;
        int chatty = 0;
        for (int i = 0; i < total; i++)
        {
            switch (PilotTraits.For(Callsign(i), 1))
            {
                case PilotTrait.Terse:
                    terse++;
                    break;
                case PilotTrait.Normal:
                    normal++;
                    break;
                case PilotTrait.Chatty:
                    chatty++;
                    break;
            }
        }

        Assert.InRange(terse, total * 20 / 100, total * 30 / 100);
        Assert.InRange(chatty, total * 20 / 100, total * 30 / 100);
        Assert.InRange(normal, total * 45 / 100, total * 55 / 100);
    }

    [Fact]
    public void For_IsDeterministic() => Assert.Equal(PilotTraits.For("AAL123", 7), PilotTraits.For("AAL123", 7));

    [Fact]
    public void For_DependsOnTheSeed()
    {
        bool anyDiffers = Enumerable.Range(0, 100).Any(i => PilotTraits.For(Callsign(i), 1) != PilotTraits.For(Callsign(i), 2));

        Assert.True(anyDiffers);
    }

    [Theory]
    [InlineData(FrequencyLoadBand.Quiet, PilotTrait.Terse, FrequencyLoadBand.Normal)]
    [InlineData(FrequencyLoadBand.Normal, PilotTrait.Terse, FrequencyLoadBand.Busy)]
    [InlineData(FrequencyLoadBand.Busy, PilotTrait.Terse, FrequencyLoadBand.Busy)]
    [InlineData(FrequencyLoadBand.Quiet, PilotTrait.Normal, FrequencyLoadBand.Quiet)]
    [InlineData(FrequencyLoadBand.Normal, PilotTrait.Normal, FrequencyLoadBand.Normal)]
    [InlineData(FrequencyLoadBand.Busy, PilotTrait.Normal, FrequencyLoadBand.Busy)]
    [InlineData(FrequencyLoadBand.Quiet, PilotTrait.Chatty, FrequencyLoadBand.Quiet)]
    [InlineData(FrequencyLoadBand.Normal, PilotTrait.Chatty, FrequencyLoadBand.Quiet)]
    [InlineData(FrequencyLoadBand.Busy, PilotTrait.Chatty, FrequencyLoadBand.Normal)]
    public void EffectiveBand_ShiftsOneBandClamped(FrequencyLoadBand band, PilotTrait trait, FrequencyLoadBand expected) =>
        Assert.Equal(expected, PilotTraits.EffectiveBand(band, trait));

    [Fact]
    public void VariantPick_IndexIsInRangeAndDeterministic()
    {
        for (int i = 0; i < 100; i++)
        {
            int index = VariantPick.Index(Callsign(i), "climb and maintain five thousand", 30, 1, 3);

            Assert.InRange(index, 0, 2);
            Assert.Equal(index, VariantPick.Index(Callsign(i), "climb and maintain five thousand", 30, 1, 3));
        }
    }

    [Fact]
    public void VariantPick_VariesWithTheSecond()
    {
        int distinct = Enumerable
            .Range(0, 60)
            .Select(second => VariantPick.Index("AAL123", "climb and maintain five thousand", second, 1, 3))
            .Distinct()
            .Count();

        Assert.True(distinct >= 2, $"Expected at least two distinct indices over 60 seconds, got {distinct}.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void VariantPick_RejectsCountBelowOne(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => VariantPick.Index("AAL123", "climb and maintain five thousand", 30, 1, count));

    [Fact]
    public void For_IsPinned()
    {
        const PilotTrait T = PilotTrait.Terse;
        const PilotTrait N = PilotTrait.Normal;
        const PilotTrait C = PilotTrait.Chatty;
        PilotTrait[] expected = [T, T, T, C, N, N, T, T, C, C, C, C, N, T, T, N];

        PilotTrait[] actual = [.. Enumerable.Range(0, 16).Select(i => PilotTraits.For(Callsign(i), 1))];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void VariantPick_IsPinned()
    {
        int[] expected = [0, 3, 3, 2, 2, 0, 0, 3, 0, 1, 1, 3, 1, 3, 2, 1];

        int[] actual = [.. Enumerable.Range(0, 16).Select(s => VariantPick.Index("AAL123", Body, s, 1, 4))];

        Assert.Equal(expected, actual);
        Assert.Equal(1096404946, VariantPick.Index("AAL123", Body, 30, 1, int.MaxValue));
    }

    [Fact]
    public void EffectiveBand_RejectsUndefinedTraitAndBand()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PilotTraits.EffectiveBand(FrequencyLoadBand.Normal, (PilotTrait)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => PilotTraits.EffectiveBand((FrequencyLoadBand)7, PilotTrait.Normal));
    }

    [Fact]
    public void For_LowBitTwinsDoNotShareATrait()
    {
        string[] twins = ["AAL123", "UAL123", "AAL127", "UAL527", "EAL963"];

        bool anySeedSplitsThem = Enumerable.Range(1, 20).Any(seed => twins.Select(c => PilotTraits.For(c, seed)).Distinct().Count() > 1);

        Assert.True(anySeedSplitsThem);
    }

    [Fact]
    public void For_SeedsWithEqualLowBitsDiffer()
    {
        int differing = Enumerable.Range(0, 200).Count(i => PilotTraits.For(Callsign(i), 1) != PilotTraits.For(Callsign(i), 5));

        Assert.True(differing >= 20, $"Expected at least 20 of 200 callsigns to change trait between seeds 1 and 5, got {differing}.");
    }

    [Fact]
    public void VariantPick_SpreadsOverSeconds()
    {
        const int seconds = 400;
        int[] indices = [.. Enumerable.Range(0, seconds).Select(s => VariantPick.Index("AAL123", Body, s, 1, 4))];

        for (int index = 0; index < 4; index++)
        {
            int hits = indices.Count(i => i == index);
            Assert.InRange(hits, seconds * 15 / 100, seconds * 35 / 100);
        }

        Assert.False(Enumerable.Range(0, 6).All(s => indices[s] == indices[s + 4]), "Seconds 0..9 follow a period-4 cycle.");
    }

    [Fact]
    public void VariantPick_VariesWithCallsignBodyAndSeed()
    {
        int baseline = VariantPick.Index("AAL123", Body, 30, 1, 4);

        Assert.Contains(Enumerable.Range(0, 20), t => VariantPick.Index($"AAL{200 + t}", Body, 30, 1, 4) != baseline);
        Assert.Contains(Enumerable.Range(0, 20), t => VariantPick.Index("AAL123", $"climb and maintain {t} thousand", 30, 1, 4) != baseline);
        Assert.Contains(Enumerable.Range(0, 20), t => VariantPick.Index("AAL123", Body, 30, 2 + t, 4) != baseline);
        Assert.All(Enumerable.Range(0, 20), t => Assert.Equal(0, VariantPick.Index($"AAL{200 + t}", Body, t, t, 1)));
    }
}
