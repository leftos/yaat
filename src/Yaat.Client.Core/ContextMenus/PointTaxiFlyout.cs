using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The ground point menu's taxi flyouts, one builder each for the strip button and its text row. Taxi here over the host's
/// choices for the clicked node (<see cref="IMenuHost.GetTaxiChoices"/>): several routes open a flyout of two-line rows
/// (<see cref="MenuTwoLineRow"/>), each the route's label over the whole command with its distance at the right, in the
/// host's order; a route with variants is a title line (its label bold, what it crosses, its distance) over its variants;
/// the host's group of several routes (a lone choice with variants and no route of its own) is unwrapped; a lone route with one
/// command is an item that sends it at once; and no route leaves a disabled row the strip shows as a faded, inert button
/// labelled <c>Taxi here — No route found</c>. Taxi to runway at a runway-surface click: one entry whose flyout has a tab
/// per end of each runway under the click, in runway order, and in each tab the host's hold-short targets for that end
/// (<see cref="IMenuHost.GetRunwayHoldShortTargets"/>, in the host's order), each of the target's routes a Taxi to runway
/// title line (<see cref="TaxiToRunwayFlyout.TitleText"/>: <c>RW At W3 · nearest · via TE U W3 · ~3,350 ft</c>, with
/// <c> · crosses 28L</c> after the via for a route that crosses another runway on the way, the target's first route
/// bold), built when the menu is, over its variants; a target with no route is a dimmed title (<c>RW At W2 · near click</c>) over a disabled
/// <see cref="NoRouteText"/>. Both are built once per right-click and never
/// refill. Keyboard focus lands on the first row; Up/Down and Tab/Shift+Tab walk the rows, skipping titles and stopping at
/// either end; Left/Right choose the previous or next tab, without wrapping. A row sends its command and closes the flyout
/// and the menu, and previews its own route while pointed at or focused; choosing another tab or closing the flyout
/// clears the preview.
/// </summary>
public static class PointTaxiFlyout
{
    /// <summary>The disabled row under a target the host found no route to.</summary>
    public const string NoRouteText = "No route found";

    /// <summary>The Taxi here flyout's scroll host's name for assistive technology.</summary>
    public const string TaxiHereRoutesName = "Taxi here routes";

    /// <summary>The class of the menu row that holds the whole panel, kept at its resting background.</summary>
    private const string PanelRowClass = "point-taxi-panel";

    /// <summary>
    /// Taxi here over <paramref name="choices"/>: null for none; a lone route with one command, an item sending it; a lone
    /// row with neither command nor variants (<see cref="NoRouteText"/>), a disabled row the strip shows faded and inert;
    /// otherwise the flyout over the routes, labelled by the lone route, or <c>Taxi here</c> for several. A lone choice with
    /// variants and no route of its own is the host's group of several routes, and its variants are the routes.
    /// </summary>
    /// <param name="choices">The host's Taxi here answer for the clicked node.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the rows send through and preview on.</param>
    internal static MenuItem? BuildTaxiHere(IReadOnlyList<MenuCommandChoice> choices, MenuContext context, IMenuHost host)
    {
        if (choices.Count == 0)
        {
            return null;
        }

        if (choices.Count == 1)
        {
            MenuCommandChoice only = choices[0];
            if (ReferenceEquals(only, MenuCommandChoice.Separator))
            {
                throw new InvalidOperationException(
                    $"IMenuHost.GetTaxiChoices answered a lone separator for {context.Callsign}; it answers routes or a \"No route found\" row."
                );
            }

            if (only.Children.Count == 0)
            {
                return (only.Command is { } command) ? SendAtOnce(only, command, context, host) : Inert(only.Label);
            }
        }

        string label = (choices.Count == 1) ? choices[0].Label : "Taxi here";
        return Flyout(label, [TaxiHereTab(Unwrapped(choices))], context, host);
    }

