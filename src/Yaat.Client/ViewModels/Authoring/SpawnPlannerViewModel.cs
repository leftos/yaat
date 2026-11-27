using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Yaat.Client.Logging;
using Yaat.Sim.Authoring;
using Yaat.Sim.Authoring.Spawns;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>One aircraft's line in a spawn plan preview.</summary>
/// <param name="Callsign">The aircraft's callsign.</param>
/// <param name="AircraftType">The aircraft's type.</param>
/// <param name="OriginKey">The origin it shares with other aircraft, or empty when it shares none.</param>
/// <param name="CurrentDelaySec">Its spawn delay now.</param>
/// <param name="NewDelaySec">The spawn delay the plan gives it.</param>
public sealed record SpawnPreviewRow(string Callsign, string AircraftType, string OriginKey, int CurrentDelaySec, int NewDelaySec);

/// <summary>
/// The spawn planner over one <see cref="ScenarioEditSession"/>: a filter with its live matched aircraft, band or workload
/// slots, a seeded order and jitter, a preview of the times with the overrun and its remedies, and an Apply that recomputes
/// the plan against the session's current document as one undo step. The preview is dropped when undo or redo replaces
/// the document; plans save and load as <see cref="SpawnPlan"/> files.
/// </summary>
public sealed partial class SpawnPlannerViewModel : ObservableObject, IDisposable
{
    /// <summary>The undo step's name an Apply records.</summary>
    public const string ApplyDescription = "Apply spawn plan";

    /// <summary>The working time field's help text.</summary>
    public const string WorkingTimeHelp = "arrivals or en route traffic: 15-20 min";

    /// <summary>The minimum gap field's guidance text.</summary>
    public const string GapGuidance =
        "The minimum gap is a time gap between spawns at one origin, not a separation standard: 60 s suits departures and "
        + "terminal fixes, 90-120 s en route or mixed speeds. Departures on one runway need 3 minutes behind a super and 2 "
        + "behind a heavy for wake, so space heavies 120 s or more apart if you do not want wake holds.";

    /// <summary>The warning a preview shows when compressing would bring same-origin spawns under the minimum gap.</summary>
    public const string CompressWarningText = "Compressing brings same-origin spawns closer than the minimum gap.";

    /// <summary>The error a preview or Apply shows when the planning length is not above zero.</summary>
    public const string LengthErrorText = "The planning length must be more than zero.";

    private static readonly ILogger Log = AppLog.CreateLogger<SpawnPlannerViewModel>();

    private static readonly HashSet<string> FilterProperties =
    [
        nameof(FlightRules),
        nameof(Kind),
        nameof(Departure),
        nameof(Destination),
        nameof(Approach),
        nameof(ExcludeDeparture),
        nameof(ExcludeDestination),
        nameof(ExcludeCallsigns),
    ];

    private readonly ScenarioEditSession _session;
    private readonly ScenarioEditApply _apply;

    [ObservableProperty]
    private string _flightRules = "";

    [ObservableProperty]
    private SpawnKind? _kind;

    [ObservableProperty]
    private string _departure = "";

    [ObservableProperty]
    private string _destination = "";

    [ObservableProperty]
    private string _approach = "";

    [ObservableProperty]
    private string _excludeDeparture = "";

    [ObservableProperty]
    private string _excludeDestination = "";

