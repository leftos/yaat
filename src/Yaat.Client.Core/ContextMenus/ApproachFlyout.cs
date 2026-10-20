using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// An approach verb's flyout over an <see cref="ApproachFlyoutModel"/>, one builder for the strip's flyout and the All
/// Commands entry, built once when it first opens and never refilled. Top to bottom: the title line naming the verb (a forced
/// verb's with the force mark, Cleared approach's with the Force approach toggle outside solo training), the pinned rows
/// (the default row, the airport-level visual row, Cancel IFR when the destination has no instrument approach) and a separator, the runway tabs
/// (<see cref="FlyoutTabStrip"/>) over the selected tab's rows in a scroller, a separator and the Approach box, then Cancel
/// IFR after a separator. Keyboard focus lands on the default row, else the selected tab's first live row; Up/Down and
/// Tab/Shift+Tab walk the pinned rows, the tab's live rows, the box and Cancel IFR, skipping greyed rows and stopping at
/// either end; Left/Right (outside the box) choose the previous or next tab and focus its first row. Typing in the box
/// filters the tab's rows by id; Enter sends its command. A row sends and closes the flyout and the menu; Cancel IFR… opens
/// an inline strip whose Confirm sends <c>CIFR</c> and whose Back folds it. Force approach re-renders the rows in place and
/// is off at every open.
/// </summary>
public static class ApproachFlyout
{
    /// <summary>The Approach box's name, by which a test or UI Automation finds it.</summary>
    public const string BoxName = "ApproachBox";

    /// <summary>What the empty Approach box reads.</summary>
    public const string BoxPlaceholder = "id, e.g. I28R";

    /// <summary>The Force approach toggle's text, as the mock's title line draws it.</summary>
    public const string ForceText = "Force";

    /// <summary>The class of the menu row that holds the whole panel, kept at its resting background.</summary>
    private const string PanelRowClass = "approach-flyout-panel";

    /// <summary>
    /// The <paramref name="spec"/> verb's entry, labelled with its label: one menu row holding the flyout, its model built from
    /// <paramref name="aircraft"/> when the flyout or the submenu first opens.
    /// </summary>
    public static MenuItem Build(ApproachVerbSpec spec, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = spec.Label };
        QuickCommandStrip.FollowInFlyout(entry, new ApproachFlyoutContent(entry, spec, aircraft, context, host));
        return entry;
    }

    /// <summary>The flyout's content on whichever surface shows it: the model, built once, its panel, and the sends.</summary>
    private sealed class ApproachFlyoutContent : IStripFlyoutContent
    {
        private readonly MenuItem _entry;
        private readonly ApproachVerbSpec _spec;
        private readonly IMenuAircraft? _aircraft;
        private readonly MenuContext _context;
        private readonly IMenuHost _host;
        private readonly ApproachFlyoutHost _holder = new();
        private ApproachFlyoutPanel? _panel;
        private Action? _closeFlyout;
        private bool _open;

        public ApproachFlyoutContent(MenuItem entry, ApproachVerbSpec spec, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
        {
            _entry = entry;
            _spec = spec;
            _aircraft = aircraft;
            _context = context;
            _host = host;
            // A menu row takes keyboard focus back from whatever inside it gets it, so the row holding the panel is not focusable.
            var panelRow = new MenuItem
            {
                Header = _holder,
                StaysOpenOnClick = true,
                Focusable = false,
            };
            panelRow.Classes.Add(PanelRowClass);
            panelRow.Styles.Add(FlyoutPanelParts.PlainPanelRowStyle(PanelRowClass));
            panelRow.Click += (_, e) => e.Handled = true;
            entry.Items.Add(panelRow);
            entry.PropertyChanged += (_, e) =>
            {
                if ((e.Property == MenuItem.IsSubMenuOpenProperty) && entry.IsSubMenuOpen)
                {
                    Open(null);
                }
                else if (e.Property == MenuItem.IsSubMenuOpenProperty)
                {
                    Closed();
                }
            };
        }

        /// <inheritdoc />
        public void Opened(ItemCollection items, Action close) => Open(close);

        /// <inheritdoc />
        public void PointerInside(bool inside) { }

        /// <inheritdoc />
        public void Closed()
        {
            _open = false;
            _closeFlyout = null;
        }

        private void Open(Action? close)
        {
            _open = true;
            _closeFlyout = close;
            if (_panel is null)
            {
                _panel = new ApproachFlyoutPanel(ApproachFlyoutModel.Build(_spec, _aircraft, _context, _host), ChooseAsync);
                _holder.Child = _panel;
            }
            else
            {
                _panel.Reset();
            }

            _panel.FocusOpeningLater(() => _open);
        }

        /// <summary>Sends <paramref name="command"/> and closes the strip's flyout and the menu, or the submenu's menu.</summary>
        private async Task ChooseAsync(string command)
        {
            Task sending = _host.SendAsync(_context.Callsign, command, _context.Initials);
            if (_closeFlyout is { } close)
            {
                close();
            }
            else
            {
                _entry.FindLogicalAncestorOfType<ContextMenu>()?.Close();
            }

            await sending;
        }
    }
}

