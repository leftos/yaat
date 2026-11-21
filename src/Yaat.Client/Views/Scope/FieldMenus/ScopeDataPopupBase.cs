using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.ContextMenus;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope.Entries;

namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>
/// A surface a Scope field click opens over the canvas, one at a time: an ERAM field popup (<see cref="ScopeDataPopupBase"/>)
/// or a CRC FDB menu (<see cref="CrcMenuPopup"/>).
/// </summary>
internal interface IScopeFieldSurface
{
    /// <summary>Whether it is open.</summary>
    bool IsOpen { get; }

    /// <summary>Fired when it closes, however it closes.</summary>
    event EventHandler<EventArgs>? Closed;

    /// <summary>Opens it over <paramref name="anchor"/>, by <paramref name="block"/> (in the anchor's coordinates) when given.</summary>
    /// <returns>False when the anchor has no overlay layer.</returns>
    bool Open(Control anchor, Rect? block);

    /// <summary>Closes it.</summary>
    void Close();
}

/// <summary>
/// What an ERAM field popup tells its opener, and how it lays out its columns.
/// </summary>
/// <param name="RecordShift">Told whether Shift is held at each press, release and key press inside the popup.</param>
/// <param name="Pick">Runs a SCOPE DATA row's entry, or stages it while Shift is held.</param>
internal sealed record ScopeDataPopupHooks(Action<bool> RecordShift, Action<string> Pick)
{
    /// <summary>The tab a tabbed popup opens on; null lays the SCOPE DATA and PILOT columns side by side.</summary>
    public ScopeFieldMenuMode? TabbedOpening { get; init; }

    /// <summary>Told which column each command sent or staged from the popup came from; tab switches tell it nothing.</summary>
    public Action<ScopeFieldMenuMode>? ModeSent { get; init; }
}

/// <summary>
/// What every ERAM field popup shares (<see cref="ScopeDataAltitudePopup"/>, <see cref="ScopeDataHsfPopup"/>): a popup below the
/// clicked block holding one panel, a SCOPE DATA column of boxes and rows beside an optional PILOT column of catalog panels. Each row
/// shows the ERAM entry it runs and is disabled, its entry and note faded, while it has none; a row click hands its entry to the pick. Shift is
/// recorded at each pointer press, release and key press inside the popup, before the row under it runs; Esc closes it, as does
/// a click outside it, which still reaches the Scope. On opening, the PILOT panels reset and follow the aircraft once a second,
/// and only the first SCOPE DATA box takes the focus. A tabbed popup (<see cref="ScopeDataPopupHooks.TabbedOpening"/>) shows the
/// two columns one at a time under an ERAM | PILOT tab strip, at the size of the larger.
/// </summary>
internal abstract class ScopeDataPopupBase : IScopeFieldSurface
{
    /// <summary>The scope-data column's head.</summary>
    public const string ScopeDataHead = "SCOPE DATA";

    /// <summary>The pilot column's head.</summary>
    public const string PilotHead = "PILOT";

    /// <summary>What a row's entry shows in its value's place while its box holds no value.</summary>
    public const string Elided = "···";

    /// <summary>What a disabled clear row's note reads: its value is not set.</summary>
    public const string NoneSet = "none set";

    /// <summary>The faded hint after each SCOPE DATA box: the rows below run what it holds.</summary>
    public const string RowsHint = "→ rows";

    /// <summary>The tabbed popup's SCOPE DATA tab.</summary>
    public const string EramTabLabel = "ERAM";

    /// <summary>The tabbed popup's PILOT tab.</summary>
    public const string PilotTabLabel = "PILOT";

    private const double ScopeDataWidth = 320;
    private const double SingleColumnWidth = 321;
    private const double PilotMinWidth = 319;
    private const double PilotRightPadding = 8;
    private const double ScrolledMaxHeight = 600;
    private const double BlockGap = 4;
    private const double DisabledClearOpacity = 0.4;
    private const string RowClass = "scope-data-row";
    private const string OutlineClass = "scope-data-outline";
    private const string BoxClass = "scope-data-box";

    private static readonly ILogger Log = AppLog.CreateLogger("ScopeDataPopupBase");

