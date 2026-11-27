using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// Find Route for one aircraft. The query comes from the aircraft: the flight plan's departure and destination (shown,
/// not editable), the runway of a runway start (none otherwise), the filed type's equipment suffix after "/" (none without
/// one), the callsign's airline prefix when it matches ^[A-Z]{3}[0-9] (none otherwise) and the aircraft's type; the
/// runway, suffix, airline and type can be edited and searched again. The search runs <see cref="RouteFinder"/> off the
/// UI thread and shows its routes on it, in the finder's order, with a pill per source (its route count, or that it
/// failed) and one line per failed source; routes only navdata offers show the first <see cref="NavdataFirstPage"/> until
/// Show all is pressed. The selected route shows the cruise altitude Apply would file and, when it has an observed sample,
/// the callsign and type Apply can also write. Apply hands the choice to the aircraft editor, which writes it as one step.
/// </summary>
public sealed partial class RouteFinderViewModel : ObservableObject, IDisposable
{
    /// <summary>How many routes only navdata offers show before Show all is pressed.</summary>
    public const int NavdataFirstPage = 10;

    /// <summary>The lowest flight number a real callsign is given.</summary>
    public const int FirstFlightNumber = 100;

    /// <summary>The highest flight number a real callsign is given.</summary>
    public const int LastFlightNumber = 9999;

    // Random flight numbers tried before the free numbers are searched in order.
    private const int RandomDraws = 64;

    // What a field with no value shows.
    private const string NoValue = "\u2014";

    private static readonly ILogger Log = AppLog.CreateLogger<RouteFinderViewModel>();

    private static readonly char[] RunwaySeparators = [' ', ','];

    private readonly RouteFinderTarget _target;
    private readonly RouteFinder _finder;
    private readonly IReadOnlyList<string> _sourceNames;
    private readonly Action<Action> _post;
    private readonly Action<RouteFinderChoice> _apply;
    private CancellationTokenSource? _search;
    private IReadOnlyList<FoundRoute> _found = [];

    // The route the real callsign was last drawn for, so a row rebuilt for the same route keeps its callsign.
    private FoundRoute? _drawnFor;

    [ObservableProperty]
    private RouteQuery _query;

    [ObservableProperty]
    private string _runwayInput;

    [ObservableProperty]
    private string _suffixInput;

    [ObservableProperty]
    private string _airlineInput;

    [ObservableProperty]
    private string _typeInput;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private RouteFinderRowViewModel? _selectedRoute;

    [ObservableProperty]
    private bool _showAllNavdata;

    [ObservableProperty]
    private bool _useRealCallsignAndType;

