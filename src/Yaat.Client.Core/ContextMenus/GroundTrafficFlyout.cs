using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The Give way to… flyout over the host's ground traffic (<see cref="IMenuHost.GetGroundTrafficRows"/>), one builder for
/// the All Commands submenu and the quick-command strip's flyout: a <see cref="GiveWayTitle"/> title with the mark legend,
/// then one list of the other ground aircraft nearest first, live-traffic shadows left out, each row led by a moving
/// (▶) or parked (■) mark and sending <c>GW</c> with its callsign. The first <see cref="GiveWayShownRows"/> show; a
/// "Show n more" row shows the rest in place and, once they show, becomes "Show fewer", which hides them again; the
/// list falls back to eight rows when the flyout closes. A row highlights its aircraft while it is pointed at or holds
/// keyboard focus, and previews it on the ground views (<see cref="ScopePreview"/>, sent again as the strip's flyout refills the list): the aircraft ringed, this aircraft's
/// route and its own, and, for Give way to…, where the two meet; an aircraft with no route is ringed alone, a gone row
/// previews nothing, and a Give way to… row whose routes meet nowhere says <see cref="NoConflictPredictedText"/>. The
/// highlight and the preview clear when the row loses both and when the flyout closes. With no other aircraft on the
/// ground the entry is disabled, the reason faint beside its label, and the strip shows its button faded, opening nothing,
/// its label naming the same reason. While the quick-command strip's flyout of either list is open it refills once a
/// second (<see cref="FlyoutLiveTick"/>): with the pointer outside it the list is rebuilt nearest first, rows leaving and
/// appearing; with the pointer inside it the order freezes, each row's distance and state updating in place, an aircraft
/// that left staying as a dimmed <see cref="GoneState"/> row that sends nothing and the keyboard skips, and a newcomer
/// joining the bottom of the list; when the pointer leaves the list catches up at once. A row whose aircraft leaves the
/// list, or goes while it is still listed, hands keyboard focus to the nearest live row, to "Show n more" (Give way) or the
/// filter box (Follow) when nothing else is in reach, and a row that goes gone under the pointer stops highlighting its
/// aircraft. A Follow list off screen, its flyout or submenu closed, leaves its stuck title alone.
/// </summary>
public static class GroundTrafficFlyout
{
    /// <summary>The Give way to… flyout's title.</summary>
    public const string GiveWayTitle = "Nearest first";

    /// <summary>The legend at the right of the Give way to… title.</summary>
    public const string GiveWayLegend = "▶ moving  ■ parked";

    /// <summary>Why a traffic entry offers nothing, as its faded strip button's label says after the entry's name.</summary>
    public const string NoTrafficReason = "no other aircraft on the ground";

    /// <summary>The muted line a traffic flyout shows once its list has emptied while open.</summary>
    public const string NoTrafficText = "No other aircraft on the ground.";

    /// <summary>The state a row reads once its aircraft has left the ground while the pointer is inside the flyout.</summary>
    public const string GoneState = "gone";

    /// <summary>The faint note after a Give way to… row's state when its aircraft's route is not predicted to meet this aircraft's.</summary>
    public const string NoConflictPredictedText = "no conflict predicted";

    /// <summary>How many rows the Give way to… list shows before its "Show n more" row.</summary>
    public const int GiveWayShownRows = 8;

    /// <summary>The Follow… filter box's placeholder and accessible name.</summary>
    public const string FollowFilterPlaceholder = "Filter or type a callsign";

    /// <summary>The Follow… hint line while some aircraft matches the filter.</summary>
    public const string FollowTopMatchHint = "Enter sends the top match.";

    /// <summary>The Follow… list's title over the moving aircraft.</summary>
    public const string FollowMovingTitle = "Moving";

    /// <summary>The Follow… list's title over the parked and holding aircraft.</summary>
    public const string FollowParkedTitle = "Parked or holding";

    /// <summary>About how many rows tall the Follow… list is before it scrolls.</summary>
    public const int FollowShownRows = 8;

    /// <summary>The height of one Follow… row, in px.</summary>
    public const double FollowRowHeight = 44;

    /// <summary>The name of the Follow… filter box.</summary>
    public const string FollowFilterBoxName = "FollowFilter";

    /// <summary>The name of the border at the Follow… list's top that shows the section title scrolled past.</summary>
    public const string FollowStickyTitleName = "FollowStickyTitle";

    /// <summary>The name of the Follow… list's scroll viewer.</summary>
    public const string FollowListName = "FollowList";

    private const string GiveWayVerb = "GW";
    private const string FollowVerb = "FOLLOWG";
    private const string MenuBackgroundKey = "MenuFlyoutPresenterBackground";

    /// <summary>The class on the menu row hosting the Follow… list, which keeps its background at rest while a row inside is pointed at.</summary>
    private const string FollowListRowClass = "follow-list";

    /// <summary>The border in a menu row's template that draws its pointed-at and selected background.</summary>
    private const string MenuItemLayoutRootPart = "PART_LayoutRoot";

    /// <summary>The presenter in a button's template that draws its background.</summary>
    private const string ButtonPresenterPart = "PART_ContentPresenter";

    /// <summary>The style class a row carries while it is a newcomer a held list appended.</summary>
    private const string NewcomerClass = "newcomer";

    private static readonly IImmutableSolidColorBrush SectionTitleBrush = new ImmutableSolidColorBrush(Color.Parse("#CFD4DA"));

    /// <summary>A newcomer row's mark: a 2 px edge in the ground teal at 85%, drawn inside the row's left side.</summary>
    private static readonly BoxShadows NewcomerEdge = new(
        new BoxShadow
        {
            IsInset = true,
            OffsetX = 2,
            Color = Color.FromArgb(
                217,
                GroundTrafficLook.MovingBrush.Color.R,
                GroundTrafficLook.MovingBrush.Color.G,
                GroundTrafficLook.MovingBrush.Color.B
            ),
        }
    );

    /// <summary>
    /// The Give way to… entry labelled <paramref name="label"/>: a submenu over the other ground aircraft, or, with none,
    /// a disabled item the strip shows as a faded button that opens nothing.
    /// </summary>
    /// <param name="label">The entry's text.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the traffic comes from and the rows send through.</param>
    public static MenuItem BuildGiveWay(string label, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        List<MenuGroundTrafficRow> traffic = GiveWayTraffic(context, host);
        if (traffic.Count == 0)
        {
            return Inert(entry, label);
        }

        var list = new GiveWayList(entry, context, host);
        list.Attach(traffic);
        QuickCommandStrip.FollowInFlyout(entry, list);
        return entry;
    }