    /// <summary>
    /// The routes in the host's taxi answer: the variants of a lone choice that has variants and no route of its own (the
    /// host's group of several routes), else the answer itself.
    /// </summary>
    private static IReadOnlyList<MenuCommandChoice> Unwrapped(IReadOnlyList<MenuCommandChoice> choices) =>
        ((choices.Count == 1) && (choices[0].Children.Count > 0) && (choices[0].Preview is null)) ? choices[0].Children : choices;

    /// <summary>
    /// Taxi to runway at <paramref name="point"/>: one tab per end of each runway under the click that has a hold-short
    /// target, or null when none has.
    /// </summary>
    /// <param name="point">The runway-surface click.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the targets and routes come from and the rows send through.</param>
    internal static MenuItem? BuildTaxiToRunway(MenuPoint point, MenuContext context, IMenuHost host)
    {
        List<PointTab> tabs = [];
        foreach (string runwayName in point.SurfaceRunways)
        {
            var ids = RunwayIdentifier.Parse(runwayName);
            foreach (string end in new[] { ids.End1, ids.End2 }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                List<TargetRoutes> targets =
                [
                    .. host.GetRunwayHoldShortTargets(context.Callsign, runwayName, end, point.Position)
                        .Select(target => RoutesTo(target, RoutesOf(Unwrapped(host.GetTaxiChoices(context.Callsign, target.Node, end))), end)),
                ];
                if (targets.Count > 0)
                {
                    bool routed = targets.Any(t => t.Routes.Count > 0);
                    tabs.Add(
                        new PointTab(
                            RunwayIdentifier.ToDisplayDesignator(end),
                            routed ? FlyoutTabState.Ready : FlyoutTabState.Empty,
                            rows => AddTargets(rows, targets)
                        )
                    );
                }
            }
        }

        return (tabs.Count > 0) ? Flyout("Taxi to runway", tabs, context, host) : null;
    }

    /// <summary><paramref name="target"/>'s routes to <paramref name="end"/>, each with its title line, the first bold.</summary>
    /// <exception cref="InvalidOperationException">A route carries no preview, or neither it nor a variant sends a command.</exception>
    private static TargetRoutes RoutesTo(RunwayHoldShortTarget target, List<MenuCommandChoice> routes, string end) =>
        new(target, [.. routes.Select((route, index) => (route, TitleRow(target, route, index == 0, end)))]);

    /// <summary>The routes among <paramref name="choices"/>: those that send a command or offer variants.</summary>
    private static List<MenuCommandChoice> RoutesOf(IReadOnlyList<MenuCommandChoice> choices) =>
        [.. choices.Where(c => !ReferenceEquals(c, MenuCommandChoice.Separator) && ((c.Command is not null) || (c.Children.Count > 0)))];

    /// <summary>The distance a route's preview runs, as the taxi rows show one (<c>~2,300 ft</c>); null without a preview.</summary>
    private static string? DistanceText(TaxiRoute? route) => (route is null) ? null : HoldShortLadder.FeetText(DistanceFt(route));

    private static double DistanceFt(TaxiRoute route) => TugMovePlanner.NoteDistanceFt(route.TotalDistanceFt);

    /// <summary>A host route label's taxiways as a title shows them: <c>Taxi via T U W</c> reads <c>via T U W</c>.</summary>
    private static string ViaOf(string label) => label.StartsWith("Taxi ", StringComparison.Ordinal) ? label["Taxi ".Length..] : label;

