using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Yaat.Sim;
using Yaat.Sim.Phases;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The Exit left / Exit right flyout over the aircraft's exits-ahead list (<see cref="IMenuAircraft.ExitsAhead"/>), one builder
/// for the All Commands submenu and the quick-command strip's flyout. Each exit on the flyout's side is one row: a glyph drawn
/// at the exit's turn-off angle (<see cref="TurnOffGlyph"/>), <c>Taxiway G</c> with a <c>· planned</c> note for the planned
/// exit, its distance and the <c>EL</c>/<c>ER</c> command naming it. A row stays one line within
/// <see cref="MenuCommandRow.RowMaxWidth"/>, its note cut with an ellipsis, and carries its whole text and turn-off angle in
/// its tooltip. On the rollout the flyout is a title saying the distances are from the aircraft, the exit rows, a separator
/// and Pilot's choice (the bare verb); with no list, Pilot's choice alone. On final it is Pilot's choice, a separator, the exit
/// rows, each distance "from thr" (the landing threshold), a separator and the "Exit at" box (<see cref="ExitAtBox"/>), whose
/// matches from the runway's exits on that side (<see cref="IMenuHost.GetRunwayExits"/>) are listed under it. On final with no
/// exit forecast on the side it is a dimmed <see cref="NoneForecast"/> line, a separator and the box, which lists every runway
/// exit on the side while empty. While the strip's flyout is open the rows refill once a second (<see cref="FlyoutLiveTick"/>):
/// with the pointer outside they follow the list's order; with it inside the order freezes, each distance updating in place, an
/// exit the list no longer holds staying as a greyed, disabled row reading <see cref="PassedState"/> once the aircraft has
/// rolled past it and <see cref="CantMakeState"/> before that and on final, and a new exit joining the bottom; when the pointer
/// leaves the list catches up. The All Commands submenu refills as it opens.
/// </summary>
public static class ExitFlyout
{
    /// <summary>The name of the "Exit at" box's text box.</summary>
    public const string BoxName = "ExitAtBox";

    /// <summary>
    /// What a row the list has dropped while the order was frozen shows in place of its distance once the aircraft has rolled
    /// past its exit.
    /// </summary>
    public const string PassedState = "passed";

    /// <summary>
    /// What a row the list has dropped while the order was frozen shows in place of its distance while its exit is still ahead
    /// (the aircraft can no longer make it), and always on final.
    /// </summary>
    public const string CantMakeState = "can't make";

    /// <summary>What the dimmed line leading the flyout on final reads when the forecast holds no exit on its side.</summary>
    public const string NoneForecast = "none forecast";

    private const double ArmLength = 12;
    private const double ArrowheadLength = 4.5;

    /// <summary>The entry's label: <c>Exit left</c> or <c>Exit right</c>.</summary>
    public static string Label(ExitSide side) => (side == ExitSide.Left) ? "Exit left" : "Exit right";

    /// <summary>The command verb: <c>EL</c> or <c>ER</c>.</summary>
    public static string Verb(ExitSide side) => (side == ExitSide.Left) ? "EL" : "ER";

    /// <summary>
    /// The Exit left / Exit right entry for <paramref name="aircraft"/>, or null when its exits-ahead list holds no exit on
    /// <paramref name="side"/> and, on final, the runway has none there either (<see cref="IMenuHost.GetRunwayExits"/>).
    /// </summary>
    /// <param name="side">The side the entry exits to.</param>
    /// <param name="aircraft">The aircraft whose exits-ahead list the rows follow.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the rows send through and the box's runway exits come from.</param>
    public static MenuItem? Build(ExitSide side, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        if (
            (aircraft?.ExitsAhead is { } exits)
            && !exits.Any(row => row.Side == side)
            && (!IsOnFinal(aircraft) || (host.GetRunwayExits(context.Callsign, side).Count == 0))
        )
        {
            return null;
        }

        var entry = new MenuItem { Header = Label(side) };
        var list = new ExitList(entry, side, aircraft, context, host);
        list.Attach();
        QuickCommandStrip.FollowInFlyout(entry, list);
        return entry;
    }

    /// <summary>
    /// Whether <paramref name="aircraft"/> is on final rather than on its rollout, its exit distances then the forecast's from
    /// the landing threshold.
    /// </summary>
    internal static bool IsOnFinal(IMenuAircraft? aircraft) => aircraft?.CurrentPhase is not ("Landing" or "Runway Exit");

