using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Data.Airport;

/// <summary>
/// The largest wingspan a stand takes (<see cref="StandFit"/>), estimated from the layout's stand spacing: the smallest
/// lateral gap to another stand within 300 ft that sits beside it, less the wingtip clearance of the group the span falls
/// in, the neighbour assumed to hold the same span. Every test runs on the committed SFO layout. A configuration SFO does
/// not draw is derived from it: copies of a real stand's feature, renamed <c>ZZ*</c> and placed out over the bay more than
/// 2000 ft from any SFO stand, at the offsets the case names.
/// </summary>
public class StandFitTests
{
    private const double FeetPerDegLat = 60.0 * GeoMath.FeetPerNm;

    /// <summary>The heading the derived stands face, the SIG rows' heading.</summary>
    private const int DerivedHeading = 284;

    /// <summary>Where the derived groups start: over the bay north-east of SFO, far from every drawn stand.</summary>
    private static readonly LatLon DerivedBase = new(37.640, -122.330);

    /// <summary>The consult's measured SIG spans, feet.</summary>
    private static readonly Dictionary<string, double> SigSpans = new()
    {
        ["SIG1"] = 79.0,
        ["SIG2"] = 76.5,
        ["SIG3"] = 56.8,
        ["SIG4"] = 52.8,
        ["SIG5"] = 52.8,
        ["SIG6"] = 79.4,
        ["SIG7"] = 70.3,
        ["SIG8"] = 69.9,
        ["SIG9"] = 62.7,
        ["SIG10"] = 62.7,
    };

    /// <summary>
    /// The derived stands: name, group (each group 2000 ft east of the last), along and lateral offset from the group's
    /// anchor in <see cref="DerivedHeading"/>'s frame (feet), and heading.
    /// </summary>
    private static readonly (string Name, int Group, double AlongFt, double LateralFt, int Heading)[] DerivedStands =
    [
        ("ZZA1", 0, 0, 0, 284),
        ("ZZA2", 0, 0, 140, 284),
        ("ZZA3", 0, 0, -140, 284),
        ("ZZB1", 1, 0, 0, 284),
        ("ZZB2", 1, 0, 150, 284),
        ("ZZC1", 2, 0, 0, 284),
        ("ZZD1", 3, 0, 0, 284),
        ("ZZD2", 3, 120, 80, 284),
        ("ZZE1", 4, 0, 0, 284),
        ("ZZE2", 4, 10, 110, 104),
        ("ZZF5", 5, 0, 0, 284),
        ("ZZF5A", 5, 0, 60, 284),
        ("ZZF5B", 5, 0, -60, 284),
        ("ZZF4", 5, 0, 200, 284),
        ("ZZF6", 5, 0, -200, 284),
        ("ZZG12", 6, 0, 0, 284),
        ("ZZG13", 6, 0, 60, 284),
        ("ZZG14", 6, 0, 120, 284),
        ("ZZJ1", 7, 0, 0, 284),
        ("ZZH1", 7, 0, 80, 284),
        ("ZZH2", 7, 0, 120, 284),
        ("ZZH3", 7, 0, 160, 284),
        ("ZZH4", 7, 0, 200, 284),
        ("ZZK1", 8, 0, 0, 284),
        ("ZZM1", 8, 0, 20, 284),
        ("ZZN1", 8, 0, -140, 284),
        ("ZZQ1", 8, 0, 160, 284),
        ("ZZP1", 9, 0, 0, 284),
        ("ZZP1A", 9, 0, 160, 284),
        ("ZZR1", 10, 0, 0, 284),
        ("ZZR2", 10, 210, 230, 284),
        ("ZZS1", 11, 0, 0, 284),
        ("ZZS2", 11, 0, 140, 284),
    ];

