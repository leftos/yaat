using Xunit;
using Yaat.Sim.Pilot;

namespace Yaat.Sim.Tests.Pilot;

/// <summary>The frequency-load meter: the dead-air share of the trailing minute and the band it reads as.</summary>
public sealed class FrequencyActivityMeterTests
{
    [Fact]
    public void DeadAirShare_OfAnEmptyMeter_IsTheWholeWindow()
    {
        var meter = new FrequencyActivityMeter();

        Assert.Equal(1.0, meter.DeadAirShare(100));
        Assert.Equal(FrequencyLoadBand.Quiet, meter.PeekLoadBand(100));
    }

    [Fact]
    public void DeadAirShare_CountsOverlappingTransmissionsOnce()
    {
        var meter = new FrequencyActivityMeter();
        meter.Record(10, 10);
        meter.Record(15, 10);

        // [10, 20] and [15, 25] occupy [10, 25]: 15 s, not 20.
        Assert.Equal(1.0 - (15.0 / 60.0), meter.DeadAirShare(60));
    }

    [Fact]
    public void DeadAirShare_CountsATransmissionInsideAnotherOnce()
    {
        var meter = new FrequencyActivityMeter();
        meter.Record(12, 3);
        meter.Record(10, 20);

        Assert.Equal(1.0 - (20.0 / 60.0), meter.DeadAirShare(60));
    }

    [Fact]
    public void DeadAirShare_ClipsATransmissionAtTheWindowStart()
    {
        var meter = new FrequencyActivityMeter();
        meter.Record(30, 20);

        // The window ending at 100 starts at 40: only [40, 50] of [30, 50] is inside it.
        Assert.Equal(1.0 - (10.0 / 60.0), meter.DeadAirShare(100));
    }

    [Fact]
    public void DeadAirShare_ClipsATransmissionStillRunningAtNow()
    {
        var meter = new FrequencyActivityMeter();
        meter.Record(95, 10);

        // [95, 105] is on air at 100: only the 5 s already spoken count.
        Assert.Equal(1.0 - (5.0 / 60.0), meter.DeadAirShare(100));
    }

    [Fact]
    public void DeadAirShare_IgnoresATransmissionThatEndedBeforeTheWindow()
    {
        var meter = new FrequencyActivityMeter();
        meter.Record(0, 30);

        Assert.Equal(1.0, meter.DeadAirShare(100));
    }

    [Theory]
    [InlineData(18.0, FrequencyLoadBand.Quiet)] // 42 s of dead air: exactly 70%
    [InlineData(18.5, FrequencyLoadBand.Normal)]
    [InlineData(39.0, FrequencyLoadBand.Normal)] // 21 s of dead air: exactly 35%
    [InlineData(39.5, FrequencyLoadBand.Busy)]
    public void PeekLoadBand_ClassifiesByDeadAirShare(double occupiedSeconds, FrequencyLoadBand expected)
    {
        var meter = new FrequencyActivityMeter();
        meter.Record(100 - occupiedSeconds, occupiedSeconds);

        Assert.Equal(expected, meter.PeekLoadBand(100));
    }

    [Fact]
    public void Trim_DropsOnlyTransmissionsThatEndedBeforeTheWindow()
    {
        var meter = new FrequencyActivityMeter();
        meter.Record(0, 10);
        meter.Record(30, 10);
        meter.Record(50, 5);

        meter.Trim(100);

        Assert.Equal([new FrequencyTransmission(30, 10), new FrequencyTransmission(50, 5)], meter.Transmissions);
    }

    [Fact]
    public void Restore_ReplacesTheTransmissionsWithoutTrimming()
    {
        var meter = new FrequencyActivityMeter();
        meter.Record(0, 45);

        meter.Restore([new FrequencyTransmission(1, 2), new FrequencyTransmission(5, 3)]);

        Assert.Equal([new FrequencyTransmission(1, 2), new FrequencyTransmission(5, 3)], meter.Transmissions);
        Assert.Equal(FrequencyLoadBand.Quiet, meter.PeekLoadBand(1000));
    }

    [Fact]
    public void Trim_KeepsATransmissionEndingExactlyAtTheWindowStart()
    {
        var meter = new FrequencyActivityMeter();
        meter.Record(0, 40);

        // The window ending at 100 starts at 40, where [0, 40] ends: kept, though it adds no airtime.
        meter.Trim(100);

        Assert.Equal([new FrequencyTransmission(0, 40)], meter.Transmissions);
        Assert.Equal(1.0, meter.DeadAirShare(100));
    }

    [Fact]
    public void GetLoadBand_AgreesWithPeekLoadBand_WhenTrimmingDropsTransmissions()
    {
        var frequency = new FrequencyState();
        frequency.ActivityMeter.Record(0, 30);
        frequency.ActivityMeter.Record(50, 10);
        frequency.ActivityMeter.Record(70, 15);
        frequency.ActivityMeter.Record(95, 10);

        // At 100 the window is [40, 100]: [0, 30] has ended before it, and [50, 60], [70, 85] and [95, 100] occupy 30 s,
        // 50% dead air.
        FrequencyLoadBand peeked = frequency.PeekLoadBand(100);
        FrequencyLoadBand got = frequency.GetLoadBand(100);

        Assert.Equal(FrequencyLoadBand.Normal, peeked);
        Assert.Equal(peeked, got);
        Assert.Equal(
            [new FrequencyTransmission(50, 10), new FrequencyTransmission(70, 15), new FrequencyTransmission(95, 10)],
            frequency.ActivityMeter.Transmissions
        );
    }

    [Fact]
    public void Record_RefusesANegativeDuration()
    {
        var meter = new FrequencyActivityMeter();

        Assert.Throws<ArgumentOutOfRangeException>(() => meter.Record(10, -1));
    }

    [Theory]
    [InlineData("", 1.0)]
    [InlineData("roger", 1.0)]
    [InlineData("one two three four", 1.0)]
    [InlineData("one two three four five", 1.25)]
    [InlineData("  descend   and maintain five thousand  ", 1.25)]
    public void EstimateAirtimeSeconds_IsAQuarterSecondPerWordWithAOneSecondMinimum(string text, double expected) =>
        Assert.Equal(expected, FrequencyState.EstimateAirtimeSeconds(text));
}
