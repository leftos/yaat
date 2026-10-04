using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Oracle;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Oracle;

/// <summary>
/// The oracle's per-second gate. An equal hash skips the tree diff for that second, so the one property that matters is
/// that equal state hashes equal and any difference the diff would see hashes differently.
/// </summary>
public class SnapshotStateHashTests
{
    private static ulong Hash(string json) => SnapshotStateHash.Compute(JsonNode.Parse(json));

    [Fact]
    public void EqualNodes_HashEqual()
    {
        const string Json = """{"Scenario":{"ElapsedSeconds":12,"Nested":{"A":1.25,"B":[1,2,3],"C":"x","D":true,"E":null}}}""";

        Assert.Equal(Hash(Json), Hash(Json));
    }

    [Fact]
    public void PropertyInsertionOrder_DoesNotChangeTheHash() =>
        Assert.Equal(Hash("""{"A":1,"B":{"X":"x","Y":[1,{"P":1,"Q":2}]}}"""), Hash("""{"B":{"Y":[1,{"Q":2,"P":1}],"X":"x"},"A":1}"""));

    [Theory]
    [InlineData("""{"A":1}""", """{"A":2}""")]
    [InlineData("""{"A":1.5}""", """{"A":1.25}""")]
    [InlineData("""{"A":"x"}""", """{"A":"y"}""")]
    [InlineData("""{"A":true}""", """{"A":false}""")]
    [InlineData("""{"A":null}""", """{}""")]
    [InlineData("""{"A":null}""", """{"A":0}""")]
    [InlineData("""{"A":{"B":1}}""", """{"A":{"B":"1"}}""")]
    public void OneChangedLeaf_ChangesTheHash(string left, string right) => Assert.NotEqual(Hash(left), Hash(right));

    [Fact]
    public void ReorderedArrayElement_ChangesTheHash() => Assert.NotEqual(Hash("""{"A":[1,2,3]}"""), Hash("""{"A":[2,1,3]}"""));

    /// <summary>
    /// Pins the canonical text and its hash to literals computed outside this code (FNV-1a 64 over the UTF-8 text), so
    /// a per-process hash, a culture-sensitive key order or a change of number format fails here. "Zulu" before "alpha"
    /// is the ordinal order; a culture-aware sort would put it last.
    /// </summary>
    [Fact]
    public void FixedTree_HashesToAPinnedLiteralOverItsCanonicalText()
    {
        var tree = new JsonObject
        {
            ["alpha"] = new JsonArray(3, -42, 1.5, "x"),
            ["Zulu"] = new JsonObject { ["b"] = true, ["a"] = null },
            ["Mid"] = "text",
        };

        Assert.Equal(
            """{"Mid":"text","Zulu":{"a":null,"b":true},"alpha":[3,-42,1.5,"x"]}""",
            Encoding.UTF8.GetString(SnapshotStateHash.CanonicalUtf8(tree))
        );
        Assert.Equal(6401434053270028732UL, SnapshotStateHash.Compute(tree));
    }

    [Fact]
    public void RealSnapshot_HashesStablyAcrossCallsAndSerializations()
    {
        TestVnasData.EnsureInitialized();
        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.LoadScenario(AiTestFixture.ParkedAtOak, 1, MagneticDeclination.EvaluationDateUtc);
        StateSnapshotDto snapshot = engine.CaptureSnapshot();

        JsonNode? first = SnapshotTreeDiff.ToComparableNode(snapshot, "first");
        JsonNode? second = SnapshotTreeDiff.ToComparableNode(snapshot, "second");
        ulong hash = SnapshotStateHash.Compute(first);

        Assert.Equal(hash, SnapshotStateHash.Compute(first));
        Assert.Equal(hash, SnapshotStateHash.Compute(second));
        Assert.NotEqual(Hash("{}"), hash);
    }
}
