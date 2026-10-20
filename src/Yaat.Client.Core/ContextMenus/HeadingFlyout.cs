using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Yaat.Client.ContextMenus;

/// <summary>What the heading flyout's rose and box send: fly the heading, or turn left or right to it.</summary>
public enum HeadingVerb
{
    /// <summary><c>FH</c>: fly the heading, the shorter way round.</summary>
    FlyHeading,

    /// <summary><c>TL</c>: turn left to the heading.</summary>
    TurnLeft,

    /// <summary><c>TR</c>: turn right to the heading.</summary>
    TurnRight,
}

/// <summary>Where keyboard focus lands as the heading flyout opens.</summary>
public enum HeadingFlyoutFocus
{
    /// <summary>The heading box.</summary>
    Box,

    /// <summary>The left degree chips (their 30°).</summary>
    LeftChips,

    /// <summary>The right degree chips (their 30°).</summary>
    RightChips,
}

/// <summary>A heading entry's flyout as it opens: the entry's label, the toggle's verb and where focus lands.</summary>
/// <param name="Label">The entry's label (<c>Fly heading</c>, <c>Turn left (degrees)</c>).</param>
/// <param name="Verb">The verb the toggle starts on.</param>
/// <param name="Opening">Where keyboard focus lands.</param>
public sealed record HeadingFlyoutPreset(string Label, HeadingVerb Verb, HeadingFlyoutFocus Opening);

/// <summary>
/// The Fly heading flyout, one builder for the strip's Fly heading and All Commands' Fly heading, Turn left, Turn right and the
/// two degree-turn entries, each preset to its own verb (<see cref="HeadingFlyoutPreset"/>). Top to bottom: the title line (the
/// toggle's verb and the present heading), the Present heading row (<c>FPH</c>), the Fly heading / Turn left / Turn right
/// toggle, the heading box, a separator, the north-up compass rose (<see cref="HeadingRose"/>) between the left and right
/// degree chips (<see cref="DegreeTurns"/>, sending <c>LT</c> / <c>RT</c>), and the readout naming the heading under the
/// pointer or picked, its turn and its command. A rose pick (a click, or a drag's release) and the box send the toggle's verb
/// with a three-digit heading (<c>FH 005</c>; north is <c>360</c>); the box also reads <c>L180</c> / <c>R090</c> as a turn.
/// Down or Tab from the box focuses the rose at the present heading rounded to 5; the arrows step 5°, Shift 30°; Enter sends;
/// Tab moves on to the chips. While open, the needle, the title, the Present heading row and the readout follow the aircraft
/// once a second (<see cref="FlyoutLiveTick"/>); the pick stays where it is.
/// </summary>
public static partial class HeadingFlyout
{
    /// <summary>The heading box's name, by which a test or UI Automation finds it.</summary>
    public const string BoxName = "HeadingBox";

    /// <summary>What the empty heading box reads.</summary>
    public const string BoxPlaceholder = "e.g. 250, or L180";

    /// <summary>What the readout says while nothing is pointed at or picked.</summary>
    public const string ReadoutHint = "Point at the rose to pick a heading; ← → step 5°, Shift 30°";

    /// <summary>The rose's step, in degrees.</summary>
    public const int Step = 5;

    /// <summary>The rose's step with Shift held, in degrees.</summary>
    public const int ShiftStep = 30;

    /// <summary>How near the rose's centre a point picks nothing, in pixels.</summary>
    public const double DeadZoneRadius = 18;

    /// <summary>The degree chips' turns, either side.</summary>
    public static IReadOnlyList<int> DegreeTurns { get; } = [5, 10, 15, 20, 30, 45, 60, 90];

    /// <summary>The chip keyboard focus lands on when the flyout opens on a chip group.</summary>
    internal const int OpeningChip = 30;

