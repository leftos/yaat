using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Sim;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The Assign speed flyout, one builder for the strip's Assign speed and All Commands' Assign speed and Speed…. Top to bottom:
/// the title line (<c>now 250 kt</c>, with the present Mach where the Mach chips show), the Speed box with focus on open and the
/// readout beside it, a separator, the Resume normal speed (<c>RNS</c>) and Final approach speed (<c>RFAS</c>, its note the
/// filed type's approach speed) rows, a separator, the knots chips eight to a row (<c>SPD 250</c>), and at or above FL240 the
/// Mach chips (<c>MACH .78</c>), from <see cref="MenuCatalog.SpeedValues"/>. The present speed's chip (IAS to the nearest 10) and
/// the present Mach's chip are filled, the assigned speed's chip is outlined, and so is the chip the box names. The box reads a
/// positive whole number of knots (<c>SPD 250</c>); the readout shows the command of the chip or row under the pointer or
/// holding focus, else the box's. While open, the title, the marks and the final approach note follow the aircraft once a
/// second (<see cref="FlyoutLiveTick"/>); the chips are fixed when the flyout is built.
/// </summary>
public static class SpeedFlyout
{
    /// <summary>The speed box's name, by which a test or UI Automation finds it.</summary>
    public const string BoxName = "SpeedBox";

    /// <summary>What the empty speed box reads.</summary>
    public const string BoxPlaceholder = "e.g. 250";

    /// <summary>What the readout shows for box text that is no speed.</summary>
    public const string InvalidSpeed = "Not a valid speed";

    /// <summary>How many chips a grid row holds.</summary>
    public const int ChipsPerRow = 8;

    /// <summary>The class of the filled chip: the present speed's, or the present Mach's.</summary>
    public const string NowClass = "now";

    /// <summary>The class of the outlined chip of the assigned speed.</summary>
    public const string AssignedClass = "assigned";

    /// <summary>The class of the outlined chip the box names.</summary>
    public const string TypedClass = "typed";

    /// <summary>The entry: one menu row holding the flyout over <paramref name="aircraft"/>'s speeds.</summary>
    /// <param name="label">The entry's label (<c>Assign speed</c>, <c>Speed…</c>).</param>
    /// <param name="aircraft">The aircraft whose speed the flyout follows.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the flyout sends through and whose clock it refreshes on.</param>
    public static MenuItem Build(string label, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        MenuCatalog.SpeedChoices choices = MenuCatalog.SpeedValues(aircraft);
        var entry = new MenuItem { Header = label };
        PanelFlyoutContent.Attach(
            entry,
            new SpeedFlyoutHost(),
            context,
            host,
            (choose, close) => new SpeedFlyoutPanel(choices, aircraft, choose, close)
        );
        return entry;
    }

    /// <summary>A knots chip's command: <c>SPD 250</c>.</summary>
    public static string KnotsCommand(int knots) => $"SPD {knots.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A Mach chip's label: <c>M.78</c> for 78 hundredths.</summary>
    public static string MachLabel(int hundredths) => $"M.{hundredths.ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>A Mach chip's command: <c>MACH .78</c> for 78 hundredths.</summary>
    public static string MachCommand(int hundredths) => $"MACH .{hundredths.ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>The present speed's chip value: the IAS to the nearest 10 kt.</summary>
    internal static int PresentKnots(double ias) => (int)(Math.Round(ias / 10, MidpointRounding.AwayFromZero) * 10);

    /// <summary>The present Mach in hundredths, from the IAS at the altitude.</summary>
    internal static int PresentMach(double ias, double altitude) =>
        (int)Math.Round(WindInterpolator.IasToMach(ias, altitude) * 100, MidpointRounding.AwayFromZero);
}

/// <summary>The speed flyout's menu row header, which holds the <see cref="SpeedFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class SpeedFlyoutHost() : FlyoutPanelHost("speed flyout", "speed-flyout-panel");

/// <summary>
/// The speed flyout's one menu row (<see cref="SpeedFlyout"/>): the title line, the Speed box and its readout, the Resume normal
/// speed and Final approach speed rows, the knots chips and, at or above FL240, the Mach chips.
/// </summary>
public sealed class SpeedFlyoutPanel : StackPanel, IFlyoutPanel
{
    private static readonly IImmutableSolidColorBrush ChipFill = new ImmutableSolidColorBrush(Color.Parse("#23272C"));
    private static readonly IImmutableSolidColorBrush ChipLine = new ImmutableSolidColorBrush(Color.Parse("#33383F"));
    private static readonly IImmutableSolidColorBrush ChipText = new ImmutableSolidColorBrush(Color.Parse("#CFD5DC"));
    private static readonly IImmutableSolidColorBrush OutlineBrush = new ImmutableSolidColorBrush(Color.Parse("#8FC1FF"));

