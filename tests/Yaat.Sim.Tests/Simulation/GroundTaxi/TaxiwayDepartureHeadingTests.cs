using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The spawn heading the taxi-coverage runner and the hold-short harnesses give an aircraft at a runway holding position
/// (<see cref="TaxiCoverageRunner.TaxiwayDepartureHeading"/>) points it off the runway, along its exit taxiway.
/// </summary>
public class TaxiwayDepartureHeadingTests(ITestOutputHelper output)
{
    /// <summary>How far (ft) ahead of the bar along the heading the test looks.</summary>
    private const double AheadFt = 10.0;

    /// <summary>
    /// At every runway holding position of <paramref name="airportId"/> whose runway the airport lists, a point
    /// <see cref="AheadFt"/> ahead of the bar along its departure heading is no nearer the runway's centreline than the bar.
    /// </summary>
    [Theory]
    [InlineData("OAK")]
    [InlineData("SFO")]
    [InlineData("FLL")]
    public void TaxiwayDepartureHeading_AtEveryRunwayBar_FacesAwayFromTheRunway(string airportId)
    {
        TestVnasData.EnsureInitialized();
        AirportGroundLayout layout =
            new TestAirportGroundData().GetLayout(airportId) ?? throw new InvalidOperationException($"no {airportId} layout");
        List<string> toward = [];
        foreach (GroundNode bar in layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.RunwayHoldShort) && (n.RunwayId is not null)))
        {
            if (RunwayOccupancy.AirportRunways(airportId).FirstOrDefault(r => r.Id.Overlaps(bar.RunwayId!.Value)) is not { } runway)
            {
                continue;
            }

            TrueHeading heading = TaxiCoverageRunner.TaxiwayDepartureHeading(layout, bar);
            LatLon ahead = GeoMath.ProjectPoint(bar.Position, heading, AheadFt / GeoMath.FeetPerNm);
            double barFt = RunwayHoldBars.CentrelineFt(runway, bar.Position);
            double aheadFt = RunwayHoldBars.CentrelineFt(runway, ahead);
            if (aheadFt < barFt)
            {
                toward.Add($"bar {bar.Id} ({runway.Id}): heading {heading.Degrees:F1} closes the centreline from {barFt:F1} to {aheadFt:F1} ft");
            }
        }

        output.WriteLine(string.Join(Environment.NewLine, toward));
        Assert.True(toward.Count == 0, $"{toward.Count} {airportId} bars face the runway: {string.Join("; ", toward)}");
    }
}
