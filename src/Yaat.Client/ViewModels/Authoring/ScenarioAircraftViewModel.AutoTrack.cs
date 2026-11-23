using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// The detail panel's auto-track block. The Auto-track checkbox removes the conditions, or makes them with no position,
/// one undo step each; the fields show only while there are conditions. The position is picked from the General tab's
/// catalog of the ARTCC's and its neighbours' positions (handed in, never loaded again here), with none first and the
/// document's position offered when the catalog does not list it. The handoff delay is an optional whole number of
/// seconds, 0 or more (blank writes none); the scratchpad and altitudes are text, trimmed, blank written as none.
/// </summary>
public sealed partial class ScenarioAircraftViewModel
{
    // Each auto-track text field by its commit's undo step: how it shows the conditions' value, its error cleared.
    private static readonly (string Step, Action<ScenarioAircraftViewModel, ScenarioAircraft> Load)[] AutoTrackFieldLoads =
    [
        (
            "Edit handoff delay",
            (vm, a) =>
                (vm.HandoffDelay, vm.HandoffDelayError) = (
                    ScenarioFieldText.OptionalNonNegativeWholeNumber.Format(a.AutoTrackConditions?.HandoffDelay),
                    null
                )
        ),
        ("Edit scratchpad", (vm, a) => vm.ScratchPad = a.AutoTrackConditions?.ScratchPad ?? ""),
        ("Edit interim altitude", (vm, a) => vm.InterimAltitude = a.AutoTrackConditions?.InterimAltitude ?? ""),
        ("Edit cleared altitude", (vm, a) => vm.ClearedAltitude = a.AutoTrackConditions?.ClearedAltitude ?? ""),
    ];

    // The position picker's one options collection, updated in place.
    private readonly ObservableCollection<ScenarioPickOption> _autoTrackPositionOptions = [];
    private IReadOnlyList<ScenarioPickOption> _positionCatalog = [];

    [ObservableProperty]
    private bool _hasAutoTrack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoTrackPositionSelection))]
    private string _autoTrackPositionId = "";

    [ObservableProperty]
    private string _handoffDelay = "";

    [ObservableProperty]
    private string? _handoffDelayError;

    [ObservableProperty]
    private string _scratchPad = "";

    [ObservableProperty]
    private string _interimAltitude = "";

    [ObservableProperty]
    private string _clearedAltitude = "";

    /// <summary>
    /// Gets or sets the ARTCC's and its neighbours' positions, the General tab's catalog; the editor hands it in when it
    /// loads. The position picker offers them.
    /// </summary>
    public IReadOnlyList<ScenarioPickOption> PositionCatalog
    {
        get => _positionCatalog;
        set
        {
            if (SetProperty(ref _positionCatalog, value ?? []))
            {
                UpdateAutoTrackOptions();
            }
        }
    }

    /// <summary>Gets the position picker's options; one collection, updated in place.</summary>
    public IReadOnlyList<ScenarioPickOption> AutoTrackPositionOptions => _autoTrackPositionOptions;

    /// <summary>
    /// Gets or sets the position picker's selected option, the one whose value the conditions hold; only the user's pick
    /// of another position is committed.
    /// </summary>
    public ScenarioPickOption? AutoTrackPositionSelection
    {
        get => ScenarioFieldText.Selected(_autoTrackPositionOptions, AutoTrackPositionId);
        set => ScenarioFieldText.Pick(value?.Value, AutoTrackPositionId, v => AutoTrackPositionId = v, CommitAutoTrackPosition);
    }

    partial void OnHasAutoTrackChanged(bool value)
    {
        if (_loadingDetail)
        {
            return;
        }

        if (value)
        {
            ApplyToSelected("Add auto-track", a => a.AutoTrackConditions = new AutoTrackConditions());
        }
        else
        {
            ApplyToSelected("Remove auto-track", a => a.AutoTrackConditions = null);
        }
    }

    /// <summary>Writes the handoff delay in seconds, 0 or more; blank writes none.</summary>
    [RelayCommand]
    private void CommitHandoffDelay() =>
        CommitParsed(
            ScenarioFieldText.OptionalNonNegativeWholeNumber,
            HandoffDelay,
            e => HandoffDelayError = e,
            "Edit handoff delay",
            (a, v) => a.AutoTrackConditions?.HandoffDelay = v
        );

    /// <summary>Writes the scratchpad, trimmed; blank writes none.</summary>
    [RelayCommand]
    private void CommitScratchPad() => CommitText("Edit scratchpad", ScratchPad, (a, v) => a.AutoTrackConditions?.ScratchPad = v);

    /// <summary>Writes the interim altitude as typed, trimmed; blank writes none.</summary>
    [RelayCommand]
    private void CommitInterimAltitude() =>
        CommitText("Edit interim altitude", InterimAltitude, (a, v) => a.AutoTrackConditions?.InterimAltitude = v);

    /// <summary>Writes the cleared altitude as typed, trimmed; blank writes none.</summary>
    [RelayCommand]
    private void CommitClearedAltitude() =>
        CommitText("Edit cleared altitude", ClearedAltitude, (a, v) => a.AutoTrackConditions?.ClearedAltitude = v);

    private void CommitAutoTrackPosition()
    {
        string positionId = AutoTrackPositionId;
        ApplyToSelected("Edit auto-track position", a => a.AutoTrackConditions?.PositionId = positionId);
    }

    // The checkbox and the position picker from the aircraft's conditions; the text fields load with the panel's others.
    private void LoadAutoTrack(ScenarioAircraft aircraft)
    {
        HasAutoTrack = aircraft.AutoTrackConditions is not null;
        AutoTrackPositionId = aircraft.AutoTrackConditions?.PositionId ?? "";
        UpdateAutoTrackOptions();
    }

    private void UpdateAutoTrackOptions() =>
        ScenarioFieldText.SetOptions(
            _autoTrackPositionOptions,
            ScenarioFieldText.AutoTrackOptions(_positionCatalog, AutoTrackPositionId),
            () => OnPropertyChanged(nameof(AutoTrackPositionSelection))
        );
}
