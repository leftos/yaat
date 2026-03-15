using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Dialog result: either a local file path (string) or an API scenario ID to fetch.
/// </summary>
public sealed record ScenarioLoadResult(string? FilePath, string? ApiScenarioId, string? ApiScenarioName = null);

public partial class LoadScenarioWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<LoadScenarioWindow>();
    private static readonly Regex NamePrefixRegex = new(@"^([A-Z]+\d+)-([A-Z]+)", RegexOptions.Compiled);

    private readonly UserPreferences _preferences;
    private readonly TrainingDataService _trainingData = new();
    private readonly string _artccId;

    // ARTCC tab state
    private readonly ComboBox _artccFacilityFilter;
    private readonly ComboBox _artccRatingFilter;
    private readonly TextBlock _artccStatusText;
    private readonly ListBox _artccScenarioList;
    private List<ArtccScenarioItem> _allArtccItems = [];

    // Local tab state
    private readonly TextBox _folderPathBox;
    private readonly ComboBox _facilityFilter;
    private readonly ComboBox _ratingFilter;
    private readonly TextBlock _localStatusText;
    private readonly ListBox _localScenarioList;
    private List<LocalScenarioItem> _allLocalItems = [];

    private readonly Button _loadButton;
    private readonly TabControl _sourceTabs;

    public LoadScenarioWindow()
        : this(new UserPreferences()) { }

    public LoadScenarioWindow(UserPreferences preferences)
    {
        _preferences = preferences;
        _artccId = preferences.ArtccId;
        InitializeComponent();
        new WindowGeometryHelper(this, preferences, "LoadScenario", 600, 500).Restore();

        _sourceTabs = this.FindControl<TabControl>("SourceTabs")!;
        _loadButton = this.FindControl<Button>("LoadButton")!;

        // ARTCC tab controls
        _artccFacilityFilter = this.FindControl<ComboBox>("ArtccFacilityFilter")!;
        _artccRatingFilter = this.FindControl<ComboBox>("ArtccRatingFilter")!;
        _artccStatusText = this.FindControl<TextBlock>("ArtccStatusText")!;
        _artccScenarioList = this.FindControl<ListBox>("ArtccScenarioList")!;

        // Local tab controls
        _folderPathBox = this.FindControl<TextBox>("FolderPathBox")!;
        _facilityFilter = this.FindControl<ComboBox>("FacilityFilter")!;
        _ratingFilter = this.FindControl<ComboBox>("RatingFilter")!;
        _localStatusText = this.FindControl<TextBlock>("LocalStatusText")!;
        _localScenarioList = this.FindControl<ListBox>("LocalScenarioList")!;

        // Wire events
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        this.FindControl<Button>("BrowseButton")!.Click += OnBrowseClick;
        _loadButton.Click += OnLoadClick;

        _artccScenarioList.SelectionChanged += OnArtccSelectionChanged;
        _artccScenarioList.DoubleTapped += OnArtccDoubleTapped;
        _artccFacilityFilter.SelectionChanged += (_, _) => ApplyArtccFilter();
        _artccRatingFilter.SelectionChanged += (_, _) => ApplyArtccFilter();

        _localScenarioList.SelectionChanged += OnLocalSelectionChanged;
        _localScenarioList.DoubleTapped += OnLocalDoubleTapped;
        _facilityFilter.SelectionChanged += (_, _) => ApplyLocalFilter();
        _ratingFilter.SelectionChanged += (_, _) => ApplyLocalFilter();

        _sourceTabs.SelectionChanged += OnTabChanged;

        // Load ARTCC scenarios if we have an ARTCC ID
        if (!string.IsNullOrWhiteSpace(_artccId))
        {
            _ = LoadArtccScenariosAsync();
        }
        else
        {
            _artccStatusText.Text = "Set ARTCC ID in Settings first.";
        }

        // Pre-populate local folder if previously used
        var lastFolder = preferences.LastScenarioFolder;
        if (lastFolder is not null && Directory.Exists(lastFolder))
        {
            ScanFolder(lastFolder);
        }
    }

    private async Task LoadArtccScenariosAsync()
    {
        _artccStatusText.Text = "Loading…";
        var summaries = await _trainingData.GetScenarioSummariesAsync(_artccId);

        if (summaries.Count == 0)
        {
            _artccStatusText.Text = "No scenarios found.";
            return;
        }

        _allArtccItems = summaries
            .Select(s =>
            {
                var facility = "Unknown";
                var rating = "Unknown";
                var match = NamePrefixRegex.Match(s.Name);
                if (match.Success)
                {
                    rating = $"{match.Groups[1].Value}-{match.Groups[2].Value}";
                    facility = match.Groups[2].Value;
                }

                return new ArtccScenarioItem(s.Id, s.Name, facility, rating);
            })
            .ToList();

        _allArtccItems.Sort(
            (a, b) =>
            {
                int cmp = string.Compare(RatingSortKey(a.Rating), RatingSortKey(b.Rating), StringComparison.Ordinal);
                return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }
        );

        RebuildArtccFilters();
        ApplyArtccFilter();
    }

    private void RebuildArtccFilters()
    {
        InitFilters(_artccFacilityFilter, _artccRatingFilter, _allArtccItems, i => i.Facility, i => i.Rating);
    }

    private void ApplyArtccFilter()
    {
        ApplyFilter(_artccFacilityFilter, _artccRatingFilter, _artccScenarioList, _artccStatusText, _allArtccItems, i => i.Facility, i => i.Rating);
    }

    // --- Local tab ---

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

        var items = new List<LocalScenarioItem>();
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

                items.Add(new LocalScenarioItem(filePath, name!, facility, rating));
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Skipped unreadable scenario file: {File}", filePath);
            }
        }

        items.Sort(
            (a, b) =>
            {
                int cmp = string.Compare(RatingSortKey(a.Rating), RatingSortKey(b.Rating), StringComparison.Ordinal);
                return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            }
        );
        _allLocalItems = items;

        RebuildLocalFilters();
        _localStatusText.Text = _allLocalItems.Count > 0 ? $"{_allLocalItems.Count} scenarios" : "No scenarios found.";
        ApplyLocalFilter();
    }

    private void RebuildLocalFilters()
    {
        InitFilters(_facilityFilter, _ratingFilter, _allLocalItems, i => i.Facility, i => i.Rating);
    }

    private void ApplyLocalFilter()
    {
        ApplyFilter(_facilityFilter, _ratingFilter, _localScenarioList, _localStatusText, _allLocalItems, i => i.Facility, i => i.Rating);
    }

    // --- Selection / load ---

    private bool IsArtccTabActive => _sourceTabs.SelectedIndex == 0;

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateLoadButton();
    }

    private void OnArtccSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateLoadButton();
    }

    private void OnLocalSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateLoadButton();
    }

    private void UpdateLoadButton()
    {
        _loadButton.IsEnabled = IsArtccTabActive
            ? _artccScenarioList.SelectedItem is ArtccScenarioItem
            : _localScenarioList.SelectedItem is LocalScenarioItem;
    }

    private void OnArtccDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_artccScenarioList.SelectedItem is ArtccScenarioItem item)
        {
            Close(new ScenarioLoadResult(null, item.Id, item.Name));
        }
    }

    private void OnLocalDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_localScenarioList.SelectedItem is LocalScenarioItem item)
        {
            Close(new ScenarioLoadResult(item.FilePath, null));
        }
    }

    private void OnLoadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (IsArtccTabActive && _artccScenarioList.SelectedItem is ArtccScenarioItem artcc)
        {
            Close(new ScenarioLoadResult(null, artcc.Id, artcc.Name));
        }
        else if (!IsArtccTabActive && _localScenarioList.SelectedItem is LocalScenarioItem local)
        {
            Close(new ScenarioLoadResult(local.FilePath, null));
        }
    }

    // --- Shared cross-filtering logic ---

    private bool _suppressFilterEvents;

    private void InitFilters<T>(ComboBox facilityBox, ComboBox ratingBox, List<T> allItems, Func<T, string> getFacility, Func<T, string> getRating)
    {
        _suppressFilterEvents = true;

        var facilities = allItems.Select(getFacility).Distinct().OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        var ratings = allItems.Select(getRating).Distinct().OrderBy(RatingSortKey).ThenBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();

        facilityBox.ItemsSource = facilities.Prepend("All").ToList();
        facilityBox.SelectedIndex = 0;

        ratingBox.ItemsSource = ratings.Prepend("All").ToList();
        ratingBox.SelectedIndex = 0;

        _suppressFilterEvents = false;
    }

    private void ApplyFilter<T>(
        ComboBox facilityBox,
        ComboBox ratingBox,
        ListBox listBox,
        TextBlock statusText,
        List<T> allItems,
        Func<T, string> getFacility,
        Func<T, string> getRating
    )
    {
        if (_suppressFilterEvents)
        {
            return;
        }

        var facilitySel = facilityBox.SelectedItem as string;
        var ratingSel = ratingBox.SelectedItem as string;

        var filtered = allItems
            .Where(i => facilitySel is null or "All" || getFacility(i) == facilitySel)
            .Where(i => ratingSel is null or "All" || getRating(i) == ratingSel)
            .ToList();

        listBox.ItemsSource = filtered;
        listBox.SelectedItem = null;
        UpdateLoadButton();

        // Cross-filter: update the other dropdown's options based on items matching the current selection
        _suppressFilterEvents = true;

        var itemsMatchingFacility = allItems.Where(i => facilitySel is null or "All" || getFacility(i) == facilitySel).ToList();
        var availableRatings = itemsMatchingFacility
            .Select(getRating)
            .Distinct()
            .OrderBy(RatingSortKey)
            .ThenBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToList();
        UpdateDropdown(ratingBox, availableRatings, ratingSel);

        var itemsMatchingRating = allItems.Where(i => ratingSel is null or "All" || getRating(i) == ratingSel).ToList();
        var availableFacilities = itemsMatchingRating.Select(getFacility).Distinct().OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
        UpdateDropdown(facilityBox, availableFacilities, facilitySel);

        _suppressFilterEvents = false;

        if (allItems.Count > 0 && filtered.Count == 0)
        {
            statusText.Text = "No scenarios match the filter.";
        }
        else
        {
            statusText.Text = allItems.Count > 0 ? $"{allItems.Count} scenarios" : "No scenarios found.";
        }
    }

    private static void UpdateDropdown(ComboBox box, List<string> available, string? currentSelection)
    {
        var newItems = available.Prepend("All").ToList();
        box.ItemsSource = newItems;

        if (currentSelection is not null && newItems.Contains(currentSelection))
        {
            box.SelectedItem = currentSelection;
        }
        else
        {
            box.SelectedIndex = 0;
        }
    }

    private static string RatingSortKey(string rating)
    {
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

internal sealed record ArtccScenarioItem(string Id, string Name, string Facility, string Rating);

internal sealed record LocalScenarioItem(string FilePath, string Name, string Facility, string Rating);
