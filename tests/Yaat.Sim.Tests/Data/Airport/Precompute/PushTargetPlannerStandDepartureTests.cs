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
/// How a stand's departure (push-back, taxi-out or either, from its geometry or a sidecar override) decides the
/// targets <see cref="PushTargetPlanner"/> stores for it.
/// </summary>
public class PushTargetPlannerStandDepartureTests
{
    public PushTargetPlannerStandDepartureTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// KOAK's sidecar names no movement-area or non-movement lanes, so both verdicts are the derived ones. TE is the terminal
    /// taxilane behind gate 26 (non-movement); F is the movement-area taxiway beside stand OLD1, and the shortest push from
    /// OLD1 for group IV (354 ft), so it survives the length cap. Both were looked up once with the classifier. OLD1 is
    /// already push-back by its geometry and the catalog passed here has no area rule, so its push-back override changes
    /// nothing today; it stays as a guard that keeps the test on its target kinds whatever OLD1's geometry or the shipped
    /// rules say.
    /// </summary>
    [Fact]
    public void Kind_FollowsMovementArea()
    {
        var old1PushBack = new AirportSidecarCatalog([
            new AirportSidecar("KOAK")
            {
                StandDepartureOverrides = new Dictionary<string, StandDeparture>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OLD1"] = StandDeparture.PushBack,
                },
            },
        ]);
        IReadOnlyList<PushTargetEntry> old1 = PushTargetPlanner.ComputeStands(
            Oak(),
            DesignGroupEnvelopes.LoadShipped(),
            old1PushBack,
            new HashSet<string>(StringComparer.Ordinal) { "OLD1" },
            Sequential
        );

        Assert.Equal(PushTargetKind.Taxilane, Assert.Single(Entry("26", "IV").Targets, t => t.Name == "TE").Kind);
        PushTargetEntry old1Iv = Assert.Single(old1, e => e.DesignGroup == "IV");
        Assert.Equal(PushTargetKind.Taxiway, Assert.Single(old1Iv.Targets, t => t.Name == "F").Kind);
    }

    /// <summary>
    /// GA20's aircraft park facing the taxiway they leave by: the layout stores it as a taxi-out stand, and it gets an empty
    /// target list for every group (it once kept <c>PUSH L</c>, a 1,965 ft tow).
    /// </summary>
    [Fact]
    public void Ga20_IsTaxiOutStand_HasNoTargets()
    {
        AirportGroundLayout layout = Oak();
        GroundNode ga20 = layout.Nodes[Ga20NodeId];
        Assert.Equal((GroundNodeType.Parking, "GA20"), (ga20.Type, ga20.Name));

        IReadOnlyList<PushTargetEntry> entries = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            Sidecars.Value,
            new HashSet<string>(StringComparer.Ordinal) { "GA20" },
            Sequential
        );

        Assert.Equal(StandDeparture.TaxiOut, ga20.StandDeparture);
        Assert.Equal(StandDeparture.TaxiOut, StandDepartures.StandDepartureOf(layout, ga20, Sidecars.Value));
        Assert.Equal(DesignGroupEnvelopes.LoadShipped().Envelopes.Select(e => e.Group), entries.Select(e => e.DesignGroup));
        Assert.All(entries, e => Assert.Empty(e.Targets));
    }

    /// <summary>
    /// MTN1 is an either stand with the shipped sidecars: its targets are planned exactly as they are when it is named a
    /// push-back stand, and at least one group has some. The two match only while KOAK's shipped sidecar names no
    /// movement-area or non-movement lanes; once it names one, the push-back comparison catalog must carry it too.
    /// </summary>
    [Fact]
    public void Planner_EitherStand_PlansPushTargets()
    {
        AirportGroundLayout layout = Oak();
        var mtn1 = new HashSet<string>(StringComparer.Ordinal) { "MTN1" };
        var pushBack = new AirportSidecarCatalog([
            new AirportSidecar("KOAK")
            {
                StandDepartureOverrides = new Dictionary<string, StandDeparture>(StringComparer.OrdinalIgnoreCase)
                {
                    ["MTN1"] = StandDeparture.PushBack,
                },
            },
        ]);

        IReadOnlyList<PushTargetEntry> either = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            Sidecars.Value,
            mtn1,
            Sequential
        );
        IReadOnlyList<PushTargetEntry> pushed = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            pushBack,
            mtn1,
            Sequential
        );

        Assert.Equal(StandDeparture.Either, StandDepartures.StandDepartureOf(layout, Stand(layout, "MTN1"), Sidecars.Value));
        Assert.Contains(either, e => e.Targets.Count > 0);
        Assert.Equal(Json(pushed), Json(either));
    }

    /// <summary>
    /// OLD1 reads push-back by its geometry, but the shipped sidecar's North Field area rule makes it a taxi-out stand, so
    /// it gets an empty target list for every group.
    /// </summary>
    [Fact]
    public void Planner_AreaTaxiOutStand_HasEmptyTargets()
    {
        AirportGroundLayout layout = Oak();
        GroundNode old1 = Stand(layout, "OLD1");

        IReadOnlyList<PushTargetEntry> entries = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            Sidecars.Value,
            new HashSet<string>(StringComparer.Ordinal) { "OLD1" },
            Sequential
        );

        Assert.Equal(StandDeparture.PushBack, old1.StandDeparture);
        Assert.Equal(StandDeparture.TaxiOut, StandDepartures.StandDepartureOf(layout, old1, Sidecars.Value));
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Empty(e.Targets));
    }

    /// <summary>Gate 26 is an airline gate, parked nose-in and pushed back.</summary>
    [Fact]
    public void Gate26_IsPushBackStand()
    {
        AirportGroundLayout layout = Oak();
        GroundNode gate = Stand(layout, "26");

        Assert.Equal(StandDeparture.PushBack, gate.StandDeparture);
        Assert.Equal(StandDeparture.PushBack, StandDepartures.StandDepartureOf(layout, gate, Sidecars.Value));
    }

    /// <summary>
    /// A sidecar override wins over the geometry both ways: gate 26 made taxi-out loses its targets, GA20 made push-back
    /// reads as push-back.
    /// </summary>
    [Fact]
    public void StandDepartureOverride_FlipsAStandBothWays()
    {
        AirportGroundLayout layout = Oak();
        var flipped = new AirportSidecarCatalog([
            new AirportSidecar("KOAK")
            {
                StandDepartureOverrides = new Dictionary<string, StandDeparture>(StringComparer.OrdinalIgnoreCase)
                {
                    ["26"] = StandDeparture.TaxiOut,
                    ["ga20"] = StandDeparture.PushBack,
                },
            },
        ]);

        Assert.Equal(StandDeparture.TaxiOut, StandDepartures.StandDepartureOf(layout, Stand(layout, "26"), flipped));
        Assert.Equal(StandDeparture.PushBack, StandDepartures.StandDepartureOf(layout, layout.Nodes[Ga20NodeId], flipped));
        IReadOnlyList<PushTargetEntry> gate26 = PushTargetPlanner.ComputeStands(
            layout,
            DesignGroupEnvelopes.LoadShipped(),
            flipped,
            Gate26,
            Sequential
        );
        Assert.NotEmpty(gate26);
        Assert.All(gate26, e => Assert.Empty(e.Targets));
        Assert.NotEmpty(Entry("26", "IV").Targets);
    }
}
