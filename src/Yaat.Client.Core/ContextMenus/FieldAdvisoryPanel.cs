using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Yaat.Client.Services;
using Yaat.Sim.Commands;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The field advisory flyout: the title line (with the <c>solo</c> mark in solo training), the Field advisory box taking
/// <c>clock miles</c> with the ghost under it and the command beside it, the lead naming the field, the clock dial (captioned
/// <c>field</c>) and the Miles stepper, which write the box as typing in it moves them, the line the pilot hears for a complete
/// advisory, the check line saying the sim does not grade it, the Enter button, the note, then the field's row: its identifier,
/// name and where it is. The true clock and miles are pre-filled in RPO mode and in solo with true values, and follow the
/// aircraft until the user types or touches the dial or stepper; with values left for the student the box starts empty and the
/// row says to find the field on the scope. Enter sends <c>RFIS {clock} {miles}</c>: the flyout closes when the sim takes it, and
/// a refusal, or a send that never reached the sim, keeps it open with the reply and the values as sent. No radar cue: the field
/// is no aircraft target.
/// </summary>
public sealed class FieldAdvisoryPanel : StackPanel, IAdvisoryFlyoutPanel
{
    /// <summary>The box's label.</summary>
    public const string BoxLabel = "Field advisory";

    /// <summary>The box's name, by which a test or UI Automation finds it.</summary>
    public const string BoxName = "FieldAdvisoryBox";

    /// <summary>The box's placeholder.</summary>
    public const string Placeholder = "clock miles: 12 5";

    /// <summary>The caption under the dial's arrow.</summary>
    public const string FieldCaption = "field";

    /// <summary>The check line: the sim records any clock and miles as the field advisory.</summary>
    public const string NotCheckedText = "not checked: the sim does not grade the field advisory";

    /// <summary>The send button's text.</summary>
    public const string SendText = "Enter ⏎";

    /// <summary>The highest miles the stepper steps to; the sim takes any positive whole miles.</summary>
    public const int MaxMiles = 999;

    private const double PanelWidth = 432;

    private static readonly IImmutableSolidColorBrush SubBrush = new ImmutableSolidColorBrush(Color.Parse("#9AA3AD"));
    private static readonly IImmutableSolidColorBrush HintBrush = new ImmutableSolidColorBrush(Color.Parse("#8F969E"));
    private static readonly IImmutableSolidColorBrush ScopeBrush = new ImmutableSolidColorBrush(Color.Parse("#6F7883"));
    private static readonly IImmutableSolidColorBrush CommandBrush = new ImmutableSolidColorBrush(Color.Parse("#CFE0FF"));

