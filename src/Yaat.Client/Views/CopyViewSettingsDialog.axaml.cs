using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public enum CopySourceKind
{
    Scenario,
    WindowProfile,
}

/// <summary>
/// Immutable inputs the dialog needs to render the comparison. Built by MainWindow from the
/// live view models so the dialog itself stays decoupled from <c>MainViewModel</c>.
/// </summary>
public sealed class CopyViewSettingsContext
{
    public required UserPreferences Preferences { get; init; }
    public required string CurrentScenarioId { get; init; }
    public required string CurrentScenarioName { get; init; }
    public string? CurrentAirport { get; init; }
    public required SavedGroundSettings CurrentGround { get; init; }
    public required SavedRadarSettings CurrentRadar { get; init; }
    public required SavedWindowProfile CurrentLayout { get; init; }
    public required Func<int, string> ResolveMapName { get; init; }
}

/// <summary>
/// Modal picker that replaces the old "Copy View Settings From…" submenu. The user chooses a
/// source — another scenario (per-scenario Ground/Radar view settings) or a saved window profile
/// (window geometry, pop-out states, column layout) — sees a Current-vs-Source diff grouped into
/// sections, and checks the sections to copy. Results are read back via <see cref="Confirmed"/>,
/// <see cref="SourceKind"/>, <see cref="SourceId"/>, and <see cref="SelectedKeys"/>; the actual
/// apply happens in MainWindow, which has the view models and window orchestration.
/// </summary>
public partial class CopyViewSettingsDialog : Window
{
    private const double MismatchNmThreshold = 10.0;