    /// <summary>
    /// The exit glyph turned to the exit's real turn-off angle, drawn in the tower amber: the runway as a straight line with
    /// travel upward, on the right of the box for a left exit and on the left for a right one, and the exit branching off it
    /// <paramref name="degrees"/> from the travel direction toward <paramref name="side"/>, an arrowhead at the branch's end. A
    /// branch bending back (over 100°) leaves the runway higher up, so it stays inside the box.
    /// </summary>
    /// <param name="side">The side the exit turns off to.</param>
    /// <param name="degrees">The turn-off angle from the runway's direction of travel.</param>
    public static QuickCommandGlyph TurnOffGlyph(ExitSide side, int degrees)
    {
        double toward = (side == ExitSide.Left) ? -1 : 1;
        double stemX = (side == ExitSide.Left) ? 18 : 6;
        double branchY = (degrees > 100) ? 7 : 13;
        double radians = degrees * Math.PI / 180;
        double dx = toward * Math.Sin(radians);
        double dy = -Math.Cos(radians);
        double endX = stemX + (dx * ArmLength);
        double endY = branchY + (dy * ArmLength);
        (double firstX, double firstY) = Turned(-dx, -dy, Math.PI / 4);
        (double secondX, double secondY) = Turned(-dx, -dy, -Math.PI / 4);
        string path =
            $"M{N(stemX)} 22V{N(branchY)} L{N(endX)} {N(endY)}"
            + $" M{N(endX + (firstX * ArrowheadLength))} {N(endY + (firstY * ArrowheadLength))} L{N(endX)} {N(endY)}"
            + $" L{N(endX + (secondX * ArrowheadLength))} {N(endY + (secondY * ArrowheadLength))}"
            + $" M{N(stemX)} {N(branchY)}V2";
        return new QuickCommandGlyph(path, QuickCommandGlyphFamily.Tower, 0, false);
    }

    private static (double X, double Y) Turned(double x, double y, double radians) =>
        ((x * Math.Cos(radians)) - (y * Math.Sin(radians)), (x * Math.Sin(radians)) + (y * Math.Cos(radians)));

    /// <summary>A path coordinate to one decimal, never <c>-0</c>.</summary>
    private static string N(double value)
    {
        double rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return ((rounded == 0) ? 0 : rounded).ToString("0.#", CultureInfo.InvariantCulture);
    }

    /// <summary>The key a row keeps across a refill: its taxiway and side.</summary>
    private static (string Taxiway, ExitSide Side) KeyOf(ExitAheadDto exit) => (exit.Taxiway, exit.Side);

    /// <summary>
    /// A list held in place while the pointer is inside the flyout: the exits shown when the pointer entered, each keeping its
    /// place while its distance follows the list; an exit the list no longer holds stays as dropped, its distance from the
    /// aircraft reckoned on from the aircraft's ground speed and the time gone by, and a new one joins the bottom.
    /// </summary>
    private sealed class FrozenExits(IEnumerable<ExitEntry> shown, TimeProvider clock)
    {
        private const double FeetPerSecondPerKnot = 6076.12 / 3600;

        private readonly List<ExitEntry> _entries = [.. shown];
        private long _updated = clock.GetTimestamp();

        /// <summary>The held entries, in order.</summary>
        public List<ExitEntry> Entries => [.. _entries];

        /// <summary>
        /// Brings every held entry up to <paramref name="exits"/>, rolls each dropped one's distance on by what
        /// <paramref name="groundSpeedKnots"/> covers since the last update, and adds the new ones at the bottom.
        /// </summary>
        public void Update(IReadOnlyList<ExitAheadDto> exits, double groundSpeedKnots)
        {
            long now = clock.GetTimestamp();
            double rolledFt = groundSpeedKnots * FeetPerSecondPerKnot * clock.GetElapsedTime(_updated, now).TotalSeconds;
            _updated = now;
            var listed = new Dictionary<(string Taxiway, ExitSide Side), ExitAheadDto>();
            foreach (ExitAheadDto exit in exits)
            {
                listed.TryAdd(KeyOf(exit), exit);
            }

            var held = new HashSet<(string Taxiway, ExitSide Side)>();
            for (int i = 0; i < _entries.Count; i++)
            {
                ExitEntry entry = _entries[i];
                held.Add(KeyOf(entry.Exit));
                _entries[i] = listed.TryGetValue(KeyOf(entry.Exit), out ExitAheadDto? current)
                    ? new ExitEntry(current, null)
                    : entry with
                    {
                        DroppedAheadFt = (entry.DroppedAheadFt ?? entry.Exit.DistanceFt) - rolledFt,
                    };
            }

            _entries.AddRange(exits.Where(exit => held.Add(KeyOf(exit))).Select(exit => new ExitEntry(exit, null)));
        }
    }

