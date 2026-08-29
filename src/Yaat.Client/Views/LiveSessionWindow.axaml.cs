using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Picker for a live-traffic session: the ARTCC's facility tree on the left, the selected facility's positions on the
/// right, the ground-view airport (defaulted from the position's facility) and the altitude ceiling below. Returns a
/// <see cref="LiveSessionChoice"/> or null; the last choice is pre-selected.
/// </summary>
public partial class LiveSessionWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<LiveSessionWindow>();

    private readonly UserPreferences _preferences;
    private readonly ServerConnection? _connection;
    private readonly TreeView _facilityTree;
    private readonly ListBox _positionList;
    private readonly ComboBox _airportBox;
    private readonly NumericUpDown _ceilingBox;
    private readonly TextBox _startAtBox;
    private readonly TextBlock _startAtHint;
    private readonly Button _startButton;
    private FacilityTreeDto? _root;

    public LiveSessionWindow()
        : this(new UserPreferences(), null) { }

    public LiveSessionWindow(UserPreferences preferences, ServerConnection? connection)
    {
        _preferences = preferences;
        _connection = connection;
        InitializeComponent();
        new WindowGeometryHelper(this, preferences, "LiveSession", 620, 520).Restore();

        _facilityTree = this.FindControl<TreeView>("FacilityTree")!;
        _positionList = this.FindControl<ListBox>("PositionList")!;
        _airportBox = this.FindControl<ComboBox>("AirportBox")!;
        _ceilingBox = this.FindControl<NumericUpDown>("CeilingBox")!;
        _startAtBox = this.FindControl<TextBox>("StartAtBox")!;
        _startAtHint = this.FindControl<TextBlock>("StartAtHint")!;
        _startButton = this.FindControl<Button>("StartButton")!;
        _startAtBox.TextChanged += (_, _) => UpdateStartEnabled();

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        _startButton.Click += (_, _) => Finish();
        _facilityTree.SelectionChanged += (_, _) => OnFacilitySelected();
        _positionList.SelectionChanged += (_, _) => OnPositionSelected();
        _positionList.DoubleTapped += (_, _) => Finish();
        _airportBox.SelectionChanged += (_, _) => UpdateStartEnabled();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
            }
        };

        _ceilingBox.Value = preferences.LastLiveSession?.CeilingFt ?? 0;
        _ = LoadTreeAsync();
    }

    private async Task LoadTreeAsync()
    {
        var artccId = _preferences.ArtccId;
        if (_connection is null || string.IsNullOrWhiteSpace(artccId))
        {
            _facilityTree.ItemsSource = new[]
            {
                new TreeViewItem { Header = "Not connected.", IsEnabled = false },
            };
            return;
        }

        try
        {
            _root = await _connection.GetArtccFacilityTreeAsync(artccId);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to fetch the facility tree for {Artcc}", artccId);
        }

        if (_root is null)
        {
            _facilityTree.ItemsSource = new[]
            {
                new TreeViewItem { Header = $"No facility data for {artccId}.", IsEnabled = false },
            };
            return;
        }

        var rootItem = BuildItem(_root);
        _facilityTree.ItemsSource = new[] { rootItem };
        rootItem.IsExpanded = true;
        PreselectLastChoice(rootItem);
    }

    private static TreeViewItem BuildItem(FacilityTreeDto facility)
    {
        var item = new TreeViewItem { Header = $"{facility.Id} — {facility.Name}", Tag = facility };
        foreach (var child in facility.Children)
        {
            item.Items.Add(BuildItem(child));
        }

        return item;
    }

    private void PreselectLastChoice(TreeViewItem rootItem)
    {
        var last = _preferences.LastLiveSession;
        if (last is null || _root is null)
        {
            _facilityTree.SelectedItem = rootItem;
            return;
        }

        var facility = LiveSessionAirportDefaults.FindFacilityOfPosition(_root, last.PositionId);
        if (facility is null || FindItem(rootItem, facility) is not { } item)
        {
            _facilityTree.SelectedItem = rootItem;
            return;
        }

        ExpandTo(rootItem, item);
        _facilityTree.SelectedItem = item;
        _positionList.SelectedItem = _positionList.Items.OfType<PositionEntry>().FirstOrDefault(p => p.Position.Id == last.PositionId);
        if (_airportBox.Items.Contains(last.AirportId))
        {
            _airportBox.SelectedItem = last.AirportId;
        }
    }

    private static TreeViewItem? FindItem(TreeViewItem item, FacilityTreeDto facility)
    {
        if (ReferenceEquals(item.Tag, facility))
        {
            return item;
        }

        foreach (var child in item.Items.OfType<TreeViewItem>())
        {
            if (FindItem(child, facility) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool ExpandTo(TreeViewItem item, TreeViewItem target)
    {
        if (ReferenceEquals(item, target))
        {
            return true;
        }

        foreach (var child in item.Items.OfType<TreeViewItem>())
        {
            if (ExpandTo(child, target))
            {
                item.IsExpanded = true;
                return true;
            }
        }

        return false;
    }

    private void OnFacilitySelected()
    {
        var facility = (_facilityTree.SelectedItem as TreeViewItem)?.Tag as FacilityTreeDto;
        _positionList.ItemsSource = facility is null
            ? null
            : facility.Positions.OrderByDescending(p => p.Starred).ThenBy(p => p.Callsign).Select(p => new PositionEntry(p)).ToList();
        _positionList.SelectedItem = null;
        _airportBox.ItemsSource = null;
        UpdateStartEnabled();
    }

    private void OnPositionSelected()
    {
        if (_root is null || _positionList.SelectedItem is not PositionEntry entry)
        {
            _airportBox.ItemsSource = null;
            UpdateStartEnabled();
            return;
        }

        var choice = LiveSessionAirportDefaults.Resolve(_root, entry.Position.Id);
        _airportBox.ItemsSource = choice.Airports;
        _airportBox.SelectedItem = choice.Default;
        UpdateStartEnabled();
    }

    private void UpdateStartEnabled()
    {
        var startAt = ParseStartAt(_startAtBox.Text, DateTimeOffset.UtcNow, out var error);
        _startAtHint.Text = error ?? (startAt is { } t ? $"= {t:yyyy-MM-dd HH:mm}Z" : "");
        _startButton.IsEnabled = (_positionList.SelectedItem is PositionEntry) && (_airportBox.SelectedItem is string) && (error is null);
    }

    /// <summary>
    /// "HH:mm" (or "HH:mm:ss") as the most recent such UTC instant not after now — yesterday's when the time of day has not
    /// come yet today. Blank means now (null); anything else is an error.
    /// </summary>
    public static DateTimeOffset? ParseStartAt(string? text, DateTimeOffset now, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (
            !TimeSpan.TryParseExact(
                text.Trim(),
                [@"h\:mm", @"hh\:mm", @"h\:mm\:ss", @"hh\:mm\:ss"],
                System.Globalization.CultureInfo.InvariantCulture,
                out var timeOfDay
            )
        )
        {
            error = "Use HH:mm (UTC)";
            return null;
        }

        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero) + timeOfDay;
        if (candidate > now)
        {
            candidate = candidate.AddDays(-1);
        }

        return candidate;
    }

    private void Finish()
    {
        if (_positionList.SelectedItem is not PositionEntry entry || _airportBox.SelectedItem is not string airport)
        {
            return;
        }

        Close(
            new LiveSessionChoice
            {
                PositionId = entry.Position.Id,
                PositionLabel = entry.Position.Callsign,
                AirportId = airport,
                CeilingFt = (int)(_ceilingBox.Value ?? 0),
                StartUtc = ParseStartAt(_startAtBox.Text, DateTimeOffset.UtcNow, out _),
            }
        );
    }

    private sealed record PositionEntry(PositionSummaryDto Position)
    {
        public override string ToString() => $"{(Position.Starred ? "★ " : "")}{Position.Callsign} — {Position.Name}";
    }
}
