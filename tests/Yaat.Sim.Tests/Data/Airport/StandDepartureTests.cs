using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Tests.Data.Airport.Precompute;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// How a stand is left (<see cref="StandDepartures"/>): the geometric answer the layout build stores, measured on the real
/// KOAK and KSFO layouts, and the warnings an airport sidecar's overrides raise.
/// </summary>
public class StandDepartureTests(ITestOutputHelper output)
{
    /// <summary>How far from the stand the tests put a connector's foot, feet.</summary>
    private const double FootDistanceFt = 100.0;

    [Fact]
    public void Classify_NoHeading_IsPushBack()
    {
        GroundNode ga3 = Stand(PushTargetPlannerTests.Oak(), "GA3");
        var headless = new GroundNode
        {
            Id = ga3.Id,
            Position = ga3.Position,
            Type = GroundNodeType.Parking,
            Name = ga3.Name,
        };

        Assert.Equal(StandDeparture.PushBack, StandDepartures.Classify(headless, FootOff(ga3, 0.0)));
    }

    [Fact]
    public void Classify_NoConnector_IsPushBack() =>
        Assert.Equal(StandDeparture.PushBack, StandDepartures.Classify(Stand(PushTargetPlannerTests.Oak(), "GA3"), null));

    /// <summary>A stand lying on its connector edge's own line has no outward bearing: the foot is the stand, so push-back.</summary>
    [Fact]
    public void Classify_FootAtTheStand_IsPushBack()
    {
        GroundNode ga3 = Stand(PushTargetPlannerTests.Oak(), "GA3");

        Assert.Equal(StandDeparture.PushBack, StandDepartures.Classify(ga3, ga3.Position));
    }

    /// <summary>The foot of the connector less than 90° off the stand's heading is a taxi-out stand; past 90°, push-back.</summary>
    [Theory]
    [InlineData(89.0, StandDeparture.TaxiOut)]
    [InlineData(-89.0, StandDeparture.TaxiOut)]
    [InlineData(91.0, StandDeparture.PushBack)]
    [InlineData(-91.0, StandDeparture.PushBack)]
    public void Classify_ByTheFootsAngleOffTheHeading(double offHeadingDeg, StandDeparture expected)
    {
        GroundNode ga3 = Stand(PushTargetPlannerTests.Oak(), "GA3");

        Assert.Equal(expected, StandDepartures.Classify(ga3, FootOff(ga3, offHeadingDeg)));
    }

    /// <summary>
    /// KOAK GA3, GA5 and GA6 are authored alike — beside the taxiway their parking connector joins — and classify
    /// TaxiOut from the perpendicular foot alone, before any sidecar override.
    /// </summary>
    [Fact]
    public void KoakGaStandsBesideTheirTaxiway_AuthoredAlike_ClassifyTaxiOut()
    {
        AirportGroundLayout oak = PushTargetPlannerTests.Oak();

        Assert.All((string[])["GA3", "GA5", "GA6"], name => Assert.Equal(StandDeparture.TaxiOut, Stand(oak, name).StandDeparture));
    }

    /// <summary>
    /// With the shipped sidecars applied — a stand's departure read the way the live sim reads it, through
    /// <see cref="StandDepartures.StandDepartureOf"/> — every KOAK <c>GA*</c> stand is a taxi-out stand. Logs the stands the
    /// shipped sidecars override and each airport's taxi-out count.
    /// </summary>
    [Fact]
    public void KoakGaStands_WithShippedSidecars_AreTaxiOut()
    {
        AirportGroundLayout oak = PushTargetPlannerTests.Oak();
        AirportSidecarCatalog sidecars = PushTargetPlannerTests.Sidecars.Value;
        List<GroundNode> ga =
        [
            .. oak.Nodes.Values.Where(n =>
                (n.Type == GroundNodeType.Parking) && (n.Name is { } name) && name.StartsWith("GA", StringComparison.Ordinal)
            ),
        ];

        LogStands(oak, ["GA", "KAI", "JSX", "HELI"]);
        LogStands(PushTargetPlannerTests.Sfo(), ["CG"]);

        Assert.Empty(ga.Where(n => StandDepartures.StandDepartureOf(oak, n, sidecars) == StandDeparture.PushBack).Select(n => n.Name));
    }

    /// <summary>
    /// Every KOAK North Field stand — a parking node right of runway 28R's nose, so north of the 10L/28R centerline — is a
    /// taxi-out stand with the shipped sidecars, picked by side of the runway rather than by name.
    /// </summary>
    [Fact]
    public void KoakNorthFieldStands_WithShippedSidecars_AreTaxiOut()
    {
        AirportGroundLayout oak = PushTargetPlannerTests.Oak();
        AirportSidecarCatalog sidecars = PushTargetPlannerTests.Sidecars.Value;

        LogStands(oak, ["GA", "OLD", "NEW", "SIG", "MTN", "RON", "PT", "S5", "S7", "S8"]);

        Assert.Empty(
            NorthFieldStands(oak)
                .Where(n => StandDepartures.StandDepartureOf(oak, n, sidecars) != StandDeparture.TaxiOut)
                .Select(n => $"{n.Name} #{n.Id}")
        );
    }