    private static readonly IImmutableSolidColorBrush PanelFill = new ImmutableSolidColorBrush(Color.Parse("#2B2B2B"));
    private static readonly IImmutableSolidColorBrush PanelEdge = new ImmutableSolidColorBrush(Color.Parse("#464646"));
    private static readonly IImmutableSolidColorBrush RuleBrush = new ImmutableSolidColorBrush(Color.Parse("#3D3D3D"));
    private static readonly IImmutableSolidColorBrush SectionRuleBrush = new ImmutableSolidColorBrush(Color.Parse("#4A4A4A"));
    private static readonly IImmutableSolidColorBrush SectionCaptionBrush = new ImmutableSolidColorBrush(Color.Parse("#CFD5DC"));
    private static readonly IImmutableSolidColorBrush OutlineBrush = new ImmutableSolidColorBrush(Color.Parse("#8FC1FF"));
    private static readonly IImmutableSolidColorBrush BoxFill = new ImmutableSolidColorBrush(Color.Parse("#1E1E1E"));
    private static readonly IImmutableSolidColorBrush BoxEdge = new ImmutableSolidColorBrush(Color.Parse("#5A5A5A"));
    private static readonly IImmutableSolidColorBrush BoxFocusEdge = new ImmutableSolidColorBrush(Color.Parse("#4A9EFF"));
    private static readonly IImmutableSolidColorBrush EramBrush = new ImmutableSolidColorBrush(Color.Parse("#58C4E6"));
    private static readonly IImmutableSolidColorBrush PilotBrush = new ImmutableSolidColorBrush(Color.Parse("#E0B84A"));
    private static readonly IImmutableSolidColorBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#E8E8E8"));
    private static readonly IImmutableSolidColorBrush EntryBrush = new ImmutableSolidColorBrush(Color.Parse("#9AA3AD"));
    private static readonly IImmutableSolidColorBrush FadedBrush = new ImmutableSolidColorBrush(Color.Parse("#5D636B"));
    private static readonly IImmutableSolidColorBrush HoverFill = new ImmutableSolidColorBrush(Color.Parse("#1FFFFFFF"));
    private static readonly IImmutableSolidColorBrush PressedFill = new ImmutableSolidColorBrush(Color.Parse("#2EFFFFFF"));

    private readonly ScopeDataPopupHooks _hooks;
    private readonly List<Row> _rows = [];
    private readonly List<(string Caption, TextBlock Note, Func<string> NoteNow)> _captionNotes = [];
    private readonly List<IFlyoutPanel> _panels = [];
    private FlyoutLiveTick? _tick;
    private (Control Anchor, Rect Block)? _placement;
    private OverlayLayer? _overlay;
    private Tabs? _tabs;
    private Border? _tabFrame;

    /// <param name="flid">The clicked track's callsign, which every entry names last.</param>
    /// <param name="hooks">What the popup tells its opener, and whether it is tabbed.</param>
    protected ScopeDataPopupBase(string flid, ScopeDataPopupHooks hooks)
    {
        Flid = flid;
        _hooks = hooks;
        Popup = new Popup
        {
            Placement = PlacementMode.Pointer,
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = true,
            ShouldUseOverlayLayer = true,
        };
        Popup.Opened += (_, _) => OnOpened();
        Popup.Closed += (_, _) => _tick?.Stop();
    }

    /// <summary>The popup itself.</summary>
    public Popup Popup { get; }

    /// <summary>Whether the popup is open.</summary>
    public bool IsOpen => Popup.IsOpen;

    /// <summary>Fired when the popup closes, by a pick, Esc, a click outside it or <see cref="Close"/>.</summary>
    public event EventHandler<EventArgs>? Closed
    {
        add => Popup.Closed += value;
        remove => Popup.Closed -= value;
    }

    /// <summary>The clicked track's callsign.</summary>
    protected string Flid { get; }

    /// <summary>The SCOPE DATA box that takes the focus as the popup opens; null in a popup with no SCOPE DATA column.</summary>
    protected abstract TextBox? FocusBox { get; }

    /// <summary>The SCOPE DATA row labelled <paramref name="label"/>.</summary>
    public Button RowFor(string label) =>
        _rows.Find(r => r.Label == label)?.Button ?? throw new ArgumentOutOfRangeException(nameof(label), label, "No row.");

    /// <summary>The entry text the SCOPE DATA row labelled <paramref name="label"/> shows.</summary>
    public string EntryShownFor(string label) =>
        _rows.Find(r => r.Label == label)?.Entry.Text ?? throw new ArgumentOutOfRangeException(nameof(label), label, "No row.");

    /// <summary>The note the SCOPE DATA row labelled <paramref name="label"/> shows beside its label.</summary>
    public string NoteShownFor(string label) => RowNamed(label).Note.Text ?? "";

