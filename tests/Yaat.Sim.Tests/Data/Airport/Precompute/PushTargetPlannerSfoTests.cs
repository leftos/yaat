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
/// <see cref="PushTargetPlanner"/> on KSFO: the sidecar catalog passed in decides the plans, and a stand name two
/// nodes share is planned once.
/// </summary>
public class PushTargetPlannerSfoTests
{
    public PushTargetPlannerSfoTests()
    {
        TestVnasData.EnsureInitialized();
    }

    /// <summary>
    /// The plans follow the sidecar catalog passed in, not the navigation database's. KSFO's shipped sidecar lists no
    /// movement-area lane, so T5A (gate D5 hangs off it) is a ramp taxilane in the global catalog; the catalog passed makes
    /// it movement area. Every target stored for D5 is a plan made under the passed catalog, every plan under it within the
    /// length cap is stored, and the passed catalog changes at least one of D5's plans, so a planner reading the global
    /// catalog fails here.
    /// </summary>
    [Fact]
    public void Compute_PlansWithThePassedSidecars()
    {
        AirportGroundLayout sfo = Sfo();
        var passed = new AirportSidecarCatalog([new AirportSidecar("KSFO") { MovementAreaTaxiways = ["T5A"] }]);
        var passedClassification = MovementAreaClassification.Build(sfo, passed);
        var globalClassification = MovementAreaClassification.For(sfo);
        Assert.True(passedClassification.IsMovementArea("T5A"), "the passed catalog does not make T5A movement area");
        Assert.False(globalClassification.IsMovementArea("T5A"), "the global catalog already makes T5A movement area");
        var envelopes = DesignGroupEnvelopes.LoadShipped();
        GroundNode d5 = Stand(sfo, "D5");

        IReadOnlyList<PushTargetEntry> entries = PushTargetPlanner.ComputeStands(
            sfo,
            envelopes,
            passed,
            new HashSet<string>(StringComparer.Ordinal) { "D5" },
            Sequential
        );

        bool differs = false;
        foreach (PushTargetEntry entry in entries)
        {
            AircraftFootprint footprint = DesignGroupEnvelopes.FootprintOf(envelopes.Envelopes.Single(e => e.Group == entry.DesignGroup));
            double capFt = PushTargetPlanner.PathCapFt(footprint);
            List<UncappedPlan> underPassed = UncappedPlans(sfo, d5, footprint, passedClassification);
            differs |= !underPassed.SequenceEqual(UncappedPlans(sfo, d5, footprint, globalClassification));
            Assert.All(
                entry.Targets,
                t =>
                    Assert.True(
                        underPassed.Any(p => (p.Command == t.Command) && (p.PathLengthFt == t.PathLengthFt)),
                        $"group {entry.DesignGroup}: {t.Command} ({t.PathLengthFt} ft) is not the plan under the passed catalog"
                    )
            );
            Assert.All(
                underPassed.Where(p => p.PathLengthFt <= capFt),
                p => Assert.True(entry.Targets.Any(t => t.Command == p.Command), $"group {entry.DesignGroup}: {p.Command} is not stored")
            );
        }

        Assert.True(differs, "The passed catalog changes none of D5's plans, so this test cannot tell the two catalogs apart");
    }

    /// <summary>
    /// SFO's layout carries stand <c>2-2B</c> on two nodes: it gets one entry per group, planned from the lower node id,
    /// with a warning.
    /// </summary>
    [Fact]
    public void DuplicateStandName_UsesFirstNodeAndWarns()
    {
        AirportGroundLayout sfo = Sfo();
        List<GroundNode> nodes = [.. sfo.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name == "2-2B")).OrderBy(n => n.Id)];
        Assert.Equal(2, nodes.Count);
        var capture = WarningLogCapture.Install();
        var envelopes = DesignGroupEnvelopes.LoadShipped();

        IReadOnlyList<PushTargetEntry> entries = PushTargetPlanner.ComputeStands(
            sfo,
            envelopes,
            Sidecars.Value,
            new HashSet<string>(StringComparer.Ordinal) { "2-2B" },
            Sequential
        );

        Assert.Equal(envelopes.Envelopes.Select(e => e.Group), entries.Select(e => e.DesignGroup));
        Assert.Contains($"Stand name 2-2B at {sfo.AirportId} is used by 2 nodes; push targets use node {nodes[0].Id}", capture.Warnings);
    }
}