    private static readonly Lazy<JsonObject> SfoJson = new(() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine("TestData", "sfo.geojson")))!
    );

    private static readonly Lazy<AirportGroundLayout> Sfo = new(() => Parse(SfoJson.Value));

    private static readonly Lazy<AirportGroundLayout> Derived = new(BuildDerived);

    private static readonly Lazy<AirportGroundLayout> DrawnTwice = new(() =>
    {
        JsonObject geo = Copy();
        Features(geo).Add(Feature(geo, "SIG3").DeepClone());
        return Parse(geo);
    });

    [Fact]
    public void Row01_ContactGateRow_BothNeighboursAt140_Takes118() => Assert.Equal(118.0, DerivedSpan("ZZA1"), 0.5);

    [Fact]
    public void Row01_SpacingOf150_Takes125_AdmittingAB752() => Assert.Equal(125.0, DerivedSpan("ZZB1"), 0.5);

    [Fact]
    public void Row02_EndOfRow_OneNeighbourAt140_Takes118() => Assert.Equal(118.0, DerivedSpan("ZZS1"), 0.5);

    /// <summary>
    /// ZZK1 and ZZM1 sit 20 ft apart under unrelated names: as overlapping positions they do not cap each other (alone they
    /// would floor at 49 ft). Their row neighbours ZZN1 and ZZQ1 sit 140 ft beside the nearer of them, so each takes 118 ft.
    /// </summary>
    [Fact]
    public void Row14b_AlternateByDistance_UnrelatedNames_DoNotCapEachOther()
    {
        Assert.Equal(118.0, DerivedSpan("ZZK1"), 0.5);
        Assert.Equal(118.0, DerivedSpan("ZZM1"), 0.5);
    }

    /// <summary>
    /// ZZP1 and ZZP1A are siblings, but 160 ft apart, past the 150 ft sibling limit, so they cap each other: 160 less group
    /// IV's 25 ft clearance is 135 ft.
    /// </summary>
    [Fact]
    public void Row14c_SiblingsPast150Ft_CapEachOther()
    {
        Assert.Equal(135.0, DerivedSpan("ZZP1"), 0.5);
        Assert.Equal(135.0, DerivedSpan("ZZP1A"), 0.5);
    }

    /// <summary>
    /// ZZR2 sits 210 ft ahead and 230 ft beside ZZR1, beside it but 311 ft away, past the 300 ft radius, so it caps nothing
    /// and ZZR1 takes the airport ceiling. Counted, 230 ft less 25 would give 205 ft, under SFO's 214 ft ceiling.
    /// </summary>
    [Fact]
    public void Row14d_BesideButPast300Ft_DoesNotCap() =>
        Assert.Equal(AirplaneDesignGroups.MaxWingspanFt(Ceiling(Derived.Value)), DerivedSpan("ZZR1"));

    /// <summary>The committed SFO layout's widest runway records a width under 200 ft, so its ceiling is ADG V (214 ft).</summary>
    [Fact]
    public void Row03_NoNeighbourWithin300Ft_TakesTheAirportCeiling() =>
        Assert.Equal(AirplaneDesignGroups.MaxWingspanFt(Ceiling(Derived.Value)), DerivedSpan("ZZC1"));

    /// <summary>SIG2 sits 43 ft behind and 92 ft beside SIG1: the stagger is under the lateral offset, so it counts.</summary>
    [Fact]
    public void Row04_Echelon_Sig1BesideSig2_Takes79() => Assert.Equal(79.0, SfoSpan("SIG1"), 1.0);

    [Fact]
    public void Row05_StaggerLargerThanLateral_DoesNotCap()
    {
        double ceilingFt = AirplaneDesignGroups.MaxWingspanFt(Ceiling(Derived.Value));
        Assert.Equal(ceilingFt, DerivedSpan("ZZD1"));
        Assert.Equal(ceilingFt, DerivedSpan("ZZD2"));
    }

    /// <summary>
    /// 42-1 faces SIG1 across the taxilane 217 ft ahead and 63 ft aside; counted, it would cap SIG1 at 53 ft. It is not
    /// beside SIG1, so SIG2 binds instead.
    /// </summary>
    [Fact]
    public void Row06_NoseToNoseAcrossATaxilane_Sig1And42Dash1_DoesNotCap()
    {
        GroundNode sig1 = Stand(Sfo.Value, "SIG1");
        OutlinePoint offset = new GroundOutlineFrame(sig1.Position).ToLocal(Stand(Sfo.Value, "42-1").Position);
        double h = sig1.TrueHeading!.Value.ToRadians();
        double alongFt = (offset.EastFt * Math.Sin(h)) + (offset.NorthFt * Math.Cos(h));
        double lateralFt = (offset.EastFt * Math.Cos(h)) - (offset.NorthFt * Math.Sin(h));
        Assert.True(Math.Sqrt((alongFt * alongFt) + (lateralFt * lateralFt)) < 300.0, $"42-1 is {alongFt:F0} ft along, {lateralFt:F0} ft aside");
        Assert.True(Math.Abs(alongFt) > Math.Abs(lateralFt), $"42-1 is {alongFt:F0} ft along, {lateralFt:F0} ft aside");
        Assert.Equal(79.0, SfoSpan("SIG1"), 1.0);
    }

    [Fact]
    public void Row07_ReciprocalHeadingBeside_Takes95() => Assert.Equal(95.0, DerivedSpan("ZZE1"), 0.5);

    /// <summary>41-1 sits 66 ft behind and 80 ft beside SIG7 at right angles; it counts, nearer than SIG6 at 94 ft.</summary>
    [Fact]
    public void Row08_PerpendicularNeighbour_Sig7Beside41Dash1_Takes70() => Assert.Equal(70.3, SfoSpan("SIG7"), 1.0);

    /// <summary>
    /// C4U and C4R overlap 13.5 ft apart; neither caps the other, and each is capped by C6 in its own frame: 138.7 ft beside
    /// C4U (all of III, 118 ft) and 131.6 ft beside C4R (116.6 ft), both group III.
    /// </summary>
    [Fact]
    public void Row09_OverlappingAlternates_C4UAndC4R_EachCappedByC6()
    {
        Assert.Equal(118.0, SfoSpan("C4U"), 1.0);
        Assert.Equal(116.6, SfoSpan("C4R"), 1.0);
    }

    /// <summary>
    /// ZZF5A and ZZF5B sit 60 ft either side of ZZF5; as siblings they do not cap it. Its row neighbours 200 ft off do: 200 less
    /// group V's 25 ft clearance is 175 ft.
    /// </summary>
    [Fact]
    public void Row10_MarsSetWithSiblingNames_CentreCappedOnlyByItsRowNeighbours() => Assert.Equal(175.0, DerivedSpan("ZZF5"), 0.5);

    /// <summary>ZZG12 to ZZG14 sit 60 ft apart under unrelated names, so the centre is capped at 50 ft; the sidecar override fixes it.</summary>
    [Fact]
    public void Row11_MarsSetWithoutSiblingNames_CapsTheCentre_AndTheOverrideFixesIt()
    {
        Assert.Equal(50.0, DerivedSpan("ZZG13"), 0.5);
        AirportGroundLayout layout = Derived.Value;
        Assert.Equal(118.0, StandFit.MaxWingspanFt(layout, Stand(layout, "ZZG13"), Ceiling(layout), AirplaneDesignGroup.III));
    }

    [Fact]
    public void Row12_StandBesideATieDownRow_Takes70() => Assert.Equal(70.0, DerivedSpan("ZZJ1"), 0.5);

    [Fact]
    public void Row13_TieDownRowAt40Ft_TakesTheAdgIFloor()
    {
        Assert.Equal(49.0, DerivedSpan("ZZH2"));
        Assert.Equal(49.0, DerivedSpan("ZZH1"));
    }

    /// <summary>SIG3's feature appended a second time, at the same place: the copy takes exactly what the single stand did.</summary>
    [Fact]
    public void Row14_StandDrawnTwice_TakesWhatTheSingleStandDoes()
    {
        AirportGroundLayout layout = DrawnTwice.Value;
        List<GroundNode> copies = [.. Parking(layout).Where(n => n.Name == "SIG3")];
        Assert.Equal(2, copies.Count);
        Assert.All(copies, n => Assert.Equal(SfoSpan("SIG3"), Span(layout, n)));
    }

    [Theory]
    [InlineData("SIG1")]
    [InlineData("SIG2")]
    [InlineData("SIG3")]
    [InlineData("SIG4")]
    [InlineData("SIG5")]
    [InlineData("SIG6")]
    [InlineData("SIG7")]
    [InlineData("SIG8")]
    [InlineData("SIG9")]
    [InlineData("SIG10")]
    public void SigStands_MeasuredSpans_MatchTheConsult(string name) => Assert.Equal(SigSpans[name], SfoSpan(name), 1.0);

    /// <summary>A stand takes a type whose span is at most its cap: SIG4 (52.8 ft) takes a Phenom 300 (52.2 ft), not a PC-12 (53.3 ft).</summary>
    [Fact]
    public void Fits_ComparesTheSpanWithTheCap()
    {
        AirportGroundLayout layout = Sfo.Value;
        GroundNode sig4 = Stand(layout, "SIG4");
        Assert.True(StandFit.Fits(layout, sig4, 52.2, Ceiling(layout), null));
        Assert.False(StandFit.Fits(layout, sig4, 53.3, Ceiling(layout), null));
    }

    [Fact]
    public void Invariance_StandOrder_DoesNotChangeAnyEstimate()
    {
        JsonObject geo = Copy();
        JsonNode?[] reversed = [.. Features(geo).Reverse().Select(n => n?.DeepClone())];
        geo["features"] = new JsonArray(reversed);

        AssertSameEstimates(Sfo.Value, Parse(geo), n => Key(n.Name, n.Position.Lat));
    }

    /// <summary>
    /// Every SFO stand moved 1 to 2 ft in a seeded random direction: no SIG span moves more than 4 ft, and a SIG stand at least
    /// 5 ft from every group ceiling keeps its group.
    /// </summary>
    [Fact]
    public void Invariance_OneToTwoFootJitter_MovesSigSpansAtMostFourFeet()
    {
        JsonObject geo = Copy();
        var random = new Random(2018);
        foreach (JsonNode? feature in Features(geo).Where(IsParking))
        {
            double bearing = random.NextDouble() * 2.0 * Math.PI;
            double distanceFt = 1.0 + random.NextDouble();
            MoveFeature(feature!, distanceFt * Math.Sin(bearing), distanceFt * Math.Cos(bearing));
        }

        AirportGroundLayout jittered = Parse(geo);
        foreach (string name in SigSpans.Keys)
        {
            double before = SfoSpan(name);
            double after = Span(jittered, Stand(jittered, name));
            Assert.InRange(after - before, -4.0, 4.0);
            if (Enum.GetValues<AirplaneDesignGroup>().All(g => Math.Abs(AirplaneDesignGroups.MaxWingspanFt(g) - before) >= 5.0))
            {
                Assert.Equal(GroupOf(before), GroupOf(after));
            }
        }
    }

    [Fact]
    public void Invariance_StandDrawnTwice_LeavesEveryEstimateUnchanged() =>
        AssertSameEstimates(Sfo.Value, DrawnTwice.Value, n => Key(n.Name, n.Position.Lat));

    /// <summary>
    /// SFO draws 2-2B twice, 135.6 ft apart: each node gets its own estimate, and with the other node's feature removed the
    /// remaining one's estimate is unchanged.
    /// </summary>
    [Fact]
    public void Invariance_DuplicateName2Dash2B_IsKeyedByNode()
    {
        AirportGroundLayout layout = Sfo.Value;
        List<GroundNode> both = [.. Parking(layout).Where(n => n.Name == "2-2B")];
        Assert.Equal(2, both.Count);
        Assert.All(both, n => Assert.InRange(Span(layout, n), 49.0, 262.0));

        JsonObject geo = Copy();
        Features(geo).Remove(Features(geo).Last(f => Name(f) == "2-2B"));
        AirportGroundLayout removed = Parse(geo);
        GroundNode kept = Assert.Single(Parking(removed), n => n.Name == "2-2B");
        GroundNode original = both.Single(n => Key(n.Name, n.Position.Lat) == Key(kept.Name, kept.Position.Lat));
        Assert.Equal(Span(layout, original), Span(removed, kept));
    }

    /// <summary>C4R overlaps C4U; removing it leaves C4U's estimate unchanged.</summary>
    [Fact]
    public void Invariance_AlternatePair_DoesNotCapEachOther()
    {
        JsonObject geo = Copy();
        Features(geo).Remove(Feature(geo, "C4R"));
        AirportGroundLayout removed = Parse(geo);
        Assert.Equal(SfoSpan("C4U"), Span(removed, Stand(removed, "C4U")));
    }

    /// <summary>A stand 1000 ft ahead of SIG1 and one 150 ft straight ahead of SIG3 change no SIG estimate.</summary>
    [Fact]
    public void Invariance_Locality_DistantOrAheadStandsChangeNothing()
    {
        JsonObject geo = Copy();
        AddDerived(geo, "ZZL1", Offset(Stand(Sfo.Value, "SIG1").Position, DerivedHeading, 1000.0, 0.0), DerivedHeading);
        AddDerived(geo, "ZZL2", Offset(Stand(Sfo.Value, "SIG3").Position, DerivedHeading, 150.0, 0.0), DerivedHeading);
        AirportGroundLayout added = Parse(geo);

        foreach (string name in SigSpans.Keys)
        {
            Assert.Equal(SfoSpan(name), Span(added, Stand(added, name)), 1e-9);
        }
    }

    /// <summary>The whole layout reflected across the meridian through SIG1, headings reflected: every estimate is unchanged.</summary>
    [Fact]
    public void Invariance_Mirror_GivesTheSameEstimates()
    {
        JsonObject geo = Copy();
        double mirrorLon = Stand(Sfo.Value, "SIG1").Position.Lon;
        foreach (JsonNode? feature in Features(geo))
        {
            ReflectCoordinates(feature!["geometry"]!["coordinates"], mirrorLon);
            JsonObject props = feature["properties"]!.AsObject();
            if (props["heading"] is { } heading)
            {
                props["heading"] = (360 - int.Parse(heading.ToString(), CultureInfo.InvariantCulture)) % 360;
            }
        }

        AssertSameEstimates(Sfo.Value, Parse(geo), n => Key(n.Name, n.Position.Lat));
    }

    [Fact]
    public void Invariance_Floor_NoSfoStandTakesLessThanAdgI()
    {
        AirportGroundLayout layout = Sfo.Value;
        Assert.All(Parking(layout), n => Assert.InRange(Span(layout, n), 49.0, 262.0));
    }

    /// <summary>SIG3's estimate is 56.8 ft; a sidecar maxAdg of I caps it at 49 ft.</summary>
    [Fact]
    public void Override_BelowTheEstimate_Wins()
    {
        AirportGroundLayout layout = Sfo.Value;
        Assert.Equal(49.0, StandFit.MaxWingspanFt(layout, Stand(layout, "SIG3"), Ceiling(layout), AirplaneDesignGroup.I));
    }

    /// <summary>SIG4's estimate refuses a G650 (99.6 ft); a sidecar maxAdg of III admits it.</summary>
    [Fact]
    public void Override_AboveTheEstimate_Wins()
    {
        AirportGroundLayout layout = Sfo.Value;
        GroundNode sig4 = Stand(layout, "SIG4");
        Assert.Equal(118.0, StandFit.MaxWingspanFt(layout, sig4, Ceiling(layout), AirplaneDesignGroup.III));
        Assert.False(StandFit.Fits(layout, sig4, 99.6, Ceiling(layout), null));
        Assert.True(StandFit.Fits(layout, sig4, 99.6, Ceiling(layout), AirplaneDesignGroup.III));
    }

    /// <summary>At Flying Cloud (FCM), whose widest runway sets a ceiling under VI, a maxAdg of VI still wins and is warned about.</summary>
    [Fact]
    public void Override_AboveTheAirportCeiling_WinsAndWarns()
    {
        AirportGroundLayout layout = GeoJsonParser.Parse(
            "FCM",
            File.ReadAllText(Path.Combine("TestData", "FCM.geojson")),
            null,
            new LayoutInputs([], VideoMap: null),
            FilletMode.None
        );
        AirplaneDesignGroup ceiling = Ceiling(layout);
        Assert.True(ceiling < AirplaneDesignGroup.VI, $"FCM's ceiling is {ceiling}");
        GroundNode stand = Parking(layout).First();
        var capture = WarningLogCapture.Install();

        Assert.Equal(262.0, StandFit.MaxWingspanFt(layout, stand, ceiling, AirplaneDesignGroup.VI));
        Assert.Contains(capture.Warnings, w => w.Contains("maxAdg", StringComparison.Ordinal) && w.Contains(stand.Name!, StringComparison.Ordinal));
    }

    private static AirportGroundLayout Parse(JsonNode geo) =>
        GeoJsonParser.Parse("SFO", geo.ToJsonString(), null, new LayoutInputs([], VideoMap: null), FilletMode.None);

    private static JsonObject Copy() => (JsonObject)SfoJson.Value.DeepClone();

    private static JsonArray Features(JsonObject geo) => geo["features"]!.AsArray();

    private static bool IsParking(JsonNode? feature) => (string?)feature?["properties"]?["type"] == "parking";

    private static string? Name(JsonNode? feature) => (string?)feature?["properties"]?["name"];

    private static JsonNode Feature(JsonObject geo, string name) => Features(geo).Single(f => IsParking(f) && (Name(f) == name))!;

    private static AirportGroundLayout BuildDerived()
    {
        JsonObject geo = Copy();
        foreach ((string name, int group, double alongFt, double lateralFt, int heading) in DerivedStands)
        {
            LatLon anchor = Offset(DerivedBase, 90.0, group * 2000.0, 0.0);
            AddDerived(geo, name, Offset(anchor, DerivedHeading, alongFt, lateralFt), heading);
        }

        return Parse(geo);
    }

    private static void AddDerived(JsonObject geo, string name, LatLon at, int heading)
    {
        JsonNode feature = Feature(geo, "SIG1").DeepClone();
        feature["properties"]!["name"] = name;
        feature["properties"]!["heading"] = heading;
        feature["geometry"]!["coordinates"] = new JsonArray(at.Lon, at.Lat);
        Features(geo).Add(feature);
    }

    /// <summary>The point <paramref name="alongFt"/> ahead of and <paramref name="lateralFt"/> right of an origin facing a heading.</summary>
    private static LatLon Offset(LatLon origin, double headingDeg, double alongFt, double lateralFt)
    {
        double h = headingDeg * Math.PI / 180.0;
        double eastFt = (alongFt * Math.Sin(h)) + (lateralFt * Math.Cos(h));
        double northFt = (alongFt * Math.Cos(h)) - (lateralFt * Math.Sin(h));
        return new LatLon(origin.Lat + (northFt / FeetPerDegLat), origin.Lon + (eastFt / (FeetPerDegLat * Math.Cos(origin.Lat * Math.PI / 180.0))));
    }

    private static void MoveFeature(JsonNode feature, double eastFt, double northFt)
    {
        JsonArray coordinates = feature["geometry"]!["coordinates"]!.AsArray();
        double lon = (double)coordinates[0]!;
        double lat = (double)coordinates[1]!;
        LatLon moved = new(lat + (northFt / FeetPerDegLat), lon + (eastFt / (FeetPerDegLat * Math.Cos(lat * Math.PI / 180.0))));
        feature["geometry"]!["coordinates"] = new JsonArray(moved.Lon, moved.Lat);
    }

    /// <summary>Reflects every <c>[lon, lat]</c> under a GeoJSON coordinates node across the meridian at <paramref name="mirrorLon"/>.</summary>
    private static void ReflectCoordinates(JsonNode? node, double mirrorLon)
    {
        if (node is not JsonArray array)
        {
            return;
        }

        if ((array.Count >= 2) && (array[0] is JsonValue))
        {
            array[0] = (2.0 * mirrorLon) - (double)array[0]!;
            return;
        }

        foreach (JsonNode? child in array)
        {
            ReflectCoordinates(child, mirrorLon);
        }
    }

    private static void AssertSameEstimates(AirportGroundLayout expected, AirportGroundLayout actual, Func<GroundNode, string> key)
    {
        Dictionary<string, List<double>> before = Estimates(expected, key);
        Dictionary<string, List<double>> after = Estimates(actual, key);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach ((string stand, List<double> spans) in before)
        {
            double expectedSpan = Assert.Single(spans.Distinct());
            Assert.All(after[stand], s => Assert.Equal(expectedSpan, s, 1e-6));
        }
    }

    private static Dictionary<string, List<double>> Estimates(AirportGroundLayout layout, Func<GroundNode, string> key) =>
        Parking(layout).GroupBy(key).ToDictionary(g => g.Key, g => g.Select(n => Span(layout, n)).ToList());

    private static string Key(string? name, double lat) => $"{name}@{lat.ToString("F7", CultureInfo.InvariantCulture)}";

    private static IEnumerable<GroundNode> Parking(AirportGroundLayout layout) => layout.Nodes.Values.Where(n => n.Type == GroundNodeType.Parking);

    private static GroundNode Stand(AirportGroundLayout layout, string name) => Assert.Single(Parking(layout), n => n.Name == name);

    /// <summary>
    /// The airport ceiling as the taxiway design groups take it: the group of the widest runway with a recorded width, VI
    /// when none records one.
    /// </summary>
    private static AirplaneDesignGroup Ceiling(AirportGroundLayout layout)
    {
        double widestFt = layout.Runways.Select(r => r.WidthFt).DefaultIfEmpty(0.0).Max();
        return widestFt > 0.0 ? AirplaneDesignGroups.FromRunwayWidth(widestFt) : AirplaneDesignGroup.VI;
    }

    private static double Span(AirportGroundLayout layout, GroundNode stand) => StandFit.MaxWingspanFt(layout, stand, Ceiling(layout), null);

    private static double SfoSpan(string name) => Span(Sfo.Value, Stand(Sfo.Value, name));

    private static double DerivedSpan(string name) => Span(Derived.Value, Stand(Derived.Value, name));

    /// <summary>The group a span falls in, a span at a group's ceiling taking all of that group (79 ft is group II).</summary>
    private static AirplaneDesignGroup GroupOf(double spanFt) =>
        Enum.GetValues<AirplaneDesignGroup>().First(g => spanFt <= AirplaneDesignGroups.MaxWingspanFt(g));
}