    /// <summary>
    /// An exit as the flyout lists it: the row as last listed, and once the list has dropped it, how far ahead of the aircraft
    /// it is reckoned to be now (zero or less once rolled past).
    /// </summary>
    private sealed record ExitEntry(ExitAheadDto Exit, double? DroppedAheadFt);

    /// <summary>
    /// The flyout's items on whichever surface shows them: filled when built, refilled when the All Commands submenu opens and,
    /// while the strip's flyout is open, once a second, held in place while the pointer is inside the flyout. An exit keeps its
    /// row, by its taxiway and side, across a refill; a dropped row sends nothing and keeps the flyout open, even on a click
    /// already on its way.
    /// </summary>
    private sealed class ExitList : IStripFlyoutContent
    {
        private readonly MenuItem _entry;
        private readonly ExitSide _side;
        private readonly IMenuAircraft? _aircraft;
        private readonly MenuContext _context;
        private readonly IMenuHost _host;
        private readonly bool _onFinal;
        private readonly MenuItem _title;
        private readonly MenuItem _choice;
        private readonly MenuItem _none;
        private readonly Separator _beforeRows = new();
        private readonly Separator _afterRows = new();
        private readonly ExitAtBox? _box;
        private readonly MenuItem? _boxItem;
        private readonly HashSet<MenuItem> _live = [];
        private Dictionary<(string Taxiway, ExitSide Side), MenuItem> _rows = [];
        private Dictionary<string, MenuItem> _matchItems = [];
        private List<MenuItem> _matchRows = [];
        private bool? _noneForecast;
        private List<ExitEntry> _shown = [];
        private ItemCollection? _flyoutItems;
        private Action? _closeFlyout;
        private FlyoutLiveTick? _tick;
        private FrozenExits? _frozen;

