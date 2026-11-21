using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Client.Views;

/// <summary>
/// Dialog result: either a local file path (string) or an API scenario ID to fetch. A downloaded (mirrored) scenario
/// picked offline is a file path: its mirror file.
/// </summary>
public sealed record ScenarioLoadResult(string? FilePath, string? ApiScenarioId, string? ApiScenarioName = null);

public partial class LoadScenarioWindow : Window
{
    private static readonly ILogger Log = AppLog.CreateLogger<LoadScenarioWindow>();

    [GeneratedRegex(@"^([A-Z]+\d+)-([A-Z]+)")]
    private static partial Regex NamePrefixRegex();

    private readonly UserPreferences _preferences;
    private readonly ArtccTabSources _sources;
    private readonly string _artccId;
    private readonly IFilePickerService _filePicker;

    // ARTCC tab state
    private readonly ComboBox _artccFacilityFilter;
    private readonly TextBlock _artccStatusText;
    private readonly TextBlock _artccGateText;
    private readonly ListBox _artccScenarioList;
    private readonly Button _downloadButton;
    private readonly CancellationTokenSource _closing = new();
    private List<ArtccScenarioItem> _allArtccItems = [];

    // True when the ARTCC tab lists the downloaded mirror (no connection, or the hub call failed).
    private bool _artccOffline;
    private bool _downloading;
    private bool _closed;

    // Local tab state
    private readonly TextBox _folderPathBox;
    private readonly ComboBox _facilityFilter;
    private readonly ComboBox _ratingFilter;
    private readonly TextBlock _localStatusText;
    private readonly ListBox _localScenarioList;
    private readonly ListBox _recentScenarioList;
    private readonly Button _removeRecentButton;
    private List<LocalScenarioItem> _allLocalItems = [];

    private readonly Button _loadButton;
    private readonly TabControl _sourceTabs;

    /// <summary>Completes once the recent-scenario file checks finish and the list is populated. Tests await it.</summary>
    internal Task RecentListReady { get; private set; } = Task.CompletedTask;

    /// <summary>Completes once the ARTCC tab's list (the hub's or the downloaded mirror) is shown. Tests await it.</summary>
    internal Task ArtccListReady { get; private set; } = Task.CompletedTask;

    /// <summary>Completes once the last "Download for offline use" run has ended and its summary is shown.</summary>
    internal Task DownloadDone { get; private set; } = Task.CompletedTask;

    // Parameterless ctor for the Avalonia designer / XamlLoader and for GuideCapture's offscreen render. It builds the
    // real picker rather than taking one, because a picker cannot be created before `this` exists.
    public LoadScenarioWindow()
        : this(new UserPreferences(), null, FilePickerFactory.Create) { }

    public LoadScenarioWindow(UserPreferences preferences, ServerConnection? connection, Func<TopLevel, IFilePickerService> filePickerFactory)
        : this(preferences, filePickerFactory, ArtccTabSources.For(connection)) { }

