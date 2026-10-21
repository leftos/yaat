using System.Globalization;
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
using Avalonia.Styling;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Which traffic flyout an <see cref="AirborneTrafficFlyout"/> is: Report traffic in sight… (<c>RTIS</c>) or Follow traffic…
/// (<c>FOLLOW</c>).
/// </summary>
public enum AirborneTrafficVerb
{
    ReportInSight,
    Follow,
}

/// <summary>
/// The Report traffic in sight… and Follow traffic… flyouts, twins on one layout, one builder each for All Commands and the
/// quick-command strip: the title line (the verb, then the aircraft, its altitude and heading, and, for Follow outside solo
/// training, the Force toggle), the Traffic box with the command Enter sends, then the other airborne aircraft
/// (<see cref="IMenuHost.GetAirborneTraffic"/>): the <see cref="AirborneTrafficRows.ListedCount"/> nearest within
/// <see cref="AirborneTrafficRows.ListedRadiusNm"/> NM, or, with text typed, every one matching it. A row reads the callsign, the
/// type and the pattern leg, the in sight, not in sight and following notes, then the clock, miles, altitude difference and
/// direction of flight, and sends <c>RTIS {callsign}</c> or <c>FOLLOW {callsign}</c>. Follow lists the aircraft reported in
/// sight first and greys the rest, which send nothing until Force turns them into <c>FOLLOWF {callsign}</c>. A row pointed at
/// or focused shows its radar cue (<see cref="RadarCue"/>) and highlights its aircraft. While the strip's flyout is open the list
/// refills once a second, its order held while the pointer is inside, an aircraft that left staying as a dimmed gone row; the
/// list catches up when the pointer leaves. With no other aircraft airborne the entry is disabled with the reason beside its
/// label and the strip's button is faded and inert.
/// </summary>
public static class AirborneTrafficFlyout
{
    /// <summary>The Report traffic in sight… flyout's title.</summary>
    public const string ReportTitle = "Report traffic in sight";

    /// <summary>The Follow traffic… flyout's title.</summary>
    public const string FollowTitle = "Follow traffic";

    /// <summary>The Force toggle's label.</summary>
    public const string ForceText = "Force";

    /// <summary>The Force toggle's tooltip.</summary>
    public const string ForceTip = "Follow traffic not reported in sight: FOLLOWF folds RTISF in (RPO only)";

    /// <summary>The box's label and accessible name.</summary>
    public const string BoxLabel = "Traffic";

    /// <summary>The box's name, by which a test or UI Automation finds it.</summary>
    public const string BoxName = "AirborneTrafficBox";

    /// <summary>The note under the box while no row it lists can be sent.</summary>
    public const string NotListed = "No listed aircraft: sent as typed";

    /// <summary>The list's header, before the count of rows.</summary>
    public const string GroupTitle = "Airborne · nearest first";

    /// <summary>The mark leading the row of the aircraft reported in sight.</summary>
    public const string InSightMark = "◆";

    /// <summary>The note on the row of the aircraft reported in sight.</summary>
    public const string InSightNote = "in sight";

    /// <summary>The note on a Follow row greyed because its aircraft is not reported in sight.</summary>
    public const string NotInSightNote = "not in sight";

    /// <summary>The note on the row of the aircraft this one follows.</summary>
    public const string FollowingNote = "following";

    /// <summary>A greyed Follow row's tooltip.</summary>
    public const string NotInSightTip = "Traffic not in sight — issue RTIS first";

    /// <summary>What a row reads in place of where its aircraft is once the aircraft has left the list while the pointer is inside.</summary>
    public const string GoneNote = "gone";

    /// <summary>The style class a row carries while it is a newcomer a held list appended, which marks its left edge.</summary>
    public const string NewcomerClass = "newcomer";

    /// <summary>Why a traffic entry offers nothing, after its label.</summary>
    public const string NoTrafficReason = "no other aircraft airborne";

    /// <summary>The muted line an open flyout shows once no other aircraft is airborne.</summary>
    public const string NoTrafficText = "No other aircraft airborne.";

    /// <summary>
    /// The muted line an open flyout shows while aircraft are airborne but none within the listed range
    /// (<see cref="AirborneTrafficRows.ListedRadiusNm"/>), with nothing typed: <c>No other aircraft within 10 NM.</c>
    /// </summary>
    public static string NoneInRangeText =>
        $"No other aircraft within {AirborneTrafficRows.ListedRadiusNm.ToString(CultureInfo.InvariantCulture)} NM.";