        public ExitList(MenuItem entry, ExitSide side, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
        {
            _entry = entry;
            _side = side;
            _aircraft = aircraft;
            _context = context;
            _host = host;
            _onFinal = IsOnFinal(aircraft);
            _title = TitleItem(side);
            _choice = ChoiceItem();
            _none = NoneForecastItem(side);
            if (_onFinal)
            {
                _box = new ExitAtBox(side, () => host.GetRunwayExits(context.Callsign, side), () => Listed());
                _boxItem = BoxItem(_box);
            }
        }

        /// <summary>Fills the submenu and refills it whenever the submenu opens.</summary>
        public void Attach()
        {
            Show([.. Listed().Select(exit => new ExitEntry(exit, null))]);
            _entry.PropertyChanged += (_, e) =>
            {
                if ((e.Property == MenuItem.IsSubMenuOpenProperty) && _entry.IsSubMenuOpen)
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
            (_tick ??= new FlyoutLiveTick(_host.Clock, Refill)).Start();
            Refill();
        }

        /// <inheritdoc />
        public void PointerInside(bool inside)
        {
            if ((_tick?.IsRunning != true) || (inside == (_frozen is not null)))
            {
                return;
            }

            _frozen = inside ? new FrozenExits(_shown, _host.Clock) : null;
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
            Refill();
        }

        /// <summary>The exits the aircraft's list holds on this side now, in the list's order.</summary>
        private List<ExitAheadDto> Listed() => [.. (_aircraft?.ExitsAhead ?? []).Where(exit => exit.Side == _side)];

        private void Refill()
        {
            List<ExitAheadDto> listed = Listed();
            if (_frozen is null)
            {
                Show([.. listed.Select(exit => new ExitEntry(exit, null))]);
                return;
            }

            _frozen.Update(listed, _aircraft?.GroundSpeedKnots ?? 0);
            Show(_frozen.Entries);
        }

        private void Show(List<ExitEntry> entries)
        {
            MenuItem? focused = _rows.Values.FirstOrDefault(row => row.IsKeyboardFocusWithin);
            _shown = entries;
            List<MenuItem> rows = Rows(entries, focused);
            List<Control> wanted = _onFinal ? FinalItems(rows) : RolloutItems(rows);
            FlyoutItemSync.Sync(_flyoutItems ?? _entry.Items, wanted, FlyoutItemSync.FocusedIn(wanted));
            HandFocusOnIfPassed(focused, rows);
        }

        /// <summary>The rollout's items: the title, the rows, a separator and Pilot's choice; Pilot's choice alone with no row.</summary>
        private List<Control> RolloutItems(List<MenuItem> rows) => (rows.Count == 0) ? [_choice] : [_title, .. rows, _beforeRows, _choice];

        /// <summary>
        /// Final's items: Pilot's choice, a separator, the rows and a separator, then the box and its matches. With no row the
        /// forecast holds nothing on this side: the dimmed "none forecast" line in place of Pilot's choice, a separator, and the
        /// box, which lists every runway exit on the side while empty.
        /// </summary>
        private List<Control> FinalItems(List<MenuItem> rows)
        {
            if ((_box is not { } box) || (_boxItem is not { } boxItem))
            {
                return [_choice, .. rows];
            }

            bool noneForecast = rows.Count == 0;
            if (noneForecast != _noneForecast)
            {
                _noneForecast = noneForecast;
                box.Rematch(noneForecast);
                FollowMatches(box);
            }

            List<Control> items = noneForecast ? [_none, _beforeRows] : [_choice, _beforeRows, .. rows, _afterRows];
            items.Add(boxItem);
            items.AddRange(_matchRows);
            return items;
        }

        /// <summary>
        /// Brings the box's match rows up to its matches, in their order, keeping the row of every taxiway matched before; run
        /// only when the box's text changes, so a refill never replaces them.
        /// </summary>
        private void FollowMatches(ExitAtBox box)
        {
            var items = new Dictionary<string, MenuItem>(StringComparer.Ordinal);
            List<MenuItem> ordered = [];
            foreach (ExitAheadDto exit in box.Matches)
            {
                if (!items.ContainsKey(exit.Taxiway))
                {
                    MenuItem item = _matchItems.TryGetValue(exit.Taxiway, out MenuItem? kept) ? kept : MatchItem(exit);
                    items.Add(exit.Taxiway, item);
                    ordered.Add(item);
                }
            }

            _matchItems = items;
            _matchRows = ordered;
        }

        /// <summary>
        /// The rows <paramref name="entries"/> call for, reusing the row of every exit shown before; <paramref name="focused"/>
        /// stays in reach for now even when dropped.
        /// </summary>
        private List<MenuItem> Rows(List<ExitEntry> entries, MenuItem? focused)
        {
            var rows = new Dictionary<(string Taxiway, ExitSide Side), MenuItem>();
            List<MenuItem> ordered = [];
            _live.Clear();
            foreach (ExitEntry shown in entries)
            {
                (string Taxiway, ExitSide Side) key = KeyOf(shown.Exit);
                MenuItem item = (!rows.ContainsKey(key) && _rows.TryGetValue(key, out MenuItem? kept)) ? kept : NewRow();
                rows.TryAdd(key, item);
                ordered.Add(item);
                Present(item, new ExitRow(shown.Exit, _onFinal, DroppedState(shown)), ReferenceEquals(item, focused));
                if (shown.DroppedAheadFt is null)
                {
                    _live.Add(item);
                }
            }

            _rows = rows;
            return ordered;
        }

        /// <summary>
        /// What a dropped row reads: <see cref="PassedState"/> once the aircraft has rolled past its exit on the rollout, else
        /// <see cref="CantMakeState"/>, always so on final; null for a listed row.
        /// </summary>
        private string? DroppedState(ExitEntry entry) =>
            entry.DroppedAheadFt switch
            {
                null => null,
                <= 0 when !_onFinal => PassedState,
                _ => CantMakeState,
            };

        /// <summary>
        /// Hands keyboard focus from <paramref name="focused"/> once its exit is dropped or no longer listed to the nearest live
        /// row below it, else above it, else Pilot's choice (the "Exit at" box on final once none is forecast), then takes it out
        /// of reach.
        /// </summary>
        private void HandFocusOnIfPassed(MenuItem? focused, List<MenuItem> rows)
        {
            if ((focused is null) || _live.Contains(focused))
            {
                return;
            }

            int at = rows.IndexOf(focused);
            MenuItem? next =
                (at >= 0)
                    ? rows.Skip(at + 1).FirstOrDefault(_live.Contains) ?? rows.Take(at).LastOrDefault(_live.Contains)
                    : rows.FirstOrDefault(_live.Contains);
            MenuItem fallback = ((_noneForecast == true) && (_boxItem is { } boxItem)) ? boxItem : _choice;
            (next ?? fallback).Focus(NavigationMethod.Directional);
            focused.IsEnabled = false;
        }

        private MenuItem NewRow()
        {
            var item = new MenuItem { HeaderTemplate = ExitRowTemplate.Instance };
            item.Click += async (_, e) =>
            {
                if (!_live.Contains(item) || (item.Header is not ExitRow row))
                {
                    e.Handled = true;
                    return;
                }

                await ChooseAsync(row.Command);
            };
            return item;
        }

        /// <summary>Pilot's choice: the side's strip glyph and the bare verb.</summary>
        private MenuItem ChoiceItem()
        {
            string verb = Verb(_side);
            var row = new MenuCommandRow
            {
                Glyph = QuickCommandGlyphs.For((_side == ExitSide.Left) ? MenuIds.TowerExitLeft : MenuIds.TowerExitRight),
                Name = "Pilot's choice",
                EmphasizeName = false,
                IsHighlighted = false,
                Detail = null,
                DetailPlacement = MenuDetailPlacement.Stacked,
                Distance = null,
                Command = verb,
            };
            return SendingItem(row, MenuCommandRowTemplate.Instance, row.ToString(), verb);
        }

        /// <summary>The dimmed, disabled "none forecast" line: the side's strip glyph and <see cref="NoneForecast"/>, sending nothing.</summary>
        private static MenuItem NoneForecastItem(ExitSide side)
        {
            var row = new MenuCommandRow
            {
                Glyph = QuickCommandGlyphs.For((side == ExitSide.Left) ? MenuIds.TowerExitLeft : MenuIds.TowerExitRight),
                Name = NoneForecast,
                EmphasizeName = false,
                IsHighlighted = false,
                Detail = null,
                DetailPlacement = MenuDetailPlacement.Stacked,
                Distance = null,
                Command = "",
            };
            var item = new MenuItem
            {
                Header = row,
                HeaderTemplate = MenuCommandRowTemplate.Instance,
                IsEnabled = false,
                Opacity = MenuGlyphRowTemplate.DimmedOpacity,
            };
            AutomationProperties.SetName(item, NoneForecast);
            return item;
        }

        /// <summary>One of the box's matches: the taxiway, its distance from the threshold and the command naming it.</summary>
        private MenuItem MatchItem(ExitAheadDto exit)
        {
            string command = $"{Verb(_side)} {exit.Taxiway}";
            var row = new MenuCommandRow
            {
                Glyph = null,
                Name = exit.Taxiway,
                EmphasizeName = true,
                IsHighlighted = false,
                Detail = null,
                DetailPlacement = MenuDetailPlacement.Stacked,
                Distance = $"{HoldShortLadder.FeetText(exit.DistanceFt)} from thr",
                Command = command,
            };
            return SendingItem(row, MenuCommandRowTemplate.Instance, row.ToString(), command);
        }

        private MenuItem SendingItem(object header, IDataTemplate template, string name, string command)
        {
            var item = new MenuItem { Header = header, HeaderTemplate = template };
            MenuCommandText.SetCommand(item, command);
            AutomationProperties.SetName(item, name);
            item.Click += async (_, _) => await ChooseAsync(command);
            return item;
        }

        /// <summary>
        /// The "Exit at" box's item: clicked, or Enter in its box, it sends the box's command; while the box cannot send it stays
        /// open and sends nothing. Typing refreshes the matches under it.
        /// </summary>
        private MenuItem BoxItem(ExitAtBox box)
        {
            var item = new MenuItem { Header = box, HeaderTemplate = ExitAtBoxTemplate.Instance };

            void Follow()
            {
                MenuCommandText.SetCommand(item, box.Command);
                AutomationProperties.SetName(item, box.ToString());
                item.StaysOpenOnClick = !box.CanSend;
            }

            Follow();
            box.Changed += (_, _) =>
            {
                Follow();
                FollowMatches(box);
                Show(_shown);
            };
            FlyoutBoxFocus.HandFocusToTheBox(item);
            item.Click += async (_, e) =>
            {
                if (box.Command is not { } command)
                {
                    e.Handled = true;
                    return;
                }

                await ChooseAsync(command);
            };
            return item;
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

        /// <summary>
        /// Shows <paramref name="row"/> in <paramref name="item"/>, dimmed and out of reach once dropped unless it
        /// <paramref name="holdsFocus"/>.
        /// </summary>
        private static void Present(MenuItem item, ExitRow row, bool holdsFocus)
        {
            if (!Equals(item.Header, row))
            {
                item.Header = row;
                MenuCommandText.SetCommand(item, row.IsDropped ? null : row.Command);
                AutomationProperties.SetName(item, row.ToString());
            }

            item.IsEnabled = (!row.IsDropped) || holdsFocus;
            item.StaysOpenOnClick = row.IsDropped;
            item.Opacity = row.IsDropped ? MenuGlyphRowTemplate.DimmedOpacity : 1;
        }

        /// <summary>The rollout's title: which side's exits, and that the distances are from the aircraft.</summary>
        private static MenuItem TitleItem(ExitSide side)
        {
            var title = new ExitFlyoutTitle((side == ExitSide.Left) ? "Exits ahead, left side" : "Exits ahead, right side", "from the aircraft");
            var item = new MenuItem
            {
                Header = title,
                HeaderTemplate = ExitFlyoutTitleTemplate.Instance,
                IsEnabled = false,
            };
            AutomationProperties.SetName(item, title.ToString());
            return item;
        }
    }
}

/// <summary>
/// One exit row of the Exit left / Exit right flyout, drawn by <see cref="ExitRowTemplate"/>: the glyph at the exit's turn-off
/// angle, <c>Taxiway G</c> with a <c>planned</c> note for the planned exit, the distance (<c>~600 ft</c>, on final
/// <c>~2,700 ft from thr</c>, or once dropped <see cref="ExitFlyout.PassedState"/> or <see cref="ExitFlyout.CantMakeState"/>)
/// and the command.
/// </summary>
/// <param name="Exit">The exit as the list last held it.</param>
/// <param name="FromThreshold">Whether the distance is from the landing threshold (on final) rather than the aircraft.</param>
/// <param name="DroppedState">
/// What the row reads in place of its distance once the list has dropped the exit while the order was frozen; null while listed.
/// </param>
public sealed record ExitRow(ExitAheadDto Exit, bool FromThreshold, string? DroppedState)
{
    /// <summary>Whether the list has dropped the exit while the order was frozen.</summary>
    public bool IsDropped => DroppedState is not null;

    /// <summary>The glyph leading the row, at the exit's turn-off angle.</summary>
    public QuickCommandGlyph Glyph => ExitFlyout.TurnOffGlyph(Exit.Side, Exit.TurnOffAngleDeg);

    /// <summary>What the row names: <c>Taxiway G</c>.</summary>
    public string Name => $"Taxiway {Exit.Taxiway}";

    /// <summary>The note after the name: <c>planned</c> for the planned exit, else null.</summary>
    public string? Note => Exit.Planned ? "planned" : null;

    /// <summary>The distance column's text.</summary>
    public string Distance => DroppedState ?? (HoldShortLadder.FeetText(Exit.DistanceFt) + (FromThreshold ? " from thr" : ""));

    /// <summary>The command the row sends: <c>EL G</c>.</summary>
    public string Command => $"{ExitFlyout.Verb(Exit.Side)} {Exit.Taxiway}";

    /// <summary>The row's whole text for its tooltip: <c>Taxiway J · planned · ~2,800 ft · turn-off 39°</c>.</summary>
    public string FullText => $"{NameAndNote} · {Distance} · turn-off {Exit.TurnOffAngleDeg}°";

    private string NameAndNote => (Note is { } note) ? $"{Name} · {note}" : Name;

    /// <summary>The row as one line, <c>Taxiway G · planned · ~600 ft — EL G</c>.</summary>
    public override string ToString() => $"{NameAndNote} · {Distance} — {Command}";
}

/// <summary>
/// The view of an <see cref="ExitRow"/>, one line capped at <see cref="MenuCommandRow.RowMaxWidth"/>: the glyph, the name, the
/// note cut with an ellipsis when the row would be wider, the distance (amber once dropped) and the command right-aligned in the
/// dimmed monospace font. The row's tooltip carries its whole text and turn-off angle.
/// </summary>
public sealed class ExitRowTemplate : FuncDataTemplate<ExitRow>
{
    private static readonly IBrush DroppedBrush = new SolidColorBrush(Color.Parse("#FFC062"));

    /// <summary>The one template every exit row uses.</summary>
    public static ExitRowTemplate Instance { get; } = new();

    private ExitRowTemplate()
        : base((row, _) => Build(row)) { }

    private static Grid Build(ExitRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), MaxWidth = MenuCommandRow.RowMaxWidth };
        ToolTip.SetTip(grid, row.FullText);
        Viewbox glyph = QuickCommandStrip.GlyphIcon(row.Glyph);
        glyph.Margin = new Thickness(0, 0, 8, 0);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(glyph);

        Grid name = NameView(row);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var distance = new TextBlock
        {
            Text = row.Distance,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (row.IsDropped)
        {
            distance.Foreground = DroppedBrush;
        }

        Grid.SetColumn(distance, 2);
        grid.Children.Add(distance);

        var command = new TextBlock
        {
            Text = row.Command,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        Grid.SetColumn(command, 3);
        grid.Children.Add(command);
        return grid;
    }

    /// <summary>The name, never cut, then the note, which takes what width is left and is cut with an ellipsis past it.</summary>
    private static Grid NameView(ExitRow row)
    {
        var view = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), VerticalAlignment = VerticalAlignment.Center };
        view.Children.Add(new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center });
        if (row.Note is { } note)
        {
            var noteView = new TextBlock
            {
                Text = $" · {note}",
                FontSize = 12,
                Opacity = 0.8,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(noteView, 1);
            view.Children.Add(noteView);
        }

        return view;
    }
}

/// <summary>The rollout exit flyout's title: <c>Exits ahead, left side</c>, with <c>from the aircraft</c> right-aligned beside it.</summary>
/// <param name="Text">The title.</param>
/// <param name="Note">The note on what the distances are measured from.</param>
public sealed record ExitFlyoutTitle(string Text, string Note)
{
    /// <summary>The title as one line, <c>Exits ahead, left side · from the aircraft</c>.</summary>
    public override string ToString() => $"{Text} · {Note}";
}

/// <summary>The view of an <see cref="ExitFlyoutTitle"/>: the title at 11 px, the note right-aligned in the monospace font.</summary>
public sealed class ExitFlyoutTitleTemplate : FuncDataTemplate<ExitFlyoutTitle>
{
    /// <summary>The one template the title uses.</summary>
    public static ExitFlyoutTitleTemplate Instance { get; } = new();