/// <summary>The approach flyout's menu row header, which holds the <see cref="ApproachFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class ApproachFlyoutHost : Border
{
    /// <summary>The header as one line, for a menu outline: what it holds before and after the first opening.</summary>
    public override string ToString() => (Child is ApproachFlyoutPanel) ? "approach flyout" : "approach flyout, built when it opens";
}

/// <summary>
/// The approach flyout's one menu row (<see cref="ApproachFlyout"/>), at most <see cref="MenuCommandRow.RowMaxWidth"/> wide,
/// the tab rows scrolling below the pinned tab strip at most <see cref="TaxiToRunwayFlyout.RowsMaxHeight"/> tall.
/// </summary>
public sealed class ApproachFlyoutPanel : StackPanel
{
    private static readonly IImmutableSolidColorBrush StripFill = new ImmutableSolidColorBrush(Color.Parse("#1F2226"));
    private static readonly IImmutableSolidColorBrush StripLine = new ImmutableSolidColorBrush(Color.Parse("#3A414B"));
    private static readonly IImmutableSolidColorBrush StripAccent = new ImmutableSolidColorBrush(Color.Parse("#E0A84A"));
    private static readonly IImmutableSolidColorBrush ConfirmFill = new ImmutableSolidColorBrush(Color.Parse("#2C5FA6"));

    private readonly ApproachFlyoutModel _model;
    private readonly Func<string, Task> _choose;
    private readonly FuncDataTemplate<ApproachRow> _rowTemplate;
    private readonly StackPanel _rows = new();
    private readonly List<(Button Button, int Index)> _pinned = [];
    private readonly Grid _titleLine;
    private readonly TextBlock? _boxCommand;
    private readonly Grid? _boxRow;
    private List<Button> _tabRows = [];
    private CancelIfrControls? _cancelIfr;

