using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Authoring.Stamping;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>What Apply Route reads besides the Aircraft tab: the profile and route stores, navdata, its seed source and its clock.</summary>
/// <param name="Profiles">The stamp profile store.</param>
/// <param name="Library">The route library.</param>
/// <param name="NavDb">The navigation database a library route is resolved against; null while it is not loaded.</param>
/// <param name="Seeds">The source each Preview draws its pool and id seeds from.</param>
/// <param name="Clock">The clock each Preview reads the time new preset ids are made at from.</param>
public sealed record ApplyRouteServices(
    StampProfileStore Profiles,
    RouteLibrary Library,
    Func<NavigationDatabase?> NavDb,
    Random Seeds,
    TimeProvider Clock
);

/// <summary>
/// Apply Route for the aircraft selected in the Aircraft tab: pick a stamp profile and a route (a library route, or one Find
/// Route found), preview every field the stamp would change with its warnings, and apply it as one undo step. Preview draws
/// its seeds and time once and Apply re-stamps the aircraft as the document holds it then with the same seeds and time, so
/// Apply writes what Preview showed. Any change of the selection, the profile or the route clears the preview, and Apply is
/// enabled only after a Preview of the aircraft still selected that was not skipped.
/// </summary>
public sealed partial class ApplyRouteViewModel : ObservableObject, IDisposable
{
    private const string ErrorPrefix = "Error:";

    private static readonly ILogger Log = AppLog.CreateLogger<ApplyRouteViewModel>();

    private readonly ScenarioAircraftViewModel _aircraft;
    private readonly ApplyRouteServices _services;
    private readonly string _artccId;

    // The last Preview that Apply may write: its aircraft, seeds and time; null when there is none.
    private StampSeeds? _preview;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private StampProfile? _selectedProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private SavedRoute? _selectedRoute;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private StampRoute? _foundRoute;

    [ObservableProperty]
    private bool _showAllArtccs;

    [ObservableProperty]
    private string? _routeProblem;

    [ObservableProperty]
    private string? _skipText;

    [ObservableProperty]
    private string? _nothingText;

    /// <summary>Initializes a new instance of the <see cref="ApplyRouteViewModel"/> class with the profiles and routes listed.</summary>
    /// <param name="aircraft">The Aircraft tab whose selected aircraft is stamped and which writes the stamp.</param>
    /// <param name="services">The stores, navdata, seed source and clock.</param>
    /// <param name="artccId">The open scenario's ARTCC, whose routes are listed unless <see cref="ShowAllArtccs"/> is on.</param>
    public ApplyRouteViewModel(ScenarioAircraftViewModel aircraft, ApplyRouteServices services, string artccId)
    {
        _aircraft = aircraft ?? throw new ArgumentNullException(nameof(aircraft));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _artccId = artccId ?? throw new ArgumentNullException(nameof(artccId));
        Editor = new StampProfileEditorViewModel(_services.Profiles, PositionIdSnapshot());
        Editor.Saved += OnEditorSaved;
        Editor.Deleted += OnEditorDeleted;
        _aircraft.PropertyChanged += OnAircraftPropertyChanged;
        LoadProfiles();
        LoadRoutes();
    }

    /// <summary>Gets the profile section's editor over the same profile store, holding the selected profile.</summary>
    public StampProfileEditorViewModel Editor { get; }

    /// <summary>Gets the readable stamp profiles, by name.</summary>
    public ObservableCollection<StampProfile> Profiles { get; } = [];

    /// <summary>Gets one "Couldn't read {file}: {reason}" line per profile file that is not readable.</summary>
    public ObservableCollection<string> ProfileProblems { get; } = [];

    /// <summary>Gets the library routes of the scenario's ARTCC, or of every ARTCC with <see cref="ShowAllArtccs"/> on.</summary>
    public ObservableCollection<SavedRoute> Routes { get; } = [];

    /// <summary>Gets the fields the last Preview would change, in document order.</summary>
    public ObservableCollection<ApplyRoutePreviewRow> PreviewRows { get; } = [];

