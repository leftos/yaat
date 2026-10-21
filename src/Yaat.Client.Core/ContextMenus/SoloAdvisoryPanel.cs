using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Yaat.Client.Services;
using Yaat.Sim.Commands;
using Yaat.Sim.Phases;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// Report traffic in sight… in a solo-training session: the student says the advisory. The box takes the call as the student
/// would say it (<c>2 4 w sr22 35</c>, <c>base l 2 28l c152</c>, <c>over vpcol sr22</c>); the ghost under it offers the next part,
/// which Tab takes from the pointed row as the session's pre-fill setting allows, or the row a typed callsign or type picks; the
/// spoken line reads a complete call as the pilot hears it; with the session's live check on, the check line names the aircraft
/// the call resolves to and grades each part, 300 ms after typing stops and again after each burst of aircraft updates while no
/// typing check is pending, flashing when an update changes its text. The folded builder below sets the same call part by part.
/// The list of other airborne aircraft points the advisory at a row; no row sends anything. Enter sends <c>RTIS</c> with the
/// call; a refusal, or a send that never reached the sim, keeps the flyout open with the reply and the call as sent.
/// </summary>
public sealed class SoloAdvisoryPanel : StackPanel
{
    /// <summary>The mark beside the title naming the session solo.</summary>
    public const string SoloBadge = "solo";

    /// <summary>The box's label.</summary>
    public const string BoxLabel = "Advisory";

    /// <summary>The note beside the box's label.</summary>
    public const string BoxSub = "as you would say it";

    /// <summary>The box's name, by which a test or UI Automation finds it.</summary>
    public const string BoxName = "SoloAdvisoryBox";

    /// <summary>The box's placeholder.</summary>
    public const string Placeholder = "2 4 w sr22 35, or a callsign";

    /// <summary>The command column of the row the call resolves to.</summary>
    public const string YourCall = "◎ your call";

    /// <summary>The mark on the row the advisory is pointed at.</summary>
    public const string PointedMark = "●";

    /// <summary>What a row's second line ends with while values are left for the student.</summary>
    public const string FindOnScope = "find it on the scope";

    /// <summary>The builder's header.</summary>
    public const string BuilderTitle = "Builder";

    /// <summary>The parts the folded builder names.</summary>
    public const string BuilderParts = "clock · miles · direction · type · altitude";

    /// <summary>The builder's hint while no row is pointed at.</summary>
    public const string BuilderHint = "Point at an aircraft below to seed the builder; a click pins it.";

    /// <summary>The note under the advisory when the send never reached the sim; the call is kept.</summary>
    public const string NotSentNote = "Not sent: connection error (see status line)";

    /// <summary>How long after the last keystroke the live check runs.</summary>
    public static readonly TimeSpan CheckDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the check line stays lit after its text changes on an aircraft update.</summary>
    public static readonly TimeSpan FlashTime = TimeSpan.FromMilliseconds(600);

    /// <summary>The panel's width, which with the flyout's edges draws the mock's 440 px flyout.</summary>
    private const double PanelWidth = 432;

    /// <summary>The widest the box's command column draws before it ends in an ellipsis.</summary>
    private const double CommandMaxWidth = 170;

    /// <summary>How faint a form tab draws while the pointed aircraft cannot take its form.</summary>
    private const double DisabledTabOpacity = 0.36;

    /// <summary>The ghost's words drawn as key caps.</summary>
    private static readonly string[] KeyWords = ["Tab", "Enter"];

    private static readonly IBrush FlashBrush = new SolidColorBrush(Color.FromArgb(70, 0x5B, 0x8D, 0xEF));
    private static readonly IBrush GoodBrush = new SolidColorBrush(Color.Parse("#7fd1b9"));
    private static readonly IBrush StrongBrush = new SolidColorBrush(Color.Parse("#E4E7EB"));
    private static readonly IBrush BadgeLine = new SolidColorBrush(Color.Parse("#3F6F62"));
    private static readonly IBrush KeyCapLine = new SolidColorBrush(Color.Parse("#4A4F57"));
    private static readonly IBrush KeyCapText = new SolidColorBrush(Color.Parse("#CFD5DC"));
    private static readonly IBrush OffBrush = new SolidColorBrush(Color.Parse("#FFC062"));
    private static readonly IBrush BadBrush = new SolidColorBrush(Color.Parse("#ff8a80"));

    private static readonly (AdvisoryForm Form, string Label)[] Forms =
    [
        (AdvisoryForm.Radar, "Radar clock"),
        (AdvisoryForm.Nose, "Nose NR"),
        (AdvisoryForm.Pattern, "Pattern DW"),
        (AdvisoryForm.Over, "Over fix"),
    ];

