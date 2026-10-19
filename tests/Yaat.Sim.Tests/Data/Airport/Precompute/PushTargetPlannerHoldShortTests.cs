using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PushTargetPlannerFixtures;
using static Yaat.Sim.Tests.Data.Airport.Precompute.PushTargetPlannerTests;

namespace Yaat.Sim.Tests.Data.Airport.Precompute;

/// <summary>
/// No push the planner accepts from the stands nearest a runway hold puts the aircraft past the hold line.
/// </summary>
public class PushTargetPlannerHoldShortTests
{
    public PushTargetPlannerHoldShortTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// No footprint corner of any sample of any plan the planner accepts, from the three stands nearest a runway holding
    /// position at KOAK and at KSFO, lies nearer a runway's centreline than that runway's nearest hold-short node (AIM
    /// 2-3-5.a.1: no part of the aircraft past the hold line). At least one checked corner lies within 200 ft of a hold, so
    /// the check can fail.
    /// </summary>
    [Theory]
    [InlineData("KOAK")]
    [InlineData("KSFO")]
    public void NoTargetPathPassesARunwayHoldShort(string airport)
    {
        AirportGroundLayout layout = airport == "KOAK" ? Oak() : Sfo();
        List<RunwayLine> runways = RunwayLines(layout);
        List<RunwayHold> holds = Holds(layout, runways);
        List<(RunwayLine Runway, double HoldFt)> closest = [.. holds.GroupBy(h => h.Runway).Select(g => (g.Key, g.Min(h => h.HoldFt)))];
        Assert.NotEmpty(runways);
        Assert.NotEmpty(holds);
        var envelopes = DesignGroupEnvelopes.LoadShipped();
        MovementAreaClassification classification = Classification(layout);
        List<GroundNode> stands =
        [
            .. layout
                .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null) && (n.TrueHeading is not null))
                .OrderBy(n => holds.Min(h => DistanceFt(h.Hold.Position, n.Position)))
                .ThenBy(n => n.Id)
                .Take(3),
        ];
        double nearestHoldFt = double.PositiveInfinity;

        foreach (GroundNode stand in stands)
        {
            foreach (DesignGroupEnvelope envelope in envelopes.Envelopes)
            {
                AircraftFootprint footprint = DesignGroupEnvelopes.FootprintOf(envelope);
                foreach ((string command, TugPlan plan) in AcceptedPlans(layout, stand, footprint, classification))
                {
                    foreach (LatLon corner in plan.Moves.SelectMany(m => m.Samples).SelectMany(s => Corners(s, footprint)))
                    {
                        nearestHoldFt = Math.Min(nearestHoldFt, holds.Min(h => DistanceFt(h.Hold.Position, corner)));
                        foreach ((RunwayLine runway, double holdFt) in closest)
                        {
                            Assert.True(
                                runway.DistanceFt(corner) > holdFt,
                                $"{command} from stand {stand.Name} (group {envelope.Group}) puts a corner at {corner.Lat},{corner.Lon}, "
                                    + $"{runway.DistanceFt(corner):F0} ft from {runway.Name}, within its {holdFt:F0} ft hold"
                            );
                        }
                    }
                }
            }
        }

        Assert.True(nearestHoldFt <= 200.0, $"No checked corner comes within 200 ft of a hold (nearest {nearestHoldFt:F0} ft)");
    }
}
