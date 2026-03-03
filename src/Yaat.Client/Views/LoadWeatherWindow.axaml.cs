using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;

namespace Yaat.Client.Views;

public partial class LoadWeatherWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<LoadWeatherWindow>();

    private readonly UserPreferences _preferences;
    private readonly TextBox _folderPathBox;
    private readonly TextBlock _statusText;
    private readonly ListBox _weatherList;
    private readonly Button _loadButton;
    private List<WeatherItem> _allItems = [];

    public LoadWeatherWindow()
        : this(new UserPreferences()) { }

    public LoadWeatherWindow(UserPreferences preferences)
    {
        _preferences = preferences;
        InitializeComponent();

        _folderPathBox = this.FindControl<TextBox>("FolderPathBox")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _weatherList = this.FindControl<ListBox>("WeatherList")!;
        _loadButton = this.FindControl<Button>("LoadButton")!;

        this.FindControl<Button>("BrowseButton")!.Click += OnBrowseClick;
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        _loadButton.Click += OnLoadClick;
        _weatherList.SelectionChanged += OnSelectionChanged;
        _weatherList.DoubleTapped += OnDoubleTapped;

        var lastFolder = preferences.LastWeatherFolder;
        if (lastFolder is not null && Directory.Exists(lastFolder))
        {
            ScanFolder(lastFolder);
        }
    }

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

        var items = new List<WeatherItem>();
        foreach (var filePath in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Skip scenario files (they have "primaryAirportId" or "aircraft" but not "windLayers")
                if (!root.TryGetProperty("windLayers", out var layersProp) || layersProp.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(filePath) : name;

                var layerCount = layersProp.GetArrayLength();

                items.Add(new WeatherItem(filePath, name!, layerCount));
            }
            catch (Exception ex)
            {
                Log.LogWarning(ex, "Skipped unreadable weather file: {File}", filePath);
            }
        }

        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _allItems = items;

        _weatherList.ItemsSource = _allItems;
        _weatherList.SelectedItem = null;
        _loadButton.IsEnabled = false;

        _statusText.Text = _allItems.Count > 0 ? $"{_allItems.Count} weather profiles" : "No weather profiles found.";
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _loadButton.IsEnabled = _weatherList.SelectedItem is WeatherItem;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_weatherList.SelectedItem is WeatherItem item)
        {
            Close(item.FilePath);
        }
    }

    private void OnLoadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_weatherList.SelectedItem is WeatherItem item)
        {
            Close(item.FilePath);
        }
    }
}

internal sealed record WeatherItem(string FilePath, string Name, int LayerCount)
{
    public string LayerCountText => LayerCount == 1 ? "(1 layer)" : $"({LayerCount} layers)";
}