    /// <summary>
    /// What a route crosses, as its title says it: <c>crosses 28L, 15</c>, the runways of its preview's runway-crossing hold
    /// shorts in route order, each once by its first end as the variant rows show it; null for a route that crosses none or
    /// has no preview. The bar at the route's last node is where it stops, never a crossing, and nor is any bar of
    /// <paramref name="destinationEnd"/>'s runway (either end, however padded), as <c>BuildTaxiDestVariants</c> counts them.
    /// </summary>
    /// <param name="route">The route's preview.</param>
    /// <param name="destinationEnd">The runway end the route taxis to, or null for a route that ends elsewhere.</param>
    private static string? CrossesText(TaxiRoute? route, string? destinationEnd)
    {
        if (route is null)
        {
            return null;
        }

        int destinationNodeId = (route.Segments.Count > 0) ? route.Segments[^1].ToNodeId : -1;
        List<string> runways =
        [
            .. route
                .HoldShortPoints.Where(hs =>
                    (hs.Reason == HoldShortReason.RunwayCrossing) && (hs.TargetName is not null) && (hs.NodeId != destinationNodeId)
                )
                .Select(hs => RunwayIdentifier.Parse(hs.TargetName!))
                .Where(runway => (destinationEnd is null) || !runway.Contains(destinationEnd))
                .Select(runway => RunwayIdentifier.ToDisplayDesignator(runway.End1))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
        return (runways.Count > 0) ? $"crosses {string.Join(", ", runways)}" : null;
    }

    /// <summary>A text and, when there is one, <c> · </c> and <paramref name="detail"/> after it.</summary>
    private static string WithDetail(string text, string? detail) => (detail is null) ? text : $"{text} · {detail}";

    private static PointTab TaxiHereTab(IReadOnlyList<MenuCommandChoice> routes) =>
        new(null, FlyoutTabState.Ready, rows => AddChoices(rows, routes, null, QuickCommandGlyphs.ForPoint(MenuIds.PointTaxiHere), true));

    /// <summary>
    /// <paramref name="choices"/> as rows: a separator line, a two-line row for a choice that sends a command (its distance
    /// at the right when <paramref name="withDistance"/>), a title line over the variants of one that has them, and a
    /// disabled row for one with neither. A row previews its own route, else <paramref name="inherited"/>.
    /// </summary>
    private static void AddChoices(
        PointRows rows,
        IReadOnlyList<MenuCommandChoice> choices,
        TaxiRoute? inherited,
        QuickCommandGlyph glyph,
        bool withDistance
    )
    {
        foreach (MenuCommandChoice choice in choices)
        {
            if (ReferenceEquals(choice, MenuCommandChoice.Separator))
            {
                rows.Add(FlyoutPanelParts.SeparatorLine(), "---");
            }
            else if (choice.Children.Count > 0)
            {
                string? crosses = CrossesText(choice.Preview, null);
                string? distance = DistanceText(choice.Preview);
                rows.Add(RouteTitle(choice.Label, crosses, distance), WithDetail(WithDetail(choice.Label, crosses), distance));
                AddChoices(rows, choice.Children, choice.Preview ?? inherited, glyph, false);
            }
            else if (choice.Command is { } command)
            {
                rows.AddRow(
                    new MenuTwoLineRow(glyph, choice.Label, command, false),
                    withDistance ? DistanceText(choice.Preview) : null,
                    choice.Preview ?? inherited
                );
            }
            else
            {
                rows.Add(DisabledRow(choice.Label), $"{choice.Label} [disabled]");
            }
        }
    }

    /// <summary>
    /// A runway end's targets: each route a Taxi to runway title line, the target's first one bold, over its variants (a
    /// route with no variant is its own row); a target with no route a dimmed title over a disabled row.
    /// </summary>
    private static void AddTargets(PointRows rows, List<TargetRoutes> targets)
    {
        QuickCommandGlyph glyph = QuickCommandGlyphs.For(MenuIds.GroundTaxiToRunway)!;
        foreach ((RunwayHoldShortTarget target, IReadOnlyList<(MenuCommandChoice Route, TaxiRouteRow Title)> routes) in targets)
        {
            if (routes.Count == 0)
            {
                string title = WithDetail($"{TaxiRouteRow.RunwayEntryBadge} {target.Name}", target.ReasonText);
                rows.Add(TaxiToRunwayFlyout.DimmedTitle(TaxiRouteRow.RunwayEntryBadge, target.Name, target.ReasonText), $"{title} [dim]");
                rows.Add(DisabledRow(NoRouteText), $"{NoRouteText} [disabled]");
                continue;
            }

            foreach ((MenuCommandChoice route, TaxiRouteRow title) in routes)
            {
                rows.Add(TaxiToRunwayFlyout.Title(title), $"{TaxiToRunwayFlyout.TitleText(title)}{(title.IsHighlighted ? " [bold]" : "")}");
                IReadOnlyList<MenuCommandChoice> variants = (route.Children.Count > 0) ? route.Children : [route];
                AddChoices(rows, variants, route.Preview, glyph, false);
            }
        }
    }

    /// <summary>
    /// A target's route to <paramref name="end"/> as a Taxi to runway title: the target's name and reasons, the route's
    /// taxiways and what it crosses on the way, and its distance (<c>RW At W3 · nearest · via TE U W3 · ~3,350 ft</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The host's route carries no preview, or neither it nor a variant sends a command.</exception>
    private static TaxiRouteRow TitleRow(RunwayHoldShortTarget target, MenuCommandChoice route, bool isFirst, string end)
    {
        TaxiRoute preview =
            route.Preview
            ?? throw new InvalidOperationException($"Taxi to runway: the route '{route.Label}' to {target.Label} carries no route preview.");
        string command =
            route.Command
            ?? route.Children.Select(c => c.Command).OfType<string>().FirstOrDefault()
            ?? throw new InvalidOperationException($"Taxi to runway: the route '{route.Label}' to {target.Label} sends no command.");
        return new TaxiRouteRow(
            TaxiRouteRow.RunwayEntryBadge,
            target.Name,
            target.ReasonText,
            null,
            isFirst,
            WithDetail(ViaOf(route.Label), CrossesText(preview, end)),
            DistanceFt(preview),
            command,
            preview,
            route.Children
        );
    }

    /// <summary>
    /// A Taxi here route's title line, highlighted as the mock draws it: its label bold, then what it crosses, and its
    /// distance at the right when it has one.
    /// </summary>
    private static Grid RouteTitle(string label, string? crosses, string? distance)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(11, 7, 11, 1) };
        AutomationProperties.SetName(grid, WithDetail(WithDetail(label, crosses), distance));
        var text = new TextBlock
        {
            FontSize = 11.5,
            Foreground = FlyoutPanelParts.TitleBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Inlines =
            [
                new Run(label)
                {
                    FontSize = 12.5,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White,
                },
            ],
        };
        if (crosses is not null)
        {
            text.Inlines.Add(new Run($" · {crosses}"));
        }