    /// <summary>The label, entry and note text blocks of the SCOPE DATA row labelled <paramref name="label"/>.</summary>
    public (TextBlock Label, TextBlock Entry, TextBlock Note) RowPartsFor(string label)
    {
        Row row = RowNamed(label);
        return (row.Name, row.Entry, row.Note);
    }

    /// <summary>The note the caption line reading <paramref name="caption"/> shows right.</summary>
    public string CaptionNoteFor(string caption)
    {
        int index = _captionNotes.FindIndex(c => c.Caption == caption);
        return (index < 0) ? throw new ArgumentOutOfRangeException(nameof(caption), caption, "No caption.") : _captionNotes[index].Note.Text ?? "";
    }

    /// <summary>The PILOT column's cell, its right padding included; null in a popup with no PILOT column.</summary>
    public Control? PilotColumnHost { get; private set; }

    /// <summary>The scroller around the PILOT column; null where the column does not scroll.</summary>
    public ScrollViewer? PilotScroll { get; private set; }

    /// <summary>The column a tabbed popup shows; null in a popup with its columns side by side.</summary>
    public ScopeFieldMenuMode? ShownTab => _tabs?.Shown;

    /// <summary>The tab of <paramref name="mode"/>'s column; null in a popup with its columns side by side.</summary>
    public ToggleButton? TabFor(ScopeFieldMenuMode mode) => (mode == ScopeFieldMenuMode.Eram) ? _tabs?.Eram : _tabs?.Pilot;

    /// <summary>
    /// Shows <paramref name="mode"/>'s column alone, as its tab does, and gives the SCOPE DATA box the focus with the ERAM
    /// column; remembers nothing. A popup with its columns side by side has no tabs to switch.
    /// </summary>
    public void ShowTab(ScopeFieldMenuMode mode)
    {
        if (_tabs is not { } tabs)
        {
            Log.LogWarning("{Flid} popup has no tabs; {Mode} is not shown alone", Flid, mode);
            return;
        }

        tabs.Show(mode);
        if (Popup.IsOpen)
        {
            FocusShown();
        }
    }

    /// <summary>The control a tabbed popup focuses while its PILOT column shows; null focuses the panel's frame.</summary>
    protected virtual Control? PilotFocus => null;

    // The focus goes to the column on show: the SCOPE DATA box with ERAM (and side by side), else the PILOT column's own box,
    // else the frame, so the popup keeps the focus (and with it Esc and the Shift record) and no hidden box takes typing.
    private void FocusShown()
    {
        if (ShownTab != ScopeFieldMenuMode.Pilot)
        {
            FocusBox?.Focus(NavigationMethod.Directional);
            return;
        }

        if ((PilotFocus?.Focus(NavigationMethod.Directional) != true) && (_tabFrame?.Focus() != true))
        {
            Log.LogWarning("{Flid} popup: nothing in its PILOT column took the focus", Flid);
        }
    }

    /// <summary>
    /// Opens the popup over <paramref name="anchor"/>, in its overlay layer: below <paramref name="block"/> (in the anchor's
    /// coordinates) when given, or above it, or beside it where neither has room; else at the pointer.
    /// </summary>
    /// <returns>False when the anchor has no overlay layer.</returns>
    public bool Open(Control anchor, Rect? block)
    {
        if (OverlayLayer.GetOverlayLayer(anchor) is not { } overlay)
        {
            return false;
        }

        _placement = null;
        if (block is { } bounds)
        {
            PlaceBelow(bounds);
            _placement = (anchor, bounds);
        }

        Popup.PlacementTarget = anchor;
        overlay.Children.Add(Popup);
        _overlay = overlay;
        Popup.Closed += LeaveOverlay;
        Popup.IsOpen = true;
        return true;
    }

    // Once closed, the popup leaves the overlay it opened in, and this handler unsubscribes itself.
    private void LeaveOverlay(object? sender, EventArgs e)
    {
        Popup.Closed -= LeaveOverlay;
        _overlay?.Children.Remove(Popup);
        _overlay = null;
    }

    /// <summary>Closes the popup.</summary>
    public void Close() => Popup.IsOpen = false;

    // Its top 4 px under the block and its left at the block's left; above the block, 4 px clear, where below would leave the
    // window, and slid back inside the window. The rect spans the gap on both sides, as a flip keeps the offset.
    private void PlaceBelow(Rect block)
    {
        Popup.Placement = PlacementMode.AnchorAndGravity;
        Popup.PlacementRect = new Rect(block.X, block.Y - BlockGap, block.Width, block.Height + (2 * BlockGap));
        Popup.PlacementAnchor = PopupAnchor.BottomLeft;
        Popup.PlacementGravity = PopupGravity.BottomRight;
        Popup.PlacementConstraintAdjustment =
            PopupPositionerConstraintAdjustment.FlipY | PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY;
    }

