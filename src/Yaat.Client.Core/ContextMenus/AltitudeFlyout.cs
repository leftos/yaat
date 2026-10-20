using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Yaat.Sim;

namespace Yaat.Client.ContextMenus;

/// <summary>Which data-block altitude a one-box altitude flyout sets.</summary>
public enum AltitudeBoxKind
{
    /// <summary><c>TEMPALT</c>: the data block's temporary altitude.</summary>
    TemporaryAltitude,

    /// <summary><c>CRUISE</c>: the flight plan's cruise altitude.</summary>
    Cruise,
}

/// <summary>What an altitude flyout reads from the aircraft on each refresh: its altitude, its assignment and the MVA under it.</summary>
/// <param name="Current">The aircraft's altitude in feet; 0 without an aircraft.</param>
/// <param name="Assigned">The assigned altitude in feet, or null.</param>
/// <param name="Mva">The minimum vectoring altitude sector under the aircraft, or null where none is known.</param>
internal sealed record AltitudeReading(double Current, double? Assigned, (string Sector, int FloorFtMsl)? Mva);

/// <summary>
/// The altitude flyouts. Maintain altitude, one builder for the strip's button and All Commands' entry: the title line (the
/// present and assigned altitudes), the Altitude box, a separator, and every altitude from the destination's field elevation up
/// to the type's ceiling as rows in a <see cref="ListHeight"/> px scrolling list opened with the assigned row (else the current
/// altitude's) centred: each row's mark (● now, ◆ assigned, ↑ climb, ↓ descend), altitude, note and the command it sends
/// (<c>CM 25000</c> / <c>DM 25000</c>), the MVA line between the rows, the rows under it greyed but still sending. The box reads
/// an altitude as <see cref="AltitudeResolver.Resolve"/> does, outlines the nearest row and previews the command. Temporary
/// altitude and Cruise, each its own one-box flyout previewing the <c>TEMPALT</c> / <c>CRUISE</c> command it sends. While open,
/// each refreshes once a second (<see cref="FlyoutLiveTick"/>): the present altitude, the marks, the verbs and the MVA line;
/// the rows are fixed when the flyout is built, and a refresh never touches the box's text or the keyboard focus.
/// </summary>
public static class AltitudeFlyout
{
    /// <summary>The altitude box's name, by which a test or UI Automation finds it.</summary>
    public const string BoxName = "AltitudeBox";

    /// <summary>What the empty Maintain altitude box reads.</summary>
    public const string BoxPlaceholder = "e.g. 230 or 23000";

    /// <summary>What a one-box flyout shows in its command's place for text that is no altitude.</summary>
    public const string InvalidAltitude = "Not a valid altitude";

    /// <summary>The Maintain altitude list's height, in pixels.</summary>
    public const double ListHeight = 300;

    /// <summary>The highest altitude the Maintain list holds, and its cap for a type without a profile.</summary>
    private const int MaintainCapFeet = 60000;

    /// <summary>
    /// The Maintain altitude entry: one menu row holding the flyout over <paramref name="aircraft"/>'s altitudes; null when no
    /// altitude is listed.
    /// </summary>
    /// <param name="label">The entry's label.</param>
    /// <param name="aircraft">The aircraft whose altitude the flyout follows.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the flyout sends through, reads the field elevation and the MVA from, and refreshes on.</param>
    public static MenuItem? BuildMaintain(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        List<int> altitudes = AltitudeValues(host.GetFieldElevation(aircraft?.Destination), AltitudeCeiling(aircraft));
        if (altitudes.Count == 0)
        {
            return null;
        }

        InsertAssignment(altitudes, aircraft?.AssignedAltitude);
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(
            entry,
            new AltitudeFlyoutHost("maintain altitude flyout"),
            context,
            host,
            (choose, _) => new MaintainAltitudePanel(altitudes, () => Read(aircraft, host), choose)
        );
        return entry;
    }

    /// <summary>A data-block altitude entry: one menu row holding the one-box flyout of <paramref name="kind"/>.</summary>
    /// <param name="label">The entry's label.</param>
    /// <param name="kind">Which altitude the box sets.</param>
    /// <param name="aircraft">The aircraft whose present altitude the title line follows.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the flyout sends through and refreshes on.</param>
    public static MenuItem BuildBox(string label, AltitudeBoxKind kind, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(
            entry,
            new AltitudeFlyoutHost((kind == AltitudeBoxKind.Cruise) ? "cruise flyout" : "temporary altitude flyout"),
            context,
            host,
            (choose, close) => new AltitudeBoxPanel(kind, aircraft, choose, close)
        );
        return entry;
    }