    /// <summary>The entry <paramref name="preset"/> names: one menu row holding the flyout over <paramref name="aircraft"/>.</summary>
    /// <param name="preset">The entry's label, the verb the toggle starts on and where focus lands.</param>
    /// <param name="aircraft">The aircraft whose heading the flyout follows.</param>
    /// <param name="context">The menu's aircraft and initials.</param>
    /// <param name="host">The host the flyout sends through and whose clock it refreshes on.</param>
    public static MenuItem Build(HeadingFlyoutPreset preset, IMenuAircraft? aircraft, MenuContext context, IMenuHost host)
    {
        var entry = new MenuItem { Header = preset.Label };
        PanelFlyoutContent.Attach(entry, new HeadingFlyoutHost(), context, host, (choose, _) => new HeadingFlyoutPanel(preset, aircraft, choose));
        return entry;
    }

    /// <summary>The verb's command text: <c>FH</c>, <c>TL</c> or <c>TR</c>.</summary>
    public static string VerbText(HeadingVerb verb) =>
        verb switch
        {
            HeadingVerb.FlyHeading => "FH",
            HeadingVerb.TurnLeft => "TL",
            HeadingVerb.TurnRight => "TR",
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unknown heading verb."),
        };

    /// <summary>The verb's label: <c>Fly heading</c>, <c>Turn left</c> or <c>Turn right</c>.</summary>
    public static string Label(HeadingVerb verb) =>
        verb switch
        {
            HeadingVerb.FlyHeading => "Fly heading",
            HeadingVerb.TurnLeft => "Turn left",
            HeadingVerb.TurnRight => "Turn right",
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "Unknown heading verb."),
        };

    /// <summary>The command for <paramref name="heading"/> (1 to 360) under <paramref name="verb"/>: <c>FH 005</c>.</summary>
    public static string Command(HeadingVerb verb, int heading) => $"{VerbText(verb)} {heading.ToString("D3", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// The command the heading box sends for <paramref name="text"/>: a heading 1 to 360 under <paramref name="verb"/>, or with an
    /// <c>L</c> / <c>R</c> before it (either case) a turn left or right to it; null for anything else.
    /// </summary>
    public static string? BoxCommand(string text, HeadingVerb verb)
    {
        Match match = BoxPattern().Match(text);
        if (!match.Success || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int heading))
        {
            return null;
        }

        if (heading is < 1 or > 360)
        {
            return null;
        }

        HeadingVerb sent = match.Groups[1].Value.ToUpperInvariant() switch
        {
            "L" => HeadingVerb.TurnLeft,
            "R" => HeadingVerb.TurnRight,
            _ => verb,
        };
        return Command(sent, heading);
    }

    /// <summary>
    /// The heading at a point on the rose, <paramref name="dx"/> right and <paramref name="dy"/> down from its centre: north up,
    /// clockwise, snapped to <see cref="Step"/>, north as 360; null within <see cref="DeadZoneRadius"/> of the centre.
    /// </summary>
    public static int? HeadingAt(double dx, double dy)
    {
        if (Math.Sqrt((dx * dx) + (dy * dy)) < DeadZoneRadius)
        {
            return null;
        }

        return RoundedToFive(Math.Atan2(dx, -dy) * 180 / Math.PI);
    }

    /// <summary><paramref name="degrees"/> rounded to the nearest <see cref="Step"/>, in 5 to 360, north as 360.</summary>
    public static int RoundedToFive(double degrees) => OnTheCompass((int)(Math.Round(degrees / Step, MidpointRounding.AwayFromZero) * Step));

    /// <summary>The aircraft's magnetic heading to the whole degree, 1 to 360; 360 without an aircraft.</summary>
    public static int PresentHeading(IMenuAircraft? aircraft) => (aircraft is null) ? 360 : WholeHeading(aircraft.MagneticHeadingDegrees);

    /// <summary><paramref name="degrees"/> to the whole degree, 1 to 360, north as 360.</summary>
    public static int WholeHeading(double degrees) => OnTheCompass((int)Math.Round(degrees, MidpointRounding.AwayFromZero));

    /// <summary>
    /// The turn <paramref name="verb"/> makes from <paramref name="present"/> to <paramref name="heading"/>: its side (-1 left,
    /// 1 right, 0 none or a 180° turn under Fly heading) and its size in degrees (360 for a turn left or right to the present
    /// heading).
    /// </summary>
    public static (int Side, int Degrees) Turn(int present, HeadingVerb verb, int heading)
    {
        switch (verb)
        {
            case HeadingVerb.TurnLeft:
                return (-1, (Modulo(present - heading) is var left and > 0) ? left : 360);
            case HeadingVerb.TurnRight:
                return (1, (Modulo(heading - present) is var right and > 0) ? right : 360);
            default:
                int relative = Modulo(heading - present + 180) - 180;
                return ((relative == 0) || (Math.Abs(relative) == 180)) ? (0, Math.Abs(relative)) : (Math.Sign(relative), Math.Abs(relative));
        }
    }

    /// <summary>The turn as the readout names it: <c>a right turn of 30°</c>, <c>the present heading</c>.</summary>
    public static string TurnText(int present, HeadingVerb verb, int heading)
    {
        (int side, int degrees) = Turn(present, verb, heading);
        return (side, degrees) switch
        {
            (0, 0) => "the present heading",
            (0, _) => "a 180° turn, the pilot's choice of side",
            (< 0, _) => $"a left turn of {degrees}°",
            _ => $"a right turn of {degrees}°",
        };
    }

    /// <summary>A whole heading brought into 1 to 360, north as 360.</summary>
    internal static int OnTheCompass(int heading) => (Modulo(heading) is var wrapped and > 0) ? wrapped : 360;

    private static int Modulo(int degrees) => ((degrees % 360) + 360) % 360;

    [GeneratedRegex(@"^\s*([LR])?\s*(\d{1,3})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BoxPattern();
}

/// <summary>The heading flyout's menu row header, which holds the <see cref="HeadingFlyoutPanel"/> once the flyout first opens.</summary>
public sealed class HeadingFlyoutHost() : FlyoutPanelHost("heading flyout", "heading-flyout-panel");

/// <summary>
/// The heading flyout's one menu row (<see cref="HeadingFlyout"/>): the title line, the Present heading row, the verb toggle,
/// the heading box, a separator, the rose between the degree chips, and the readout.
/// </summary>
public sealed class HeadingFlyoutPanel : StackPanel, IFlyoutPanel
{
    private const double ChipColumnWidth = 56;
    private static readonly IImmutableSolidColorBrush ReadoutBrush = new ImmutableSolidColorBrush(Color.Parse("#CFD5DC"));

    private readonly HeadingFlyoutPreset _preset;
    private readonly IMenuAircraft? _aircraft;
    private readonly Func<string, Task> _choose;
    private readonly TextBlock _title = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly TextBlock _titleNow = new() { FontSize = 11, Foreground = FlyoutPanelParts.TitleBrush };
    private readonly TextBlock _presentNote = new()
    {
        FontSize = 12,
        Opacity = 0.8,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly List<ToggleButton> _toggles = [];
    private readonly List<Button> _leftChips;
    private readonly List<Button> _rightChips;
    private int _present = 360;

    internal HeadingFlyoutPanel(HeadingFlyoutPreset preset, IMenuAircraft? aircraft, Func<string, Task> choose)
    {
        _preset = preset;
        _aircraft = aircraft;
        _choose = choose;
        Verb = preset.Verb;
        MaxWidth = MenuCommandRow.RowMaxWidth;
        QuickCommandGlyph glyph =
            QuickCommandGlyphs.For(MenuIds.HeadingFly) ?? throw new InvalidOperationException($"No glyph for {MenuIds.HeadingFly}.");
        Children.Add(TitleLine());
        PresentRow = PresentHeadingRow(glyph);
        Children.Add(PresentRow);
        Children.Add(ToggleStrip());
        Box = new TextBox
        {
            Name = HeadingFlyout.BoxName,
            PlaceholderText = HeadingFlyout.BoxPlaceholder,
            Width = 150,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(Box, "Heading");
        TakeoffFlyout.KeepClicksInside(Box);
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                ShowBoxCommand();
            }
        };
        Children.Add(BoxRow(glyph));
        Children.Add(FlyoutPanelParts.SeparatorLine());
        Rose.ShownChanged += (_, _) => ShowReadout();
        Rose.Chosen += async (_, heading) => await _choose(HeadingFlyout.Command(Verb, heading));
        _leftChips = [.. HeadingFlyout.DegreeTurns.Select(degrees => Chip("LT", degrees))];
        _rightChips = [.. HeadingFlyout.DegreeTurns.Select(degrees => Chip("RT", degrees))];
        Children.Add(Compass());
        Children.Add(Readout);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        SetVerb(preset.Verb);
        Refresh();
    }

    /// <summary>The verb the rose and the box send now, which the toggle sets.</summary>
    public HeadingVerb Verb { get; private set; }

    /// <summary>The Present heading row, sending <c>FPH</c>.</summary>
    public Button PresentRow { get; }

    /// <summary>The heading box.</summary>
    public TextBox Box { get; }

    /// <summary>The command column beside the box: what Enter in it sends, or the verb with <c>···</c>, faded, while it cannot send.</summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>The compass rose.</summary>
    public HeadingRose Rose { get; } = new();

    /// <summary>The left degree chips, in <see cref="HeadingFlyout.DegreeTurns"/> order.</summary>
    public IReadOnlyList<Button> LeftChips => _leftChips;

    /// <summary>The right degree chips, in <see cref="HeadingFlyout.DegreeTurns"/> order.</summary>
    public IReadOnlyList<Button> RightChips => _rightChips;

    /// <summary>The readout under the rose.</summary>
    public TextBlock Readout { get; } =
        new()
        {
            FontSize = 12,
            Foreground = ReadoutBrush,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(11, 4, 11, 7),
            MinHeight = 18,
        };

    /// <summary>The command the box sends now; null while it cannot send.</summary>
    public string? BoxCommand => HeadingFlyout.BoxCommand(Box.Text ?? "", Verb);

    /// <summary>The toggle button of <paramref name="verb"/>.</summary>
    public ToggleButton Toggle(HeadingVerb verb) => _toggles[(int)verb];

    /// <summary>
    /// The panel as text, one line per part: the title line, the Present heading row, the toggle (the verb on in brackets), the
    /// box with its command, <c>---</c>, the left chips, the rose, the right chips and the readout.
    /// </summary>
    public IReadOnlyList<string> Lines =>
        [
            $"{HeadingFlyout.Label(Verb)} · now {Three(_present)}",
            PresentRowText(),
            string.Join(" | ", Enum.GetValues<HeadingVerb>().Select(ToggleText)),
            $"Heading — {BoxCommandText.Text}",
            "---",
            $"← Left by {string.Join(" ", _leftChips.Select(chip => chip.Content))}",
            $"rose · now {Three(_present)}{((Rose.Pick is { } pick) ? $" · pick {Three(pick)}" : "")}",
            $"Right by → {string.Join(" ", _rightChips.Select(chip => chip.Content))}",
            Readout.Text ?? "",
        ];

    Control IFlyoutPanel.View => this;

    void IFlyoutPanel.Reset() => Reset();

    void IFlyoutPanel.Refresh() => Refresh();

    void IFlyoutPanel.FocusOpeningLater(Func<bool> isOpen) => FocusOpeningLater(isOpen);

    /// <summary>Puts the flyout back as it opens: the toggle on the preset's verb, the box empty, nothing picked.</summary>
    internal void Reset()
    {
        SetVerb(_preset.Verb);
        Box.Text = "";
        Rose.ClearPointing();
        Rose.Pick = null;
    }

    /// <summary>Follows the aircraft's heading: the needle, the title, the Present heading row, the chips' tips and the readout.</summary>
    internal void Refresh()
    {
        _present = HeadingFlyout.PresentHeading(_aircraft);
        Rose.PresentHeading = _aircraft?.MagneticHeadingDegrees ?? 360;
        _titleNow.Text = $"now {Three(_present)}";
        _presentNote.Text = $" · {Three(_present)} now";
        AutomationProperties.SetName(PresentRow, PresentRowText());
        for (int index = 0; index < HeadingFlyout.DegreeTurns.Count; index++)
        {
            int degrees = HeadingFlyout.DegreeTurns[index];
            ToolTip.SetTip(_leftChips[index], $"LT {degrees} → {Three(HeadingFlyout.OnTheCompass(_present - degrees))}");
            ToolTip.SetTip(_rightChips[index], $"RT {degrees} → {Three(HeadingFlyout.OnTheCompass(_present + degrees))}");
        }

        ShowReadout();
    }

    /// <summary>Focuses where the preset says, once laid out and while <paramref name="isOpen"/> still holds.</summary>
    internal void FocusOpeningLater(Func<bool> isOpen) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!isOpen())
                {
                    return;
                }

                int chip = HeadingFlyout.DegreeTurns.ToList().IndexOf(HeadingFlyout.OpeningChip);
                Control target = _preset.Opening switch
                {
                    HeadingFlyoutFocus.LeftChips => _leftChips[chip],
                    HeadingFlyoutFocus.RightChips => _rightChips[chip],
                    _ => Box,
                };
                target.Focus(NavigationMethod.Directional);
            },
            DispatcherPriority.Loaded
        );

    private static string Three(int heading) => heading.ToString("D3", CultureInfo.InvariantCulture);

    private string PresentRowText() => $"Present heading · {Three(_present)} now — FPH";

    private string ToggleText(HeadingVerb verb)
    {
        string text = $"{HeadingFlyout.Label(verb)} {HeadingFlyout.VerbText(verb)}";
        return (verb == Verb) ? $"[{text}]" : text;
    }

    private void SetVerb(HeadingVerb verb)
    {
        Verb = verb;
        foreach (HeadingVerb each in Enum.GetValues<HeadingVerb>())
        {
            Toggle(each).IsChecked = (each == verb);
        }

        _title.Text = HeadingFlyout.Label(verb);
        Rose.Verb = verb;
        ShowBoxCommand();
        ShowReadout();
    }

    private void ShowBoxCommand()
    {
        string? command = BoxCommand;
        BoxCommandText.Text = command ?? $"{HeadingFlyout.VerbText(Verb)} {TakeoffFlyout.Unfilled}";
        BoxCommandText.Opacity = (command is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        MenuCommandText.SetCommand(Box, command);
    }

    private void ShowReadout()
    {
        if (Rose.Shown is not { } heading)
        {
            Readout.Text = HeadingFlyout.ReadoutHint;
            return;
        }

        Readout.Text = $"{Three(heading)} · {HeadingFlyout.TurnText(_present, Verb, heading)} · {HeadingFlyout.Command(Verb, heading)}";
    }

    /// <summary>The title line: the toggle's verb, and the present heading right-aligned.</summary>
    private Grid TitleLine()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(11, 6, 11, 2) };
        grid.Children.Add(_title);
        _titleNow.Bind(TextBlock.FontFamilyProperty, _titleNow.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        Grid.SetColumn(_titleNow, 1);
        grid.Children.Add(_titleNow);
        return grid;
    }

    /// <summary>The Present heading row: the heading glyph, the label with the present heading after it, and <c>FPH</c>.</summary>
    private Button PresentHeadingRow(QuickCommandGlyph glyph)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Viewbox icon = QuickCommandStrip.GlyphIcon(glyph);
        icon.Margin = new Thickness(0, 0, 8, 0);
        grid.Children.Add(icon);
        var name = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Present heading", VerticalAlignment = VerticalAlignment.Center },
                _presentNote,
            },
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        TextBlock command = FlyoutPanelParts.CommandText("FPH");
        Grid.SetColumn(command, 2);
        grid.Children.Add(command);
        var row = new Button
        {
            Content = grid,
            Padding = new Thickness(11, 5),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        MenuCommandText.SetCommand(row, "FPH");
        row.Click += async (_, e) =>
        {
            e.Handled = true;
            await _choose("FPH");
        };
        return row;
    }

    /// <summary>The Fly heading / Turn left / Turn right toggle: one button per verb, the verb's command beside its label.</summary>
    private Border ToggleStrip()
    {
        foreach (HeadingVerb verb in Enum.GetValues<HeadingVerb>())
        {
            _toggles.Add(
                FlyoutPanelParts.VerbToggle(
                    HeadingFlyout.Label(verb),
                    HeadingFlyout.VerbText(verb),
                    verb == HeadingVerb.FlyHeading,
                    () => SetVerb(verb)
                )
            );
        }

        return FlyoutPanelParts.ToggleStrip(_toggles);
    }

    /// <summary>The heading box's row: the heading glyph, the label with the box under it, and the command it sends.</summary>
    private Grid BoxRow(QuickCommandGlyph glyph) => FlyoutPanelParts.BoxRow(glyph, "Heading", Box, BoxCommandText);

    /// <summary>The rose between the left and right chip columns.</summary>
    private Grid Compass()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(9, 6, 9, 2),
        };
        StackPanel left = ChipColumn("← Left by", _leftChips);
        grid.Children.Add(left);
        Rose.Margin = new Thickness(8, 0);
        Grid.SetColumn(Rose, 1);
        grid.Children.Add(Rose);
        StackPanel right = ChipColumn("Right by →", _rightChips);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private static StackPanel ChipColumn(string label, List<Button> chips)
    {
        var column = new StackPanel
        {
            Spacing = 3,
            Width = ChipColumnWidth,
            VerticalAlignment = VerticalAlignment.Center,
        };
        column.Children.Add(
            new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = FlyoutPanelParts.TitleBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1),
            }
        );
        column.Children.AddRange(chips);
        return column;
    }

    /// <summary>A degree chip, <c>20°</c>, sending <paramref name="verb"/> with its degrees.</summary>
    private Button Chip(string verb, int degrees)
    {
        string command = $"{verb} {degrees.ToString(CultureInfo.InvariantCulture)}";
        return FlyoutPanelParts.Chip(
            $"{degrees.ToString(CultureInfo.InvariantCulture)}°",
            command,
            $"{((verb == "LT") ? "Left" : "Right")} by {degrees}° — {command}",
            _choose
        );
    }

    /// <summary>Every control Up, Down and Tab walk, in order: the Present heading row, the box, the rose, the left chips, the right chips.</summary>
    private List<Control> Focusables() => [PresentRow, Box, Rose, .. _leftChips, .. _rightChips];

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Rose.IsKeyboardFocusWithin && await OnRoseKeyAsync(e))
        {
            return;
        }

        if ((e.Key == Key.Enter) && Box.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            if (BoxCommand is { } command)
            {
                await _choose(command);
            }

            return;
        }

        OnWalkKey(e);
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
                Rose.Pick ??= HeadingFlyout.RoundedToFive(Rose.PresentHeading);
                await _choose(HeadingFlyout.Command(Verb, Rose.Pick.Value));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Up, Down and Tab walk the focusable parts; Left and Right on a chip cross to the other column.</summary>
    private void OnWalkKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.Tab:
                e.Handled = true;
                Step(((e.Key == Key.Down) || ((e.Key == Key.Tab) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))) ? 1 : -1);
                break;
            case Key.Left or Key.Right when FocusedChip() is { } at:
                e.Handled = true;
                List<Button> other = (e.Key == Key.Right) ? _rightChips : _leftChips;
                other[at].Focus(NavigationMethod.Directional);
                break;
        }
    }

    /// <summary>The index of the chip holding focus within its column, or null when no chip holds it.</summary>
    private int? FocusedChip()
    {
        int left = _leftChips.FindIndex(chip => chip.IsKeyboardFocusWithin);
        int right = _rightChips.FindIndex(chip => chip.IsKeyboardFocusWithin);
        return (left >= 0) ? left : ((right >= 0) ? right : null);
    }

    /// <summary>
    /// Focuses the control after (<paramref name="step"/> 1) or before (-1) the focused one, staying put at either end; the rose
    /// takes focus picking the present heading rounded to 5 when nothing is picked yet.
    /// </summary>
    private void Step(int step)
    {
        List<Control> order = Focusables();
        int at = order.FindIndex(control => control.IsKeyboardFocusWithin);
        int next = (at < 0) ? 0 : at + step;
        if ((next < 0) || (next >= order.Count))
        {
            return;
        }

        if (ReferenceEquals(order[next], Rose))
        {
            Rose.Pick ??= HeadingFlyout.RoundedToFive(Rose.PresentHeading);
        }

        order[next].Focus(NavigationMethod.Directional);
    }
}