    private readonly IMenuAircraft? _aircraft;
    private readonly MenuContext _context;
    private readonly IMenuHost _host;
    private readonly Func<string, Task<CommandResultDto?>> _send;
    private readonly Action _close;
    private readonly TextBlock _titleNow = new();
    private readonly TextBlock _ghostView = new()
    {
        FontSize = 11,
        Foreground = FlyoutPanelParts.TitleBrush,
        Margin = new Thickness(0, 1, 0, 0),
    };
    private readonly WrapPanel _checkLine = new() { Orientation = Orientation.Horizontal };
    private readonly Border _checkBorder;
    private readonly StackPanel _list = new();
    private readonly TextBlock _groupCount = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly Grid _groupLine;
    private readonly TextBlock _emptyText = MutedLine();
    private readonly StackPanel _builderBody = new() { Margin = new Thickness(11, 2, 11, 4), IsVisible = false };
    private readonly Dictionary<string, IReadOnlyList<string>> _pointsNear = new(StringComparer.Ordinal);
    private readonly List<Button> _formTabs = [];
    private IReadOnlyList<MenuAirborneTrafficRow> _traffic = [];
    private IReadOnlyList<TrafficAdvisoryCandidate> _candidates = [];
    private Dictionary<string, SoloTrafficRowButton> _rows = new(StringComparer.Ordinal);
    private List<SoloTrafficRowButton> _rowOrder = [];
    private List<string>? _held;
    private AdvisoryCheck? _check;
    private string? _checkedInput;
    private string? _pinned;
    private string? _hovered;
    private string? _focused;
    private long _typedAt;
    private ITimer? _debounce;
    private ITimer? _flash;
    private long _litAt;
    private IDisposable? _updates;
    private bool _composing;
    private bool _sending;
    private int _opening;
    private AdvisoryForm _builderForm = AdvisoryForm.Radar;
    private Dictionary<AdvisoryPart, string> _built = [];
    private (AdvisoryForm Form, string? Pointed)? _builderShape;
    private AdvisoryBuilderView? _builder;

    internal SoloAdvisoryPanel(IMenuAircraft? aircraft, MenuContext context, IMenuHost host, Func<string, Task<CommandResultDto?>> send, Action close)
    {
        _aircraft = aircraft;
        _context = context;
        _host = host;
        _send = send;
        _close = close;
        Width = PanelWidth;
        Children.Add(TitleLine());
        Box = new TextBox
        {
            Name = BoxName,
            PlaceholderText = Placeholder,
            MaxWidth = 250,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(Box, BoxLabel);
        TakeoffFlyout.KeepClicksInside(Box);
        QuickCommandGlyph glyph =
            QuickCommandGlyphs.For(MenuIds.ApproachReportTrafficInSight) ?? throw new InvalidOperationException("No Report traffic in sight glyph.");
        var boxColumn = new StackPanel { Children = { Box, _ghostView } };
        Children.Add(FlyoutPanelParts.BoxRow(glyph, $"{BoxLabel}  {BoxSub}", boxColumn, BoxCommandText));
        BoxCommandText.MaxWidth = CommandMaxWidth;
        BoxCommandText.TextTrimming = TextTrimming.CharacterEllipsis;
        Children.Add(SpokenText);
        _checkBorder = new Border
        {
            Child = _checkLine,
            Margin = new Thickness(33, 0, 11, 2),
            Padding = new Thickness(2, 1),
            CornerRadius = new CornerRadius(3),
        };
        Children.Add(_checkBorder);
        Children.Add(NoteText);
        Children.Add(BuilderToggle);
        Children.Add(_builderBody);
        Children.Add(FlyoutPanelParts.SeparatorLine());
        _groupLine = GroupLineView();
        Children.Add(_groupLine);
        Children.Add(
            new ScrollViewer
            {
                Content = _list,
                MaxHeight = AirborneTrafficRows.ListedCount * AirborneTrafficRowButton.RowHeight,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            }
        );
        Children.Add(NoMatchText);
        Children.Add(_emptyText);
        BuilderToggle.Click += (_, e) =>
        {
            e.Handled = true;
            IsBuilderOpen = !IsBuilderOpen;
            RefreshBuilder();
        };
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnTyped();
            }
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _checkBorder.IsVisible = LiveCheck;
    }

    /// <summary>The box.</summary>
    public TextBox Box { get; }

    /// <summary>The command column beside the box: the call with its unset parts as gaps, or <c>RTIS ···</c>.</summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>
    /// The ghost's words (<see cref="SoloAdvisoryModel.Ghost"/>, <see cref="SoloAdvisoryModel.PickGhost"/>); the line under
    /// the box draws them with the keys as key caps.
    /// </summary>
    public TextBlock GhostText { get; } = new();

    /// <summary>The line the pilot hears for a complete call, in quotes; hidden for any other.</summary>
    public TextBlock SpokenText { get; } =
        new()
        {
            FontSize = 11,
            FontStyle = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(33, 2, 11, 0),
            IsVisible = false,
        };

