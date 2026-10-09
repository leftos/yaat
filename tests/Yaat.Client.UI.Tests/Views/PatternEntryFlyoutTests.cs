using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Phases;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The pattern entries' runway flyout: one row per runway end at the destination (else the departure airport), the room's
/// arrival and both-way active ends first, a separator, then the rest; within each group the ends the type can land on
/// first and the short ones last, dimmed with a "short" note and still clickable. Each row's glyph is the entry's
/// lands-west glyph rotated by the end's true course less 270°, and Make straight-in's is mirrored onto the right-traffic
/// side when that is the end's default pattern side. The strip shows the assigned runway's rotated glyph.
/// </summary>
public class PatternEntryFlyoutTests
{
    private const string Callsign = "N123AB";
    private const string Initials = "AB";
    private const string Oak = "KOAK";

    private static MenuContext Context() => TestMenuContext.Create(Callsign, Initials, null, false, VfrCommandsForIfr.All);

    private static AircraftModel Arrival(string type, string assignedRunway) =>
        new()
        {
            Callsign = Callsign,
            IsOnGround = false,
            CurrentPhase = "",
            FlightRules = "VFR",
            AircraftType = type,
            FiledAircraftType = type,
            Destination = Oak,
            AssignedRunway = assignedRunway,
        };