    [ObservableProperty]
    private bool _isSearching = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenNavdata))]
    private int _hiddenNavdataCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllNavdataText))]
    private int _navdataRouteCount;

    [ObservableProperty]
    private string _cruisePreview = NoValue;

    [ObservableProperty]
    private string _cruiseCaption = "";

    [ObservableProperty]
    private string? _drawnCallsign;

    [ObservableProperty]
    private string? _observedType;

    [ObservableProperty]
    private bool _canUseRealCallsignAndType;

    [ObservableProperty]
    private string _realCallsignText = "";

    /// <summary>
    /// Initializes a new instance of the <see cref="RouteFinderViewModel"/> class with the aircraft's query in the
    /// boxes; nothing is searched until <see cref="StartSearch"/>.
    /// </summary>
    /// <param name="target">The aircraft, how its real callsign is drawn and the magnetic model's date.</param>
    /// <param name="sources">The route sources to search, in source order.</param>
    /// <param name="post">Runs an action on the UI thread.</param>
    /// <param name="apply">Writes the chosen route to the aircraft.</param>
    public RouteFinderViewModel(RouteFinderTarget target, IReadOnlyList<IRouteSource> sources, Action<Action> post, Action<RouteFinderChoice> apply)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sources);
        _target = target;
        _finder = new RouteFinder(sources, NavigationDatabase.Instance);
        _sourceNames = [.. sources.Select(s => s.Name)];
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _query = QueryFor(target.Aircraft);
        _runwayInput = string.Join(" ", _query.DepartureRunways);
        _suffixInput = (_query.EquipmentSuffix is { } suffix) ? "/" + suffix : "";
        _airlineInput = _query.AirlinePrefix ?? "";
        _typeInput = _query.AircraftType ?? "";
        UpdateCruisePreview();
    }

    /// <summary>Raised after Apply handed the chosen route to the aircraft editor.</summary>
    public event EventHandler? Applied;

    /// <summary>Raised when Cancel or the title's close button asks the flyout to close without writing.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Gets the aircraft's callsign.</summary>
    public string Callsign => _target.Aircraft.AircraftId ?? "";

    /// <summary>Gets the flyout's title: Find Route and the callsign.</summary>
    public string Title => "Find Route \u00b7 " + Callsign;

    /// <summary>Gets the departure airport, from the flight plan.</summary>
    public string DepartureText => Shown(Query.Departure);

    /// <summary>Gets the destination airport, from the flight plan.</summary>
    public string DestinationText => Shown(Query.Destination);

    /// <summary>Gets the line under the query saying where its fields came from.</summary>
    public string QueryCaption =>
        "From the flight plan, the start runway, the filed type "
        + Shown(_target.Aircraft.FlightPlan?.AircraftType)
        + " and the airline of callsign "
        + Shown(Callsign)
        + "; each can be overridden.";

    /// <summary>Gets the Show all button's text: how many routes only navdata offers.</summary>
    public string ShowAllNavdataText => string.Create(CultureInfo.InvariantCulture, $"Show all {NavdataRouteCount} navdata routes");

    /// <summary>Gets a value indicating whether routes only navdata offers are left out of the list.</summary>
    public bool HasHiddenNavdata => HiddenNavdataCount > 0;

    /// <summary>Gets the routes shown, in the finder's order.</summary>
    public ObservableCollection<RouteFinderRowViewModel> Routes { get; } = [];

    /// <summary>Gets one line per source that failed: its name and its message.</summary>
    public ObservableCollection<string> Failures { get; } = [];

    /// <summary>Gets one pill per source, in source order: its route count, or that it failed.</summary>
    public ObservableCollection<RouteSourcePill> SourcePills { get; } = [];

    /// <summary>Gets the running search; it completes once its routes (or its failure) show.</summary>
    public Task SearchTask { get; private set; } = Task.CompletedTask;

    /// <summary>The query an aircraft makes (see the class summary).</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <returns>The query.</returns>
    public static RouteQuery QueryFor(ScenarioAircraft aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ScenarioFlightPlan? plan = aircraft.FlightPlan;
        StartingConditions? start = aircraft.StartingConditions;
        string? runway = string.Equals(start?.Type, StartingConditionKinds.OnRunway, StringComparison.Ordinal) ? Blank(start?.Runway) : null;
        return new RouteQuery(
            (plan?.Departure ?? "").Trim(),
            (plan?.Destination ?? "").Trim(),
            (runway is null) ? [] : [runway],
            [],
            SuffixOf(plan?.AircraftType),
            AirlinePrefix: AirlinePrefixOf(aircraft.AircraftId),
            AircraftType: Blank(aircraft.AircraftType)
        );
    }

    /// <summary>The equipment suffix of a filed type: the text after its "/", upper-cased; null without one.</summary>
    /// <param name="filedType">The filed type text, e.g. "B738/L".</param>
    /// <returns>The suffix without its slash, e.g. "L".</returns>
    public static string? SuffixOf(string? filedType)
    {
        if (filedType is null)
        {
            return null;
        }

        int slash = filedType.IndexOf('/', StringComparison.Ordinal);
        return (slash < 0) ? null : Blank(filedType[(slash + 1)..])?.ToUpperInvariant();
    }

    /// <summary>The airline prefix of a callsign that matches ^[A-Z]{3}[0-9] (trimmed and upper-cased first); null otherwise.</summary>
    /// <param name="callsign">The callsign, e.g. "SWA2190".</param>
    /// <returns>The three-letter prefix, e.g. "SWA".</returns>
    public static string? AirlinePrefixOf(string? callsign)
    {
        string text = (callsign ?? "").Trim().ToUpperInvariant();
        return AirlineCallsign().IsMatch(text) ? text[..3] : null;
    }

    /// <summary>
    /// What Apply writes to an aircraft: the route always; the cruise altitude only when the plan's is empty (0), as
    /// <see cref="FiledCruiseAltitude.Pick"/> files it for the type the aircraft will have, from the airports' navdata
    /// positions and field elevations (none when either airport is unknown or Pick gives none); and, when the choice asks
    /// for a real callsign and type and the route has an observed sample, the choice's drawn callsign (when there is one)
    /// and the sample's type (when it has one). The departure, destination and filed type are never written.
    /// </summary>
    /// <param name="aircraft">The aircraft as the document holds it now.</param>
    /// <param name="choice">The chosen route, the real-callsign choice and the drawn callsign.</param>
    /// <param name="magneticModelDateUtc">The date the direction of flight's magnetic model is evaluated at.</param>
    /// <returns>The write.</returns>
    public static RouteFinderWrite WriteFor(ScenarioAircraft aircraft, RouteFinderChoice choice, DateTime magneticModelDateUtc)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        ArgumentNullException.ThrowIfNull(choice);
        ObservedRouteSample? sample = choice.UseRealCallsignAndType ? choice.Route.Observed : null;
        string? callsign = (sample is null) ? null : Blank(choice.Callsign);
        string? type = Blank(sample?.AircraftType)?.ToUpperInvariant();
        int? cruise =
            (aircraft.FlightPlan is { CruiseAltitude: 0 } plan)
                ? CruiseFor(type ?? aircraft.AircraftType, plan, choice.Route.Preferred?.Altitude, magneticModelDateUtc)
                : null;
        return new RouteFinderWrite(choice.Route.RouteText, cruise, callsign, type);
    }

    /// <summary>
    /// A callsign of the prefix and a flight number from <see cref="FirstFlightNumber"/> to <see cref="LastFlightNumber"/>
    /// that no aircraft of the scenario has (compared ignoring case): drawn from the random source, else the lowest free
    /// one; null when every number is taken.
    /// </summary>
    /// <param name="scenario">The scenario.</param>
    /// <param name="prefix">The airline prefix, e.g. "SWA".</param>
    /// <param name="random">The random source.</param>
    /// <returns>The callsign, e.g. "SWA4321".</returns>
    public static string? FreeCallsign(Scenario scenario, string prefix, Random random)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(random);
        var used = new HashSet<string>(scenario.Aircraft.Select(a => (a.AircraftId ?? "").Trim()), StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < RandomDraws; i++)
        {
            string drawn = CallsignOf(prefix, random.Next(FirstFlightNumber, LastFlightNumber + 1));
            if (!used.Contains(drawn))
            {
                return drawn;
            }
        }

        return Enumerable
            .Range(FirstFlightNumber, LastFlightNumber - FirstFlightNumber + 1)
            .Select(n => CallsignOf(prefix, n))
            .FirstOrDefault(c => !used.Contains(c));
    }

    /// <summary>Starts a search over <see cref="Query"/>, cancelling one still running; its routes show on the UI thread.</summary>
    /// <returns>The search, also kept as <see cref="SearchTask"/>.</returns>
    public Task StartSearch()
    {
        _search?.Cancel();
        _search = new CancellationTokenSource();
        IsSearching = true;
        SearchTask = SearchAsync(Query, _search.Token);
        return SearchTask;
    }

    /// <summary>Cancels a search still running.</summary>
    public void Dispose() => _search?.Cancel();

    partial void OnShowAllNavdataChanged(bool value) => ShowRows();

    partial void OnUseRealCallsignAndTypeChanged(bool value) => UpdateCruisePreview();

    partial void OnQueryChanged(RouteQuery value)
    {
        OnPropertyChanged(nameof(DepartureText));
        OnPropertyChanged(nameof(DestinationText));
    }

    // Another route: its real callsign drawn afresh (the free-number rule), its type, and the cruise altitude Apply would
    // file. A row rebuilt for the same route keeps them, and the null a list clear passes on leaves them (ShowRows resets
    // them when the list ends empty).
    partial void OnSelectedRouteChanged(RouteFinderRowViewModel? value)
    {
        if ((value is null) || (ReferenceEquals(value.Route, _drawnFor)))
        {
            return;
        }

        _drawnFor = value.Route;
        ObservedRouteSample? sample = value?.Route.Observed;
        string? prefix = Blank(sample?.AirlinePrefix)?.ToUpperInvariant();
        DrawnCallsign = ((prefix is not null) && BarePrefix().IsMatch(prefix)) ? _target.DrawCallsign(prefix) : null;
        ObservedType = Blank(sample?.AircraftType)?.ToUpperInvariant();
        CanUseRealCallsignAndType = (sample is not null) && ((DrawnCallsign is not null) || (ObservedType is not null));
        RealCallsignText = RealCallsignLabel(DrawnCallsign, ObservedType);
        UpdateCruisePreview();
    }

    /// <summary>Searches again with the edited runway, suffix, airline and type.</summary>
    [RelayCommand]
    private void Search()
    {
        string[] runways = RunwayInput.Split(RunwaySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Query = Query with
        {
            DepartureRunways = [.. runways.Select(r => r.ToUpperInvariant())],
            EquipmentSuffix = Blank(SuffixInput.Trim().TrimStart('/'))?.ToUpperInvariant(),
            AirlinePrefix = Blank(AirlineInput)?.ToUpperInvariant(),
            AircraftType = Blank(TypeInput)?.ToUpperInvariant(),
        };
        StartSearch();
    }

    /// <summary>Shows every route navdata offers.</summary>
    [RelayCommand]
    private void ShowAllNavdataRoutes() => ShowAllNavdata = true;

    /// <summary>Hands the selected route, the real-callsign choice and the drawn callsign to the aircraft editor.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (SelectedRoute is not { } row)
        {
            return;
        }

        _apply(new RouteFinderChoice(row.Route, UseRealCallsignAndType && CanUseRealCallsignAndType, DrawnCallsign));
        Applied?.Invoke(this, EventArgs.Empty);
    }

    private bool CanApply() => SelectedRoute is not null;

    /// <summary>Closes the flyout without writing.</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // The finder off the UI thread, its result shown on it; a cancelled search shows nothing, a failed one its message.
    private async Task SearchAsync(RouteQuery query, CancellationToken ct)
    {
        RouteFindResult result;
        try
        {
            result = await Task.Run(() => _finder.FindAsync(query, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (Log.IsEnabled(LogLevel.Warning))
            {
                Log.LogWarning(ex, "Find Route failed for {Callsign}", Callsign);
            }

            await OnUiAsync(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        ShowFailure(ex.Message);
                    }
                })
                .ConfigureAwait(false);
            return;
        }

        // A search replaced by a newer one (or closed) while its result was on the way shows nothing.
        await OnUiAsync(() =>
            {
                if (!ct.IsCancellationRequested)
                {
                    Show(result);
                }
            })
            .ConfigureAwait(false);
    }

    // Runs the action on the UI thread and completes once it has run.
    private Task OnUiAsync(Action action)
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            try
            {
                action();
            }
            finally
            {
                shown.SetResult();
            }
        });
        return shown.Task;
    }

    private void Show(RouteFindResult result)
    {
        Failures.Clear();
        foreach (RouteSourceFailure failure in result.Failures)
        {
            Failures.Add(failure.Source + ": " + failure.Message);
        }

        _found = result.Routes;
        NavdataRouteCount = _found.Count(IsNavdataOnly);
        ShowSourcePills(result.Failures);
        ShowRows();
        IsSearching = false;
    }

    private void ShowFailure(string message)
    {
        Failures.Clear();
        Failures.Add("Find Route failed: " + message);
        _found = [];
        NavdataRouteCount = 0;
        SourcePills.Clear();
        ShowRows();
        IsSearching = false;
    }

    private void ShowSourcePills(IReadOnlyList<RouteSourceFailure> failures)
    {
        SourcePills.Clear();
        foreach (string name in _sourceNames)
        {
            bool failed = failures.Any(f => string.Equals(f.Source, name, StringComparison.Ordinal));
            int count = _found.Count(r => r.Sources.Contains(name, StringComparer.Ordinal));
            string text = (failed && (count == 0)) ? name + " failed" : name + " " + count.ToString(CultureInfo.InvariantCulture);
            SourcePills.Add(new RouteSourcePill(name, text, failed));
        }
    }

    // The found routes, those only navdata offers past the first page left out unless Show all is on; the selected route
    // stays selected while it shows, else the first row is.
    private void ShowRows()
    {
        FoundRoute? selected = SelectedRoute?.Route;
        Routes.Clear();
        int navdataShown = 0;
        int hidden = 0;
        foreach (FoundRoute route in _found)
        {
            if (IsNavdataOnly(route))
            {
                if ((!ShowAllNavdata) && (navdataShown >= NavdataFirstPage))
                {
                    hidden++;
                    continue;
                }

                navdataShown++;
            }

            Routes.Add(new RouteFinderRowViewModel(route));
        }

        HiddenNavdataCount = hidden;
        SelectedRoute = Routes.FirstOrDefault(r => ReferenceEquals(r.Route, selected)) ?? Routes.FirstOrDefault();
        if (SelectedRoute is null)
        {
            ClearRealCallsign();
        }
    }

    private void ClearRealCallsign()
    {
        _drawnFor = null;
        DrawnCallsign = null;
        ObservedType = null;
        CanUseRealCallsignAndType = false;
        RealCallsignText = "";
        UpdateCruisePreview();
    }

    // The cruise altitude Apply would leave: "Keeps" a set one; Pick's value for the selected route and the type the
    // aircraft will have when it is empty; a dash with no plan, no route or no value.
    private void UpdateCruisePreview()
    {
        ScenarioFlightPlan? plan = _target.Aircraft.FlightPlan;
        if (plan is null)
        {
            (CruisePreview, CruiseCaption) = (NoValue, "");
            return;
        }

        if (plan.CruiseAltitude != 0)
        {
            string set = plan.CruiseAltitude.ToString(CultureInfo.InvariantCulture);
            (CruisePreview, CruiseCaption) = ("Keeps " + set, "The flight plan already says " + set + "; it is kept.");
            return;
        }

        string type = ((UseRealCallsignAndType) && (ObservedType is { } observed)) ? observed : _target.Aircraft.AircraftType;
        int? cruise = (SelectedRoute is { } row) ? CruiseFor(type, plan, row.Route.Preferred?.Altitude, _target.MagneticModelDateUtc) : null;
        CruisePreview = cruise?.ToString(CultureInfo.InvariantCulture) ?? NoValue;
        CruiseCaption = (cruise is null) ? "" : "From the chosen route; the flight plan has none.";
    }

    private static string RealCallsignLabel(string? callsign, string? type) =>
        (callsign, type) switch
        {
            ({ } c, { } t) => "Also apply callsign " + c + " and type " + t,
            ({ } c, null) => "Also apply callsign " + c,
            (null, { } t) => "Also apply type " + t,
            _ => "",
        };

    private static bool IsNavdataOnly(FoundRoute route) =>
        (route.Sources.Count == 1) && (string.Equals(route.Sources[0], NavdataRouteSource.SourceName, StringComparison.Ordinal));

    private static int? CruiseFor(string aircraftType, ScenarioFlightPlan plan, string? preferredAltitude, DateTime magneticModelDateUtc)
    {
        if ((AirportOf(plan.Departure) is not { } departure) || (AirportOf(plan.Destination) is not { } destination))
        {
            return null;
        }

        return FiledCruiseAltitude.Pick((aircraftType ?? "").Trim(), departure, destination, preferredAltitude, magneticModelDateUtc);
    }

    private static CruiseAirport? AirportOf(string? id)
    {
        if (Blank(id) is not { } airport)
        {
            return null;
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        if ((navDb.GetAirportPosition(airport) is not { } position) || (navDb.GetAirportElevation(airport) is not { } elevation))
        {
            return null;
        }

        return new CruiseAirport(new LatLon(position.Lat, position.Lon), elevation);
    }

    private static string CallsignOf(string prefix, int number) => prefix + number.ToString(CultureInfo.InvariantCulture);

    private static string Shown(string? text) => Blank(text) ?? NoValue;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [GeneratedRegex("^[A-Z]{3}[0-9]")]
    private static partial Regex AirlineCallsign();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex BarePrefix();
}

