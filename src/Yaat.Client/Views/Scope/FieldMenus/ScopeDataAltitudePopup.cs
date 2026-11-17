using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;

namespace Yaat.Client.Views.Scope.FieldMenus;

/// <summary>
/// The popup a click on an ERAM block's altitude opens at the pointer: one panel with two columns side by side. SCOPE DATA
/// holds one altitude box (hundreds of feet, 1-999) feeding every row that takes an altitude, then the altitude rows and the
/// clear rows, each showing the ERAM entry it runs (<see cref="ScopeDataEntries"/>); the altitude rows are disabled while the
/// box holds no altitude. PILOT holds the Maintain altitude panel the catalog's entry builds
/// (<see cref="AltitudeFlyout.MaintainPanelFactory"/>), sending through the host. Shift is recorded at each pointer press,
/// release and key press inside the popup, before the row under it runs; Esc closes it, as does a click outside it, which
/// still reaches the Scope.
/// </summary>
internal sealed class ScopeDataAltitudePopup
{
    /// <summary>The scope-data column's head.</summary>
    public const string ScopeDataHead = "SCOPE DATA";

    /// <summary>The pilot column's head.</summary>
    public const string PilotHead = "PILOT";

    /// <summary>What the empty altitude box reads.</summary>
    public const string BoxPlaceholder = "altitude, e.g. 180";

    /// <summary>What an altitude row's entry shows in its digits' place while the box holds no altitude.</summary>
    public const string Elided = "···";

    private const double PanelWidth = 640;
    private const string RowClass = "scope-data-row";

    private static readonly IImmutableSolidColorBrush PanelFill = new ImmutableSolidColorBrush(Color.Parse("#2B2B2B"));
    private static readonly IImmutableSolidColorBrush PanelEdge = new ImmutableSolidColorBrush(Color.Parse("#464646"));
    private static readonly IImmutableSolidColorBrush RuleBrush = new ImmutableSolidColorBrush(Color.Parse("#3D3D3D"));
    private static readonly IImmutableSolidColorBrush EramBrush = new ImmutableSolidColorBrush(Color.Parse("#58C4E6"));
    private static readonly IImmutableSolidColorBrush PilotBrush = new ImmutableSolidColorBrush(Color.Parse("#E0B84A"));
    private static readonly IImmutableSolidColorBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#E8E8E8"));
    private static readonly IImmutableSolidColorBrush EntryBrush = new ImmutableSolidColorBrush(Color.Parse("#9AA3AD"));
    private static readonly IImmutableSolidColorBrush FadedBrush = new ImmutableSolidColorBrush(Color.Parse("#5D636B"));
    private static readonly IImmutableSolidColorBrush HoverFill = new ImmutableSolidColorBrush(Color.Parse("#1FFFFFFF"));
    private static readonly IImmutableSolidColorBrush PressedFill = new ImmutableSolidColorBrush(Color.Parse("#2EFFFFFF"));

    private readonly string _flid;
    private readonly Action<string> _pick;
    private readonly List<Row> _rows = [];
    private readonly FlyoutLiveTick? _tick;

    /// <param name="aircraft">The clicked track, whose callsign every entry names last.</param>
    /// <param name="host">The host the PILOT panel sends through and reads the field elevation, the MVA and the clock from.</param>
    /// <param name="recordShift">Told whether Shift is held at each press, release and key press inside the popup.</param>
    /// <param name="pick">Runs a SCOPE DATA row's entry, or stages it while Shift is held.</param>
    public ScopeDataAltitudePopup(AircraftModel aircraft, IMenuHost host, Action<bool> recordShift, Action<string> pick)
    {
        _flid = aircraft.Callsign;
        _pick = pick;
        AltitudeBox = new TextBox
        {
            PlaceholderText = BoxPlaceholder,
            Width = 150,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AltitudeBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Present();
            }
        };

        string initials = host.Session.Initials;
        PilotPanel = AltitudeFlyout.MaintainPanelFactory(aircraft, host)?.Invoke(command => ChooseAsync(host, command, initials));
        if (PilotPanel is IFlyoutPanel panel)
        {
            _tick = new FlyoutLiveTick(host.Clock, panel.Refresh);
        }

