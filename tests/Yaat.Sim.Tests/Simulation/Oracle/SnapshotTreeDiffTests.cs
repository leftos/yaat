using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Simulation.Oracle;

namespace Yaat.Sim.Tests.Simulation.Oracle;

/// <summary>
/// The oracle's measuring instrument. These drive <see cref="SnapshotTreeDiff.CompareNodes"/> over hand-built trees
/// rather than real captures: a differ that silently under-reports would produce an empty accepted-divergence
/// baseline that looks like success, so its own behaviour has to be pinned before anything is compared with it.
/// </summary>
public class SnapshotTreeDiffTests
{
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    private static IReadOnlyList<SnapshotDivergence> Diff(string left, string right) => SnapshotTreeDiff.CompareNodes(Parse(left), Parse(right));

    [Fact]
    public void IdenticalTrees_ReportNothing()
    {
        var divergences = Diff(
            """{"Scenario":{"ElapsedSeconds":12,"Nested":{"A":1,"B":[1,2,3]}}}""",
            """{"Scenario":{"ElapsedSeconds":12,"Nested":{"A":1,"B":[1,2,3]}}}"""
        );

        Assert.Empty(divergences);
    }

    [Fact]
    public void ChangedLeaf_ReportsItsPathAndBothValues()
    {
        var divergence = Assert.Single(Diff("""{"Scenario":{"Nested":{"A":1}}}""", """{"Scenario":{"Nested":{"A":2}}}"""));

        Assert.Equal("Scenario.Nested.A", divergence.Path);
        Assert.Equal("1", divergence.Left);
        Assert.Equal("2", divergence.Right);
    }

    [Fact]
    public void PropertyMissingOnOneSide_ReportsAbsent()
    {
        var divergence = Assert.Single(Diff("""{"A":1}""", """{"A":1,"B":"x"}"""));

        Assert.Equal("B", divergence.Path);
        Assert.Equal(SnapshotTreeDiff.Absent, divergence.Left);
        Assert.Equal("\"x\"", divergence.Right);
    }

    [Fact]
    public void PropertyWrittenAsNull_VersusAbsentEntirely_Reports()
    {
        // JsonNode has no node type for JSON null, so both sides look like a C# null here; only the lookup's own
        // result tells them apart. Comparing them equal would be a silent miss.
        var divergence = Assert.Single(Diff("""{"A":1,"B":null}""", """{"A":1}"""));

        Assert.Equal("B", divergence.Path);
        Assert.Equal("null", divergence.Left);
        Assert.Equal(SnapshotTreeDiff.Absent, divergence.Right);
    }

    [Fact]
    public void PropertyNullOnBothSides_ReportsNothing()
    {
        Assert.Empty(Diff("""{"A":null}""", """{"A":null}"""));
    }

    [Fact]
    public void PropertyNullOnOneSideAndValuedOnTheOther_RendersNullNotAbsent()
    {
        var divergence = Assert.Single(Diff("""{"A":null}""", """{"A":5}"""));

        Assert.Equal("null", divergence.Left);
        Assert.Equal("5", divergence.Right);
    }

    [Fact]
    public void DuplicateCallsignsInOneSnapshot_BothSurface_RatherThanTheLaterReplacingTheEarlier()
    {
        // A duplicate callsign is itself a state bug the oracle should expose, not quietly collapse.
        var divergences = Diff(
            """{"Aircraft":[{"Callsign":"AAL1","Altitude":3000},{"Callsign":"AAL1","Altitude":9000}]}""",
            """{"Aircraft":[{"Callsign":"AAL1","Altitude":3000}]}"""
        );

        var divergence = Assert.Single(divergences);
        Assert.Equal("Aircraft[AAL1#1]", divergence.Path);
        Assert.Equal(SnapshotTreeDiff.Absent, divergence.Right);
    }

    [Fact]
    public void ObjectVersusScalarAtTheSamePath_Reports()
    {
        var divergence = Assert.Single(Diff("""{"A":{"X":1}}""", """{"A":5}"""));

        Assert.Equal("A", divergence.Path);
        Assert.Equal("{object, 1 properties}", divergence.Left);
    }

    [Fact]
    public void AircraftList_IsKeyedByCallsign_SoReorderingIsNotADivergence()
    {
        var divergences = Diff(
            """{"Aircraft":[{"Callsign":"AAL1","Altitude":3000},{"Callsign":"SWA2","Altitude":5000}]}""",
            """{"Aircraft":[{"Callsign":"SWA2","Altitude":5000},{"Callsign":"AAL1","Altitude":3000}]}"""
        );

        Assert.Empty(divergences);
    }

    [Fact]
    public void AircraftOnOneSideOnly_ReportsAtItsCallsignPath()
    {
        var divergence = Assert.Single(Diff("""{"Aircraft":[{"Callsign":"AAL1"},{"Callsign":"SWA2"}]}""", """{"Aircraft":[{"Callsign":"AAL1"}]}"""));

        Assert.Equal("Aircraft[SWA2]", divergence.Path);
        Assert.Equal(SnapshotTreeDiff.Absent, divergence.Right);
    }

    [Fact]
    public void AircraftFieldChange_ReportsUnderTheCallsign()
    {
        var divergence = Assert.Single(
            Diff("""{"Aircraft":[{"Callsign":"AAL1","Track":{"Owner":"3T"}}]}""", """{"Aircraft":[{"Callsign":"AAL1","Track":{"Owner":null}}]}""")
        );

        Assert.Equal("Aircraft[AAL1].Track.Owner", divergence.Path);
    }

