using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.ContextMenus;

/// <summary>Which way a hold's turns go: right (the standard pattern, opening first) or left.</summary>
public enum HoldTurns
{
    /// <summary>Right turns: <c>R</c> in a <c>HOLDP</c>, <c>HFIXR</c> for 360s at a fix.</summary>
    Right,

    /// <summary>Left turns: <c>L</c> in a <c>HOLDP</c>, <c>HFIXL</c> for 360s at a fix.</summary>
    Left,
}

/// <summary>The leg of a holding pattern the Hold at fix flyout offers as chips.</summary>
public enum HoldLeg
{
    /// <summary>A 1-minute inbound leg, the standard at or below 14,000 ft MSL (<c>1M</c>).</summary>
    OneMinute,

    /// <summary>A 1½-minute inbound leg, the standard above 14,000 ft MSL (<c>1.5M</c>).</summary>
    OneAndHalfMinutes,

    /// <summary>A 5-mile leg (<c>5</c>).</summary>
    FiveMiles,

    /// <summary>A 10-mile leg (<c>10</c>).</summary>
    TenMiles,
}

/// <summary>
/// The two hold flyouts. Hold at fix… (<c>HOLDP</c>) is the Cross fix flyout's fix list (<see cref="FixValueFlyoutPanel"/>) with a
/// holding-pattern form under the fix box: the Inbound course box (empty sends the default course, <see cref="DefaultCourse"/>),
/// beside it the direction of holding and, at a VOR, the radial; the Legs chips (opening on the altitude standard,
/// <see cref="StandardLeg"/>, and following it until one is picked); and the Right / Left turns toggle. Every row shows and sends the
/// whole four-token <c>HOLDP SUNOL 184 1M R</c>, and the caption says it as a controller does (7110.65 4-6-4). The fix box takes a
/// fix's own name only, since the <c>HOLDP</c> parser resolves no fix-radial-distance. Left 360s at fix… and Right 360s at fix… open
/// the 360s flyout (<see cref="HoldTurnsFlyoutPanel"/>): the same fix list under a Left 360s / Right 360s toggle, sending <c>HFIXL SUNOL</c> or
/// <c>HFIXR SUNOL</c>; its fix box also takes a fix-radial-distance.
/// </summary>
public static class HoldFlyout
{
    /// <summary>The 360s flyout's fix box name, by which a test or UI Automation finds it.</summary>
    public const string TurnsBoxName = "HoldTurnsFixBox";

    /// <summary>The Hold at fix flyout's Inbound course box name.</summary>
    public const string CourseBoxName = "HoldCourseBox";

    /// <summary>The refusal under the Inbound course box while it is empty and the fix has no default course.</summary>
    public const string CourseRequired = "Course required";

    /// <summary>The refusal under the Inbound course box for text that is no course from 001 to 360.</summary>
    public const string InvalidCourse = "Not a valid course";

    /// <summary>What the empty Inbound course box reads while the fix box names no fix with a default course.</summary>
    public const string CoursePlaceholder = "Default course";

    /// <summary>The highest altitude, in feet MSL, at which the standard leg is 1 minute; above it, 1½ minutes (AIM 5-3-8).</summary>
    public const double OneMinuteLegCeilingFeet = 14000;

    private static readonly string[] CompassPoints = ["north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest"];

    /// <summary>The Hold at fix entry <paramref name="label"/> names: one menu row holding the holding-pattern flyout.</summary>
    public static MenuItem BuildPattern(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(entry, new HoldPatternFlyoutHost(), context, host, (choose, _) => new HoldPatternFlyoutPanel(aircraft, choose));
        return entry;
    }

    /// <summary>
    /// The 360s-at-fix entry <paramref name="label"/> names: one menu row holding the 360s flyout opening on
    /// <paramref name="preset"/>.
    /// </summary>
    public static MenuItem BuildTurns(string label, HoldTurns preset, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(entry, new HoldTurnsFlyoutHost(), context, host, (choose, _) => new HoldTurnsFlyoutPanel(preset, aircraft, choose));
        return entry;
    }

