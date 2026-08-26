using Xunit;
using Yaat.Sim.Phases;
using Yaat.Sim.Testing;

namespace Yaat.Sim.Tests;

/// <summary>
/// <see cref="RunwayInfo.PavementLengthFt"/> is the physical runway, and it is what every caller that
/// asks "how long is this runway" means: a takeoff run, a departure flight-path projection, and
/// "crossed the runway end" (7110.65 §3-9-6, §3-10-3).
///
/// It used to be the nav data's declared <c>landing_distance_available</c>, which is a different
/// quantity: the LDA "may be less than the physical length of the runway or the length of the runway
/// remaining beyond a displaced threshold" (AIM 4-3-4.d.4), and it is declared once per runway *end*,
/// so a single stored value was also silently one end's number applied to both directions.
/// </summary>
public class RunwayLengthTests
{
    private readonly ITestOutputHelper _output;

    public RunwayLengthTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private RunwayInfo? Runway(string airport, string designator)
    {
        var rwy = TestVnasData.NavigationDb?.GetRunway(airport, designator);
        if (rwy is not null)
        {
            _output.WriteLine($"{airport} {designator}: pavement={rwy.PavementLengthFt:F0}ft");
        }

        return rwy;
    }

    /// <summary>
    /// KSJC 12R/30L is 11,001 ft of pavement. The nav data declares 8,587 ft landing 12R and 7,614 ft
    /// landing 30L — neither is the runway's length, and the old model reported 8,587 ft in both
    /// directions. KMIA 09/27 is the same shape at 12,993 ft of pavement against 11,397 / 12,755.
    /// </summary>
    [Theory]
    [InlineData("KSJC", "12L", "30R", 11000)]
    [InlineData("KSJC", "12R", "30L", 11001)]
    [InlineData("KMIA", "09", "27", 12993)]
    public void PavementLengthIsThePhysicalRunwayInBothDirections(string airport, string end1, string end2, double expectedPavementFt)
    {
        var forward = Runway(airport, end1);
        var reverse = Runway(airport, end2);
        if (forward is null || reverse is null)
        {
            return;
        }

        Assert.InRange(forward.PavementLengthFt, expectedPavementFt - 20, expectedPavementFt + 20);
        Assert.Equal(forward.PavementLengthFt, reverse.PavementLengthFt, 3);
    }

    /// <summary>
    /// A departure lining up at a displaced end has the whole pavement ahead of it — the pre-threshold
    /// pavement is available for takeoff in either direction (AIM 2-3-3.b.8.2). Landing 12R at KSJC
    /// declares 8,587 ft, so a line-up geometry working from that number would think 2,414 ft of the
    /// runway it is standing on does not exist.
    /// </summary>
    [Fact]
    public void PavementExceedsWhatEitherEndDeclaresForLanding()
    {
        var rwy = Runway("KSJC", "12R");
        if (rwy is null)
        {
            return;
        }

        const double LongestDeclaredLandingDistanceFt = 8587;
        Assert.True(
            rwy.PavementLengthFt > LongestDeclaredLandingDistanceFt + 2000,
            $"KSJC 12R/30L pavement should be ~11,001 ft, not a landing distance; got {rwy.PavementLengthFt:F0}ft"
        );
    }
}