    /// <summary>Gets the last Preview's warnings, every line starting "Error:" first.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>Lists the profiles again from the store, keeping the selected one when it is still there.</summary>
    public void LoadProfiles()
    {
        string? selectedId = SelectedProfile?.Id;
        StampProfileListing listing = _services.Profiles.List();
        Profiles.Clear();
        foreach (StampProfile profile in listing.Profiles)
        {
            Profiles.Add(profile);
        }

        ProfileProblems.Clear();
        foreach (StoreProblem problem in listing.Problems)
        {
            ProfileProblems.Add($"Couldn't read {problem.FileName}: {problem.Reason}");
        }

        SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.Id, selectedId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Lists the library routes again, keeping the selected one when it is still listed.</summary>
    public void LoadRoutes()
    {
        string? selectedId = SelectedRoute?.Id;
        RouteLibraryListing listing = _services.Library.List(ShowAllArtccs ? null : _artccId);
        Routes.Clear();
        foreach (SavedRoute route in listing.Routes)
        {
            Routes.Add(route);
        }

        if (selectedId is not null)
        {
            SelectedRoute = Routes.FirstOrDefault(r => string.Equals(r.Id, selectedId, StringComparison.Ordinal));
        }
    }

    /// <summary>Stamps the route Find Route found, in place of a library route.</summary>
    /// <param name="found">The found route.</param>
    public void UseFoundRoute(FoundRoute found) => FoundRoute = StampRoute.FromFoundRoute(found);

    /// <summary>
    /// Previews the stamp on the selected aircraft: its change rows, its warnings, or why it was skipped, or why the library
    /// route cannot be used. Draws the pool and id seeds Apply then reuses; never changes the document.
    /// </summary>
    [RelayCommand]
    public void Preview()
    {
        ClearPreview();
        if ((_aircraft.SelectedForStamp() is not { } target) || (SelectedProfile is not { } profile))
        {
            return;
        }

        (StampRoute? route, string? problem) = RouteFor(target.Aircraft);
        if (route is null)
        {
            RouteProblem = problem;
            return;
        }

        var seeds = new StampSeeds(target.Aircraft.Id, _services.Seeds.Next(), _services.Seeds.Next(), _services.Clock.GetUtcNow());
        StampResult result = Stamp(target.Index, target.Aircraft, route, profile, seeds);
        Show(result);
        _preview = ((result.SkipReason is null) && (result.Changes.Count > 0)) ? seeds : null;
        ApplyCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Editor.Saved -= OnEditorSaved;
        Editor.Deleted -= OnEditorDeleted;
        _aircraft.PropertyChanged -= OnAircraftPropertyChanged;
    }

    // Writes the stamp of the previewed aircraft, re-run on it as the document holds it now with the preview's seeds and time.
    // A skip, a stamp that changes nothing or a route that no longer converts found at this point is shown in place of the preview, never dropped.
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (WriteInputs() is not { } inputs)
        {
            return;
        }

        StampResult result = Stamp(inputs.Index, inputs.Aircraft, inputs.Route, inputs.Profile, inputs.Seeds);
        ClearPreview();
        if ((result.SkipReason is not null) || (result.Changes.Count == 0))
        {
            Show(result);
            if (Log.IsEnabled(LogLevel.Information))
            {
                Log.LogInformation(
                    "Apply Route wrote nothing to {Callsign}: {Reason}",
                    inputs.Aircraft.AircraftId,
                    result.SkipReason ?? "the stamp changes nothing"
                );
            }

            return;
        }

        _aircraft.ApplyStamp(inputs.Index, result.Aircraft);
    }

    // What Apply stamps: the previewed aircraft as the document holds it now, its route, the profile and the kept seeds;
    // null (refused and logged, or with the route's problem shown) when any of them is missing.
    private WriteInput? WriteInputs()
    {
        if (!CanApply() || (_preview is not { } seeds) || (SelectedProfile is not { } profile))
        {
            return null;
        }

        if ((_aircraft.SelectedForStamp() is not { } target) || !string.Equals(target.Aircraft.Id, seeds.AircraftId, StringComparison.Ordinal))
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning("Apply Route refused: the selected aircraft is no longer {Id}", seeds.AircraftId);
            }