    /// <summary>The leg's <c>HOLDP</c> token: <c>1M</c>, <c>1.5M</c>, <c>5</c> or <c>10</c>.</summary>
    public static string LegToken(HoldLeg leg) =>
        leg switch
        {
            HoldLeg.OneMinute => "1M",
            HoldLeg.OneAndHalfMinutes => "1.5M",
            HoldLeg.FiveMiles => "5",
            HoldLeg.TenMiles => "10",
            _ => throw new ArgumentOutOfRangeException(nameof(leg), leg, "Unknown hold leg."),
        };

    /// <summary>The leg's chip: <c>1 min</c>, <c>1½ min</c>, <c>5 NM</c> or <c>10 NM</c>.</summary>
    public static string LegLabel(HoldLeg leg) =>
        leg switch
        {
            HoldLeg.OneMinute => "1 min",
            HoldLeg.OneAndHalfMinutes => "1½ min",
            HoldLeg.FiveMiles => "5 NM",
            HoldLeg.TenMiles => "10 NM",
            _ => throw new ArgumentOutOfRangeException(nameof(leg), leg, "Unknown hold leg."),
        };

    /// <summary>The leg as the caption says it: <c>1½-minute leg</c>, <c>5-mile leg</c>.</summary>
    public static string LegWords(HoldLeg leg) =>
        leg switch
        {
            HoldLeg.OneMinute => "1-minute leg",
            HoldLeg.OneAndHalfMinutes => "1½-minute leg",
            HoldLeg.FiveMiles => "5-mile leg",
            HoldLeg.TenMiles => "10-mile leg",
            _ => throw new ArgumentOutOfRangeException(nameof(leg), leg, "Unknown hold leg."),
        };

    /// <summary>The turns' <c>HOLDP</c> token: <c>R</c> or <c>L</c>.</summary>
    public static string TurnsToken(HoldTurns turns) =>
        turns switch
        {
            HoldTurns.Right => "R",
            HoldTurns.Left => "L",
            _ => throw new ArgumentOutOfRangeException(nameof(turns), turns, "Unknown hold turns."),
        };

    /// <summary>The turns' toggle label in the Hold at fix flyout: <c>Right</c> or <c>Left</c>.</summary>
    public static string TurnsLabel(HoldTurns turns) =>
        turns switch
        {
            HoldTurns.Right => "Right",
            HoldTurns.Left => "Left",
            _ => throw new ArgumentOutOfRangeException(nameof(turns), turns, "Unknown hold turns."),
        };

    /// <summary>The 360s verb: <c>HFIXR</c> or <c>HFIXL</c>.</summary>
    public static string TurnsVerb(HoldTurns turns) =>
        turns switch
        {
            HoldTurns.Right => "HFIXR",
            HoldTurns.Left => "HFIXL",
            _ => throw new ArgumentOutOfRangeException(nameof(turns), turns, "Unknown hold turns."),
        };

    /// <summary>The 360s toggle label: <c>Right 360s</c> or <c>Left 360s</c>.</summary>
    public static string TurnsToggleLabel(HoldTurns turns) => $"{TurnsLabel(turns)} 360s";

    /// <summary>The 360s flyout's title: <c>Right 360s at fix</c> or <c>Left 360s at fix</c>.</summary>
    public static string TurnsTitle(HoldTurns turns) => $"{TurnsToggleLabel(turns)} at fix";

    /// <summary>
    /// The standard leg for the aircraft's altitude (AIM 5-3-8): 1 minute at or below <see cref="OneMinuteLegCeilingFeet"/>, 1½
    /// minutes above it, reading the assigned altitude, else the present one; 1 minute without an aircraft.
    /// </summary>
    public static HoldLeg StandardLeg(IMenuAircraft? aircraft)
    {
        double feet = aircraft?.AssignedAltitude ?? aircraft?.AltitudeFeet ?? 0;
        return (feet <= OneMinuteLegCeilingFeet) ? HoldLeg.OneMinute : HoldLeg.OneAndHalfMinutes;
    }