    // Tests give the ARTCC tab's sources (connection state, hub catalog, download) directly.
    internal LoadScenarioWindow(UserPreferences preferences, Func<TopLevel, IFilePickerService> filePickerFactory, ArtccTabSources sources)
    {
        _preferences = preferences;
        _sources = sources;
        _artccId = preferences.ArtccId;
        InitializeComponent();
        _filePicker = filePickerFactory(this);
        new WindowGeometryHelper(this, preferences, "LoadScenario", 600, 500).Restore();

        _sourceTabs = this.FindControl<TabControl>("SourceTabs")!;
        _loadButton = this.FindControl<Button>("LoadButton")!;

        // ARTCC tab controls
        _artccFacilityFilter = this.FindControl<ComboBox>("ArtccFacilityFilter")!;
        _artccStatusText = this.FindControl<TextBlock>("ArtccStatusText")!;
        _artccGateText = this.FindControl<TextBlock>("ArtccGateText")!;
        _artccScenarioList = this.FindControl<ListBox>("ArtccScenarioList")!;
        _downloadButton = this.FindControl<Button>("DownloadButton")!;

        // Local tab controls
        _folderPathBox = this.FindControl<TextBox>("FolderPathBox")!;
        _facilityFilter = this.FindControl<ComboBox>("FacilityFilter")!;
        _ratingFilter = this.FindControl<ComboBox>("RatingFilter")!;
        _localStatusText = this.FindControl<TextBlock>("LocalStatusText")!;
        _localScenarioList = this.FindControl<ListBox>("LocalScenarioList")!;
        _recentScenarioList = this.FindControl<ListBox>("RecentScenarioList")!;
        _removeRecentButton = this.FindControl<Button>("RemoveRecentButton")!;

        // Wire events
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => DialogPresenter.Close(this, null);
        this.FindControl<Button>("BrowseButton")!.Click += OnBrowseClick;
        this.FindControl<Button>("LoadFileButton")!.Click += OnLoadFileClick;
        _removeRecentButton.Click += OnRemoveRecentClick;
        _loadButton.Click += OnLoadClick;

        _artccScenarioList.SelectionChanged += OnArtccSelectionChanged;
        _artccScenarioList.DoubleTapped += OnArtccDoubleTapped;
        _artccFacilityFilter.SelectionChanged += (_, _) => ApplyArtccFilter();

        _localScenarioList.SelectionChanged += OnLocalSelectionChanged;
        _localScenarioList.DoubleTapped += OnLocalDoubleTapped;
        _recentScenarioList.SelectionChanged += OnRecentSelectionChanged;
        _recentScenarioList.DoubleTapped += OnRecentDoubleTapped;
        _facilityFilter.SelectionChanged += (_, _) => ApplyLocalFilter();
        _ratingFilter.SelectionChanged += (_, _) => ApplyLocalFilter();

        _sourceTabs.SelectionChanged += OnTabChanged;
        _downloadButton.Click += (_, _) => StartDownload();
        _downloadButton.IsEnabled = !string.IsNullOrWhiteSpace(_artccId);
        Closed += (_, _) =>
        {
            _closed = true;
            _closing.Cancel();
            _closing.Dispose();
        };

        RebuildRecentList();

        // Connected: the hub's list, which applies the rating gate. Otherwise the downloaded mirror of the ARTCC.
        if (string.IsNullOrWhiteSpace(_artccId))
        {
            _artccStatusText.Text = "ARTCC not set — create or join a room first.";
        }
        else if (!_sources.IsConnected())
        {
            ArtccListReady = LoadMirrorScenariosAsync();
        }
        else
        {
            ArtccListReady = LoadArtccScenariosAsync();
        }

        // Pre-populate local folder if previously used
        string? lastFolder = preferences.LastScenarioFolder;
        if (lastFolder is not null && Directory.Exists(lastFolder))
        {
            ScanFolder(lastFolder);
        }
    }

    private async Task LoadArtccScenariosAsync()
    {
        ShowListStatus("Loading…");
        ScenarioCatalogResponseDto response;
        try
        {
            response = await _sources.GetScenarios();
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Failed to fetch scenarios from server for {Artcc}; listing the downloaded ones", _artccId);
            }

            await LoadMirrorScenariosAsync();
            return;
        }

        ScenarioSummaryDto[] visible = response.Visible;
        int hidden = response.HiddenByGateCount;

        if (visible.Length == 0 && hidden == 0)
        {
            ShowListStatus("No scenarios found.");
            return;
        }

        _allArtccItems = [.. visible.Select(s => new ArtccScenarioItem(s.Id, s.Name, FacilityOf(s.Name), null))];

        _allArtccItems.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        RebuildArtccFilters();
        ApplyArtccFilter();

