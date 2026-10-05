using Xunit;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// The parking call-up pace is stored as a rate percent (100% = one call-up per 20 s) but shown as an interval:
/// paused (0) or once per 10-120 s in steps of 10. The two conversions agree on every interval the slider offers.
/// </summary>
public class SoloPacingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(70)]
    [InlineData(80)]
    [InlineData(90)]
    [InlineData(100)]
    [InlineData(110)]
    [InlineData(120)]
    public void EverySliderInterval_RoundTripsThroughTheRate(int seconds)
    {
        int rate = SoloPacing.ParkingInitialCallupIntervalSecondsToRate(seconds);

        Assert.Equal(seconds, SoloPacing.ParkingInitialCallupRateToIntervalSeconds(rate));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 200)]
    [InlineData(20, 100)]
    [InlineData(30, 67)]
    [InlineData(40, 50)]
    [InlineData(120, 17)]
    public void IntervalToRate_IsTwoThousandOverTheInterval(int seconds, int expectedRate) =>
        Assert.Equal(expectedRate, SoloPacing.ParkingInitialCallupIntervalSecondsToRate(seconds));

    [Theory]
    [InlineData(200, 10)]
    [InlineData(100, 20)]
    [InlineData(67, 30)]
    [InlineData(50, 40)]
    [InlineData(17, 120)]
    public void EveryStoredRate_RoundTripsThroughTheInterval(int rate, int expectedSeconds)
    {
        int seconds = SoloPacing.ParkingInitialCallupRateToIntervalSeconds(rate);

        Assert.Equal(expectedSeconds, seconds);
        Assert.Equal(rate, SoloPacing.ParkingInitialCallupIntervalSecondsToRate(seconds));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 120)]
    [InlineData(250, 10)]
    public void RateToInterval_ClampsTheRateAndTheInterval(int rate, int expectedSeconds) =>
        Assert.Equal(expectedSeconds, SoloPacing.ParkingInitialCallupRateToIntervalSeconds(rate));

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 10)]
    [InlineData(10, 10)]
    [InlineData(120, 120)]
    [InlineData(500, 120)]
    public void NormalizeInterval_IsPausedOrTenToOneTwenty(int seconds, int expected) =>
        Assert.Equal(expected, SoloPacing.NormalizeParkingInitialCallupIntervalSeconds(seconds));

    [Theory]
    [InlineData(5, 200)]
    [InlineData(500, 17)]
    public void IntervalToRate_ClampsTheIntervalFirst(int seconds, int expectedRate) =>
        Assert.Equal(expectedRate, SoloPacing.ParkingInitialCallupIntervalSecondsToRate(seconds));

    [Fact]
    public void SelectLoadRates_TheDialogShowedBoth_SendsBothChoices() =>
        Assert.Equal((150, 80), SoloPacing.SelectLoadRates(showParking: true, showArrival: true, chosen: (150, 80), defaults: (50, 35)));

    [Fact]
    public void SelectLoadRates_TheDialogShowedOnlyParking_SendsTheArrivalDefault() =>
        Assert.Equal((150, 35), SoloPacing.SelectLoadRates(showParking: true, showArrival: false, chosen: (150, 80), defaults: (50, 35)));

    [Fact]
    public void SelectLoadRates_TheDialogShowedOnlyArrival_SendsTheParkingDefault() =>
        Assert.Equal((50, 80), SoloPacing.SelectLoadRates(showParking: false, showArrival: true, chosen: (150, 80), defaults: (50, 35)));

    [Fact]
    public void SelectLoadRates_TheDialogShowedNoPacingControl_SendsTheStoredDefaults() =>
        Assert.Equal((50, 35), SoloPacing.SelectLoadRates(showParking: false, showArrival: false, chosen: (150, 80), defaults: (50, 35)));

    [Fact]
    public void LoadDefaults_ReadsTheStoredPacingPair()
    {
        var preferences = UserPreferences.CreateDefaults();

        Assert.Equal(
            (preferences.SoloParkingInitialCallupRatePercent, preferences.SoloArrivalGeneratorRatePercent),
            SoloPacing.LoadDefaults(preferences)
        );
    }

    [Theory]
    [InlineData(0, "Paused")]
    [InlineData(-10, "Paused")]
    [InlineData(20, "Once per 20 sec")]
    [InlineData(5, "Once per 10 sec")]
    [InlineData(500, "Once per 120 sec")]
    public void FormatInterval_SaysPausedOrOncePerNSec(int seconds, string expected) =>
        Assert.Equal(expected, SoloPacing.FormatParkingInitialCallupInterval(seconds));
}
