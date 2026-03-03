using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public partial class LoadScenarioWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<LoadScenarioWindow>();

    // Matches "S1-", "C1-", "OI-", etc. at the start of a scenario name to infer a rating prefix.
    private static readonly Regex RatingPrefixRegex = new(@"^([A-Z]+\d+)-", RegexOptions.Compiled);

    private readonly UserPreferences _preferences;
    private readonly TextBox _folderPathBox;
    private readonly ComboBox _airportFilter;
    private readonly ComboBox _ratingFilter;
    private readonly TextBlock _statusText;
    private readonly ListBox _scenarioList;
    private readonly Button _loadButton;
    private List<ScenarioItem> _allItems = [];

    public LoadScenarioWindow()
        : this(new UserPreferences()) { }

    public LoadScenarioWindow(UserPreferences preferences)
    {
        _preferences = preferences;
        InitializeComponent();

        _folderPathBox = this.FindControl<TextBox>("FolderPathBox")!;
        _airportFilter = this.FindControl<ComboBox>("AirportFilter")!;
        _ratingFilter = this.FindControl<ComboBox>("RatingFilter")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _scenarioList = this.FindControl<ListBox>("ScenarioList")!;
        _loadButton = this.FindControl<Button>("LoadButton")!;

        this.FindControl<Button>("BrowseButton")!.Click += OnBrowseClick;
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        _loadButton.Click += OnLoadClick;
        _scenarioList.SelectionChanged += OnSelectionChanged;
        _scenarioList.DoubleTapped += OnDoubleTapped;
        _airportFilter.SelectionChanged += (_, _) => ApplyFilter();
        _ratingFilter.SelectionChanged += (_, _) => ApplyFilter();

        var lastFolder = preferences.LastScenarioFolder;
        if (lastFolder is not null && Directory.Exists(lastFolder))
        {
            ScanFolder(lastFolder);
        }
    }

    private async void OnBrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select Scenario Folder", AllowMultiple = false }
        );

        if (folders.Count > 0)
        {
            var path = folders[0].TryGetLocalPath();
            if (path is not null)
            {
                ScanFolder(path);
            }
        }
    }

    private void ScanFolder(string folder)
    {
        _folderPathBox.Text = folder;
        _preferences.SetLastScenarioFolder(folder);

        var items = new List<ScenarioItem>();
        foreach (var filePath in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(filePath) : name;

                var airport = "Unknown";
                if (root.TryGetProperty("primaryAirportId", out var airportProp))
                {
                    var raw = airportProp.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        airport = raw;
                    }
                }

                var rating = "Unknown";
                if (root.TryGetProperty("minimumRating", out var ratingProp))
                {
                    var raw = ratingProp.GetString()?.Trim();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        rating = raw;
                    }
                }

                if (rating == "Unknown" && name is not null)
                {
                    var match = RatingPrefixRegex.Match(name);
                    if (match.Success)
                    {
                        rating = match.Groups[1].Value;
                    }
                }

                items.Add(new ScenarioItem(filePath, name!, airport, rating));
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Skipped unreadable scenario file: {File}", filePath);
            }
        }

        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _allItems = items;

        RebuildFilters();

        _statusText.Text = _allItems.Count > 0 ? $"{_allItems.Count} scenarios" : "No scenarios found.";
        ApplyFilter();
    }

    private void RebuildFilters()
    {
        var airports = _allItems.Select(i => i.Airport).Distinct().OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        var ratings = _allItems.Select(i => i.Rating).Distinct().OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();

        _airportFilter.ItemsSource = airports.Prepend("All").ToList();
        _airportFilter.SelectedIndex = 0;

        _ratingFilter.ItemsSource = ratings.Prepend("All").ToList();
        _ratingFilter.SelectedIndex = 0;
    }

    private void ApplyFilter()
    {
        var airportSel = _airportFilter.SelectedItem as string;
        var ratingSel = _ratingFilter.SelectedItem as string;

        var filtered = _allItems
            .Where(i => airportSel is null or "All" || i.Airport == airportSel)
            .Where(i => ratingSel is null or "All" || i.Rating == ratingSel)
            .ToList();

        _scenarioList.ItemsSource = filtered;
        _scenarioList.SelectedItem = null;
        _loadButton.IsEnabled = false;

        if (_allItems.Count > 0 && filtered.Count == 0)
        {
            _statusText.Text = "No scenarios match the filter.";
        }
        else if (_allItems.Count > 0)
        {
            _statusText.Text = $"{_allItems.Count} scenarios";
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _loadButton.IsEnabled = _scenarioList.SelectedItem is ScenarioItem;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_scenarioList.SelectedItem is ScenarioItem item)
        {
            Close(item.FilePath);
        }
    }

    private void OnLoadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_scenarioList.SelectedItem is ScenarioItem item)
        {
            Close(item.FilePath);
        }
    }
}

internal sealed record ScenarioItem(string FilePath, string Name, string Airport, string Rating);