    /// <summary>
    /// The Follow… entry labelled <paramref name="label"/>: a submenu holding a filter box, the hint line under it and
    /// one scrolling list, about <see cref="FollowShownRows"/> rows tall, of the other ground aircraft under the sticky
    /// <see cref="FollowMovingTitle"/> and <see cref="FollowParkedTitle"/> titles, nearest first within each, live-traffic
    /// shadows included; or, with no other aircraft on the ground, a disabled item the strip shows faded.
    /// </summary>
    /// <param name="label">The entry's text.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the traffic comes from and the rows send through.</param>
    public static MenuItem BuildFollow(string label, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        List<MenuGroundTrafficRow> traffic = FollowTraffic(context, host);
        if (traffic.Count == 0)
        {
            return Inert(entry, label);
        }

        var list = new FollowList(entry, context, host);
        list.Fill(traffic);
        QuickCommandStrip.FollowInFlyout(entry, list);
        return entry;
    }

    /// <summary>The hint line's text when the filter matches no aircraft: <c>No aircraft matches; Enter sends FOLLOWG N123 as typed.</c></summary>
    public static string FollowNoMatchHint(string typed) => $"No aircraft matches; Enter sends {FollowVerb} {typed} as typed.";

    /// <summary>
    /// The faded strip button's label for the entry <paramref name="label"/>: <c>Give way to · no other aircraft on the ground</c>.
    /// </summary>
    internal static string InertLabel(string label) => $"{label.TrimEnd('…')} · {NoTrafficReason}";

    /// <summary>The other ground aircraft Give way to… offers, nearest first, live-traffic shadows left out.</summary>
    private static List<MenuGroundTrafficRow> GiveWayTraffic(MenuContext context, IMenuHost host) =>
        [.. host.GetGroundTrafficRows(context.Callsign).Where(row => !row.IsSurfaceShadow).OrderBy(row => row.DistanceFeet)];

    /// <summary>The other ground aircraft Follow… offers, nearest first, live-traffic shadows included.</summary>
    private static List<MenuGroundTrafficRow> FollowTraffic(MenuContext context, IMenuHost host) =>
        [.. host.GetGroundTrafficRows(context.Callsign).OrderBy(row => row.DistanceFeet)];

    /// <summary>
    /// Dims <paramref name="row"/> and takes it out of reach once its aircraft is gone, or shows it as usual, and marks it
    /// with the teal left edge while it is a newcomer. A gone row that <paramref name="holdsFocus"/> stays in reach until
    /// its list has handed the focus on, since a row taken out of reach drops the focus altogether.
    /// </summary>
    private static void ShowState(InputElement row, TrafficEntry shown, bool holdsFocus)
    {
        row.IsEnabled = (!shown.IsGone) || holdsFocus;
        row.Opacity = shown.IsGone ? MenuGlyphRowTemplate.DimmedOpacity : 1;
        row.Classes.Set(NewcomerClass, shown.IsNew);
    }

    /// <summary>Draws the newcomer's teal left edge inside the background of the template part <paramref name="part"/> picks.</summary>
    private static Style NewcomerEdgeStyle(Func<Selector?, Selector> part, AvaloniaProperty boxShadow) =>
        new(part) { Setters = { new Setter(boxShadow, NewcomerEdge) } };

    /// <summary>
    /// Disables <paramref name="entry"/>, which All Commands then draws with <see cref="NoTrafficReason"/> faint beside its
    /// label, and has the strip show its button faded, its label naming the same reason.
    /// </summary>
    private static MenuItem Inert(MenuItem entry, string label)
    {
        MenuDisabledReasonRow.Apply(entry, label, NoTrafficReason);
        QuickCommandStrip.MarkInert(entry, InertLabel(label));
        return entry;
    }

    /// <summary>Sends <paramref name="command"/> and closes the strip's flyout and the menu, or the submenu's menu.</summary>
    private static async Task ChooseAsync(string command, MenuItem entry, Action? closeFlyout, MenuContext context, IMenuHost host)
    {
        Task sending = host.SendAsync(context.Callsign, command, context.Initials);
        if (closeFlyout is not null)
        {
            closeFlyout();
        }
        else
        {
            entry.FindLogicalAncestorOfType<ContextMenu>()?.Close();
        }

        await sending;
    }

    private static MenuItem Muted(string text) => new() { Header = text, IsEnabled = false };

    /// <summary>
    /// The scope preview for the row of <paramref name="callsign"/> among <paramref name="shown"/>: nothing for a row not
    /// shown or gone; the aircraft ringed alone when it has no route; else the aircraft ringed, the commanded aircraft's
    /// route (<paramref name="ownRoute"/>, read once for the whole list) and its own, and, when
    /// <paramref name="withMeetPoint"/> (Give way to…), where the two routes meet.
    /// </summary>
    private static ScopePreview? TrafficPreview(string callsign, List<TrafficEntry> shown, TaxiRoute? ownRoute, bool withMeetPoint, IMenuHost host)
    {
        TrafficEntry? entry = shown.FirstOrDefault(candidate => candidate.Traffic.Callsign == callsign);
        if ((entry is null) || entry.IsGone)
        {
            return null;
        }

        if (host.GetRemainingTaxiRoute(callsign) is not { } targetRoute)
        {
            return new ScopePreview(callsign, null, null, null, null);
        }

        LatLon? meetPoint = withMeetPoint ? entry.Traffic.MeetPoint : null;
        return new ScopePreview(callsign, ownRoute, targetRoute, null, meetPoint);
    }

    /// <summary>
    /// An aircraft row as a traffic flyout lists it: the aircraft as last seen, whether it has left the ground since, and
    /// the Follow… section it sits under.
    /// </summary>
    /// <param name="Traffic">The aircraft as the host last listed it.</param>
    /// <param name="IsGone">Whether the host no longer lists it.</param>
    /// <param name="InMovingSection">Whether it sits under Follow…'s Moving title rather than Parked or holding.</param>
    /// <param name="IsNew">Whether it joined the bottom of a held list as a newcomer, which its row's teal left edge marks.</param>
    private sealed record TrafficEntry(MenuGroundTrafficRow Traffic, bool IsGone, bool InMovingSection, bool IsNew)
    {
        /// <summary>The entry for an aircraft the host lists, under the section its own motion calls for.</summary>
        public static TrafficEntry Live(MenuGroundTrafficRow traffic) => new(traffic, false, traffic.IsMoving, false);
    }

