using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Pathfinding;

/// <summary>
/// Movement area vs non-movement lanes, by the first rule that matches (<see cref="MovementAreaRule"/>): RAMP, a
/// runway key or a single letter is never a lane; a lane with a runway holding position on its own edges is movement
/// area; a lane with a parking gate one edge off it is a ramp taxilane; a lane joined to two or more single-letter
/// taxiways is movement area; any other lane is a ramp taxilane. The airport sidecar's <c>movementAreaTaxiways</c> /
/// <c>nonMovementTaxilanes</c> lists override that both ways. The ramp-lane cut and the tug planner read the same verdict.
/// Every multi-character taxiway name of SFO and OAK is pinned, so a layout or rule change that moves one fails here.
/// </summary>
public class MovementAreaClassificationTests
{
    private const string Sfo = "SFO";

    private const string Oak = "OAK";

    /// <summary>SFO's movement-area lanes (from the SFO airport diagram).</summary>
    private static readonly string[] SfoMovementArea =
    [
        "A1",
        "A2",
        "C2",
        "C3",
        "F1",
        "F2",
        "GL",
        "L2",
        "M1",
        "S1",
        "S2",
        "S3",
        "Z1",
        "AF",
        "AY1",
        "AY2",
        "AY3",
        "AY4",
        "B1",
        "B2",
        "B3",
        "B4",
        "CZ",
        "LF",
        "Q1",
        "ZS",
    ];

    /// <summary>SFO's non-movement ramp taxilanes (from the SFO airport diagram).</summary>
    private static readonly string[] SfoNonMovement =
    [
        "B5",
        "BC",
        "CG",
        "M2",
        "M3",
        "M5",
        "SBE",
        "SBW",
        "T41E",
        "T41W",
        "T421",
        "T422",
        "T423",
        "T424",
        "T5",
        "T5A",
        "T5B",
        "T6A",
        "T6B",
        "T7A",
        "T7B",
        "T8",
        "T9",
        "UB1",
        "UB2",
        "Z2",
        "M4",
        "SBC",
        "T6",
        "T7",
    ];

    private static readonly string[] OakMovementArea = ["C1", "W1", "W2", "W3", "W4", "W5", "W6", "W7"];

    /// <summary>OAK's ramp taxilanes: TE and TC join taxiways T and U, but gates hang off them, and rule 3 outranks rule 4.</summary>
    private static readonly string[] OakNonMovement = ["B1", "B2", "B3", "B5", "R1", "S1", "TC", "TE"];

    private readonly ITestOutputHelper _output;

    public MovementAreaClassificationTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    private static AirportGroundLayout? Layout(string airport) =>
        TestVnasData.NavigationDb is null ? null : new TestAirportGroundData().GetLayout(airport);

    [Fact]
    public void Sfo_WholeClassification()
    {
        AirportGroundLayout? layout = Layout(Sfo);
        if (layout is null)
        {
            return;
        }

        AssertWholeClassification(layout, SfoMovementArea, SfoNonMovement);
    }

    [Fact]
    public void Oak_WholeClassification()
    {
        AirportGroundLayout? layout = Layout(Oak);
        if (layout is null)
        {
            return;
        }

        AssertWholeClassification(layout, OakMovementArea, OakNonMovement);
    }

    /// <summary>
    /// One SFO name decided by each rule, in the order they are tried: a single letter is no lane (1); M1 has gates off
    /// it across the apron but carries a runway holding position, and rule 2 outranks rule 3; T5A has gate D5 off it
    /// (3); B1 joins A and B with no gate off it (4); M4 joins only M1 (5).
    /// </summary>
    [Theory]
    [InlineData(Sfo, "A", MovementAreaRule.NotALane)]
    [InlineData(Sfo, "M1", MovementAreaRule.RunwayHoldShort)]
    [InlineData(Sfo, "T5A", MovementAreaRule.GateOffLane)]
    [InlineData(Sfo, "B1", MovementAreaRule.JoinsTwoTaxiways)]
    [InlineData(Sfo, "M4", MovementAreaRule.Taxilane)]
    [InlineData(Oak, "TE", MovementAreaRule.GateOffLane)]
    public void DerivedRule_FirstMatchDecides(string airport, string name, MovementAreaRule expected)
    {
        AirportGroundLayout? layout = Layout(airport);
        if (layout is null)
        {
            return;
        }

        var classification = MovementAreaClassification.For(layout);
        _output.WriteLine(Describe(classification, name));
        Assert.Equal(expected, classification.DerivedRule(name));
        Assert.Equal(expected == MovementAreaRule.GateOffLane, classification.GateOf(name) is not null);
    }

