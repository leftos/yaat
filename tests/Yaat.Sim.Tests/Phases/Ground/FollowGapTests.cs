using Xunit;
using Yaat.Sim.Phases.Ground;

namespace Yaat.Sim.Tests.Phases.Ground;

/// <summary>
/// The nose-to-tail gap a <c>FOLLOWG</c> follower stops at, keyed by the lead: about 100 ft behind a small or prop lead,
/// 150 ft behind a large or 757-class jet, 250 ft behind a heavy or super, plus 100 ft for a small or helicopter follower
/// behind any jet. The close-follow band, where the follower stops matching taxi speed and closes up, is 150 ft beyond it.
/// </summary>
public class FollowGapTests
{
    public FollowGapTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>A large jet follower carries no adjustment, so the gap is the lead's own.</summary>
    [Theory]
    [InlineData("C172", 100.0)]
    [InlineData("B738", 150.0)]
    [InlineData("B752", 150.0)]
    [InlineData("B744", 250.0)]
    [InlineData("A388", 250.0)]
    [InlineData("AT76", 100.0)]
    public void StopGap_ByLeadWakeClass(string leadType, double expectedFt) => Assert.Equal(expectedFt, StopGap(leadType, "B738"), 6);

    [Fact]
    public void StopGap_SmallFollowerBehindJet_Adds100Ft() => Assert.Equal(250.0, StopGap("B738", "C172"), 6);

    [Fact]
    public void StopGap_HelicopterBehindJet_Adds100Ft()
    {
        Assert.Equal(AircraftCategory.Helicopter, AircraftCategorization.Categorize("H60"));
        Assert.Equal(250.0, StopGap("B738", "H60"), 6);
        Assert.Equal(350.0, StopGap("B744", "H60"), 6);
    }

    [Fact]
    public void StopGap_SmallFollowerBehindProp_NoAdjustment()
    {
        Assert.Equal(100.0, StopGap("C172", "C172"), 6);
        Assert.Equal(100.0, StopGap("AT76", "C172"), 6);
    }

    [Theory]
    [InlineData("C172", "C172")]
    [InlineData("B738", "B738")]
    [InlineData("B738", "C172")]
    [InlineData("A388", "B738")]
    public void CloseFollowBand_IsStopGapPlus150(string leadType, string followerType)
    {
        double bandFt = FollowGap.CloseFollowBandFt(
            leadType,
            AircraftCategorization.Categorize(leadType),
            followerType,
            AircraftCategorization.Categorize(followerType)
        );
        Assert.Equal(StopGap(leadType, followerType) + 150.0, bandFt, 6);
    }

    private static double StopGap(string leadType, string followerType) =>
        FollowGap.StopGapFt(leadType, AircraftCategorization.Categorize(leadType), followerType, AircraftCategorization.Categorize(followerType));
}
