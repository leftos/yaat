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
    [InlineData("Observer")]
    [InlineData("Student1")]
    [InlineData("Student2")]
    public void Classify_LowRatings_AreUngated(string? rating)
    {
        Assert.Equal(ScenarioRatingTier.Ungated, ScenarioRatingClassifier.Classify(rating));
    }

    [Theory]
    [InlineData("S3")]
    [InlineData("s3")]
    [InlineData("Student3")]
    [InlineData("student3")]
    public void Classify_Student3Range_AreS3Tier(string rating)
    {
        Assert.Equal(ScenarioRatingTier.S3, ScenarioRatingClassifier.Classify(rating));
    }

    [Theory]
    [InlineData("C1")]
    [InlineData("C3")]
    [InlineData("c1")]
    [InlineData("Controller1")]
    [InlineData("Controller3")]
    [InlineData("controller1")]
    public void Classify_Controller1Range_AreC1Tier(string rating)
    {
        Assert.Equal(ScenarioRatingTier.C1, ScenarioRatingClassifier.Classify(rating));
    }

    [Theory]
    [InlineData("I1")]
    [InlineData("I3")]
    [InlineData("SUP")]
    [InlineData("ADM")]
    [InlineData("Instructor1")]
    [InlineData("Instructor3")]
    [InlineData("Supervisor")]
    [InlineData("Administrator")]
    [InlineData("instructor1")]
    public void Classify_Instructor1Range_AreI1Tier(string rating)
    {
        Assert.Equal(ScenarioRatingTier.I1, ScenarioRatingClassifier.Classify(rating));
    }

    [Theory]
    [InlineData("junk")]
    [InlineData("MENTOR")]
    [InlineData("???")]
    [InlineData("S99")]
    public void Classify_UnknownRating_FailsClosedToI1Tier(string rating)
    {
        // Unknowns route to the most restrictive tier so an unrecognised future rating
        // value doesn't accidentally become world-readable until the table catches up.
        Assert.Equal(ScenarioRatingTier.I1, ScenarioRatingClassifier.Classify(rating));
    }

    [Fact]
    public void IsAccessible_Ungated_AlwaysTrue()
    {
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.Ungated, new HashSet<ScenarioRatingTier>()));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.Ungated, new HashSet<ScenarioRatingTier> { ScenarioRatingTier.S3 }));
    }

    [Fact]
    public void IsAccessible_S3Required_AcceptsS3OrC1OrI1()
    {
        Assert.False(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.S3, new HashSet<ScenarioRatingTier>()));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.S3, new HashSet<ScenarioRatingTier> { ScenarioRatingTier.S3 }));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.S3, new HashSet<ScenarioRatingTier> { ScenarioRatingTier.C1 }));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.S3, new HashSet<ScenarioRatingTier> { ScenarioRatingTier.I1 }));
    }

    [Fact]
    public void IsAccessible_C1Required_AcceptsC1OrI1ButNotS3()
    {
        Assert.False(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.C1, new HashSet<ScenarioRatingTier>()));
        Assert.False(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.C1, new HashSet<ScenarioRatingTier> { ScenarioRatingTier.S3 }));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.C1, new HashSet<ScenarioRatingTier> { ScenarioRatingTier.C1 }));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.C1, new HashSet<ScenarioRatingTier> { ScenarioRatingTier.I1 }));
    }

    [Fact]
    public void IsAccessible_I1Required_AcceptsOnlyI1()
    {
        Assert.False(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.I1, new HashSet<ScenarioRatingTier>()));
        Assert.False(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.I1, new HashSet<ScenarioRatingTier> { ScenarioRatingTier.S3 }));
        Assert.False(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.I1, new HashSet<ScenarioRatingTier> { ScenarioRatingTier.C1 }));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.I1, new HashSet<ScenarioRatingTier> { ScenarioRatingTier.I1 }));
    }

    [Fact]
    public void IsAccessible_AllUnlocked_AllAccessible()
    {
        var unlocked = new HashSet<ScenarioRatingTier> { ScenarioRatingTier.S3, ScenarioRatingTier.C1, ScenarioRatingTier.I1 };
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.Ungated, unlocked));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.S3, unlocked));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.C1, unlocked));
        Assert.True(ScenarioRatingClassifier.IsAccessible(ScenarioRatingTier.I1, unlocked));
    }
}