    /// <summary>The box verb's text: <c>TEMPALT</c> or <c>CRUISE</c>.</summary>
    internal static string Verb(AltitudeBoxKind kind) => (kind == AltitudeBoxKind.Cruise) ? "CRUISE" : "TEMPALT";

    /// <summary>The one-box flyout's title: <c>Temporary altitude</c> or <c>Cruise</c>.</summary>
    internal static string Title(AltitudeBoxKind kind) => (kind == AltitudeBoxKind.Cruise) ? "Cruise" : "Temporary altitude";

    /// <summary>The command the one-box flyout of <paramref name="kind"/> sends for <paramref name="text"/>; null when it is no altitude.</summary>
    public static string? BoxCommand(AltitudeBoxKind kind, string text) =>
        (kind == AltitudeBoxKind.Cruise) ? MenuCatalog.FormatCruiseAltitude(text) : MenuCatalog.FormatTemporaryAltitude(text);

    /// <summary><paramref name="feet"/> to the nearest 100 ft, as the title line shows the present altitude.</summary>
    internal static string PresentText(double feet) => MenuCatalog.FormatAltitude((int)(Math.Round(feet / 100, MidpointRounding.AwayFromZero) * 100));

    /// <summary>
    /// The Maintain list over the fixed <paramref name="altitudes"/> (highest first) as <paramref name="reading"/> finds the
    /// aircraft: one row per altitude, marked ● for the row nearest the current altitude, ◆ on the assigned altitude's row when
    /// it is listed, ↑ above the current altitude and ↓ below it, and, when the MVA is known, the MVA line between the lowest row
    /// at or above its floor and the highest row below it, with every row under the floor greyed. Titled <c>Maintain altitude</c>
    /// over <c>now {current} · assigned {assigned}</c>; selects the assigned row, else the ● row.
    /// </summary>
    internal static MenuRichList MaintainList(IReadOnlyList<int> altitudes, AltitudeReading reading)
    {
        int? assignedIndex = (reading.Assigned is { } feet) ? IndexOrNull(altitudes, (int)Math.Round(feet)) : null;
        int nowIndex = NearestIndex(altitudes, reading.Current);
        var rows = new List<MenuRichRow>(altitudes.Count + 1);
        for (int i = 0; i < altitudes.Count; i++)
        {
            MenuRichRowKind kind = MaintainKind(i == nowIndex, i == assignedIndex, altitudes[i], reading.Current);
            rows.Add(
                MaintainRow(altitudes[i], kind, MaintainCommand(altitudes[i], kind, reading.Current, reading.Assigned), reading.Mva?.FloorFtMsl)
            );
        }

        int selected = assignedIndex ?? nowIndex;
        if (reading.Mva is { } sector)
        {
            selected = InsertMvaLine(rows, altitudes, sector, selected);
        }

        return new MenuRichList("Maintain altitude", MaintainSubtitle(reading.Current, reading.Assigned), rows, selected);
    }

    /// <summary>The index of the altitude nearest <paramref name="target"/>; of two equally near, the higher (listed first).</summary>
    internal static int NearestIndex(IReadOnlyList<int> altitudes, double target)
    {
        int best = 0;
        for (int i = 1; i < altitudes.Count; i++)
        {
            if (Math.Abs(altitudes[i] - target) < Math.Abs(altitudes[best] - target))
            {
                best = i;
            }
        }

        return best;
    }

    private static AltitudeReading Read(IMenuAircraft? aircraft, IMenuHost host) =>
        new(aircraft?.AltitudeFeet ?? 0, aircraft?.AssignedAltitude, (aircraft is not null) ? host.GetMva(aircraft.Position) : null);

