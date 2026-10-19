using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Sim.Situation;

namespace Yaat.Client.Tests;

/// <summary>
/// Guards the default quick-command lists and the glyph table against the catalog: every classified situation has a
/// list, every listed and glyph-bearing identifier is a catalog action, every default entry has a glyph, and no list
/// repeats an action or names a display item.
/// </summary>
public class QuickCommandDefaultsTests
{
    private const string MockRect = "M3.5 9h12a2 2 0 0 1 2 2v2a2 2 0 0 1 -2 2h-12a2 2 0 0 1 -2 -2v-2a2 2 0 0 1 2 -2z";

    private const string MockRunwayArrowLeft = "M11 7.4 L9.4 9 L11 10.6";

    private const string MockRunwayArrowRight = "M11 13.4 L9.4 15 L11 16.6";

    private const string ClearedApproachPath = "M3 12L21 5 M3 12L21 19 M3 12h18";

    private const string JoinApproachPath = "M3 12h18 M20 20.5C18 15 15.5 12 11.5 12 M9.5 9l-3 3 3 3";

    private const string StraightInPath = "M3 7v10 M21 12H7 M10 9l-3 3 3 3";

    private const string JoinFinalCoursePath = "M2 20h9 M21 10H5 M8 7l-3 3 3 3";

    public static TheoryData<AircraftSituation> ClassifiedSituations() =>
        [.. Enum.GetValues<AircraftSituation>().Where(situation => situation != AircraftSituation.Unknown)];

    private static readonly HashSet<string> CatalogIds = [.. MenuCatalog.All.Select(entry => entry.Id)];

    [Theory]
    [MemberData(nameof(ClassifiedSituations))]
    public void EveryClassifiedSituation_HasANonEmptyList(AircraftSituation situation) => Assert.NotEmpty(QuickCommandDefaults.For(situation));

    [Fact]
    public void Unknown_HasNoList()
    {
        Assert.Empty(QuickCommandDefaults.For(AircraftSituation.Unknown));
        Assert.False(QuickCommandDefaults.Lists.ContainsKey(AircraftSituation.Unknown));
    }

    /// <summary>The catalog ids <paramref name="situation"/>'s default list names; every default entry is a catalog entry.</summary>
    private static IEnumerable<string> Ids(AircraftSituation situation) =>
        QuickCommandDefaults.For(situation).Select(entry => Assert.IsType<CatalogQuickCommandEntry>(entry).CatalogId);

