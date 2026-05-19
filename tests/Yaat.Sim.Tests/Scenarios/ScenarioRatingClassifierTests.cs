using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

public class ScenarioRatingClassifierTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OBS")]
    [InlineData("S1")]
    [InlineData("S2")]
    public void Classify_LowRatings_AreUngated(string? rating)
    {
        Assert.Equal(ScenarioRatingTier.Ungated, ScenarioRatingClassifier.Classify(rating));
    }

    [Theory]
    [InlineData("S3")]
    [InlineData("C1")]
    [InlineData("C3")]
    [InlineData("s3")]
    [InlineData("c1")]
    public void Classify_S3RangeRatings_AreS3Tier(string rating)
    {
        Assert.Equal(ScenarioRatingTier.S3, ScenarioRatingClassifier.Classify(rating));
    }

    [Theory]
    [InlineData("I1")]
    [InlineData("I3")]
    [InlineData("SUP")]
    [InlineData("ADM")]
    [InlineData("i1")]
    [InlineData("adm")]
    public void Classify_I1RangeRatings_AreI1Tier(string rating)
    {
        Assert.Equal(ScenarioRatingTier.I1, ScenarioRatingClassifier.Classify(rating));
    }

    [Theory]
    [InlineData("junk")]
    [InlineData("MENTOR")]
    [InlineData("???")]
    [InlineData("S99")]
    public void Classify_UnknownRating_FailsClosedToS3Tier(string rating)
    {
        Assert.Equal(ScenarioRatingTier.S3, ScenarioRatingClassifier.Classify(rating));
    }

    [Fact]
    public void IsAccessible_Ungated_AlwaysTrue()
    {
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.Ungated, new HashSet<ScenarioRatingTier>()));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.Ungated, new HashSet<ScenarioRatingTier> { ScenarioRatingTier.S3 }));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.Ungated, new HashSet<ScenarioRatingTier> { ScenarioRatingTier.I1 }));
    }

    [Fact]
    public void IsAccessible_S3Required_NoKey_False()
    {
        Assert.False(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.S3, new HashSet<ScenarioRatingTier>()));
    }

    [Fact]
    public void IsAccessible_S3Required_S3Unlocked_True()
    {
        var unlocked = new HashSet<ScenarioRatingTier> { ScenarioRatingTier.S3 };
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.S3, unlocked));
    }

    [Fact]
    public void IsAccessible_S3Required_I1Unlocked_True_Hierarchical()
    {
        var unlocked = new HashSet<ScenarioRatingTier> { ScenarioRatingTier.I1 };
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.S3, unlocked));
    }

    [Fact]
    public void IsAccessible_I1Required_NoKey_False()
    {
        Assert.False(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.I1, new HashSet<ScenarioRatingTier>()));
    }

    [Fact]
    public void IsAccessible_I1Required_S3Unlocked_False()
    {
        var unlocked = new HashSet<ScenarioRatingTier> { ScenarioRatingTier.S3 };
        Assert.False(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.I1, unlocked));
    }

    [Fact]
    public void IsAccessible_I1Required_I1Unlocked_True()
    {
        var unlocked = new HashSet<ScenarioRatingTier> { ScenarioRatingTier.I1 };
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.I1, unlocked));
    }

    [Fact]
    public void IsAccessible_AllUnlocked_AllAccessible()
    {
        var unlocked = new HashSet<ScenarioRatingTier> { ScenarioRatingTier.S3, ScenarioRatingTier.I1 };
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.Ungated, unlocked));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.S3, unlocked));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.I1, unlocked));
    }
}