            return null;
        }

        (StampRoute? route, string? problem) = RouteFor(target.Aircraft);
        if (route is null)
        {
            ClearPreview();
            RouteProblem = problem;
            return null;
        }

        return new WriteInput(target.Index, target.Aircraft, route, profile, seeds);
    }

    private bool CanApply() =>
        (_preview is { } seeds)
        && (SelectedProfile is not null)
        && ((SelectedRoute is not null) || (FoundRoute is not null))
        && string.Equals(_aircraft.Selected?.Id, seeds.AircraftId, StringComparison.Ordinal);

    // The route to stamp on the aircraft: the found route, else the library route resolved against its flight plan's
    // airports; a null route with the text to show for a library route that does not convert, or with none when there is no route.
    private (StampRoute? Route, string? Problem) RouteFor(ScenarioAircraft aircraft)
    {
        if (FoundRoute is { } found)
        {
            return (found, null);
        }

        if (SelectedRoute is not { } saved)
        {
            return (null, null);
        }

        ScenarioFlightPlan? plan = aircraft.FlightPlan;
        StampRouteConversion conversion = StampRoute.FromSavedRoute(
            saved,
            new FixResolutionContext(plan?.Destination, plan?.Departure),
            _services.NavDb()
        );
        if (conversion.Failure is not { } failure)
        {
            return (conversion.Route, null);
        }

        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation("Apply Route: route {Route} cannot be used for {Callsign}: {Failure}", saved.Name, aircraft.AircraftId, failure);
        }

        return (null, $"Can't use this route: {failure}");
    }

    private StampResult Stamp(int index, ScenarioAircraft aircraft, StampRoute route, StampProfile profile, StampSeeds seeds)
    {
        var context = new StampContext(
            seeds.Now,
            new Random(seeds.PoolSeed),
            new Random(seeds.IdSeed),
            _aircraft.CallsignsExcept(index),
            MagneticDeclination.EvaluationDateUtc
        );
        return RouteStamp.Apply(aircraft, route, profile, context);
    }

    private void Show(StampResult result)
    {
        if (result.SkipReason is { } reason)
        {
            SkipText = $"Skipped: {reason}";
            return;
        }

        if (result.Changes.Count == 0)
        {
            NothingText = $"Nothing to change: this profile and route already match {result.Aircraft.AircraftId}.";
        }

        foreach (StampChange change in result.Changes)
        {
            PreviewRows.Add(ApplyRoutePreviewRow.From(change));
        }

        foreach (string warning in result.Warnings.OrderBy(w => !w.StartsWith(ErrorPrefix, StringComparison.Ordinal)))
        {
            Warnings.Add(warning);
        }
    }

    private void ClearPreview()
    {
        PreviewRows.Clear();
        Warnings.Clear();
        SkipText = null;
        NothingText = null;
        RouteProblem = null;
        _preview = null;
        ApplyCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedProfileChanged(StampProfile? oldValue, StampProfile? newValue)
    {
        if (!string.Equals(oldValue?.Id, newValue?.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (newValue is null)
            {
                Editor.New();
            }
            else
            {
                Editor.Load(newValue);
            }
        }

        ClearPreview();
    }

    partial void OnSelectedRouteChanged(SavedRoute? value)
    {
        if (value is not null)
        {
            FoundRoute = null;
        }

        ClearPreview();
    }

    partial void OnFoundRouteChanged(StampRoute? value)
    {
        if (value is not null)
        {
            SelectedRoute = null;
        }

        ClearPreview();
    }

    partial void OnShowAllArtccsChanged(bool value) => LoadRoutes();

    // The profile section wrote a profile: a Save (the profile the editor holds) selects that profile and previews it; a
    // Duplicate only lists the copy. The list is read again first either way.
    private void OnEditorSaved(StampProfile saved)
    {
        bool savedTheEditedProfile = string.Equals(saved.Id, Editor.Id, StringComparison.OrdinalIgnoreCase);
        LoadProfiles();
        if (!savedTheEditedProfile)
        {
            return;
        }

        SelectedProfile = Profiles.FirstOrDefault(p => string.Equals(p.Id, saved.Id, StringComparison.OrdinalIgnoreCase));
        Preview();
    }

    // The profile section deleted a profile: the list is read again, which drops the selection when it was that profile.
    private void OnEditorDeleted(string _) => LoadProfiles();

    // The ATC position ids the profile editor's auto-track picker offers, as the General tab's catalog holds them.
    private IReadOnlyList<string> PositionIdSnapshot() => [.. _aircraft.PositionCatalog.Select(o => o.Value)];

    private void OnAircraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ScenarioAircraftViewModel.Selected), StringComparison.Ordinal))
        {
            ClearPreview();
        }
        else if (string.Equals(e.PropertyName, nameof(ScenarioAircraftViewModel.PositionCatalog), StringComparison.Ordinal))
        {
            Editor.PositionIds = PositionIdSnapshot();
        }
    }

    // What a Preview drew: the aircraft it stamped, the pool and id seeds, and the time new preset ids are made at.
    private sealed record StampSeeds(string AircraftId, int PoolSeed, int IdSeed, DateTimeOffset Now);

    // What one Apply stamps: the aircraft and its document index, the route, the profile and the kept seeds.
    private sealed record WriteInput(int Index, ScenarioAircraft Aircraft, StampRoute Route, StampProfile Profile, StampSeeds Seeds);
}