    /// <summary>
    /// The entry labelled <paramref name="label"/>: the Report traffic in sight… flyout, or, with no other aircraft airborne,
    /// inert.
    /// </summary>
    public static MenuItem BuildReportInSight(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        Build(label, AirborneTrafficVerb.ReportInSight, aircraft, context, host);

    /// <summary>
    /// The entry labelled <paramref name="label"/>: the Follow traffic… flyout, or, with no other aircraft airborne, inert.
    /// </summary>
    public static MenuItem BuildFollow(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host) =>
        Build(label, AirborneTrafficVerb.Follow, aircraft, context, host);

    /// <summary>The verb's command word: <c>RTIS</c> or <c>FOLLOW</c>.</summary>
    public static string VerbWord(AirborneTrafficVerb verb) => (verb == AirborneTrafficVerb.ReportInSight) ? "RTIS" : "FOLLOW";

    /// <summary>The flyout's title: <see cref="ReportTitle"/> or <see cref="FollowTitle"/>.</summary>
    public static string Title(AirborneTrafficVerb verb) => (verb == AirborneTrafficVerb.ReportInSight) ? ReportTitle : FollowTitle;

    /// <summary>
    /// The line the list shows while the box's text matches no aircraft, naming the <paramref name="command"/> Enter sends:
    /// <c>No aircraft matches; Enter sends RTIS N1 as typed.</c>
    /// </summary>
    public static string NoMatch(string command) => $"No aircraft matches; Enter sends {command} as typed.";

    /// <summary>The faded strip button's label: <c>Report traffic in sight… · no other aircraft airborne</c>.</summary>
    public static string InertLabel(string label) => $"{label} · {NoTrafficReason}";

    /// <summary>
    /// The title line's right side: <c>N52TW · 2,000 · hdg 300</c>, the altitude rounded to 100 ft and the magnetic heading in
    /// three digits; the callsign alone without an aircraft model.
    /// </summary>
    public static string TitleNow(IMenuAircraft? aircraft, string callsign)
    {
        if (aircraft is null)
        {
            return callsign;
        }

        int altitude = (int)(Math.Round(aircraft.AltitudeFeet / 100, MidpointRounding.AwayFromZero) * 100);
        int heading = (int)Math.Round(aircraft.MagneticHeadingDegrees, MidpointRounding.AwayFromZero) % 360;
        string altitudeText = altitude.ToString("N0", CultureInfo.InvariantCulture);
        return $"{callsign} · {altitudeText} · hdg {((heading == 0) ? 360 : heading):000}";
    }

    private static MenuItem Build(string label, AirborneTrafficVerb verb, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        if (host.GetAirborneTraffic(context.Callsign).Count == 0)
        {
            MenuDisabledReasonRow.Apply(entry, label, NoTrafficReason);
            QuickCommandStrip.MarkInert(entry, InertLabel(label));
            return entry;
        }

        AirborneTrafficContent.Attach(entry, verb, aircraft, context, host);
        return entry;
    }

    /// <summary>
    /// The flyout's content on whichever surface shows it: one menu row whose header holds the panel, refilled from the host as
    /// the submenu or the strip's flyout opens and, while the strip's flyout is open, once a second (<see cref="FlyoutLiveTick"/>),
    /// its order held while the pointer is inside. A choice sends and closes the strip's flyout and the menu, or the submenu's menu.
    /// </summary>
    private sealed class AirborneTrafficContent : IStripFlyoutContent
    {
        private readonly MenuItem _entry;
        private readonly MenuContext _context;
        private readonly IMenuHost _host;
        private readonly AirborneTrafficPanel _panel;
        private readonly FlyoutLiveTick _tick;
        private Action? _closeFlyout;
        private bool _open;

        private AirborneTrafficContent(MenuItem entry, AirborneTrafficVerb verb, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
        {
            _entry = entry;
            _context = context;
            _host = host;
            _panel = new AirborneTrafficPanel(verb, aircraft, context, host, ChooseAsync);
            _tick = new FlyoutLiveTick(host.Clock, Refill);
            entry.Items.Add(PanelRow(new AirborneTrafficFlyoutHost(verb, _panel)));
            entry.PropertyChanged += (_, e) =>
            {
                if (e.Property != MenuItem.IsSubMenuOpenProperty)
                {
                    return;
                }

                if (entry.IsSubMenuOpen)
                {
                    Refill();
                    _panel.FocusBoxLater(() => entry.IsSubMenuOpen);
                }
                else
                {
                    _panel.Reset();
                }
            };
        }

        /// <summary>
        /// Gives <paramref name="entry"/> the <paramref name="verb"/>'s flyout, filled from the host, and has the strip open it as a
        /// flyout.
        /// </summary>
        public static void Attach(MenuItem entry, AirborneTrafficVerb verb, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
        {
            var content = new AirborneTrafficContent(entry, verb, aircraft, context, host);
            content.Refill();
            QuickCommandStrip.FollowInFlyout(entry, content);
        }

        /// <inheritdoc />
        public void Opened(ItemCollection items, Action close)
        {
            _closeFlyout = close;
            _open = true;
            _tick.Start();
            Refill();
            _panel.FocusBoxLater(() => _open);
        }

        /// <inheritdoc />
        public void PointerInside(bool inside)
        {
            if (!_tick.IsRunning)
            {
                return;
            }

            _panel.Hold(inside);
            if (!inside)
            {
                Refill();
            }
        }

        /// <inheritdoc />
        public void Closed()
        {
            _tick.Stop();
            _open = false;
            _closeFlyout = null;
            _panel.Reset();
            Refill();
        }

        /// <summary>The menu row holding the panel, which no hover or selection repaints and which never takes keyboard focus.</summary>
        private static MenuItem PanelRow(FlyoutPanelHost holder)
        {
            var panelRow = new MenuItem
            {
                Header = holder,
                StaysOpenOnClick = true,
                Focusable = false,
            };
            panelRow.Classes.Add(holder.PanelRowClass);
            panelRow.Styles.Add(FlyoutPanelParts.PlainPanelRowStyle(holder.PanelRowClass));
            panelRow.Click += (_, e) => e.Handled = true;
            return panelRow;
        }

        private void Refill() => _panel.Update(_host.GetAirborneTraffic(_context.Callsign));

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

/// <summary>A traffic flyout's menu row header, holding its <see cref="AirborneTrafficPanel"/>.</summary>
public sealed class AirborneTrafficFlyoutHost : FlyoutPanelHost
{
    internal AirborneTrafficFlyoutHost(AirborneTrafficVerb verb, AirborneTrafficPanel panel)
        : base($"{AirborneTrafficFlyout.Title(verb).ToLowerInvariant()} flyout", "airborne-traffic-flyout-panel")
    {
        Panel = panel;
        Child = panel;
    }

    /// <summary>The panel the row holds.</summary>
    public AirborneTrafficPanel Panel { get; }
}

/// <summary>
/// One row of a traffic flyout, drawn by <see cref="AirborneTrafficRowTemplate"/>. Its text form, which the row's automation
/// name uses, reads <c>◆ N738SP · C172 · left downwind 28L · in sight — 12 o'clock · 3 nm · 1,000 below · eastbound — FOLLOW N738SP</c>.
/// </summary>
/// <param name="Row">The aircraft as the host lists it, or last listed it once gone.</param>
/// <param name="InSight">Whether the commanded aircraft has reported it in sight (<see cref="AirborneTrafficRows.IsInSight"/>).</param>
/// <param name="Following">Whether the commanded aircraft follows it (<see cref="AirborneTrafficRows.IsFollowing"/>).</param>
/// <param name="Command">The command the row shows: <c>RTIS {callsign}</c>, <c>FOLLOW {callsign}</c> or <c>FOLLOWF {callsign}</c>.</param>
/// <param name="IsGreyed">Whether the row is a Follow row greyed for traffic not reported in sight, which sends nothing.</param>
/// <param name="IsGone">Whether its aircraft has left the list while the pointer is inside the flyout, which sends nothing.</param>
public sealed record AirborneTrafficEntry(MenuAirborneTrafficRow Row, bool InSight, bool Following, string Command, bool IsGreyed, bool IsGone)
{
    /// <summary>The aircraft's callsign.</summary>
    public string Callsign => Row.Callsign;

    /// <summary>Whether a click sends <see cref="Command"/>: the row is neither greyed nor gone.</summary>
    public bool SendsCommand => !IsGreyed && !IsGone;

    /// <summary>The notes after the name: <c>in sight</c>, <c>not in sight</c>, <c>following</c>, where they apply.</summary>
    public IReadOnlyList<string> Notes
    {
        get
        {
            List<string> notes = [];
            if (InSight)
            {
                notes.Add(AirborneTrafficFlyout.InSightNote);
            }

            if (IsGreyed)
            {
                notes.Add(AirborneTrafficFlyout.NotInSightNote);
            }

            if (Following)
            {
                notes.Add(AirborneTrafficFlyout.FollowingNote);
            }

            return notes;
        }
    }

    /// <summary>
    /// The second line: <c>12 o'clock · 3 nm · 1,000 below · eastbound</c> (<see cref="RelativeGeometry.AltitudeDeltaColumn"/>),
    /// or <see cref="AirborneTrafficFlyout.GoneNote"/> once gone.
    /// </summary>
    public string Where =>
        IsGone
            ? AirborneTrafficFlyout.GoneNote
            : $"{Row.Traffic.ClockPosition} o'clock · {RelativeGeometry.WholeNm(Row.Traffic.DistanceNm)} nm · "
                + $"{RelativeGeometry.AltitudeDeltaColumn(Row.Traffic.AltitudeDeltaFeet)} · {Row.Direction}";

    /// <inheritdoc />
    public override string ToString()
    {
        var parts = new List<string> { Callsign };
        if (Row.Traffic.AircraftType.Length > 0)
        {
            parts.Add(Row.Traffic.AircraftType);
        }

        if (Row.Leg is { } leg)
        {
            parts.Add(leg);
        }

        parts.AddRange(Notes);
        string mark = InSight ? $"{AirborneTrafficFlyout.InSightMark} " : "";
        return $"{mark}{string.Join(" · ", parts)} — {Where} — {Command}";
    }
}

/// <summary>
/// One aircraft row of a traffic flyout: a click, or Enter while focused, sends its command unless it is greyed or gone. A greyed
/// row stays dimmed, out of the keyboard's reach and tipped <see cref="AirborneTrafficFlyout.NotInSightTip"/>; a gone row is dimmed
/// and disabled.
/// </summary>
public sealed class AirborneTrafficRowButton : Button
{
    /// <summary>The height of one row, in px.</summary>
    public const double RowHeight = 40;

    internal AirborneTrafficRowButton(AirborneTrafficEntry entry, Func<AirborneTrafficRowButton, Task> chosen)
    {
        Entry = entry;
        Present(entry);
        ContentTemplate = AirborneTrafficRowTemplate.Instance;
        MinHeight = RowHeight;
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        CornerRadius = new CornerRadius(0);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Click += async (_, e) =>
        {
            e.Handled = true;
            await chosen(this);
        };
    }

    /// <summary>What the row shows.</summary>
    public AirborneTrafficEntry Entry { get; private set; }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>
    /// Shows <paramref name="entry"/>, marked as a newcomer a held list appended when <paramref name="isNew"/>. A gone row that
    /// <paramref name="holdsFocus"/> stays in reach until the panel has handed the focus on, since a row taken out of reach drops
    /// the focus altogether.
    /// </summary>
    internal void Show(AirborneTrafficEntry entry, bool isNew, bool holdsFocus)
    {
        if (!Equals(Entry, entry))
        {
            Entry = entry;
            Present(entry);
        }

        IsEnabled = !entry.IsGone || holdsFocus;
        Focusable = !entry.IsGreyed;
        Opacity = entry.SendsCommand ? 1 : MenuGlyphRowTemplate.DimmedOpacity;
        ToolTip.SetTip(this, (entry.IsGreyed && !entry.IsGone) ? AirborneTrafficFlyout.NotInSightTip : null);
        Classes.Set(AirborneTrafficFlyout.NewcomerClass, isNew);
    }

    private void Present(AirborneTrafficEntry entry)
    {
        Content = entry;
        AutomationProperties.SetName(this, entry.ToString());
        MenuCommandText.SetCommand(this, entry.SendsCommand ? entry.Command : null);
    }
}

/// <summary>
/// The view of an <see cref="AirborneTrafficEntry"/>: the in-sight mark, then the callsign in a semibold face with the type, the
/// leg and the notes dimmed beside it and where the aircraft is under them, then the command in the dim monospace font.
/// </summary>
internal sealed class AirborneTrafficRowTemplate : FuncDataTemplate<AirborneTrafficEntry>
{
    /// <summary>The one template every traffic flyout row uses.</summary>
    public static AirborneTrafficRowTemplate Instance { get; } = new();

    private AirborneTrafficRowTemplate()
        : base((entry, _) => Build(entry)) { }

    private static Grid Build(AirborneTrafficEntry entry)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,*,Auto"),
            ColumnSpacing = 6,
            Margin = new Thickness(11, 3),
        };
        grid.Children.Add(
            new TextBlock
            {
                Text = entry.InSight ? AirborneTrafficFlyout.InSightMark : "",
                FontSize = 10,
                Foreground = FlyoutPanelParts.NowEdge,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );

        var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        nameLine.Children.Add(new TextBlock { Text = entry.Callsign, FontWeight = FontWeight.SemiBold });
        foreach (string dim in NameNotes(entry))
        {
            nameLine.Children.Add(GroundTrafficLook.Subtle($"· {dim}", 12));
        }

        var nameColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        nameColumn.Children.Add(nameLine);
        nameColumn.Children.Add(GroundTrafficLook.Subtle(entry.Where, 11));
        Grid.SetColumn(nameColumn, 1);
        grid.Children.Add(nameColumn);

        TextBlock command = FlyoutPanelParts.CommandText(entry.Command);
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);
        return grid;
    }

    private static IEnumerable<string> NameNotes(AirborneTrafficEntry entry)
    {
        if (entry.Row.Traffic.AircraftType.Length > 0)
        {
            yield return entry.Row.Traffic.AircraftType;
        }

        if (entry.Row.Leg is { } leg)
        {
            yield return leg;
        }

        foreach (string note in entry.Notes)
        {
            yield return note;
        }
    }
}

/// <summary>
/// A traffic flyout's panel (<see cref="AirborneTrafficFlyout"/>): the title line, the Traffic box with the command Enter sends and
/// its note, a separator, the list's header, the scrolling rows, then the no-match or empty line. Typing filters the rows
/// (<see cref="AirborneTrafficRows.Listed"/>); the keyboard walks them (Down from the box onto the first row that sends, Down and
/// Up through those rows, Up from the top one back to the box, Enter on a row sends it, Enter in the box sends the top row's
/// command, else the verb with the typed callsign, else nothing).
/// </summary>
public sealed class AirborneTrafficPanel : StackPanel
{
    /// <summary>A newcomer row's mark: a 2 px edge in the panel's accent blue at 85%, drawn inside the row's left side.</summary>
    private static readonly BoxShadows NewcomerEdge = new(
        new BoxShadow
        {
            IsInset = true,
            OffsetX = 2,
            Color = Color.FromArgb(217, FlyoutPanelParts.NowEdge.Color.R, FlyoutPanelParts.NowEdge.Color.G, FlyoutPanelParts.NowEdge.Color.B),
        }
    );

    private readonly AirborneTrafficVerb _verb;
    private readonly IMenuAircraft? _aircraft;
    private readonly MenuContext _context;
    private readonly Func<string, Task> _choose;
    private readonly RadarCueTracker _cue;
    private readonly TextBlock _titleNow = new();
    private readonly TextBlock _groupCount = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly Grid _groupLine;
    private readonly StackPanel _list = new();
    private readonly TextBlock _emptyText = MutedLine();
    private IReadOnlyList<MenuAirborneTrafficRow> _traffic = [];
    private Dictionary<string, AirborneTrafficRowButton> _buttons = new(StringComparer.Ordinal);
    private List<AirborneTrafficRowButton> _rows = [];
    private List<Listing> _shown = [];
    private List<Listing>? _held;

    internal AirborneTrafficPanel(AirborneTrafficVerb verb, IMenuAircraft? aircraft, MenuContext context, IMenuHost host, Func<string, Task> choose)
    {
        _verb = verb;
        _aircraft = aircraft;
        _context = context;
        _choose = choose;
        _cue = new RadarCueTracker(host, CueFor);
        _list.Styles.Add(
            new Style(x =>
                x.OfType<Button>().Class(AirborneTrafficFlyout.NewcomerClass).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter")
            )
            {
                Setters = { new Setter(ContentPresenter.BoxShadowProperty, NewcomerEdge) },
            }
        );
        MinWidth = 340;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        if ((verb == AirborneTrafficVerb.Follow) && !context.SoloTrainingMode)
        {
            ForceToggle = NewForceToggle();
        }

        Children.Add(TitleLine());
        Box = new TextBox
        {
            Name = AirborneTrafficFlyout.BoxName,
            PlaceholderText = GroundTrafficFlyout.FollowFilterPlaceholder,
            Width = 230,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(Box, AirborneTrafficFlyout.BoxLabel);
        TakeoffFlyout.KeepClicksInside(Box);
        string glyphId = (verb == AirborneTrafficVerb.ReportInSight) ? MenuIds.ApproachReportTrafficInSight : MenuIds.PatternFollow;
        QuickCommandGlyph glyph = QuickCommandGlyphs.For(glyphId) ?? throw new InvalidOperationException($"No {glyphId} glyph.");
        Children.Add(FlyoutPanelParts.BoxRow(glyph, AirborneTrafficFlyout.BoxLabel, Box, BoxCommandText));
        Children.Add(BoxNote);
        Children.Add(FlyoutPanelParts.SeparatorLine());
        _groupLine = GroupLineView();
        Children.Add(_groupLine);
        Children.Add(
            new ScrollViewer
            {
                Content = _list,
                MaxHeight = AirborneTrafficRows.ListedCount * AirborneTrafficRowButton.RowHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            }
        );
        Children.Add(NoMatchText);
        Children.Add(_emptyText);
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnTyped();
            }
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The flyout's title: <see cref="AirborneTrafficFlyout.ReportTitle"/> or <see cref="AirborneTrafficFlyout.FollowTitle"/>.</summary>
    public string Title => AirborneTrafficFlyout.Title(_verb);

    /// <summary>The title line's right side (<see cref="AirborneTrafficFlyout.TitleNow"/>).</summary>
    public string TitleNow => _titleNow.Text ?? "";

    /// <summary>The Force toggle; null on Report traffic in sight… and in solo training.</summary>
    public CheckBox? ForceToggle { get; }

    /// <summary>The box.</summary>
    public TextBox Box { get; }

    /// <summary>The command column beside the box: what Enter sends, or <c>RTIS ···</c>, faded, while the box is blank.</summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>The note under the box, <see cref="AirborneTrafficFlyout.NotListed"/>, shown while no listed row can be sent.</summary>
    public TextBlock BoxNote { get; } =
        new()
        {
            Text = AirborneTrafficFlyout.NotListed,
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            Margin = new Thickness(33, 0, 11, 2),
            IsVisible = false,
        };

    /// <summary>The line closing the list while the box's text matches no aircraft (<see cref="AirborneTrafficFlyout.NoMatch"/>).</summary>
    public TextBlock NoMatchText { get; } = MutedLine();

    /// <summary>The list's header as one line: <c>Airborne · nearest first · 8</c>.</summary>
    public string GroupLine => $"{AirborneTrafficFlyout.GroupTitle} · {_groupCount.Text}";

    /// <summary>The rows, in list order.</summary>
    public IReadOnlyList<AirborneTrafficRowButton> Rows => _rows;

    /// <summary>Whether Force is on.</summary>
    private bool IsForced => ForceToggle?.IsChecked == true;

    /// <summary>
    /// Follows <paramref name="traffic"/>, the host's airborne aircraft, nearest first: the rows are rebuilt from it, or, while
    /// held, keep their order with their text updated in place, an aircraft no longer listed staying as a gone row and a newcomer
    /// joining the bottom.
    /// </summary>
    internal void Update(IReadOnlyList<MenuAirborneTrafficRow> traffic)
    {
        _traffic = traffic;
        _titleNow.Text = AirborneTrafficFlyout.TitleNow(_aircraft, _context.Callsign);
        Show((_held is { } held) ? Held(held) : Live());
    }

    /// <summary>Holds the rows' order while <paramref name="inside"/> (the pointer is over the flyout), or lets it go.</summary>
    internal void Hold(bool inside) => _held = inside ? [.. _shown] : null;

    /// <summary>Puts the flyout back as it opens: the order let go, Force off, the box empty, no radar cue.</summary>
    internal void Reset()
    {
        _held = null;
        ForceToggle?.IsChecked = false;
        Box.Text = "";
        _cue.Clear();
    }

    /// <summary>Focuses the box once laid out and while <paramref name="isOpen"/> still holds.</summary>
    internal void FocusBoxLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(Box, isOpen, () => { });

    private static TextBlock MutedLine() =>
        new()
        {
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(11, 4, 11, 2),
            IsVisible = false,
        };

    private Grid TitleLine()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(11, 6, 11, 2) };
        grid.Children.Add(
            new TextBlock
            {
                Text = Title,
                FontSize = 11,
                Foreground = FlyoutPanelParts.TitleBrush,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        _titleNow.FontSize = 11;
        _titleNow.Margin = new Thickness(12, 0, 0, 0);
        _titleNow.VerticalAlignment = VerticalAlignment.Center;
        FlyoutPanelParts.Mono(_titleNow);
        Grid.SetColumn(_titleNow, 1);
        grid.Children.Add(_titleNow);
        if (ForceToggle is { } force)
        {
            Grid.SetColumn(force, 2);
            grid.Children.Add(force);
        }

        return grid;
    }

    private CheckBox NewForceToggle()
    {
        var toggle = new CheckBox
        {
            Content = AirborneTrafficFlyout.ForceText,
            FontSize = 11,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(toggle, AirborneTrafficFlyout.ForceTip);
        AutomationProperties.SetName(toggle, AirborneTrafficFlyout.ForceText);
        toggle.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty)
            {
                Show(_shown);
            }
        };
        return toggle;
    }

    private Grid GroupLineView()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(11, 4, 11, 2) };
        grid.Children.Add(
            new TextBlock
            {
                Text = AirborneTrafficFlyout.GroupTitle,
                FontSize = 11,
                Foreground = FlyoutPanelParts.TitleBrush,
            }
        );
        FlyoutPanelParts.Mono(_groupCount);
        Grid.SetColumn(_groupCount, 1);
        grid.Children.Add(_groupCount);
        return grid;
    }

    private string Typed() => (Box.Text ?? "").Trim();

    /// <summary>
    /// What the box sends after the verb when no row can be sent: every typed word, upper-cased and single-spaced, since RTIS also
    /// takes a descriptive form (<c>2 4 W SR22 35</c>); null with nothing typed.
    /// </summary>
    private string? TypedAsSent()
    {
        string[] words = Typed().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return (words.Length == 0) ? null : string.Join(' ', words).ToUpperInvariant();
    }

    /// <summary>The verb the box sends its typed text with: <c>FOLLOWF</c> while Force is on, else the flyout's own verb.</summary>
    private string TypedVerb() => IsForced ? "FOLLOWF" : AirborneTrafficFlyout.VerbWord(_verb);

    /// <summary>The aircraft Follow traffic… lists first, the one reported in sight; null for Report traffic in sight… or none.</summary>
    private string? Lead() =>
        (
            (_verb == AirborneTrafficVerb.Follow)
            && (_aircraft?.LastReportedTrafficCallsign is { } reported)
            && AirborneTrafficRows.IsInSight(_aircraft, reported)
        )
            ? reported
            : null;

    private List<Listing> Live() => [.. AirborneTrafficRows.Listed(_traffic, Typed(), Lead()).Select(row => new Listing(row, false, false))];

    /// <summary>
    /// The <paramref name="heldRows"/> brought up to the host's traffic, a gone one where its aircraft is no longer listed, then
    /// the newcomers, marked new.
    /// </summary>
    private List<Listing> Held(List<Listing> heldRows)
    {
        var listed = new Dictionary<string, MenuAirborneTrafficRow>(StringComparer.Ordinal);
        foreach (MenuAirborneTrafficRow row in _traffic)
        {
            listed.TryAdd(row.Callsign, row);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<Listing> held = [];
        foreach (Listing listing in heldRows)
        {
            seen.Add(listing.Row.Callsign);
            held.Add(
                listed.TryGetValue(listing.Row.Callsign, out MenuAirborneTrafficRow? now)
                    ? listing with
                    {
                        Row = now,
                        IsGone = false,
                    }
                    : listing with
                    {
                        IsGone = true,
                    }
            );
        }

        held.AddRange(
            AirborneTrafficRows.Listed(_traffic, Typed(), Lead()).Where(row => seen.Add(row.Callsign)).Select(row => new Listing(row, false, true))
        );
        _held = held;
        return held;
    }

    private void OnTyped()
    {
        Show(Live());
        if (_held is not null)
        {
            _held = [.. _shown];
        }
    }

    private AirborneTrafficEntry EntryFor(Listing listing)
    {
        string callsign = listing.Row.Callsign;
        bool inSight = AirborneTrafficRows.IsInSight(_aircraft, callsign);
        bool following = AirborneTrafficRows.IsFollowing(_aircraft, callsign);
        (string command, bool greyed) = _verb switch
        {
            AirborneTrafficVerb.ReportInSight => ($"RTIS {callsign}", false),
            _ when inSight => ($"FOLLOW {callsign}", false),
            _ when IsForced => ($"FOLLOWF {callsign}", false),
            _ => ($"FOLLOW {callsign}", true),
        };
        return new AirborneTrafficEntry(listing.Row, inSight, following, command, greyed, listing.IsGone);
    }

    /// <summary>
    /// Lists <paramref name="listings"/> in order, reusing the row of every aircraft shown before, then restates the box and the
    /// lines. A row that leaves, or goes gone, under the pointer raises no pointer exit, so its cue is let go here.
    /// </summary>
    private void Show(List<Listing> listings)
    {
        AirborneTrafficRowButton? focused = _rows.FirstOrDefault(row => row.IsKeyboardFocusWithin);
        int at = (focused is null) ? -1 : _rows.IndexOf(focused);
        _shown = listings;
        var buttons = new Dictionary<string, AirborneTrafficRowButton>(StringComparer.Ordinal);
        List<AirborneTrafficRowButton> rows = [];
        foreach (Listing listing in listings)
        {
            string callsign = listing.Row.Callsign;
            AirborneTrafficEntry entry = EntryFor(listing);
            AirborneTrafficRowButton row =
                (!buttons.ContainsKey(callsign) && _buttons.TryGetValue(callsign, out AirborneTrafficRowButton? kept)) ? kept : NewRow(entry);
            buttons.TryAdd(callsign, row);
            row.Show(entry, listing.IsNew, ReferenceEquals(row, focused));
            if (listing.IsGone)
            {
                _cue.Unpoint(callsign);
            }

            rows.Add(row);
        }

        List<string> removed = [.. _buttons.Keys.Where(callsign => !buttons.ContainsKey(callsign))];
        _buttons = buttons;
        foreach (string callsign in removed)
        {
            _cue.Unpoint(callsign);
        }

        _rows = rows;
        FlyoutItemSync.Sync(_list.Children, [.. rows]);
        HandFocusOnIfGone(focused, at);
        ShowLines();
    }

    /// <summary>
    /// Hands keyboard focus from <paramref name="focused"/> once its row can no longer hold it — its aircraft left the list,
    /// <paramref name="at"/> being where its row sat, or went while its row is still listed — to the nearest row that sends below
    /// it, else above it, else the box, then takes it out of reach.
    /// </summary>
    private void HandFocusOnIfGone(AirborneTrafficRowButton? focused, int at)
    {
        if ((focused is null) || (_rows.Contains(focused) && focused.Entry.SendsCommand))
        {
            return;
        }

        int from = _rows.Contains(focused) ? _rows.IndexOf(focused) : at;
        AirborneTrafficRowButton? next =
            _rows.Skip(from + 1).FirstOrDefault(Reachable) ?? _rows.Take(Math.Min(from, _rows.Count)).LastOrDefault(Reachable);
        if (next is not null)
        {
            next.Focus(NavigationMethod.Directional);
        }
        else
        {
            Box.Focus(NavigationMethod.Directional);
        }

        _cue.Unpoint(focused.Entry.Callsign);
        if (focused.Entry.IsGone)
        {
            focused.IsEnabled = false;
        }
    }

    /// <summary>Restates the list's header, the no-match and empty lines, and the box's command and note.</summary>
    private void ShowLines()
    {
        string typed = Typed();
        _groupCount.Text = _rows.Count.ToString(CultureInfo.InvariantCulture);
        _groupLine.IsVisible = _rows.Count > 0;
        NoMatchText.Text = AirborneTrafficFlyout.NoMatch($"{TypedVerb()} {TypedAsSent()}");
        NoMatchText.IsVisible = (typed.Length > 0) && (_rows.Count == 0);
        _emptyText.Text = (_traffic.Count == 0) ? AirborneTrafficFlyout.NoTrafficText : AirborneTrafficFlyout.NoneInRangeText;
        _emptyText.IsVisible = (typed.Length == 0) && (_rows.Count == 0);
        string? command = BoxCommand();
        BoxCommandText.Text = command ?? $"{AirborneTrafficFlyout.VerbWord(_verb)} {TakeoffFlyout.Unfilled}";
        BoxCommandText.Opacity = (command is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        BoxNote.IsVisible = (typed.Length > 0) && (_rows.FirstOrDefault(Reachable) is null);
        MenuCommandText.SetCommand(Box, command);
    }

    /// <summary>
    /// The command Enter in the box sends: the top row's that sends, else the verb (<c>FOLLOWF</c> under Force) with the typed
    /// text; null while blank.
    /// </summary>
    private string? BoxCommand()
    {
        if (TypedAsSent() is not { } typed)
        {
            return null;
        }

        return _rows.FirstOrDefault(Reachable)?.Entry.Command ?? $"{TypedVerb()} {typed}";
    }

    private static bool Reachable(AirborneTrafficRowButton row) => row.Entry.SendsCommand;

    private AirborneTrafficRowButton NewRow(AirborneTrafficEntry entry)
    {
        var row = new AirborneTrafficRowButton(entry, SendRowAsync);
        _cue.Track(row, entry.Callsign);
        return row;
    }

    private Task SendRowAsync(AirborneTrafficRowButton row) => row.Entry.SendsCommand ? _choose(row.Entry.Command) : Task.CompletedTask;

    /// <summary>The cue for the row of <paramref name="callsign"/>: the bearing line to it from this aircraft; none once gone.</summary>
    private RadarCue? CueFor(string callsign) =>
        (_buttons.TryGetValue(callsign, out AirborneTrafficRowButton? row) && !row.Entry.IsGone)
            ? new RadarCue(_context.Callsign, callsign, null, true)
            : null;

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        List<AirborneTrafficRowButton> rows = [.. _rows.Where(Reachable)];
        int focused = rows.FindIndex(row => row.IsKeyboardFocusWithin);
        bool inBox = ReferenceEquals(e.Source, Box) || Box.IsKeyboardFocusWithin;
        switch (e.Key)
        {
            case Key.Enter when inBox:
                e.Handled = true;
                if (BoxCommand() is { } command)
                {
                    await _choose(command);
                }

                break;
            case Key.Enter when focused >= 0:
                e.Handled = true;
                await SendRowAsync(rows[focused]);
                break;
            case Key.Down or Key.Up:
                Walk(e, rows, inBox, focused);
                break;
            case Key.Tab when ForceToggle is { } force:
                TabAround(e, force, rows, inBox, focused);
                break;
        }
    }

    /// <summary>
    /// Tab order for Follow traffic… with its Force toggle, which sits in the title line above the box: the box, then Force, then
    /// the first row that sends; Shift+Tab walks back. Elsewhere Tab keeps its own course.
    /// </summary>
    private void TabAround(KeyEventArgs e, CheckBox force, List<AirborneTrafficRowButton> rows, bool inBox, int focused)
    {
        bool back = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        Control? next = (back, inBox, force.IsKeyboardFocusWithin, focused) switch
        {
            (false, true, _, _) => force,
            (false, false, true, _) => rows.FirstOrDefault(),
            (true, false, true, _) => Box,
            (true, false, false, 0) => force,
            _ => null,
        };
        if (next is null)
        {
            return;
        }

        e.Handled = true;
        next.Focus(NavigationMethod.Tab);
    }

    /// <summary>Down from the box focuses the first row that sends; Down and Up walk those rows; Up from the top one returns to the box.</summary>
    private void Walk(KeyEventArgs e, List<AirborneTrafficRowButton> rows, bool inBox, int focused)
    {
        int? next = (inBox, focused, e.Key) switch
        {
            (true, _, Key.Down) when rows.Count > 0 => 0,
            (false, 0, Key.Up) => -1,
            (false, >= 0, Key.Up) => focused - 1,
            (false, >= 0, Key.Down) => Math.Min(focused + 1, rows.Count - 1),
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

        rows[target].Focus(NavigationMethod.Directional);
        rows[target].BringIntoView();
    }

    /// <summary>
    /// One aircraft the list shows: as last listed, whether it has left the list while the order is held, and whether a held
    /// list appended it as a newcomer.
    /// </summary>
    private sealed record Listing(MenuAirborneTrafficRow Row, bool IsGone, bool IsNew);
}
