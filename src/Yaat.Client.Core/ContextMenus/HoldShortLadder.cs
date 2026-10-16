using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The Hold short of… route ladder over the host's answer (<see cref="IMenuHost.GetHoldShortChoices"/>), one builder for
/// the All Commands submenu and the quick-command strip's flyout: a title naming the route, a dimmed line saying where the
/// aircraft is now, one stop per bar along the route nearest first, each led by its bar mark (a runway's two amber lines,
/// a taxiway's teal solid-over-dashed lines) and sending its <c>HS</c>, then where the route ends, dimmed and unclickable.
/// With no stop the entry is not built. A stop pointed at or holding keyboard focus previews on the ground views the route
/// from the aircraft to its bar and boxes the bar (no route for the bar the route starts at, while the aircraft is short
/// of it; the bar a holding-short aircraft is held at is named by the line for the aircraft, never a stop); the preview
/// clears when the stop loses both and when the flyout closes. While the quick-command strip's flyout is open the ladder
/// refills once a second (<see cref="FlyoutLiveTick"/>): with the pointer outside it the ladder is rebuilt as the host
/// answers; with the pointer inside it the order freezes, each stop's distance updating in place, a stop no longer on the
/// route staying as a dimmed <see cref="GroundTrafficFlyout.GoneState"/> row that sends nothing and the keyboard skips,
/// and a new stop joining the bottom; when the pointer leaves the ladder catches up at once. The All Commands submenu
/// refills as it opens.
/// </summary>
public static class HoldShortLadder
{
    /// <summary>The ladder's title, left of the route line.</summary>
    public const string Title = "Hold short of";

    /// <summary>The muted line a ladder shows once its stops have all gone while open.</summary>
    public const string NoStopsText = "No bars left on the route.";

    /// <summary>
    /// The Hold short of… entry labelled <paramref name="label"/>: the ladder over the aircraft's route, or null when the
    /// route offers no stop.
    /// </summary>
    /// <param name="label">The entry's text.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the ladder comes from and the stops send through.</param>
    public static MenuItem? Build(string label, MenuContext context, IMenuHost host)
    {
        HoldShortMenu ladder = host.GetHoldShortChoices(context.Callsign);
        if (ladder.Rows.Count == 0)
        {
            return null;
        }

        var entry = new MenuItem { Header = label };
        var list = new LadderList(entry, context, host);
        list.Attach(ladder);
        QuickCommandStrip.FollowInFlyout(entry, list);
        return entry;
    }

    /// <summary>A distance as a ladder row shows it: <c>~1,650 ft</c>.</summary>
    internal static string FeetText(double distanceFt) => $"~{distanceFt.ToString("N0", CultureInfo.InvariantCulture)} ft";

    /// <summary>A stop as the ladder lists it: the stop as last answered, and whether the route has left it since.</summary>
    private sealed record StopEntry(HoldShortChoice Choice, bool IsGone);

    /// <summary>
    /// A ladder held in place while the pointer is inside its flyout: the stops shown when the pointer entered, each keeping
    /// its place while its distance follows the host, keyed by its <c>HS</c> command; a stop the host no longer answers stays
    /// as a gone entry, and a new stop joins the bottom.
    /// </summary>
    private sealed class FrozenStops(IEnumerable<StopEntry> shown)
    {
        private readonly List<StopEntry> _entries = [.. shown];

        /// <summary>The held entries, in order.</summary>
        public List<StopEntry> Entries => [.. _entries];

        /// <summary>Brings every held entry up to <paramref name="stops"/> and adds the new ones at the bottom.</summary>
        public void Update(IReadOnlyList<HoldShortChoice> stops)
        {
            var listed = new Dictionary<string, HoldShortChoice>(StringComparer.Ordinal);
            foreach (HoldShortChoice stop in stops)
            {
                listed.TryAdd(stop.Command, stop);
            }

            var held = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _entries.Count; i++)
            {
                string command = _entries[i].Choice.Command;
                held.Add(command);
                _entries[i] = listed.TryGetValue(command, out HoldShortChoice? now) ? new StopEntry(now, false) : _entries[i] with { IsGone = true };
            }