        Control content = Panel(ScopeDataColumn(), PilotColumn());
        ScopeFieldMenus.RecordShift(content, recordShift);
        content.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Popup = new Popup
        {
            Placement = PlacementMode.Pointer,
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = true,
            Child = content,
        };
        Popup.Opened += (_, _) => OnOpened();
        Popup.Closed += (_, _) => _tick?.Stop();
        Present();
    }

    /// <summary>The popup itself.</summary>
    public Popup Popup { get; }

    /// <summary>The SCOPE DATA altitude box, in hundreds of feet.</summary>
    public TextBox AltitudeBox { get; }

    /// <summary>The PILOT column's Maintain altitude panel; null when no altitude is listed for the aircraft.</summary>
    public MaintainAltitudePanel? PilotPanel { get; }

    /// <summary>Whether the popup is open.</summary>
    public bool IsOpen => Popup.IsOpen;

    /// <summary>Fired when the popup closes, by a pick, Esc, a click outside it or <see cref="Close"/>.</summary>
    public event EventHandler<EventArgs>? Closed
    {
        add => Popup.Closed += value;
        remove => Popup.Closed -= value;
    }

    /// <summary>The SCOPE DATA row labelled <paramref name="label"/>.</summary>
    public Button RowFor(string label) =>
        _rows.Find(r => r.Label == label)?.Button ?? throw new ArgumentOutOfRangeException(nameof(label), label, "No row.");

    /// <summary>The entry text the SCOPE DATA row labelled <paramref name="label"/> shows.</summary>
    public string EntryShownFor(string label) =>
        _rows.Find(r => r.Label == label)?.Entry.Text ?? throw new ArgumentOutOfRangeException(nameof(label), label, "No row.");

    /// <summary>Opens the popup at the pointer over <paramref name="anchor"/>, in its overlay layer.</summary>
    /// <returns>False when the anchor has no overlay layer.</returns>
    public bool Open(Control anchor)
    {
        if (OverlayLayer.GetOverlayLayer(anchor) is not { } overlay)
        {
            return false;
        }

        Popup.PlacementTarget = anchor;
        overlay.Children.Add(Popup);
        Popup.Closed += (_, _) => overlay.Children.Remove(Popup);
        Popup.IsOpen = true;
        return true;
    }

    /// <summary>Closes the popup.</summary>
    public void Close() => Popup.IsOpen = false;

    // The box's text as hundreds of feet, 1-999; null for anything else.
    private int? Hundreds() =>
        (
            (int.TryParse((AltitudeBox.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int hundreds))
            && (hundreds is >= 1 and <= 999)
        )
            ? hundreds
            : null;

    // An altitude row: its entry for the box's altitude, none while the box holds none, when it shows the entry for 001 with
    // that first 001 (the altitude, ahead of the flight ID) elided.
    private Row AltitudeRow(string label, Func<int, string, string> entry)
    {
        string sample = entry(1, _flid);
        int at = sample.IndexOf("001", StringComparison.Ordinal);
        string elided = string.Concat(sample.AsSpan(0, at), Elided, sample.AsSpan(at + 3));
        return new Row(label, hundreds => (hundreds is { } h) ? entry(h, _flid) : null, elided);
    }

    // A clear row: its entry from the flight ID alone, whatever the box holds.
    private Row ClearRow(string label, Func<string, string> entry)
    {
        string text = entry(_flid);
        return new Row(label, _ => text, text);
    }

    private void Present()
    {
        int? hundreds = Hundreds();
        foreach (Row row in _rows)
        {
            string? entry = row.EntryFor(hundreds);
            row.Button.IsEnabled = entry is not null;
            row.Entry.Text = entry ?? row.Elided;
            row.Entry.Foreground = (entry is null) ? FadedBrush : EntryBrush;
            row.Name.Foreground = (entry is null) ? FadedBrush : LabelBrush;
        }
    }

    private void OnRowClick(Row row)
    {
        if (row.EntryFor(Hundreds()) is { } entry)
        {
            _pick(entry);
        }
    }

    // The PILOT panel resets, follows the aircraft and centres its list on the selected altitude; only the SCOPE DATA box takes
    // the focus.
    private void OnOpened()
    {
        if (PilotPanel is IFlyoutPanel panel)
        {
            panel.Reset();
            panel.Refresh();
            _tick?.Start();
        }

        FlyoutPanelParts.FocusLater(AltitudeBox, () => Popup.IsOpen, () => PilotPanel?.CentreSelected());
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    // A PILOT choice: sends (or the host stages it) and closes the popup.
    private async Task ChooseAsync(IMenuHost host, string command, string initials)
    {
        Task sending = host.SendAsync(_flid, command, initials);
        Close();
        await sending;
    }

    // The rows' looks on the row's content presenter: the hover and pressed fills, and no fill while disabled.
    private static Style RowStyle(string state, IBrush fill) =>
        new(x => x.OfType<Button>().Class(RowClass).Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, fill) },
        };

    // The panel: two equal columns split by a one-pixel rule, the rows' looks styled here.
    private static Border Panel(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        grid.Children.Add(left);
        var rule = new Border
        {
            Width = 1,
            Background = RuleBrush,
            Margin = new Thickness(0, 8),
        };
        Grid.SetColumn(rule, 1);
        grid.Children.Add(rule);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        var panel = new Border
        {
            Width = PanelWidth,
            Background = PanelFill,
            BorderBrush = PanelEdge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(0, 4),
            BoxShadow = BoxShadows.Parse("0 8 18 0 #8C000000"),
            Child = grid,
        };
        panel.Styles.Add(RowStyle(":pointerover", HoverFill));
        panel.Styles.Add(RowStyle(":pressed", PressedFill));
        panel.Styles.Add(RowStyle(":disabled", Brushes.Transparent));
        return panel;
    }

    // A column's head: its kind in its colour, bold and monospace, left; its hint right; a rule under both.
    private static Border ColumnHead(string kind, IImmutableSolidColorBrush brush, string hint)
    {
        var label = new TextBlock
        {
            Text = kind,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            LetterSpacing = 0.5,
            Foreground = brush,
        };
        FlyoutPanelParts.Mono(label);
        var note = new TextBlock
        {
            Text = hint,
            FontSize = 11,
            Foreground = FlyoutPanelParts.TitleBrush,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(label);
        Grid.SetColumn(note, 1);
        grid.Children.Add(note);
        return new Border
        {
            BorderBrush = RuleBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(4),
            Padding = new Thickness(7, 2, 7, 4),
            Child = grid,
        };
    }

    private StackPanel ScopeDataColumn()
    {
        var column = new StackPanel { Margin = new Thickness(0, 4) };
        column.Children.Add(ColumnHead(ScopeDataHead, EramBrush, "runs an ERAM entry"));
        column.Children.Add(
            FlyoutPanelParts.TitleLine(
                new TextBlock
                {
                    Text = "Scope data",
                    FontSize = 11,
                    Foreground = FlyoutPanelParts.TitleBrush,
                },
                new TextBlock()
            )
        );
        column.Children.Add(BoxRow());
        column.Children.Add(FlyoutPanelParts.SeparatorLine());
        AddRow(column, AltitudeRow("FP altitude", ScopeDataEntries.FpAltitude));
        AddRow(column, AltitudeRow("Interim", ScopeDataEntries.Interim));
        AddRow(column, AltitudeRow("Local interim", ScopeDataEntries.Local));
        AddRow(column, AltitudeRow("Procedure", ScopeDataEntries.Procedure));
        AddRow(column, AltitudeRow("Interim + CERA", ScopeDataEntries.InterimWithCera));
        AddRow(column, AltitudeRow("CERA only", ScopeDataEntries.CeraOnly));
        column.Children.Add(FlyoutPanelParts.SeparatorLine());
        AddRow(column, ClearRow("Clear interim / procedure", ScopeDataEntries.ClearInterimOrProcedure));
        AddRow(column, ClearRow("Clear local", ScopeDataEntries.ClearLocal));
        AddRow(column, ClearRow("Clear CERA", ScopeDataEntries.ClearCera));
        return column;
    }

    // The ERAM kind chip and the altitude box.
    private StackPanel BoxRow()
    {
        var kind = new TextBlock
        {
            Text = "ERAM",
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = EramBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        FlyoutPanelParts.Mono(kind);
        var chip = new Border
        {
            Width = 40,
            Height = 22,
            BorderBrush = EramBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = kind,
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Height = 30,
            Margin = new Thickness(11, 4),
            Children = { chip, AltitudeBox },
        };
    }

    private void AddRow(StackPanel column, Row row)
    {
        row.Button.Click += (_, _) => OnRowClick(row);
        _rows.Add(row);
        column.Children.Add(row.Button);
    }

    private StackPanel PilotColumn()
    {
        var column = new StackPanel { Margin = new Thickness(0, 4) };
        column.Children.Add(ColumnHead(PilotHead, PilotBrush, "sends a pilot command"));
        if (PilotPanel is not null)
        {
            column.Children.Add(PilotPanel);
        }

        return column;
    }

    // A SCOPE DATA row: its label left and the entry it runs right, in the monospace font. Its entry comes from the box's
    // altitude (null: the row is disabled and shows its elided text).
    private sealed class Row
    {
        public Row(string label, Func<int?, string?> entryFor, string elided)
        {
            Label = label;
            EntryFor = entryFor;
            Elided = elided;
            Entry = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            FlyoutPanelParts.Mono(Entry);
            Name = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = LabelBrush,
                Padding = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(Name);
            Grid.SetColumn(Entry, 1);
            grid.Children.Add(Entry);
            Button = new Button
            {
                Content = grid,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(3),
                Height = 28,
                Padding = new Thickness(8, 0),
                Margin = new Thickness(4, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            Button.Classes.Add(RowClass);
            AutomationProperties.SetName(Button, label);
        }

        public string Label { get; }

        public Func<int?, string?> EntryFor { get; }

        public string Elided { get; }

        public TextBlock Name { get; }

        public TextBlock Entry { get; }

        public Button Button { get; }
    }
}
