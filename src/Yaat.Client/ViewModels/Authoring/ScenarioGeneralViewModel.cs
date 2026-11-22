using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>One entry of a General-tab picker: the value the document stores and the text the picker shows.</summary>
/// <param name="Value">The stored value; empty for none (the document holds null).</param>
/// <param name="Label">The text shown.</param>
public sealed record ScenarioPickOption(string Value, string Label);

/// <summary>
/// The General tab's settings fields over one <see cref="ScenarioEditSession"/>: name, ARTCC (read-only), student
/// position, minimum rating, primary airport, primary approach and auto-delete. Each field holds the text the author
/// sees; its Commit command (the view calls it on focus loss, Enter or a picker's selection) writes it to the document
/// as one undo step, and a commit that changes nothing records nothing. A blank name is refused with
/// <see cref="NameError"/>. The position and airport options come from one load of the ARTCC's config, off the UI thread
/// through the services' post; when it cannot load, both fall back to free text with <see cref="ConfigProblem"/>. A value
/// the document holds that no option lists is still offered, so it is never dropped. Holds the session, never the
/// scenario: <see cref="Reload"/> re-reads every field after undo or redo replaced the document. The ATC positions,
/// initialization triggers and strip placements are row lists kept in the ScenarioGeneralViewModel.Lists.cs partial; the
/// arrival, VFR arrival and overflight generators are row lists kept in the ScenarioGeneralViewModel.Generators.cs partial.
/// </summary>
public sealed partial class ScenarioGeneralViewModel : ObservableObject, IDisposable
{
    /// <summary>The name error a blank name shows.</summary>
    public const string NameRequiredText = "A scenario needs a name.";

    private static readonly ILogger Log = AppLog.CreateLogger<ScenarioGeneralViewModel>();

    private static readonly IReadOnlyList<ScenarioPickOption> BaseRatingOptions =
    [
        new ScenarioPickOption("", "None"),
        .. ScenarioRatingClassifier.LongFormRatings.Select(r => new ScenarioPickOption(r, r)),
    ];

    private static readonly IReadOnlyList<ScenarioPickOption> BaseAutoDeleteOptions =
    [
        .. AutoDeleteModes.Ordered.Select(m => (m is null) ? new ScenarioPickOption("", "Default") : new ScenarioPickOption(m, m)),
    ];

