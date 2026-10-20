using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.ContextMenus;

/// <summary>Which way the Join radial flyout's command flies the radial: outbound from the navaid or inbound to it.</summary>
public enum RadialSide
{
    /// <summary><c>JRADO</c>: intercept the radial and fly it outbound.</summary>
    Outbound,

    /// <summary><c>JRADI</c>: intercept the radial and fly it inbound to the navaid.</summary>
    Inbound,
}

/// <summary>
/// The Join radial flyout (<c>JRADO</c> / <c>JRADI</c>): the title (<c>Join radial outbound</c> / <c>Join radial inbound</c>,
/// following the toggle), the Outbound / Inbound toggle, the Fix box (a VOR, VORTAC or TACAN only; anything else, a
/// fix-radial-distance included, shows <see cref="NotANavaid"/>), the navaids nearest first, the first <see cref="MaxNavaids"/>
/// (<see cref="NavigationDatabase.NavaidsNear"/>; typing lists those starting with the text), each naming the radial the
/// aircraft is on from it now and its distance, then the Radial box (001 to 360; empty sends the aircraft's present radial
/// from the navaid) and the heading flyout's rose (<see cref="HeadingRose"/>) with the picked navaid in its centre, the needle
/// on the aircraft's radial and the mark on the radial picked. It opens with the nearest navaid on the aircraft's route
/// picked, else the nearest navaid. Every command is one token, <c>JRADO RBL184</c>, the radial magnetic by the station's
/// variation of record (<see cref="RadialConversion"/>, as the sim flies it). A row click sends its navaid's command; a press
/// or drag on the rose sets the Radial box and the release sends; Enter in a box sends the picked navaid's command.
/// </summary>
public static class JoinRadialFlyout
{
    /// <summary>The Fix box's name, by which a test or UI Automation finds it.</summary>
    public const string FixBoxName = "JoinRadialFixBox";

    /// <summary>The Radial box's name.</summary>
    public const string RadialBoxName = "JoinRadialBox";

    /// <summary>The refusal under the Fix box while its text names no radial navaid.</summary>
    public const string NotANavaid = "not a VOR, VORTAC or TACAN";

    /// <summary>The refusal under the Radial box for text that is no radial from 001 to 360.</summary>
    public const string InvalidRadial = "Not a valid radial";

    /// <summary>The most navaid rows the flyout lists.</summary>
    public const int MaxNavaids = 12;

    /// <summary>How far a route navaid may be and still be the opening pick: a VOR's high service volume (AIM 1-1-8).</summary>
    public const double RouteNavaidRangeNm = 130;

    /// <summary>The Join radial entry <paramref name="label"/> names: one menu row holding the flyout opening on <paramref name="preset"/>.</summary>
    public static MenuItem Build(string label, RadialSide preset, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(
            entry,
            new JoinRadialFlyoutHost(),
            context,
            host,
            (choose, _) => new JoinRadialFlyoutPanel(preset, aircraft, choose)
        );
        return entry;
    }

    /// <summary>The verb of <paramref name="side"/>: <c>JRADO</c> or <c>JRADI</c>.</summary>
    public static string Verb(RadialSide side) =>
        side switch
        {
            RadialSide.Outbound => "JRADO",
            RadialSide.Inbound => "JRADI",
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Unknown radial side."),
        };

    /// <summary>The toggle label of <paramref name="side"/>: <c>Outbound</c> or <c>Inbound</c>.</summary>
    public static string ToggleLabel(RadialSide side) =>
        side switch
        {
            RadialSide.Outbound => "Outbound",
            RadialSide.Inbound => "Inbound",
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Unknown radial side."),
        };

    /// <summary>The flyout's title: <c>Join radial outbound</c> or <c>Join radial inbound</c>.</summary>
    public static string Title(RadialSide side) => $"Join radial {ToggleLabel(side).ToLowerInvariant()}";

    /// <summary>The command, one token after the verb: <c>JRADO RBL184</c>, the radial in three digits, north 360.</summary>
    public static string Command(RadialSide side, string navaid, int radial) => $"{Verb(side)} {navaid}{HoldFlyout.Three(radial)}";