    /// <summary>The <c>HOLDP</c> command, always four tokens: <c>HOLDP SUNOL 184 1M R</c>, the course in three digits, north 360.</summary>
    public static string PatternCommand(string fix, int course, HoldLeg leg, HoldTurns turns) =>
        $"HOLDP {fix} {Three(course)} {LegToken(leg)} {TurnsToken(turns)}";

    /// <summary>The direction of holding from the fix: the outbound course (inbound + 180°) as one of eight compass points.</summary>
    public static string HoldingSide(int inboundCourse)
    {
        int outbound = (inboundCourse + 180) % 360;
        return CompassPoints[(int)Math.Round(outbound / 45.0, MidpointRounding.AwayFromZero) % CompassPoints.Length];
    }

    /// <summary>The radial a hold on <paramref name="inboundCourse"/> lies on: its reciprocal, 001 to 360.</summary>
    public static int Radial(int inboundCourse) => HeadingFlyout.WholeHeading(inboundCourse + 180);

    /// <summary>Whether <paramref name="fix"/> is a VOR, VORTAC or TACAN, whose hold the caption names by its radial.</summary>
    public static bool IsRadialNavaid(string fix) => NavigationDatabase.InstanceOrNull?.IsRadialNavaid(fix) ?? false;

    /// <summary>What sits beside the Inbound course box: the direction of holding, and at a VOR the radial (<c>north · 004 radial</c>).</summary>
    public static string SideText(string fix, int course) =>
        IsRadialNavaid(fix) ? $"{HoldingSide(course)} · {Three(Radial(course))} radial" : HoldingSide(course);

    /// <summary>
    /// The hold as a controller says it (7110.65 4-6-4): <c>Hold north of SPAMY on the 184 course inbound</c>, at a VOR
    /// <c>Hold north of RBL on the 004 radial</c>, then <paramref name="legWords"/> when given and <c>, left turns</c> when left.
    /// </summary>
    public static string PatternCaption(string fix, int course, string? legWords, HoldTurns turns)
    {
        string on = IsRadialNavaid(fix) ? $"on the {Three(Radial(course))} radial" : $"on the {Three(course)} course inbound";
        string leg = (legWords is null) ? "" : $", {legWords}";
        string left = (turns == HoldTurns.Left) ? ", left turns" : "";
        return $"Hold {HoldingSide(course)} of {fix} {on}{leg}{left}";
    }

    /// <summary>
    /// The course Hold at fix sends for <paramref name="fix"/> with the course box empty, magnetic, 001 to 360: when the fix
    /// lies further down the aircraft's live navigation route, the course of the route leg into it from the fix before it;
    /// otherwise (the next fix, a fix already flown, the filed route's tail, an airport, a fix off the route) the course from the
    /// aircraft. Null without the navigation data, a position for the fix, or a place to start from.
    /// </summary>
    public static int? DefaultCourse(IMenuAircraft? aircraft, string fix) =>
        (LegStart(aircraft, fix) is { } start) ? MagneticCourse(start, fix) : null;

    /// <summary>
    /// The magnetic course arriving at <paramref name="fix"/> from <paramref name="from"/> (the reciprocal of the great-circle
    /// bearing from the fix back to <paramref name="from"/>), 001 to 360, made magnetic by the declination the sim flies a hold
    /// there by (<see cref="NavigationDatabase.GetPublishedCourseDeclination"/>: a navaid's station declination, else the
    /// modelled one at the fix), never the aircraft's.
    /// </summary>
    public static int? MagneticCourse(LatLon from, string fix)
    {
        if ((NavigationDatabase.InstanceOrNull is not { } db) || (db.GetFixPosition(fix) is not { } position))
        {
            return null;
        }

        var fixPosition = new LatLon(position.Lat, position.Lon);
        double arriving = (GeoMath.BearingTo(fixPosition, from) + 180) % 360;
        double declination = db.GetPublishedCourseDeclination(fix, string.Empty, fixPosition);
        return HeadingFlyout.WholeHeading(new TrueHeading(arriving).ToMagnetic(declination).Degrees);
    }