    private readonly ScenarioEditSession _session;
    private readonly ScenarioBrowserServices _services;
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<ScenarioPickOption> _catalogOptions = [];
    private IReadOnlyList<string> _configAirports = [];
    private string _artccId = "";
    private bool _disposed;

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string? _nameError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StudentPositionPick), nameof(StudentPositionSelection))]
    private string _studentPositionId = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinimumRatingPick), nameof(MinimumRatingSelection))]
    private string _minimumRating = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryAirportPick))]
    private string _primaryAirportId = "";

    [ObservableProperty]
    private string _primaryApproach = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoDeletePick), nameof(AutoDeleteSelection))]
    private string _autoDeleteMode = "";

    [ObservableProperty]
    private bool _isStudentPositionFreeText = true;

    [ObservableProperty]
    private bool _isPrimaryAirportFreeText = true;

    [ObservableProperty]
    private string? _configProblem;

    // Each picker's one options collection, its ItemsSource for the view model's lifetime: a rebuild updates it in place.
    private readonly ObservableCollection<ScenarioPickOption> _studentPositionOptions = [];
    private readonly ObservableCollection<ScenarioPickOption> _ratingOptions = [.. BaseRatingOptions];
    private readonly ObservableCollection<ScenarioPickOption> _autoDeleteOptions = [.. BaseAutoDeleteOptions];
    private readonly ObservableCollection<string> _airportOptions = [];

    /// <summary>Initializes a new instance of the <see cref="ScenarioGeneralViewModel"/> class and starts loading its pickers.</summary>
    /// <param name="session">The open working copy's session.</param>
    /// <param name="services">The ARTCC configs, airport ids and UI-thread post the pickers load through.</param>
    public ScenarioGeneralViewModel(ScenarioEditSession session, ScenarioBrowserServices services)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Reload();
        PickersLoaded = LoadPickersAsync(session.ArtccId, _cts.Token);
    }

    /// <summary>Raised after a commit wrote to the document (whether or not it changed it).</summary>
    public event EventHandler? Edited;

    /// <summary>Gets the scenario's ARTCC id, shown read-only: a copy stays in the folder of the ARTCC it was opened from.</summary>
    public string ArtccId
    {
        get => _artccId;
        private set => SetProperty(ref _artccId, value);
    }

    /// <summary>Gets the student position picker's options; one collection, updated in place.</summary>
    public IReadOnlyList<ScenarioPickOption> StudentPositionOptions => _studentPositionOptions;

    /// <summary>Gets the minimum rating picker's options; one collection, updated in place.</summary>
    public IReadOnlyList<ScenarioPickOption> RatingOptions => _ratingOptions;

    /// <summary>Gets the auto-delete picker's options; one collection, updated in place.</summary>
    public IReadOnlyList<ScenarioPickOption> AutoDeleteOptions => _autoDeleteOptions;

    /// <summary>Gets the primary airport picker's options; one collection, updated in place.</summary>
    public IReadOnlyList<string> AirportOptions => _airportOptions;

    /// <summary>Gets the pickers' first load: finished once the position and airport options are applied. Tests await it.</summary>
    public Task PickersLoaded { get; }

    /// <summary>Re-reads every field from the session's document and clears the name error.</summary>
    public void Reload()
    {
        Scenario scenario = _session.Document.Scenario;
        ArtccId = scenario.ArtccId;
        Name = scenario.Name;
        NameError = null;
        StudentPositionId = scenario.StudentPositionId ?? "";
        MinimumRating = scenario.MinimumRating ?? "";
        PrimaryAirportId = scenario.PrimaryAirportId ?? "";
        PrimaryApproach = scenario.PrimaryApproach ?? "";
        AutoDeleteMode = scenario.AutoDeleteMode ?? "";
        RebuildRows();
        RebuildOptions();
        RefreshSummaries();
    }

    /// <summary>Stops the pickers' load; nothing that completes afterwards changes the fields.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>Writes the name, trimmed; a blank name sets <see cref="NameError"/> and leaves the document's name.</summary>
    [RelayCommand]
    private void CommitName()
    {
        string name = Name.Trim();
        if (name.Length == 0)
        {
            NameError = NameRequiredText;
            return;
        }

        NameError = null;
        Name = name;
        Apply("Edit name", s => s.Name = name);
    }

    /// <summary>Writes the student position id; blank writes none.</summary>
    [RelayCommand]
    private void CommitStudentPosition()
    {
        string? value = Optional(StudentPositionId);
        StudentPositionId = value ?? "";
        Apply("Edit student position", s => s.StudentPositionId = value);
    }

    /// <summary>Writes the minimum rating; the none entry writes none.</summary>
    [RelayCommand]
    private void CommitMinimumRating()
    {
        string? value = Optional(MinimumRating);
        MinimumRating = value ?? "";
        Apply("Edit minimum rating", s => s.MinimumRating = value);
    }

    /// <summary>Writes the primary airport id; blank writes none.</summary>
    [RelayCommand]
    private void CommitPrimaryAirport()
    {
        string? value = Optional(PrimaryAirportId);
        PrimaryAirportId = value ?? "";
        Apply("Edit primary airport", s => s.PrimaryAirportId = value);
    }

    /// <summary>Writes the primary approach; blank writes none.</summary>
    [RelayCommand]
    private void CommitPrimaryApproach()
    {
        string? value = Optional(PrimaryApproach);
        PrimaryApproach = value ?? "";
        Apply("Edit primary approach", s => s.PrimaryApproach = value);
    }

    /// <summary>Writes the auto-delete mode; the Default entry writes none.</summary>
    [RelayCommand]
    private void CommitAutoDelete()
    {
        string? value = Optional(AutoDeleteMode);
        AutoDeleteMode = value ?? "";
        Apply("Edit auto-delete", s => s.AutoDeleteMode = value);
    }

    private static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void Apply(string description, Action<Scenario> change)
    {
        _session.Edit(description, change);
        RebuildOptions();
        RefreshSummaries();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    // Each picker's options, plus the document's value when no option lists it, so the picker still shows it; each list is
    // updated in place, so its picker keeps the entries that stay and its selection binding.
    private void RebuildOptions()
    {
        ScenarioFieldText.SetOptions(
            _studentPositionOptions,
            WithCurrent(_catalogOptions, StudentPositionId, $"{StudentPositionId} (not in the ARTCC config)"),
            () => OnPropertyChanged(nameof(StudentPositionSelection))
        );
        ScenarioFieldText.SetOptions(
            _ratingOptions,
            WithCurrent(BaseRatingOptions, MinimumRating, MinimumRating),
            () => OnPropertyChanged(nameof(MinimumRatingSelection))
        );
        ScenarioFieldText.SetOptions(
            _autoDeleteOptions,
            WithCurrent(BaseAutoDeleteOptions, AutoDeleteMode, AutoDeleteMode),
            () => OnPropertyChanged(nameof(AutoDeleteSelection))
        );
        ScenarioFieldText.SetOptions(
            _airportOptions,
            WithCurrentAirport(_configAirports, PrimaryAirportId),
            () => OnPropertyChanged(nameof(PrimaryAirportPick))
        );
        RebuildRowOptions();
    }

    // The config's airports with the document's airport offered once, in the document's spelling: it takes the place of
    // the config entry it matches whatever the case, or is appended when none does.
    private static IReadOnlyList<string> WithCurrentAirport(IReadOnlyList<string> airports, string value)
    {
        if (value.Length == 0)
        {
            return airports;
        }

        return airports.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? [.. airports.Select(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase) ? value : a)]
            : [.. airports, value];
    }

    // One load of the ARTCC's config per open: its positions (with its neighbours') and its airports.
    private async Task LoadPickersAsync(string artccId, CancellationToken ct)
    {
        PickerData data;
        try
        {
            ArtccConfigSource configs = _services.Configs;
            data = await Task.Run(() => ReadPickersAsync(configs, artccId, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            LogFailure(ex, "loading the ARTCC config of " + artccId);
            data = new PickerData(null, null, null, ex.Message);
        }

        await PostAsync(() => ApplyPickers(data)).ConfigureAwait(false);
    }

    // The catalog of the ARTCC and its neighbours, one row per position id, and the airports and strip bays its own config
    // lists; no options and the load's problem when the ARTCC's own config cannot be read.
    private static async Task<PickerData> ReadPickersAsync(ArtccConfigSource configs, string artccId, CancellationToken ct)
    {
        ArtccConfigLoad load = await configs.LoadAsync(artccId, ct).ConfigureAwait(false);
        if (load.Config is null)
        {
            return new PickerData(null, null, null, load.Problem ?? $"the ARTCC config for {artccId} could not be read");
        }

        ArtccPositionCatalog catalog = await ArtccNeighbourLoader.LoadCatalogAsync(configs, load.Config, ct).ConfigureAwait(false);
        List<ArtccPositionRow> positions = [.. catalog.Positions.DistinctBy(p => p.PositionId, StringComparer.Ordinal)];
        return new PickerData(
            positions,
            UnderlyingAirports(load.Config),
            StripBays(load.Config),
            JoinProblems(load.Problem, catalog.UnreadNeighbours)
        );
    }

    // The load's own problem and, when some neighbouring centers' configs could not be read, which ones; null for neither.
    private static string? JoinProblems(string? loadProblem, IReadOnlyList<string> unreadNeighbours)
    {
        string? neighbours = (unreadNeighbours.Count == 0) ? null : $"positions of {string.Join(", ", unreadNeighbours)} could not be read";
        string[] parts = [.. new[] { loadProblem, neighbours }.OfType<string>()];
        return (parts.Length == 0) ? null : string.Join("; ", parts);
    }

    // The airports the config's STARS areas list, the ones ArtccAirportResolver takes: every facility's, once each
    // (case-insensitively), in order.
    private static List<string> UnderlyingAirports(ArtccConfigRoot config)
    {
        var airports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        WalkFacilities(
            config.Facility,
            facility =>
            {
                foreach (string id in (facility.StarsConfiguration?.Areas ?? []).SelectMany(a => a.UnderlyingAirports))
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        airports.Add(id);
                    }
                }
            }
        );
        return [.. airports.Order()];
    }

    // Visits a facility and then each child facility's tree, in the config's order.
    private static void WalkFacilities(FacilityConfig? facility, Action<FacilityConfig> visit)
    {
        if (facility is null)
        {
            return;
        }

        visit(facility);
        foreach (FacilityConfig child in facility.ChildFacilities)
        {
            WalkFacilities(child, visit);
        }
    }

    private void ApplyPickers(PickerData data)
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<ArtccPositionRow> positions = data.Positions ?? [];
        _catalogOptions = [.. positions.Select(p => new ScenarioPickOption(p.PositionId, $"{p.Callsign} — {p.Name} ({p.FacilityId})"))];
        _catalogRows = positions.ToDictionary(p => p.PositionId, StringComparer.Ordinal);
        _bayOptions = data.Bays ?? [];
        IsStudentPositionFreeText = data.Positions is null;
        IsPrimaryAirportFreeText = data.Airports is null;
        _configAirports = data.Airports ?? [];
        ConfigProblem = data.Problem;
        RebuildOptions();
        RefreshSummaries();
    }

    // Runs the action through the post seam and finishes when it has run.
    private Task PostAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Post(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    private static void LogFailure(Exception ex, string what)
    {
        if (Log.IsEnabled(LogLevel.Warning))
        {
            Log.LogWarning(ex, "Scenario editor: {What} failed", what);
        }
    }

    // What one config load gives the pickers: null lists when the config could not be read, and the load's problem.
    private sealed record PickerData(
        IReadOnlyList<ArtccPositionRow>? Positions,
        IReadOnlyList<string>? Airports,
        IReadOnlyList<ScenarioPickOption>? Bays,
        string? Problem
    );
}