            _entries.AddRange(stops.Where(stop => held.Add(stop.Command)).Select(stop => new StopEntry(stop, false)));
        }
    }

    /// <summary>
    /// The ladder's rows on whichever surface shows them: filled when built, refilled when the All Commands submenu opens
    /// and, while the strip's flyout is open, once a second, held in place while the pointer is inside the flyout. A stop
    /// keeps its row, by its <c>HS</c> command, across a refill; a row gone sends nothing, even on a click already on its way.
    /// </summary>
    private sealed class LadderList(MenuItem entry, MenuContext context, IMenuHost host) : IStripFlyoutContent
    {
        private readonly MenuItem _title = new() { HeaderTemplate = GroundTrafficTitleTemplate.Instance, IsEnabled = false };
        private readonly MenuItem _me = new() { HeaderTemplate = HoldShortMeRowTemplate.Instance, IsEnabled = false };
        private readonly MenuItem _end = new()
        {
            HeaderTemplate = HoldShortEndRowTemplate.Instance,
            IsEnabled = false,
            Opacity = MenuGlyphRowTemplate.DimmedOpacity,
        };

        private readonly MenuItem _empty = new() { Header = NoStopsText, IsEnabled = false };
        private readonly HashSet<MenuItem> _live = [];
        private Dictionary<string, MenuItem> _rows = new(StringComparer.Ordinal);
        private List<StopEntry> _shown = [];
        private ScopeFeedbackTracker? _feedback;
        private ItemCollection? _flyoutItems;
        private Action? _closeFlyout;
        private FlyoutLiveTick? _tick;
        private FrozenStops? _frozen;

        /// <summary>The bar box and the route to it the stops preview; no aircraft is highlighted.</summary>
        private ScopeFeedbackTracker Feedback => _feedback ??= new ScopeFeedbackTracker(host, StopPreview, highlightsAircraft: false);

        /// <summary>Fills the submenu with <paramref name="ladder"/> and refills it whenever the submenu opens.</summary>
        public void Attach(HoldShortMenu ladder)
        {
            Show(ladder, Live(ladder));
            entry.PropertyChanged += (_, e) =>
            {
                if ((e.Property == MenuItem.IsSubMenuOpenProperty) && entry.IsSubMenuOpen)
                {
                    Refill();
                }
            };
        }

        /// <inheritdoc />
        public void Opened(ItemCollection items, Action close)
        {
            _flyoutItems = items;
            _closeFlyout = close;
            (_tick ??= new FlyoutLiveTick(host.Clock, Refill)).Start();
            Refill();
        }

        /// <inheritdoc />
        public void PointerInside(bool inside)
        {
            if ((_tick?.IsRunning != true) || (inside == (_frozen is not null)))
            {
                return;
            }

            _frozen = inside ? new FrozenStops(_shown) : null;
            if (!inside)
            {
                Refill();
            }
        }

        /// <inheritdoc />
        public void Closed()
        {
            _tick?.Stop();
            _frozen = null;
            _closeFlyout = null;
            Feedback.Clear();
            Refill();
        }

        private static List<StopEntry> Live(HoldShortMenu ladder) => [.. ladder.Rows.Select(stop => new StopEntry(stop, false))];

        private void Refill()
        {
            HoldShortMenu ladder = host.GetHoldShortChoices(context.Callsign);
            if (_frozen is null)
            {
                Show(ladder, Live(ladder));
                return;
            }

            _frozen.Update(ladder.Rows);
            Show(ladder, _frozen.Entries);
        }

        private void Show(HoldShortMenu ladder, List<StopEntry> entries)
        {
            MenuItem? focused = _rows.Values.FirstOrDefault(row => row.IsKeyboardFocusWithin);
            _shown = entries;
            List<Control> wanted = Wanted(ladder, entries, focused);
            FlyoutItemSync.Sync(_flyoutItems ?? entry.Items, wanted, FlyoutItemSync.FocusedIn(wanted));
            HandFocusOnIfGone(focused, wanted);
            // The preview follows the live refill alone, as Give way's does.
            if (_tick?.IsRunning == true)
            {
                Feedback.Refresh();
            }
        }

        /// <summary>
        /// The items <paramref name="ladder"/> and <paramref name="entries"/> call for: the title, the line saying where the
        /// aircraft is, the stops (reusing the row of every stop shown before; <paramref name="focused"/> stays in reach for
        /// now even when gone), and the destination row, or the muted <see cref="NoStopsText"/> line with no stop.
        /// </summary>
        private List<Control> Wanted(HoldShortMenu ladder, List<StopEntry> entries, MenuItem? focused)
        {
            SetHeader(_title, new GroundTrafficFlyoutTitle(Title, ladder.RouteLine ?? ""));
            List<Control> wanted = [_title];
            if (ladder.MeLine is { } meLine)
            {
                SetHeader(_me, new HoldShortMeRow(context.Callsign, meLine));
                wanted.Add(_me);
            }

            wanted.AddRange(StopRows(entries, focused));
            if (entries.Count == 0)
            {
                wanted.Add(_empty);
            }
            else if (ladder.Destination is { } destination)
            {
                SetHeader(_end, destination);
                wanted.Add(_end);
            }

            return wanted;
        }

        private List<MenuItem> StopRows(List<StopEntry> entries, MenuItem? focused)
        {
            var rows = new Dictionary<string, MenuItem>(StringComparer.Ordinal);
            List<MenuItem> ordered = [];
            _live.Clear();
            foreach (StopEntry shown in entries)
            {
                string key = shown.Choice.Command;
                MenuItem item = (!rows.ContainsKey(key) && _rows.TryGetValue(key, out MenuItem? kept)) ? kept : NewRow(key);
                rows.TryAdd(key, item);
                ordered.Add(item);
                Present(item, shown, ReferenceEquals(item, focused));
                if (shown.IsGone)
                {
                    Feedback.Unpoint(key);
                }
                else
                {
                    _live.Add(item);
                }
            }

            _rows = rows;
            return ordered;
        }

        /// <summary>
        /// Hands keyboard focus from <paramref name="focused"/> once its stop can no longer hold it, gone or off the route,
        /// to the nearest live stop below it, else above it, else the first live stop, then takes it out of reach.
        /// </summary>
        private void HandFocusOnIfGone(MenuItem? focused, List<Control> wanted)
        {
            if ((focused is null) || _live.Contains(focused))
            {
                return;
            }

            var listed = new HashSet<MenuItem>(_rows.Values);
            List<MenuItem> rows = [.. wanted.OfType<MenuItem>().Where(listed.Contains)];
            int at = rows.IndexOf(focused);
            MenuItem? next =
                (at >= 0)
                    ? rows.Skip(at + 1).FirstOrDefault(_live.Contains) ?? rows.Take(at).LastOrDefault(_live.Contains)
                    : rows.FirstOrDefault(_live.Contains);
            next?.Focus(NavigationMethod.Directional);
            if (focused.Header is HoldShortLadderStop { Command: { } key })
            {
                Feedback.Unpoint(key);
            }

            focused.IsEnabled = false;
        }

        private MenuItem NewRow(string key)
        {
            var item = new MenuItem { HeaderTemplate = HoldShortStopRowTemplate.Instance };
            item.Click += async (_, e) =>
            {
                if (!_live.Contains(item) || (item.Header is not HoldShortLadderStop row))
                {
                    e.Handled = true;
                    return;
                }

                await ChooseAsync(row.Command);
            };
            Feedback.Track(item, key);
            return item;
        }

        /// <summary>Sends <paramref name="command"/> and closes the strip's flyout and the menu, or the submenu's menu.</summary>
        private async Task ChooseAsync(string command)
        {
            Task sending = host.SendAsync(context.Callsign, command, context.Initials);
            if (_closeFlyout is { } close)
            {
                close();
            }
            else
            {
                entry.FindLogicalAncestorOfType<ContextMenu>()?.Close();
            }

            await sending;
        }

        /// <summary>
        /// The scope preview for the stop keyed <paramref name="key"/>: nothing for a stop not shown or gone; else the bar
        /// boxed and the route to it, no route for the bar the route starts at.
        /// </summary>
        private ScopePreview? StopPreview(string key)
        {
            StopEntry? shown = _shown.FirstOrDefault(candidate => candidate.Choice.Command == key);
            if ((shown is null) || shown.IsGone)
            {
                return null;
            }

            TaxiRoute? route = (shown.Choice.Preview.Segments.Count > 0) ? shown.Choice.Preview : null;
            return new ScopePreview(null, route, null, shown.Choice.BarPoint, null);
        }

        /// <summary>
        /// Shows <paramref name="shown"/> in <paramref name="item"/>, dimmed and out of reach once gone unless it
        /// <paramref name="holdsFocus"/>.
        /// </summary>
        private static void Present(MenuItem item, StopEntry shown, bool holdsFocus)
        {
            var row = HoldShortLadderStop.From(shown.Choice, shown.IsGone);
            if (!Equals(item.Header, row))
            {
                item.Header = row;
                MenuCommandText.SetCommand(item, row.Command);
                AutomationProperties.SetName(item, row.ToString());
            }

            item.IsEnabled = (!shown.IsGone) || holdsFocus;
            item.Opacity = shown.IsGone ? MenuGlyphRowTemplate.DimmedOpacity : 1;
        }

        private static void SetHeader(MenuItem item, object header)
        {
            if (!Equals(item.Header, header))
            {
                item.Header = header;
                AutomationProperties.SetName(item, header.ToString());
            }
        }
    }
}