    [Theory]
    [MemberData(nameof(ClassifiedSituations))]
    public void EveryListedId_IsACatalogAction(AircraftSituation situation)
    {
        List<string> missing = [.. Ids(situation).Where(id => !CatalogIds.Contains(id))];
        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(ClassifiedSituations))]
    public void NoList_NamesADisplayItem(AircraftSituation situation)
    {
        List<string> display = [.. Ids(situation).Where(id => id.StartsWith("display.", StringComparison.Ordinal))];
        Assert.Empty(display);
    }

    [Theory]
    [MemberData(nameof(ClassifiedSituations))]
    public void NoList_RepeatsAnId(AircraftSituation situation)
    {
        List<string> repeated = [.. Ids(situation).GroupBy(id => id).Where(group => group.Count() > 1).Select(group => group.Key)];
        Assert.Empty(repeated);
    }

    [Fact]
    public void EveryGlyphKey_IsACatalogAction()
    {
        List<string> missing = [.. QuickCommandGlyphs.ById.Keys.Where(id => !CatalogIds.Contains(id))];
        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(ClassifiedSituations))]
    public void EveryDefaultQuickCommand_HasAGlyph(AircraftSituation situation)
    {
        List<string> missing = [.. Ids(situation).Where(id => !QuickCommandGlyphs.ById.ContainsKey(id))];
        Assert.True(missing.Count == 0, $"{situation} default quick commands with no glyph: {string.Join(", ", missing)}");
    }

    [AvaloniaFact]
    public void EveryGlyph_ParsesAsGeometry()
    {
        foreach ((string id, QuickCommandGlyph glyph) in QuickCommandGlyphs.ById)
        {
            Exception? error = Record.Exception(() => Geometry.Parse(glyph.PathData));
            Assert.True(error is null, $"{id}'s glyph path does not parse: {error?.Message}");
        }
    }

    [Theory]
    [InlineData(MenuIds.PatternEnterLeftDownwind, MenuIds.PatternEnterRightDownwind)]
    [InlineData(MenuIds.PatternEnterLeftBase, MenuIds.PatternEnterRightBase)]
    public void LeftAndRightPatternEntry_HaveDifferentGlyphs(string left, string right) =>
        Assert.NotEqual(QuickCommandGlyphs.For(left)?.PathData, QuickCommandGlyphs.For(right)?.PathData);

    /// <summary>
    /// The pattern-entry glyphs are the approved mock's (<c>docs/plans/canvases/pattern-glyph-rotation</c>) paths drawn
    /// landing west: its rounded rectangle, the side's runway arrowhead and the entry's arrow, joined by a space.
    /// </summary>
    [Theory]
    [InlineData(MenuIds.PatternEnterLeftDownwind, MockRunwayArrowLeft, "M6.35 20.65 L10.15 16.85 M10.15 16.85 h-3 M10.15 16.85 v3")]
    [InlineData(MenuIds.PatternEnterRightDownwind, MockRunwayArrowRight, "M6.35 3.35 L10.15 7.15 M10.15 7.15 h-3 M10.15 7.15 v-3")]
    [InlineData(MenuIds.PatternEnterLeftBase, MockRunwayArrowLeft, "M17.5 23 V16.6 M17.5 16.6 l-2.2 2.2 M17.5 16.6 l2.2 2.2")]
    [InlineData(MenuIds.PatternEnterRightBase, MockRunwayArrowRight, "M17.5 1 V7.4 M17.5 7.4 l-2.2 -2.2 M17.5 7.4 l2.2 -2.2")]
    [InlineData(MenuIds.PatternEnterFinal, MockRunwayArrowLeft, "M23.5 9 H19.1 M19.1 9 l2.2 -2.2 M19.1 9 l2.2 2.2")]
    public void PatternEntryGlyphs_MatchTheApprovedMock(string id, string runwayArrow, string entryArrow) =>
        Assert.Equal($"{MockRect} {runwayArrow} {entryArrow}", QuickCommandGlyphs.For(id)?.PathData);

    [AvaloniaFact]
    public void EveryPointGlyph_ParsesAsGeometry()
    {
        foreach (QuickCommandGlyph glyph in QuickCommandGlyphs.PointById.Values)
        {
            Assert.NotNull(Geometry.Parse(glyph.PathData));
        }
    }

    [Fact]
    public void NoPointGlyphKey_IsAQuickCommandGlyphKey()
    {
        List<string> shared = [.. QuickCommandGlyphs.PointById.Keys.Where(QuickCommandGlyphs.ById.ContainsKey)];
        Assert.Empty(shared);
    }

    [Fact]
    public void ForPoint_ANonPointId_Throws() => Assert.Throws<ArgumentException>(() => QuickCommandGlyphs.ForPoint(MenuIds.GroundHoldPosition));

    /// <summary>Air taxi sits where the mock puts it: first where nothing outranks it, after the runway-bound entries where they do.</summary>
    [Theory]
    [InlineData(AircraftSituation.AtParking, 0)]
    [InlineData(AircraftSituation.HoldingOnGround, 0)]
    [InlineData(AircraftSituation.HoldingShort, 3)]
    [InlineData(AircraftSituation.RolloutExit, 2)]
    public void AirTaxi_IsListedInTheFourGroundSituations(AircraftSituation situation, int index) =>
        Assert.Equal(index, Ids(situation).ToList().IndexOf(MenuIds.GroundAirTaxi));

    public static TheoryData<AircraftSituation> AirborneSituations() =>
        [
            AircraftSituation.Taxiing,
            AircraftSituation.Departing,
            AircraftSituation.IfrEnroute,
            AircraftSituation.IfrArrival,
            AircraftSituation.VfrFlightFollowing,
            AircraftSituation.Approach,
            AircraftSituation.Holding,
            AircraftSituation.Pattern,
            AircraftSituation.Final,
            AircraftSituation.GoAround,
            AircraftSituation.VfrArrivalInbound,
            AircraftSituation.VfrDeparting,
        ];

    /// <summary>Land closes every list an airborne helicopter can be in, the air-taxiing one (Taxiing) included.</summary>
    [Theory]
    [MemberData(nameof(AirborneSituations))]
    public void LandHelicopter_ClosesEveryAirborneSituationsList(AircraftSituation situation) =>
        Assert.Equal(MenuIds.GroundLandHelicopter, Ids(situation).Last());

    [Theory]
    [InlineData(AircraftSituation.AtParking)]
    [InlineData(AircraftSituation.PushingBack)]
    [InlineData(AircraftSituation.LinedUp)]
    [InlineData(AircraftSituation.LiveTraffic)]
    public void NoGroundOrShadowList_CarriesLand(AircraftSituation situation) => Assert.DoesNotContain(MenuIds.GroundLandHelicopter, Ids(situation));

    /// <summary>The glyphs are the mocks' paths: the Air taxi glyph, and Land's with the down arrow in place of the side arrow.</summary>
    [Fact]
    public void HelicopterGlyphs_MatchTheMocks()
    {
        const string Helicopter = "M3 4.5h14 M10 4.5v3 M5.5 11.5a4.5 3.5 0 1 0 9 0a4.5 3.5 0 1 0-9 0 M8 15v3 M12 15v3 M5.5 18h9";
        Assert.Equal($"{Helicopter} M16 11.5h5.5 M19 9l2.5 2.5L19 14", QuickCommandGlyphs.For(MenuIds.GroundAirTaxi)?.PathData);
        Assert.Equal($"{Helicopter} M19.5 8v10 M17 15.5l2.5 2.5 2.5-2.5", QuickCommandGlyphs.For(MenuIds.GroundLandHelicopter)?.PathData);
        Assert.Equal(QuickCommandGlyphFamily.Ground, QuickCommandGlyphs.For(MenuIds.GroundAirTaxi)?.Family);
        Assert.Equal(QuickCommandGlyphFamily.Ground, QuickCommandGlyphs.For(MenuIds.GroundLandHelicopter)?.Family);
    }

    /// <summary>
    /// The approach verbs' glyphs are the approved mock's (<c>docs/plans/canvases/yaat-569-x3b-verbs</c>, lane B): one per
    /// family, flight blue, the force pair their base glyph with the force mark.
    /// </summary>
    [Theory]
    [InlineData(MenuIds.ApproachCleared, ClearedApproachPath, false)]
    [InlineData(MenuIds.ApproachJoin, JoinApproachPath, false)]
    [InlineData(MenuIds.ApproachClearedStraightIn, StraightInPath, false)]
    [InlineData(MenuIds.ApproachJoinStraightIn, StraightInPath, false)]
    [InlineData(MenuIds.ApproachClearedForce, ClearedApproachPath, true)]
    [InlineData(MenuIds.ApproachJoinForce, JoinApproachPath, true)]
    [InlineData(MenuIds.ApproachJoinFinalCourse, JoinFinalCoursePath, false)]
    public void ApproachVerbGlyphs_MatchTheApprovedMock(string id, string path, bool forceMark)
    {
        QuickCommandGlyph glyph = Assert.IsType<QuickCommandGlyph>(QuickCommandGlyphs.For(id));
        Assert.Equal(path, glyph.PathData);
        Assert.Equal(QuickCommandGlyphFamily.Flight, glyph.Family);
        Assert.Equal(forceMark, glyph.HasForceMark);
    }

    /// <summary>A force-marked glyph draws its path, then the amber disc and the "!" over its lower right; another draws its path alone.</summary>
    [AvaloniaFact]
    public void ForceMarkedGlyph_DrawsTheAmberDiscOverItsPath()
    {
        Canvas forced = Assert.IsType<Canvas>(QuickCommandStrip.GlyphIcon(QuickCommandGlyphs.For(MenuIds.ApproachClearedForce)!).Child);
        Canvas plain = Assert.IsType<Canvas>(QuickCommandStrip.GlyphIcon(QuickCommandGlyphs.For(MenuIds.ApproachCleared)!).Child);

        Assert.Single(plain.Children);
        Assert.Equal(3, forced.Children.Count);
        Ellipse disc = Assert.IsType<Ellipse>(forced.Children[1]);
        Assert.Equal(Color.Parse("#FFB259"), Assert.IsAssignableFrom<ISolidColorBrush>(disc.Fill).Color);
        Assert.IsType<Avalonia.Controls.Shapes.Path>(forced.Children[2]);
    }
}