    private static int? IndexOrNull(IReadOnlyList<int> altitudes, int altitude)
    {
        for (int i = 0; i < altitudes.Count; i++)
        {
            if (altitudes[i] == altitude)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// The type's service ceiling (<see cref="AircraftPerformance.Ceiling"/>) capped at 60,000 ft; 60,000 ft for a type
    /// without a profile.
    /// </summary>
    private static int AltitudeCeiling(IMenuAircraft? aircraft)
    {
        string type = aircraft?.DisplayAircraftType ?? "";
        double? ceiling = (type.Length > 0) ? AircraftPerformance.Ceiling(type) : null;
        return (ceiling is { } feet) ? (int)Math.Min(feet, MaintainCapFeet) : MaintainCapFeet;
    }

    /// <summary>
    /// Adds the assigned altitude to <paramref name="altitudes"/> (highest first) in its sorted place when it is off the steps or
    /// above the ceiling; nothing without an assignment.
    /// </summary>
    private static void InsertAssignment(List<int> altitudes, double? assigned)
    {
        if (assigned is not { } feet)
        {
            return;
        }

        int altitude = (int)Math.Round(feet);
        int index = altitudes.FindIndex(listed => listed <= altitude);
        if (index < 0)
        {
            altitudes.Add(altitude);
        }
        else if (altitudes[index] != altitude)
        {
            altitudes.Insert(index, altitude);
        }
    }

    /// <summary>
    /// Inserts the MVA line before the first row under <paramref name="sector"/>'s floor (at the end when none is) and
    /// returns <paramref name="selected"/> moved past it when it was at or after the line.
    /// </summary>
    private static int InsertMvaLine(List<MenuRichRow> rows, IReadOnlyList<int> altitudes, (string Sector, int FloorFtMsl) sector, int selected)
    {
        int lineIndex = altitudes.Count;
        for (int i = 0; i < altitudes.Count; i++)
        {
            if (altitudes[i] < sector.FloorFtMsl)
            {
                lineIndex = i;
                break;
            }
        }

        string floor = sector.FloorFtMsl.ToString("N0", CultureInfo.InvariantCulture);
        rows.Insert(lineIndex, new MenuRichRow("", $"MVA {floor} here (sector {sector.Sector})", "", MenuRichRowKind.MvaLine, null, []));
        return (selected >= lineIndex) ? selected + 1 : selected;
    }

    /// <summary>
    /// <c>now {current} · assigned {assigned}</c>, the current altitude to the nearest 100 ft; without the assignment when there
    /// is none.
    /// </summary>
    private static string MaintainSubtitle(double current, double? assigned)
    {
        string now = $"now {PresentText(current)}";
        return (assigned is { } feet) ? $"{now} · assigned {MenuCatalog.FormatAltitude((int)Math.Round(feet))}" : now;
    }

    /// <summary>
    /// A row's mark before the MVA greys it: ● for the current row, ◆ for the assigned one, else ↑ above the current
    /// altitude and ↓ below.
    /// </summary>
    private static MenuRichRowKind MaintainKind(bool isNow, bool isAssigned, int altitude, double current) =>
        (isNow, isAssigned) switch
        {
            (true, true) => MenuRichRowKind.NowAssigned,
            (true, false) => MenuRichRowKind.Now,
            (false, true) => MenuRichRowKind.Assigned,
            _ => (altitude > current) ? MenuRichRowKind.Climb : MenuRichRowKind.Descend,
        };

    /// <summary>
    /// The command a row sends: the ● row climbs unless the assignment is below the current altitude; every other row
    /// climbs above the current altitude and descends otherwise. The value is bare feet (<c>CM 35000</c>).
    /// </summary>
    private static string MaintainCommand(int altitude, MenuRichRowKind kind, double current, double? assigned)
    {
        bool climb = (kind is MenuRichRowKind.Now or MenuRichRowKind.NowAssigned) ? !(assigned < current) : (altitude > current);
        return climb ? $"CM {altitude}" : $"DM {altitude}";
    }

    /// <summary>
    /// One altitude row with its glyph and hint: the ● and ◆ rows name their mark (<c>now</c>, <c>assigned</c>,
    /// <c>now · assigned</c>), the ↑ and ↓ rows their command. Every row under <paramref name="mvaFloor"/> is greyed
    /// (<see cref="MenuRichRowKind.BelowMva"/>), keeps its glyph, ends its hint with <c>below MVA</c> in place of the
    /// command, and still sends <paramref name="command"/>.
    /// </summary>
    private static MenuRichRow MaintainRow(int altitude, MenuRichRowKind kind, string command, int? mvaFloor)
    {
        (string glyph, string mark) = MaintainMark(kind);
        string label = MenuCatalog.FormatAltitude(altitude);
        if (altitude < mvaFloor)
        {
            string hint = (mark.Length > 0) ? $"{mark} · below MVA" : "below MVA";
            return new MenuRichRow(glyph, label, hint, MenuRichRowKind.BelowMva, command, []);
        }

        return new MenuRichRow(glyph, label, (mark.Length > 0) ? mark : command, kind, command, []);
    }

    /// <summary>A row's glyph and, for the ● and ◆ rows, the mark its hint names; the ↑ and ↓ rows have none.</summary>
    private static (string Glyph, string Mark) MaintainMark(MenuRichRowKind kind) =>
        kind switch
        {
            MenuRichRowKind.Now => ("●", "now"),
            MenuRichRowKind.NowAssigned => ("●", "now · assigned"),
            MenuRichRowKind.Assigned => ("◆", "assigned"),
            MenuRichRowKind.Climb => ("↑", ""),
            _ => ("↓", ""),
        };

    /// <summary>
    /// Every 100 ft from the field up to 5,000 ft above it, then every 500 ft, up to <paramref name="ceiling"/>; highest first.
    /// </summary>
    private static List<int> AltitudeValues(double fieldElevation, int ceiling)
    {
        var items = new List<int>();
        int lowThreshold = (int)(fieldElevation + 5000);

        int roundedLow = (int)(Math.Ceiling(fieldElevation / 100.0) * 100);
        if (roundedLow < 100)
        {
            roundedLow = 100;
        }

        for (int altitude = roundedLow; (altitude < lowThreshold) && (altitude <= ceiling); altitude += 100)
        {
            items.Add(altitude);
        }

        int start500 = (int)(Math.Ceiling(lowThreshold / 500.0) * 500);
        for (int altitude = start500; altitude <= ceiling; altitude += 500)
        {
            items.Add(altitude);
        }

        items.Reverse();
        return items;
    }
}

/// <summary>An altitude flyout's menu row header, which holds its panel once the flyout first opens.</summary>
/// <param name="outline">The header as one line, for a menu outline, the same before and after the first opening.</param>
public sealed class AltitudeFlyoutHost(string outline) : FlyoutPanelHost(outline, "altitude-flyout-panel");

/// <summary>The altitude flyouts' shared looks.</summary>
internal static class AltitudePanelParts
{
    public static readonly IImmutableSolidColorBrush ValueBrush = new ImmutableSolidColorBrush(Color.Parse("#E4E7EB"));
    public static readonly IImmutableSolidColorBrush GreyedBrush = new ImmutableSolidColorBrush(Color.Parse("#7D838B"));

    /// <summary>A box row: the glyph, the <c>Altitude</c> label with <paramref name="box"/> under it, and <paramref name="command"/> right.</summary>
    public static Grid BoxRow(QuickCommandGlyph glyph, TextBox box, TextBlock command)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(11, 4) };
        Viewbox icon = QuickCommandStrip.GlyphIcon(glyph);
        icon.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(icon);
        var name = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Altitude" },
                box,
            },
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        command.FontSize = 12;
        command.Margin = new Thickness(16, 0, 0, 0);
        command.HorizontalAlignment = HorizontalAlignment.Right;
        command.VerticalAlignment = VerticalAlignment.Center;
        FlyoutPanelParts.Mono(command);
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);
        return grid;
    }

    /// <summary>An altitude box named <see cref="AltitudeFlyout.BoxName"/>, whose clicks stay in it.</summary>
    public static TextBox Box(string placeholder, double width)
    {
        var box = new TextBox
        {
            Name = AltitudeFlyout.BoxName,
            PlaceholderText = placeholder,
            Width = width,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(box, "Altitude");
        TakeoffFlyout.KeepClicksInside(box);
        return box;
    }

    /// <summary>Maintain altitude's climbing step glyph.</summary>
    public static QuickCommandGlyph MaintainGlyph() =>
        QuickCommandGlyphs.For(MenuIds.AltitudeMaintain) ?? throw new InvalidOperationException($"No glyph for {MenuIds.AltitudeMaintain}.");
}