    [Fact]
    public void NonAircraftList_StaysIndexKeyed_BecauseItsIndexIsSemantic()
    {
        // Phases.Phases is addressed by CurrentIndex, so reordering it is a real difference, not a re-key.
        var divergences = Diff("""{"Phases":[{"Name":"Taxiing"},{"Name":"Landing"}]}""", """{"Phases":[{"Name":"Landing"},{"Name":"Taxiing"}]}""");

        Assert.Equal(2, divergences.Count);
        Assert.Equal("Phases[0].Name", divergences[0].Path);
        Assert.Equal("Phases[1].Name", divergences[1].Path);
    }

    [Fact]
    public void ShorterList_ReportsTheMissingTailAsAbsent()
    {
        var divergence = Assert.Single(Diff("""{"Blocks":[1,2]}""", """{"Blocks":[1]}"""));

        Assert.Equal("Blocks[1]", divergence.Path);
        Assert.Equal(SnapshotTreeDiff.Absent, divergence.Right);
    }

    [Fact]
    public void PolymorphicDiscriminatorChange_ReportsAsDollarType()
    {
        var divergence = Assert.Single(Diff("""{"Phases":[{"$type":"Taxiing"}]}""", """{"Phases":[{"$type":"HoldingShort"}]}"""));

        Assert.Equal("Phases[0].$type", divergence.Path);
    }

    [Fact]
    public void EmbeddedJsonString_IsReparsedSoThePathReachesTheInnerField()
    {
        var divergence = Assert.Single(
            Diff(
                """{"WeatherJson":"{\"Stations\":{\"KOAK\":{\"WindDirection\":120}}}"}""",
                """{"WeatherJson":"{\"Stations\":{\"KOAK\":{\"WindDirection\":300}}}"}"""
            )
        );

        Assert.Equal("WeatherJson.Stations.KOAK.WindDirection", divergence.Path);
        Assert.Equal("120", divergence.Left);
        Assert.Equal("300", divergence.Right);
    }

    [Fact]
    public void EmbeddedJsonString_ThatIsIdentical_ReportsNothing()
    {
        Assert.Empty(Diff("""{"ConfigJson":"{\"Rate\":5}"}""", """{"ConfigJson":"{\"Rate\":5}"}"""));
    }

    [Fact]
    public void EmbeddedJsonProperty_HoldingNonJson_FallsBackToComparingTheStrings()
    {
        var divergence = Assert.Single(Diff("""{"AircraftJson":"not json"}""", """{"AircraftJson":"also not json"}"""));

        Assert.Equal("AircraftJson", divergence.Path);
        Assert.Equal("\"not json\"", divergence.Left);
        Assert.Equal("\"also not json\"", divergence.Right);
    }

    [Fact]
    public void NegativeVirtualNodeIds_AreNormalized_SoDifferentLabelsForTheSameGeometryAgree()
    {
        Assert.Empty(
            Diff("""{"HoldShortNodeId":-101,"ExitWaypointNodeIds":[-102,-103]}""", """{"HoldShortNodeId":-207,"ExitWaypointNodeIds":[-208,-209]}""")
        );
    }

    [Fact]
    public void VirtualNodeIdVersusLayoutNodeId_StillReports()
    {
        var divergence = Assert.Single(Diff("""{"TargetNodeId":-101}""", """{"TargetNodeId":42}"""));

        Assert.Equal("TargetNodeId", divergence.Path);
        Assert.Equal("-V", divergence.Left);
        Assert.Equal("42", divergence.Right);
    }

    [Fact]
    public void SmallNegativeSentinelOnANodeIdProperty_IsNotNormalized()
    {
        // VirtualNode ids are all <= -101. A -1 "unassigned" sentinel must report rather than being folded together
        // with a real virtual-node id, which would hide unset-versus-resolved.
        var divergence = Assert.Single(Diff("""{"TargetNodeId":-1}""", """{"TargetNodeId":-105}"""));

        Assert.Equal("-1", divergence.Left);
        Assert.Equal("-V", divergence.Right);
    }

    [Fact]
    public void NegativeValueOnAPropertyThatIsNotANodeId_IsNotNormalized()
    {
        var divergence = Assert.Single(Diff("""{"VerticalSpeed":-101}""", """{"VerticalSpeed":-207}"""));

        Assert.Equal("-101", divergence.Left);
        Assert.Equal("-207", divergence.Right);
    }

    [Theory]
    [InlineData("Aircraft[SWA123].Track.Owner", "Aircraft[*].Track.Owner")]
    [InlineData("Scenario.DelayedQueue[3].SpawnAtSeconds", "Scenario.DelayedQueue[*].SpawnAtSeconds")]
    [InlineData("Aircraft[AAL1].Phases[2].Requirements[0].Type", "Aircraft[*].Phases[*].Requirements[*].Type")]
    [InlineData("Scenario.ElapsedSeconds", "Scenario.ElapsedSeconds")]
    [InlineData("Broken[unterminated", "Broken[unterminated")]
    public void Normalize_CollapsesEveryCollectionKey(string concrete, string expected)
    {
        Assert.Equal(expected, DivergencePath.Normalize(concrete));
    }
}
