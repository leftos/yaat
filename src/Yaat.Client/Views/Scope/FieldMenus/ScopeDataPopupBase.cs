using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Yaat.Client.ContextMenus;
using Yaat.Client.Views.Scope.Entries;

namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>
/// What every ERAM field popup shares (<see cref="ScopeDataAltitudePopup"/>, <see cref="ScopeDataHsfPopup"/>): a popup below the
/// clicked block holding one panel, a SCOPE DATA column of boxes and rows beside an optional PILOT column of catalog panels. Each row
/// shows the ERAM entry it runs and is disabled, its entry and note faded, while it has none; a row click hands its entry to the pick. Shift is
/// recorded at each pointer press, release and key press inside the popup, before the row under it runs; Esc closes it, as does
/// a click outside it, which still reaches the Scope. On opening, the PILOT panels reset and follow the aircraft once a second,
/// and only the first SCOPE DATA box takes the focus.
/// </summary>
internal abstract class ScopeDataPopupBase
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

    private const double ScopeDataWidth = 320;
    private const double SingleColumnWidth = 321;
    private const double PilotMinWidth = 319;
    private const double PilotRightPadding = 8;
    private const double ScrolledMaxHeight = 600;
    private const double BlockGap = 4;
    private const string RowClass = "scope-data-row";

    private static readonly IImmutableSolidColorBrush PanelFill = new ImmutableSolidColorBrush(Color.Parse("#2B2B2B"));
    private static readonly IImmutableSolidColorBrush PanelEdge = new ImmutableSolidColorBrush(Color.Parse("#464646"));
    private static readonly IImmutableSolidColorBrush RuleBrush = new ImmutableSolidColorBrush(Color.Parse("#3D3D3D"));
    private static readonly IImmutableSolidColorBrush EramBrush = new ImmutableSolidColorBrush(Color.Parse("#58C4E6"));
    private static readonly IImmutableSolidColorBrush PilotBrush = new ImmutableSolidColorBrush(Color.Parse("#E0B84A"));
    private static readonly IImmutableSolidColorBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#E8E8E8"));
    private static readonly IImmutableSolidColorBrush EntryBrush = new ImmutableSolidColorBrush(Color.Parse("#9AA3AD"));
    private static readonly IImmutableSolidColorBrush FadedBrush = new ImmutableSolidColorBrush(Color.Parse("#5D636B"));
    private static readonly IImmutableSolidColorBrush HoverFill = new ImmutableSolidColorBrush(Color.Parse("#1FFFFFFF"));
    private static readonly IImmutableSolidColorBrush PressedFill = new ImmutableSolidColorBrush(Color.Parse("#2EFFFFFF"));

    private readonly Action<string> _pick;
    private readonly List<Row> _rows = [];
    private readonly List<(string Caption, TextBlock Note, Func<string> NoteNow)> _captionNotes = [];
    private readonly List<IFlyoutPanel> _panels = [];
    private FlyoutLiveTick? _tick;

    /// <param name="flid">The clicked track's callsign, which every entry names last.</param>
    /// <param name="pick">Runs a SCOPE DATA row's entry, or stages it while Shift is held.</param>
    protected ScopeDataPopupBase(string flid, Action<string> pick)
    {
        Flid = flid;
        _pick = pick;
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

    /// <summary>The SCOPE DATA box that takes the focus as the popup opens.</summary>
    protected abstract TextBox FocusBox { get; }

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

    /// <summary>
    /// Opens the popup over <paramref name="anchor"/>, in its overlay layer: below <paramref name="block"/> (in the anchor's
    /// coordinates) when given, else at the pointer.
    /// </summary>
    /// <returns>False when the anchor has no overlay layer.</returns>
    public bool Open(Control anchor, Rect? block)
    {
        if (OverlayLayer.GetOverlayLayer(anchor) is not { } overlay)
        {
            return false;
        }

        if (block is { } bounds)
        {
            PlaceBelow(bounds);
        }

        Popup.PlacementTarget = anchor;
        overlay.Children.Add(Popup);
        Popup.Closed += (_, _) => overlay.Children.Remove(Popup);
        Popup.IsOpen = true;
        return true;
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
    /// Shows each caption's note, and each row's entry, or its elided text, faded and disabled, while it has none.
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
            row.Button.IsEnabled = entry is not null;
            row.Entry.Text = ScopeEramKeys.ToDisplay(ScopeEramKeys.MapTyped(entry ?? row.Elided));
            row.Entry.Foreground = (entry is null) ? FadedBrush : EntryBrush;
            row.Note.Foreground = (entry is null) ? FadedBrush : FlyoutPanelParts.TitleBrush;
            row.Note.Text = (entry is null) ? row.DisabledNote() : row.EnabledNote();
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
    /// <paramref name="entry"/> right, enabled while <paramref name="isSet"/> holds; disabled it reads <see cref="NoneSet"/>.
    /// </summary>
    protected void AddClearRow(StackPanel column, string label, string note, Func<bool> isSet, string entry) =>
        Add(column, new Row(label, (() => note, () => NoneSet), () => isSet() ? entry : null, entry));

    private Row RowNamed(string label) => _rows.Find(r => r.Label == label) ?? throw new ArgumentOutOfRangeException(nameof(label), label, "No row.");

    private void Add(StackPanel column, Row row)
    {
        row.Button.Click += (_, _) => OnRowClick(row);
        _rows.Add(row);
        column.Children.Add(row.Button);
    }

    /// <summary>A SCOPE DATA box: its placeholder, 150 wide.</summary>
    protected static TextBox NewBox(string placeholder) =>
        new()
        {
            PlaceholderText = placeholder,
            Width = 150,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>
    /// The panel: SCOPE DATA and, split by a one-pixel rule, the PILOT column as wide as its panels need (at least 319) with 8 to
    /// spare on its right. With <paramref name="scrollPilot"/> the panel is at most 600 high and the PILOT column scrolls.
    /// </summary>
    protected Border Panel(Control left, Control right, bool scrollPilot)
    {
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
    protected void AddCaption(StackPanel column, string caption, Func<string> noteNow, FontWeight weight)
    {
        var right = new TextBlock
        {
            Text = noteNow(),
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
        };
        FlyoutPanelParts.Mono(right);
        _captionNotes.Add((caption, right, noteNow));
        column.Children.Add(
            FlyoutPanelParts.TitleLine(
                new TextBlock
                {
                    Text = caption,
                    FontSize = 11,
                    FontWeight = weight,
                    Foreground = FlyoutPanelParts.TitleBrush,
                },
                right
            )
        );
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
            Height = 22,
            BorderBrush = EramBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
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
            _pick(entry);
        }
    }

    // The PILOT panels reset and follow the aircraft; only the SCOPE DATA box takes the focus, never a panel's own box.
    private void OnOpened()
    {
        foreach (IFlyoutPanel panel in _panels)
        {
            panel.Reset();
            panel.Refresh();
        }

        _tick?.Start();
        FlyoutPanelParts.FocusLater(FocusBox, () => Popup.IsOpen, AfterFocus);
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
        return panel;
    }

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
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            grid.Children.Add(Name);
            Grid.SetColumn(Note, 2);
            grid.Children.Add(Note);
            Grid.SetColumn(Entry, 3);
            grid.Children.Add(Entry);
            Button = new Button
            {
                Content = grid,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                Height = 28,
                Padding = new Thickness(8, 0),
                Margin = new Thickness(4, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            Button.Classes.Add(RowClass);
            AutomationProperties.SetName(Button, label);
        }

        public string Label { get; }

        public Func<string?> EntryNow { get; }

        public string Elided { get; }

        public Func<string> EnabledNote { get; }

        public Func<string> DisabledNote { get; }

        public TextBlock Name { get; }

        public TextBlock Note { get; }

        public TextBlock Entry { get; }

        public Button Button { get; }
    }
}