    private ExitFlyoutTitleTemplate()
        : base((title, _) => Build(title)) { }

    private static Grid Build(ExitFlyoutTitle title)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = title.Text, FontSize = 11 });
        var note = new TextBlock
        {
            Text = title.Note,
            FontSize = 11,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        note.Bind(TextBlock.FontFamilyProperty, note.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        Grid.SetColumn(note, 1);
        grid.Children.Add(note);
        return grid;
    }
}

/// <summary>
/// The "Exit at" box of an exit flyout on final: a taxiway typed in, sent as <c>ER &lt;twy&gt;</c> (or <c>EL</c>). Typing
/// matches the runway's exits on the flyout's side whose name starts with the text: the one the text names exactly first, then
/// those the aircraft's exits-ahead list holds, in the list's order, then the rest nearest the threshold first. The box sends
/// the top match, or with none the text as typed, upper-cased. Empty, or holding a space, it cannot send: its command shows
/// <c>ER ···</c>, faded, and a click sends nothing.
/// </summary>
/// <param name="side">The side the box exits to.</param>
/// <param name="runwayExits">The runway's exits on that side (<see cref="IMenuHost.GetRunwayExits"/>), read once, at the first typing.</param>
/// <param name="listedExits">The aircraft's exits-ahead rows, in the list's order, read at each typing.</param>
public sealed class ExitAtBox(ExitSide side, Func<IReadOnlyList<ExitAheadDto>> runwayExits, Func<IReadOnlyList<ExitAheadDto>> listedExits)
{
    /// <summary>What the box is labelled.</summary>
    public const string Label = "Exit at";