    /// <summary>The KOAK South Field remote stands a tug pushes off, whose geometry reads taxi-out, are push-back stands.</summary>
    [Fact]
    public void KoakSouthFieldRemotes_WithShippedSidecars_ArePushBack() =>
        AssertShipped(["S8B", "S5A", "S5B", "S7", "RON9", "RON10", "PT1", "PT2"], StandDeparture.PushBack);

    /// <summary>The KOAK MTN stands are pushed off or taxied out of alike.</summary>
    [Fact]
    public void KoakMtnStands_WithShippedSidecars_AreEither() =>
        AssertShipped(["MTN1", "MTN2", "MTN3", "MTN4", "MTN5", "MTN6", "MTN7", "MTN8", "MTN1A", "MTN2A", "MTN6A"], StandDeparture.Either);

    /// <summary>
    /// The North Field holds 64 stands; a layout refresh that moves a stand across the 10L/28R centerline, or the line
    /// itself, changes the count.
    /// </summary>
    [Fact]
    public void KoakNorthFieldStandCount_IsSixtyFour() => Assert.Equal(64, NorthFieldStands(PushTargetPlannerTests.Oak()).Count);

    /// <summary>
    /// An area rule gives every stand on its side of the runway end's extended centerline its departure, and leaves every
    /// other stand on its geometry. Right of 28R and left of 10L are the same side, the North Field.
    /// </summary>
    [Theory]
    [InlineData("28R", ExitSide.Right, true)]
    [InlineData("10L", ExitSide.Left, true)]
    [InlineData("28R", ExitSide.Left, false)]
    [InlineData("10L", ExitSide.Right, false)]
    public void StandDepartureOf_AreaRule_CoversItsSideOnly(string runway, ExitSide side, bool northField)
    {
        AirportGroundLayout oak = PushTargetPlannerTests.Oak();
        var sidecars = new AirportSidecarCatalog([
            new AirportSidecar(oak.AirportId) { StandDepartureAreas = [new StandDepartureArea(runway, side, StandDeparture.Either, null)] },
        ]);
        HashSet<int> north = [.. NorthFieldStands(oak).Select(n => n.Id)];
        List<GroundNode> parking = [.. oak.Nodes.Values.Where(n => n.Type == GroundNodeType.Parking)];

        List<GroundNode> onSide = [.. parking.Where(n => north.Contains(n.Id) == northField)];
        Assert.NotEmpty(onSide);
        Assert.All(onSide, n => Assert.Equal(StandDeparture.Either, StandDepartures.StandDepartureOf(oak, n, sidecars)));
        Assert.All(
            parking.Where(n => north.Contains(n.Id) != northField),
            n => Assert.Equal(n.StandDeparture ?? StandDeparture.PushBack, StandDepartures.StandDepartureOf(oak, n, sidecars))
        );
    }

    /// <summary>A per-name override wins over an area rule covering the same stand.</summary>
    [Fact]
    public void StandDepartureOf_NameOverrideBeatsAreaRule()
    {
        AirportGroundLayout oak = PushTargetPlannerTests.Oak();
        var sidecars = new AirportSidecarCatalog([
            new AirportSidecar(oak.AirportId)
            {
                StandDepartureOverrides = new Dictionary<string, StandDeparture>(StringComparer.OrdinalIgnoreCase)
                {
                    ["OLD1"] = StandDeparture.Either,
                },
                StandDepartureAreas = [new StandDepartureArea("28R", ExitSide.Right, StandDeparture.TaxiOut, "North Field")],
            },
        ]);

        Assert.Equal(StandDeparture.Either, StandDepartures.StandDepartureOf(oak, Stand(oak, "OLD1"), sidecars));
        Assert.Equal(StandDeparture.TaxiOut, StandDepartures.StandDepartureOf(oak, Stand(oak, "NEW1"), sidecars));
    }

    /// <summary>An area rule naming a runway end the layout lacks warns; one naming a real end does not.</summary>
    [Fact]
    public void WarnAboutOverrides_AreaRuleNamingNoRunway_Warns()
    {
        AirportGroundLayout oak = PushTargetPlannerTests.Oak();
        var sidecars = new AirportSidecarCatalog([
            new AirportSidecar(oak.AirportId)
            {
                StandDepartureAreas =
                [
                    new StandDepartureArea("36", ExitSide.Left, StandDeparture.TaxiOut, null),
                    new StandDepartureArea("28R", ExitSide.Right, StandDeparture.TaxiOut, null),
                ],
            },
        ]);
        var capture = WarningLogCapture.Install();

        StandDepartures.WarnAboutOverrides(oak, sidecars);

        string warning = Assert.Single(capture.Warnings);
        Assert.Contains("runway 36", warning, StringComparison.Ordinal);
        Assert.Contains("names no runway end", warning, StringComparison.Ordinal);
    }