    /// <summary>
    /// A traffic list held in place while the pointer is inside its flyout: the entries shown when the pointer entered,
    /// each keeping its place and section while its text follows the host; an aircraft the host no longer lists stays as
    /// a gone entry, and a newcomer joins the bottom of the list, under the last entry's section.
    /// </summary>
    private sealed class FrozenTraffic(IEnumerable<TrafficEntry> shown)
    {
        private readonly List<TrafficEntry> _entries = [.. shown];

        /// <summary>The held entries, in order.</summary>
        public List<TrafficEntry> Entries => [.. _entries];

        /// <summary>Brings every held entry up to <paramref name="traffic"/>, nearest first, and adds its newcomers at the bottom.</summary>
        public void Update(List<MenuGroundTrafficRow> traffic)
        {
            var listed = new Dictionary<string, MenuGroundTrafficRow>(StringComparer.Ordinal);
            foreach (MenuGroundTrafficRow row in traffic)
            {
                listed.TryAdd(row.Callsign, row);
            }

            var held = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _entries.Count; i++)
            {
                TrafficEntry entry = _entries[i];
                held.Add(entry.Traffic.Callsign);
                _entries[i] = listed.TryGetValue(entry.Traffic.Callsign, out MenuGroundTrafficRow? now)
                    ? entry with
                    {
                        Traffic = now,
                        IsGone = false,
                    }
                    : entry with
                    {
                        IsGone = true,
                    };
            }

            foreach (MenuGroundTrafficRow row in traffic.Where(row => held.Add(row.Callsign)))
            {
                bool movingSection = (_entries.Count > 0) ? _entries[^1].InMovingSection : row.IsMoving;
                _entries.Add(new TrafficEntry(row, false, movingSection, true));
            }
        }