    /// <summary>What the empty box reads.</summary>
    public const string Placeholder = "taxiway";

    private string _text = "";
    private IReadOnlyList<ExitAheadDto>? _runwayExits;
    private IReadOnlyList<ExitAheadDto> _matches = [];
    private bool _listAllWhileEmpty;

    /// <summary>The side the box exits to.</summary>
    public ExitSide Side => side;

    /// <summary>The box's text as typed; setting it raises <see cref="Changed"/> when it differs.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }

            _text = value;
            _matches = Match();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The taxiway typed: the text trimmed and upper-cased.</summary>
    public string Typed => Text.Trim().ToUpperInvariant();

    /// <summary>Whether the text names one taxiway: not blank, no space inside.</summary>
    public bool IsReadable => (Typed.Length > 0) && !Typed.Any(char.IsWhiteSpace);

    /// <summary>
    /// The runway's exits on the box's side whose name starts with the typed text, ranked as of the last typing: an exact name
    /// first, then the listed exits in the list's order, then the rest nearest the threshold first; none while unreadable.
    /// </summary>
    public IReadOnlyList<ExitAheadDto> Matches => _matches;

    /// <summary>The command the box sends now: the verb with the top match, else with the typed text; null while unreadable.</summary>
    public string? Command => IsReadable ? $"{ExitFlyout.Verb(side)} {Matches.FirstOrDefault()?.Taxiway ?? Typed}" : null;