    private readonly List<int> _knots;
    private readonly List<int> _machs;
    private readonly IMenuAircraft? _aircraft;
    private readonly Func<string, Task> _choose;
    private readonly Action _close;
    private readonly List<Button> _knotChips;
    private readonly List<Button> _machChips;
    private readonly Dictionary<Button, string> _commands = [];
    private readonly TextBlock _titleNow = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly TextBlock _title = new()
    {
        Text = "Assign speed",
        FontSize = 11,
        Foreground = FlyoutPanelParts.TitleBrush,
    };
    private Button? _pointed;
    private int? _nowKnots;
    private int? _nowMach;
    private int? _assignedKnots;

    internal SpeedFlyoutPanel(MenuCatalog.SpeedChoices choices, IMenuAircraft? aircraft, Func<string, Task> choose, Action close)
    {
        _knots = [.. choices.Knots];
        _machs = [.. choices.MachHundredths];
        _aircraft = aircraft;
        _choose = choose;
        _close = close;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        QuickCommandGlyph glyph =
            QuickCommandGlyphs.For(MenuIds.SpeedAssign) ?? throw new InvalidOperationException($"No glyph for {MenuIds.SpeedAssign}.");
        Children.Add(FlyoutPanelParts.TitleLine(_title, _titleNow));
        Box = SpeedBox();
        Children.Add(BoxRow(glyph));
        Children.Add(FlyoutPanelParts.SeparatorLine());
        ResumeRow = GlyphRow(glyph, "Resume normal speed", new TextBlock(), "RNS");
        Children.Add(ResumeRow);
        FinalApproachRow = GlyphRow(glyph, "Final approach speed", FinalApproachNote, "RFAS");
        Children.Add(FinalApproachRow);
        Children.Add(FlyoutPanelParts.SeparatorLine());
        _knotChips = [.. _knots.Select(knots => Chip(knots.ToString(CultureInfo.InvariantCulture), SpeedFlyout.KnotsCommand(knots)))];
        _machChips = [.. _machs.Select(mach => Chip(SpeedFlyout.MachLabel(mach), SpeedFlyout.MachCommand(mach)))];
        Children.Add(GroupLabel("Knots"));
        Children.Add(ChipGrid(_knotChips));
        MachGroup = new StackPanel { IsVisible = _machChips.Count > 0, Children = { GroupLabel("Mach"), ChipGrid(_machChips) } };
        Children.Add(MachGroup);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, (_, _) => ShowReadout(), RoutingStrategies.Bubble);
        AddHandler(LostFocusEvent, (_, _) => ShowReadout(), RoutingStrategies.Bubble);
        Refresh();
    }

    /// <summary>The Speed box.</summary>
    public TextBox Box { get; }

    /// <summary>
    /// The readout beside the box: the command of the chip or row under the pointer or holding focus, else what Enter in the box
    /// sends, <c>SPD ···</c> faded while the box is empty, or the refusal in red.
    /// </summary>
    public TextBlock Readout { get; } = new();

    /// <summary>The Resume normal speed row, sending <c>RNS</c>.</summary>
    public Button ResumeRow { get; }

    /// <summary>The Final approach speed row, sending <c>RFAS</c>.</summary>
    public Button FinalApproachRow { get; }

    /// <summary>The Final approach speed row's note: the filed type's approach speed (<c>144 kt</c>), empty without a filed type.</summary>
    public TextBlock FinalApproachNote { get; } =
        new()
        {
            FontSize = 12,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>The knots chips, lowest first; fixed for the panel's life.</summary>
    public IReadOnlyList<Button> KnotChips => _knotChips;

    /// <summary>The Mach chips, lowest first; empty below FL240 at the flyout's building, and fixed for the panel's life.</summary>
    public IReadOnlyList<Button> MachChips => _machChips;

    /// <summary>The Mach group (its label and chips), shown only where there are Mach chips.</summary>
    public Control MachGroup { get; }

    /// <summary>The command the box sends now; null while it holds no speed.</summary>
    public string? BoxCommand => MenuCatalog.FormatAssignedSpeed(Box.Text ?? "");

    /// <summary>The knots of the chip the box outlines; null while the box names no chip.</summary>
    public int? OutlinedKnots { get; private set; }

    /// <summary>The title line as text: <c>Assign speed · now 250 kt · M.75</c>.</summary>
    public string Title => (_titleNow.Text is { Length: > 0 } now) ? $"{_title.Text} · {now}" : _title.Text ?? "";

    /// <summary>The knots chip of <paramref name="knots"/>.</summary>
    public Button KnotChip(int knots)
    {
        int index = _knots.IndexOf(knots);
        return (index >= 0) ? _knotChips[index] : throw new ArgumentOutOfRangeException(nameof(knots), knots, "No chip offers this speed.");
    }

    /// <summary>The Mach chip labelled <paramref name="label"/> (<c>M.78</c>).</summary>
    public Button MachChip(string label) =>
        _machChips.FirstOrDefault(chip => (chip.Content as string) == label)
        ?? throw new ArgumentOutOfRangeException(nameof(label), label, "No chip offers this Mach.");

    Control IFlyoutPanel.View => this;

    void IFlyoutPanel.Reset()
    {
        Box.Text = "";
        _pointed = null;
        ShowReadout();
    }

    void IFlyoutPanel.Refresh() => Refresh();

    void IFlyoutPanel.FocusOpeningLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(Box, isOpen, () => { });

    /// <summary>Follows the aircraft: the title, the filled and outlined chips, and the final approach speed note.</summary>
    private void Refresh()
    {
        ShowFinalApproach();
        if (_aircraft is not { } aircraft)
        {
            _nowKnots = null;
            _nowMach = null;
            _assignedKnots = null;
            _titleNow.Text = "";
            ShowMarks();
            return;
        }

        double ias = aircraft.IndicatedAirspeedKnots;
        _nowKnots = SpeedFlyout.PresentKnots(ias);
        _nowMach = (_machChips.Count > 0) ? SpeedFlyout.PresentMach(ias, aircraft.AltitudeFeet) : null;
        _assignedKnots = (aircraft.AssignedSpeed is { } assigned and > 0) ? SpeedFlyout.PresentKnots(assigned) : null;
        string now = $"now {Math.Round(ias, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)} kt";
        _titleNow.Text = (_nowMach is { } mach) ? $"{now} · {SpeedFlyout.MachLabel(mach)}" : now;
        ShowMarks();
    }

    /// <summary>The Final approach speed row's note and automation name, from the filed type's approach speed.</summary>
    private void ShowFinalApproach()
    {
        FinalApproachNote.Text = (MenuCatalog.FinalApproachKnots(_aircraft) is { } knots) ? $"{knots.ToString(CultureInfo.InvariantCulture)} kt" : "";
        string note = (FinalApproachNote.Text.Length > 0) ? $" · {FinalApproachNote.Text}" : "";
        AutomationProperties.SetName(FinalApproachRow, $"Final approach speed{note} — RFAS");
    }

    /// <summary>Paints every chip by its marks: filled for the present speed or Mach, outlined for the assigned or typed speed.</summary>
    private void ShowMarks()
    {
        for (int i = 0; i < _knotChips.Count; i++)
        {
            int knots = _knots[i];
            Mark(_knotChips[i], knots == _nowKnots, knots == _assignedKnots, knots == OutlinedKnots);
        }

        for (int i = 0; i < _machChips.Count; i++)
        {
            Mark(_machChips[i], _machs[i] == _nowMach, false, false);
        }
    }

    private static void Mark(Button chip, bool now, bool assigned, bool typed)
    {
        chip.Classes.Set(SpeedFlyout.NowClass, now);
        chip.Classes.Set(SpeedFlyout.AssignedClass, assigned);
        chip.Classes.Set(SpeedFlyout.TypedClass, typed);
        bool outlined = assigned || typed;
        chip.Background = now ? FlyoutPanelParts.NowFill : ChipFill;
        chip.Foreground = now ? Brushes.White : ChipText;
        chip.BorderBrush = outlined ? OutlineBrush : (now ? FlyoutPanelParts.NowEdge : ChipLine);
        chip.BorderThickness = new Thickness(outlined ? 2 : 1);
    }

    private void OnBoxChanged()
    {
        OutlinedKnots =
            ((BoxCommand is not null) && int.TryParse(Box.Text, CultureInfo.InvariantCulture, out int knots) && _knots.Contains(knots))
                ? knots
                : null;
        ShowMarks();
        ShowReadout();
        MenuCommandText.SetCommand(Box, BoxCommand);
    }

    /// <summary>Shows the pointed-at or focused chip's or row's command in the readout, else the box's.</summary>
    private void ShowReadout()
    {
        Button? shown = _pointed ?? _commands.Keys.FirstOrDefault(control => control.IsKeyboardFocusWithin);
        if (shown is not null)
        {
            Readout.Text = _commands[shown];
            Readout.ClearValue(TextBlock.ForegroundProperty);
            Readout.Opacity = 0.7;
            return;
        }

        string? command = BoxCommand;
        bool blank = string.IsNullOrWhiteSpace(Box.Text);
        Readout.Text = command ?? (blank ? $"SPD {TakeoffFlyout.Unfilled}" : SpeedFlyout.InvalidSpeed);
        if ((command is null) && !blank)
        {
            Readout.Foreground = FlyoutPanelParts.RefusalBrush;
        }
        else
        {
            Readout.ClearValue(TextBlock.ForegroundProperty);
        }

        Readout.Opacity = (command is not null) ? 0.7 : (blank ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 1);
    }

    private TextBox SpeedBox()
    {
        var box = new TextBox
        {
            Name = SpeedFlyout.BoxName,
            PlaceholderText = SpeedFlyout.BoxPlaceholder,
            Width = 110,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(box, "Speed (knots)");
        TakeoffFlyout.KeepClicksInside(box);
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnBoxChanged();
            }
        };
        return box;
    }

    /// <summary>The box row: the speed glyph, the <c>Speed (knots)</c> label with the box under it, and the readout right.</summary>
    private Grid BoxRow(QuickCommandGlyph glyph)
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
                new TextBlock { Text = "Speed (knots)" },
                Box,
            },
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Readout.FontSize = 12;
        Readout.Margin = new Thickness(16, 0, 0, 0);
        Readout.HorizontalAlignment = HorizontalAlignment.Right;
        Readout.VerticalAlignment = VerticalAlignment.Center;
        FlyoutPanelParts.Mono(Readout);
        Grid.SetColumn(Readout, 2);
        grid.Children.Add(Readout);
        return grid;
    }

    /// <summary>A row with the speed glyph, <paramref name="label"/> with <paramref name="note"/> after it, and its command right.</summary>
    private Button GlyphRow(QuickCommandGlyph glyph, string label, TextBlock note, string command)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Viewbox icon = QuickCommandStrip.GlyphIcon(glyph);
        icon.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(icon);
        note.Margin = new Thickness(6, 0, 0, 0);
        note.Foreground = FlyoutPanelParts.TitleBrush;
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
                note,
            },
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var commandText = new TextBlock
        {
            Text = command,
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        FlyoutPanelParts.Mono(commandText);
        Grid.SetColumn(commandText, 2);
        grid.Children.Add(commandText);
        var row = new Button
        {
            Content = grid,
            Padding = new Thickness(11, 5),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(row, $"{label} — {command}");
        Choosable(row, command);
        return row;
    }

    /// <summary>A chip showing <paramref name="label"/> and sending <paramref name="command"/>, which its tooltip names.</summary>
    private Button Chip(string label, string command)
    {
        var chip = new Button
        {
            Content = label,
            FontSize = 12,
            Width = 46,
            Height = 26,
            Margin = new Thickness(1.5),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(3),
        };
        chip.Bind(TemplatedControl.FontFamilyProperty, chip.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        ToolTip.SetTip(chip, command);
        AutomationProperties.SetName(chip, $"{label} — {command}");
        Choosable(chip, command);
        Mark(chip, false, false, false);
        return chip;
    }

    /// <summary>Has <paramref name="control"/> send <paramref name="command"/> on a click and show it in the readout while pointed at.</summary>
    private void Choosable(Button control, string command)
    {
        _commands[control] = command;
        MenuCommandText.SetCommand(control, command);
        control.Click += async (_, e) =>
        {
            e.Handled = true;
            await _choose(command);
        };
        control.PointerEntered += (_, _) =>
        {
            _pointed = control;
            ShowReadout();
        };
        control.PointerExited += (_, _) =>
        {
            if (ReferenceEquals(_pointed, control))
            {
                _pointed = null;
                ShowReadout();
            }
        };
    }

    private static TextBlock GroupLabel(string text) =>
        new()
        {
            Text = text,
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            Margin = new Thickness(11, 5, 11, 2),
        };

    private static UniformGrid ChipGrid(List<Button> chips)
    {
        var grid = new UniformGrid
        {
            Columns = SpeedFlyout.ChipsPerRow,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(9, 0, 9, 6),
        };
        grid.Children.AddRange(chips);
        return grid;
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await OnEnterAsync(e);
            return;
        }

        if (e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right))
        {
            return;
        }

        if (WalkTarget(e.Key) is { } target)
        {
            e.Handled = true;
            target.Focus(NavigationMethod.Directional);
            target.BringIntoView();
        }
        else if (_commands.Keys.Any(control => control.IsKeyboardFocusWithin) || ((e.Key == Key.Up) && Box.IsKeyboardFocusWithin))
        {
            // A walk off an end keeps focus where it is; the menu behind never sees the key.
            e.Handled = true;
        }
    }

    /// <summary>Enter sends the box's command (closing the menu when the box is empty), or the focused row's or chip's.</summary>
    private async Task OnEnterAsync(KeyEventArgs e)
    {
        if (Box.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            if (BoxCommand is { } command)
            {
                await _choose(command);
            }
            else if (string.IsNullOrWhiteSpace(Box.Text))
            {
                _close();
            }

            return;
        }

        if (_commands.Keys.FirstOrDefault(control => control.IsKeyboardFocusWithin) is { } focused)
        {
            e.Handled = true;
            await _choose(_commands[focused]);
        }
    }

    /// <summary>Where <paramref name="key"/> moves focus from the focused part; null where it stays or the key is not a walk.</summary>
    private Control? WalkTarget(Key key)
    {
        int knot = _knotChips.FindIndex(chip => chip.IsKeyboardFocusWithin);
        int mach = _machChips.FindIndex(chip => chip.IsKeyboardFocusWithin);
        if (knot >= 0)
        {
            return GridStep(key, _knotChips, knot, KnotsBelow, _ => FinalApproachRow);
        }

        if (mach >= 0)
        {
            return GridStep(key, _machChips, mach, _ => null, MachAbove);
        }

        return (key, Box.IsKeyboardFocusWithin, ResumeRow.IsKeyboardFocusWithin, FinalApproachRow.IsKeyboardFocusWithin) switch
        {
            (Key.Down, true, _, _) => ResumeRow,
            (Key.Down, _, true, _) => FinalApproachRow,
            (Key.Down, _, _, true) => FirstChip(),
            (Key.Up, _, true, _) => Box,
            (Key.Up, _, _, true) => ResumeRow,
            _ => null,
        };
    }

    /// <summary>
    /// A grid's step from the chip at <paramref name="at"/>: Left and Right one chip, Up and Down one row of eight; Down from the
    /// last row goes to <paramref name="below"/>, Up from the first to <paramref name="above"/>, each given the chip's column.
    /// </summary>
    private static Control? GridStep(Key key, List<Button> chips, int at, Func<int, Control?> below, Func<int, Control?> above)
    {
        int column = at % SpeedFlyout.ChipsPerRow;
        int lastRowStart = ((chips.Count - 1) / SpeedFlyout.ChipsPerRow) * SpeedFlyout.ChipsPerRow;
        return key switch
        {
            Key.Left => chips[Math.Max(at - 1, 0)],
            Key.Right => chips[Math.Min(at + 1, chips.Count - 1)],
            Key.Up => (at < SpeedFlyout.ChipsPerRow) ? above(column) : chips[at - SpeedFlyout.ChipsPerRow],
            Key.Down => (at >= lastRowStart) ? below(column) : chips[Math.Min(at + SpeedFlyout.ChipsPerRow, chips.Count - 1)],
            _ => null,
        };
    }

    /// <summary>Down from the Final approach speed row: the present speed's chip, else the first knots chip, else the first Mach chip.</summary>
    private Button? FirstChip()
    {
        int now = (_nowKnots is { } knots) ? _knots.IndexOf(knots) : -1;
        return (now >= 0) ? _knotChips[now] : (_knotChips.FirstOrDefault() ?? _machChips.FirstOrDefault());
    }

    /// <summary>Down from the last knots row: the Mach chip in the same column of the Mach grid's first row, its last chip if shorter.</summary>
    private Control? KnotsBelow(int column) => (_machChips.Count > 0) ? _machChips[Math.Min(column, _machChips.Count - 1)] : null;

    /// <summary>Up from the first Mach row: the knots chip in the same column of the last knots row, else the Final approach speed row.</summary>
    private Control MachAbove(int column)
    {
        if (_knotChips.Count == 0)
        {
            return FinalApproachRow;
        }

        int lastRowStart = ((_knotChips.Count - 1) / SpeedFlyout.ChipsPerRow) * SpeedFlyout.ChipsPerRow;
        return _knotChips[Math.Min(lastRowStart + column, _knotChips.Count - 1)];
    }
}
