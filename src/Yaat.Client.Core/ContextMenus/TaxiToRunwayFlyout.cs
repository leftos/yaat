using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The Taxi to runway flyout over the host's runway ends (<see cref="TaxiToRunwayMenu.RunwayEnds"/>), one builder for the
/// All Commands submenu and the quick-command strip's flyout: a <see cref="FlyoutTabStrip"/> with one tab per runway end,
/// in the host's order (<see cref="TabLabel"/>), above the selected tab's rows. A tab's rows are, per runway entry, its
/// title line (<see cref="TitleText"/>) and then its variants as two-line rows (<see cref="MenuTwoLineRow"/>), a separator
/// between the For departure and Hold short groups, then the end's presets in the same two-line look. The host is asked
/// once, when the flyout first opens; the assigned and departure runways come searched. A tab whose end has not been
/// searched starts its runway's search when chosen (<see cref="TaxiToRunwayEndList.SearchRunwayOf"/>): that tab and its
/// runway's other end show the spinner and the panel reads <see cref="SearchingText"/> until the rows land; an end found
/// empty, or whose search failed or no longer applies, is dimmed <c>none</c> and its panel reads
/// <see cref="NoRunwayEntryText"/>. A tab whose search failed or no longer applies searches again only on a deliberate
/// pick: a left press on it, choosing it from another tab by UI Automation's invoke, or Enter while it is selected;
/// arrowing onto it, or reopening the flyout on it, shows its <c>none</c> without searching. Keyboard focus lands on the
/// first row; Up/Down and Tab/Shift+Tab walk the rows, skipping titles and stopping at either end; Left/Right choose the
/// previous or next tab, without wrapping, and focus its first row; the tabs never take focus, and a tab with no row
/// leaves focus on the panel. A row sends its command and closes the flyout and the menu, and previews its own route
/// while pointed at or focused; choosing another tab or closing the flyout clears the preview. Nothing refills while it
/// is open.
/// </summary>
public static class TaxiToRunwayFlyout
{
    /// <summary>What a tab's panel reads while its runway's search runs.</summary>
    public const string SearchingText = "Finding runway entries…";

    /// <summary>What a tab's panel reads when its end has no row, and what the flyout reads with no runway end at all.</summary>
    public const string NoRunwayEntryText = "No runway entry found";

    /// <summary>The tallest the rows panel grows below the tabs before it scrolls.</summary>
    public const double RowsMaxHeight = 420;

    private static readonly ILogger Log = AppLog.CreateLogger("TaxiToRunwayFlyout");

    private static readonly IImmutableSolidColorBrush RunwayBadgeBrush = new ImmutableSolidColorBrush(Color.Parse("#E8A33D"));
    private static readonly IImmutableSolidColorBrush TitleBrush = new ImmutableSolidColorBrush(Color.Parse("#A4ADB8"));
    private static readonly IImmutableSolidColorBrush NameBrush = new ImmutableSolidColorBrush(Color.Parse("#E4E7EB"));

    private static readonly FuncDataTemplate<MenuTwoLineRow> RowTemplate = new((row, _) => MenuTwoLineRowTemplate.Build(row));

    /// <summary>The class of the menu row that holds the whole panel, kept at its resting background.</summary>
    private const string PanelRowClass = "taxi-to-runway-panel";

    /// <summary>
    /// The Taxi to runway entry labelled <paramref name="label"/>: one menu row holding the tabs and the rows, filled when
    /// the flyout or the submenu first opens.
    /// </summary>
    /// <param name="label">The entry's text.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the runway ends come from and the rows send through.</param>
    public static MenuItem BuildTaxiToRunway(string label, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        QuickCommandStrip.FollowInFlyout(entry, new TaxiToRunwayContent(entry, context, host));
        return entry;
    }

    /// <summary>A runway end's tab text: <c>30 assigned</c>, <c>28L departure</c>, or the bare designator.</summary>
    public static string TabLabel(TaxiToRunwayEnd end) =>
        end.Role switch
        {
            TaxiToRunwayEndRole.Assigned => $"{end.Designator} assigned",
            TaxiToRunwayEndRole.Departure => $"{end.Designator} departure",
            _ => end.Designator,
        };

