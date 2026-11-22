using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// One entry of the General tab's arrival generators (<see cref="Scenario.AircraftGenerators"/>, the vNAS kind): the
/// runway, engine and weight pickers, the distances, randomize weight, and the auto-track position, on top of the
/// window and cadence fields of <see cref="ScenarioGeneratorRowViewModel{T}"/>.
/// </summary>
public sealed partial class ScenarioArrivalGeneratorRowViewModel : ScenarioGeneratorRowViewModel<ScenarioGeneratorConfig>
{
    [ObservableProperty]
    private string _runway;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineTypePick))]
    private string _engineType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WeightCategoryPick))]
    private string _weightCategory;

    [ObservableProperty]
    private string _initialDistanceText;

    [ObservableProperty]
    private string? _initialDistanceError;

    [ObservableProperty]
    private string _maxDistanceText;

    [ObservableProperty]
    private string? _maxDistanceError;

    [ObservableProperty]
    private string _intervalDistanceText;

    [ObservableProperty]
    private string? _intervalDistanceError;

    [ObservableProperty]
    private bool _randomizeWeightCategory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoTrackPositionPick), nameof(AutoTrackPositionSelection))]
    private string _autoTrackPositionId;

    private readonly ObservableCollection<ScenarioPickOption> _autoTrackPositionOptions = [];

    /// <summary>Initializes a new instance of the <see cref="ScenarioArrivalGeneratorRowViewModel"/> class from the item at the index.</summary>
    /// <param name="session">The open working copy's session.</param>
    /// <param name="index">The item's index into <see cref="Scenario.AircraftGenerators"/>.</param>
    /// <param name="apply">Writes one edit as one undo step.</param>
    public ScenarioArrivalGeneratorRowViewModel(ScenarioEditSession session, int index, ScenarioEditApply apply)
        : base(session, index, apply, s => s.AircraftGenerators)
    {
        ScenarioGeneratorConfig g = Item;
        _runway = g.Runway;
        _engineType = g.EngineType;
        _weightCategory = g.WeightCategory;
        _initialDistanceText = ScenarioFieldText.Number.Format(g.InitialDistance);
        _maxDistanceText = ScenarioFieldText.Number.Format(g.MaxDistance);
        _intervalDistanceText = ScenarioFieldText.Number.Format(g.IntervalDistance);
        _randomizeWeightCategory = g.RandomizeWeightCategory;
        _autoTrackPositionId = g.AutoTrackConfiguration?.PositionId ?? "";
        EngineTypeOptions = ScenarioFieldText.Picks(ScenarioFieldText.EngineTypes, g.EngineType);
        WeightCategoryOptions = ScenarioFieldText.Picks(ScenarioFieldText.WeightCategories, g.WeightCategory);
    }

    /// <inheritdoc/>
    public override bool IsYaatOnly => false;

    /// <summary>Gets the engine types offered, plus the document's when unlisted.</summary>
    public IReadOnlyList<string> EngineTypeOptions { get; }

    /// <summary>Gets the weight categories offered, plus the document's when unlisted.</summary>
    public IReadOnlyList<string> WeightCategoryOptions { get; }

    /// <summary>Offers none, the catalog's positions, and the row's own position when the catalog does not list it.</summary>
    /// <param name="catalog">The ARTCC's and its neighbours' positions.</param>
    public void UpdateOptions(IReadOnlyList<ScenarioPickOption> catalog) =>
        ScenarioFieldText.SetOptions(
            _autoTrackPositionOptions,
            ScenarioFieldText.AutoTrackOptions(catalog, AutoTrackPositionId),
            () => OnPropertyChanged(nameof(AutoTrackPositionSelection))
        );

    /// <summary>Gets the auto-track picker's options; one collection, updated in place.</summary>
    public IReadOnlyList<ScenarioPickOption> AutoTrackPositionOptions => _autoTrackPositionOptions;

    /// <summary>Gets or sets the engine picker's selection; only the user's pick is committed (see <see cref="Pick"/>).</summary>
    public string? EngineTypePick
    {
        get => EngineType;
        set => Pick(value, EngineType, v => EngineType = v, CommitEngineType);
    }

    /// <summary>Gets or sets the weight picker's selection; only the user's pick is committed (see <see cref="Pick"/>).</summary>
    public string? WeightCategoryPick
    {
        get => WeightCategory;
        set => Pick(value, WeightCategory, v => WeightCategory = v, CommitWeightCategory);
    }

    /// <summary>
    /// Gets or sets the picker's selected option, the one of <see cref="AutoTrackPositionOptions"/> whose value the field holds. The
    /// picker binds its SelectedItem here: a picker's SelectedValue binding does not survive its options being swapped.
    /// Setting it goes through <see cref="AutoTrackPositionPick"/>, so only the user's pick is committed.
    /// </summary>
    public ScenarioPickOption? AutoTrackPositionSelection
    {
        get => ScenarioFieldText.Selected(AutoTrackPositionOptions, AutoTrackPositionId);
        set => AutoTrackPositionPick = value?.Value;
    }

    /// <summary>Gets or sets the auto-track picker's selection; only the user's pick is committed (see <see cref="Pick"/>).</summary>
    public string? AutoTrackPositionPick
    {
        get => AutoTrackPositionId;
        set => Pick(value, AutoTrackPositionId, v => AutoTrackPositionId = v, CommitAutoTrackPosition);
    }

    /// <inheritdoc/>
    protected override void SetStartTimeOffset(ScenarioGeneratorConfig generator, int seconds) => generator.StartTimeOffset = seconds;

    /// <inheritdoc/>
    protected override void SetMaxTime(ScenarioGeneratorConfig generator, int? seconds) => generator.MaxTime = seconds;

    /// <inheritdoc/>
    protected override void SetIntervalTime(ScenarioGeneratorConfig generator, int seconds) => generator.IntervalTime = seconds;

    /// <inheritdoc/>
    protected override void SetRandomizeInterval(ScenarioGeneratorConfig generator, bool value) => generator.RandomizeInterval = value;

    /// <inheritdoc/>
    protected override void SetEnabled(ScenarioGeneratorConfig generator, bool? value) => generator.Enabled = value;

    [RelayCommand]
    private void CommitRunway() => CommitText(Runway, t => Runway = t, (g, v) => g.Runway = v);

    [RelayCommand]
    private void CommitEngineType() => CommitText(EngineType, t => EngineType = t, (g, v) => g.EngineType = v);

    [RelayCommand]
    private void CommitWeightCategory() => CommitText(WeightCategory, t => WeightCategory = t, (g, v) => g.WeightCategory = v);

    [RelayCommand]
    private void CommitInitialDistance() =>
        CommitField(
            ScenarioFieldText.Number,
            InitialDistanceText,
            (t, e) => (InitialDistanceText, InitialDistanceError) = (t, e),
            (g, v) => g.InitialDistance = v
        );

    [RelayCommand]
    private void CommitMaxDistance() =>
        CommitField(ScenarioFieldText.Number, MaxDistanceText, (t, e) => (MaxDistanceText, MaxDistanceError) = (t, e), (g, v) => g.MaxDistance = v);

    [RelayCommand]
    private void CommitIntervalDistance() =>
        CommitField(
            ScenarioFieldText.Number,
            IntervalDistanceText,
            (t, e) => (IntervalDistanceText, IntervalDistanceError) = (t, e),
            (g, v) => g.IntervalDistance = v
        );

    [RelayCommand]
    private void CommitAutoTrackPosition() =>
        CommitAutoTrack(AutoTrackPositionId, t => AutoTrackPositionId = t, g => g.AutoTrackConfiguration, (g, v) => g.AutoTrackConfiguration = v);

    partial void OnRandomizeWeightCategoryChanged(bool value) => WriteIfCurrent(g => g.RandomizeWeightCategory = value);
}