    internal ApproachFlyoutPanel(ApproachFlyoutModel model, Func<string, Task> choose)
    {
        _model = model;
        _choose = choose;
        string glyphId = model.Spec.GlyphId;
        QuickCommandGlyph glyph = QuickCommandGlyphs.For(glyphId) ?? throw new InvalidOperationException($"No glyph for {glyphId}.");
        QuickCommandGlyph visualGlyph =
            QuickCommandGlyphs.For(MenuIds.ApproachClearedVisual)
            ?? throw new InvalidOperationException($"No glyph for {MenuIds.ApproachClearedVisual}.");
        _rowTemplate = new FuncDataTemplate<ApproachRow>((row, _) => ApproachRowView.Build(row, glyph, visualGlyph));
        MaxWidth = MenuCommandRow.RowMaxWidth;
        Scroll = new ScrollViewer
        {
            Content = _rows,
            MaxHeight = TaxiToRunwayFlyout.RowsMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true,
        };
        AutomationProperties.SetName(Scroll, $"{model.Title} approaches");
        _titleLine = TitleLine();
        Children.Add(_titleLine);

        AddPinned();
        AddTabs();
        if (model.HasBox)
        {
            Children.Add(FlyoutPanelParts.SeparatorLine());
            Box = new TextBox
            {
                Name = ApproachFlyout.BoxName,
                PlaceholderText = ApproachFlyout.BoxPlaceholder,
                Width = 130,
                Margin = new Thickness(0, 3, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(Box, $"{model.Title} by approach id");
            TakeoffFlyout.KeepClicksInside(Box);
            _boxCommand = new TextBlock();
            _boxRow = BoxRow((TextBox)Box, (TextBlock)_boxCommand, glyph);
            Children.Add(_boxRow);
            // Followed through the text property, so the rows filter as the text is set, before any later dispatcher pass.
            Box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    OnBoxChanged();
                }
            };
            ShowBoxCommand();
        }

        if (model.CancelIfr is { Placement: CancelIfrPlacement.Bottom } bottom)
        {
            Children.Add(FlyoutPanelParts.SeparatorLine());
            AddCancelIfr(bottom);
        }

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The runway tabs, hidden when the flyout has none.</summary>
    public FlyoutTabStrip Strip { get; } = new();

    /// <summary>The scrolling host of the selected tab's rows.</summary>
    public ScrollViewer Scroll { get; }

    /// <summary>The Force approach toggle, or null when the flyout offers none.</summary>
    public CheckBox? ForceToggle { get; private set; }

    /// <summary>The Approach box, or null when the flyout has none.</summary>
    public TextBox? Box { get; }

    /// <summary>The pinned rows above the tabs, in order (the default row, the airport-level visual row).</summary>
    public IReadOnlyList<Button> PinnedRows => [.. _pinned.Select(pinned => pinned.Button)];

    /// <summary>The selected tab's rows, in order, those the box's text filters out included.</summary>
    public IReadOnlyList<Button> Rows => _tabRows;

    /// <summary>The Cancel IFR… row, or null when the flyout offers none.</summary>
    public Button? CancelIfrRow => _cancelIfr?.Row;

    /// <summary>The Cancel IFR confirmation strip, or null when the flyout offers no Cancel IFR.</summary>
    public Border? CancelIfrStrip => _cancelIfr?.Strip;

    /// <summary>The confirmation strip's Confirm button.</summary>
    public Button? CancelIfrConfirm => _cancelIfr?.Confirm;

    /// <summary>The confirmation strip's Back button.</summary>
    public Button? CancelIfrBack => _cancelIfr?.Back;

    /// <summary>Whether Force approach is on.</summary>
    public bool IsForced => ForceToggle?.IsChecked == true;

    /// <summary>The selected tab's index; -1 without tabs.</summary>
    private int SelectedTab => (_model.Tabs.Count > 0) ? Strip.SelectedIndex : -1;

    /// <summary>
    /// The panel as text, one line per shown element: the title line, each pinned and visible tab row
    /// (<see cref="ApproachRow.ToString"/>, <c> [disabled]</c> after a greyed one), <c>---</c> for a separator, the box
    /// (<c>Approach — CAPP ···</c>) and Cancel IFR.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            List<string> lines = [];
            foreach (Control child in Children)
            {
                lines.AddRange(LinesOf(child));
            }

            return lines;
        }
    }

    /// <summary>Puts the flyout back as it opens: Force approach off, the box empty, the opening tab selected, the Cancel IFR strip folded.</summary>
    internal void Reset()
    {
        ForceToggle?.IsChecked = false;
        Box?.Text = "";
        if (_model.Tabs.Count > 0)
        {
            Strip.Select(_model.SelectedTab);
        }

        _cancelIfr?.Strip.IsVisible = false;
    }

    /// <summary>
    /// Focuses the default row, else the selected tab's first live row, else the first focusable control, once laid out and
    /// while <paramref name="isOpen"/> still holds.
    /// </summary>
    internal void FocusOpeningLater(Func<bool> isOpen) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!isOpen())
                {
                    return;
                }

                Control? first =
                    _pinned.Select(pinned => pinned.Button).FirstOrDefault(button => button.IsEnabled && IsDefault(button))
                    ?? LiveRows().FirstOrDefault()
                    ?? Focusables().FirstOrDefault();
                Focus(first);
            },
            DispatcherPriority.Loaded
        );

    private static bool IsDefault(Button button) => button.Content is ApproachRow { Badge: ApproachFlyoutModel.DefaultBadge };

    private void Focus(Control? control)
    {
        if (control is null)
        {
            Scroll.Focus(NavigationMethod.Directional);
            return;
        }

        control.Focus(NavigationMethod.Directional);
        control.BringIntoView();
    }

    /// <summary>The title line: the verb's title, the force mark after it for a forced verb, and the Force approach toggle where offered.</summary>
    private Grid TitleLine()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(11, 6, 11, 2) };
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = _model.Title,
                    FontSize = 11,
                    Foreground = FlyoutPanelParts.TitleBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        if (_model.Spec.IsForced)
        {
            title.Children.Add(ApproachRowView.ForceBadge());
        }

        grid.Children.Add(title);
        if (_model.OffersForce)
        {
            AddForceToggle(grid);
        }

        return grid;
    }

    private void AddForceToggle(Grid grid)
    {
        ForceToggle = new CheckBox
        {
            Content = ApproachFlyout.ForceText,
            FontSize = 11,
            Focusable = false,
            MinHeight = 0,
            Padding = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(ForceToggle, "Force approach");
        ForceToggle.IsCheckedChanged += (_, _) => Rerender();
        Grid.SetColumn(ForceToggle, 1);
        grid.Children.Add(ForceToggle);
    }

    private void AddPinned()
    {
        IReadOnlyList<ApproachRow> pinned = _model.Pinned(force: false);
        for (int index = 0; index < pinned.Count; index++)
        {
            Button button = RowButton();
            Present(button, pinned[index]);
            _pinned.Add((button, index));
            Children.Add(button);
        }

        if (_model.CancelIfr is { Placement: CancelIfrPlacement.Top } top)
        {
            AddCancelIfr(top);
        }

        bool anyPinned = (pinned.Count > 0) || (_model.CancelIfr?.Placement == CancelIfrPlacement.Top);
        if (anyPinned && ((_model.Tabs.Count > 0) || _model.HasBox))
        {
            Children.Add(FlyoutPanelParts.SeparatorLine());
        }
    }

    private void AddTabs()
    {
        if (_model.Tabs.Count == 0)
        {
            return;
        }

        Strip.SetTabs([.. _model.Tabs.Select(tab => new FlyoutTab(tab.Label, FlyoutTabState.Ready))]);
        Strip.Select(_model.SelectedTab);
        Strip.SelectionChanged += (_, _) =>
        {
            ShowTab();
            Dispatcher.UIThread.Post(() => Focus(LiveRows().FirstOrDefault()), DispatcherPriority.Loaded);
        };
        Children.Add(Strip);
        Children.Add(Scroll);
        ShowTab();
    }

    /// <summary>Shows the selected tab's rows, filtered by the box's text.</summary>
    private void ShowTab()
    {
        IReadOnlyList<ApproachRow> rows = _model.Rows(SelectedTab, IsForced);
        _tabRows = [];
        foreach (ApproachRow row in rows)
        {
            Button button = RowButton();
            Present(button, row);
            _tabRows.Add(button);
        }

        _rows.Children.Clear();
        _rows.Children.AddRange(_tabRows);
        Filter();
        ShowBoxCommand();
        Scroll.Offset = default;
    }

    /// <summary>Re-renders every row's command and marks for Force approach, in place, keeping the tab and the focused row.</summary>
    private void Rerender()
    {
        IReadOnlyList<ApproachRow> pinned = _model.Pinned(IsForced);
        foreach ((Button button, int index) in _pinned)
        {
            Present(button, pinned[index]);
        }

        if (SelectedTab >= 0)
        {
            IReadOnlyList<ApproachRow> rows = _model.Rows(SelectedTab, IsForced);
            for (int index = 0; index < rows.Count; index++)
            {
                Present(_tabRows[index], rows[index]);
            }
        }

        Filter();
        ShowBoxCommand();
    }

    private Button RowButton()
    {
        var button = new Button
        {
            ContentTemplate = _rowTemplate,
            Padding = new Thickness(11, 5),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ToolTip.SetShowOnDisabled(button, true);
        // A greyed row is drawn dimmed only, without the theme's filled disabled-button background.
        button.Resources["ButtonBackgroundDisabled"] = Brushes.Transparent;
        button.Click += async (_, e) =>
        {
            e.Handled = true;
            if (button.Content is ApproachRow { IsEnabled: true } row)
            {
                await _choose(row.Command);
            }
        };
        return button;
    }

    private static void Present(Button button, ApproachRow row)
    {
        button.Content = row;
        button.IsEnabled = row.IsEnabled;
        MenuCommandText.SetCommand(button, row.IsEnabled ? row.Command : null);
        AutomationProperties.SetName(button, row.ToString());
        ToolTip.SetTip(button, row.Tooltip);
    }

    /// <summary>Hides the tab rows whose id does not start with the box's text.</summary>
    private void Filter()
    {
        string typed = Box?.Text?.Trim() ?? "";
        foreach (Button button in _tabRows)
        {
            button.IsVisible = (typed.Length == 0) || ((button.Content is ApproachRow row) && row.Matches(typed));
        }
    }

    private void OnBoxChanged()
    {
        Filter();
        ShowBoxCommand();
    }

    private string? BoxCommand() => (Box is null) ? null : _model.BoxCommand(SelectedTab, Box.Text ?? "", IsForced);

    private void ShowBoxCommand()
    {
        if (_boxCommand is null)
        {
            return;
        }

        string? command = BoxCommand();
        _boxCommand.Text = command ?? $"{_model.BoxVerb(IsForced)} {TakeoffFlyout.Unfilled}";
        _boxCommand.Opacity = (command is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        if (Box is not null)
        {
            MenuCommandText.SetCommand(Box, command);
        }
    }

    private static Grid BoxRow(TextBox box, TextBlock command, QuickCommandGlyph glyph)
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
                new TextBlock { Text = "Approach" },
                box,
            },
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        command.FontSize = 12;
        command.Margin = new Thickness(16, 0, 0, 0);
        command.HorizontalAlignment = HorizontalAlignment.Right;
        command.VerticalAlignment = VerticalAlignment.Center;
        command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);
        return grid;
    }

    private void AddCancelIfr(ApproachCancelIfr cancelIfr)
    {
        var row = new Button
        {
            Content = CancelIfrRowView(),
            Padding = new Thickness(11, 5),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        // Named without the command: the row only opens the confirmation strip, whose Confirm sends it.
        AutomationProperties.SetName(row, ApproachCancelIfr.Label);
        var confirm = new Button
        {
            Content = "Confirm",
            Background = ConfirmFill,
            Padding = new Thickness(14, 4),
        };
        var back = new Button { Content = "Back", Padding = new Thickness(14, 4) };
        var strip = new Border
        {
            Background = StripFill,
            BorderBrush = StripLine,
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(41, 2, 6, 4),
            Padding = new Thickness(10, 8),
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = false,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = cancelIfr.Question,
                        FontSize = 12.5,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Margin = new Thickness(0, 8, 0, 0),
                        Children = { confirm, back },
                    },
                },
            },
        };
        strip.BorderBrush = StripAccent;
        AutomationProperties.SetName(strip, "Confirm cancel IFR");
        MenuCommandText.SetCommand(confirm, ApproachCancelIfr.Command);
        row.Click += (_, e) =>
        {
            e.Handled = true;
            strip.IsVisible = !strip.IsVisible;
        };
        confirm.Click += async (_, e) =>
        {
            e.Handled = true;
            await _choose(ApproachCancelIfr.Command);
        };
        back.Click += (_, e) =>
        {
            e.Handled = true;
            strip.IsVisible = false;
            row.Focus(NavigationMethod.Directional);
        };
        _cancelIfr = new CancelIfrControls(row, strip, confirm, back);
        Children.Add(row);
        Children.Add(strip);
    }

    private static Grid CancelIfrRowView()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(
            new Border
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 8, 0),
            }
        );
        var label = new TextBlock { Text = ApproachCancelIfr.Label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        var command = new TextBlock
        {
            Text = ApproachCancelIfr.Command,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);
        return grid;
    }

    private IEnumerable<Button> LiveRows() => _tabRows.Where(button => button.IsVisible && button.IsEnabled);

    /// <summary>
    /// Every control the arrow keys walk, in order: the live pinned rows, Cancel IFR and its strip, the live tab rows (the
    /// scroller when the tab has none), the box.
    /// </summary>
    private List<Control> Focusables()
    {
        List<Control> order = [];
        foreach (Control child in Children)
        {
            if (ReferenceEquals(child, Scroll))
            {
                // A tab with no live row keeps its place in the order through the scroller, so Down from it reaches the box.
                List<Button> live = [.. LiveRows()];
                order.AddRange((live.Count > 0) ? live : [Scroll]);
            }
            else if ((child is Button { IsEnabled: true, IsVisible: true } button))
            {
                order.Add(button);
            }
            else if (child.IsVisible && ((_cancelIfr is { } cancel) && ReferenceEquals(child, cancel.Strip)))
            {
                order.Add(cancel.Confirm);
                order.Add(cancel.Back);
            }
            else if ((Box is not null) && ReferenceEquals(child, _boxRow))
            {
                order.Add(Box);
            }
        }

        return order;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool inBox = Box?.IsKeyboardFocusWithin == true;
        switch (e.Key)
        {
            case Key.Left or Key.Right when !inBox && (_model.Tabs.Count > 0):
                e.Handled = true;
                Strip.Move((e.Key == Key.Left) ? -1 : 1);
                break;
            case Key.Enter when inBox:
                e.Handled = true;
                if (BoxCommand() is { } command)
                {
                    await _choose(command);
                }

                break;
            case Key.Up or Key.Down or Key.Tab:
                e.Handled = true;
                Step(((e.Key == Key.Down) || ((e.Key == Key.Tab) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))) ? 1 : -1);
                break;
        }
    }

    /// <summary>Focuses the control after (<paramref name="step"/> 1) or before (-1) the focused one, staying put at either end.</summary>
    private void Step(int step)
    {
        List<Control> order = Focusables();
        int at = order.FindIndex(control => control.IsKeyboardFocusWithin);
        int next = (at < 0) ? 0 : at + step;
        if ((next >= 0) && (next < order.Count))
        {
            Focus(order[next]);
        }
    }

    private IEnumerable<string> LinesOf(Control child)
    {
        if (!child.IsVisible)
        {
            return [];
        }

        return child switch
        {
            Grid when ReferenceEquals(child, _titleLine) => [TitleText()],
            Button { Content: ApproachRow row } button => [RowLine(row, button)],
            Button when ReferenceEquals(child, _cancelIfr?.Row) => [$"{ApproachCancelIfr.Label} — {ApproachCancelIfr.Command}"],
            Border { Height: 1 } => ["---"],
            FlyoutTabStrip => [string.Join(" | ", _model.Tabs.Select((tab, index) => (index == Strip.SelectedIndex) ? $"[{tab.Label}]" : tab.Label))],
            ScrollViewer => [.. _tabRows.Where(button => button.IsVisible).Select(button => RowLine((ApproachRow)button.Content!, button))],
            Border when ReferenceEquals(child, _cancelIfr?.Strip) => [$"Confirm: {_model.CancelIfr?.Question} [Confirm] [Back]"],
            Grid when ReferenceEquals(child, _boxRow) => [$"Approach — {_boxCommand?.Text}"],
            _ => [],
        };
    }

    /// <summary>The title line as text: <c>Cleared approach · Force [on]</c>, <c>Join approach (force) · force</c>, <c>Expect approach</c>.</summary>
    private string TitleText()
    {
        string mark = _model.Spec.IsForced ? $" · {ApproachRow.ForceMark}" : "";
        string toggle = (ForceToggle is null) ? "" : $" · {ApproachFlyout.ForceText}{(IsForced ? " [on]" : "")}";
        return $"{_model.Title}{mark}{toggle}";
    }

    private static string RowLine(ApproachRow row, Button button) => button.IsEnabled ? row.ToString() : $"{row} [disabled]";

    /// <summary>The Cancel IFR row, its confirmation strip and the strip's buttons.</summary>
    private sealed record CancelIfrControls(Button Row, Border Strip, Button Confirm, Button Back);
}