    // Decided as the popup opens, before its first frame: its content is measured in the popup, where it is styled and
    // templated (detached, a ScrollViewer measures empty), against the window in the anchor's coordinates. Where neither below
    // nor above the block has room, the popup moves beside it (PlaceBeside), so it never covers the block.
    private void PlaceBesideWhereNoRoom(Control anchor, Rect block)
    {
        if (Popup.Child is not { } content)
        {
            Log.LogWarning("{Flid} popup has no content to measure; it keeps its below-or-above placement", Flid);
            return;
        }

        if (TopLevel.GetTopLevel(anchor) is not { } top)
        {
            Log.LogWarning("{Flid} popup's anchor has no TopLevel; it keeps its below-or-above placement", Flid);
            return;
        }

        if (anchor.TranslatePoint(default, top) is not { } offset)
        {
            Log.LogWarning("{Flid} popup's anchor does not translate to its TopLevel; it keeps its below-or-above placement", Flid);
            return;
        }

        content.Measure(Size.Infinity);
        Size size = content.DesiredSize;
        var window = new Rect(new Point(-offset.X, -offset.Y), top.ClientSize);
        bool fitsBelow = (block.Bottom + BlockGap + size.Height) <= window.Bottom;
        bool fitsAbove = (block.Top - BlockGap - size.Height) >= window.Top;
        if ((!fitsBelow) && (!fitsAbove))
        {
            PlaceBeside(BesideAt(block, size, window));
        }
    }

    // The popup's top-left beside the block: its left edge 4 px right of the block where it fits there, else its right edge
    // 4 px left of it, and at the window's left edge where it fits on neither side; its top level with the block's, clamped
    // inside the window.
    private static Point BesideAt(Rect block, Size size, Rect window)
    {
        double right = block.Right + BlockGap;
        double x = ((right + size.Width) <= window.Right) ? right : Math.Max(window.Left, block.Left - BlockGap - size.Width);
        double y = Math.Max(window.Top, Math.Min(block.Top, window.Bottom - size.Height));
        return new Point(x, y);
    }

    // The popup's top-left at `at`, in the anchor's coordinates, slid only up or down, never across the block.
    private void PlaceBeside(Point at)
    {
        Popup.Placement = PlacementMode.AnchorAndGravity;
        Popup.PlacementRect = new Rect(at, new Size(1, 1));
        Popup.PlacementAnchor = PopupAnchor.TopLeft;
        Popup.PlacementGravity = PopupGravity.BottomRight;
        Popup.PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideY;
    }

    /// <summary>Runs once the focus box has the focus as the popup opens.</summary>
    protected virtual void AfterFocus() { }

    /// <summary>
    /// Gives the popup its panel: records Shift and takes Esc inside it, follows <paramref name="panels"/> on
    /// <paramref name="host"/>'s clock while open, and shows every row's entry.
    /// </summary>
    protected void Attach(Control content, Action<bool> recordShift, IMenuHost host, IReadOnlyList<IFlyoutPanel> panels)
    {
        ScopeFieldMenus.RecordShift(content, recordShift);
        content.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Popup.Child = content;
        _panels.AddRange(panels);
        _tick = new FlyoutLiveTick(host.Clock, Refresh);
        Present();
    }

    /// <summary>What the live tick runs while the popup is open: the PILOT panels follow the aircraft and the rows re-read it.</summary>
    internal void Refresh()
    {
        foreach (IFlyoutPanel panel in _panels)
        {
            panel.Refresh();
        }

        Present();
    }

    /// <summary>The choose action a PILOT panel sends through: sends (or the host stages it) and closes the popup.</summary>
    protected Func<string, Task> Chooser(IMenuHost host)
    {
        string initials = host.Session.Initials;
        return command => ChooseAsync(host, command, initials);
    }