        /// <summary>Lets go of the gone entries from <paramref name="index"/> on, which a folded list never shows.</summary>
        public void DropGoneFrom(int index)
        {
            for (int i = _entries.Count - 1; i >= index; i--)
            {
                if (_entries[i].IsGone)
                {
                    _entries.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>
    /// The Give way to… rows over the host's traffic, on whichever surface shows them: filled when built, refilled when
    /// a surface opens, when "Show n more" or "Show fewer" is chosen and, while the strip's flyout is open, once a second,
    /// held in place while the pointer is inside the flyout. An aircraft keeps its row, by callsign, across a refill; a
    /// row no longer listed, or gone, sends nothing, even on a click already on its way. A gone row past the first eight
    /// is let go while the list is folded, so "Show n more" counts only aircraft still on the ground.
    /// </summary>
    private sealed class GiveWayList(MenuItem entry, MenuContext context, IMenuHost host) : IStripFlyoutContent
    {
        private readonly MenuItem _title = new()
        {
            Header = new GroundTrafficFlyoutTitle(GiveWayTitle, GiveWayLegend),
            HeaderTemplate = GroundTrafficTitleTemplate.Instance,
            IsEnabled = false,
        };

        private readonly MenuItem _empty = Muted(NoTrafficText);
        private readonly HashSet<MenuItem> _live = [];
        private ScopeFeedbackTracker? _feedback;
        private MenuItem? _more;
        private Dictionary<string, MenuItem> _rows = new(StringComparer.Ordinal);
        private ItemCollection? _flyoutItems;
        private Action? _closeFlyout;
        private bool _expanded;
        private FlyoutLiveTick? _tick;
        private FrozenTraffic? _frozen;
        private List<TrafficEntry> _shown = [];
        private TaxiRoute? _ownRoute;

        /// <summary>The highlight and scope preview the rows drive, the meet point included.</summary>
        private ScopeFeedbackTracker Feedback =>
            _feedback ??= new ScopeFeedbackTracker(host, callsign => TrafficPreview(callsign, _shown, _ownRoute, true, host));

        /// <summary>Fills the submenu with <paramref name="traffic"/> and folds the list back to eight rows whenever the submenu closes.</summary>
        public void Attach(List<MenuGroundTrafficRow> traffic)
        {
            Show([.. traffic.Select(TrafficEntry.Live)]);
            entry.PropertyChanged += (_, e) =>
            {
                if ((e.Property == MenuItem.IsSubMenuOpenProperty) && !entry.IsSubMenuOpen)
                {
                    _expanded = false;
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

            _frozen = inside ? new FrozenTraffic(_shown) : null;
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
            _expanded = false;
            Feedback.Clear();
            Refill();
        }

        private void Refill()
        {
            List<MenuGroundTrafficRow> traffic = GiveWayTraffic(context, host);
            if (_frozen is null)
            {
                Show([.. traffic.Select(TrafficEntry.Live)]);
                return;
            }

            _frozen.Update(traffic);
            if (!_expanded)
            {
                _frozen.DropGoneFrom(GiveWayShownRows);
            }

            Show(_frozen.Entries);
        }

        private void Show(List<TrafficEntry> entries)
        {
            MenuItem? focused = _rows.Values.FirstOrDefault(row => row.IsKeyboardFocusWithin);
            int at = ListedIndex(focused);
            _shown = entries;
            _ownRoute = host.GetRemainingTaxiRoute(context.Callsign);
            List<Control> wanted = Wanted(entries, focused, _ownRoute is not null);
            FlyoutItemSync.Sync(_flyoutItems ?? entry.Items, wanted, FlyoutItemSync.FocusedIn(wanted));
            HandFocusOnIfGone(focused, wanted, at);
            // The preview follows the live refill alone: a submenu closing fires no list refill of its own, and sending
            // the pointed row's preview again there would put it back after the menu has cleared it.
            if (_tick?.IsRunning == true)
            {
                Feedback.Refresh();
            }
        }

        /// <summary>Where <paramref name="focused"/> sat among the listed aircraft rows, or -1 when it was not one of them.</summary>
        private int ListedIndex(MenuItem? focused)
        {
            if (focused is null)
            {
                return -1;
            }

            int listed = _expanded ? _shown.Count : Math.Min(_shown.Count, GiveWayShownRows);
            for (int i = 0; i < listed; i++)
            {
                if (_rows.TryGetValue(_shown[i].Traffic.Callsign, out MenuItem? row) && ReferenceEquals(row, focused))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Hands keyboard focus from <paramref name="focused"/> once its row can no longer hold it — its aircraft left the
        /// list, <paramref name="at"/> being where its row sat, or went while its row is still listed — to the nearest live
        /// row, "Show n more" when that is all that is left, else nowhere, then takes it out of reach; the highlight follows.
        /// </summary>
        private void HandFocusOnIfGone(MenuItem? focused, List<Control> wanted, int at)
        {
            if ((focused is null) || (at < 0) || _live.Contains((MenuItem)focused))
            {
                return;
            }

            var listed = new HashSet<MenuItem>(_rows.Values);
            List<MenuItem> rows = [.. wanted.OfType<MenuItem>().Where(listed.Contains)];
            int left = rows.IndexOf((MenuItem)focused);
            MenuItem? next;
            if (left >= 0)
            {
                next = rows.Skip(left + 1).FirstOrDefault(_live.Contains) ?? rows.Take(left).LastOrDefault(_live.Contains);
            }
            else
            {
                next = NearestLiveAt(rows, at);
            }

            if (next is not null)
            {
                next.Focus(NavigationMethod.Directional);
            }
            else if ((_more is not null) && wanted.Contains(_more))
            {
                _more.Focus(NavigationMethod.Directional);
            }

            if (focused.Header is GroundTrafficFlyoutRow { Traffic.Callsign: { } callsign })
            {
                Feedback.Unpoint(callsign);
            }

            focused.IsEnabled = false;
        }

        /// <summary>The nearest live row at or above <paramref name="at"/> in <paramref name="rows"/>; null when there is none.</summary>
        private MenuItem? NearestLiveAt(List<MenuItem> rows, int at)
        {
            for (int i = Math.Min(at, rows.Count - 1); i >= 0; i--)
            {
                if (_live.Contains(rows[i]))
                {
                    return rows[i];
                }
            }

            return null;
        }

        /// <summary>
        /// The items <paramref name="entries"/> call for, reusing the row of every aircraft shown before; <paramref name="focused"/>,
        /// the row holding keyboard focus, stays in reach for now even when gone; <paramref name="ownHasRoute"/> is whether
        /// this aircraft has a route the rows' conflict notes are about.
        /// </summary>
        private List<Control> Wanted(List<TrafficEntry> entries, MenuItem? focused, bool ownHasRoute)
        {
            var rows = new Dictionary<string, MenuItem>(StringComparer.Ordinal);
            _live.Clear();
            if (entries.Count == 0)
            {
                _rows = rows;
                return [_empty];
            }

            List<Control> wanted = [_title];
            foreach (TrafficEntry shown in _expanded ? entries : entries.Take(GiveWayShownRows))
            {
                string callsign = shown.Traffic.Callsign;
                MenuItem item = (!rows.ContainsKey(callsign) && _rows.TryGetValue(callsign, out MenuItem? kept)) ? kept : NewRow(callsign);
                rows.TryAdd(callsign, item);
                Present(item, shown, ReferenceEquals(item, focused), ownHasRoute);
                if (shown.IsGone)
                {
                    Feedback.Unpoint(callsign);
                }
                else
                {
                    _live.Add(item);
                }

                wanted.Add(item);
            }

            _rows = rows;
            if (entries.Count > GiveWayShownRows)
            {
                wanted.Add(MoreRow(entries[GiveWayShownRows..]));
            }

            return wanted;
        }

        /// <summary>The "Show n more" row over <paramref name="rest"/>, the entries past the first eight.</summary>
        private MenuItem MoreRow(List<TrafficEntry> rest)
        {
            MenuItem more = _more ??= NewMoreRow();
            var header = new GroundTrafficMoreRow(rest.Count, rest.Max(shown => shown.Traffic.DistanceFeet), _expanded);
            if (!Equals(more.Header, header))
            {
                more.Header = header;
                AutomationProperties.SetName(more, header.ToString());
            }

            return more;
        }

        private MenuItem NewRow(string callsign)
        {
            var item = new MenuItem { HeaderTemplate = GroundTrafficRowTemplate.Instance };
            item.Click += async (_, e) =>
            {
                if (!_live.Contains(item) || (item.Header is not GroundTrafficFlyoutRow row))
                {
                    e.Handled = true;
                    return;
                }

                await ChooseAsync(row.Command, entry, _closeFlyout, context, host);
            };
            Feedback.Track(item, callsign);
            // The rows leave the entry for the strip's own flyout while it is open, out of a style attached to the entry,
            // so each row carries the newcomer edge's style itself.
            item.Styles.Add(
                NewcomerEdgeStyle(
                    x => x.OfType<MenuItem>().Class(NewcomerClass).Template().OfType<Border>().Name(MenuItemLayoutRootPart),
                    Border.BoxShadowProperty
                )
            );
            return item;
        }

        private MenuItem NewMoreRow()
        {
            var more = new MenuItem { HeaderTemplate = GroundTrafficMoreRowTemplate.Instance, StaysOpenOnClick = true };
            more.Click += (_, e) =>
            {
                e.Handled = true;
                _expanded = !_expanded;
                Refill();
            };
            return more;
        }

        /// <summary>
        /// Shows <paramref name="shown"/> in <paramref name="item"/>. The row says no conflict is predicted when this aircraft
        /// has a route (<paramref name="ownHasRoute"/>) that no meet point puts the row's aircraft on; a gone row keeps what
        /// it said.
        /// </summary>
        private static void Present(MenuItem item, TrafficEntry shown, bool holdsFocus, bool ownHasRoute)
        {
            bool noConflict =
                (shown.IsGone && (item.Header is GroundTrafficFlyoutRow before))
                    ? before.NoConflictPredicted
                    : ownHasRoute && (shown.Traffic.MeetPoint is null);
            var row = new GroundTrafficFlyoutRow(shown.Traffic, $"{GiveWayVerb} {shown.Traffic.Callsign}", ShowsMark: true, shown.IsGone)
            {
                NoConflictPredicted = noConflict,
            };
            if (!Equals(item.Header, row))
            {
                item.Header = row;
                MenuCommandText.SetCommand(item, row.Command);
                AutomationProperties.SetName(item, row.ToString());
            }

            ShowState(item, shown, holdsFocus);
        }
    }

    /// <summary>
    /// The Follow… flyout's three rows: the filter box, the hint line and the scrolling list. Typing narrows the list to
    /// the callsigns holding the typed text, ignoring case, and hides a section title with no row left; matching nothing
    /// hides every row and title and the hint says what Enter sends. Enter in the box sends the top row, or, with no
    /// row left, <c>FOLLOWG</c> with the typed text upper-cased. Down from the box moves to the first row, Up from the
    /// first row back to the box; Enter or a click on a row sends it. A row highlights its aircraft while pointed at or
    /// focused, and previews it on the ground views, with no meet point. The section title scrolled past sticks at the
    /// list's top. While the strip's flyout is open the list
    /// refills once a second, held in place while the pointer is inside the flyout: an aircraft keeps its row, by callsign,
    /// moved in place rather than rebuilt, so the filter text, the caret, the scroll offset and the focused row all stay.
    /// A gone row keeps its place in its section and Down, Up and Enter pass it by. A list emptied with nothing typed
    /// shows the muted <see cref="NoTrafficText"/> line.
    /// </summary>
    private sealed class FollowList : IStripFlyoutContent
    {
        private readonly MenuItem _entry;
        private readonly MenuContext _context;
        private readonly IMenuHost _host;
        private readonly TextBox _box;
        private readonly TextBlock _hint;
        private readonly StackPanel _rows = new();
        private readonly ScrollViewer _scroll;
        private readonly Border _sticky;
        private readonly TextBlock _movingTitle = SectionTitle(FollowMovingTitle);
        private readonly TextBlock _parkedTitle = SectionTitle(FollowParkedTitle);
        private readonly TextBlock _emptyLine = EmptyLine();
        private readonly List<(TextBlock Title, List<Button> Rows)> _sections = [];
        private readonly HashSet<Button> _live = [];
        private readonly ScopeFeedbackTracker _feedback;
        private readonly FlyoutLiveTick _tick;
        private Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
        private List<TrafficEntry> _shown = [];
        private TaxiRoute? _ownRoute;
        private FrozenTraffic? _frozen;
        private Action? _closeFlyout;

        public FollowList(MenuItem entry, MenuContext context, IMenuHost host)
        {
            _entry = entry;
            _context = context;
            _host = host;
            _feedback = new ScopeFeedbackTracker(host, callsign => TrafficPreview(callsign, _shown, _ownRoute, false, host));
            _tick = new FlyoutLiveTick(host.Clock, Refill);
            _rows.Styles.Add(GoneRowStyle());
            _rows.Styles.Add(
                NewcomerEdgeStyle(
                    x => x.OfType<Button>().Class(NewcomerClass).Template().OfType<ContentPresenter>().Name(ButtonPresenterPart),
                    ContentPresenter.BoxShadowProperty
                )
            );
            _box = new TextBox
            {
                Name = FollowFilterBoxName,
                PlaceholderText = FollowFilterPlaceholder,
                MinWidth = 220,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(_box, FollowFilterPlaceholder);
            _box.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    ApplyFilter();
                }
            };
            _box.AddHandler(InputElement.KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
            _hint = GroundTrafficLook.Subtle(FollowTopMatchHint, 11);
            _scroll = new ScrollViewer
            {
                Name = FollowListName,
                Content = _rows,
                MaxHeight = FollowShownRows * FollowRowHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            // Raised when the offset, the extent or the viewport changes, so a filter that moves the titles under a scroll
            // offset that stays put still restates the stuck title.
            _scroll.ScrollChanged += (_, _) => UpdateSticky();
            _sticky = new Border
            {
                Name = FollowStickyTitleName,
                Child = SectionTitle(""),
                IsVisible = false,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top,
            };
            _sticky.Bind(Border.BackgroundProperty, _sticky.GetResourceObservable(MenuBackgroundKey));

            var boxLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            boxLine.Children.Add(QuickCommandStrip.GlyphIcon(QuickCommandGlyphs.For(MenuIds.GroundFollow)!));
            boxLine.Children.Add(_box);
            var boxItem = new MenuItem { Header = boxLine, StaysOpenOnClick = true };
            // The flyout focuses its first row as it opens; handed on to the box, the flyout opens ready for typing.
            FlyoutBoxFocus.HandFocusToTheBox(boxItem);
            entry.Items.Add(KeepClicks(boxItem));
            entry.Items.Add(new MenuItem { Header = _hint, IsEnabled = false });
            // A menu row takes keyboard focus back from whatever inside it gets it; the list's row would take it from its
            // buttons, so it is not focusable, and focus stays on the button Down, Up or a click gave it.
            var listItem = new MenuItem
            {
                Header = new Panel { Children = { _scroll, _sticky } },
                StaysOpenOnClick = true,
                Focusable = false,
            };
            // The menu selects the row under the pointer and paints it; this row is the whole list, so it keeps its background
            // at rest and only the pointed row inside shows the pointer.
            listItem.Classes.Add(FollowListRowClass);
            listItem.Styles.Add(PlainListRowStyle());
            entry.Items.Add(KeepClicks(listItem));
        }

        /// <summary>Lists <paramref name="traffic"/>, nearest first, Moving then Parked or holding, and applies the filter to it.</summary>
        public void Fill(List<MenuGroundTrafficRow> traffic) => Show(LiveEntries(traffic));

        /// <inheritdoc />
        public void Opened(ItemCollection items, Action close)
        {
            _closeFlyout = close;
            _tick.Start();
            Refill();
        }

        /// <inheritdoc />
        public void PointerInside(bool inside)
        {
            if (!_tick.IsRunning || (inside == (_frozen is not null)))
            {
                return;
            }

            _frozen = inside ? new FrozenTraffic(_shown) : null;
            if (!inside)
            {
                Refill();
            }
        }

        /// <inheritdoc />
        public void Closed()
        {
            _tick.Stop();
            _frozen = null;
            _closeFlyout = null;
            _box.Text = "";
            _feedback.Clear();
            Refill();
        }

        /// <summary><paramref name="traffic"/>, nearest first, as the Moving rows then the Parked or holding rows.</summary>
        private static List<TrafficEntry> LiveEntries(List<MenuGroundTrafficRow> traffic) =>
            [.. traffic.Where(row => row.IsMoving).Select(TrafficEntry.Live), .. traffic.Where(row => !row.IsMoving).Select(TrafficEntry.Live)];

        private static TextBlock EmptyLine()
        {
            TextBlock line = GroundTrafficLook.Subtle(NoTrafficText, 12);
            line.Padding = new Thickness(8, 4);
            return line;
        }

        /// <summary>Keeps a gone row's background clear, as at rest, rather than the theme's disabled fill: only its fading shows it gone.</summary>
        private static Style GoneRowStyle() =>
            new(x => x.OfType<Button>().Class(":disabled").Template().OfType<ContentPresenter>().Name(ButtonPresenterPart))
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
            };

        private static MenuItem KeepClicks(MenuItem item)
        {
            item.Click += (_, e) => e.Handled = true;
            return item;
        }

        /// <summary>Keeps the list's menu row at its resting background while it is selected or under the pointer.</summary>
        private static Style PlainListRowStyle()
        {
            static Selector LayoutRoot(Selector? x, string state) =>
                x.OfType<MenuItem>().Class(FollowListRowClass).Class(state).Template().OfType<Border>().Name(MenuItemLayoutRootPart);

            return new Style(x => Selectors.Or(LayoutRoot(x, ":selected"), LayoutRoot(x, ":pointerover")))
            {
                Setters = { new Setter(Border.BackgroundProperty, Brushes.Transparent) },
            };
        }

        private static TextBlock SectionTitle(string text) =>
            new()
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = SectionTitleBrush,
                Padding = new Thickness(8, 4),
            };

        private void Refill()
        {
            List<MenuGroundTrafficRow> traffic = FollowTraffic(_context, _host);
            if (_frozen is null)
            {
                Show(LiveEntries(traffic));
                return;
            }

            _frozen.Update(traffic);
            Show(_frozen.Entries);
        }

        /// <summary>
        /// Lists <paramref name="entries"/> in order, each section's title over its run of rows, reusing the row of every
        /// aircraft shown before and moving it in place, then applies the filter.
        /// </summary>
        private void Show(List<TrafficEntry> entries)
        {
            _shown = entries;
            _ownRoute = _host.GetRemainingTaxiRoute(_context.Callsign);
            Button? focused = _buttons.Values.FirstOrDefault(row => row.IsKeyboardFocusWithin);
            int at = (focused is null) ? -1 : ListedRows().IndexOf((Button)focused);
            var buttons = new Dictionary<string, Button>(StringComparer.Ordinal);
            _live.Clear();
            _sections.Clear();
            List<Control> wanted = [];
            List<Button> section = [];
            foreach (TrafficEntry shown in entries)
            {
                TextBlock title = shown.InMovingSection ? _movingTitle : _parkedTitle;
                if ((_sections.Count == 0) || !ReferenceEquals(_sections[^1].Title, title))
                {
                    section = [];
                    _sections.Add((title, section));
                    wanted.Add(title);
                }

                Button row = RowFor(shown, buttons, focused);
                section.Add(row);
                wanted.Add(row);
            }

            if (entries.Count == 0)
            {
                wanted.Add(_emptyLine);
            }

            _buttons = buttons;
            FlyoutItemSync.Sync(_rows.Children, wanted);
            ApplyFilter();
            HandFocusOnIfGone(focused, at);
            // The preview follows the live refill alone, as Give way's does.
            if (_tick.IsRunning)
            {
                _feedback.Refresh();
            }
        }

        /// <summary>
        /// Hands keyboard focus from <paramref name="focused"/> once its row can no longer hold it — its aircraft left the
        /// list, <paramref name="at"/> being where its row sat, or went while its row is still listed — to the nearest live
        /// row below it, else above it, else the filter box, then takes it out of reach; the highlight follows the focus.
        /// </summary>
        private void HandFocusOnIfGone(Button? focused, int at)
        {
            if ((focused is null) || (at < 0) || _live.Contains((Button)focused))
            {
                return;
            }

            List<Button> rows = ListedRows();
            int left = rows.IndexOf((Button)focused);
            Button? next = (left >= 0) ? NextReachable((Button)focused, 1) ?? NextReachable((Button)focused, -1) : NearestLiveAt(rows, at);
            if (next is not null)
            {
                FocusRow((Button)next);
            }
            else
            {
                _box.Focus();
            }

            if (focused.Content is GroundTrafficFlyoutRow { Traffic.Callsign: { } callsign })
            {
                _feedback.Unpoint(callsign);
            }

            focused.IsEnabled = false;
        }

        /// <summary>
        /// The nearest reachable row at or below <paramref name="at"/> in <paramref name="rows"/>, else above it, passing gone
        /// rows and the rows the filter hides; null when there is none.
        /// </summary>
        private Button? NearestLiveAt(List<Button> rows, int at) =>
            rows.Skip(at).FirstOrDefault(Reachable) ?? rows.Take(Math.Min(at, rows.Count)).LastOrDefault(Reachable);

        /// <summary>
        /// The row for <paramref name="shown"/>: the one its aircraft had, unless already taken, else a new one. The row
        /// holding keyboard focus, <paramref name="focused"/>, stays in reach for now even when gone.
        /// </summary>
        private Button RowFor(TrafficEntry shown, Dictionary<string, Button> buttons, Button? focused)
        {
            string callsign = shown.Traffic.Callsign;
            Button row = (!buttons.ContainsKey(callsign) && _buttons.TryGetValue(callsign, out Button? kept)) ? kept : NewRow(callsign);
            buttons.TryAdd(callsign, row);
            var content = new GroundTrafficFlyoutRow(shown.Traffic, $"{FollowVerb} {callsign}", ShowsMark: false, shown.IsGone);
            if (!Equals(row.Content, content))
            {
                row.Content = content;
                AutomationProperties.SetName(row, content.ToString());
            }

            ShowState(row, shown, ReferenceEquals(row, focused));
            if (shown.IsGone)
            {
                _feedback.Unpoint(callsign);
            }
            else
            {
                _live.Add(row);
            }

            return row;
        }

        private Button NewRow(string callsign)
        {
            var button = new Button
            {
                ContentTemplate = GroundTrafficRowTemplate.Instance,
                MinHeight = FollowRowHeight,
                Padding = new Thickness(8, 4),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            button.Click += async (_, e) =>
            {
                e.Handled = true;
                if (_live.Contains(button) && (button.Content is GroundTrafficFlyoutRow row))
                {
                    await Choose(row.Command);
                }
            };
            button.AddHandler(InputElement.KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
            _feedback.Track(button, callsign);
            return button;
        }

        private Task Choose(string command) => ChooseAsync(command, _entry, _closeFlyout, _context, _host);

        /// <summary>The rows Down, Up and Enter reach, in order.</summary>
        private List<Button> VisibleRows() => [.. ListedRows().Where(Reachable)];

        private List<Button> ListedRows() => [.. _sections.SelectMany(section => section.Rows)];

        /// <summary>Whether Down, Up and Enter reach <paramref name="row"/>: it shows and its aircraft is still on the ground.</summary>
        private bool Reachable(Button row) => row.IsVisible && _live.Contains(row);

        /// <summary>
        /// The nearest row Down (<paramref name="step"/> 1) or Up (-1) reaches from <paramref name="row"/>'s place in the list,
        /// passing gone and hidden rows, and from a focused row just gone, where the focus is handed to; null past either end.
        /// </summary>
        private Button? NextReachable(Button row, int step)
        {
            List<Button> rows = ListedRows();
            for (int at = rows.IndexOf(row) + step; (at >= 0) && (at < rows.Count); at += step)
            {
                if (Reachable(rows[at]))
                {
                    return rows[at];
                }
            }

            return null;
        }

        private string Typed() => (_box.Text ?? "").Trim();

        /// <summary>The callsign Enter sends when no row matches: the first typed word, upper-cased; null with nothing typed.</summary>
        private string? TypedCallsign() => Typed().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToUpperInvariant();

        private void ApplyFilter()
        {
            string typed = Typed();
            foreach ((TextBlock title, List<Button> rows) in _sections)
            {
                foreach (Button row in rows)
                {
                    row.IsVisible = ((GroundTrafficFlyoutRow)row.Content!).Traffic.Callsign.Contains(typed, StringComparison.OrdinalIgnoreCase);
                }

                title.IsVisible = rows.Any(row => row.IsVisible);
            }

            bool matchesNone = (VisibleRows().Count == 0) && (TypedCallsign() is not null);
            _hint.Text = matchesNone ? FollowNoMatchHint(TypedCallsign()!) : FollowTopMatchHint;
            _emptyLine.IsVisible = typed.Length == 0;
            // The titles take their new places at the next layout pass, which Loaded runs after.
            Dispatcher.UIThread.Post(UpdateSticky, DispatcherPriority.Loaded);
        }

        /// <summary>The command Enter in the box sends: the top row's, else <c>FOLLOWG</c> with the typed callsign, else none.</summary>
        private string? EnterCommand()
        {
            if (VisibleRows().FirstOrDefault()?.Content is GroundTrafficFlyoutRow top)
            {
                return top.Command;
            }

            return (TypedCallsign() is { } callsign) ? $"{FollowVerb} {callsign}" : null;
        }

        /// <summary>Focuses <paramref name="row"/> and scrolls it into view clear of the section title stuck over the list's top.</summary>
        private void FocusRow(Button row)
        {
            row.Focus();
            double titleHeight = _sections.Max(section => section.Title.Bounds.Height);
            row.BringIntoView(new Rect(0, -titleHeight, row.Bounds.Width, row.Bounds.Height + titleHeight));
        }

        private async void OnBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                if (VisibleRows().FirstOrDefault() is { } first)
                {
                    FocusRow(first);
                }
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (EnterCommand() is { } command)
                {
                    await Choose(command);
                }
            }
        }

        private void OnRowKeyDown(object? sender, KeyEventArgs e)
        {
            if ((e.Key is not (Key.Up or Key.Down)) || (sender is not Button row))
            {
                return;
            }

            e.Handled = true;
            if (NextReachable(row, (e.Key == Key.Down) ? 1 : -1) is { } next)
            {
                FocusRow(next);
            }
            else if (e.Key == Key.Up)
            {
                _box.Focus();
            }
        }

        /// <summary>
        /// Shows the title of the section the list has scrolled into, once its own title has scrolled out of view; a list off
        /// screen, its flyout or submenu closed, stays put.
        /// </summary>
        private void UpdateSticky()
        {
            if (!_scroll.IsAttachedToVisualTree())
            {
                return;
            }

            string? current = null;
            foreach ((TextBlock title, _) in _sections)
            {
                if (title.IsVisible && (title.Bounds.Y < _scroll.Offset.Y))
                {
                    current = title.Text;
                }
            }

            ((TextBlock)_sticky.Child!).Text = current ?? "";
            _sticky.IsVisible = current is not null;
        }
    }
}

/// <summary>A traffic flyout's title row: the bold title and, at its right, a dim note.</summary>
/// <param name="Title">The title, <c>Nearest first</c>.</param>
/// <param name="Note">The note at the right, <c>▶ moving  ■ parked</c>.</param>
public sealed record GroundTrafficFlyoutTitle(string Title, string Note)
{
    /// <inheritdoc />
    public override string ToString() => $"{Title} · {Note}";
}

/// <summary>
/// One aircraft row of a traffic flyout, drawn by <see cref="GroundTrafficRowTemplate"/>: the moving or parked mark when
/// <paramref name="ShowsMark"/>, the callsign with the type dimmed beside it, the state under them, the distance and the
/// command. Its text form, which the row's automation name uses, reads
/// <c>SWA5286 · B738 · taxiing on W · ahead · ~1,100 ft — GW SWA5286</c>, or, once its aircraft is gone,
/// <c>SWA5286 · B738 · gone — GW SWA5286</c>.
/// </summary>
/// <param name="Traffic">The aircraft as the host sees it, or last saw it once gone.</param>
/// <param name="Command">The command the row sends.</param>
/// <param name="ShowsMark">Whether the row leads with the moving (▶) or parked (■) mark.</param>
/// <param name="IsGone">
/// Whether the aircraft has left the ground while the pointer is inside the flyout: the row's state reads
/// <see cref="GroundTrafficFlyout.GoneState"/>, it shows no distance, and it sends nothing.
/// </param>
public sealed record GroundTrafficFlyoutRow(MenuGroundTrafficRow Traffic, string Command, bool ShowsMark, bool IsGone)
{
    /// <summary>The distance the row shows, <c>~1,100 ft</c>; empty once the aircraft is gone.</summary>
    public string Distance => IsGone ? "" : $"~{RelativeGeometry.FeetText(Traffic.DistanceFeet)}";

    /// <summary>The state the row shows under the callsign, <c>taxiing on W · ahead</c>, or <c>gone</c>.</summary>
    public string State => IsGone ? GroundTrafficFlyout.GoneState : Traffic.State;

    /// <summary>
    /// Whether the row says, faint under its state, <see cref="GroundTrafficFlyout.NoConflictPredictedText"/>: a Give way to…
    /// row whose aircraft's route the commanded aircraft's route is not predicted to meet. It stays as it was once the
    /// aircraft is gone, and the row's text form leaves it out.
    /// </summary>
    public bool NoConflictPredicted { get; init; }

    /// <inheritdoc />
    public override string ToString()
    {
        var text = new StringBuilder(Traffic.Callsign);
        if (Traffic.AircraftType.Length > 0)
        {
            text.Append(" · ").Append(Traffic.AircraftType);
        }

        text.Append(" · ").Append(State);
        if (Distance.Length > 0)
        {
            text.Append(" · ").Append(Distance);
        }

        return text.Append(" — ").Append(Command).ToString();
    }
}

/// <summary>
/// The row under the first rows of a traffic flyout: <c>Show 5 more · up to ~3,900 ft</c> while the rest is hidden,
/// <c>Show fewer</c> once it shows.
/// </summary>
/// <param name="Count">How many rows are past the first ones.</param>
/// <param name="FarthestFeet">The distance of the farthest of them.</param>
/// <param name="Expanded">Whether they show.</param>
public sealed record GroundTrafficMoreRow(int Count, double FarthestFeet, bool Expanded)
{
    /// <summary>What the row reads before its note: <c>Show 5 more</c> or <c>Show fewer</c>.</summary>
    public string Label => Expanded ? "Show fewer" : $"Show {Count} more";

    /// <summary>The dim note after the label, <c>up to ~3,900 ft</c>, or null once the rest shows.</summary>
    public string? Note => Expanded ? null : $"up to ~{RelativeGeometry.FeetText(FarthestFeet)}";

    /// <inheritdoc />
    public override string ToString() => (Note is { } note) ? $"{Label} · {note}" : Label;
}

/// <summary>The faces and brushes the traffic flyout templates share.</summary>
internal static class GroundTrafficLook
{
    private const string SubtleTextBrushKey = "SubtleTextBrush";

    /// <summary>The moving mark's colour, the ground family's teal.</summary>
    public static readonly IImmutableSolidColorBrush MovingBrush = new ImmutableSolidColorBrush(
        QuickCommandStrip.FamilyColor(QuickCommandGlyphFamily.Ground)
    );

    /// <summary>The parked mark's colour.</summary>
    public static readonly IImmutableSolidColorBrush ParkedBrush = new ImmutableSolidColorBrush(Color.Parse("#8C95A0"));

    /// <summary>A text in the dim face at <paramref name="fontSize"/>.</summary>
    public static TextBlock Subtle(string text, double fontSize)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable(SubtleTextBrushKey));
        return block;
    }

    /// <summary>A command in the dim monospace face, right-aligned.</summary>
    public static TextBlock Command(string command)
    {
        TextBlock block = Subtle(command, 12);
        block.HorizontalAlignment = HorizontalAlignment.Right;
        block.Bind(TextBlock.FontFamilyProperty, block.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        return block;
    }
}

/// <summary>
/// The view of a <see cref="GroundTrafficFlyoutRow"/>: the mark (▶ teal for moving, ■ grey for parked), the callsign in a
/// semibold face with the type dimmed beside it and the state under them, the distance, then the command in the dim
/// monospace font.
/// </summary>
internal sealed class GroundTrafficRowTemplate : FuncDataTemplate<GroundTrafficFlyoutRow>
{
    /// <summary>The one template every traffic row's menu item uses.</summary>
    public static GroundTrafficRowTemplate Instance { get; } = new();

    private GroundTrafficRowTemplate()
        : base((row, _) => Build(row)) { }

    private static Grid Build(GroundTrafficFlyoutRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        if (row.ShowsMark)
        {
            grid.Children.Add(
                new TextBlock
                {
                    Text = row.Traffic.IsMoving ? "▶" : "■",
                    Width = 14,
                    FontSize = 10,
                    Foreground = row.Traffic.IsMoving ? GroundTrafficLook.MovingBrush : GroundTrafficLook.ParkedBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
        }
        else
        {
            grid.Children.Add(QuickCommandStrip.GlyphIcon(QuickCommandGlyphs.For(MenuIds.GroundFollow)!));
        }

        var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        nameLine.Children.Add(new TextBlock { Text = row.Traffic.Callsign, FontWeight = FontWeight.SemiBold });
        if (row.Traffic.AircraftType.Length > 0)
        {
            TextBlock type = GroundTrafficLook.Subtle(row.Traffic.AircraftType, 12);
            type.Opacity = 0.75;
            nameLine.Children.Add(type);
        }

        var nameColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        nameColumn.Children.Add(nameLine);
        nameColumn.Children.Add(GroundTrafficLook.Subtle(row.State, 11));
        if (row.NoConflictPredicted)
        {
            nameColumn.Children.Add(NoConflictLine());
        }

        Grid.SetColumn(nameColumn, 1);
        grid.Children.Add(nameColumn);

        var distance = new TextBlock { Text = row.Distance, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(distance, 2);
        grid.Children.Add(distance);

        TextBlock command = GroundTrafficLook.Command(row.Command);
        command.Opacity = 0.7;
        Grid.SetColumn(command, 3);
        grid.Children.Add(command);
        return grid;
    }

    /// <summary><see cref="GroundTrafficFlyout.NoConflictPredictedText"/> in the dim face, fainter, on its own line under the state.</summary>
    private static TextBlock NoConflictLine()
    {
        TextBlock note = GroundTrafficLook.Subtle(GroundTrafficFlyout.NoConflictPredictedText, 11);
        note.Opacity = 0.6;
        return note;
    }
}

/// <summary>The view of a <see cref="GroundTrafficFlyoutTitle"/>: the title in a semibold face, the note dim at the right.</summary>
internal sealed class GroundTrafficTitleTemplate : FuncDataTemplate<GroundTrafficFlyoutTitle>
{
    /// <summary>The one template every traffic flyout title uses.</summary>
    public static GroundTrafficTitleTemplate Instance { get; } = new();

    private GroundTrafficTitleTemplate()
        : base((title, _) => Build(title)) { }

    private static Grid Build(GroundTrafficFlyoutTitle title)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(
            new TextBlock
            {
                Text = title.Title,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
            }
        );
        TextBlock note = GroundTrafficLook.Subtle(title.Note, 11);
        Grid.SetColumn(note, 1);
        grid.Children.Add(note);
        return grid;
    }
}

/// <summary>The view of a <see cref="GroundTrafficMoreRow"/>: the label, its dim note, then ▾ (or ▴ once the rest shows) at the right.</summary>
internal sealed class GroundTrafficMoreRowTemplate : FuncDataTemplate<GroundTrafficMoreRow>
{
    /// <summary>The one template every "Show n more" row uses.</summary>
    public static GroundTrafficMoreRowTemplate Instance { get; } = new();

    private GroundTrafficMoreRowTemplate()
        : base((row, _) => Build(row)) { }

    private static Grid Build(GroundTrafficMoreRow row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*,Auto"), ColumnSpacing = 8 };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        label.Children.Add(new TextBlock { Text = row.Label, VerticalAlignment = VerticalAlignment.Center });
        if (row.Note is { } note)
        {
            label.Children.Add(GroundTrafficLook.Subtle(note, 12));
        }

        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        TextBlock chevron = GroundTrafficLook.Subtle(row.Expanded ? "▴" : "▾", 12);
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);
        return grid;
    }
}