/// <summary>
/// The view of an <see cref="ApproachRow"/>, one line capped at <see cref="MenuCommandRow.RowMaxWidth"/>: the glyph (the verb's
/// for an instrument row, the visual eye otherwise), the name, the badge, the force mark, the notes cut with an ellipsis past
/// the cap, the kind and the command in the dimmed monospace font (amber when forced); the whole row dimmed when short or
/// greyed.
/// </summary>
internal static class ApproachRowView
{
    private static readonly IImmutableSolidColorBrush BadgeBrush = new ImmutableSolidColorBrush(Color.Parse("#E0A84A"));
    private static readonly IImmutableSolidColorBrush ForceFill = new ImmutableSolidColorBrush(Color.Parse("#4A3111"));
    private static readonly IImmutableSolidColorBrush ForceLine = new ImmutableSolidColorBrush(Color.Parse("#FFB259"));
    private static readonly IImmutableSolidColorBrush ForceText = new ImmutableSolidColorBrush(Color.Parse("#FFD08A"));

    /// <summary>The amber of a forced command (a forced approach row's, the PTAC flyout's rows with Forced on).</summary>
    internal static readonly IImmutableSolidColorBrush ForcedCommand = new ImmutableSolidColorBrush(Color.Parse("#FFC062"));

    public static Grid Build(ApproachRow row, QuickCommandGlyph instrumentGlyph, QuickCommandGlyph visualGlyph)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            MaxWidth = MenuCommandRow.RowMaxWidth,
            Opacity = row.IsDimmed ? MenuGlyphRowTemplate.DimmedOpacity : 1,
        };
        QuickCommandGlyph glyph = (row.Kind == ApproachRowKind.Instrument) ? instrumentGlyph : visualGlyph;
        Viewbox icon = QuickCommandStrip.GlyphIcon(glyph);
        icon.Margin = new Thickness(0, 0, 8, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);

        Grid name = NameView(row);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var kind = new TextBlock
        {
            Text = row.KindText,
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(kind, 2);
        grid.Children.Add(kind);

        var command = new TextBlock
        {
            Text = row.Command,
            FontSize = 12,
            Opacity = row.IsForced ? 0.95 : 0.7,
            Margin = new Thickness(14, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (row.IsForced)
        {
            command.Foreground = ForcedCommand;
        }

        command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        Grid.SetColumn(command, 3);
        grid.Children.Add(command);
        return grid;
    }

    /// <summary>The name, never cut, its badge and force mark, then the notes, cut with an ellipsis past the row's width.</summary>
    private static Grid NameView(ApproachRow row)
    {
        List<Control> parts = [new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center }];
        if (row.Badge is { } badge)
        {
            parts.Add(Badge(badge, BadgeBrush, null, BadgeBrush, FontWeight.Normal));
        }

        if (row.IsForced)
        {
            parts.Add(ForceBadge());
        }

        if (row.Notes.Count > 0)
        {
            parts.Add(
                new TextBlock
                {
                    Text = $"· {string.Join(" · ", row.Notes)}",
                    FontSize = 12,
                    Opacity = 0.8,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }

        // Every part takes its own width but the last, the notes when there are any, which takes what is left and is cut past it.
        var view = new Grid { VerticalAlignment = VerticalAlignment.Center };
        for (int index = 0; index < parts.Count; index++)
        {
            view.ColumnDefinitions.Add(new ColumnDefinition((index == parts.Count - 1) ? GridLength.Star : GridLength.Auto));
            parts[index].Margin = new Thickness((index == 0) ? 0 : 6, 0, 0, 0);
            Grid.SetColumn(parts[index], index);
            view.Children.Add(parts[index]);
        }

        return view;
    }

    /// <summary>The force mark a forced row, and a forced verb's title line, carry after the name.</summary>
    public static Border ForceBadge() => Badge(ApproachRow.ForceMark, ForceLine, ForceFill, ForceText, FontWeight.SemiBold);

    private static Border Badge(string text, IBrush line, IBrush? fill, IBrush foreground, FontWeight weight) =>
        new()
        {
            BorderBrush = line,
            Background = fill,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0),
            // Hugs its text: the last part of the name view sits in a star column and would otherwise stretch across the row.
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = weight,
                Foreground = foreground,
            },
        };
}