    /// <summary>
    /// Shows each caption's note, and each row's entry, or its elided text, disabled, while it has none: a clear row then fades
    /// whole, to 40% opacity; any other row keeps its label and fades its entry and note.
    /// </summary>
    protected void Present()
    {
        foreach ((_, TextBlock note, Func<string> noteNow) in _captionNotes)
        {
            note.Text = noteNow();
        }

        foreach (Row row in _rows)
        {
            string? entry = row.EntryNow();
            bool disabled = entry is null;
            bool fadesParts = (disabled) && (!row.FadesWhole);
            row.Button.IsEnabled = !disabled;
            row.Button.Opacity = ((disabled) && (row.FadesWhole)) ? DisabledClearOpacity : 1;
            row.Entry.Text = ScopeEramKeys.ToDisplay(ScopeEramKeys.MapTyped(entry ?? row.Elided));
            row.Entry.Foreground = fadesParts ? FadedBrush : EntryBrush;
            row.Note.Foreground = fadesParts ? FadedBrush : FlyoutPanelParts.TitleBrush;
            row.Note.Text = disabled ? row.DisabledNote() : row.EnabledNote();
        }
    }

    /// <summary>
    /// Adds a SCOPE DATA row to <paramref name="column"/>: <paramref name="label"/> and <paramref name="note"/> left, the entry
    /// <paramref name="entryNow"/> gives right, or <paramref name="elided"/> while it gives none.
    /// </summary>
    protected void AddRow(StackPanel column, string label, string note, Func<string?> entryNow, string elided) =>
        AddRow(column, label, () => note, entryNow, elided);

    /// <summary>As the other overload, the note read again with the rows.</summary>
    protected void AddRow(StackPanel column, string label, Func<string> note, Func<string?> entryNow, string elided) =>
        Add(column, new Row(label, (note, note), entryNow, elided));

    /// <summary>
    /// Adds a clear row to <paramref name="column"/>: <paramref name="label"/> and <paramref name="note"/> left and
    /// <paramref name="entry"/> right, enabled while <paramref name="isSet"/> holds; disabled it reads <see cref="NoneSet"/> and
    /// fades whole.
    /// </summary>
    protected void AddClearRow(StackPanel column, string label, string note, Func<bool> isSet, string entry) =>
        Add(column, new Row(label, (() => note, () => NoneSet), () => isSet() ? entry : null, entry) { FadesWhole = true });

    private Row RowNamed(string label) => _rows.Find(r => r.Label == label) ?? throw new ArgumentOutOfRangeException(nameof(label), label, "No row.");

    private void Add(StackPanel column, Row row)
    {
        row.Button.Click += (_, _) => OnRowClick(row);
        _rows.Add(row);
        column.Children.Add(row.Button);
    }

