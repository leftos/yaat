using Xunit;
using Yaat.Sim.Training;

namespace Yaat.Sim.Tests.Training;

/// <summary>
/// The 7110.65 same-runway separation landmark distances. §3-9-6.a is not symmetric in the two
/// categories: the succeeding aircraft's class drives the 4,500 ft case (a.3 "when either the
/// succeeding or both are Category II"), and a.2 puts a Category I behind a Category II at
/// a.1’s 3,000 ft — only a Category III on either side raises it to 6,000 ft (a.4).
/// </summary>
public class SameRunwaySeparationTests
{
    [Theory]
    [InlineData(SrsCategory.I, SrsCategory.I, 3000.0)]
    [InlineData(SrsCategory.II, SrsCategory.I, 3000.0)]
    [InlineData(SrsCategory.I, SrsCategory.II, 4500.0)]
    [InlineData(SrsCategory.II, SrsCategory.II, 4500.0)]
    [InlineData(SrsCategory.III, SrsCategory.I, 6000.0)]
    [InlineData(SrsCategory.I, SrsCategory.III, 6000.0)]
    public void RequiredDepartureBehindDeparture_FollowsParagraph396a(SrsCategory preceding, SrsCategory succeeding, double expectedFt)
    {
        Assert.Equal(expectedFt, SameRunwaySeparation.RequiredDepartureBehindDepartureFt(preceding, succeeding));
    }
}