/// <summary>
/// The Maintain altitude flyout's one menu row (<see cref="AltitudeFlyout"/>): the title line, the Altitude box, a separator,
/// and the list of altitude rows with the MVA line among them, <see cref="AltitudeFlyout.ListHeight"/> px tall at most.
/// </summary>
public sealed class MaintainAltitudePanel : StackPanel, IFlyoutPanel
{
    private static readonly IImmutableSolidColorBrush ClimbBrush = new ImmutableSolidColorBrush(Color.Parse("#7FD1B9"));
    private static readonly IImmutableSolidColorBrush DescendBrush = new ImmutableSolidColorBrush(Color.Parse("#F2B880"));
    private static readonly IImmutableSolidColorBrush PlainGlyphBrush = new ImmutableSolidColorBrush(Color.Parse("#CCCCCC"));
    private static readonly IImmutableSolidColorBrush OutlineBrush = new ImmutableSolidColorBrush(Color.Parse("#8FC1FF"));
    private static readonly IImmutableSolidColorBrush MvaBrush = new ImmutableSolidColorBrush(Color.Parse("#E0A84A"));
    private static readonly IImmutableSolidColorBrush HoverFill = new ImmutableSolidColorBrush(Color.Parse("#16FFFFFF"));
    private static readonly IImmutableSolidColorBrush PressedFill = new ImmutableSolidColorBrush(Color.Parse("#24FFFFFF"));