    private readonly IMenuAircraft _aircraft;
    private readonly AdvisoryField _field;
    private readonly MenuContext _context;
    private readonly IMenuHost _host;
    private readonly AdvisorySend _sender;
    private readonly Action _close;
    private readonly TextBlock _titleNow = new();
    private readonly TextBlock _ghostView = new()
    {
        FontSize = 11,
        Foreground = HintBrush,
        Margin = new Thickness(0, 3, 0, 0),
    };
    private readonly TextBlock _sendCommand = new()
    {
        FontSize = 12,
        Foreground = CommandBrush,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _rowName = new() { FontSize = 13, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _rowWhere = GroundTrafficLook.Subtle("", 11);
    private readonly IBrush? _whereBrush;
    private IDisposable? _updates;
    private int? _clock;
    private int? _miles;
    private bool _owned;
    private bool _composing;

    internal FieldAdvisoryPanel(IMenuAircraft aircraft, AdvisoryField field, MenuContext context, IMenuHost host, AdvisoryFlyoutContent content)
    {
        _aircraft = aircraft;
        _field = field;
        _context = context;
        _host = host;
        _sender = new AdvisorySend(content.SendAsync);
        _close = content.Close;
        _whereBrush = _rowWhere.Foreground;
        Width = PanelWidth;
        Box = new TextBox
        {
            Name = BoxName,
            PlaceholderText = Placeholder,
            Width = 250,
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(Box, BoxLabel);
        TakeoffFlyout.KeepClicksInside(Box);
        Miles = new AdvisoryStepper("Miles", 1, MaxMiles, 1, miles => miles.ToString(CultureInfo.InvariantCulture));
        SendButton = NewSendButton();
        Children.Add(SoloAdvisoryPanel.AdvisoryTitleLine(FieldAdvisoryFlyout.Title, _titleNow, context.SoloTrainingMode));
        Children.Add(BoxRowView());
        Children.Add(AdvisoryView());
        Children.Add(FlyoutPanelParts.SeparatorLine());
        Children.Add(FieldRowView());
        Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                OnTyped();
            }
        };
        Dial.Picked += hour => Build(int.Parse(hour, CultureInfo.InvariantCulture), _miles);
        Miles.Changed += miles => Build(_clock, miles);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Refresh();
    }

    /// <summary>The flyout's title.</summary>
    public string Title => FieldAdvisoryFlyout.Title;

    /// <summary>The title line's right side (<see cref="AirborneTrafficFlyout.TitleNow"/>).</summary>
    public string TitleNow => _titleNow.Text ?? "";

    /// <summary>Whether the title line carries the <c>solo</c> mark: only in solo training.</summary>
    public bool HasSoloBadge => _context.SoloTrainingMode;

    /// <summary>The note beside the box's label: <c>KOAK · destination</c>.</summary>
    public string BoxSub => $"{_field.Id} · destination";

    /// <summary>The lead over the builder: <c>Field advisory for KOAK</c>.</summary>
    public string Lead => $"Field advisory for {_field.Id}";

    /// <summary>The box: <c>clock miles</c>.</summary>
    public TextBox Box { get; }

    /// <summary>The command column beside the box: <c>RFIS {clock} {miles}</c> once complete, else <c>RFIS ···</c>.</summary>
    public TextBlock BoxCommandText { get; } = new();

    /// <summary>The ghost under the box: <c>complete · Enter sends</c>, or the next part as <c>next: ‹kind›</c>.</summary>
    public TextBlock GhostText { get; } = new();

    /// <summary>The clock dial, captioned <see cref="FieldCaption"/>.</summary>
    public AdvisoryClockDial Dial { get; } = new();

    /// <summary>The dial's caption.</summary>
    public string DialCaption => FieldCaption;

    /// <summary>The Miles stepper, 1 to <see cref="MaxMiles"/>.</summary>
    public AdvisoryStepper Miles { get; }

    /// <summary>The line the pilot hears for a complete advisory, in quotes; hidden for any other.</summary>
    public TextBlock SpokenText { get; } =
        new()
        {
            FontSize = 12.5,
            FontStyle = FontStyle.Italic,
            Foreground = SoloAdvisoryPanel.KeyCapText,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

    /// <summary>The check line, always <see cref="NotCheckedText"/>.</summary>
    public TextBlock CheckText { get; } =
        new()
        {
            Text = NotCheckedText,
            FontSize = 11.5,
            Foreground = HintBrush,
            TextWrapping = TextWrapping.Wrap,
        };

    /// <summary>The note under the advisory: the sim's refusal, a send that never reached it, or the parts Enter still needs.</summary>
    public TextBlock NoteText { get; } =
        new()
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

    /// <summary>The send button, enabled once the advisory is complete.</summary>
    public Button SendButton { get; }

    /// <summary>The field row's first line: <c>● KOAK · Oakland San Francisco Bay · destination</c>.</summary>
    public string FieldRowName =>
        (_field.Name.Length > 0)
            ? $"{SoloAdvisoryPanel.PointedMark} {_field.Id} · {_field.Name} · destination"
            : $"{SoloAdvisoryPanel.PointedMark} {_field.Id} · destination";

    /// <summary>
    /// The field row's second line: <c>12 o'clock · 5 nm · runways 28L 28R 30</c>, live, or, with values left for the student,
    /// <see cref="SoloAdvisoryPanel.FindOnScope"/>.
    /// </summary>
    public string FieldRowWhere => _rowWhere.Text ?? "";

    /// <summary>Whether the field row's second line is set in italics, as it is while it says to find the field on the scope.</summary>
    public bool IsFieldRowWhereItalic => _rowWhere.FontStyle == FontStyle.Italic;

    /// <summary>Whether the true clock and miles are offered: always in RPO mode, in solo only with true values pre-filled.</summary>
    private bool TrueValues => !_context.SoloTrainingMode || (_context.Session.SoloAdvisoryPrefill == SoloAdvisoryPrefill.TrueValues);

    /// <summary>The command a complete advisory sends, <c>RFIS {clock} {miles}</c>; null while a part is unset.</summary>
    private static string? CommandFor(int? clock, int? miles) =>
        ((clock is { } c) && (miles is { } m)) ? $"{FieldAdvisoryFlyout.Verb} {c} {m}" : null;

    /// <summary>The send line's command: the advisory with each unset part as its gap, <c>RFIS 7 ‹miles›</c>.</summary>
    private static string SendLineText(int? clock, int? miles) =>
        $"{FieldAdvisoryFlyout.Verb} {clock?.ToString(CultureInfo.InvariantCulture) ?? SoloAdvisoryText.Gap(AdvisoryPart.Clock)} "
        + (miles?.ToString(CultureInfo.InvariantCulture) ?? SoloAdvisoryText.Gap(AdvisoryPart.Miles));

    /// <summary>The parts an advisory of <paramref name="clock"/> and <paramref name="miles"/> still needs, in the order they are said.</summary>
    private static List<AdvisoryPart> Missing(int? clock, int? miles)
    {
        List<AdvisoryPart> missing = [];
        if (clock is null)
        {
            missing.Add(AdvisoryPart.Clock);
        }

        if (miles is null)
        {
            missing.Add(AdvisoryPart.Miles);
        }

        return missing;
    }

    /// <summary>
    /// The box read word by word: the clock (1 to 12) and the miles (a positive whole number) where the first and second words
    /// give them; later words are not read.
    /// </summary>
    private (int? Clock, int? Miles, int Words) Reading()
    {
        string[] words = (Box.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int? clock = ((words.Length > 0) && int.TryParse(words[0], out int hour) && (hour is >= 1 and <= 12)) ? hour : null;
        int? miles = ((words.Length > 1) && int.TryParse(words[1], out int nm) && (nm > 0)) ? nm : null;
        return (clock, miles, words.Length);
    }

    void IAdvisoryFlyoutPanel.Refill()
    {
        FollowTruth();
        Refresh();
    }

    /// <summary>The field advisory has no list whose order a pointer could hold.</summary>
    void IAdvisoryFlyoutPanel.Hold(bool inside) { }

    void IAdvisoryFlyoutPanel.Opened()
    {
        _updates?.Dispose();
        _updates = _host.ObserveAircraftUpdates(() =>
        {
            FollowTruth();
            Refresh();
        });
        FollowTruth();
        Refresh();
    }

    void IAdvisoryFlyoutPanel.Reset()
    {
        _sender.Forget();
        _updates?.Dispose();
        _updates = null;
        _owned = false;
        _clock = null;
        _miles = null;
        Compose();
        NoteText.IsVisible = false;
    }

    void IAdvisoryFlyoutPanel.FocusBoxLater(Func<bool> isOpen) => FlyoutPanelParts.FocusLater(Box, isOpen, () => { });

    private Grid BoxRowView()
    {
        QuickCommandGlyph glyph =
            QuickCommandGlyphs.For(MenuIds.ApproachReportFieldInSight) ?? throw new InvalidOperationException("No Report field in sight glyph.");
        var sub = new TextBlock
        {
            Text = BoxSub,
            FontSize = 11.5,
            Foreground = SubBrush,
        };
        FlyoutPanelParts.Mono(sub);
        FlyoutPanelParts.Mono(_ghostView);
        var boxColumn = new StackPanel { Children = { sub, Box, _ghostView } };
        Grid row = FlyoutPanelParts.BoxRow(glyph, BoxLabel, boxColumn, BoxCommandText);
        BoxCommandText.TextTrimming = TextTrimming.CharacterEllipsis;
        return row;
    }

    /// <summary>The lead, the dial and the stepper, the spoken line, the check line, the send line and the note.</summary>
    private StackPanel AdvisoryView()
    {
        var field = new Run(_field.Id)
        {
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = SoloAdvisoryPanel.StrongBrush,
        };
        field.Bind(TextElement.FontFamilyProperty, field.GetResourceObservable(QuickCommandStrip.MonoFontKey));
        var lead = new TextBlock
        {
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            Inlines = [new Run("Field advisory for "), field],
        };
        AutomationProperties.SetName(lead, Lead);
        var milesRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                new TextBlock
                {
                    Text = "Miles",
                    FontSize = 11.5,
                    Foreground = FlyoutPanelParts.TitleBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Miles,
            },
        };
        var builder = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { Dial, milesRow },
        };
        FlyoutPanelParts.Mono(CheckText);
        FlyoutPanelParts.Mono(_sendCommand);
        var sendLine = new DockPanel { Children = { SendButton, _sendCommand } };
        DockPanel.SetDock(SendButton, Dock.Right);
        return new StackPanel
        {
            Spacing = 7,
            Margin = new Thickness(11, 6, 11, 8),
            Children = { lead, builder, SpokenText, CheckText, sendLine, NoteText },
        };
    }

    private Button NewSendButton()
    {
        var button = new Button
        {
            Content = SendText,
            FontSize = 11,
            Padding = new Thickness(7, 1),
            MinHeight = 0,
            Foreground = SoloAdvisoryPanel.KeyCapText,
            Background = FlyoutPanelParts.ToggleFill,
            BorderBrush = SoloAdvisoryPanel.KeyCapLine,
            BorderThickness = new Thickness(1, 1, 1, 2),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, SendText);
        button.Click += async (_, e) =>
        {
            e.Handled = true;
            await EnterAsync();
        };
        return button;
    }

    /// <summary>
    /// The field's row, lit as the one the advisory names: the mark, the identifier, name and <c>destination</c>, then where it is.
    /// </summary>
    private Border FieldRowView()
    {
        var mark = new TextBlock
        {
            Text = SoloAdvisoryPanel.PointedMark,
            FontSize = 10,
            Foreground = FlyoutPanelParts.NowEdge,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _rowName.Text = _field.Id;
        FlyoutPanelParts.Mono(_rowName);
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        nameLine.Children.Add(_rowName);
        if (_field.Name.Length > 0)
        {
            var name = new TextBlock
            {
                Text = $"· {_field.Name}",
                FontSize = 12,
                Foreground = SubBrush,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            FlyoutPanelParts.Mono(name);
            nameLine.Children.Add(name);
        }

        nameLine.Children.Add(
            new TextBlock
            {
                Text = "· destination",
                FontSize = 11.5,
                Foreground = SoloAdvisoryPanel.StrongBrush,
                VerticalAlignment = VerticalAlignment.Bottom,
            }
        );
        var column = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
            Children = { nameLine, _rowWhere },
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("14,*"),
            ColumnSpacing = 6,
            Margin = new Thickness(11, 3),
            Children = { mark, column },
        };
        Grid.SetColumn(column, 1);
        return new Border
        {
            Child = grid,
            MinHeight = AirborneTrafficRowButton.RowHeight,
            Background = SoloTrafficRowButton.PointedFill,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 2, 0, 2),
        };
    }

    /// <summary>While the values are still the pre-filled ones, sets the builder to the true clock and miles and writes the box.</summary>
    private void FollowTruth()
    {
        if (_owned || !TrueValues)
        {
            return;
        }

        FieldAdvisoryDetails truth = _field.TruthFrom(_aircraft);
        _clock = truth.Clock;
        _miles = truth.Miles;
        Compose();
    }

    /// <summary>
    /// Sets the builder to <paramref name="clock"/> and <paramref name="miles"/> from the dial or stepper; the values are the user's.
    /// </summary>
    private void Build(int? clock, int? miles)
    {
        _owned = true;
        _clock = clock;
        _miles = miles;
        Compose();
    }

    /// <summary>
    /// Writes the builder into the box, <c>{clock} {miles}</c>, up to the first part still unset (a miles set before the clock
    /// waits in the stepper).
    /// </summary>
    private void Compose()
    {
        string text = (_clock is { } clock) ? ((_miles is { } miles) ? $"{clock} {miles}" : $"{clock}") : "";
        if (text == (Box.Text ?? ""))
        {
            Refresh();
            return;
        }

        _composing = true;
        Box.Text = text;
        _composing = false;
        Refresh();
    }

    /// <summary>
    /// Typing makes the values the user's and moves the dial and the stepper to the parts typed; an emptied box clears both. A
    /// dial or stepper value set while the box lacks the part before it waits ahead of the box until the box is composed.
    /// </summary>
    private void OnTyped()
    {
        NoteText.IsVisible = false;
        if (_composing)
        {
            return;
        }

        _owned = true;
        (int? clock, int? miles, int words) = Reading();
        _clock = clock;
        _miles = (words > 1) ? miles : ((words == 0) ? null : _miles);
        Refresh();
    }

    /// <summary>Restates the title, the dial and stepper, the command, the ghost, the spoken line, the send line and the field row.</summary>
    private void Refresh()
    {
        _titleNow.Text = AirborneTrafficFlyout.TitleNow(_aircraft, _context.Callsign);
        (int? clock, int? miles, _) = Reading();
        string? command = CommandFor(clock, miles);
        ShowBox(command, Missing(clock, miles));
        ShowControls(command, clock, miles);
        RefreshRow();
    }

    /// <summary>The command column, the ghost and the spoken line for the box's <paramref name="command"/> or its first missing part.</summary>
    private void ShowBox(string? command, List<AdvisoryPart> missing)
    {
        BoxCommandText.Text = command ?? $"{FieldAdvisoryFlyout.Verb} {TakeoffFlyout.Unfilled}";
        BoxCommandText.Opacity = (command is null) ? 0.7 * MenuGlyphRowTemplate.DimmedOpacity : 0.7;
        MenuCommandText.SetCommand(Box, command);
        string ghost = (command is null) ? $"next: {SoloAdvisoryText.Gap(missing[0])}" : SoloAdvisoryModel.CompleteGhost;
        GhostText.Text = ghost;
        SoloAdvisoryPanel.ShowKeyed(_ghostView, ghost);
    }

    /// <summary>
    /// The dial, the stepper, the spoken line and the send line for the box's <paramref name="clock"/> and <paramref name="miles"/>.
    /// </summary>
    private void ShowControls(string? command, int? clock, int? miles)
    {
        Dial.Show(_clock?.ToString(CultureInfo.InvariantCulture), FieldCaption);
        Miles.Show(_miles);
        SpokenText.IsVisible = command is not null;
        SpokenText.Text =
            ((clock is { } c) && (miles is { } m)) ? $"“{CommandDescriber.FormatFieldAdvisoryPhrase(new FieldAdvisoryDetails(c, m))}”" : "";
        _sendCommand.Text = SendLineText(clock, miles);
        SendButton.IsEnabled = command is not null;
        SendButton.Opacity = (command is null) ? 0.4 : 1;
    }

    private void RefreshRow()
    {
        if (TrueValues)
        {
            FieldAdvisoryDetails truth = _field.TruthFrom(_aircraft);
            IReadOnlyList<string> runways = _field.RunwaysIn(_host);
            string runwayText = (runways.Count > 0) ? $" · runways {string.Join(' ', runways)}" : "";
            _rowWhere.Text = $"{truth.Clock} o'clock · {truth.Miles} nm{runwayText}";
            _rowWhere.FontStyle = FontStyle.Normal;
            _rowWhere.Foreground = _whereBrush;
        }
        else
        {
            _rowWhere.Text = SoloAdvisoryPanel.FindOnScope;
            _rowWhere.FontStyle = FontStyle.Italic;
            _rowWhere.Foreground = ScopeBrush;
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool inBox = ReferenceEquals(e.Source, Box) || Box.IsKeyboardFocusWithin;
        if ((e.Key == Key.Enter) && inBox)
        {
            e.Handled = true;
            await EnterAsync();
        }
    }

    /// <summary>
    /// Enter: an advisory missing a part says which and sends nothing; a complete one is sent, unless a send is already in
    /// flight, and the values become the user's.
    /// </summary>
    private async Task EnterAsync()
    {
        if (_sender.IsSending)
        {
            return;
        }

        (int? clock, int? miles, _) = Reading();
        if (CommandFor(clock, miles) is not { } command)
        {
            ShowNote(AdvisorySend.MissingNote(Missing(clock, miles)), null);
            return;
        }

        _owned = true;
        (bool answered, CommandResultDto? result) = await _sender.SendAsync(command);
        if (!answered)
        {
            return;
        }

        if (AdvisorySend.IsTaken(result))
        {
            _close();
        }
        else
        {
            ShowNote(AdvisorySend.NoteFor(result), SoloAdvisoryPanel.RefusalNoteBrush);
        }
    }

    private void ShowNote(string text, IBrush? brush)
    {
        NoteText.Text = text;
        NoteText.Foreground = brush ?? HintBrush;
        NoteText.IsVisible = true;
    }
}
