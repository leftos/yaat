using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

/// <summary>
/// Dialog result: either a local file path (string) or an API weather ID to fetch.
/// </summary>
public sealed record WeatherLoadResult(string? FilePath, string? ApiWeatherId, string? ApiWeatherName = null);

public partial class LoadWeatherWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<LoadWeatherWindow>();

    private readonly UserPreferences _preferences;
    private readonly TrainingDataService _trainingData = new();
    private readonly string _artccId;

    // ARTCC tab state
    private readonly TextBlock _artccStatusText;
    private readonly ListBox _artccWeatherList;
    private List<ArtccWeatherItem> _allArtccItems = [];

    // Local tab state
    private readonly TextBox _folderPathBox;
    private readonly TextBlock _localStatusText;
    private readonly ListBox _localWeatherList;
    private List<LocalWeatherItem> _allLocalItems = [];

    private readonly Button _loadButton;
    private readonly TabControl _sourceTabs;

    public LoadWeatherWindow()
        : this(new UserPreferences()) { }

    public LoadWeatherWindow(UserPreferences preferences)
    {
        _preferences = preferences;
        _artccId = preferences.ArtccId;
        InitializeComponent();
        new WindowGeometryHelper(this, preferences, "LoadWeather", 550, 450).Restore();

        _sourceTabs = this.FindControl<TabControl>("SourceTabs")!;
        _loadButton = this.FindControl<Button>("LoadButton")!;

        // ARTCC tab controls
        _artccStatusText = this.FindControl<TextBlock>("ArtccStatusText")!;
        _artccWeatherList = this.FindControl<ListBox>("ArtccWeatherList")!;

        // Local tab controls
        _folderPathBox = this.FindControl<TextBox>("FolderPathBox")!;
        _localStatusText = this.FindControl<TextBlock>("LocalStatusText")!;
        _localWeatherList = this.FindControl<ListBox>("LocalWeatherList")!;

        // Wire events
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        this.FindControl<Button>("BrowseButton")!.Click += OnBrowseClick;
        _loadButton.Click += OnLoadClick;

        _artccWeatherList.SelectionChanged += OnArtccSelectionChanged;
        _artccWeatherList.DoubleTapped += OnArtccDoubleTapped;

        _localWeatherList.SelectionChanged += OnLocalSelectionChanged;
        _localWeatherList.DoubleTapped += OnLocalDoubleTapped;

        _sourceTabs.SelectionChanged += OnTabChanged;

        if (!string.IsNullOrWhiteSpace(_artccId))
        {
            _ = LoadArtccWeatherAsync();
        }
        else
        {
            _artccStatusText.Text = "Set ARTCC ID in Settings first.";
        }

        var lastFolder = preferences.LastWeatherFolder;
        if (lastFolder is not null && Directory.Exists(lastFolder))
        {
            ScanFolder(lastFolder);
        }
    }

    private async Task LoadArtccWeatherAsync()
    {
        _artccStatusText.Text = "Loading…";
        var profiles = await _trainingData.GetWeatherProfilesAsync(_artccId);

        if (profiles.Count == 0)
        {
            _artccStatusText.Text = "No weather profiles found.";
            return;
        }

        _allArtccItems = profiles
            .Select(p => new ArtccWeatherItem(p.Id, p.Name, p.WindLayers.Count))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _artccWeatherList.ItemsSource = _allArtccItems;
        _artccStatusText.Text = $"{_allArtccItems.Count} weather profiles";
    }

    // --- Local tab ---

    private async void OnBrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select Weather Folder", AllowMultiple = false }
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
        _preferences.SetLastWeatherFolder(folder);

        var items = new List<LocalWeatherItem>();
        foreach (var filePath in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("windLayers", out var layersProp) || layersProp.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(filePath) : name;

                var layerCount = layersProp.GetArrayLength();
                items.Add(new LocalWeatherItem(filePath, name, layerCount));
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Skipped unreadable weather file: {File}", filePath);
            }
        }

        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _allLocalItems = items;

        _localWeatherList.ItemsSource = _allLocalItems;
        _localWeatherList.SelectedItem = null;
        UpdateLoadButton();

        _localStatusText.Text = _allLocalItems.Count > 0 ? $"{_allLocalItems.Count} weather profiles" : "No weather profiles found.";
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
            ? _artccWeatherList.SelectedItem is ArtccWeatherItem
            : _localWeatherList.SelectedItem is LocalWeatherItem;
    }

    private void OnArtccDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_artccWeatherList.SelectedItem is ArtccWeatherItem item)
        {
            Close(new WeatherLoadResult(null, item.Id, item.Name));
        }
    }

    private void OnLocalDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_localWeatherList.SelectedItem is LocalWeatherItem item)
        {
            Close(new WeatherLoadResult(item.FilePath, null));
        }
    }

    private void OnLoadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (IsArtccTabActive && _artccWeatherList.SelectedItem is ArtccWeatherItem artcc)
        {
            Close(new WeatherLoadResult(null, artcc.Id, artcc.Name));
        }
        else if (!IsArtccTabActive && _localWeatherList.SelectedItem is LocalWeatherItem local)
        {
            Close(new WeatherLoadResult(local.FilePath, null));
        }
    }
}

internal sealed record ArtccWeatherItem(string Id, string Name, int LayerCount)
{
    public string LayerCountText => LayerCount == 1 ? "(1 layer)" : $"({LayerCount} layers)";
}

internal sealed record LocalWeatherItem(string FilePath, string Name, int LayerCount)
{
    public string LayerCountText => LayerCount == 1 ? "(1 layer)" : $"({LayerCount} layers)";
}