        grid.Children.Add(text);
        if (distance is not null)
        {
            TextBlock right = DistanceView(distance);
            right.FontWeight = FontWeight.Bold;
            right.Foreground = Brushes.White;
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }

        return grid;
    }

    private static TextBlock DisabledRow(string text) =>
        new()
        {
            Text = text,
            FontSize = 12.5,
            Opacity = MenuGlyphRowTemplate.DimmedOpacity,
            Padding = new Thickness(11, 5),
        };

    private static TextBlock DistanceView(string distance) =>
        new()
        {
            Text = distance,
            FontSize = 11.5,
            Foreground = FlyoutPanelParts.TitleBrush,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>A lone route with one command: an item that sends it at once and previews its route while pointed at.</summary>
    private static MenuItem SendAtOnce(MenuCommandChoice choice, string command, MenuContext context, IMenuHost host)
    {
        MenuItem item = MenuCatalog.BuildSend(choice.Label, command, context, host);
        if (choice.Preview is { } preview)
        {
            item.PointerEntered += (_, _) => host.SetRoutePreview(preview);
        }

        return item;
    }

    /// <summary>The disabled row naming why there is no route, whose strip button stays faded and inert.</summary>
    private static MenuItem Inert(string label)
    {
        var item = new MenuItem { Header = label, IsEnabled = false };
        QuickCommandStrip.MarkInert(item, $"Taxi here — {label}");
        return item;
    }

    private static MenuItem Flyout(string label, IReadOnlyList<PointTab> tabs, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        QuickCommandStrip.FollowInFlyout(entry, new PointFlyoutContent(entry, tabs, context, host));
        return entry;
    }

    /// <summary>A Taxi to runway target and its routes, each with its title line; no route for a target the host cannot reach.</summary>
    private sealed record TargetRoutes(RunwayHoldShortTarget Target, IReadOnlyList<(MenuCommandChoice Route, TaxiRouteRow Title)> Routes);

    /// <summary>One tab of a point flyout: its label (null for the one unlabelled tab of Taxi here), its state and its rows.</summary>
    private sealed record PointTab(string? Label, FlyoutTabState State, Action<PointRows> Fill);

    /// <summary>A tab's panel content as it is built: the controls in order, the sending rows among them, and each control's text.</summary>
    private sealed class PointRows(Func<MenuTwoLineRow, string?, TaxiRoute?, Button> row)
    {
        public List<Control> Controls { get; } = [];

        public List<Button> Buttons { get; } = [];

        public List<string> Lines { get; } = [];

        public void Add(Control control, string line)
        {
            Controls.Add(control);
            Lines.Add(line);
        }

        /// <summary>Adds a sending row; its text is the row's, then <c> · ~N ft</c> when it shows a distance.</summary>
        public void AddRow(MenuTwoLineRow twoLine, string? distance, TaxiRoute? preview)
        {
            Button button = row(twoLine, distance, preview);
            Add(button, (distance is null) ? twoLine.ToString() : $"{twoLine} · {distance}");
            Buttons.Add(button);
        }
    }

    /// <summary>
    /// A point flyout's content on whichever surface shows it, the strip's flyout or the text row's submenu: its tabs, the
    /// selected tab's rows, the keys and the previews.
    /// </summary>
    private sealed class PointFlyoutContent : IStripFlyoutContent
    {
        private readonly MenuItem _entry;
        private readonly IReadOnlyList<PointTab> _tabs;
        private readonly MenuContext _context;
        private readonly IMenuHost _host;
        private readonly TaxiToRunwayPanel _panel = new();
        private Action? _closeFlyout;
        private bool _open;

        public PointFlyoutContent(MenuItem entry, IReadOnlyList<PointTab> tabs, MenuContext context, IMenuHost host)
        {
            _entry = entry;
            _tabs = tabs;
            _context = context;
            _host = host;
            if (tabs.All(tab => tab.Label is null))
            {
                AutomationProperties.SetName(_panel.Scroll, TaxiHereRoutesName);
            }

            _panel.Strip.SelectionChanged += (_, _) => OnTabChosen();
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
            _panel.Strip.SetTabs([.. _tabs.Select(tab => new FlyoutTab(tab.Label ?? "", tab.State))]);
            _panel.Strip.IsVisible = _tabs.Any(tab => tab.Label is not null);
            ShowSelected();
        }

        private void OnTabChosen()
        {
            _host.SetRoutePreview(null);
            ShowSelected();
        }

        private void ShowSelected()
        {
            int index = _panel.Strip.SelectedIndex;
            if ((index < 0) || (index >= _tabs.Count))
            {
                return;
            }

            var rows = new PointRows(Row);
            _tabs[index].Fill(rows);
            _panel.Show(rows.Controls, rows.Buttons, rows.Lines);
            FocusFirstLater();
        }

        private Button Row(MenuTwoLineRow row, string? distance, TaxiRoute? preview)
        {
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            content.Children.Add(MenuTwoLineRowTemplate.Build(row));
            if (distance is not null)
            {
                TextBlock right = DistanceView(distance);
                Grid.SetColumn(right, 1);
                content.Children.Add(right);
            }

            var button = new Button
            {
                Content = content,
                Padding = new Thickness(11, 5),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            MenuCommandText.SetCommand(button, row.Command);
            AutomationProperties.SetName(button, (distance is null) ? row.ToString() : $"{row} · {distance}");
            button.Click += async (_, e) =>
            {
                e.Handled = true;
                await ChooseAsync(row.Command);
            };
            button.PointerEntered += (_, _) => _host.SetRoutePreview(preview);
            // The flyout's own focus handling may mark focus events handled before handlers added the plain way see them.
            button.AddHandler(InputElement.GotFocusEvent, (_, _) => _host.SetRoutePreview(preview), handledEventsToo: true);
            return button;
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
                    _panel.Strip.Move((e.Key == Key.Left) ? -1 : 1);
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
}