    /// <summary>
    /// The rule makes T5A a ramp taxilane (gate D5 hangs off it); a sidecar <c>movementAreaTaxiways</c> entry makes it
    /// movement area. A listed name the layout lacks does not throw.
    /// </summary>
    [Fact]
    public void Sidecar_MovementAreaOverride_WinsOverRule()
    {
        AirportGroundLayout? layout = Layout(Sfo);
        if (layout is null)
        {
            return;
        }

        Assert.False(MovementAreaClassification.Build(layout, AirportSidecarCatalog.Empty).IsMovementArea("T5A"));

        var sidecars = new AirportSidecarCatalog([new AirportSidecar("KSFO") { MovementAreaTaxiways = ["T5A", "NOSUCH9"] }]);
        var classification = MovementAreaClassification.Build(layout, sidecars);

        Assert.True(classification.IsMovementArea("T5A"));
        Assert.False(classification.IsMovementArea("T5B"));
    }

    /// <summary>
    /// The rule makes B1 movement area (it joins taxiways A and B with no gate off it); a sidecar
    /// <c>nonMovementTaxilanes</c> entry pins it to a ramp taxilane.
    /// </summary>
    [Fact]
    public void Sidecar_NonMovementOverride_WinsOverRule()
    {
        AirportGroundLayout? layout = Layout(Sfo);
        if (layout is null)
        {
            return;
        }

        Assert.True(MovementAreaClassification.Build(layout, AirportSidecarCatalog.Empty).IsMovementArea("B1"));

        var sidecars = new AirportSidecarCatalog([new AirportSidecar("KSFO") { NonMovementTaxilanes = ["B1"] }]);
        var classification = MovementAreaClassification.Build(layout, sidecars);

        Assert.False(classification.IsMovementArea("B1"));
        Assert.True(classification.IsMovementArea("B2"));
    }

    /// <summary>A name in both sidecar lists is movement area, whichever verdict the rules derive (T5A: rule 3, a taxilane).</summary>
    [Fact]
    public void Sidecar_NameInBothLists_IsMovementArea()
    {
        AirportGroundLayout? layout = Layout(Sfo);
        if (layout is null)
        {
            return;
        }

        var sidecars = new AirportSidecarCatalog([
            new AirportSidecar("KSFO") { MovementAreaTaxiways = ["T5A", "B1"], NonMovementTaxilanes = ["T5A", "B1"] },
        ]);
        var classification = MovementAreaClassification.Build(layout, sidecars);

        Assert.True(classification.IsMovementArea("T5A"));
        Assert.True(classification.IsMovementArea("B1"));
    }

    /// <summary>
    /// Every listed name is in the layout and has the listed verdict, the ramp-lane cut agrees with it, and the lists
    /// between them cover every multi-character taxiway name the layout carries.
    /// </summary>
    private void AssertWholeClassification(AirportGroundLayout layout, string[] movementArea, string[] nonMovement)
    {
        var classification = MovementAreaClassification.For(layout);
        List<string> wrong = [.. Misclassified(layout, classification, movementArea, expectMovementArea: true)];
        wrong.AddRange(Misclassified(layout, classification, nonMovement, expectMovementArea: false));
        Assert.True(wrong.Count == 0, $"{layout.AirportId} misclassified: {string.Join(", ", wrong)}");

        var listed = new HashSet<string>(movementArea.Concat(nonMovement), StringComparer.OrdinalIgnoreCase);
        List<string> unlisted = [.. MultiCharacterNames(layout).Where(name => !listed.Contains(name))];
        Assert.True(unlisted.Count == 0, $"{layout.AirportId} names with no pinned verdict: {string.Join(", ", unlisted)}");
    }

    /// <summary>The names whose movement-area verdict differs from <paramref name="expectMovementArea"/>, each with its verdict.</summary>
    private List<string> Misclassified(AirportGroundLayout layout, MovementAreaClassification classification, string[] names, bool expectMovementArea)
    {
        var wrong = new List<string>();
        foreach (string name in names)
        {
            Assert.True(classification.DerivedRule(name) is not null, $"{name}: not in the {layout.AirportId} layout");
            bool movementArea = classification.IsMovementArea(name);
            Assert.Equal(movementArea, !RampLaneReposition.IsRampTaxilane(layout, name));
            _output.WriteLine($"TABLE {layout.AirportId} {Describe(classification, name)}");
            if (movementArea != expectMovementArea)
            {
                wrong.Add(Describe(classification, name));
            }
        }

        return wrong;
    }

    /// <summary>Every multi-character taxiway name in the layout, neither RAMP nor a runway key.</summary>
    private static IEnumerable<string> MultiCharacterNames(AirportGroundLayout layout) =>
        layout
            .AllEdges.SelectMany(RampLaneReposition.EdgeNames)
            .Where(n =>
                (n.Length > 1) && !n.Equals("RAMP", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("RWY", StringComparison.OrdinalIgnoreCase)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

    private static string Describe(MovementAreaClassification classification, string name)
    {
        string gate = classification.GateOf(name) is { } node ? $", gate #{node.Id} {node.Name}" : string.Empty;
        string verdict = classification.IsMovementArea(name) ? "movement area" : "non-movement";
        return $"{name}: {verdict} (rule {(int?)classification.DerivedRule(name)}{gate})";
    }
}