/// <summary>
/// One stop of the Hold short of… ladder, drawn by <see cref="HoldShortStopRowTemplate"/>: the bar mark, the TW/RW badge,
/// the bold name, where along the route, the distance and the command. Its text form, which the row's automation name
/// uses, reads <c>TW W · before turning onto W · ~350 ft — HS W</c>, or, once the route has left the bar,
/// <c>TW A · gone — HS A</c>.
/// </summary>
/// <param name="Badge"><see cref="HoldShortChoice.TaxiwayBadge"/> or <see cref="HoldShortChoice.RunwayBadge"/>.</param>
/// <param name="Name">The taxiway or runway the stop holds short of.</param>
/// <param name="Where">Where along the route the bar is.</param>
/// <param name="DistanceFt">The distance to the bar along the route.</param>
/// <param name="Command">The <c>HS</c> the stop sends.</param>
/// <param name="IsGone">Whether the route has left the bar while the pointer is inside the flyout.</param>
public sealed record HoldShortLadderStop(string Badge, string Name, string Where, double DistanceFt, string Command, bool IsGone)
{
    /// <summary>The stop <paramref name="choice"/> shows, gone or not.</summary>
    public static HoldShortLadderStop From(HoldShortChoice choice, bool isGone) =>
        new(choice.Label.Badge, choice.Label.Name, choice.Label.Where, choice.Label.DistanceFt, choice.Command, isGone);