    /// <summary>A SCOPE DATA box: its placeholder, 150 wide, in the mock's frame (the panel's styles).</summary>
    protected static TextBox NewBox(string placeholder)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            Width = 150,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.Classes.Add(BoxClass);
        return box;
    }

    /// <summary>The rule between two SCOPE DATA sections.</summary>
    protected static Border SectionRule() =>
        new()
        {
            Height = 1,
            Background = SectionRuleBrush,
            Margin = new Thickness(2, 8, 2, 2),
        };

    /// <summary>
    /// The panel: SCOPE DATA and, split by a one-pixel rule, the PILOT column as wide as its panels need (at least 319) with 8 to
    /// spare on its right. With <paramref name="scrollPilot"/> the panel is at most 600 high and the PILOT column scrolls. A
    /// tabbed popup (<see cref="ScopeDataPopupHooks.TabbedOpening"/>) shows one column at a time instead
    /// (<see cref="TabbedPanel"/>), and never scrolls.
    /// </summary>
    protected Border Panel(Control left, Control right, bool scrollPilot)
    {
        if (_hooks.TabbedOpening is { } opening)
        {
            return TabbedPanel(left, right, opening);
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions($"{ScopeDataWidth},Auto,Auto") };
        grid.Children.Add(left);
        PilotScroll = scrollPilot
            ? new ScrollViewer
            {
                Content = right,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            }
            : null;
        var host = new Border
        {
            MinWidth = PilotMinWidth,
            Padding = new Thickness(0, 0, PilotRightPadding, 0),
            Child = PilotScroll ?? right,
        };
        PilotColumnHost = host;
        var rule = new Border
        {
            Width = 1,
            Background = RuleBrush,
            Margin = new Thickness(0, 8),
        };
        Grid.SetColumn(rule, 1);
        grid.Children.Add(rule);
        Grid.SetColumn(host, 2);
        grid.Children.Add(host);
        Border panel = Frame(grid, double.NaN);
        if (scrollPilot)
        {
            panel.MaxHeight = ScrolledMaxHeight;
        }

        return panel;
    }

    // The tabbed panel: the ERAM | PILOT tab strip above a body holding both columns in one cell, the one not shown kept in the
    // layout unseen, so the body takes the larger column's size and switching tabs never resizes the popup. It opens on
    // `opening`.
    private Border TabbedPanel(Control left, Control right, ScopeFieldMenuMode opening)
    {
        var scopeData = new Border { MinWidth = ScopeDataWidth, Child = left };
        var pilot = new Border
        {
            MinWidth = PilotMinWidth,
            Padding = new Thickness(0, 0, PilotRightPadding, 0),
            Child = right,
        };
        PilotColumnHost = pilot;
        var tabs = new Tabs(scopeData, pilot, this);
        _tabs = tabs;
        tabs.Show(opening);
        return TabbedFrame(tabs, new Grid { Children = { scopeData, pilot } });
    }

    /// <summary>
    /// The panel of a popup with a PILOT column alone, as the STARS field menus' tabbed style shows it: a tab strip holding the one
    /// PILOT tab above the column, shown.
    /// </summary>
    protected Border PilotOnlyPanel(Control pilotColumn)
    {
        var pilot = new Border
        {
            MinWidth = PilotMinWidth,
            Padding = new Thickness(0, 0, PilotRightPadding, 0),
            Child = pilotColumn,
        };
        PilotColumnHost = pilot;
        var tabs = new Tabs(null, pilot, this);
        _tabs = tabs;
        tabs.Show(ScopeFieldMenuMode.Pilot);
        return TabbedFrame(tabs, pilot);
    }

    // The tab strip above the body, in a frame that takes the focus while no box in it has it.
    private Border TabbedFrame(Tabs tabs, Control body)
    {
        var content = new StackPanel { Children = { FlyoutPanelParts.ToggleStrip(tabs.Buttons), body } };
        Border frame = Frame(content, double.NaN);
        frame.Focusable = true;
        _tabFrame = frame;
        return frame;
    }

    /// <summary>The panel of one column alone, as wide as the two-column panel's SCOPE DATA column and its rule.</summary>
    protected static Border SingleColumnPanel(Control column) => Frame(column, SingleColumnWidth);

    /// <summary>A SCOPE DATA column, its head in place.</summary>
    protected static StackPanel ScopeDataColumn()
    {
        var column = new StackPanel { Margin = new Thickness(0, 4) };
        column.Children.Add(ColumnHead(ScopeDataHead, EramBrush, "runs an ERAM entry"));
        return column;
    }

    /// <summary>The PILOT column: its head above <paramref name="panels"/>, separated from each other by a line.</summary>
    protected static StackPanel PilotColumn(IReadOnlyList<Control> panels)
    {
        var column = new StackPanel { Margin = new Thickness(0, 4) };
        column.Children.Add(ColumnHead(PilotHead, PilotBrush, "sends a pilot command"));
        for (int index = 0; index < panels.Count; index++)
        {
            if (index > 0)
            {
                column.Children.Add(FlyoutPanelParts.SeparatorLine());
            }

            column.Children.Add(panels[index]);
        }

        return column;
    }

    /// <summary>
    /// Adds a section's caption line to <paramref name="column"/>: <paramref name="caption"/> left, the note
    /// <paramref name="noteNow"/> gives right in the monospace font, read again with the rows; the caption in
    /// <paramref name="weight"/>.
    /// </summary>
    protected void AddCaption(StackPanel column, string caption, Func<string> noteNow, FontWeight weight) =>
        AddCaptionLine(
            column,
            caption,
            new TextBlock
            {
                Text = caption,
                FontSize = 11,
                FontWeight = weight,
                Foreground = FlyoutPanelParts.TitleBrush,
            },
            noteNow
        );

    /// <summary>As <see cref="AddCaption"/> for a section's caption: bold, in the section caption's lighter colour.</summary>
    protected void AddSectionCaption(StackPanel column, string caption, Func<string> noteNow) =>
        AddCaptionLine(
            column,
            caption,
            new TextBlock
            {
                Text = caption,
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = SectionCaptionBrush,
            },
            noteNow
        );

    // The caption line: `title` (which reads `caption`) left, the note `noteNow` gives right.
    private void AddCaptionLine(StackPanel column, string caption, TextBlock title, Func<string> noteNow)
    {
        var right = new TextBlock
        {
            Text = noteNow(),
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
        };
        FlyoutPanelParts.Mono(right);
        _captionNotes.Add((caption, right, noteNow));
        column.Children.Add(FlyoutPanelParts.TitleLine(title, right));
    }

    /// <summary>The ERAM kind chip, <paramref name="box"/> and, right, the faded <see cref="RowsHint"/>.</summary>
    protected static Grid BoxRow(TextBox box)
    {
        var kind = new TextBlock
        {
            Text = "ERAM",
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = EramBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        FlyoutPanelParts.Mono(kind);
        var chip = new Border
        {
            Width = 40,
            Height = 18,
            BorderBrush = EramBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = kind,
        };
        var hint = new TextBlock
        {
            Text = RowsHint,
            FontSize = 11,
            Foreground = FadedBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            ColumnSpacing = 8,
            Height = 30,
            Margin = new Thickness(11, 4),
            Children = { chip, box, hint },
        };
        Grid.SetColumn(box, 1);
        Grid.SetColumn(hint, 2);
        return row;
    }

    private void OnRowClick(Row row)
    {
        if (row.EntryNow() is { } entry)
        {
            _hooks.ModeSent?.Invoke(ScopeFieldMenuMode.Eram);
            _hooks.Pick(entry);
        }
    }

    // The PILOT panels reset and follow the aircraft; the popup moves beside its block where neither below nor above has room;
    // only the SCOPE DATA box takes the focus, never a panel's own box.
    private void OnOpened()
    {
        foreach (IFlyoutPanel panel in _panels)
        {
            panel.Reset();
            panel.Refresh();
        }

        if (_placement is { } placement)
        {
            PlaceBesideWhereNoRoom(placement.Anchor, placement.Block);
        }

        _tick?.Start();
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!Popup.IsOpen)
                {
                    return;
                }

                FocusShown();
                AfterFocus();
            },
            DispatcherPriority.Loaded
        );
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private async Task ChooseAsync(IMenuHost host, string command, string initials)
    {
        _hooks.ModeSent?.Invoke(ScopeFieldMenuMode.Pilot);
        Task sending = host.SendAsync(Flid, command, initials);
        Close();
        await sending;
    }

    // The rows' looks on the row's content presenter: the hover and pressed fills, and no fill while disabled.
    private static Style RowStyle(string state, IBrush fill) =>
        new(x => x.OfType<Button>().Class(RowClass).Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, fill) },
        };

    // The panel's frame around its content, the rows' looks styled here.
    private static Border Frame(Control child, double width)
    {
        var panel = new Border
        {
            Width = width,
            Background = PanelFill,
            BorderBrush = PanelEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(0, 4),
            BoxShadow = BoxShadows.Parse("0 8 18 0 #8C000000"),
            Child = child,
        };
        panel.Styles.Add(RowStyle(":pointerover", HoverFill));
        panel.Styles.Add(RowStyle(":pressed", PressedFill));
        panel.Styles.Add(RowStyle(":disabled", Brushes.Transparent));
        // The hover outline is hidden by a style rather than a local value, which would win over the hover style.
        panel.Styles.Add(new Style(x => x.OfType<Rectangle>().Class(OutlineClass)) { Setters = { new Setter(Visual.IsVisibleProperty, false) } });
        panel.Styles.Add(
            new Style(x => x.OfType<Button>().Class(RowClass).Class(":pointerover").Descendant().OfType<Rectangle>().Class(OutlineClass))
            {
                Setters = { new Setter(Visual.IsVisibleProperty, true) },
            }
        );
        panel.Styles.Add(BoxStyle(null, BoxEdge, 1));
        panel.Styles.Add(BoxStyle(":focus", BoxFocusEdge, 2));
        return panel;
    }

    // The SCOPE DATA box's frame in `state` (none: at rest and hovered), on the template's border, where the Fluent theme sets
    // its own: the mock's fill and corners, and `edge` `thickness` wide. A style outranks the theme's state setters, so the
    // rest style holds under the pointer too; a disabled row never gets :pointerover, so the outline style needs no :disabled.
    private static Style BoxStyle(string? state, IBrush edge, double thickness) =>
        new(x =>
        {
            Selector box = x.OfType<TextBox>().Class(BoxClass);
            return ((state is null) ? box : box.Class(state)).Template().OfType<Border>().Name("PART_BorderElement");
        })
        {
            Setters =
            {
                new Setter(Border.BackgroundProperty, BoxFill),
                new Setter(Border.BorderBrushProperty, edge),
                new Setter(Border.BorderThicknessProperty, new Thickness(thickness)),
                new Setter(Border.CornerRadiusProperty, new CornerRadius(4)),
            },
        };

    // A column's head: its kind in its colour, bold and monospace, left; its hint right; a rule under both.
    private static Border ColumnHead(string kind, IImmutableSolidColorBrush brush, string hint)
    {
        var label = new TextBlock
        {
            Text = kind,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 0.5,
            Foreground = brush,
        };
        FlyoutPanelParts.Mono(label);
        var note = new TextBlock
        {
            Text = hint,
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(label);
        Grid.SetColumn(note, 1);
        grid.Children.Add(note);
        return new Border
        {
            BorderBrush = RuleBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(4),
            Padding = new Thickness(7, 2, 7, 4),
            Child = grid,
        };
    }

    // A tabbed popup's ERAM and PILOT tabs and the columns they show: the shown column seen, the other kept in the layout but
    // unseen, out of hit testing and disabled, so it takes neither a click nor the focus. With no SCOPE DATA column there is no
    // ERAM tab, and the PILOT tab stands alone.
    private sealed class Tabs(Control? scopeData, Control pilot, ScopeDataPopupBase popup)
    {
        public ToggleButton? Eram { get; } =
            (scopeData is null) ? null : FlyoutPanelParts.VerbToggle(EramTabLabel, "", first: true, () => popup.ShowTab(ScopeFieldMenuMode.Eram));

        public ToggleButton Pilot { get; } =
            FlyoutPanelParts.VerbToggle(PilotTabLabel, "", first: (scopeData is null), () => popup.ShowTab(ScopeFieldMenuMode.Pilot));

        public ScopeFieldMenuMode Shown { get; private set; }

        // The tab strip's buttons, ERAM (where there is one) then PILOT.
        public Button[] Buttons => (Eram is { } eram) ? [eram, Pilot] : [Pilot];

        public void Show(ScopeFieldMenuMode mode)
        {
            bool eram = (mode == ScopeFieldMenuMode.Eram) && (scopeData is not null);
            Shown = eram ? ScopeFieldMenuMode.Eram : ScopeFieldMenuMode.Pilot;
            Eram?.IsChecked = eram;
            Pilot.IsChecked = !eram;
            if (scopeData is not null)
            {
                Reveal(scopeData, eram);
            }

            Reveal(pilot, !eram);
        }

        private static void Reveal(Control column, bool shown)
        {
            column.Opacity = shown ? 1 : 0;
            column.IsHitTestVisible = shown;
            column.IsEnabled = shown;
        }
    }

    // A SCOPE DATA row: its label left, and right its note, right-aligned, beside the entry it runs, in the monospace font. Its
    // entry and notes come from the popup's state now (a null entry: the row is disabled and shows its elided text and its
    // disabled note).
    private sealed class Row
    {
        public Row(string label, (Func<string> Enabled, Func<string> Disabled) notes, Func<string?> entryNow, string elided)
        {
            Label = label;
            EntryNow = entryNow;
            Elided = elided;
            EnabledNote = notes.Enabled;
            DisabledNote = notes.Disabled;
            Entry = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            FlyoutPanelParts.Mono(Entry);
            Name = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = LabelBrush,
                Padding = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Note = new TextBlock
            {
                Text = notes.Enabled(),
                FontSize = 11,
                Foreground = FlyoutPanelParts.TitleBrush,
                Margin = new Thickness(8, 0, 0, 0),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                Margin = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Children.Add(Name);
            Grid.SetColumn(Note, 2);
            grid.Children.Add(Note);
            Grid.SetColumn(Entry, 3);
            grid.Children.Add(Entry);
            // The hover outline: a 1 px dashed line just inside the row, shown by the panel's style.
            var outline = new Rectangle
            {
                Stroke = OutlineBrush,
                StrokeThickness = 1,
                StrokeDashArray = [3, 3],
                RadiusX = 3,
                RadiusY = 3,
                IsHitTestVisible = false,
            };
            outline.Classes.Add(OutlineClass);
            Button = new Button
            {
                Content = new Panel { Children = { outline, grid } },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            Button.Classes.Add(RowClass);
            AutomationProperties.SetName(Button, label);
        }

        public string Label { get; }

        public Func<string?> EntryNow { get; }

        public string Elided { get; }

        public Func<string> EnabledNote { get; }

        public Func<string> DisabledNote { get; }

        // A clear row: disabled, it fades whole rather than only its entry and note.
        public bool FadesWhole { get; init; }

        public TextBlock Name { get; }

        public TextBlock Note { get; }

        public TextBlock Entry { get; }

        public Button Button { get; }
    }
}