    [ObservableProperty]
    private string _excludeCallsigns = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchedCount))]
    private IReadOnlyList<ScenarioAircraft> _matched = [];

    [ObservableProperty]
    private SpawnPlannerMode _mode = SpawnPlannerMode.Bands;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanningLengthMinutes))]
    private int _planningLengthSec = SpawnPlan.DefaultPlanningLengthSec;

    [ObservableProperty]
    private int _workingTimeMinutes = SpawnPlan.DefaultWorkingTimeMinutes;

    [ObservableProperty]
    private int _minGapSec = SpawnPlan.DefaultMinGapSec;

    [ObservableProperty]
    private int _seed = Random.Shared.Next(1, int.MaxValue);

    [ObservableProperty]
    private bool _randomOrder;

    [ObservableProperty]
    private SpawnOverrunRemedy _selectedRemedy = SpawnOverrunRemedy.None;

    [ObservableProperty]
    private bool _hasPreview;

    [ObservableProperty]
    private SpawnScheduleResult? _previewResult;

    [ObservableProperty]
    private IReadOnlyList<int> _previewSlots = [];

    [ObservableProperty]
    private int _unusedSlots;

    [ObservableProperty]
    private SpawnOverrun? _overrun;

    [ObservableProperty]
    private string? _compressWarning;

    [ObservableProperty]
    private string? _previewError;

    [ObservableProperty]
    private string? _planError;

    /// <summary>Initializes a new instance of the <see cref="SpawnPlannerViewModel"/> class with the default inputs.</summary>
    /// <param name="session">The open working copy's session.</param>
    /// <param name="apply">Writes one edit as one undo step.</param>
    public SpawnPlannerViewModel(ScenarioEditSession session, ScenarioEditApply apply)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        Bands.Add(SpawnBandRowViewModel.FromPlan(SpawnPlanBand.Default));
        _session.DocumentReplaced += OnDocumentReplaced;
        RefreshMatched();
    }

    /// <summary>Gets the number of aircraft the filter takes.</summary>
    public int MatchedCount => Matched.Count;

    /// <summary>Gets or sets the planning length in minutes; the seconds are rounded to the nearest whole one.</summary>
    public double PlanningLengthMinutes
    {
        get => PlanningLengthSec / 60.0;
        set => PlanningLengthSec = double.IsFinite(value) ? (int)Math.Round(value * 60) : 0;
    }

    /// <summary>Gets the previewed aircraft, in assignment order, with the times the plan gives them.</summary>
    public ObservableCollection<SpawnPreviewRow> PreviewRows { get; } = [];

    /// <summary>Gets the callsigns of the matched aircraft the preview found no slot for.</summary>
    public ObservableCollection<string> UnassignedCallsigns { get; } = [];

    /// <summary>Gets the preview's warnings: skipped and reordering bands and the compress warning.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>Gives the plan a new seed, different from the one it has.</summary>
    [RelayCommand]
    public void Reroll()
    {
        int next;
        do
        {
            next = Random.Shared.Next(1, int.MaxValue);
        } while (next == Seed);

        Seed = next;
    }

    /// <summary>Lays the plan over the session's document without changing it, filling the preview members.</summary>
    [RelayCommand]
    public void Preview()
    {
        ClearPreview();
        if (InputError() is { } error)
        {
            PreviewError = error;
            return;
        }

        Scenario scenario = _session.Document.Scenario;
        PlanComputation plan = Compute(scenario);
        ShowPreview(scenario, plan);
    }

    /// <summary>
    /// Writes the plan's spawn delays as one undo step named <see cref="ApplyDescription"/>, recomputed with the same seed
    /// against the document the edit hands in, with the chosen remedy; an extend then sets the planning length to the
    /// last spawn plus the working time. The preview is dropped.
    /// </summary>
    [RelayCommand]
    public void Apply()
    {
        if (InputError() is { } error)
        {
            PreviewError = error;
            return;
        }

        PlanComputation? applied = null;
        _apply(
            ApplyDescription,
            scenario =>
            {
                applied = Compute(scenario);
                SpawnSchedule.Apply(applied.Final);
            }
        );
        if ((applied is { } done) && (SelectedRemedy == SpawnOverrunRemedy.ExtendScenario) && (done.Raw.Overrun is not null))
        {
            PlanningLengthSec = done.NewLengthSec + (WorkingTimeMinutes * 60);
        }

        if (Log.IsEnabled(LogLevel.Information))
        {
            Log.LogInformation("Applied spawn plan: {Count} aircraft, seed {Seed}", applied?.Final.Assignments.Count ?? 0, Seed);
        }

        ClearPreview();
        RefreshMatched();
    }

    /// <summary>
    /// Drops the preview and matches the filter against the session's document again: for the editor to call after an
    /// edit it made in place, which raises no <see cref="ScenarioEditSession.DocumentReplaced"/>.
    /// </summary>
    public void Refresh()
    {
        ClearPreview();
        RefreshMatched();
    }

    /// <summary>Sets the planning length to the previewed overrun's extended length plus the working time, and previews again.</summary>
    [RelayCommand]
    public void Extend()
    {
        if (Overrun is not { } overrun)
        {
            return;
        }

        PlanningLengthSec = overrun.ExtendedLengthSec + (WorkingTimeMinutes * 60);
        Preview();
    }

    /// <summary>Every planner input as a plan.</summary>
    /// <returns>The plan, at <see cref="SpawnPlan.CurrentVersion"/>.</returns>
    public SpawnPlan ToPlan() =>
        new(
            SpawnPlan.CurrentVersion,
            new SpawnPlanFilter(FlightRules, Kind, Departure, Destination, Approach, ExcludeDeparture, ExcludeDestination, ExcludeCallsigns),
            Mode,
            [.. Bands.Select(row => row.ToPlan())],
            WorkloadPlan(),
            [.. ControlPoints.Select(point => point.ToPlan())],
            PlanningLengthSec,
            WorkingTimeMinutes,
            MinGapSec,
            Seed,
            RandomOrder,
            SelectedRemedy
        );

    /// <summary>Writes every planner input to a plan file; a file that cannot be written sets <see cref="PlanError"/>.</summary>
    /// <param name="path">The file to write.</param>
    /// <returns>Whether the plan was written.</returns>
    public bool SavePlan(string path)
    {
        try
        {
            SpawnPlanFile.Write(path, ToPlan());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Cannot write spawn plan {Path}", path);
            PlanError = $"Cannot write the plan file: {ex.Message}";
            return false;
        }

        PlanError = null;
        return true;
    }

    /// <summary>
    /// Reads a plan file into the planner, dropping the preview; a file that cannot be read, or is another version, sets
    /// <see cref="PlanError"/> and keeps every current input. Airports the scenario lacks simply match nothing.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <returns>Whether the plan was loaded.</returns>
    public bool LoadPlanFile(string path)
    {
        SpawnPlanReadResult read = SpawnPlanFile.Read(path);
        if (read.Plan is not { } plan)
        {
            PlanError = read.Error;
            Log.LogWarning("Spawn plan {Path} not loaded: {Error}", path, read.Error);
            return false;
        }

        LoadPlan(plan);
        PlanError = null;
        return true;
    }

    /// <summary>Sets every planner input from a plan and drops the preview.</summary>
    /// <param name="plan">The plan.</param>
    public void LoadPlan(SpawnPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ClearPreview();
        LoadFilter(plan.Filter);
        Mode = plan.Mode;
        Bands.Clear();
        foreach (SpawnPlanBand band in plan.Bands)
        {
            Bands.Add(SpawnBandRowViewModel.FromPlan(band));
        }

        LoadWorkload(plan.Workload, plan.ControlPoints);
        PlanningLengthSec = plan.PlanningLengthSec;
        WorkingTimeMinutes = plan.WorkingTimeMinutes;
        MinGapSec = plan.MinGapSec;
        Seed = plan.Seed;
        RandomOrder = plan.RandomOrder;
        SelectedRemedy = plan.Remedy;
    }

    /// <summary>Stops following the session's document.</summary>
    public void Dispose() => _session.DocumentReplaced -= OnDocumentReplaced;

    /// <inheritdoc/>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if ((e.PropertyName is { } name) && (FilterProperties.Contains(name)))
        {
            RefreshMatched();
        }
    }

    private static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private SpawnFilter BuildFilter()
    {
        List<string> callsigns = ScenarioIdListText.Split(ExcludeCallsigns);
        return new SpawnFilter(
            Optional(FlightRules),
            Kind,
            Optional(Departure),
            Optional(Destination),
            Optional(Approach),
            Optional(ExcludeDeparture),
            Optional(ExcludeDestination),
            (callsigns.Count == 0) ? null : new HashSet<string>(callsigns, StringComparer.OrdinalIgnoreCase)
        );
    }

    private void LoadFilter(SpawnPlanFilter filter)
    {
        FlightRules = filter.FlightRules ?? "";
        Kind = filter.Kind;
        Departure = filter.Departure ?? "";
        Destination = filter.Destination ?? "";
        Approach = filter.Approach ?? "";
        ExcludeDeparture = filter.ExcludeDeparture ?? "";
        ExcludeDestination = filter.ExcludeDestination ?? "";
        ExcludeCallsigns = filter.ExcludeCallsigns ?? "";
    }

    private void RefreshMatched() => Matched = SpawnSchedule.Match(_session.Document.Scenario, BuildFilter());

    private string? InputError()
    {
        if (PlanningLengthSec <= 0)
        {
            return LengthErrorText;
        }

        return (Mode == SpawnPlannerMode.Workload) ? WorkloadError() : null;
    }

    private PlanComputation Compute(Scenario scenario)
    {
        IReadOnlyList<ScenarioAircraft> matched = SpawnSchedule.Match(scenario, BuildFilter());
        var random = new Random(Seed);
        IReadOnlyList<ScenarioAircraft> ordered = SpawnSchedule.Order(matched, RandomOrder, random);
        IReadOnlyList<int> slots =
            (Mode == SpawnPlannerMode.Bands) ? SpawnSchedule.BandSlots(ValidBands(), random) : WorkloadSlots(matched.Count, random);
        SpawnDeconfliction? deconfliction = (MinGapSec > 0) ? new SpawnDeconfliction(MinGapSec) : null;
        SpawnScheduleResult raw = SpawnSchedule.Assign(scenario, ordered, slots, PlanningLengthSec, deconfliction);
        SpawnScheduleResult final = SpawnSchedule.ApplyRemedy(raw, SelectedRemedy, out int newLengthSec);
        return new PlanComputation(slots, raw, final, newLengthSec);
    }

    private void ShowPreview(Scenario scenario, PlanComputation plan)
    {
        PreviewSlots = plan.Slots;
        PreviewResult = plan.Final;
        UnusedSlots = plan.Final.UnusedSlots;
        Overrun = plan.Raw.Overrun;
        foreach (SpawnAssignment assignment in plan.Final.Assignments)
        {
            ScenarioAircraft aircraft = assignment.Aircraft;
            PreviewRows.Add(
                new SpawnPreviewRow(aircraft.AircraftId, aircraft.AircraftType, assignment.OriginKey, aircraft.SpawnDelay, assignment.SpawnDelaySec)
            );
        }

        foreach (ScenarioAircraft aircraft in plan.Final.Unassigned)
        {
            UnassignedCallsigns.Add(aircraft.AircraftId);
        }

        AddBandWarnings();
        if (plan.Raw.Overrun?.CompressBreaksSeparation == true)
        {
            CompressWarning = CompressWarningText;
            Warnings.Add(CompressWarningText);
        }

        HasPreview = true;
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Spawn plan preview over {Scenario}: {Count} assigned, {Slots} slots", scenario.Id, PreviewRows.Count, PreviewSlots.Count);
        }
    }

    private void ClearPreview()
    {
        PreviewRows.Clear();
        UnassignedCallsigns.Clear();
        Warnings.Clear();
        PreviewSlots = [];
        PreviewResult = null;
        UnusedSlots = 0;
        Overrun = null;
        CompressWarning = null;
        PreviewError = null;
        HasPreview = false;
    }

    private void OnDocumentReplaced(object? sender, EventArgs e)
    {
        ClearPreview();
        RefreshMatched();
    }

    private sealed record PlanComputation(IReadOnlyList<int> Slots, SpawnScheduleResult Raw, SpawnScheduleResult Final, int NewLengthSec);
}