    /// <summary>What follows the name: where along the route, or <see cref="GroundTrafficFlyout.GoneState"/>.</summary>
    public string Detail => IsGone ? GroundTrafficFlyout.GoneState : Where;

    /// <summary>The distance the row shows, <c>~350 ft</c>; empty once gone.</summary>
    public string Distance => IsGone ? "" : HoldShortLadder.FeetText(DistanceFt);

    /// <inheritdoc />
    public override string ToString() => IsGone ? $"{Badge} {Name} · {Detail} — {Command}" : $"{Badge} {Name} · {Detail} · {Distance} — {Command}";
}

/// <summary>The ladder's line for the aircraft itself: <c>UAL455 · now, holding short of 28R on B</c>.</summary>
/// <param name="Callsign">The aircraft the menu commands.</param>
/// <param name="Line">Where it is now (<see cref="HoldShortMenu.MeLine"/>).</param>
public sealed record HoldShortMeRow(string Callsign, string Line)
{
    /// <inheritdoc />
    public override string ToString() => $"{Callsign} · {Line}";
}

/// <summary>The bar marks leading the ladder's rows, 22 by 10 px.</summary>
internal static class HoldShortBarMark
{
    /// <summary>A runway bar's amber.</summary>
    public static readonly IImmutableSolidColorBrush RunwayBrush = new ImmutableSolidColorBrush(Color.Parse("#E8A33D"));

    /// <summary>A taxiway bar's teal.</summary>
    public static readonly IImmutableSolidColorBrush TaxiwayBrush = new ImmutableSolidColorBrush(Color.Parse("#4FB8A8"));

    /// <summary>A stop's mark: two amber lines for a runway, teal solid over dashed for a taxiway.</summary>
    public static Canvas ForBadge(string badge) => (badge == HoldShortChoice.RunwayBadge) ? Runway() : Taxiway();

    /// <summary>Two amber lines.</summary>
    public static Canvas Runway() => Mark(Line(3, RunwayBrush, null), Line(7, RunwayBrush, null));

    /// <summary>A teal ring, for a spot, gate or helipad.</summary>
    public static Canvas Stand()
    {
        var ring = new Ellipse
        {
            Width = 7.2,
            Height = 7.2,
            Stroke = TaxiwayBrush,
            StrokeThickness = 1.6,
        };
        Canvas.SetLeft(ring, 7.4);
        Canvas.SetTop(ring, 1.4);
        return Mark(ring);
    }