    /// <summary>The note under the advisory: the sim's refusal, a send that never reached it, or the parts Enter still needs.</summary>
    public TextBlock NoteText { get; } =
        new()
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(33, 0, 11, 2),
            IsVisible = false,
        };

    /// <summary>The line closing the list while a typed filter matches no aircraft.</summary>
    public TextBlock NoMatchText { get; } = MutedLine();

    /// <summary>The builder's fold, <c>▸ Builder</c> or <c>▾ Builder</c>.</summary>
    public Button BuilderToggle { get; } =
        new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(29, 0, 11, 0),
            FontSize = 11,
        };

    /// <summary>Whether the builder is unfolded.</summary>
    public bool IsBuilderOpen { get; private set; }

    /// <summary>
    /// The builder's form tabs, radar, nose, pattern and over, each disabled with its reason while the pointed aircraft cannot
    /// take it.
    /// </summary>
    public IReadOnlyList<Button> FormTabs => _formTabs;

    /// <summary>The unfolded builder's controls; null while folded or with no row pointed at.</summary>
    public AdvisoryBuilderView? Builder => _builder;

    /// <summary>Whether the check line shows: only with the session's live check on.</summary>
    public bool IsCheckVisible => _checkBorder.IsVisible;

    /// <summary>The check line as one string (<see cref="AdvisoryCheck.Text"/>); empty before the first check.</summary>
    public string CheckText => _check?.Text ?? "";

    /// <summary>Whether the check line is lit after its text changed on an aircraft update.</summary>
    public bool IsCheckFlashing => _flash is not null;

    /// <summary>The rows, in list order.</summary>
    public IReadOnlyList<SoloTrafficRowButton> Rows => _rowOrder;

    /// <summary>The aircraft the advisory is pointed at: the row under the pointer, else the focused row, else the row picked.</summary>
    public string? Pointed => _hovered ?? _focused ?? _pinned;

    private bool LiveCheck => _context.Session.SoloAdvisoryLiveCheck;

    private SoloAdvisoryPrefill Prefill => _context.Session.SoloAdvisoryPrefill;

    private AdvisoryReading Reading => SoloAdvisoryText.Read(Box.Text ?? "");

    /// <summary>
    /// Follows <paramref name="traffic"/>, the host's airborne aircraft nearest first; the order is held while the pointer is
    /// inside.
    /// </summary>
    internal void Update(IReadOnlyList<MenuAirborneTrafficRow> traffic)
    {
        _traffic = traffic;
        _titleNow.Text = AirborneTrafficFlyout.TitleNow(_aircraft, _context.Callsign);
        ShowRows();
        RefreshStatic();
    }

    /// <summary>Holds the rows' order while <paramref name="inside"/>, or lets it go.</summary>
    internal void Hold(bool inside) => _held = inside ? [.. _rowOrder.Select(row => row.Callsign)] : null;

    /// <summary>Starts following aircraft updates, which re-run the live check, as the flyout opens.</summary>
    internal void Opened()
    {
        _updates?.Dispose();
        _updates = _host.ObserveAircraftUpdates(OnAircraftUpdate);
        ReadAircraft();
        RefreshStatic();
        RunCheck(false);
    }

    /// <summary>
    /// Puts the flyout back as it opens: no updates followed, no timers, the box empty, nothing pointed at, no radar cue; a send
    /// still in flight is answered to nobody.
    /// </summary>
    internal void Reset()
    {
        _opening++;
        _sending = false;
        _updates?.Dispose();
        _updates = null;
        _held = null;
        _pinned = null;
        _hovered = null;
        _focused = null;
        _built = [];
        Box.Text = "";
        NoteText.IsVisible = false;
        _debounce?.Dispose();
        _debounce = null;
        StopFlash();
        _check = null;
        _checkedInput = null;
        _candidates = [];
        _pointsNear.Clear();
        _host.HighlightAircraft(null);
        _host.SetRadarCue(null);
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
                Text = AirborneTrafficFlyout.ReportTitle,
                FontSize = 12.5,
                FontWeight = FontWeight.SemiBold,
                Foreground = StrongBrush,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        _titleNow.FontSize = 11;
        _titleNow.Margin = new Thickness(12, 0, 0, 0);
        _titleNow.VerticalAlignment = VerticalAlignment.Center;
        FlyoutPanelParts.Mono(_titleNow);
        Grid.SetColumn(_titleNow, 1);
        grid.Children.Add(_titleNow);
        var badge = new Border
        {
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(4, 0),
            CornerRadius = new CornerRadius(3),
            BorderBrush = BadgeLine,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = SoloBadge.ToUpperInvariant(),
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                LetterSpacing = 0.4,
                Foreground = GoodBrush,
            },
        };
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);
        return grid;
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

    /// <summary>
    /// Reads the aircraft the client holds, once per pass, as the matcher's candidates, and forgets the reporting points found
    /// near each, since the aircraft have moved.
    /// </summary>
    private void ReadAircraft()
    {
        _candidates = _host.GetAdvisoryCandidates();
        _pointsNear.Clear();
    }

    private void OnTyped()
    {
        if (!_composing)
        {
            TakeTypedParts();
        }

        NoteText.IsVisible = false;
        ShowRows();
        RefreshStatic();
        if (!LiveCheck)
        {
            return;
        }

        _typedAt = _host.Clock.GetTimestamp();
        _checkLine.Opacity = 0.5;
        if (_debounce is null)
        {
            _debounce = _host.Clock.CreateTimer(_ => OnDebounce(), null, CheckDelay, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _debounce.Change(CheckDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// The builder takes the box's parts in the form typed, keeping its own parts the box has not reached yet (a value the
    /// pre-fill seeded past the parts left for the student); a new form starts from the box alone. Nothing typed, or a filter,
    /// leaves the builder as it was.
    /// </summary>
    private void TakeTypedParts()
    {
        AdvisoryReading reading = Reading;
        if (reading.Form is not { } form)
        {
            return;
        }

        var parts = new Dictionary<AdvisoryPart, string>(reading.Parts);
        IEnumerable<AdvisoryPart> kept = (form == _builderForm) ? SoloAdvisoryText.Unreached(reading) : [];
        foreach (AdvisoryPart part in kept.Where(_built.ContainsKey))
        {
            parts[part] = _built[part];
        }

        _built = parts;
        _builderForm = form;
    }

    /// <summary>
    /// The debounce timer's beat: the check runs once the keys have rested <see cref="CheckDelay"/> by the host's clock; a beat
    /// before then (a timer firing a little early) waits out the rest.
    /// </summary>
    private void OnDebounce()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnDebounce);
            return;
        }

        if (_debounce is null)
        {
            return;
        }

        TimeSpan rested = _host.Clock.GetElapsedTime(_typedAt);
        if (rested < CheckDelay)
        {
            _debounce.Change(CheckDelay - rested, Timeout.InfiniteTimeSpan);
            return;
        }

        _debounce.Dispose();
        _debounce = null;
        RunCheck(false);
    }

    /// <summary>
    /// One pass after a burst of aircraft updates: the aircraft are read once and the static lines restated; the check re-runs
    /// unless a typing check is pending, which reads the box once the keys rest.
    /// </summary>
    private void OnAircraftUpdate()
    {
        ReadAircraft();
        RefreshStatic();
        if (_debounce is null)
        {
            RunCheck(true);
        }
    }

    /// <summary>
    /// Runs the live check on the box's call; on an aircraft update (<paramref name="fromUpdate"/>), a line whose text changed
    /// with the box unchanged since the last check flashes.
    /// </summary>
    private void RunCheck(bool fromUpdate)
    {
        if (!LiveCheck)
        {
            return;
        }

        string input = Box.Text ?? "";
        string before = CheckText;
        bool sameInput = input == _checkedInput;
        _check = SoloAdvisoryModel.Check(Reading, _context.Callsign, _candidates, _host.GetFixPosition);
        _checkedInput = input;
        _checkLine.Opacity = 1;
        _checkLine.Children.Clear();
        foreach (AdvisorySegment segment in _check.Segments)
        {
            _checkLine.Children.Add(SegmentText(segment));
        }

        AutomationProperties.SetName(_checkLine, CheckText);
        if (fromUpdate && sameInput && (before.Length > 0) && (before != CheckText))
        {
            Flash();
        }

        ShowRows();
        ApplyCue();
    }

    /// <summary>
    /// One piece of the check line. A plain or callsign piece sets no brush of its own and inherits the panel's text brush; a
    /// local null would draw it invisible.
    /// </summary>
    private static TextBlock SegmentText(AdvisorySegment segment)
    {
        var text = new TextBlock
        {
            Text = segment.Text,
            FontSize = 11,
            Margin = new Thickness(0, 0, 5, 0),
            FontWeight = (segment.Tone == AdvisoryTone.Callsign) ? FontWeight.SemiBold : FontWeight.Normal,
        };
        if (ToneBrush(segment.Tone) is { } brush)
        {
            text.Foreground = brush;
        }

        return text;
    }

    private static IBrush? ToneBrush(AdvisoryTone tone) =>
        tone switch
        {
            AdvisoryTone.Good => GoodBrush,
            AdvisoryTone.Off => OffBrush,
            AdvisoryTone.Bad => BadBrush,
            AdvisoryTone.Hint => FlyoutPanelParts.TitleBrush,
            _ => null,
        };

    private void Flash()
    {
        StopFlash();
        _checkBorder.Background = FlashBrush;
        _litAt = _host.Clock.GetTimestamp();
        _flash = _host.Clock.CreateTimer(_ => OnFlashBeat(), null, FlashTime, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The flash timer's beat: the line goes dark once lit <see cref="FlashTime"/>; a beat before then waits out the rest.</summary>
    private void OnFlashBeat()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnFlashBeat);
            return;
        }

        if (_flash is null)
        {
            return;
        }

        TimeSpan lit = _host.Clock.GetElapsedTime(_litAt);
        if (lit < FlashTime)
        {
            _flash.Change(FlashTime - lit, Timeout.InfiniteTimeSpan);
            return;
        }

        StopFlash();
    }

    private void StopFlash()
    {
        _flash?.Dispose();
        _flash = null;
        _checkBorder.Background = null;
    }

    /// <summary>
    /// Restates the command column, the ghost, the spoken line, the no-match line and the builder from the box and the pointed
    /// row.
    /// </summary>
    private void RefreshStatic()
    {
        AdvisoryReading reading = Reading;
        BoxCommandText.Text = SoloAdvisoryText.Preview(reading);
        BoxCommandText.Opacity = reading.IsComplete ? 0.7 : 0.7 * MenuGlyphRowTemplate.DimmedOpacity;
        MenuCommandText.SetCommand(Box, reading.IsComplete ? SoloAdvisoryText.Command(reading) : null);
        ShowGhost(Ghost(reading).Text);
        string? spoken = SoloAdvisoryModel.Spoken(reading);
        SpokenText.Text = (spoken is null) ? "" : $"“{spoken}”";
        SpokenText.IsVisible = spoken is not null;
        NoMatchText.Text =
            (reading.FilterText is { } filter)
                ? $"No aircraft matches {filter}. In solo a callsign is not a call: pick an aircraft, or type the advisory."
                : "";
        NoMatchText.IsVisible = (reading.FilterText is not null) && (_rowOrder.Count == 0);
        RefreshBuilder();
    }

    /// <summary>Draws the ghost <paramref name="text"/> with its keys (<c>Tab</c>, <c>Enter</c>) as key caps.</summary>
    private void ShowGhost(string text)
    {
        GhostText.Text = text;
        _ghostView.Inlines ??= [];
        _ghostView.Inlines.Clear();
        string[] words = text.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];
            string space = (i < words.Length - 1) ? " " : "";
            if (KeyWords.Contains(word))
            {
                _ghostView.Inlines.Add(new InlineUIContainer(KeyCap(word)) { BaselineAlignment = BaselineAlignment.Center });
                _ghostView.Inlines.Add(new Run(space));
            }
            else
            {
                _ghostView.Inlines.Add(new Run(word + space));
            }
        }

        AutomationProperties.SetName(_ghostView, text);
    }

    private static Border KeyCap(string key) =>
        new()
        {
            BorderBrush = KeyCapLine,
            BorderThickness = new Thickness(1, 1, 1, 2),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0),
            Child = new TextBlock
            {
                Text = key,
                FontSize = 10.5,
                Foreground = KeyCapText,
            },
        };

    /// <summary>The ghost for the box: the row a filter picks among the rows it lists, or the call's next part in its form.</summary>
    private AdvisoryGhost Ghost(AdvisoryReading reading)
    {
        if (reading.FilterText is { } filter)
        {
            return SoloAdvisoryModel.PickGhost(filter, ListedRows(filter));
        }

        AdvisoryForm form = reading.Form ?? _builderForm;
        return SoloAdvisoryModel.Ghost(reading, form, TruthOf(form, Pointed), Pointed is not null, Prefill);
    }

    /// <summary>The pointed aircraft's true parts in <paramref name="form"/>; null with none pointed, none known, or the form not taken.</summary>
    private IReadOnlyDictionary<AdvisoryPart, string>? TruthOf(AdvisoryForm form, string? pointed)
    {
        TrafficAdvisoryCandidate? me = _candidates.FirstOrDefault(c => c.Callsign == _context.Callsign);
        TrafficAdvisoryCandidate? target = (pointed is null) ? null : _candidates.FirstOrDefault(c => c.Callsign == pointed);
        if ((me is null) || (target is null))
        {
            return null;
        }

        IReadOnlyList<string> points = (form == AdvisoryForm.Over) ? PointsNear(target) : [];
        return SoloAdvisoryModel.TruthOf(form, me, target, points);
    }

    /// <summary>The reporting points near <paramref name="target"/>, found once per pass of aircraft updates.</summary>
    private IReadOnlyList<string> PointsNear(TrafficAdvisoryCandidate target)
    {
        if (!_pointsNear.TryGetValue(target.Callsign, out IReadOnlyList<string>? points))
        {
            points = _host.GetReportingPointsNear(target.Position);
            _pointsNear[target.Callsign] = points;
        }

        return points;
    }

    private IReadOnlyList<MenuAirborneTrafficRow> ListedRows(string filter) => AirborneTrafficRows.Listed(_traffic, filter, null);

    /// <summary>Lists the rows: the nearest, or those a typed filter matches; while held, in the held order.</summary>
    private void ShowRows()
    {
        string filter = Reading.FilterText ?? "";
        List<SoloTrafficRowButton> order = [.. HeldOrder([.. ListedRows(filter)]).Select(ShowRow)];
        if ((_hovered is { } hovered) && !order.Exists(row => row.Callsign == hovered))
        {
            _hovered = null;
        }

        var rows = new Dictionary<string, SoloTrafficRowButton>(StringComparer.Ordinal);
        foreach (SoloTrafficRowButton row in order)
        {
            rows.TryAdd(row.Callsign, row);
        }

        _rows = rows;
        _rowOrder = order;
        FlyoutItemSync.Sync(_list.Children, [.. order]);
        _groupCount.Text = order.Count.ToString(CultureInfo.InvariantCulture);
        _groupLine.IsVisible = order.Count > 0;
        _emptyText.Text = (_traffic.Count == 0) ? AirborneTrafficFlyout.NoTrafficText : AirborneTrafficFlyout.NoneInRangeText;
        _emptyText.IsVisible = (filter.Length == 0) && (order.Count == 0);
    }

    /// <summary><paramref name="listed"/> in the held order while the pointer holds it, rows new since following.</summary>
    private List<MenuAirborneTrafficRow> HeldOrder(List<MenuAirborneTrafficRow> listed) =>
        (_held is { } held)
            ?
            [
                .. held.Select(cs => listed.FirstOrDefault(row => row.Callsign == cs)).OfType<MenuAirborneTrafficRow>(),
                .. listed.Where(row => !held.Contains(row.Callsign)),
            ]
            : listed;

    /// <summary>The row button for <paramref name="row"/>, kept from the last listing or new, showing it.</summary>
    private SoloTrafficRowButton ShowRow(MenuAirborneTrafficRow row)
    {
        SoloTrafficRowButton button = _rows.TryGetValue(row.Callsign, out SoloTrafficRowButton? kept) ? kept : NewRow(row.Callsign);
        bool resolved = LiveCheck && (_check?.ResolvedCallsign == row.Callsign);
        button.Show(row, Prefill == SoloAdvisoryPrefill.TrueValues, row.Callsign == _pinned, resolved);
        button.Light(row.Callsign == Pointed);
        return button;
    }

    private SoloTrafficRowButton NewRow(string callsign)
    {
        var row = new SoloTrafficRowButton(callsign);
        row.Click += (_, e) =>
        {
            e.Handled = true;
            Point(callsign);
        };
        row.PointerEntered += (_, _) => Hover(callsign);
        row.PointerExited += (_, _) => Unhover(callsign);
        row.AddHandler(GotFocusEvent, (_, _) => FocusRow(callsign), handledEventsToo: true);
        row.AddHandler(LostFocusEvent, (_, _) => BlurRow(callsign), handledEventsToo: true);
        return row;
    }

    /// <summary>
    /// Points the advisory at <paramref name="callsign"/>, as a click on its row or Tab on a picked filter does: the box and the
    /// builder empty for its call.
    /// </summary>
    private void Point(string callsign)
    {
        _pinned = callsign;
        _built = [];
        Box.Text = "";
        Box.Focus();
        ShowRows();
        RefreshStatic();
        ApplyCue();
    }

    private void Hover(string callsign)
    {
        _hovered = callsign;
        RefreshStatic();
        ApplyCue();
    }

    private void Unhover(string callsign)
    {
        if (_hovered == callsign)
        {
            _hovered = null;
            RefreshStatic();
            ApplyCue();
        }
    }

    private void FocusRow(string callsign)
    {
        _focused = callsign;
        ApplyCue();
    }

    private void BlurRow(string callsign)
    {
        if (_focused == callsign)
        {
            _focused = null;
        }

        ApplyCue();
    }

    /// <summary>
    /// The radar cue: the pointed aircraft ringed solid and highlighted, with the bearing line to it, and the aircraft the live
    /// check resolves the call to ringed dashed when it is another; with none pointed, the resolved aircraft's dashed ring alone.
    /// The bearing label shows only with true values pre-filled.
    /// </summary>
    private void ApplyCue()
    {
        string? resolved = LiveCheck ? _check?.ResolvedCallsign : null;
        string? pointed = Pointed;
        RadarCue? cue =
            ((pointed ?? resolved) is null) ? null : new RadarCue(_context.Callsign, pointed, resolved, Prefill == SoloAdvisoryPrefill.TrueValues);
        _host.HighlightAircraft(pointed);
        _host.SetRadarCue(cue);
        foreach (SoloTrafficRowButton row in _rowOrder)
        {
            row.Light(row.Callsign == pointed);
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool inBox = ReferenceEquals(e.Source, Box) || Box.IsKeyboardFocusWithin;
        switch (e.Key)
        {
            case Key.Tab when inBox && !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                TakeGhost(e);
                break;
            case Key.Enter when inBox:
                e.Handled = true;
                await EnterAsync();
                break;
            case Key.Enter when _rowOrder.FirstOrDefault(row => row.IsKeyboardFocusWithin) is { } row:
                e.Handled = true;
                Point(row.Callsign);
                break;
            case Key.Down or Key.Up:
                Walk(e, inBox);
                break;
        }
    }

    /// <summary>Tab takes the ghost's offer: the row a filter picks, or the next part, typed with a trailing space.</summary>
    private void TakeGhost(KeyEventArgs e)
    {
        AdvisoryGhost ghost = Ghost(Reading);
        if (ghost.PickCallsign is { } pick)
        {
            e.Handled = true;
            Point(pick);
        }
        else if (ghost.Token is { } token)
        {
            e.Handled = true;
            Box.Text = $"{(Box.Text ?? "").Trim()} {token} ".TrimStart();
            Box.CaretIndex = Box.Text.Length;
        }
    }

    /// <summary>
    /// Enter in the box: a filter points at the row it picks; a call missing parts says which; a complete call is sent, unless a
    /// send is already in flight.
    /// </summary>
    private async Task EnterAsync()
    {
        AdvisoryReading reading = Reading;
        if (reading.FilterText is not null)
        {
            if (Ghost(reading).PickCallsign is { } pick)
            {
                Point(pick);
            }

            return;
        }

        if (reading.IsEmpty || _sending)
        {
            return;
        }

        if (!reading.IsComplete)
        {
            ShowNote($"Set the {string.Join(", ", reading.Missing.Select(SoloAdvisoryText.KindWord))} first; Enter sends nothing.", null);
            return;
        }

        await SendAsync(reading);
    }

    /// <summary>
    /// Sends the call: the flyout closes when the sim takes it; a refusal shows the sim's reply and a send that never reached the
    /// sim says so, both keeping the call. An answer arriving after the flyout was put back is dropped.
    /// </summary>
    private async Task SendAsync(AdvisoryReading reading)
    {
        int opening = _opening;
        _sending = true;
        CommandResultDto? result;
        try
        {
            result = await _send(SoloAdvisoryText.Command(reading));
        }
        finally
        {
            if (opening == _opening)
            {
                _sending = false;
            }
        }

        if (opening != _opening)
        {
            return;
        }

        switch (result)
        {
            case null:
                ShowNote(NotSentNote, BadBrush);
                break;
            case { Success: false } refused:
                ShowNote($"Refused: “{refused.Message}”. The values are kept; fix a part and press Enter again.", BadBrush);
                break;
            default:
                _close();
                break;
        }
    }

    private void ShowNote(string text, IBrush? brush)
    {
        NoteText.Text = text;
        NoteText.Foreground = brush ?? FlyoutPanelParts.TitleBrush;
        NoteText.IsVisible = true;
    }

    private void Walk(KeyEventArgs e, bool inBox)
    {
        int focused = _rowOrder.FindIndex(row => row.IsKeyboardFocusWithin);
        int? next = (inBox, focused, e.Key) switch
        {
            (true, _, Key.Down) when _rowOrder.Count > 0 => 0,
            (false, 0, Key.Up) => -1,
            (false, >= 0, Key.Up) => focused - 1,
            (false, >= 0, Key.Down) => Math.Min(focused + 1, _rowOrder.Count - 1),
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

        _rowOrder[target].Focus(NavigationMethod.Directional);
        _rowOrder[target].BringIntoView();
    }

    /// <summary>
    /// Restates the builder: folded, its header alone; unfolded with no row pointed at, its hint; else the lead naming the
    /// pointed aircraft, the form tabs (each greyed with its reason), and the form's controls showing the call's parts.
    /// </summary>
    private void RefreshBuilder()
    {
        BuilderToggle.Content = $"{(IsBuilderOpen ? "▾" : "▸")} {BuilderTitle}   {BuilderParts}";
        _builderBody.IsVisible = IsBuilderOpen;
        if (!IsBuilderOpen)
        {
            _builder = null;
            _builderShape = null;
            return;
        }

        string? pointed = Pointed;
        AdvisoryForm form = Reading.Form ?? _builderForm;
        if (_builderShape != (form, pointed))
        {
            _builderShape = (form, pointed);
            RebuildBuilder(form, pointed);
        }

        _builder?.Show(_built);
    }

    private void RebuildBuilder(AdvisoryForm form, string? pointed)
    {
        _builderBody.Children.Clear();
        _formTabs.Clear();
        _builder = null;
        if (pointed is null)
        {
            _builderBody.Children.Add(
                new TextBlock
                {
                    Text = BuilderHint,
                    FontSize = 11,
                    Foreground = FlyoutPanelParts.TitleBrush,
                }
            );
            return;
        }

        Border tabs = FlyoutPanelParts.ToggleStrip([
            .. Forms.Select((each, i) => FormTab(each.Form, each.Label, i == 0, each.Form == form, pointed)),
        ]);
        tabs.Margin = new Thickness(0);
        tabs.HorizontalAlignment = HorizontalAlignment.Right;
        var callsign = new Run(pointed)
        {
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = StrongBrush,
        };
        callsign.Bind(TextElement.FontFamilyProperty, callsign.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        var lead = new TextBlock
        {
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Inlines = [new Run("Advisory for "), callsign],
        };
        AutomationProperties.SetName(lead, $"Advisory for {pointed}");
        _builderBody.Children.Add(new DockPanel { Children = { lead, tabs } });
        IReadOnlyList<string> points = (form == AdvisoryForm.Over) ? PointsFor(pointed) : [];
        _builder = new AdvisoryBuilderView(form, pointed, points, _aircraft, SetPart);
        _builderBody.Children.Add(_builder);
    }

    /// <summary>
    /// The tab for <paramref name="form"/>, the <paramref name="first"/> of the segmented control or not, lit when
    /// <paramref name="current"/>, faded with its reason when <paramref name="pointed"/> cannot take it.
    /// </summary>
    private Button FormTab(AdvisoryForm form, string label, bool first, bool current, string pointed)
    {
        string? reason = SoloAdvisoryModel.UnavailableReason(form, pointed, TruthOf(form, pointed));
        Button tab = FlyoutPanelParts.SegmentButton(label, first, current);
        tab.FontSize = 11;
        tab.Padding = new Thickness(7, 3);
        tab.IsEnabled = reason is null;
        tab.Opacity = (reason is null) ? 1 : DisabledTabOpacity;
        AutomationProperties.SetName(tab, label);
        ToolTip.SetTip(tab, reason);
        ToolTip.SetShowOnDisabled(tab, true);
        tab.Click += (_, e) =>
        {
            e.Handled = true;
            PickForm(form);
        };
        _formTabs.Add(tab);
        return tab;
    }

    private IReadOnlyList<string> PointsFor(string pointed)
    {
        TrafficAdvisoryCandidate? target = _candidates.FirstOrDefault(c => c.Callsign == pointed);
        return (target is null) ? [] : PointsNear(target);
    }

    /// <summary>
    /// Switches the builder to <paramref name="form"/>, seeded from the pointed aircraft's true parts as the pre-fill setting
    /// allows.
    /// </summary>
    private void PickForm(AdvisoryForm form)
    {
        _builderForm = form;
        IReadOnlyDictionary<AdvisoryPart, string>? truth = TruthOf(form, Pointed);
        _built =
            (truth is null)
                ? []
                : truth.Where(part => (Prefill == SoloAdvisoryPrefill.TrueValues) || !SoloAdvisoryModel.IsJudged(form, part.Key)).ToDictionary();
        Compose();
    }

    /// <summary>Sets <paramref name="part"/> to <paramref name="value"/> (null clears it) from the builder and rewrites the box.</summary>
    private void SetPart(AdvisoryPart part, string? value)
    {
        if (value is null)
        {
            _built.Remove(part);
        }
        else
        {
            _built[part] = value;
        }

        if ((part == AdvisoryPart.Leg) && (value is not null) && (SoloAdvisoryText.LegOf(value) == PatternEntryLeg.Final))
        {
            _built.Remove(AdvisoryPart.Side);
        }

        Compose();
    }

    /// <summary>
    /// Writes the builder's parts into the box in the form's order, up to the first part still unset (the call is said in order,
    /// so a later part waits in the builder until the ones before it are set).
    /// </summary>
    private void Compose()
    {
        bool final = _built.TryGetValue(AdvisoryPart.Leg, out string? leg) && (SoloAdvisoryText.LegOf(leg) == PatternEntryLeg.Final);
        List<string> words = (_builderForm == AdvisoryForm.Over) ? ["OVER"] : [];
        foreach (AdvisoryPart part in SoloAdvisoryText.OrderOf(_builderForm, final))
        {
            if (!_built.TryGetValue(part, out string? word))
            {
                break;
            }

            words.Add(word);
        }

        string text = string.Join(' ', words);
        if (text == (Box.Text ?? ""))
        {
            RefreshStatic();
            return;
        }

        _composing = true;
        Box.Text = text;
        _composing = false;
    }
}