    /// <summary>A course or radial in three digits.</summary>
    public static string Three(int degrees) => degrees.ToString("D3", CultureInfo.InvariantCulture);

    /// <summary>
    /// Where the leg into <paramref name="fix"/> starts: the fix before it on the live navigation route (whose first fix is the
    /// next one), else the aircraft.
    /// </summary>
    private static LatLon? LegStart(IMenuAircraft? aircraft, string fix)
    {
        IReadOnlyList<string> route = aircraft?.NavigationRoute ?? [];
        int index = IndexOf(route, fix);
        return (index > 0) ? FixList.Position(route[index - 1]) ?? aircraft?.Position : aircraft?.Position;
    }

    private static int IndexOf(IReadOnlyList<string> route, string fix)
    {
        for (int i = 0; i < route.Count; i++)
        {
            if (string.Equals(route[i], fix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>The Hold at fix flyout's menu row header, which holds the <see cref="HoldPatternFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class HoldPatternFlyoutHost() : FlyoutPanelHost("hold at fix flyout", "hold-at-fix-flyout-panel");

/// <summary>The 360s flyout's menu row header, which holds the <see cref="HoldTurnsFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class HoldTurnsFlyoutHost() : FlyoutPanelHost("360s at fix flyout", "hold-turns-flyout-panel");

/// <summary>
/// The Hold at fix flyout's panel (<see cref="HoldFlyout"/>): under the fix box the Inbound course box with the direction of
/// holding beside it, the Legs chips and the Turns toggle. Each refresh (<see cref="OnRefreshed"/>) also re-reads the default
/// courses and, until a leg chip is picked, the altitude standard.
/// </summary>
public sealed class HoldPatternFlyoutPanel : FixValueFlyoutPanel
{
    private readonly IMenuAircraft? _aircraft;
    private readonly List<ToggleButton> _legToggles = [];
    private readonly List<ToggleButton> _turnToggles = [];
    private readonly TextBlock _side = new();

    internal HoldPatternFlyoutPanel(IMenuAircraft? aircraft, Func<string, Task> choose)
        : base("Hold at fix", "HOLDP", MenuIds.HoldPattern, aircraft, choose)
    {
        _aircraft = aircraft;
        foreach (HoldLeg leg in Enum.GetValues<HoldLeg>())
        {
            _legToggles.Add(
                FlyoutPanelParts.VerbToggle(HoldFlyout.LegLabel(leg), HoldFlyout.LegToken(leg), leg == HoldLeg.OneMinute, () => PickLeg(leg))
            );
        }

        foreach (HoldTurns turns in Enum.GetValues<HoldTurns>())
        {
            _turnToggles.Add(
                FlyoutPanelParts.VerbToggle(
                    HoldFlyout.TurnsLabel(turns),
                    HoldFlyout.TurnsToken(turns),
                    turns == HoldTurns.Right,
                    () => SetTurns(turns)
                )
            );
        }

        CourseBox = ValueBox(HoldFlyout.CourseBoxName, HoldFlyout.CoursePlaceholder, "Inbound course", 110);
        QuickCommandGlyph courseGlyph = QuickCommandGlyphs.For(MenuIds.HeadingFly) ?? throw new InvalidOperationException("No heading glyph.");
        Compose([
            FlyoutPanelParts.BoxRow(courseGlyph, "Inbound course (empty: default)", CourseBox, _side),
            CourseError,
            LabelledToggles("Legs", _legToggles),
            LabelledToggles("Turns", _turnToggles),
        ]);
    }

    /// <summary>The Inbound course box: empty sends each fix's default course; a typed course, 001 to 360, overrides it.</summary>
    public TextBox CourseBox { get; }

    /// <summary>The Inbound course box's refusal: <see cref="HoldFlyout.CourseRequired"/> or <see cref="HoldFlyout.InvalidCourse"/>.</summary>
    public TextBlock CourseError { get; } = Refusal(HoldFlyout.CourseRequired);

    /// <summary>The direction of holding (and at a VOR the radial) for the fix box's fix and course; empty while there is none.</summary>
    public string SideText => _side.Text ?? "";

    /// <summary>The leg the commands carry now.</summary>
    public HoldLeg Leg { get; private set; }

    /// <summary>Whether a leg chip was picked since the flyout opened; until then the leg follows the altitude standard.</summary>
    public bool LegPicked { get; private set; }

    /// <summary>The turns the commands carry now, which the toggle sets.</summary>
    public HoldTurns Turns { get; private set; }

    /// <summary>The chip of <paramref name="leg"/>.</summary>
    public ToggleButton LegChip(HoldLeg leg) => _legToggles[(int)leg];

    /// <summary>The toggle button of <paramref name="turns"/>.</summary>
    public ToggleButton TurnsToggle(HoldTurns turns) => _turnToggles[(int)turns];

    /// <summary>The course <paramref name="fix"/>'s command carries: the typed course, else its default; null while there is none.</summary>
    public int? CourseFor(string fix)
    {
        string text = CourseBox.Text ?? "";
        return string.IsNullOrWhiteSpace(text) ? HoldFlyout.DefaultCourse(_aircraft, fix) : FixValueFlyout.ParseHeading(text);
    }

    /// <inheritdoc />
    public override string? CommandFor(string fix) => (CourseFor(fix) is { } course) ? HoldFlyout.PatternCommand(fix, course, Leg, Turns) : null;

    /// <inheritdoc />
    protected override IReadOnlyList<TextBox> ValueBoxes() => [CourseBox];

    /// <inheritdoc />
    protected override IEnumerable<string> ValueLines()
    {
        yield return (SideText.Length > 0) ? $"Inbound course [{CourseBox.Text}] — {SideText}" : $"Inbound course [{CourseBox.Text}]";
        if (CourseError.IsVisible)
        {
            yield return CourseError.Text ?? "";
        }

        yield return "Legs " + string.Join(" | ", Enum.GetValues<HoldLeg>().Select(leg => Marked(HoldFlyout.LegLabel(leg), leg == Leg)));
        yield return "Turns " + string.Join(" | ", Enum.GetValues<HoldTurns>().Select(turns => Marked(HoldFlyout.TurnsLabel(turns), turns == Turns)));
    }

    /// <inheritdoc />
    protected override string? CaptionFor(string fix)
    {
        bool sayLeg = LegPicked || (Leg != HoldFlyout.StandardLeg(_aircraft));
        return (CourseFor(fix) is { } course) ? HoldFlyout.PatternCaption(fix, course, sayLeg ? HoldFlyout.LegWords(Leg) : null, Turns) : null;
    }

    /// <inheritdoc />
    protected override TextBox? FirstMissingBox() => ((BoxFix is { } fix) && (CourseFor(fix) is null)) ? CourseBox : null;

    /// <inheritdoc />
    protected override void ResetValues()
    {
        CourseBox.Text = "";
        LegPicked = false;
        ApplyLeg(HoldFlyout.StandardLeg(_aircraft));
        SetTurns(HoldTurns.Right);
    }

    /// <summary>
    /// The live beat, after the list follows the aircraft: puts the leg on the altitude standard while no chip was picked, and
    /// shows every default course anew.
    /// </summary>
    protected override void OnRefreshed()
    {
        if (!LegPicked)
        {
            ApplyLeg(HoldFlyout.StandardLeg(_aircraft));
        }

        ShowValues();
    }

    /// <inheritdoc />
    protected override void ShowRefusals()
    {
        string text = CourseBox.Text ?? "";
        string? fix = BoxFix;
        int? defaultCourse = (fix is null) ? null : HoldFlyout.DefaultCourse(_aircraft, fix);
        CourseBox.PlaceholderText = (defaultCourse is { } shown) ? HoldFlyout.Three(shown) : HoldFlyout.CoursePlaceholder;
        CourseError.Text = string.IsNullOrWhiteSpace(text) ? HoldFlyout.CourseRequired : HoldFlyout.InvalidCourse;
        CourseError.IsVisible = CourseRefused(text, fix, defaultCourse);
        _side.Text = ((fix is not null) && (CourseFor(fix) is { } course)) ? HoldFlyout.SideText(fix, course) : "";
    }

    private static string Marked(string text, bool on) => on ? $"[{text}]" : text;

    private static StackPanel LabelledToggles(string label, IEnumerable<ToggleButton> toggles)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        row.Children.Add(
            new TextBlock
            {
                Text = label,
                Width = 44,
                Margin = new Thickness(11, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        row.Children.Add(FlyoutPanelParts.ToggleStrip(toggles));
        return row;
    }

    /// <summary>
    /// Whether the course box shows its refusal: typed text that is no course, or (once a send found it missing) an empty box
    /// for a fix without a default course.
    /// </summary>
    private bool CourseRefused(string text, string? fix, int? defaultCourse)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            return FixValueFlyout.ParseHeading(text) is null;
        }

        return RequiredShown && (fix is not null) && (defaultCourse is null);
    }

    private void PickLeg(HoldLeg leg)
    {
        LegPicked = true;
        ApplyLeg(leg);
        ShowValues();
    }

    private void ApplyLeg(HoldLeg leg)
    {
        Leg = leg;
        foreach (HoldLeg each in Enum.GetValues<HoldLeg>())
        {
            LegChip(each).IsChecked = (each == leg);
        }
    }

    private void SetTurns(HoldTurns turns)
    {
        Turns = turns;
        foreach (HoldTurns each in Enum.GetValues<HoldTurns>())
        {
            TurnsToggle(each).IsChecked = (each == turns);
        }

        ShowValues();
    }
}

/// <summary>
/// The 360s flyout (<see cref="HoldFlyout"/>): the title line (<c>Left 360s at fix</c> / <c>Right 360s at fix</c>, following the
/// toggle), the Left 360s / Right 360s toggle, the fix box (a fix or a fix-radial-distance) with the command Enter sends, a
/// separator, then the fix list: the route rows and, as you type, the typed fix-radial-distance's own row and every other matching
/// fix, nearest first, each sending <c>HFIXL SUNOL</c> or <c>HFIXR SUNOL</c>.
/// </summary>
public sealed class HoldTurnsFlyoutPanel : StackPanel, IFlyoutPanel
{
    private static readonly HoldTurns[] Sides = [HoldTurns.Left, HoldTurns.Right];

    private readonly HoldTurns _preset;
    private readonly IMenuAircraft? _aircraft;
    private readonly Func<string, Task> _choose;
    private readonly TextBlock _title = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly TextBlock _titleNow = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly Dictionary<HoldTurns, ToggleButton> _toggles = [];
    private readonly FixListView _fixes;
    private readonly Grid _boxRow;

    internal HoldTurnsFlyoutPanel(HoldTurns preset, IMenuAircraft? aircraft, Func<string, Task> choose)
    {
        _preset = preset;
        _aircraft = aircraft;
        _choose = choose;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        Children.Add(FlyoutPanelParts.TitleLine(_title, _titleNow));
        foreach (HoldTurns side in Sides)
        {
            _toggles[side] = FlyoutPanelParts.VerbToggle(
                HoldFlyout.TurnsToggleLabel(side),
                HoldFlyout.TurnsVerb(side),
                side == Sides[0],
                () => SetSide(side)
            );
        }

        Children.Add(FlyoutPanelParts.ToggleStrip(Sides.Select(side => _toggles[side])));
        Box = new TextBox
        {
            Name = HoldFlyout.TurnsBoxName,
            PlaceholderText = DirectToFlyout.BoxPlaceholder,
            Width = 190,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(Box, "Fix");
        TakeoffFlyout.KeepClicksInside(Box);
        _fixes = new FixListView(
            aircraft,
            Box,
            "Route · next ●",
            new FixListBehaviour(
                RowCommand,
                FixList.FrdRow,
                SendRowAsync,
                e => ReferenceEquals(e.Source, Box) || Box.IsKeyboardFocusWithin,
                SendBoxAsync
            )
        );
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnTyped();
            }
        };
        _boxRow = FlyoutPanelParts.BoxRow(SideGlyph(preset), "Fix", Box, BoxCommandText);
        Children.Add(_boxRow);
        Children.Add(BoxError);
        Children.Add(FlyoutPanelParts.SeparatorLine());
        Children.Add(_fixes.View);
        AddHandler(KeyDownEvent, _fixes.OnKeyDown, RoutingStrategies.Tunnel);
        SetSide(preset);
        Refresh();
    }

    /// <summary>The side the 360s turn to now, which the toggle sets.</summary>
    public HoldTurns Side { get; private set; }

    /// <summary>The glyph the fix box row draws: the racetrack whose arrow turns to <see cref="Side"/>.</summary>
    public QuickCommandGlyph? BoxGlyph { get; private set; }

    /// <summary>The title line's left part: <c>Left 360s at fix</c> or <c>Right 360s at fix</c>.</summary>
    public string Title => _title.Text ?? "";

    /// <summary>The fix box.</summary>
    public TextBox Box { get; }

    /// <summary>The command column beside the box: what Enter in it sends, or the verb with <c>···</c>, faded, while it cannot send.</summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>The refusal under the box (<see cref="DirectToFlyout.UnknownFix"/>), shown while the box names no fix.</summary>
    public TextBlock BoxError { get; } =
        new()
        {
            Text = DirectToFlyout.UnknownFix,
            FontSize = 11,
            Foreground = FlyoutPanelParts.RefusalBrush,
            Margin = new Thickness(33, 0, 11, 2),
            IsVisible = false,
        };

    /// <summary>
    /// The command Enter in the box sends now: the outlined row's, else the typed fix or fix-radial-distance's when the navigation
    /// data knows it; null while it cannot send.
    /// </summary>
    public string? BoxCommand => OutlinedRow?.SentCommand ?? TypedCommand(Box.Text ?? "");

    /// <summary>The route's rows as the flyout opened, in route order; they never move while it stays open.</summary>
    public IReadOnlyList<FixRow> RouteRows => _fixes.RouteRows;

    /// <summary>The rows the box's text adds: a typed fix-radial-distance's own row first, then every other matching fix, nearest first.</summary>
    public IReadOnlyList<FixRow> MatchRows => _fixes.MatchRows;

    /// <summary>The row Enter in the box sends, outlined: the first shown row while the box holds text; null otherwise.</summary>
    public FixRow? OutlinedRow => _fixes.OutlinedRow;

    /// <summary>
    /// The panel as text, one line per part: the title line, the toggle (the side on in brackets), the box with its command, the
    /// refusal while shown, <c>---</c>, then each shown group header and row of the list.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            List<string> lines = [string.IsNullOrEmpty(_titleNow.Text) ? Title : $"{Title} · {_titleNow.Text}"];
            lines.Add(string.Join(" | ", Sides.Select(ToggleText)));
            lines.Add($"Fix — {BoxCommandText.Text}");
            if (BoxError.IsVisible)
            {
                lines.Add(BoxError.Text ?? "");
            }

            lines.Add("---");
            lines.AddRange(_fixes.Lines);
            return lines;
        }
    }

    Control IFlyoutPanel.View => this;

    void IFlyoutPanel.Reset() => Reset();

    void IFlyoutPanel.Refresh() => Refresh();

    void IFlyoutPanel.FocusOpeningLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(Box, isOpen, () => { });

    /// <summary>The toggle button of <paramref name="side"/>.</summary>
    public ToggleButton Toggle(HoldTurns side) => _toggles[side];

    /// <summary>Puts the flyout back as it opens: the toggle on the preset's side, the box empty, the route rows read afresh.</summary>
    public void Reset()
    {
        _fixes.Reset((_aircraft is null) ? [] : _aircraft.RouteFixNames());
        Box.Text = "";
        SetSide(_preset);
        OnTyped();
    }

    /// <summary>
    /// Follows the aircraft, in place: the title's next fix, each row's distance and bearing, the ● and the fixes passed since the
    /// flyout opened (<see cref="FixListView.Refresh"/>).
    /// </summary>
    public void Refresh()
    {
        string next = _aircraft?.NavigatingTo ?? "";
        _titleNow.Text = (next.Length > 0) ? $"→ {next}" : "";
        _fixes.Refresh();
    }

    /// <summary>The point menu's racetrack of <paramref name="side"/>, its arrow showing the direction of the turns.</summary>
    private static QuickCommandGlyph SideGlyph(HoldTurns side) =>
        QuickCommandGlyphs.ForPoint((side == HoldTurns.Left) ? MenuIds.PointHoldLeft : MenuIds.PointHoldRight);

    /// <summary>The command for the typed fix or fix-radial-distance (<see cref="FixList.TypedFixOrFrd"/>); null otherwise.</summary>
    private string? TypedCommand(string text) => (FixList.TypedFixOrFrd(text) is { } fix) ? $"{HoldFlyout.TurnsVerb(Side)} {fix}" : null;

    private (string? Sent, string Shown) RowCommand(string fix)
    {
        string command = $"{HoldFlyout.TurnsVerb(Side)} {fix}";
        return (command, command);
    }

    private Task SendRowAsync(FixRow row) => (row.SentCommand is { } command) ? _choose(command) : Task.CompletedTask;

    private Task SendBoxAsync() => (BoxCommand is { } command) ? _choose(command) : Task.CompletedTask;

    private string ToggleText(HoldTurns side)
    {
        string text = $"{HoldFlyout.TurnsToggleLabel(side)} {HoldFlyout.TurnsVerb(side)}";
        return (side == Side) ? $"[{text}]" : text;
    }

    private void SetSide(HoldTurns side)
    {
        Side = side;
        foreach (HoldTurns each in Sides)
        {
            Toggle(each).IsChecked = (each == side);
        }

        _title.Text = HoldFlyout.TurnsTitle(side);
        BoxGlyph = SideGlyph(side);
        FlyoutPanelParts.SetBoxRowGlyph(_boxRow, BoxGlyph);
        _fixes.UpdateCommands();
        ShowBoxCommand();
    }

    private void ShowBoxCommand()
    {
        string? command = BoxCommand;
        BoxCommandText.Text = command ?? $"{HoldFlyout.TurnsVerb(Side)} {TakeoffFlyout.Unfilled}";
        BoxCommandText.Opacity = (command is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        BoxError.IsVisible = (command is null) && !string.IsNullOrWhiteSpace(Box.Text);
        MenuCommandText.SetCommand(Box, command);
    }

    /// <summary>Shows what the box now reads in the list (<see cref="FixListView.Filter"/>), then the box's command.</summary>
    private void OnTyped()
    {
        _fixes.Filter();
        ShowBoxCommand();
    }
}