/// <summary>The aircraft Find Route was opened for, how its real callsign is drawn, and the magnetic model's date.</summary>
/// <param name="Aircraft">The aircraft as the document held it when Find Route opened.</param>
/// <param name="DrawCallsign">Draws a free callsign of an airline prefix (<see cref="RouteFinderViewModel.FreeCallsign"/>).</param>
/// <param name="MagneticModelDateUtc">The date the direction of flight's magnetic model is evaluated at.</param>
public sealed record RouteFinderTarget(ScenarioAircraft Aircraft, Func<string, string?> DrawCallsign, DateTime MagneticModelDateUtc);

/// <summary>The route Apply hands to the aircraft editor, whether a real callsign and type go with it, and the drawn callsign.</summary>
/// <param name="Route">The chosen route.</param>
/// <param name="UseRealCallsignAndType">Whether the real-callsign box is checked (and shown).</param>
/// <param name="Callsign">The callsign drawn for the route's observed prefix, or null when there is none.</param>
public sealed record RouteFinderChoice(FoundRoute Route, bool UseRealCallsignAndType, string? Callsign);

/// <summary>One source's pill in the Sources bar.</summary>
/// <param name="Source">The source's name.</param>
/// <param name="Text">Its name and route count, or its name and "failed".</param>
/// <param name="Failed">Whether it failed.</param>
public sealed record RouteSourcePill(string Source, string Text, bool Failed);