    /// <summary>Whether the box sends a command now.</summary>
    public bool CanSend => Command is not null;

    /// <summary>The command column's text: the command, or <c>ER ···</c> while the box cannot send.</summary>
    public string ShownCommand => Command ?? $"{ExitFlyout.Verb(side)} {TakeoffFlyout.Unfilled}";

    /// <summary>Raised when <see cref="Text"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>The box as one line: <c>Exit at — ER ···</c>.</summary>
    public override string ToString() => $"{Label} — {ShownCommand}";

    /// <summary>
    /// Ranks the matches again, as typing does, now listing every runway exit on the side while the box is empty when
    /// <paramref name="listAllWhileEmpty"/> (the forecast holds no exit on the side); raises no <see cref="Changed"/>.
    /// </summary>
    public void Rematch(bool listAllWhileEmpty)
    {
        _listAllWhileEmpty = listAllWhileEmpty;
        _matches = Match();
    }

    private List<ExitAheadDto> Match()
    {
        if (!IsReadable && !(_listAllWhileEmpty && (Typed.Length == 0)))
        {
            return [];
        }

        string typed = Typed;
        List<string> listed = [.. listedExits().Where(exit => exit.Side == side).Select(exit => exit.Taxiway)];
        return
        [
            .. (_runwayExits ??= runwayExits())
                .Where(exit => (exit.Side == side) && exit.Taxiway.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .OrderBy(exit => string.Equals(exit.Taxiway, typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(exit => ListedRank(listed, exit.Taxiway))
                .ThenBy(exit => exit.DistanceFt)
                .ThenBy(exit => exit.Taxiway, StringComparer.Ordinal),
        ];
    }

    /// <summary>Where <paramref name="taxiway"/> stands in the exits-ahead list, or after every listed exit when it is not there.</summary>
    private static int ListedRank(List<string> listed, string taxiway)
    {
        int at = listed.FindIndex(name => string.Equals(name, taxiway, StringComparison.OrdinalIgnoreCase));
        return (at >= 0) ? at : int.MaxValue;
    }
}

/// <summary>
/// The view of the <see cref="ExitAtBox"/>: the side's strip glyph, the label with the box under it, and the command
/// right-aligned in the dimmed monospace font, which follows the box as it is typed in. While the box cannot send the glyph,
/// label and command are drawn faded; the box never is.
/// </summary>
public sealed class ExitAtBoxTemplate : FuncDataTemplate<ExitAtBox>
{
    private const double BoxWidth = 96;

    /// <summary>The one template the box uses.</summary>
    public static ExitAtBoxTemplate Instance { get; } = new();

    private ExitAtBoxTemplate()
        : base((box, _) => Build(box)) { }

    private static Grid Build(ExitAtBox exitAt)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Viewbox? glyph = null;
        if (QuickCommandGlyphs.For((exitAt.Side == ExitSide.Left) ? MenuIds.TowerExitLeft : MenuIds.TowerExitRight) is { } sideGlyph)
        {
            glyph = QuickCommandStrip.GlyphIcon(sideGlyph);
            glyph.Margin = new Thickness(0, 0, 8, 0);
            grid.Children.Add(glyph);
        }

        var label = new TextBlock { Text = ExitAtBox.Label, VerticalAlignment = VerticalAlignment.Center };
        var box = new TextBox
        {
            Name = ExitFlyout.BoxName,
            Text = exitAt.Text,
            PlaceholderText = ExitAtBox.Placeholder,
            Width = BoxWidth,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        box.TextChanged += (_, _) => exitAt.Text = box.Text ?? "";
        TakeoffFlyout.KeepClicksInside(box);
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { label, box } };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var command = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        command.Bind(TextBlock.FontFamilyProperty, command.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);

        void Show()
        {
            double faded = exitAt.CanSend ? 1 : MenuGlyphRowTemplate.DimmedOpacity;
            command.Text = exitAt.ShownCommand;
            command.Opacity = 0.7 * faded;
            glyph?.Opacity = faded;
            label.Opacity = faded;
        }

        void OnChanged(object? sender, EventArgs e) => Show();

        Show();
        grid.AttachedToVisualTree += (_, _) =>
        {
            exitAt.Changed += OnChanged;
            Show();
        };
        grid.DetachedFromVisualTree += (_, _) => exitAt.Changed -= OnChanged;
        return grid;
    }
}