    /// <summary>An override naming no stand on the layout, and one naming a stand several parking nodes share, each warn.</summary>
    [Fact]
    public void WarnAboutOverrides_WarnsForANameWithNoStandAndASharedName()
    {
        (AirportGroundLayout layout, string shared) = new[] { PushTargetPlannerTests.Oak(), PushTargetPlannerTests.Sfo() }
            .Select(l => (Layout: l, Shared: SharedStandName(l)))
            .Where(p => p.Shared is not null)
            .Select(p => (p.Layout, Shared: p.Shared!))
            .First();
        var sidecars = new AirportSidecarCatalog([
            new AirportSidecar(layout.AirportId)
            {
                StandDepartureOverrides = new Dictionary<string, StandDeparture>(StringComparer.OrdinalIgnoreCase)
                {
                    ["NOSUCHSTAND"] = StandDeparture.TaxiOut,
                    [shared] = StandDeparture.TaxiOut,
                },
            },
        ]);
        var capture = WarningLogCapture.Install();

        StandDepartures.WarnAboutOverrides(layout, sidecars);

        Assert.Equal(2, capture.Warnings.Count);
        Assert.Contains(
            capture.Warnings,
            w => w.Contains("NOSUCHSTAND", StringComparison.Ordinal) && w.Contains("names no stand", StringComparison.Ordinal)
        );
        Assert.Contains(
            capture.Warnings,
            w => w.Contains(shared, StringComparison.Ordinal) && w.Contains("applies to all", StringComparison.Ordinal)
        );
    }

    private void LogStands(AirportGroundLayout layout, string[] prefixes)
    {
        AirportSidecarCatalog sidecars = PushTargetPlannerTests.Sidecars.Value;
        List<GroundNode> parking = [.. layout.Nodes.Values.Where(n => n.Type == GroundNodeType.Parking)];
        int geometric = parking.Count(n => n.StandDeparture == StandDeparture.TaxiOut);
        int shipped = parking.Count(n => StandDepartures.StandDepartureOf(layout, n, sidecars) == StandDeparture.TaxiOut);
        int either = parking.Count(n => StandDepartures.StandDepartureOf(layout, n, sidecars) == StandDeparture.Either);
        output.WriteLine(
            $"{layout.AirportId}: {parking.Count} parking nodes; TaxiOut {geometric} by geometry, {shipped} shipped; Either {either} shipped"
        );
        IEnumerable<GroundNode> named = layout
            .Nodes.Values.Where(n => (n.Name is { } name) && prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n.Name, StringComparer.Ordinal);
        foreach (GroundNode node in named)
        {
            string withSidecars = node.Type == GroundNodeType.Parking ? StandDepartures.StandDepartureOf(layout, node, sidecars).ToString() : "-";
            output.WriteLine(
                $"  {node.Name} #{node.Id} {node.Type}: heading {node.TrueHeading?.Degrees:F1}, {node.StandDeparture} by geometry, {withSidecars} shipped"
            );
        }
    }

    /// <summary>Asserts every KOAK parking node of each name exists and reads <paramref name="expected"/> with the shipped sidecars.</summary>
    private static void AssertShipped(string[] names, StandDeparture expected)
    {
        AirportGroundLayout oak = PushTargetPlannerTests.Oak();
        AirportSidecarCatalog sidecars = PushTargetPlannerTests.Sidecars.Value;
        Assert.All(
            names,
            name =>
            {
                List<GroundNode> stands =
                [
                    .. oak.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && string.Equals(n.Name, name, StringComparison.Ordinal)),
                ];
                Assert.NotEmpty(stands);
                Assert.All(stands, n => Assert.Equal(expected, StandDepartures.StandDepartureOf(oak, n, sidecars)));
            }
        );
    }

    /// <summary>KOAK's parking nodes right of runway 28R's nose: north of the 10L/28R centerline, extended past both ends.</summary>
    private static List<GroundNode> NorthFieldStands(AirportGroundLayout oak)
    {
        GroundRunway runway = oak.FindRunway("28R") ?? throw new InvalidOperationException("KOAK layout has no runway 28R");
        (LatLon threshold, double landingCourseDeg) =
            runway.LandingThresholdForEnd("28R") ?? throw new InvalidOperationException("KOAK runway 28R has no geometry");
        var course = new TrueHeading(landingCourseDeg);
        return
        [
            .. oak.Nodes.Values.Where(n =>
                (n.Type == GroundNodeType.Parking) && (GeoMath.SignedCrossTrackDistanceNm(n.Position, threshold, course) > 0)
            ),
        ];
    }

    private static string? SharedStandName(AirportGroundLayout layout) =>
        layout
            .Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name is not null))
            .GroupBy(n => n.Name!, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)
            ?.Key;

    private static LatLon FootOff(GroundNode stand, double offHeadingDeg) =>
        GeoMath.ProjectPoint(stand.Position, new TrueHeading(stand.TrueHeading!.Value.Degrees + offHeadingDeg), FootDistanceFt / GeoMath.FeetPerNm);

    private static GroundNode Stand(AirportGroundLayout layout, string name) =>
        layout.Nodes.Values.Where(n => (n.Type == GroundNodeType.Parking) && (n.Name == name)).OrderBy(n => n.Id).First();
}