    /// <summary>The instruction as a controller says it (7110.65 5-6-2.b): <c>Intercept the RBL 184 radial outbound</c>.</summary>
    public static string Caption(RadialSide side, string navaid, int radial) =>
        $"Intercept the {navaid} {HoldFlyout.Three(radial)} radial {ToggleLabel(side).ToLowerInvariant()}";

    /// <summary>
    /// The magnetic radial from <paramref name="navaid"/> that <paramref name="from"/> lies on, 001 to 360, made magnetic by
    /// the variation the sim flies the radial by (<see cref="RadialConversion.ToMagnetic"/>: the station's declination of
    /// record, else the modelled one at the navaid), never the aircraft's; null without the navigation data or a position.
    /// </summary>
    public static int? CurrentRadial(string navaid, LatLon from)
    {
        if ((NavigationDatabase.InstanceOrNull is not { } db) || (db.GetFixPosition(navaid) is not (double lat, double lon)))
        {
            return null;
        }

        var station = new LatLon(lat, lon);
        MagneticHeading radial = RadialConversion.ToMagnetic(db, navaid, station, new TrueHeading(GeoMath.BearingTo(station, from)));
        return HeadingFlyout.WholeHeading(radial.Degrees);
    }

    /// <summary>
    /// The navaid the Fix box's <paramref name="text"/> names: one word, upper-cased, that is a VOR, VORTAC or TACAN
    /// (<see cref="NavigationDatabase.IsRadialNavaid"/>; any word while the navigation data is not loaded); null otherwise.
    /// </summary>
    public static string? TypedNavaid(string text)
    {
        string navaid = text.Trim().ToUpperInvariant();
        if ((navaid.Length == 0) || navaid.Any(char.IsWhiteSpace))
        {
            return null;
        }

        return ((NavigationDatabase.InstanceOrNull is not { } db) || db.IsRadialNavaid(navaid)) ? navaid : null;
    }

    /// <summary>
    /// The navaid the flyout opens on: of the aircraft's route fixes that are radial navaids within
    /// <see cref="RouteNavaidRangeNm"/> NM, the nearest; else the nearest radial navaid; null without an aircraft or the
    /// navigation data.
    /// </summary>
    public static string? OpeningNavaid(IMenuAircraft? aircraft)
    {
        if ((aircraft is null) || (NavigationDatabase.InstanceOrNull is not { } db))
        {
            return null;
        }

        string? onRoute = aircraft
            .RouteFixNames()
            .Where(db.IsRadialNavaid)
            .Select(fix =>
                (FixList.Position(fix) is { } position)
                    ? (Fix: fix.ToUpperInvariant(), Miles: GeoMath.DistanceNm(aircraft.Position, position))
                    : default
            )
            .Where(fix => (fix.Fix is not null) && (fix.Miles <= RouteNavaidRangeNm))
            .OrderBy(fix => fix.Miles)
            .ThenBy(fix => fix.Fix, StringComparer.Ordinal)
            .Select(fix => fix.Fix)
            .FirstOrDefault();
        return onRoute ?? db.NavaidsNear(aircraft.Position, "").FirstOrDefault()?.Name;
    }
}

/// <summary>The Join radial flyout's menu row header, which holds the <see cref="JoinRadialFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class JoinRadialFlyoutHost() : FlyoutPanelHost("join radial flyout", "join-radial-flyout-panel");

/// <summary>
/// The Join radial flyout's panel (<see cref="JoinRadialFlyout"/>). The keyboard: Down from the Fix box onto the picked navaid's
/// row (else the first), Down and Up through the rows, Up from the top row back to the box, Enter on a row sends it, a row
/// taking focus picks its navaid; on the rose the arrows step the radial 5° (Shift 30°) and Enter sends.
/// </summary>
public sealed class JoinRadialFlyoutPanel : StackPanel, IFlyoutPanel
{
    private const double ListMaxHeight = 200;
    private static readonly RadialSide[] Sides = [RadialSide.Outbound, RadialSide.Inbound];

