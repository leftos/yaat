using Xunit;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// The auto-delete modes the live session's picker and the Scenario Editor's General tab share: the order and both index
/// conversions, as the live editor's mapping always gave them.
/// </summary>
public sealed class AutoDeleteModesTests
{
    [Fact]
    public void Ordered_IsDefaultNeverOnLandingParked() => Assert.Equal([null, "Never", "OnLanding", "Parked"], AutoDeleteModes.Ordered);

    [Theory]
    [InlineData(null, 0)]
    [InlineData("Never", 1)]
    [InlineData("OnLanding", 2)]
    [InlineData("Parked", 3)]
    [InlineData("None", 0)]
    [InlineData("parked", 0)]
    [InlineData("", 0)]
    public void ToIndex_MapsEachModeAndAnUnknownOneToZero(string? mode, int index) => Assert.Equal(index, AutoDeleteModes.ToIndex(mode));

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "Never")]
    [InlineData(2, "OnLanding")]
    [InlineData(3, "Parked")]
    [InlineData(4, null)]
    [InlineData(-1, null)]
    public void FromIndex_MapsEachIndexAndAnOutOfRangeOneToTheDefault(int index, string? mode) =>
        Assert.Equal(mode, AutoDeleteModes.FromIndex(index));

    [Fact]
    public void EveryIndex_RoundTrips()
    {
        for (int i = 0; i < AutoDeleteModes.Ordered.Count; i++)
        {
            Assert.Equal(i, AutoDeleteModes.ToIndex(AutoDeleteModes.FromIndex(i)));
        }
    }
}