    /// <summary>
    /// A runway entry's title as one line: the badge, the name, why it is offered, the runway it leaves ahead, the via and
    /// the distance (<c>RW At W4 · nearest · ~7,600 ft avail · via S T V W4 · ~2,300 ft</c>).
    /// </summary>
    public static string TitleText(TaxiRouteRow row) => $"{row.Badge} {row.Name} · {TitleDetail(row)} · {HoldShortLadder.FeetText(row.DistanceFt)}";

    private static string TitleDetail(TaxiRouteRow row)
    {
        string? available = (row.AvailableFt is { } availableFt) ? $"{HoldShortLadder.FeetText(availableFt)} avail" : null;
        string?[] detail = [row.Reason, available, row.Via];
        return string.Join(" · ", detail.OfType<string>());
    }

    /// <summary>A runway entry's title line: the RW badge, the name over the dimmed detail, the distance at the right.</summary>
    private static Grid Title(TaxiRouteRow row)
    {
        FontWeight weight = row.IsHighlighted ? FontWeight.Bold : FontWeight.Normal;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(11, 7, 11, 1) };
        AutomationProperties.SetName(grid, TitleText(row));
        grid.Children.Add(
            new Border
            {
                BorderBrush = RunwayBadgeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = row.Badge,
                    FontSize = 10.5,
                    Foreground = RunwayBadgeBrush,
                },
            }
        );
        var text = new TextBlock
        {
            FontSize = 11.5,
            Foreground = TitleBrush,
            FontWeight = weight,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Inlines =
            [
                new Run(row.Name)
                {
                    FontSize = 12.5,
                    FontWeight = row.IsHighlighted ? FontWeight.Bold : FontWeight.SemiBold,
                    Foreground = row.IsHighlighted ? Brushes.White : NameBrush,
                },
                new Run($" · {TitleDetail(row)}"),
            ],
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var distance = new TextBlock
        {
            Text = HoldShortLadder.FeetText(row.DistanceFt),
            FontSize = 11.5,
            FontWeight = weight,
            Foreground = row.IsHighlighted ? Brushes.White : TitleBrush,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(distance, 2);
        grid.Children.Add(distance);
        return grid;
    }

    private static TextBlock StatusLine(string text) =>
        new()
        {
            Text = text,
            FontSize = 12.5,
            Foreground = TitleBrush,
            Padding = new Thickness(11, 8),
        };

    /// <summary>
    /// The flyout's content on whichever surface shows it: the host's runway ends, asked for once, the selected tab's rows,
    /// the keys and the previews.
    /// </summary>
    private sealed class TaxiToRunwayContent : IStripFlyoutContent
    {
        private readonly MenuItem _entry;
        private readonly MenuContext _context;
        private readonly IMenuHost _host;
        private readonly TaxiToRunwayPanel _panel = new();

        /// <summary>The runways whose search this flyout started: an end of one still not searched once it lands read <c>none</c>.</summary>
        private readonly HashSet<RunwayIdentifier> _asked = [];

        private TaxiToRunwayEndList? _ends;
        private Action? _closeFlyout;
        private bool _open;

        /// <summary>Whether Left or Right is changing the tab, a pass that never retries a failed search.</summary>
        private bool _arrowing;

        /// <summary>The selected tab's index as the strip's latest pointer press began, before any tab took it.</summary>
        private int _selectedAtPress = -1;

        public TaxiToRunwayContent(MenuItem entry, MenuContext context, IMenuHost host)
        {
            _entry = entry;
            _context = context;
            _host = host;
            _panel.Strip.SelectionChanged += (_, _) => OnTabChosen();
            // A tab marks its left press handled; a press on the tab already selected changes no selection, so it is seen here.
            _panel.Strip.AddHandler(
                InputElement.PointerPressedEvent,
                (_, _) => _selectedAtPress = _panel.Strip.SelectedIndex,
                RoutingStrategies.Tunnel,
                handledEventsToo: true
            );
            _panel.Strip.AddHandler(InputElement.PointerPressedEvent, OnStripPressed, RoutingStrategies.Bubble, handledEventsToo: true);
            _panel.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            // A menu row takes keyboard focus back from whatever inside it gets it, so the row holding the panel is not
            // focusable, and focus stays on the row button or the panel's scroll host.
            var panelRow = new MenuItem
            {
                Header = _panel,
                StaysOpenOnClick = true,
                Focusable = false,
            };
            panelRow.Classes.Add(PanelRowClass);
            panelRow.Styles.Add(FlyoutPanelParts.PlainPanelRowStyle(PanelRowClass));
            panelRow.Click += (_, e) => e.Handled = true;
            entry.Items.Add(panelRow);
            entry.PropertyChanged += (_, e) =>
            {
                if (e.Property != MenuItem.IsSubMenuOpenProperty)
                {
                    return;
                }

                if (entry.IsSubMenuOpen)
                {
                    Open(null);
                }
                else
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
            _host.SetRoutePreview(null);
        }

        private void Open(Action? close)
        {
            _closeFlyout = close;
            _open = true;
            _ends ??= _host.GetTaxiToRunwayChoices(_context.Callsign).RunwayEnds;
            _panel.Strip.SetTabs([.. _ends.Ends.Select(TabOf)]);
            _panel.Strip.IsVisible = _ends.Ends.Count > 0;
            ShowSelected(retryFailed: false);
        }

        private void OnTabChosen()
        {
            _host.SetRoutePreview(null);
            ShowSelected(retryFailed: !_arrowing);
        }

        /// <summary>
        /// A left press on the tab already selected retries its failed search; a press that chose another tab already
        /// searched it, through <see cref="OnTabChosen"/>.
        /// </summary>
        private void OnStripPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.Handled && (_panel.Strip.SelectedIndex == _selectedAtPress))
            {
                RetrySelectedIfFailed();
            }
        }

        /// <summary>Searches the selected tab's runway again when its search failed or no longer applies; false when it did not.</summary>
        private bool RetrySelectedIfFailed()
        {
            if ((_ends is not { } ends) || (Selected(ends) is not { } end) || !SearchFailed(end))
            {
                return false;
            }

            ShowSelected(retryFailed: true);
            return true;
        }

        /// <summary>Whether this flyout started <paramref name="end"/>'s runway search and it landed with no rows for it.</summary>
        private bool SearchFailed(TaxiToRunwayEnd end) => (end.State == TaxiToRunwayEndState.NotSearched) && _asked.Contains(end.Runway);

        /// <summary>
        /// Shows the selected tab, starting its runway's search when it has not run, or, with <paramref name="retryFailed"/>,
        /// when it failed, and focuses its first row.
        /// </summary>
        private void ShowSelected(bool retryFailed)
        {
            if ((_ends is not { } ends) || (Selected(ends) is not { } end))
            {
                _panel.Show([StatusLine(NoRunwayEntryText)], [], [NoRunwayEntryText]);
                FocusFirstLater();
                return;
            }

            if ((end.State == TaxiToRunwayEndState.NotSearched) && (retryFailed || !_asked.Contains(end.Runway)))
            {
                StartSearch(ends, end);
            }

            RefreshTabs(ends);
            // Starting the search replaces the end in the list, so the end shown is read again.
            if (Selected(ends) is { } current)
            {
                Render(current);
            }

            FocusFirstLater();
        }

        private TaxiToRunwayEnd? Selected(TaxiToRunwayEndList ends) =>
            ((_panel.Strip.SelectedIndex >= 0) && (_panel.Strip.SelectedIndex < ends.Ends.Count)) ? ends.Ends[_panel.Strip.SelectedIndex] : null;

        private void StartSearch(TaxiToRunwayEndList ends, TaxiToRunwayEnd end)
        {
            _asked.Add(end.Runway);
            _ = LandAsync(ends, ends.SearchRunwayOf(end.Designator), end.Runway);
        }

        /// <summary>
        /// Once <paramref name="search"/> lands, brings the tabs up to date and, while the flyout is open and a tab of
        /// <paramref name="runway"/> is still selected, shows its rows and focuses the first; a closed flyout drops the update.
        /// An update that fails is logged; the search logs its own failure (<see cref="TaxiToRunwayEndList.SearchRunwayOf"/>).
        /// </summary>
        private async Task LandAsync(TaxiToRunwayEndList ends, Task search, RunwayIdentifier runway)
        {
            await search;
            if (!_open)
            {
                return;
            }

            try
            {
                RefreshTabs(ends);
                if ((Selected(ends) is { } selected) && (selected.Runway == runway))
                {
                    Render(selected);
                    FocusFirstLater();
                }
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Taxi to runway: showing the rows of runway {Runway} once its search landed failed", runway);
            }
        }

        private void RefreshTabs(TaxiToRunwayEndList ends)
        {
            for (int index = 0; index < ends.Ends.Count; index++)
            {
                _panel.Strip.SetState(index, StateOf(ends.Ends[index]));
            }
        }

        private FlyoutTab TabOf(TaxiToRunwayEnd end) => new(TabLabel(end), StateOf(end));

        private FlyoutTabState StateOf(TaxiToRunwayEnd end) =>
            end.State switch
            {
                TaxiToRunwayEndState.Searching => FlyoutTabState.Searching,
                TaxiToRunwayEndState.Ready => FlyoutTabState.Ready,
                TaxiToRunwayEndState.Empty => FlyoutTabState.Empty,
                _ => _asked.Contains(end.Runway) ? FlyoutTabState.Empty : FlyoutTabState.Ready,
            };

        private void Render(TaxiToRunwayEnd end)
        {
            if (end.State == TaxiToRunwayEndState.Searching)
            {
                _panel.Show([StatusLine(SearchingText)], [], [SearchingText]);
                return;
            }

            if (end.State != TaxiToRunwayEndState.Ready)
            {
                _panel.Show([StatusLine(NoRunwayEntryText)], [], [NoRunwayEntryText]);
                return;
            }

            var shown = new PanelContent();
            foreach (TaxiRouteRow entry in end.Rows.Where(row => row.Variants.Count > 0))
            {
                shown.Add(Title(entry), $"{TitleText(entry)}{(entry.IsHighlighted ? " [bold]" : "")}");
                AddVariants(shown, entry);
            }

            List<TaxiRouteRow> presets = [.. end.Rows.Where(row => row.Variants.Count == 0)];
            if ((presets.Count > 0) && (shown.Lines.Count > 0))
            {
                shown.Add(FlyoutPanelParts.SeparatorLine(), "---");
            }

            foreach (TaxiRouteRow preset in presets)
            {
                AddRow(
                    shown,
                    new MenuTwoLineRow(QuickCommandGlyphs.For(MenuIds.GroundTaxiPreset)!, preset.Name, preset.Command, false),
                    preset.Preview
                );
            }

            _panel.Show(shown.Controls, shown.Buttons, shown.Lines);
        }

        private void AddVariants(PanelContent shown, TaxiRouteRow entry)
        {
            foreach (MenuCommandChoice variant in entry.Variants)
            {
                if (ReferenceEquals(variant, MenuCommandChoice.Separator))
                {
                    shown.Add(FlyoutPanelParts.SeparatorLine(), "---");
                    continue;
                }

                string command =
                    variant.Command
                    ?? throw new InvalidOperationException($"Taxi to runway: the variant '{variant.Label}' of {entry.Name} sends no command.");
                var row = new MenuTwoLineRow(QuickCommandGlyphs.For(MenuIds.GroundTaxiToRunway)!, variant.Label, command, false);
                AddRow(shown, row, variant.Preview ?? entry.Preview);
            }
        }

        private void AddRow(PanelContent shown, MenuTwoLineRow row, TaxiRoute preview)
        {
            var button = new Button
            {
                Content = row,
                ContentTemplate = RowTemplate,
                Padding = new Thickness(11, 5),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            MenuCommandText.SetCommand(button, row.Command);
            AutomationProperties.SetName(button, row.ToString());
            button.Click += async (_, e) =>
            {
                e.Handled = true;
                await ChooseAsync(row.Command);
            };
            button.PointerEntered += (_, _) => _host.SetRoutePreview(preview);
            // The flyout's own focus handling may mark focus events handled before handlers added the plain way see them.
            button.AddHandler(InputElement.GotFocusEvent, (_, _) => _host.SetRoutePreview(preview), handledEventsToo: true);
            shown.Add(button, row.ToString());
            shown.Buttons.Add(button);
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

        /// <summary>Focuses the first row once the panel has laid out its new rows, or the panel itself when it has none.</summary>
        private void FocusFirstLater() =>
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!_open)
                    {
                        return;
                    }

                    if (_panel.Rows.Count > 0)
                    {
                        FocusRow(_panel.Rows[0]);
                    }
                    else
                    {
                        _panel.Scroll.Focus(NavigationMethod.Directional);
                    }
                },
                DispatcherPriority.Loaded
            );

        private static void FocusRow(Button row)
        {
            row.Focus(NavigationMethod.Directional);
            row.BringIntoView();
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left or Key.Right:
                    e.Handled = true;
                    _arrowing = true;
                    try
                    {
                        _panel.Strip.Move((e.Key == Key.Left) ? -1 : 1);
                    }
                    finally
                    {
                        _arrowing = false;
                    }

                    break;
                case Key.Enter:
                    e.Handled = RetrySelectedIfFailed();
                    break;
                case Key.Up or Key.Down or Key.Tab:
                    e.Handled = true;
                    bool forward = (e.Key == Key.Down) || ((e.Key == Key.Tab) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                    Step(forward ? 1 : -1);
                    break;
            }
        }

        /// <summary>Focuses the row after (<paramref name="step"/> 1) or before (-1) the focused one, staying put at either end.</summary>
        private void Step(int step)
        {
            IReadOnlyList<Button> rows = _panel.Rows;
            if (rows.Count == 0)
            {
                return;
            }

            int at = -1;
            for (int index = 0; index < rows.Count; index++)
            {
                if (rows[index].IsKeyboardFocusWithin)
                {
                    at = index;
                }
            }

            int next = (at < 0) ? 0 : at + step;
            if ((next >= 0) && (next < rows.Count))
            {
                FocusRow(rows[next]);
            }
        }
    }

    /// <summary>A tab's panel content as it is built: the controls in order, the rows among them, and each control's text.</summary>
    private sealed class PanelContent
    {
        public List<Control> Controls { get; } = [];

        public List<Button> Buttons { get; } = [];

        public List<string> Lines { get; } = [];

        public void Add(Control control, string line)
        {
            Controls.Add(control);
            Lines.Add(line);
        }
    }
}