    private static readonly IBrush DiffBrush = new SolidColorBrush(Color.Parse("#FF6FB7FF"));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.Parse("#FFE0A030"));
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#FF9AA0A6"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#FF888888"));

    private readonly CopyViewSettingsContext? _context;
    private readonly List<(string Key, CheckBox Check)> _rows = [];
    private bool _suppress;

    public bool Confirmed { get; private set; }
    public CopySourceKind SourceKind { get; private set; }
    public string? SourceId { get; private set; }
    public IReadOnlyList<string> SelectedKeys { get; private set; } = [];

    // Parameterless ctor required for the Avalonia designer / XamlLoader. Not used at runtime.
    public CopyViewSettingsDialog()
    {
        InitializeComponent();
    }

    public CopyViewSettingsDialog(CopyViewSettingsContext context)
    {
        InitializeComponent();
        _context = context;
        new WindowGeometryHelper(this, context.Preferences, "CopyViewSettings", 680, 620).Restore();

        HeaderText.Text = $"Copy into: {context.CurrentScenarioName}";

        var scenarios = context.Preferences.GetSavedViewScenarioIds();
        scenarios.RemoveAll(s => s.ScenarioId == context.CurrentScenarioId);
        var scenarioEntries = scenarios.Select(s => new ComboEntry(s.ScenarioId, s.DisplayName)).ToList();
        ScenarioCombo.ItemsSource = scenarioEntries;
        var hasScenarios = scenarioEntries.Count > 0;
        ScenarioRadio.IsEnabled = hasScenarios;
        ScenarioCombo.IsEnabled = hasScenarios;
        if (hasScenarios)
        {
            ScenarioCombo.SelectedIndex = 0;
        }

        var profileNames = context.Preferences.WindowProfiles.Select(p => p.Name).ToList();
        ProfileCombo.ItemsSource = profileNames;
        var hasProfiles = profileNames.Count > 0;
        ProfileRadio.IsEnabled = hasProfiles;
        ProfileCombo.IsEnabled = hasProfiles;
        if (hasProfiles)
        {
            ProfileCombo.SelectedIndex = 0;
        }

        ScenarioRadio.IsCheckedChanged += OnSourceRadioChanged;
        ProfileRadio.IsCheckedChanged += OnSourceRadioChanged;
        ScenarioCombo.SelectionChanged += OnScenarioComboChanged;
        ProfileCombo.SelectionChanged += OnProfileComboChanged;
        SelectAllButton.Click += (_, _) => SetAllChecks(true);
        SelectNoneButton.Click += (_, _) => SetAllChecks(false);
        CopyButton.Click += OnCopyClick;
        CancelButton.Click += (_, _) => Close();

        _suppress = true;
        if (hasScenarios)
        {
            ScenarioRadio.IsChecked = true;
        }
        else if (hasProfiles)
        {
            ProfileRadio.IsChecked = true;
        }

        _suppress = false;
        RebuildRows();
    }

    private void OnSourceRadioChanged(object? sender, RoutedEventArgs e)
    {
        if (!_suppress)
        {
            RebuildRows();
        }
    }

    private void OnScenarioComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }

        _suppress = true;
        ScenarioRadio.IsChecked = true;
        _suppress = false;
        RebuildRows();
    }

    private void OnProfileComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress)
        {
            return;
        }

        _suppress = true;
        ProfileRadio.IsChecked = true;
        _suppress = false;
        RebuildRows();
    }

    private void RebuildRows()
    {
        if (_context is null)
        {
            return;
        }

        RowsPanel.Children.Clear();
        _rows.Clear();
        SetWarning(null);

        if (ScenarioRadio.IsChecked == true && ScenarioCombo.SelectedItem is ComboEntry scenario)
        {
            BuildScenarioRows(scenario.Id);
        }
        else if (ProfileRadio.IsChecked == true && ProfileCombo.SelectedItem is string profileName)
        {
            BuildProfileRows(profileName);
        }
        else
        {
            AddInfo("Nothing to copy from yet. Save view settings in another scenario, or create a window profile first.");
        }
    }

    private void BuildScenarioRows(string sourceScenarioId)
    {
        var context = _context!;
        var prefs = context.Preferences;
        var srcGround = prefs.GetGroundSettings(sourceScenarioId);
        var srcRadar = prefs.GetRadarSettings(sourceScenarioId);
        var sourceAirport = prefs.GetScenarioAirport(sourceScenarioId);
        var mismatch = ComputeAirportMismatch(sourceAirport, srcGround, srcRadar);

        AddGroupHeader("Ground view");
        if (srcGround is null)
        {
            AddInfo("    (source scenario has no saved ground settings)");
        }
        else
        {
            foreach (var group in ViewSettingsCopyCatalog.GroundGroups)
            {
                var isPosition = group.Key == ViewSettingsCopyCatalog.GroundPositionKey;
                var current = group.Describe(context.CurrentGround);
                var source = group.Describe(srcGround);
                if (isPosition)
                {
                    current = WithAirport(context.CurrentAirport, current);
                    source = WithAirport(sourceAirport, source);
                }

                AddRow(
                    new RowSpec
                    {
                        Key = group.Key,
                        Label = group.Label,
                        CurrentText = current,
                        SourceText = source,
                        Differs = !group.AreEqual(context.CurrentGround, srcGround),
                        Warn = isPosition && mismatch,
                    }
                );
            }
        }

        AddGroupHeader("Radar view");
        if (srcRadar is null)
        {
            AddInfo("    (source scenario has no saved radar settings)");
        }
        else
        {
            foreach (var group in ViewSettingsCopyCatalog.RadarGroups)
            {
                var isCenter = group.Key == ViewSettingsCopyCatalog.RadarCenterKey;
                var current = group.Describe(context.CurrentRadar);
                var source = group.Describe(srcRadar);
                if (isCenter)
                {
                    current = WithAirport(context.CurrentAirport, current);
                    source = WithAirport(sourceAirport, source);
                }

                AddRow(
                    new RowSpec
                    {
                        Key = group.Key,
                        Label = group.Label,
                        CurrentText = current,
                        SourceText = source,
                        Differs = !group.AreEqual(context.CurrentRadar, srcRadar),
                        Warn = isCenter && mismatch,
                        Tooltip = group.Key == ViewSettingsCopyCatalog.RadarMapsKey ? BuildMapsTooltip(context.CurrentRadar, srcRadar) : null,
                    }
                );
            }
        }

        if (mismatch)
        {
            var current = context.CurrentAirport;
            SetWarning(
                (!string.IsNullOrEmpty(current) && !string.IsNullOrEmpty(sourceAirport))
                    ? $"⚠ Source is a different airport ({current} → {sourceAirport}). Copying map position / center will move your view to {sourceAirport}."
                    : "⚠ Source view is centered on a different location. Copying map position / center will move your view there."
            );
        }
    }

    private void BuildProfileRows(string profileName)
    {
        var context = _context!;
        var profile = context.Preferences.WindowProfiles.FirstOrDefault(p => p.Name == profileName);
        if (profile is null)
        {
            AddInfo("    (profile not found)");
            return;
        }

        var current = context.CurrentLayout;

        AddGroupHeader("Window geometry");
        var keys = profile.WindowGeometries.Keys.OrderBy(FriendlyWindowName, StringComparer.OrdinalIgnoreCase).ToList();
        if (keys.Count == 0)
        {
            AddInfo("    (profile captured no window geometry)");
        }

        foreach (var key in keys)
        {
            var sourceGeo = profile.WindowGeometries[key];
            current.WindowGeometries.TryGetValue(key, out var currentGeo);
            AddRow(
                new RowSpec
                {
                    Key = "geo:" + key,
                    Label = FriendlyWindowName(key),
                    CurrentText = FormatGeo(currentGeo),
                    SourceText = FormatGeo(sourceGeo),
                    Differs = !GeoEqual(currentGeo, sourceGeo),
                }
            );
        }

        AddGroupHeader("Layout");
        AddRow(
            new RowSpec
            {
                Key = "popouts",
                Label = "Pop-out / dock states",
                CurrentText = FormatPopouts(current),
                SourceText = FormatPopouts(profile),
                Differs = PopoutsDiffer(current, profile),
            }
        );
        AddRow(
            new RowSpec
            {
                Key = "columns",
                Label = "Aircraft-list column layout",
                CurrentText = FormatGrid(current.DataGridLayout),
                SourceText = FormatGrid(profile.DataGridLayout),
                Differs = !GridEqual(current.DataGridLayout, profile.DataGridLayout),
            }
        );
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.Check.IsChecked == true).Select(r => r.Key).ToList();
        if (selected.Count == 0)
        {
            SetWarning("Select at least one item to copy.");
            return;
        }

        if (ScenarioRadio.IsChecked == true && ScenarioCombo.SelectedItem is ComboEntry scenario)
        {
            SourceKind = CopySourceKind.Scenario;
            SourceId = scenario.Id;
        }
        else if (ProfileRadio.IsChecked == true && ProfileCombo.SelectedItem is string profileName)
        {
            SourceKind = CopySourceKind.WindowProfile;
            SourceId = profileName;
        }
        else
        {
            SetWarning("Choose a source first.");
            return;
        }

        SelectedKeys = selected;
        Confirmed = true;
        Close();
    }

    private bool ComputeAirportMismatch(string? sourceAirport, SavedGroundSettings? srcGround, SavedRadarSettings? srcRadar)
    {
        var context = _context!;
        var currentAirport = context.CurrentAirport;
        if (!string.IsNullOrEmpty(currentAirport) && !string.IsNullOrEmpty(sourceAirport))
        {
            return !string.Equals(currentAirport, sourceAirport, StringComparison.OrdinalIgnoreCase);
        }

        if (srcGround is not null)
        {
            return NmBetween(context.CurrentGround.CenterLat, context.CurrentGround.CenterLon, srcGround.CenterLat, srcGround.CenterLon)
                > MismatchNmThreshold;
        }

        if (srcRadar is not null)
        {
            return NmBetween(context.CurrentRadar.CenterLat, context.CurrentRadar.CenterLon, srcRadar.CenterLat, srcRadar.CenterLon)
                > MismatchNmThreshold;
        }

        return false;
    }

    private string BuildMapsTooltip(SavedRadarSettings current, SavedRadarSettings source)
    {
        var context = _context!;
        string Names(SavedRadarSettings s) =>
            s.EnabledStarsIds.Count == 0 ? "(none)" : string.Join(", ", s.EnabledStarsIds.Select(context.ResolveMapName));
        return $"Current: {Names(current)}\nSource: {Names(source)}";
    }

    private void AddGroupHeader(string title)
    {
        RowsPanel.Children.Add(
            new TextBlock
            {
                Text = title,
                FontWeight = FontWeight.SemiBold,
                Foreground = HeaderBrush,
                Margin = new Avalonia.Thickness(0, 8, 0, 2),
            }
        );
    }

    private void AddInfo(string text)
    {
        RowsPanel.Children.Add(
            new TextBlock
            {
                Text = text,
                Foreground = InfoBrush,
                FontStyle = FontStyle.Italic,
            }
        );
    }

    private void AddRow(RowSpec spec)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,130,130,24") };

        var check = new CheckBox
        {
            Content = spec.Label,
            IsChecked = spec.Enabled && spec.Differs,
            IsEnabled = spec.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (spec.Tooltip is not null)
        {
            ToolTip.SetTip(check, spec.Tooltip);
        }

        Grid.SetColumn(check, 0);

        var current = new TextBlock
        {
            Text = spec.CurrentText,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(current, spec.CurrentText);
        Grid.SetColumn(current, 1);

        var source = new TextBlock
        {
            Text = spec.SourceText,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = spec.Differs ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = spec.Differs ? DiffBrush : Brushes.Gray,
        };
        ToolTip.SetTip(source, spec.SourceText);
        Grid.SetColumn(source, 2);

        var marker = new TextBlock
        {
            Text = spec.Warn ? "⚠" : (spec.Differs ? "◀" : ""),
            Foreground = spec.Warn ? WarnBrush : DiffBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(marker, 3);

        grid.Children.Add(check);
        grid.Children.Add(current);
        grid.Children.Add(source);
        grid.Children.Add(marker);
        RowsPanel.Children.Add(grid);
        _rows.Add((spec.Key, check));
    }

    private void SetAllChecks(bool value)
    {
        foreach (var (_, check) in _rows)
        {
            if (check.IsEnabled)
            {
                check.IsChecked = value;
            }
        }
    }

    private void SetWarning(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            WarningText.IsVisible = false;
            return;
        }

        WarningText.Text = message;
        WarningText.IsVisible = true;
    }

    private static string WithAirport(string? airport, string text) => string.IsNullOrEmpty(airport) ? text : $"{airport} · {text}";

    private static double NmBetween(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusNm = 3440.065;
        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2)) + (Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return earthRadiusNm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double degrees) => degrees * Math.PI / 180.0;

    private static string FriendlyWindowName(string key)
    {
        if (key.StartsWith("VStripsView:", StringComparison.Ordinal))
        {
            return $"Flight Strips ({key["VStripsView:".Length..]})";
        }

        if (key.StartsWith("VTdlsView:", StringComparison.Ordinal))
        {
            return $"vTDLS ({key["VTdlsView:".Length..]})";
        }

        return key switch
        {
            "Main" => "Main window",
            "Terminal" => "Terminal window",
            "DataGrid" => "Aircraft List window",
            "GroundView" => "Ground View window",
            "RadarView" => "Radar View window",
            "Settings" => "Settings window",
            "Metar" => "METAR window",
            "Controllers" => "Controllers window",
            "FavoritesPanel" => "Favorites panel",
            "VStripsView" => "Flight Strips",
            "VTdlsView" => "vTDLS",
            _ => key,
        };
    }

    private static string FormatGeo(SavedWindowGeometry? geo)
    {
        if (geo is null)
        {
            return "—";
        }

        return geo.IsMaximized ? "maximized" : $"{(int)geo.Width}×{(int)geo.Height}";
    }

    private static bool GeoEqual(SavedWindowGeometry? a, SavedWindowGeometry? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.X == b.X
            && a.Y == b.Y
            && Math.Abs(a.Width - b.Width) < 0.5
            && Math.Abs(a.Height - b.Height) < 0.5
            && a.IsMaximized == b.IsMaximized
            && a.ScreenIndex == b.ScreenIndex
            && a.IsTopmost == b.IsTopmost;
    }

    private static string FormatPopouts(SavedWindowProfile p) =>
        $"Term:{(p.IsTerminalDocked ? "dock" : "float")} AC:{Pop(p.IsDataGridPoppedOut)} Gnd:{Pop(p.IsGroundViewPoppedOut)} Rdr:{Pop(p.IsRadarViewPoppedOut)}";

    private static string Pop(bool poppedOut) => poppedOut ? "pop" : "dock";

    private static bool PopoutsDiffer(SavedWindowProfile a, SavedWindowProfile b) =>
        a.IsTerminalDocked != b.IsTerminalDocked
        || a.IsDataGridPoppedOut != b.IsDataGridPoppedOut
        || a.IsGroundViewPoppedOut != b.IsGroundViewPoppedOut
        || a.IsRadarViewPoppedOut != b.IsRadarViewPoppedOut;

    private static string FormatGrid(SavedGridLayout? layout)
    {
        if (layout is null)
        {
            return "default";
        }

        var columns = layout.ColumnOrder?.Count ?? 0;
        var hidden = layout.HiddenColumns?.Count ?? 0;
        return columns == 0 && hidden == 0 ? "custom" : $"{columns} cols, {hidden} hidden";
    }

    private static bool GridEqual(SavedGridLayout? a, SavedGridLayout? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return SequenceEqual(a.ColumnOrder, b.ColumnOrder)
            && SequenceEqual(a.HiddenColumns, b.HiddenColumns)
            && a.SortColumn == b.SortColumn
            && a.SortDirection == b.SortDirection
            && WidthsEqual(a.ColumnWidths, b.ColumnWidths);
    }

    private static bool SequenceEqual(List<string>? a, List<string>? b)
    {
        var listA = a ?? [];
        var listB = b ?? [];
        return listA.SequenceEqual(listB);
    }

    private static bool WidthsEqual(Dictionary<string, double>? a, Dictionary<string, double>? b)
    {
        var countA = a?.Count ?? 0;
        var countB = b?.Count ?? 0;
        if (countA != countB)
        {
            return false;
        }

        if (a is null || b is null)
        {
            return true;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || Math.Abs(other - value) > 0.5)
            {
                return false;
            }
        }

        return true;
    }

    private sealed record ComboEntry(string Id, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed class RowSpec
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required string CurrentText { get; init; }
        public required string SourceText { get; init; }
        public bool Differs { get; init; }
        public bool Enabled { get; init; } = true;
        public bool Warn { get; init; }
        public string? Tooltip { get; init; }
    }
}
