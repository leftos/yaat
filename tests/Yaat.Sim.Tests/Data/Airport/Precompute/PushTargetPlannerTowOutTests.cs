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
/// The targets <see cref="PushTargetPlanner"/> leaves out: the ones the tug planner refuses and the tow-outs past the
/// length cap.
/// </summary>
public class PushTargetPlannerTowOutTests
{
    public PushTargetPlannerTowOutTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void RefusedTargetsAreOmitted()
    {
        AirportGroundLayout layout = Oak();
        GroundNode gate = Stand(layout, "26");
        AircraftFootprint footprint = DesignGroupEnvelopes.LoadShipped().FootprintOf(AirplaneDesignGroup.IV);
        List<UncappedPlan> plans = UncappedPlans(layout, gate, footprint, Classification(layout));
        List<UncappedPlan> taxiways = [.. plans.Where(p => p.Kind != PushTargetKind.Spot)];

        Assert.Contains(taxiways, p => p.PathLengthFt is null);
        Assert.Contains(taxiways, p => p.PathLengthFt is not null);

        List<string> offered = [.. Entry("26", "IV").Targets.Where(t => t.Kind != PushTargetKind.Spot).Select(t => t.Name)];
        Assert.All(taxiways.Where(p => p.PathLengthFt is null), p => Assert.DoesNotContain(p.Name, offered));
        Assert.All(offered, name => Assert.Contains(taxiways, p => (p.Name == name) && (p.PathLengthFt is not null)));
        double capFt = PushTargetPlanner.PathCapFt(footprint);
        Assert.All(
            taxiways.Where(p => (p.PathLengthFt is not null) && !offered.Contains(p.Name)),
            p => Assert.True(p.PathLengthFt > capFt, $"PUSH {p.Name} was accepted at {p.PathLengthFt} ft, under the cap, yet not offered")
        );
    }

    /// <summary>
    /// A target the planner accepts but whose path is over the cap (<see cref="PushTargetPlanner.PathCapFt"/>) is a tow-out
    /// and is not stored, while the stand's shorter targets are.
    /// </summary>
    [Fact]
    public void LongTowOuts_AreDropped()
    {
        AirportGroundLayout layout = Oak();
        var envelopes = DesignGroupEnvelopes.LoadShipped();
        bool found = false;

        foreach (string standName in SampleStands.Value.Order(StringComparer.Ordinal))
        {
            GroundNode stand = Stand(layout, standName);
            foreach (DesignGroupEnvelope envelope in envelopes.Envelopes)
            {
                AircraftFootprint footprint = DesignGroupEnvelopes.FootprintOf(envelope);
                double capFt = PushTargetPlanner.PathCapFt(footprint);
                List<UncappedPlan> accepted =
                [
                    .. UncappedPlans(layout, stand, footprint, Classification(layout)).Where(p => p.PathLengthFt is not null),
                ];
                if (!accepted.Any(p => p.PathLengthFt > capFt) || !accepted.Any(p => p.PathLengthFt <= capFt))
                {
                    continue;
                }

                found = true;
                List<string> stored = [.. Entry(standName, envelope.Group).Targets.Select(t => t.Command)];
                Assert.Equal(
                    accepted.Where(p => p.PathLengthFt <= capFt).Select(p => p.Command).Order(StringComparer.Ordinal),
                    stored.Order(StringComparer.Ordinal)
                );
            }
        }

        Assert.True(found, "No sample stand and group has both a target over the length cap and one under it");
    }
}