        if (hidden > 0)
        {
            string noun = hidden == 1 ? "scenario" : "scenarios";
            _artccGateText.Text = $"{hidden} {noun} hidden — requires a higher VATSIM rating.";
            _artccGateText.IsVisible = true;
        }
        else
        {
            _artccGateText.IsVisible = false;
        }
    }

    private void RebuildArtccFilters()
    {
        _suppressFilterEvents = true;
        var facilities = _allArtccItems.Select(i => i.Facility).Distinct().OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        _artccFacilityFilter.ItemsSource = facilities.Prepend("All").ToList();
        _artccFacilityFilter.SelectedIndex = 0;
        _suppressFilterEvents = false;
    }

    private void ApplyArtccFilter()
    {
        if (_suppressFilterEvents)
        {
            return;
        }

        string? facilitySel = _artccFacilityFilter.SelectedItem as string;
        var filtered = _allArtccItems.Where(i => facilitySel is null or "All" || i.Facility == facilitySel).ToList();

        _artccScenarioList.ItemsSource = filtered;
        _artccScenarioList.SelectedItem = null;
        UpdateLoadButton();

        ShowListStatus((_allArtccItems.Count > 0 && filtered.Count == 0) ? "No scenarios match the filter." : ArtccCountText());
    }

    // While a download runs it owns the status line: a list load or a filter change leaves it alone.
    private void ShowListStatus(string text)
    {
        if (!_downloading)
        {
            _artccStatusText.Text = text;
        }
    }

    private string ArtccCountText() =>
        (_artccOffline, _allArtccItems.Count) switch
        {
            (true, 0) => $"Offline: no downloaded scenarios for {_artccId}",
            (true, 1) => "Offline: 1 downloaded scenario",
            (true, int n) => $"Offline: {n} downloaded scenarios",
            (false, 0) => "No scenarios found.",
            (false, 1) => "1 scenario",
            (false, int n) => $"{n} scenarios",
        };

    private static string FacilityOf(string name)
    {
        Match match = NamePrefixRegex().Match(name);
        return match.Success ? match.Groups[2].Value : "Unknown";
    }

    // --- ARTCC tab, offline: the downloaded mirror ---

    // Lists the mirror of the preference ARTCC from the local store, read off the UI thread. A picked entry loads as a
    // file. Each unreadable store file is listed by name on the notice line.
    private async Task LoadMirrorScenariosAsync()
    {
        List<string> notices = await ListMirrorAsync();
        await Dispatcher.UIThread.InvokeAsync(() => ShowNotices(notices));
    }

    // Lists the mirror and returns its notices. Any failure is logged and shown on the status line; during a download,
    // which owns the status line, it is returned as a notice instead.
    private async Task<List<string>> ListMirrorAsync()
    {
        string artcc = _artccId;
        try
        {
            ShowListStatus("Loading downloaded scenarios…");
            (List<ArtccScenarioItem> items, List<string> notices) = await Task.Run(() => ReadMirror(artcc));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _artccOffline = true;
                _allArtccItems = [.. items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
                RebuildArtccFilters();
                ApplyArtccFilter();
            });
            return notices;
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Error))
            {
                Log.LogError(ex, "Listing the downloaded scenarios of {Artcc} failed", artcc);
            }

            string text = "The downloaded scenarios could not be read: " + ex.Message;
            await Dispatcher.UIThread.InvokeAsync(() => ShowListStatus(text));
            return _downloading ? [text] : [];
        }
    }

    private static (List<ArtccScenarioItem> Items, List<string> Notices) ReadMirror(string artcc)
    {
        try
        {
            var store = new LocalScenarioStore();
            ArtccStoreListing listing = store.Load(artcc);
            List<ArtccScenarioItem> items =
            [
                .. listing
                    .Mirrors.Where(m => !m.Corrupt)
                    .Select(m => m.Mirror)
                    .Select(m => new ArtccScenarioItem(m.Id, m.Name, FacilityOf(m.Name), store.GetMirrorPath(artcc, m.Id))),
            ];
            return (items, [.. listing.Problems.Select(p => $"{p.FileName}: {p.Reason}")]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Could not read the downloaded scenarios of {Artcc}", artcc);
            }

            return ([], ["The downloaded scenarios could not be read: " + ex.Message]);
        }
    }

    private void ShowNotices(List<string> lines)
    {
        _artccGateText.Text = string.Join("\n", lines);
        _artccGateText.IsVisible = lines.Count > 0;
    }

    // --- ARTCC tab: Download for offline use ---

    private void StartDownload()
    {
        if (_downloading || _closed || string.IsNullOrWhiteSpace(_artccId))
        {
            return;
        }

        DownloadDone = DownloadAsync();
    }

    // Downloads every scenario of the preference ARTCC into the local store off the UI thread; one run at a time, the
    // button disabled until it ends, however it ends. Progress and the summary go to the status text; offline, the
    // mirror is listed again and its notices are shown before the download's failures and store problems.
    private async Task DownloadAsync()
    {
        string artcc = _artccId;
        CancellationToken ct = _closing.Token;
        _downloading = true;
        _downloadButton.IsEnabled = false;
        _artccStatusText.Text = "Downloading…";
        var progress = new UiThreadProgress(p =>
        {
            if (_downloading)
            {
                _artccStatusText.Text = $"Downloading {p.Completed}/{p.Total}…";
            }
        });

        string status = "Download failed.";
        List<string> notices = [];
        try
        {
            (status, notices) = await Task.Run(() => RunDownloadAsync(_sources.Download, artcc, progress, ct));
            if (_artccOffline && !_closed)
            {
                notices = [.. await ListMirrorAsync(), .. notices];
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _downloading = false;
                _downloadButton.IsEnabled = true;
                _artccStatusText.Text = status;
                ShowNotices(notices);
            });
        }
    }

    private static async Task<(string Status, List<string> Notices)> RunDownloadAsync(
        Func<string, IProgress<DownloadProgress>, CancellationToken, Task<DownloadReport>> download,
        string artcc,
        IProgress<DownloadProgress> progress,
        CancellationToken ct
    )
    {
        try
        {
            return Summarize(await download(artcc, progress, ct));
        }
        catch (OperationCanceledException)
        {
            return ("Download cancelled.", []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Downloading the scenarios of {Artcc} failed", artcc);
            }

            return ("Download failed: " + ex.Message, []);
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Error))
            {
                Log.LogError(ex, "Downloading the scenarios of {Artcc} failed unexpectedly", artcc);
            }

            return ("Download failed: " + ex.Message, []);
        }
    }

    /// <summary>The status line and the notice lines for a finished download.</summary>
    internal static (string Status, List<string> Notices) Summarize(DownloadReport report)
    {
        if (report.Offline)
        {
            return ($"Download failed: {report.ListFailure}", []);
        }

        int updated = report.Updated.Count + report.ServerChanged.Count;
        string status = $"{report.Downloaded.Count} downloaded, {updated} updated, {report.Failed.Count} failed";
        return (
            status,
            [
                .. report.Failed.Select(f => $"{(string.IsNullOrEmpty(f.Name) ? f.Id : f.Name)}: {f.Reason}"),
                .. report.Problems.Select(p => $"{p.FileName}: {p.Reason}"),
            ]
        );
    }

    private static ScenarioLoadResult ArtccResult(ArtccScenarioItem item) =>
        item.MirrorPath is not null ? new ScenarioLoadResult(item.MirrorPath, null) : new ScenarioLoadResult(null, item.Id, item.Name);

    // Shows each progress report on the UI thread.
    private sealed class UiThreadProgress(Action<DownloadProgress> show) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => Dispatcher.UIThread.Post(() => show(value));
    }

    // --- Local tab ---

    private async void OnBrowseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string? path = await _filePicker.OpenFolderAsync(new OpenFolderOptions("Select Scenario Folder"));
        if (path is not null)
        {
            ScanFolder(path);
        }
    }

    private void ScanFolder(string folder)
    {
        _folderPathBox.Text = folder;
        _preferences.SetLastScenarioFolder(folder);

        var items = new List<LocalScenarioItem>();
        foreach (string filePath in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                string? name = root.TryGetProperty("name", out JsonElement nameProp) ? nameProp.GetString() : null;
                name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(filePath) : name;

                string facility = "Unknown";
                string rating = "Unknown";
                if (name is not null)
                {
                    Match match = NamePrefixRegex().Match(name);
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

    private void RebuildLocalFilters() => InitFilters(_facilityFilter, _ratingFilter, _allLocalItems, i => i.Facility, i => i.Rating);

    private void ApplyLocalFilter() =>
        ApplyFilter(_facilityFilter, _ratingFilter, _localScenarioList, _localStatusText, _allLocalItems, i => i.Facility, i => i.Rating);

    // --- Recent scenarios ---

    private void RebuildRecentList() => RecentListReady = RebuildRecentListAsync();

    // File.Exists can block for tens of seconds on an unreachable network share, so the checks run off the UI
    // thread; the list is empty until they finish. A failed check leaves the list empty and logs, never throws.
    private async Task RebuildRecentListAsync()
    {
        List<RecentScenario> recents = [.. _preferences.RecentScenarios.Where(r => !r.IsApi)];
        List<RecentScenarioItem> items;
        try
        {
            items = await Task.Run(() => BuildRecentItems(recents));
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Failed to check recent scenario files for missing files.");
            items = [];
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _recentScenarioList.ItemsSource = items;
            _recentScenarioList.SelectedItem = null;
            UpdateRecentRemoveButton();
            UpdateLoadButton();
        });
    }

    private static List<RecentScenarioItem> BuildRecentItems(List<RecentScenario> recents) =>
        [
            .. recents.Select(r =>
            {
                bool missing = !File.Exists(r.FilePath);
                string displayName = missing ? $"{r.Name} (missing)" : r.Name;
                return new RecentScenarioItem(r.Key, displayName, r.FilePath, missing);
            }),
        ];

    private async void OnLoadFileClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string? path = await _filePicker.OpenFileAsync(
            new OpenFileOptions("Load Scenario File", [new FilePickerFilter("Scenario files", ["*.json"])])
        );
        if (path is not null)
        {
            DialogPresenter.Close(this, new ScenarioLoadResult(path, null));
        }
    }

    private void OnRemoveRecentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_recentScenarioList.SelectedItem is RecentScenarioItem item)
        {
            _preferences.RemoveRecentScenario(item.Key);
            RebuildRecentList();
        }
    }

    private void OnRecentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_recentScenarioList.SelectedItem is RecentScenarioItem)
        {
            _localScenarioList.SelectedItem = null;
        }

        UpdateRecentRemoveButton();
        UpdateLoadButton();
    }

    private void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_recentScenarioList.SelectedItem is RecentScenarioItem { IsMissing: false } item)
        {
            DialogPresenter.Close(this, new ScenarioLoadResult(item.FilePath, null));
        }
    }

    private void UpdateRecentRemoveButton() => _removeRecentButton.IsEnabled = _recentScenarioList.SelectedItem is RecentScenarioItem;

    // --- Selection / load ---

    private bool IsArtccTabActive => _sourceTabs.SelectedIndex == 0;

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e) => UpdateLoadButton();

    private void OnArtccSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateLoadButton();

    private void OnLocalSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_localScenarioList.SelectedItem is LocalScenarioItem)
        {
            _recentScenarioList.SelectedItem = null;
        }

        UpdateLoadButton();
    }

    private void UpdateLoadButton()
    {
        if (IsArtccTabActive)
        {
            _loadButton.IsEnabled = _artccScenarioList.SelectedItem is ArtccScenarioItem;
            return;
        }

        _loadButton.IsEnabled =
            (_recentScenarioList.SelectedItem is RecentScenarioItem { IsMissing: false }) || (_localScenarioList.SelectedItem is LocalScenarioItem);
    }

    private void OnArtccDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_artccScenarioList.SelectedItem is ArtccScenarioItem item)
        {
            DialogPresenter.Close(this, ArtccResult(item));
        }
    }

    private void OnLocalDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_localScenarioList.SelectedItem is LocalScenarioItem item)
        {
            DialogPresenter.Close(this, new ScenarioLoadResult(item.FilePath, null));
        }
    }

    private void OnLoadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (IsArtccTabActive && _artccScenarioList.SelectedItem is ArtccScenarioItem artcc)
        {
            DialogPresenter.Close(this, ArtccResult(artcc));
        }
        else if (!IsArtccTabActive && _recentScenarioList.SelectedItem is RecentScenarioItem { IsMissing: false } recent)
        {
            DialogPresenter.Close(this, new ScenarioLoadResult(recent.FilePath, null));
        }
        else if (!IsArtccTabActive && _localScenarioList.SelectedItem is LocalScenarioItem local)
        {
            DialogPresenter.Close(this, new ScenarioLoadResult(local.FilePath, null));
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

        string? facilitySel = facilityBox.SelectedItem as string;
        string? ratingSel = ratingBox.SelectedItem as string;

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

/// <summary>An ARTCC-tab row: a hub scenario (loaded by id) or, offline, a downloaded one (loaded from its mirror file).</summary>
internal sealed record ArtccScenarioItem(string Id, string Name, string Facility, string? MirrorPath);

/// <summary>Where the ARTCC tab gets its data: whether the hub is connected, the hub's catalog, and the offline download.</summary>
/// <param name="IsConnected">Whether the hub connection is up now.</param>
/// <param name="GetScenarios">Reads the hub's scenario catalog for the room's ARTCC.</param>
/// <param name="Download">Downloads an ARTCC's scenarios into the local store.</param>
internal sealed record ArtccTabSources(
    Func<bool> IsConnected,
    Func<Task<ScenarioCatalogResponseDto>> GetScenarios,
    Func<string, IProgress<DownloadProgress>, CancellationToken, Task<DownloadReport>> Download
)
{
    /// <summary>The sources over a hub connection, or none, and the vNAS data-api.</summary>
    public static ArtccTabSources For(ServerConnection? connection) =>
        new(
            () => connection is { IsConnected: true },
            () =>
                connection is not null
                    ? connection.GetScenariosAsync()
                    : Task.FromException<ScenarioCatalogResponseDto>(new InvalidOperationException("No server connection.")),
            DownloadFromVnasAsync
        );

    private static async Task<DownloadReport> DownloadFromVnasAsync(string artcc, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        using var client = new VnasTrainingClient();
        return await new ScenarioDownloader(client, new LocalScenarioStore()).DownloadArtccAsync(artcc, progress, ct);
    }
}

internal sealed record LocalScenarioItem(string FilePath, string Name, string Facility, string Rating);

internal sealed record RecentScenarioItem(string Key, string DisplayName, string FilePath, bool IsMissing)
{
    /// <summary>Dim a missing entry so it reads as unavailable; loadable entries are fully opaque.</summary>
    public double Opacity => IsMissing ? 0.5 : 1.0;
}
