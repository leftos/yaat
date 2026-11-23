using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The Aircraft tab's detail panel over the selected aircraft: its own fields (callsign, type, transponder, spawn delay,
/// the YAAT-only taxi time and RPO note, airport, approach, difficulty, altitude profile) and its starting conditions, a
/// kind and only the fields <see cref="StartingConditionKinds.FieldsOf"/> lists for it. A text field commits on focus loss
/// or Enter as one undo step "Edit &lt;field&gt;"; a text that is not a value of its field keeps the text, shows the
/// field's error under it and writes nothing. Changing the kind is one undo step "Change start". Every commit that
/// changes the document, and every undo and redo, refreshes the rows and re-shows the selected aircraft's values; a
/// commit that changes nothing re-shows only its own field, so every other field keeps its text and its error.
/// </summary>
public sealed partial class ScenarioAircraftViewModel
{
    /// <summary>The hint under the type field when the type is not an AircraftSpecs designator; the type is still written.</summary>
    public const string NotInAircraftSpecsText = "Not in AircraftSpecs";

    /// <summary>The most types the type field suggests.</summary>
    public const int TypeSuggestionLimit = 20;

    private static readonly IReadOnlyList<ScenarioPickOption> BaseStartKindOptions =
    [
        .. StartingConditionKinds.Kinds.Select(k => new ScenarioPickOption(k, KindLabel(k))),
    ];

    // Each text field by its commit's undo step: how it shows the aircraft's value, its error cleared.
    private static readonly (string Step, Action<ScenarioAircraftViewModel, ScenarioAircraft> Load)[] FieldLoads =
    [
        ("Edit callsign", (vm, a) => vm.Callsign = a.AircraftId),
        ("Edit type", (vm, a) => vm.LoadType(a.AircraftType)),
        ("Edit transponder", (vm, a) => vm.Transponder = a.TransponderMode),
        ("Edit spawn delay", (vm, a) => (vm.SpawnDelay, vm.SpawnDelayError) = (ScenarioFieldText.FormatTime(a.SpawnDelay), null)),
        ("Edit taxi time", (vm, a) => (vm.TaxiTime, vm.TaxiTimeError) = (ExtensionText(a, WorkingCopyExporter.AircraftTaxiSecondsKey), null)),
        ("Edit RPO note", (vm, a) => vm.RpoNote = ExtensionText(a, WorkingCopyExporter.AircraftRpoNoteKey)),
        ("Edit airport", (vm, a) => vm.Airport = a.AirportId ?? ""),
        ("Edit approach", (vm, a) => vm.Approach = a.ExpectedApproach ?? ""),
        ("Edit difficulty", (vm, a) => vm.Difficulty = a.Difficulty ?? ""),
        ("Edit parking", (vm, a) => vm.Parking = StartOf(a).Parking ?? ""),
        ("Edit runway", (vm, a) => vm.Runway = StartOf(a).Runway ?? ""),
        ("Edit latitude", (vm, a) => (vm.Latitude, vm.LatitudeError) = (ScenarioFieldText.OptionalNumber.Format(StartOf(a).Coordinates?.Lat), null)),
        (
            "Edit longitude",
            (vm, a) => (vm.Longitude, vm.LongitudeError) = (ScenarioFieldText.OptionalNumber.Format(StartOf(a).Coordinates?.Lon), null)
        ),
        ("Edit altitude", (vm, a) => (vm.Altitude, vm.AltitudeError) = (ScenarioFieldText.OptionalNumber.Format(StartOf(a).Altitude), null)),
        ("Edit speed", (vm, a) => (vm.Speed, vm.SpeedError) = (ScenarioFieldText.OptionalNumber.Format(StartOf(a).Speed), null)),
        ("Edit heading", (vm, a) => (vm.Heading, vm.HeadingError) = (ScenarioFieldText.OptionalNumber.Format(StartOf(a).Heading), null)),
        ("Edit navigation path", (vm, a) => vm.NavigationPath = StartOf(a).NavigationPath ?? ""),
        ("Edit fix", (vm, a) => vm.Fix = StartOf(a).Fix ?? ""),
        (
            "Edit distance from runway",
            (vm, a) =>
                (vm.DistanceFromRunway, vm.DistanceFromRunwayError) = (ScenarioFieldText.OptionalNumber.Format(StartOf(a).DistanceFromRunway), null)
        ),
    ];

