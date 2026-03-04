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

    // Matches "S1-OAK", "C1-SFO", etc. at the start of a scenario name to infer rating + facility.
    private static readonly Regex NamePrefixRegex = new(@"^([A-Z]+\d+)-([A-Z]+)", RegexOptions.Compiled);

    private readonly UserPreferences _preferences;
    private readonly TextBox _folderPathBox;
    private readonly ComboBox _facilityFilter;
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
        _facilityFilter = this.FindControl<ComboBox>("FacilityFilter")!;
        _ratingFilter = this.FindControl<ComboBox>("RatingFilter")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _scenarioList = this.FindControl<ListBox>("ScenarioList")!;
        _loadButton = this.FindControl<Button>("LoadButton")!;

        this.FindControl<Button>("BrowseButton")!.Click += OnBrowseClick;
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        _loadButton.Click += OnLoadClick;
        _scenarioList.SelectionChanged += OnSelectionChanged;
        _scenarioList.DoubleTapped += OnDoubleTapped;
        _facilityFilter.SelectionChanged += (_, _) => ApplyFilter();
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

                var facility = "Unknown";
                var rating = "Unknown";
                if (name is not null)
                {
                    var match = NamePrefixRegex.Match(name);
                    if (match.Success)
                    {
                        rating = $"{match.Groups[1].Value}-{match.Groups[2].Value}";
                        facility = match.Groups[2].Value;
                    }
                }

                items.Add(new ScenarioItem(filePath, name!, facility, rating));
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Skipped unreadable scenario file: {File}", filePath);
            }
        }

        items.Sort((a, b) =>
        {
            int cmp = RatingSortKey(a.Rating).CompareTo(RatingSortKey(b.Rating));
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        _allItems = items;

        RebuildFilters();

        _statusText.Text = _allItems.Count > 0 ? $"{_allItems.Count} scenarios" : "No scenarios found.";
        ApplyFilter();
    }

    private void RebuildFilters()
    {
        var facilities = _allItems.Select(i => i.Facility).Distinct().OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        var ratings = _allItems.Select(i => i.Rating).Distinct().OrderBy(RatingSortKey).ThenBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();

        _facilityFilter.ItemsSource = facilities.Prepend("All").ToList();
        _facilityFilter.SelectedIndex = 0;

        _ratingFilter.ItemsSource = ratings.Prepend("All").ToList();
        _ratingFilter.SelectedIndex = 0;
    }

    private void ApplyFilter()
    {
        var facilitySel = _facilityFilter.SelectedItem as string;
        var ratingSel = _ratingFilter.SelectedItem as string;

        var filtered = _allItems
            .Where(i => facilitySel is null or "All" || i.Facility == facilitySel)
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

    private static string RatingSortKey(string rating)
    {
        // S ratings before C ratings, everything else after
        char prefix = rating.Length > 0 ? rating[0] : 'Z';
        int order = prefix switch
        {
            'S' => 0,
            'C' => 1,
            _ => 2,
        };
        return $"{order}{rating}";
    }
}

internal sealed record ScenarioItem(string FilePath, string Name, string Facility, string Rating);