    private readonly List<int> _altitudes;
    private readonly Func<AltitudeReading> _read;
    private readonly Func<string, Task> _choose;
    private readonly List<Button> _rows = [];
    private AltitudeReading _reading;
    private readonly List<RowParts> _parts = [];
    private readonly StackPanel _list = new();
    private readonly TextBlock _title = new()
    {
        Text = "Maintain altitude",
        FontSize = 11,
        Foreground = FlyoutPanelParts.TitleBrush,
    };
    private readonly TextBlock _titleNow = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly TextBlock _mvaText = new() { FontSize = 11, Foreground = MvaBrush };
    private int _mvaAt = -1;

    internal MaintainAltitudePanel(List<int> altitudes, Func<AltitudeReading> read, Func<string, Task> choose)
    {
        _altitudes = altitudes;
        _read = read;
        _choose = choose;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        MinWidth = 300;
        Children.Add(FlyoutPanelParts.TitleLine(_title, _titleNow));
        Box = AltitudePanelParts.Box(AltitudeFlyout.BoxPlaceholder, 150);
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnBoxChanged();
            }
        };
        Children.Add(AltitudePanelParts.BoxRow(AltitudePanelParts.MaintainGlyph(), Box, BoxCommandText));
        Children.Add(FlyoutPanelParts.SeparatorLine());
        MvaLine = new StackPanel
        {
            Margin = new Thickness(11, 3, 12, 3),
            Spacing = 2,
            Focusable = false,
            IsVisible = false,
            Children =
            {
                new Rectangle
                {
                    Height = 1,
                    Stroke = MvaBrush,
                    StrokeThickness = 1,
                    StrokeDashArray = [4, 3],
                },
                _mvaText,
            },
        };
        foreach (int altitude in altitudes)
        {
            (Button row, RowParts parts) = RowButton();
            AutomationProperties.SetName(row, MenuCatalog.FormatAltitude(altitude));
            _rows.Add(row);
            _parts.Add(parts);
            _list.Children.Add(row);
        }

        Scroll = new ScrollViewer
        {
            Content = _list,
            MaxHeight = AltitudeFlyout.ListHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 4),
        };
        AutomationProperties.SetName(Scroll, "Altitudes");
        Children.Add(Scroll);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _reading = _read();
        List = AltitudeFlyout.MaintainList(_altitudes, _reading);
        Present();
    }

    /// <summary>The Altitude box.</summary>
    public TextBox Box { get; }

    /// <summary>The command column beside the box: what Enter in it sends, or <c>CM ···</c>, faded, while it cannot send.</summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>The scrolling list of rows.</summary>
    public ScrollViewer Scroll { get; }

    /// <summary>The MVA line, a dashed rule over its text among the rows; hidden where no MVA is known.</summary>
    public Control MvaLine { get; }

    /// <summary>The altitude rows, highest first, one per <see cref="Altitudes"/> entry; fixed for the panel's life.</summary>
    public IReadOnlyList<Button> Rows => _rows;

    /// <summary>The rows' altitudes in feet, highest first.</summary>
    public IReadOnlyList<int> Altitudes => _altitudes;

    /// <summary>The list as the last refresh found the aircraft: its title, subtitle, rows (the MVA line among them) and selected row.</summary>
    public MenuRichList List { get; private set; }

    /// <summary>The altitude of the row the box outlines; null while the box holds no altitude.</summary>
    public int? OutlinedAltitude { get; private set; }

    /// <summary>The command the box sends now; null while it holds no altitude.</summary>
    public string? BoxCommand { get; private set; }

    /// <summary>The row of <paramref name="altitude"/>.</summary>
    public Button RowFor(int altitude)
    {
        int index = _altitudes.IndexOf(altitude);
        return (index >= 0) ? _rows[index] : throw new ArgumentOutOfRangeException(nameof(altitude), altitude, "No row lists this altitude.");
    }

    Control IFlyoutPanel.View => this;

    void IFlyoutPanel.Reset() => Box.Text = "";

    void IFlyoutPanel.Refresh()
    {
        _reading = _read();
        List = AltitudeFlyout.MaintainList(_altitudes, _reading);
        Present();
        ShowBoxCommand();
    }

    void IFlyoutPanel.FocusOpeningLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(Box, isOpen, CentreSelected);

    /// <summary>The row the list's entry at <paramref name="listIndex"/> stands for, the MVA line skipped; null for the line.</summary>
    private int? AltitudeIndex(int listIndex)
    {
        if (List.Rows[listIndex].Kind == MenuRichRowKind.MvaLine)
        {
            return null;
        }

        return ((_mvaAt >= 0) && (listIndex > _mvaAt)) ? listIndex - 1 : listIndex;
    }

    /// <summary>The list's row for the altitude row at <paramref name="index"/>.</summary>
    private MenuRichRow RowAt(int index) => List.Rows[((_mvaAt >= 0) && (index >= _mvaAt)) ? index + 1 : index];

    /// <summary>Shows <see cref="List"/> in place: every row's mark, note and command, the title, and the MVA line between its rows.</summary>
    private void Present()
    {
        int line = -1;
        for (int i = 0; i < List.Rows.Count; i++)
        {
            if (List.Rows[i].Kind == MenuRichRowKind.MvaLine)
            {
                line = i;
                _mvaText.Text = List.Rows[i].Label;
            }
        }

        PlaceMvaLine(line);
        for (int i = 0; i < _rows.Count; i++)
        {
            ShowRow(i, RowAt(i));
        }

        _titleNow.Text = List.Subtitle;
    }

    /// <summary>
    /// Moves the MVA line to sit before the altitude row at <paramref name="at"/>, or hides it for -1. The rows keep their order,
    /// and the list scrolls by whatever the line's move shifts the topmost shown row, so the shown rows stay where they are on
    /// screen.
    /// </summary>
    private void PlaceMvaLine(int at)
    {
        if (at == _mvaAt)
        {
            return;
        }

        Button? anchor = TopShownRow();
        double before = anchor?.Bounds.Y ?? 0;
        _list.Children.Remove(MvaLine);
        _mvaAt = at;
        MvaLine.IsVisible = at >= 0;
        if (at >= 0)
        {
            _list.Children.Insert(at, MvaLine);
        }

        if (anchor is null)
        {
            return;
        }

        Scroll.UpdateLayout();
        Scroll.Offset = new Vector(Scroll.Offset.X, Scroll.Offset.Y + (anchor.Bounds.Y - before));
    }

    /// <summary>The topmost row the list shows, at least in part; null before the list is laid out.</summary>
    private Button? TopShownRow() => (Scroll.Viewport.Height > 0) ? _rows.FirstOrDefault(row => row.Bounds.Bottom > Scroll.Offset.Y) : null;

    private void ShowRow(int index, MenuRichRow row)
    {
        RowParts parts = _parts[index];
        bool greyed = row.Kind == MenuRichRowKind.BelowMva;
        bool now = row.Kind is MenuRichRowKind.Now or MenuRichRowKind.NowAssigned;
        parts.Glyph.Text = row.Glyph;
        parts.Glyph.Foreground = GlyphBrush(row.Kind);
        parts.Value.Text = row.Label;
        parts.Value.Foreground = greyed ? AltitudePanelParts.GreyedBrush : AltitudePanelParts.ValueBrush;
        parts.Value.FontWeight = now ? FontWeight.SemiBold : FontWeight.Normal;
        string note = (row.Hint == row.Command) ? "" : row.Hint;
        parts.Note.Text = note;
        parts.Note.Foreground = greyed ? AltitudePanelParts.GreyedBrush : FlyoutPanelParts.TitleBrush;
        parts.Command.Text = row.Command;
        parts.Fill.Background = now ? FlyoutPanelParts.NowFill : Brushes.Transparent;
        parts.Fill.BorderBrush = now ? FlyoutPanelParts.NowEdge : Brushes.Transparent;
        MenuCommandText.SetCommand(_rows[index], row.Command);
        AutomationProperties.SetName(_rows[index], (note.Length > 0) ? $"{row.Label} · {note} — {row.Command}" : $"{row.Label} — {row.Command}");
    }

    private static IBrush GlyphBrush(MenuRichRowKind kind) =>
        kind switch
        {
            MenuRichRowKind.Climb => ClimbBrush,
            MenuRichRowKind.Descend => DescendBrush,
            MenuRichRowKind.Assigned => FlyoutPanelParts.NowEdge,
            MenuRichRowKind.BelowMva => AltitudePanelParts.GreyedBrush,
            _ => PlainGlyphBrush,
        };

    /// <summary>One altitude row: the mark, the value, the note and the command, in the outline the box draws round its row.</summary>
    private (Button Row, RowParts Parts) RowButton()
    {
        var parts = new RowParts();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,Auto,Auto") };
        parts.Glyph.FontSize = 12;
        parts.Glyph.HorizontalAlignment = HorizontalAlignment.Center;
        parts.Glyph.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(parts.Glyph);
        parts.Value.FontSize = 13;
        parts.Value.VerticalAlignment = VerticalAlignment.Center;
        FlyoutPanelParts.Mono(parts.Value);
        Grid.SetColumn(parts.Value, 1);
        grid.Children.Add(parts.Value);
        parts.Note.FontSize = 11.5;
        parts.Note.Margin = new Thickness(12, 0, 0, 0);
        parts.Note.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(parts.Note, 2);
        grid.Children.Add(parts.Note);
        parts.Command.FontSize = 12;
        parts.Command.Opacity = 0.7;
        parts.Command.Margin = new Thickness(14, 0, 0, 0);
        parts.Command.VerticalAlignment = VerticalAlignment.Center;
        FlyoutPanelParts.Mono(parts.Command);
        Grid.SetColumn(parts.Command, 3);
        grid.Children.Add(parts.Command);
        parts.Fill.Child = grid;
        parts.Outline.Child = parts.Fill;
        var row = new Button
        {
            Content = parts.Outline,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        row.Resources["ButtonBackgroundPointerOver"] = HoverFill;
        row.Resources["ButtonBackgroundPressed"] = PressedFill;
        row.Click += async (_, e) =>
        {
            e.Handled = true;
            await SendRow(row);
        };
        return (row, parts);
    }

    /// <summary>Sends the command <paramref name="row"/> shows now.</summary>
    private Task SendRow(Button row)
    {
        int index = _rows.IndexOf(row);
        return (RowAt(index).Command is { } command) ? _choose(command) : Task.CompletedTask;
    }

    private void OnBoxChanged()
    {
        int? feet = MenuCatalog.ParseAltitudeFeet(Box.Text ?? "");
        int? outlined = (feet is { } typed) ? AltitudeFlyout.NearestIndex(_altitudes, typed) : null;
        for (int i = 0; i < _parts.Count; i++)
        {
            _parts[i].Outline.BorderBrush = (i == outlined) ? OutlineBrush : Brushes.Transparent;
        }

        OutlinedAltitude = (outlined is { } index) ? _altitudes[index] : null;
        if (outlined is { } shown)
        {
            _rows[shown].BringIntoView();
        }

        ShowBoxCommand();
    }

    /// <summary>
    /// The box's command: the outlined row's own command when the box names its altitude, so the ● row keeps its verb; else a
    /// climb above the current altitude and a descent below it, with the feet as typed.
    /// </summary>
    private void ShowBoxCommand()
    {
        int? feet = MenuCatalog.ParseAltitudeFeet(Box.Text ?? "");
        BoxCommand = feet switch
        {
            null => null,
            { } typed when _altitudes.IndexOf(typed) is var at and >= 0 => RowAt(at).Command,
            { } typed => (typed > _reading.Current) ? $"CM {typed}" : $"DM {typed}",
        };
        BoxCommandText.Text = BoxCommand ?? $"CM {TakeoffFlyout.Unfilled}";
        BoxCommandText.Opacity = (BoxCommand is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        MenuCommandText.SetCommand(Box, BoxCommand);
    }

    /// <summary>Scrolls the list so the selected row (the assigned one, else the current altitude's) sits in its middle.</summary>
    private void CentreSelected()
    {
        if (AltitudeIndex(List.SelectedIndex) is not { } index)
        {
            return;
        }

        UpdateLayout();
        Button row = _rows[index];
        if (row.TranslatePoint(default, _list) is not { } top)
        {
            return;
        }

        double wanted = top.Y + (row.Bounds.Height / 2) - (Scroll.Viewport.Height / 2);
        double most = Math.Max(0, Scroll.Extent.Height - Scroll.Viewport.Height);
        Scroll.Offset = new Vector(0, Math.Clamp(wanted, 0, most));
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        int focused = _rows.FindIndex(row => row.IsKeyboardFocusWithin);
        bool inBox = Box.IsKeyboardFocusWithin;
        switch (e.Key)
        {
            case Key.Enter when inBox:
                e.Handled = true;
                if (BoxCommand is { } command)
                {
                    await _choose(command);
                }

                break;
            case Key.Enter when focused >= 0:
                e.Handled = true;
                await SendRow(_rows[focused]);
                break;
            case Key.Down or Key.Up:
                OnWalkKey(e, inBox, focused);
                break;
        }
    }

    /// <summary>Down from the box focuses the ● row; Down and Up walk the rows; Up from the top row returns to the box.</summary>
    private void OnWalkKey(KeyEventArgs e, bool inBox, int focused)
    {
        int? next = (inBox, focused, e.Key) switch
        {
            (true, _, Key.Down) => NowIndex(),
            (false, 0, Key.Up) => -1,
            (false, >= 0, Key.Up) => focused - 1,
            (false, >= 0, Key.Down) => Math.Min(focused + 1, _rows.Count - 1),
            _ => null,
        };
        if (next is not { } target)
        {
            return;
        }

        e.Handled = true;
        if (target < 0)
        {
            Box.Focus(NavigationMethod.Directional);
            return;
        }

        _rows[target].Focus(NavigationMethod.Directional);
        _rows[target].BringIntoView();
    }

    /// <summary>The ● row, the current altitude's, greyed or not.</summary>
    private int NowIndex()
    {
        int index = Enumerable.Range(0, _rows.Count).FirstOrDefault(i => RowAt(i).Glyph == "●", -1);
        return (index >= 0) ? index : 0;
    }

    /// <summary>The parts of one altitude row that each refresh rewrites.</summary>
    private sealed class RowParts
    {
        public TextBlock Glyph { get; } = new();

        public TextBlock Value { get; } = new();

        public TextBlock Note { get; } = new();

        public TextBlock Command { get; } = new();

        public Border Fill { get; } =
            new()
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(5, 1, 9, 1),
                MinHeight = 24,
            };

        public Border Outline { get; } =
            new()
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
            };
    }
}