/// <summary>What Apply writes to the aircraft (<see cref="RouteFinderViewModel.WriteFor"/>); a null field is left alone.</summary>
/// <param name="Route">The route, always written.</param>
/// <param name="CruiseAltitude">The cruise altitude, written only to a plan whose cruise altitude is empty.</param>
/// <param name="Callsign">The callsign.</param>
/// <param name="AircraftType">The aircraft's type (not the filed type).</param>
public sealed record RouteFinderWrite(string Route, int? CruiseAltitude, string? Callsign, string? AircraftType)
{
    /// <summary>Writes this to an aircraft.</summary>
    /// <param name="aircraft">The aircraft.</param>
    public void ApplyTo(ScenarioAircraft aircraft)
    {
        ArgumentNullException.ThrowIfNull(aircraft);
        if (aircraft.FlightPlan is { } plan)
        {
            plan.Route = Route;
            if ((CruiseAltitude is { } cruise) && (plan.CruiseAltitude == 0))
            {
                plan.CruiseAltitude = cruise;
            }
        }

        if (Callsign is { } callsign)
        {
            aircraft.AircraftId = callsign;
        }

        if (AircraftType is { } type)
        {
            aircraft.AircraftType = type;
        }
    }
}

/// <summary>One route Find Route shows: its text, a badge per source, its count and its notes.</summary>
/// <param name="route">The route.</param>
public sealed class RouteFinderRowViewModel(FoundRoute route)
{
    /// <summary>Gets the route.</summary>
    public FoundRoute Route { get; } = route ?? throw new ArgumentNullException(nameof(route));