    // The kind picker's one options collection, updated in place; the type field's suggestions, likewise.
    private readonly ObservableCollection<ScenarioPickOption> _startKindOptions = [.. BaseStartKindOptions];
    private readonly ObservableCollection<string> _typeSuggestions = [];
    private IReadOnlyList<string> _airportOptions = [];
    private bool _loadingDetail;

    // While a commit that names its own field reload runs (a preset row's): how a commit that changes nothing re-shows it.
    // Set only for the duration of WithFieldReload's commit() and null at any other time.
    private Action<ScenarioAircraft>? _fieldReload;

    [ObservableProperty]
    private string _callsign = "";

    [ObservableProperty]
    private string _aircraftType = "";

    [ObservableProperty]
    private string? _typeHint;

    [ObservableProperty]
    private string _transponder = "";

    [ObservableProperty]
    private string _spawnDelay = "";

    [ObservableProperty]
    private string? _spawnDelayError;

    [ObservableProperty]
    private string _taxiTime = "";

    [ObservableProperty]
    private string? _taxiTimeError;

    [ObservableProperty]
    private string _rpoNote = "";

    [ObservableProperty]
    private string _airport = "";

    [ObservableProperty]
    private string _approach = "";

    [ObservableProperty]
    private string _difficulty = "";

    [ObservableProperty]
    private bool _onAltitudeProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartKindSelection), nameof(ShowsParking), nameof(ShowsRunway), nameof(ShowsCoordinates))]
    [NotifyPropertyChangedFor(nameof(ShowsAltitude), nameof(ShowsSpeed), nameof(ShowsHeading), nameof(ShowsNavigationPath))]
    [NotifyPropertyChangedFor(nameof(ShowsFix), nameof(ShowsDistanceFromRunway))]
    private string _startKind = "";

    [ObservableProperty]
    private string _parking = "";

    [ObservableProperty]
    private string _runway = "";

    [ObservableProperty]
    private string _latitude = "";

    [ObservableProperty]
    private string? _latitudeError;

    [ObservableProperty]
    private string _longitude = "";

    [ObservableProperty]
    private string? _longitudeError;

    [ObservableProperty]
    private string _altitude = "";

    [ObservableProperty]
    private string? _altitudeError;

    [ObservableProperty]
    private string _speed = "";

    [ObservableProperty]
    private string? _speedError;

    [ObservableProperty]
    private string _heading = "";

    [ObservableProperty]
    private string? _headingError;

    [ObservableProperty]
    private string _navigationPath = "";

    [ObservableProperty]
    private string _fix = "";

    [ObservableProperty]
    private string _distanceFromRunway = "";

    [ObservableProperty]
    private string? _distanceFromRunwayError;

    /// <summary>Gets a value indicating whether the detail panel shows: only while an aircraft is selected.</summary>
    public bool IsDetailVisible => Selected is not null;

    /// <summary>Gets the type field's suggestions: the catalog types whose designator starts with the typed text.</summary>
    public IReadOnlyList<string> TypeSuggestions => _typeSuggestions;

    /// <summary>
    /// Gets or sets the airport field's options: the ARTCC config's airports the General tab offers. The field also takes
    /// free text.
    /// </summary>
    public IReadOnlyList<string> AirportOptions
    {
        get => _airportOptions;
        set => SetProperty(ref _airportOptions, value ?? []);
    }

    /// <summary>Gets the starting-condition kind picker's options; one collection, updated in place.</summary>
    public IReadOnlyList<ScenarioPickOption> StartKindOptions => _startKindOptions;

    /// <summary>Gets or sets the kind picker's selection; only the user's pick of another kind changes the start.</summary>
    public ScenarioPickOption? StartKindSelection
    {
        get => ScenarioFieldText.Selected(_startKindOptions, StartKind);
        set => ScenarioFieldText.Pick(value?.Value, StartKind, v => StartKind = v, CommitStartKind);
    }

    /// <summary>Gets a value indicating whether the kind uses a parking spot.</summary>
    public bool ShowsParking => Uses("parking");

    /// <summary>Gets a value indicating whether the kind uses a runway.</summary>
    public bool ShowsRunway => Uses("runway");

    /// <summary>Gets a value indicating whether the kind uses a latitude and longitude.</summary>
    public bool ShowsCoordinates => Uses("coordinates");

    /// <summary>Gets a value indicating whether the kind uses an altitude.</summary>
    public bool ShowsAltitude => Uses("altitude");

    /// <summary>Gets a value indicating whether the kind uses a speed.</summary>
    public bool ShowsSpeed => Uses("speed");

    /// <summary>Gets a value indicating whether the kind uses a heading.</summary>
    public bool ShowsHeading => Uses("heading");

    /// <summary>Gets a value indicating whether the kind uses a navigation path.</summary>
    public bool ShowsNavigationPath => Uses("navigationPath");

    /// <summary>Gets a value indicating whether the kind uses a fix or FRD.</summary>
    public bool ShowsFix => Uses("fix");

    /// <summary>Gets a value indicating whether the kind uses a distance from the runway.</summary>
    public bool ShowsDistanceFromRunway => Uses("distanceFromRunway");

    /// <summary>The name the kind picker shows for a starting-condition kind; an unknown kind is shown as itself.</summary>
    /// <param name="kind">The <c>startingConditions.type</c> value.</param>
    /// <returns>The display name.</returns>
    public static string KindLabel(string kind) =>
        kind switch
        {
            StartingConditionKinds.Parking => "Parking",
            StartingConditionKinds.OnRunway => "On runway",
            StartingConditionKinds.Coordinates => "Coordinates",
            StartingConditionKinds.FixOrFrd => "Fix or FRD",
            StartingConditionKinds.OnFinal => "On final",
            _ => kind,
        };

    partial void OnSelectedChanged(ScenarioAircraftRowViewModel? value)
    {
        OnPropertyChanged(nameof(IsDetailVisible));
        LoadDetail();
    }

    partial void OnAircraftTypeChanged(string value) =>
        ScenarioFieldText.UpdateInPlace(_typeSuggestions, [.. AircraftTypeCatalog.Search(value, TypeSuggestionLimit).Select(e => e.Designator)]);

    partial void OnOnAltitudeProfileChanged(bool value)
    {
        if (!_loadingDetail)
        {
            ApplyToSelected("Edit altitude profile", a => a.OnAltitudeProfile = value);
        }
    }

    /// <summary>Writes the callsign, trimmed.</summary>
    [RelayCommand]
    private void CommitCallsign() => CommitText("Edit callsign", Callsign, (a, v) => a.AircraftId = v ?? "");

    /// <summary>Writes the type, trimmed; a type not in the catalog is written too, with the hint shown.</summary>
    [RelayCommand]
    private void CommitAircraftType() => CommitText("Edit type", AircraftType, (a, v) => a.AircraftType = v ?? "");

    /// <summary>Writes the transponder mode, trimmed.</summary>
    [RelayCommand]
    private void CommitTransponder() => CommitText("Edit transponder", Transponder, (a, v) => a.TransponderMode = v ?? "");

    /// <summary>Writes the spawn delay; blank is 0.</summary>
    [RelayCommand]
    private void CommitSpawnDelay() =>
        CommitParsed(ScenarioFieldText.Time, SpawnDelay, e => SpawnDelayError = e, "Edit spawn delay", (a, v) => a.SpawnDelay = v);

    /// <summary>Writes the taxi time in seconds, 0 or more, under its YAAT-only key; blank removes the key.</summary>
    [RelayCommand]
    private void CommitTaxiTime() =>
        CommitParsed(
            ScenarioFieldText.OptionalNonNegativeWholeNumber,
            TaxiTime,
            e => TaxiTimeError = e,
            "Edit taxi time",
            (a, v) =>
                SetExtension(a, WorkingCopyExporter.AircraftTaxiSecondsKey, (v is { } seconds) ? JsonSerializer.SerializeToElement(seconds) : null)
        );

    /// <summary>Writes the RPO note under its YAAT-only key; blank removes the key.</summary>
    [RelayCommand]
    private void CommitRpoNote() =>
        CommitText(
            "Edit RPO note",
            RpoNote,
            (a, v) => SetExtension(a, WorkingCopyExporter.AircraftRpoNoteKey, (v is null) ? null : JsonSerializer.SerializeToElement(v))
        );

    /// <summary>Writes the aircraft's airport id; blank writes none.</summary>
    [RelayCommand]
    private void CommitAirport() => CommitText("Edit airport", Airport, (a, v) => a.AirportId = v);

    /// <summary>Writes the expected approach; blank writes none.</summary>
    [RelayCommand]
    private void CommitApproach() => CommitText("Edit approach", Approach, (a, v) => a.ExpectedApproach = v);

    /// <summary>Writes the difficulty; blank writes none.</summary>
    [RelayCommand]
    private void CommitDifficulty() => CommitText("Edit difficulty", Difficulty, (a, v) => a.Difficulty = v);

    /// <summary>Writes the parking spot; blank writes none.</summary>
    [RelayCommand]
    private void CommitParking() => CommitText("Edit parking", Parking, (a, v) => a.StartingConditions.Parking = v);

    /// <summary>Writes the runway; blank writes none.</summary>
    [RelayCommand]
    private void CommitRunway() => CommitText("Edit runway", Runway, (a, v) => a.StartingConditions.Runway = v);

    /// <summary>Writes the navigation path; blank writes none.</summary>
    [RelayCommand]
    private void CommitNavigationPath() => CommitText("Edit navigation path", NavigationPath, (a, v) => a.StartingConditions.NavigationPath = v);

    /// <summary>Writes the fix or FRD; blank writes none.</summary>
    [RelayCommand]
    private void CommitFix() => CommitText("Edit fix", Fix, (a, v) => a.StartingConditions.Fix = v);

    /// <summary>Writes the latitude; coordinates the start lacks are made with a longitude of 0.</summary>
    [RelayCommand]
    private void CommitLatitude() =>
        CommitParsed(ScenarioFieldText.Latitude, Latitude, e => LatitudeError = e, "Edit latitude", (a, v) => CoordinatesOf(a).Lat = v);

    /// <summary>Writes the longitude; coordinates the start lacks are made with a latitude of 0.</summary>
    [RelayCommand]
    private void CommitLongitude() =>
        CommitParsed(ScenarioFieldText.Longitude, Longitude, e => LongitudeError = e, "Edit longitude", (a, v) => CoordinatesOf(a).Lon = v);

    /// <summary>Writes the altitude in feet; blank writes none.</summary>
    [RelayCommand]
    private void CommitAltitude() =>
        CommitParsed(
            ScenarioFieldText.OptionalNumber,
            Altitude,
            e => AltitudeError = e,
            "Edit altitude",
            (a, v) => a.StartingConditions.Altitude = v
        );

    /// <summary>Writes the speed in knots, 0 or more; blank writes none.</summary>
    [RelayCommand]
    private void CommitSpeed() =>
        CommitParsed(ScenarioFieldText.OptionalNonNegativeNumber, Speed, e => SpeedError = e, "Edit speed", (a, v) => a.StartingConditions.Speed = v);

    /// <summary>Writes the heading, 0 to 360, as typed; blank writes none.</summary>
    [RelayCommand]
    private void CommitHeading() =>
        CommitParsed(ScenarioFieldText.OptionalHeading, Heading, e => HeadingError = e, "Edit heading", (a, v) => a.StartingConditions.Heading = v);

    /// <summary>Writes the distance from the runway in nautical miles, 0 or more; blank writes none.</summary>
    [RelayCommand]
    private void CommitDistanceFromRunway() =>
        CommitParsed(
            ScenarioFieldText.OptionalNonNegativeNumber,
            DistanceFromRunway,
            e => DistanceFromRunwayError = e,
            "Edit distance from runway",
            (a, v) => a.StartingConditions.DistanceFromRunway = v
        );

    // Switches the selected aircraft's start to the picked kind, clearing what the kind does not use, as one step.
    private void CommitStartKind()
    {
        string kind = StartKind;
        if (StartingConditionKinds.Kinds.Contains(kind, StringComparer.Ordinal))
        {
            ApplyToSelected("Change start", a => a.StartingConditions = StartingConditionKinds.Switch(a.StartingConditions ?? new(), kind));
        }
    }

    private bool Uses(string member) =>
        StartingConditionKinds.Kinds.Contains(StartKind, StringComparer.Ordinal) && StartingConditionKinds.FieldsOf(StartKind).Contains(member);

    private static ScenarioCoordinates CoordinatesOf(ScenarioAircraft aircraft) =>
        aircraft.StartingConditions.Coordinates ??= new ScenarioCoordinates();

    // Sets or, for null, removes a YAAT-only key of the aircraft's extension data; no extension data is left empty.
    private static void SetExtension(ScenarioAircraft aircraft, string key, JsonElement? value)
    {
        if (value is { } element)
        {
            (aircraft.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal))[key] = element;
            return;
        }

        aircraft.ExtensionData?.Remove(key);
        if (aircraft.ExtensionData is { Count: 0 })
        {
            aircraft.ExtensionData = null;
        }
    }

    // A text field's commit: the text trimmed, blank as null, written to the selected aircraft as one step.
    private void CommitText(string description, string text, Action<ScenarioAircraft, string?> write)
    {
        string? value = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        ApplyToSelected(description, a => write(a, value));
    }

    // A typed field's commit: a text that is not a value sets the field's error and writes nothing.
    private void CommitParsed<T>(
        ScenarioFieldKind<T> kind,
        string text,
        Action<string?> setError,
        string description,
        Action<ScenarioAircraft, T> write
    )
    {
        if (!kind.TryParse(text, out T value))
        {
            setError(kind.ErrorText);
            return;
        }

        setError(null);
        ApplyToSelected(description, a => write(a, value));
    }

    // One edit of the selected aircraft as one undo step; nothing with no aircraft selected or it gone from the document.
    private void ApplyToSelected(string description, Action<ScenarioAircraft> change)
    {
        if ((SelectedDocumentIndex(description) is not { } index) || (Selected is not { } selected))
        {
            return;
        }

        Apply(description, s => change(s.Aircraft[index]), selected.Id);
    }

    // The panel's fields from the selected aircraft as the document holds it now, every error cleared; with none
    // selected, from a blank aircraft (the panel is hidden then).
    private void LoadDetail()
    {
        ScenarioAircraft aircraft =
            (SelectedDocumentIndex("Show aircraft") is { } index)
                ? _session.Document.Scenario.Aircraft[index]
                : new ScenarioAircraft { TransponderMode = "" };
        _loadingDetail = true;
        try
        {
            LoadStartKind(StartOf(aircraft).Type ?? "");
            OnAltitudeProfile = aircraft.OnAltitudeProfile;
            foreach ((_, Action<ScenarioAircraftViewModel, ScenarioAircraft> load) in AllFieldLoads())
            {
                load(this, aircraft);
            }

            LoadFlightPlan(aircraft);
            LoadPresets(aircraft);
            LoadAutoTrack(aircraft);
        }
        finally
        {
            _loadingDetail = false;
        }
    }

    // After a commit that changed nothing: only the committed field shows the document's value again, its error cleared;
    // a step that is no text field's re-shows the whole panel.
    private void ReloadField(string description)
    {
        Action<ScenarioAircraft>? own = _fieldReload;
        Action<ScenarioAircraftViewModel, ScenarioAircraft>? load = AllFieldLoads()
            .FirstOrDefault(f => string.Equals(f.Step, description, StringComparison.Ordinal))
            .Load;
        if (((own is null) && (load is null)) || (SelectedDocumentIndex(description) is not { } index))
        {
            LoadDetail();
            return;
        }

        _loadingDetail = true;
        try
        {
            ScenarioAircraft aircraft = _session.Document.Scenario.Aircraft[index];
            if (own is not null)
            {
                own(aircraft);
            }
            else if (load is not null)
            {
                load(this, aircraft);
            }
        }
        finally
        {
            _loadingDetail = false;
        }
    }

    // The text fields of every block: the aircraft's own and its starting conditions', the flight plan's, the auto-track's.
    private static IEnumerable<(string Step, Action<ScenarioAircraftViewModel, ScenarioAircraft> Load)> AllFieldLoads() =>
        FieldLoads.Concat(FlightPlanFieldLoads).Concat(AutoTrackFieldLoads);

    // Runs a commit whose no-op re-shows only what the reload shows, not a field of the panel's tables.
    private void WithFieldReload(Action<ScenarioAircraft> reload, Action commit)
    {
        _fieldReload = reload;
        try
        {
            commit();
        }
        finally
        {
            _fieldReload = null;
        }
    }

    private void LoadType(string type)
    {
        AircraftType = type;
        TypeHint = ((type.Trim().Length > 0) && !AircraftTypeCatalog.IsKnown(type)) ? NotInAircraftSpecsText : null;
    }

    private void LoadStartKind(string kind)
    {
        ScenarioFieldText.SetOptions(
            _startKindOptions,
            ScenarioGeneralViewModel.WithCurrent(BaseStartKindOptions, kind, $"{kind} (unknown kind)"),
            () => OnPropertyChanged(nameof(StartKindSelection))
        );
        StartKind = kind;
    }

    private static StartingConditions StartOf(ScenarioAircraft aircraft) => aircraft.StartingConditions ?? new StartingConditions();

    // A YAAT-only key's value as its field shows it: a string as itself, anything else as its JSON; empty when absent.
    private static string ExtensionText(ScenarioAircraft aircraft, string key)
    {
        if ((aircraft.ExtensionData is not { } extension) || !extension.TryGetValue(key, out JsonElement value))
        {
            return "";
        }

        return (value.ValueKind == JsonValueKind.String) ? (value.GetString() ?? "") : value.GetRawText();
    }
}