    private readonly RadialSide _preset;
    private readonly IMenuAircraft? _aircraft;
    private readonly Func<string, Task> _choose;
    private readonly TextBlock _title = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly Dictionary<RadialSide, ToggleButton> _toggles = [];
    private readonly StackPanel _list = new();
    private readonly TextBlock _listHeader = new()
    {
        Text = "Navaids · nearest first",
        FontSize = 11,
        Foreground = FlyoutPanelParts.TitleBrush,
        Margin = new Thickness(11, 4, 11, 2),
    };
    private readonly List<FixRow> _rows = [];
    private string? _opening;
    private string? _picked;

    internal JoinRadialFlyoutPanel(RadialSide preset, IMenuAircraft? aircraft, Func<string, Task> choose)
    {
        _preset = preset;
        _aircraft = aircraft;
        _choose = choose;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        Children.Add(FlyoutPanelParts.TitleLine(_title, new TextBlock()));
        foreach (RadialSide side in Sides)
        {
            _toggles[side] = FlyoutPanelParts.VerbToggle(
                JoinRadialFlyout.ToggleLabel(side),
                JoinRadialFlyout.Verb(side),
                side == Sides[0],
                () => SetSide(side)
            );
        }

        Children.Add(FlyoutPanelParts.ToggleStrip(Sides.Select(side => _toggles[side])));
        FixBox = Box(JoinRadialFlyout.FixBoxName, "e.g. RBL", "Fix", 150);
        RadialBox = Box(JoinRadialFlyout.RadialBoxName, "e.g. 184", "Radial", 90);
        QuickCommandGlyph glyph =
            QuickCommandGlyphs.For(MenuIds.ProceduresJoinRadialOutbound) ?? throw new InvalidOperationException("No Join radial glyph.");
        QuickCommandGlyph headingGlyph = QuickCommandGlyphs.For(MenuIds.HeadingFly) ?? throw new InvalidOperationException("No heading glyph.");
        Children.Add(FlyoutPanelParts.BoxRow(glyph, "Fix (VOR, VORTAC or TACAN)", FixBox, BoxCommandText));
        Children.Add(FixError);
        Children.Add(new ScrollViewer { MaxHeight = ListMaxHeight, Content = _list });
        Children.Add(FlyoutPanelParts.SeparatorLine());
        Children.Add(FlyoutPanelParts.BoxRow(headingGlyph, "Radial (empty: the aircraft's)", RadialBox, new TextBlock()));
        Children.Add(RadialError);
        Children.Add(Caption);
        Rose.ShowTurnArc = false;
        Rose.HorizontalAlignment = HorizontalAlignment.Center;
        Rose.Margin = new Thickness(8, 0, 8, 6);
        AutomationProperties.SetName(Rose, "Radial rose");
        Children.Add(Rose);
        FixBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnFixTyped();
            }
        };
        RadialBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnRadialTyped();
            }
        };
        Rose.ShownChanged += (_, _) => OnRosePicked();
        Rose.Chosen += async (_, radial) => await SendAsync(Picked, radial);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Reset();
        Refresh();
    }

    /// <summary>The side the commands fly the radial now, which the toggle sets.</summary>
    public RadialSide Side { get; private set; }

    /// <summary>The title line: <c>Join radial outbound</c> or <c>Join radial inbound</c>.</summary>
    public string Title => _title.Text ?? "";

    /// <summary>The Fix box.</summary>
    public TextBox FixBox { get; }

    /// <summary>The Radial box: 001 to 360; empty sends the aircraft's present radial from the picked navaid.</summary>
    public TextBox RadialBox { get; }

    /// <summary>The rose: the picked navaid in its centre, the needle on the aircraft's radial, the mark on the picked radial.</summary>
    public HeadingRose Rose { get; } = new();

    /// <summary>The command column beside the Fix box: what Enter sends, or the verb with <c>···</c>, faded, while it cannot send.</summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>The refusal under the Fix box (<see cref="JoinRadialFlyout.NotANavaid"/>), shown while its text names no radial navaid.</summary>
    public TextBlock FixError { get; } = Refusal(JoinRadialFlyout.NotANavaid);

    /// <summary>The refusal under the Radial box (<see cref="JoinRadialFlyout.InvalidRadial"/>).</summary>
    public TextBlock RadialError { get; } = Refusal(JoinRadialFlyout.InvalidRadial);

    /// <summary>The caption: what Enter sends, as a controller says it; hidden while it cannot send.</summary>
    public TextBlock Caption { get; } =
        new()
        {
            FontSize = 12,
            Margin = new Thickness(11, 2, 11, 2),
            IsVisible = false,
        };

    /// <summary>
    /// The navaid the boxes and the rose send for: the opening navaid while the Fix box is empty, the navaid it names once typed,
    /// or the navaid of the row that last took focus; null while the box holds text naming none and no row was focused since.
    /// </summary>
    public string? Picked => _picked;

    /// <summary>The command Enter in a box sends now; null while there is no navaid or no radial.</summary>
    public string? BoxCommand => (Picked is { } navaid) ? CommandFor(navaid) : null;

    /// <summary>The navaid rows, nearest first; the live beat redraws them in place.</summary>
    public IReadOnlyList<FixRow> Rows => _rows;

    /// <summary>
    /// The panel as text, one line per part: the title, the toggle (the side on in brackets), the Fix box with its command, its
    /// refusal while shown, the list header and rows, <c>---</c>, the Radial box, its refusal and the caption while shown, and
    /// the rose.
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            List<string> lines = [Title, string.Join(" | ", Sides.Select(ToggleText)), $"Fix — {BoxCommandText.Text}"];
            AddShown(lines, FixError);
            lines.Add(_listHeader.Text ?? "");
            lines.AddRange(_rows.Select(row => row.Line));
            lines.Add("---");
            lines.Add($"Radial [{RadialBox.Text}]");
            AddShown(lines, RadialError);
            AddShown(lines, Caption);
            string pick = (Rose.Pick is { } radial) ? $" · pick {HoldFlyout.Three(radial)}" : "";
            lines.Add($"rose · {Rose.CentreCaption ?? "no navaid"} · now {HoldFlyout.Three(HeadingFlyout.WholeHeading(Rose.PresentHeading))}{pick}");
            return lines;
        }
    }

    Control IFlyoutPanel.View => this;

    void IFlyoutPanel.Reset() => Reset();

    void IFlyoutPanel.Refresh() => Refresh();

    void IFlyoutPanel.FocusOpeningLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(FixBox, isOpen, () => { });

    /// <summary>The toggle button of <paramref name="side"/>.</summary>
    public ToggleButton Toggle(RadialSide side) => _toggles[side];

    /// <summary>
    /// The command for <paramref name="navaid"/>: the typed radial, else the aircraft's present radial from it; null while the
    /// Radial box holds no radial or there is no present radial.
    /// </summary>
    public string? CommandFor(string navaid) => (RadialFor(navaid) is { } radial) ? JoinRadialFlyout.Command(Side, navaid, radial) : null;

    /// <summary>
    /// Puts the flyout back as it opens: the toggle on the preset's side, the boxes empty, the opening navaid picked, the navaid
    /// rows read afresh.
    /// </summary>
    public void Reset()
    {
        _opening = JoinRadialFlyout.OpeningNavaid(_aircraft);
        Rose.ClearPointing();
        FixBox.Text = "";
        RadialBox.Text = "";
        Rose.Pick = null;
        SetSide(_preset);
        OnFixTyped();
    }

    /// <summary>Follows the aircraft, in place: each row's radial and distance, the rose's needle, every command and the caption.</summary>
    public void Refresh() => ShowAll();

    private static TextBox Box(string name, string placeholder, string automationName, double width)
    {
        var box = new TextBox
        {
            Name = name,
            PlaceholderText = placeholder,
            Width = width,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(box, automationName);
        TakeoffFlyout.KeepClicksInside(box);
        return box;
    }

    private static TextBlock Refusal(string text) =>
        new()
        {
            Text = text,
            FontSize = 11,
            Foreground = FlyoutPanelParts.RefusalBrush,
            Margin = new Thickness(33, 0, 11, 2),
            IsVisible = false,
        };

    private static void AddShown(List<string> lines, TextBlock text)
    {
        if (text.IsVisible)
        {
            lines.Add(text.Text ?? "");
        }
    }

    private int? CurrentRadial(string navaid) => (_aircraft is null) ? null : JoinRadialFlyout.CurrentRadial(navaid, _aircraft.Position);

    private int? RadialFor(string navaid)
    {
        string text = RadialBox.Text ?? "";
        return string.IsNullOrWhiteSpace(text) ? CurrentRadial(navaid) : FixValueFlyout.ParseHeading(text);
    }

    private string Note(string navaid)
    {
        if ((_aircraft is null) || (CurrentRadial(navaid) is not { } radial) || (FixList.Position(navaid) is not { } position))
        {
            return "";
        }

        double miles = Math.Round(GeoMath.DistanceNm(_aircraft.Position, position), MidpointRounding.AwayFromZero);
        return $"{HoldFlyout.Three(radial)} radial · {miles.ToString("0", CultureInfo.InvariantCulture)} NM";
    }

    private string ToggleText(RadialSide side)
    {
        string text = $"{JoinRadialFlyout.ToggleLabel(side)} {JoinRadialFlyout.Verb(side)}";
        return (side == Side) ? $"[{text}]" : text;
    }

    private Task SendAsync(string? navaid, int radial) =>
        (navaid is null) ? Task.CompletedTask : _choose(JoinRadialFlyout.Command(Side, navaid, radial));

    private Task SendRowAsync(FixRow row) => (CommandFor(row.Fix) is { } command) ? _choose(command) : Task.CompletedTask;

    private void SetSide(RadialSide side)
    {
        Side = side;
        foreach (RadialSide each in Sides)
        {
            Toggle(each).IsChecked = (each == side);
        }

        _title.Text = JoinRadialFlyout.Title(side);
        ShowAll();
    }

    /// <summary>Lists the navaids the Fix box's text starts (all of them while it is empty), nearest first, then shows everything.</summary>
    private void OnFixTyped()
    {
        string prefix = (FixBox.Text ?? "").Trim();
        _picked = (prefix.Length == 0) ? _opening : JoinRadialFlyout.TypedNavaid(prefix);

        _rows.Clear();
        if ((_aircraft is not null) && (NavigationDatabase.InstanceOrNull is { } db))
        {
            foreach (NearbyFix navaid in db.NavaidsNear(_aircraft.Position, prefix).Take(JoinRadialFlyout.MaxNavaids))
            {
                var row = new FixRow(navaid.Name, SendRowAsync);
                row.GotFocus += (_, _) => PickRow(row);
                _rows.Add(row);
            }
        }

        _list.Children.Clear();
        _list.Children.Add(_listHeader);
        _list.Children.AddRange(_rows);
        ShowAll();
    }

    /// <summary>A row taking focus picks its navaid, which the rose then centres on.</summary>
    private void PickRow(FixRow row)
    {
        if (!string.Equals(_picked, row.Fix, StringComparison.OrdinalIgnoreCase))
        {
            _picked = row.Fix;
            ShowAll();
        }
    }

    private void OnRadialTyped()
    {
        Rose.Pick = FixValueFlyout.ParseHeading(RadialBox.Text ?? "");
        ShowAll();
    }

    /// <summary>A press, drag or key on the rose moved its pick: the Radial box follows it.</summary>
    private void OnRosePicked()
    {
        if ((Rose.Pick is { } pick) && (FixValueFlyout.ParseHeading(RadialBox.Text ?? "") != pick))
        {
            RadialBox.Text = HoldFlyout.Three(pick);
        }
    }

    /// <summary>Shows every row's note and command, the Fix box's command and refusal, the Radial box's refusal, the caption and the rose.</summary>
    private void ShowAll()
    {
        ShowRows();
        ShowBoxes();
        ShowCaptionAndRose();
    }

    /// <summary>Every row's command, note, and the outline on the picked navaid's row.</summary>
    private void ShowRows()
    {
        foreach (FixRow row in _rows)
        {
            string? command = CommandFor(row.Fix);
            row.SetCommand(command, command ?? $"{JoinRadialFlyout.Verb(Side)} {row.Fix}{TakeoffFlyout.Unfilled}");
            row.Show(false, false, Note(row.Fix));
            row.SetOutlined(string.Equals(row.Fix, Picked, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The Fix box's command column and refusal, and the Radial box's refusal.</summary>
    private void ShowBoxes()
    {
        string? picked = Picked;
        string? boxCommand = BoxCommand;
        BoxCommandText.Text = boxCommand ?? $"{JoinRadialFlyout.Verb(Side)} {picked ?? ""}{TakeoffFlyout.Unfilled}";
        BoxCommandText.Opacity = (boxCommand is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        FixError.IsVisible = !string.IsNullOrWhiteSpace(FixBox.Text) && (JoinRadialFlyout.TypedNavaid(FixBox.Text ?? "") is null);
        MenuCommandText.SetCommand(FixBox, boxCommand);
        MenuCommandText.SetCommand(RadialBox, boxCommand);
        string radialText = RadialBox.Text ?? "";
        RadialError.IsVisible = !string.IsNullOrWhiteSpace(radialText) && (FixValueFlyout.ParseHeading(radialText) is null);
    }

    /// <summary>The caption of what Enter sends, and the rose: the picked navaid in its centre, the needle on the aircraft's radial.</summary>
    private void ShowCaptionAndRose()
    {
        string? picked = Picked;
        int? radial = (picked is null) ? null : RadialFor(picked);
        Caption.Text = ((picked is not null) && (radial is { } shown)) ? JoinRadialFlyout.Caption(Side, picked, shown) : "";
        Caption.IsVisible = Caption.Text.Length > 0;
        Rose.CentreCaption = picked;
        Rose.PresentHeading = ((picked is not null) && (CurrentRadial(picked) is { } now)) ? now : 360;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Rose.IsKeyboardFocusWithin && await OnRoseKeyAsync(e))
        {
            return;
        }

        bool inBox =
            FixBox.IsKeyboardFocusWithin
            || RadialBox.IsKeyboardFocusWithin
            || ReferenceEquals(e.Source, FixBox)
            || ReferenceEquals(e.Source, RadialBox);
        int focused = _rows.FindIndex(row => row.IsKeyboardFocusWithin);
        switch (e.Key)
        {
            case Key.Enter when inBox:
                e.Handled = true;
                if (BoxCommand is { } command)
                {
                    await _choose(command);
                }

                break;
            case Key.Enter when focused >= 0:
                e.Handled = true;
                await SendRowAsync(_rows[focused]);
                break;
            case Key.Down or Key.Up:
                Walk(e, FixBox.IsKeyboardFocusWithin || ReferenceEquals(e.Source, FixBox), focused);
                break;
        }
    }

    /// <summary>The rose's keys: the arrows step the pick, Shift by 30°; Enter sends it. Whether the key was the rose's.</summary>
    private async Task<bool> OnRoseKeyAsync(KeyEventArgs e)
    {
        int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? HeadingFlyout.ShiftStep : HeadingFlyout.Step;
        switch (e.Key)
        {
            case Key.Right or Key.Up:
                e.Handled = true;
                Rose.StepBy(step);
                return true;
            case Key.Left or Key.Down:
                e.Handled = true;
                Rose.StepBy(-step);
                return true;
            case Key.Enter:
                e.Handled = true;
                if (BoxCommand is { } command)
                {
                    await _choose(command);
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Down from the Fix box focuses the picked navaid's row (else the first); Down and Up walk the rows; Up from the top row
    /// returns to the Fix box.
    /// </summary>
    private void Walk(KeyEventArgs e, bool inFixBox, int focused)
    {
        int? next = (inFixBox, focused, e.Key) switch
        {
            (true, _, Key.Down) when _rows.Count > 0 => Math.Max(0, _rows.FindIndex(row => row.IsOutlined)),
            (false, 0, Key.Up) => -1,
            (false, >= 0, Key.Up) => focused - 1,
            (false, >= 0, Key.Down) => Math.Min(focused + 1, _rows.Count - 1),
            _ => null,
        };
        if (next is not { } target)
        {
            return;
        }

        e.Handled = true;
        if (target < 0)
        {
            FixBox.Focus(NavigationMethod.Directional);
            return;
        }

        _rows[target].Focus(NavigationMethod.Directional);
        _rows[target].BringIntoView();
    }
}