    /// <summary>A filled teal dot, for the aircraft itself.</summary>
    public static Canvas Me()
    {
        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = GroundTrafficLook.MovingBrush,
        };
        Canvas.SetLeft(dot, 6.5);
        Canvas.SetTop(dot, 0.5);
        return Mark(dot);
    }

    private static Canvas Taxiway() => Mark(Line(3, TaxiwayBrush, null), Line(7, TaxiwayBrush, new AvaloniaList<double>(1.7, 1.7)));

    private static Line Line(double y, IBrush brush, AvaloniaList<double>? dashes) =>
        new()
        {
            StartPoint = new Point(1, y),
            EndPoint = new Point(21, y),
            Stroke = brush,
            StrokeThickness = 1.8,
            StrokeDashArray = dashes,
        };

    private static Canvas Mark(params Control[] parts)
    {
        var mark = new Canvas
        {
            Width = 22,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        mark.Children.AddRange(parts);
        return mark;
    }
}

/// <summary>
/// The view of a <see cref="HoldShortLadderStop"/>: the bar mark, the TW/RW badge, the bold name, the dimmed where (or
/// gone), the distance, then the command in the dim monospace font.
/// </summary>
internal sealed class HoldShortStopRowTemplate : FuncDataTemplate<HoldShortLadderStop>
{
    /// <summary>The one template every ladder stop's menu item uses.</summary>
    public static HoldShortStopRowTemplate Instance { get; } = new();

    private HoldShortStopRowTemplate()
        : base((row, _) => Build(row)) { }

    private static Grid Build(HoldShortLadderStop row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 8,
            MaxWidth = MenuCommandRow.InlineRowMaxWidth,
        };
        grid.Children.Add(HoldShortBarMark.ForBadge(row.Badge));

        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        name.Children.Add(Badge(row.Badge));
        name.Children.Add(
            new TextBlock
            {
                Text = row.Name,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        name.Children.Add(GroundTrafficLook.Subtle($"· {row.Detail}", 12));
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var distance = new TextBlock { Text = row.Distance, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(distance, 2);
        grid.Children.Add(distance);

        TextBlock command = GroundTrafficLook.Command(row.Command);
        command.Opacity = 0.7;
        Grid.SetColumn(command, 3);
        grid.Children.Add(command);
        return grid;
    }

    private static Border Badge(string badge) =>
        new()
        {
            Background = (badge == HoldShortChoice.RunwayBadge) ? HoldShortBarMark.RunwayBrush : HoldShortBarMark.TaxiwayBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = badge,
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.Black,
            },
        };
}

/// <summary>The view of a <see cref="HoldShortMeRow"/>: a teal dot, the bold callsign, then the dimmed line.</summary>
internal sealed class HoldShortMeRowTemplate : FuncDataTemplate<HoldShortMeRow>
{
    /// <summary>The one template every ladder's line for the aircraft uses.</summary>
    public static HoldShortMeRowTemplate Instance { get; } = new();

    private HoldShortMeRowTemplate()
        : base((row, _) => Build(row)) { }

    private static StackPanel Build(HoldShortMeRow row)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(HoldShortBarMark.Me());
        line.Children.Add(
            new TextBlock
            {
                Text = row.Callsign,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        line.Children.Add(GroundTrafficLook.Subtle($"· {row.Line}", 12));
        return line;
    }
}

/// <summary>
/// The view of a <see cref="HoldShortDestination"/>: the runway's bar mark or a stand's ring, the name, the dimmed
/// <see cref="HoldShortDestination.RouteEndsHere"/>, then the distance.
/// </summary>
internal sealed class HoldShortEndRowTemplate : FuncDataTemplate<HoldShortDestination>
{
    /// <summary>The one template every ladder's destination row uses.</summary>
    public static HoldShortEndRowTemplate Instance { get; } = new();

    private HoldShortEndRowTemplate()
        : base((row, _) => Build(row)) { }

    private static Grid Build(HoldShortDestination row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8,
            MaxWidth = MenuCommandRow.InlineRowMaxWidth,
        };
        grid.Children.Add(row.IsRunway ? HoldShortBarMark.Runway() : HoldShortBarMark.Stand());

        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        name.Children.Add(new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center });
        name.Children.Add(GroundTrafficLook.Subtle($"· {HoldShortDestination.RouteEndsHere}", 12));
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var distance = new TextBlock { Text = HoldShortLadder.FeetText(row.DistanceFt), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(distance, 2);
        grid.Children.Add(distance);
        return grid;
    }
}