    /// <summary>Gets the route text.</summary>
    public string RouteText => Route.RouteText;

    /// <summary>Gets the names of the sources that offered it, in source order, one badge each.</summary>
    public IReadOnlyList<string> Sources => Route.Sources;

    /// <summary>Gets how many flights filed it, or a dash for a route no flight was seen filing.</summary>
    public string CountText => (Route.Count > 0) ? Route.Count.ToString(CultureInfo.InvariantCulture) : "\u2014";

    /// <summary>Gets its notes, each led by its source: the published SID and STAR, the preferred route, the observed flights.</summary>
    public IReadOnlyList<string> Notes { get; } = NotesOf(route);

    /// <summary>Gets its notes on one line, joined with " · ".</summary>
    public string NoteText => string.Join(" \u00b7 ", Notes);

    /// <summary>Gets a value indicating whether it has a note.</summary>
    public bool HasNotes => Notes.Count > 0;

    private static List<string> NotesOf(FoundRoute route)
    {
        List<string> notes = [];
        if (route.Published is { } published)
        {
            notes.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{NavdataRouteSource.SourceName}: {published.Sid} to {published.Star}, {published.LengthNm:0} NM"
                )
            );
        }

        if (route.Preferred is { } preferred)
        {
            string?[] parts = [preferred.Area, preferred.Altitude, preferred.Aircraft, preferred.Hours, preferred.Direction];
            string rest = string.Concat(parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => ", " + p));
            notes.Add("preferred: " + preferred.Type + " route" + rest);
        }

        if (route.Observed is { } observed)
        {
            notes.Add(RouteHistorySource.SourceName + ": most often " + ObservedText(observed));
        }

        return notes;
    }

    // "SWA (B738)", "SWA", "a B738", or "no airline" for a sample that names neither.
    private static string ObservedText(ObservedRouteSample observed) =>
        (Blank(observed.AirlinePrefix), Blank(observed.AircraftType)) switch
        {
            ({ } prefix, { } type) => prefix + " (" + type + ")",
            ({ } prefix, null) => prefix,
            (null, { } type) => "a " + type,
            _ => "no airline",
        };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