    private static RecordingMenuHost Host(params string[] oakTokens) =>
        new("") { RoomActiveRunways = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["OAK"] = oakTokens } };

    private static MenuItem Flyout(string entryId, AircraftModel aircraft, RecordingMenuHost host)
    {
        TestVnasData.EnsureInitialized();
        MenuItem? item = MenuCatalog.Get(entryId).Build(aircraft, Context(), host);
        Assert.NotNull(item);
        Assert.NotEmpty(item.Items);
        return item;
    }

    private static MenuGlyphRow Row(MenuItem flyout, string runway) =>
        flyout.Items.OfType<MenuItem>().Select(item => Assert.IsType<MenuGlyphRow>(item.Header)).Single(row => row.Label == $"Runway {runway}");

    private static MenuItem RowItem(MenuItem flyout, string runway) =>
        flyout.Items.OfType<MenuItem>().Single(item => (item.Header as MenuGlyphRow)?.Label == $"Runway {runway}");

    /// <summary>Each item as a short line: "---" for the separator, else the runway and "short" when it is one.</summary>
    private static List<string> Outline(MenuItem flyout) =>
        [
            .. flyout.Items.Select(item =>
                item switch
                {
                    Separator => "---",
                    MenuItem { Header: MenuGlyphRow row } => (row.Note is { } note) ? $"{row.Label} {note}" : row.Label,
                    _ => item?.GetType().Name ?? "null",
                }
            ),
        ];

    private static double TrueCourse(string runway) =>
        (NavigationDatabase.Instance.GetRunway(Oak, runway) ?? throw new InvalidOperationException($"No KOAK {runway}")).TrueHeading.Degrees;

    [AvaloniaFact]
    public void Flyout_ActiveArrivalEndsFirst_ThenASeparator_ThenTheRest_LandableBeforeShortInEachGroup()
    {
        AircraftModel b738 = Arrival("B738", "");
        RecordingMenuHost host = Host("A28R", "30");

        MenuItem flyout = Flyout(MenuIds.PatternEnterLeftDownwind, b738, host);
        List<string> lines = Outline(flyout);

        Assert.Equal("Enter left downwind", flyout.Header as string);
        Assert.Null(flyout.Tag);
        // A B738 needs 6,036 ft. Active: 30 lands, 28R (5,457 ft declared) is short and comes last. Inactive, each part in
        // runway order: 10R, 12 and 28L land; 10L (5,336 ft) and 15/33 (3,376 ft) are short.
        Assert.Equal(
            [
                "Runway 30",
                "Runway 28R short",
                "---",
                "Runway 10R",
                "Runway 12",
                "Runway 28L",
                "Runway 10L short",
                "Runway 15 short",
                "Runway 33 short",
            ],
            lines
        );

        MenuGlyphRow short28R = Row(flyout, "28R");
        Assert.True(short28R.IsDimmed);
        Assert.Equal("ELD 28R", short28R.Command);
        Assert.True(MenuGlyphRowTemplate.Instance.Build(short28R)!.Opacity < 1);
        Assert.False(Row(flyout, "30").IsDimmed);
        Assert.Equal(1, MenuGlyphRowTemplate.Instance.Build(Row(flyout, "30"))!.Opacity);

        RowItem(flyout, "28R").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal([(Callsign, "ELD 28R", Initials)], host.Sent);
    }

    /// <summary>
    /// An A388 filed as a B744: the sim lands the A388, so the flyout judges the physical type. The A388 has no profile and
    /// so no landing distance, which finds every KOAK end landable; judged as the filed B744 (6,988 ft, needing 8,036 ft)
    /// most of them would be short.
    /// </summary>
    [AvaloniaFact]
    public void Flyout_JudgesThePhysicalType_NotTheFiledOne()
    {
        AircraftModel a388FiledAsB744 = Arrival("B744", "");
        a388FiledAsB744.AircraftType = "A388";

        MenuItem flyout = Flyout(MenuIds.PatternEnterLeftDownwind, a388FiledAsB744, Host());

        Assert.Equal(["Runway 10L", "Runway 10R", "Runway 12", "Runway 15", "Runway 28L", "Runway 28R", "Runway 30", "Runway 33"], Outline(flyout));
    }

    /// <summary>
    /// A C172 filed as a B744: the C172 needs 603 ft and lands on every KOAK end, where the filed B744 (needing 8,036 ft)
    /// would mark 10L, 28R, 15 and 33 short among others. The flyout judges the C172, so no row is short.
    /// </summary>
    [AvaloniaFact]
    public void Flyout_C172FiledAsB744_IsJudgedAsTheC172()
    {
        AircraftModel c172FiledAsB744 = Arrival("B744", "");
        c172FiledAsB744.AircraftType = "C172";

        MenuItem flyout = Flyout(MenuIds.PatternEnterLeftDownwind, c172FiledAsB744, Host());

        Assert.Equal(["Runway 10L", "Runway 10R", "Runway 12", "Runway 15", "Runway 28L", "Runway 28R", "Runway 30", "Runway 33"], Outline(flyout));
    }

    [AvaloniaFact]
    public void Flyout_OtherCompanion_ListsTheSameOrderedRotatedRows()
    {
        RecordingMenuHost host = Host("A28R", "30");
        MenuItem flyout = Flyout(MenuIds.PatternEnterLeftBase, Arrival("B738", ""), host);

        MenuItem? other = MenuCatalog.BuildPatternEntryOther(MenuIds.PatternEnterLeftBase, Arrival("B738", "30"), Context(), host);

        Assert.NotNull(other);
        Assert.Equal("Enter left base (other)", other.Header as string);
        Assert.Equal(Outline(flyout), Outline(other));
        Assert.Equal(Row(flyout, "28R"), Row(other, "28R"));
    }

    [AvaloniaFact]
    public void Flyout_RowGlyph_IsTheEntryGlyphRotatedByTheEndsTrueCourseLess270()
    {
        MenuItem flyout = Flyout(MenuIds.PatternEnterLeftDownwind, Arrival("C172", ""), Host());
        QuickCommandGlyph landsWest = QuickCommandGlyphs.For(MenuIds.PatternEnterLeftDownwind)!;

        MenuGlyphRow row = Row(flyout, "28R");

        Assert.Equal(TrueCourse("28R") - 270, row.Glyph.RotationDegrees, 6);
        Assert.Equal(landsWest.PathData, row.Glyph.PathData);
        Assert.False(row.Glyph.MirroredTopToBottom);
        Assert.Equal(0, landsWest.RotationDegrees);

        Canvas canvas = Assert.IsType<Canvas>(QuickCommandStrip.GlyphIcon(row.Glyph).Child);
        Assert.Equal(RelativePoint.TopLeft, canvas.RenderTransformOrigin);
        RotateTransform rotation = Assert.IsType<RotateTransform>(canvas.RenderTransform);
        Assert.Equal(row.Glyph.RotationDegrees, rotation.Angle, 6);
        Assert.Equal(new Point(12, 12), rotation.Value.Transform(new Point(12, 12)));
    }

    /// <summary>
    /// KOAK 28R's default pattern side is right (28L is its parallel, so its pattern goes outboard): Make straight-in draws
    /// the right-traffic frame there, the left one mirrored top to bottom about the glyph's centre line, which puts the
    /// left-traffic final arrow's tip (19.1, 9) where the mock draws the right-traffic one (19.1, 15). 28L and 30 keep left.
    /// </summary>
    [AvaloniaFact]
    public void MakeStraightIn_RightTrafficDefaultSide_IsMirrored()
    {
        MenuItem flyout = Flyout(MenuIds.PatternEnterFinal, Arrival("C172", ""), Host());

        MenuGlyphRow r28R = Row(flyout, "28R");

        Assert.True(r28R.Glyph.MirroredTopToBottom);
        Assert.False(Row(flyout, "28L").Glyph.MirroredTopToBottom);
        Assert.False(Row(flyout, "30").Glyph.MirroredTopToBottom);
        Assert.Equal(TrueCourse("28R") - 270, r28R.Glyph.RotationDegrees, 6);

        Canvas canvas = Assert.IsType<Canvas>(QuickCommandStrip.GlyphIcon(r28R.Glyph with { RotationDegrees = 0 }).Child);
        Matrix transform = Assert.IsType<TransformGroup>(canvas.RenderTransform).Value;
        Assert.Equal(new Point(19.1, 15), transform.Transform(new Point(19.1, 9)));
        Assert.Equal(new Point(11, 16.6), transform.Transform(new Point(11, 7.4)));

        // Mirrored first, then rotated: at 90° the mirrored tip (19.1, 15) turns clockwise about the centre to (9, 19.1).
        Canvas turned = Assert.IsType<Canvas>(QuickCommandStrip.GlyphIcon(r28R.Glyph with { RotationDegrees = 90 }).Child);
        Point tip = Assert.IsType<TransformGroup>(turned.RenderTransform).Value.Transform(new Point(19.1, 9));
        Assert.Equal(9, tip.X, 6);
        Assert.Equal(19.1, tip.Y, 6);
    }

    [AvaloniaFact]
    public void StripGlyph_IsTheAssignedRunwaysRotatedGlyph_AndTheDefaultWithoutOne()
    {
        TestVnasData.EnsureInitialized();
        QuickCommandEntry[] entries = [new CatalogQuickCommandEntry(MenuIds.PatternEnterLeftDownwind, null)];
        QuickCommandGlyph landsWest = QuickCommandGlyphs.For(MenuIds.PatternEnterLeftDownwind)!;

        QuickCommandGlyph assigned = Assert.Single(QuickCommandResolver.Resolve(entries, Arrival("C172", "28R"), Context(), _ => true).Strip).Glyph;
        QuickCommandGlyph none = Assert.Single(QuickCommandResolver.Resolve(entries, Arrival("C172", ""), Context(), _ => true).Strip).Glyph;
        QuickCommandGlyph unknown = Assert.Single(QuickCommandResolver.Resolve(entries, Arrival("C172", "99"), Context(), _ => true).Strip).Glyph;

        Assert.Equal(landsWest with { RotationDegrees = TrueCourse("28R") - 270 }, assigned);
        Assert.Equal(landsWest, none);
        Assert.Equal(landsWest, unknown);
    }

    [AvaloniaFact]
    public void DepartureOnlyActiveRunway_IsGroupedWithTheInactive()
    {
        MenuItem flyout = Flyout(MenuIds.PatternEnterLeftDownwind, Arrival("C172", ""), Host("D28L"));

        List<string> lines = Outline(flyout);

        Assert.DoesNotContain("---", lines);
        Assert.Equal(["Runway 10L", "Runway 10R", "Runway 12", "Runway 15", "Runway 28L", "Runway 28R", "Runway 30", "Runway 33"], lines);
    }

    /// <summary>
    /// The room names KSFO's 1L zero-padded (<c>A01L</c>) where the row names it as the controller says it ("Runway 1L"):
    /// the two still match, so 1L is grouped as active.
    /// </summary>
    [AvaloniaFact]
    public void ZeroPaddedActiveRunway_IsGroupedAsActive()
    {
        AircraftModel c172 = Arrival("C172", "");
        c172.Destination = "KSFO";
        var host = new RecordingMenuHost("")
        {
            RoomActiveRunways = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["SFO"] = ["A01L"] },
        };

        MenuItem flyout = Flyout(MenuIds.PatternEnterLeftDownwind, c172, host);

        Assert.Equal(
            ["Runway 1L", "---", "Runway 1R", "Runway 10L", "Runway 10R", "Runway 19L", "Runway 19R", "Runway 28L", "Runway 28R"],
            Outline(flyout)
        );
    }
}
