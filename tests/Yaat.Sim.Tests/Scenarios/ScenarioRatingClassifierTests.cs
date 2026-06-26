using Xunit;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests.Scenarios;

public class ScenarioRatingClassifierTests
{
    [Theory]
    [InlineData("OBS", 0)]
    [InlineData("S1", 1)]
    [InlineData("S2", 2)]
    [InlineData("S3", 3)]
    [InlineData("C1", 4)]
    [InlineData("C3", 5)]
    [InlineData("I1", 6)]
    [InlineData("I2", 7)]
    [InlineData("I3", 8)]
    [InlineData("SUP", 9)]
    [InlineData("ADM", 10)]
    [InlineData("Observer", 0)]
    [InlineData("Student3", 3)]
    [InlineData("Controller1", 4)]
    [InlineData("Instructor1", 6)]
    [InlineData("s3", 3)]
    [InlineData("controller1", 4)]
    public void OrdinalOf_KnownRatings_ResolveBothForms(string rating, int expected)
    {
        Assert.Equal(expected, ScenarioRatingClassifier.OrdinalOf(rating));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("junk")]
    [InlineData("S99")]
    [InlineData("MENTOR")]
    public void OrdinalOf_BlankOrUnknown_IsNull(string? rating)
    {
        Assert.Null(ScenarioRatingClassifier.OrdinalOf(rating));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsRatingSufficient_BlankRequirement_IsUngated(string? requirement)
    {
        // A scenario with no minimumRating is open to anyone who can connect.
        Assert.True(ScenarioRatingClassifier.IsRatingSufficient("OBS", requirement));
        Assert.True(ScenarioRatingClassifier.IsRatingSufficient("S3", requirement));
    }

    [Theory]
    [InlineData("S3", "S3")]
    [InlineData("C1", "S3")]
    [InlineData("I1", "Student3")]
    [InlineData("Controller1", "C1")]
    [InlineData("ADM", "I1")]
    public void IsRatingSufficient_AtOrAboveRequirement_IsAllowed(string rating, string requirement)
    {
        Assert.True(ScenarioRatingClassifier.IsRatingSufficient(rating, requirement));
    }

    [Theory]
    [InlineData("S2", "S3")]
    [InlineData("S3", "C1")]
    [InlineData("C1", "I1")]
    [InlineData("OBS", "S2")]
    public void IsRatingSufficient_BelowRequirement_IsDenied(string rating, string requirement)
    {
        Assert.False(ScenarioRatingClassifier.IsRatingSufficient(rating, requirement));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("junk")]
    public void IsRatingSufficient_UnknownCaller_FailsClosed(string? rating)
    {
        // An unknown/blank caller rating can never satisfy a real requirement.
        Assert.False(ScenarioRatingClassifier.IsRatingSufficient(rating, "S3"));
    }

    [Theory]
    [InlineData("junk")]
    [InlineData("S99")]
    public void IsRatingSufficient_UnrecognisedRequirement_FailsClosed(string requirement)
    {
        // A requirement we don't map yet must block, not leak, access.
        Assert.False(ScenarioRatingClassifier.IsRatingSufficient("ADM", requirement));
    }

    [Theory]
    [InlineData("I1")]
    [InlineData("I2")]
    [InlineData("I3")]
    [InlineData("Instructor1")]
    [InlineData("SUP")]
    [InlineData("ADM")]
    public void IsInstructorOrAbove_InstructorRatings_AreTrue(string rating)
    {
        Assert.True(ScenarioRatingClassifier.IsInstructorOrAbove(rating));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OBS")]
    [InlineData("S3")]
    [InlineData("C1")]
    [InlineData("C3")]
    [InlineData("junk")]
    public void IsInstructorOrAbove_NonInstructorRatings_AreFalse(string? rating)
    {
        Assert.False(ScenarioRatingClassifier.IsInstructorOrAbove(rating));
    }
}