/// <summary>
/// The Taxi to runway flyout's one menu row: the tab strip pinned above a scrolling panel of the selected tab's rows, at
/// most <see cref="MenuCommandRow.RowMaxWidth"/> wide and <see cref="TaxiToRunwayFlyout.RowsMaxHeight"/> tall below
/// the tabs. Its scroll host takes keyboard focus while the selected tab has no row.
/// </summary>
public sealed class TaxiToRunwayPanel : StackPanel
{
    /// <summary>The scroll host's name for assistive technology.</summary>
    public const string EntriesName = "Taxi to runway entries";

    private readonly StackPanel _rows = new();
    private List<Button> _buttons = [];
    private List<string> _lines = [TaxiToRunwayFlyout.SearchingText];

    internal TaxiToRunwayPanel()
    {
        MaxWidth = MenuCommandRow.RowMaxWidth;
        Scroll = new ScrollViewer
        {
            Content = _rows,
            MaxHeight = TaxiToRunwayFlyout.RowsMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = true,
        };
        AutomationProperties.SetName(Scroll, EntriesName);
        _rows.Children.Add(new TextBlock { Text = TaxiToRunwayFlyout.SearchingText });
        Children.Add(Strip);
        Children.Add(Scroll);
    }

    /// <summary>The runway ends' tabs.</summary>
    public FlyoutTabStrip Strip { get; } = new();

    /// <summary>The scrolling host of the selected tab's rows.</summary>
    public ScrollViewer Scroll { get; }

    /// <summary>The selected tab's sending rows, in order; titles and separators are not among them.</summary>
    public IReadOnlyList<Button> Rows => _buttons;

    /// <summary>
    /// The panel as text, one line per element: a title (<see cref="TaxiToRunwayFlyout.TitleText"/>, <c> [bold]</c> after a
    /// highlighted one), a row (<see cref="MenuTwoLineRow.ToString"/>), <c>---</c> for a separator, or the panel's status.
    /// </summary>
    public IReadOnlyList<string> Lines => _lines;

    internal void Show(List<Control> controls, List<Button> buttons, List<string> lines)
    {
        _rows.Children.Clear();
        _rows.Children.AddRange(controls);
        _buttons = buttons;
        _lines = lines;
        Scroll.Offset = default;
    }
}