/// <summary>
/// A one-box altitude flyout's one menu row (<see cref="AltitudeFlyout.BuildBox"/>): the title line naming the entry and the
/// aircraft's present altitude, and the Altitude box with the command it sends beside it, or the refusal where the text is no
/// altitude. Enter sends; Enter in the empty box closes the menu, sending nothing.
/// </summary>
public sealed class AltitudeBoxPanel : StackPanel, IFlyoutPanel
{
    private readonly AltitudeBoxKind _kind;
    private readonly IMenuAircraft? _aircraft;
    private readonly Func<string, Task> _choose;
    private readonly Action _close;
    private readonly TextBlock _titleNow = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };

    internal AltitudeBoxPanel(AltitudeBoxKind kind, IMenuAircraft? aircraft, Func<string, Task> choose, Action close)
    {
        _kind = kind;
        _aircraft = aircraft;
        _choose = choose;
        _close = close;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        var title = new TextBlock
        {
            Text = AltitudeFlyout.Title(kind),
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
        };
        Children.Add(FlyoutPanelParts.TitleLine(title, _titleNow));
        Box = AltitudePanelParts.Box((kind == AltitudeBoxKind.Cruise) ? "e.g. 240" : "e.g. 120", 130);
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                ShowBoxCommand();
            }
        };
        QuickCommandGlyph glyph = AltitudePanelParts.MaintainGlyph() with { Family = QuickCommandGlyphFamily.ScopeAndSim };
        Children.Add(AltitudePanelParts.BoxRow(glyph, Box, BoxCommandText));
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        ShowBoxCommand();
    }

    /// <summary>The Altitude box.</summary>
    public TextBox Box { get; }

    /// <summary>The command column beside the box: what Enter sends, the verb with <c>···</c> while empty, or the refusal.</summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>The command the box sends now; null while it holds no altitude.</summary>
    public string? BoxCommand => AltitudeFlyout.BoxCommand(_kind, Box.Text ?? "");

    /// <summary>The title line as text: <c>Temporary altitude · now FL240</c>.</summary>
    public string Title => (_titleNow.Text is { Length: > 0 } now) ? $"{AltitudeFlyout.Title(_kind)} · {now}" : AltitudeFlyout.Title(_kind);

    Control IFlyoutPanel.View => this;

    void IFlyoutPanel.Reset() => Box.Text = "";

    void IFlyoutPanel.Refresh() => _titleNow.Text = (_aircraft is { } aircraft) ? $"now {AltitudeFlyout.PresentText(aircraft.AltitudeFeet)}" : "";

    void IFlyoutPanel.FocusOpeningLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(Box, isOpen, () => { });

    private void ShowBoxCommand()
    {
        string text = Box.Text ?? "";
        string? command = BoxCommand;
        bool blank = string.IsNullOrWhiteSpace(text);
        BoxCommandText.Text = command ?? (blank ? $"{AltitudeFlyout.Verb(_kind)} {TakeoffFlyout.Unfilled}" : AltitudeFlyout.InvalidAltitude);
        if ((command is null) && !blank)
        {
            BoxCommandText.Foreground = FlyoutPanelParts.RefusalBrush;
        }
        else
        {
            BoxCommandText.ClearValue(TextBlock.ForegroundProperty);
        }

        BoxCommandText.Opacity = (command is not null) ? 0.7 : (blank ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 1);
        MenuCommandText.SetCommand(Box, command);
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Key != Key.Enter) || !Box.IsKeyboardFocusWithin)
        {
            return;
        }

        e.Handled = true;
        if (BoxCommand is { } command)
        {
            await _choose(command);
        }
        else if (string.IsNullOrWhiteSpace(Box.Text))
        {
            _close();
        }
    }
}
