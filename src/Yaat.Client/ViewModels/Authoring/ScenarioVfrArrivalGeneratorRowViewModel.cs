using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// One entry of the General tab's VFR arrival generators (<see cref="Scenario.VfrArrivalGenerators"/>, a YAAT-only
/// kind): the bearing arc, distances, altitude band, direct-to, initial vertical speed, descend-to altitude (blank is
/// none), engine and weight pickers, and the auto-track position, on top of the window and cadence fields of
/// <see cref="ScenarioGeneratorRowViewModel{T}"/>.
/// </summary>
public sealed partial class ScenarioVfrArrivalGeneratorRowViewModel : ScenarioGeneratorRowViewModel<VfrArrivalGeneratorConfig>
{
    [ObservableProperty]
    private string _bearingFromText;

    [ObservableProperty]
    private string? _bearingFromError;

    [ObservableProperty]
    private string _bearingToText;

    [ObservableProperty]
    private string? _bearingToError;

    [ObservableProperty]
    private string _initialDistanceText;

    [ObservableProperty]
    private string? _initialDistanceError;

    [ObservableProperty]
    private string _maxDistanceText;

    [ObservableProperty]
    private string? _maxDistanceError;

    [ObservableProperty]
    private string _altitudeMinText;

    [ObservableProperty]
    private string? _altitudeMinError;

    [ObservableProperty]
    private string _altitudeMaxText;

    [ObservableProperty]
    private string? _altitudeMaxError;

    [ObservableProperty]
    private string _directTo;

    [ObservableProperty]
    private string _initialVsFpmText;

    [ObservableProperty]
    private string? _initialVsFpmError;

    [ObservableProperty]
    private string _descendToAltitudeText;

    [ObservableProperty]
    private string? _descendToAltitudeError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineTypePick))]
    private string _engineType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WeightCategoryPick))]
    private string _weightCategory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoTrackPositionPick), nameof(AutoTrackPositionSelection))]
    private string _autoTrackPositionId;

    private readonly ObservableCollection<ScenarioPickOption> _autoTrackPositionOptions = [];

    /// <summary>Initializes a new instance of the <see cref="ScenarioVfrArrivalGeneratorRowViewModel"/> class from the item at the index.</summary>
    /// <param name="session">The open working copy's session.</param>
    /// <param name="index">The item's index into <see cref="Scenario.VfrArrivalGenerators"/>.</param>
    /// <param name="apply">Writes one edit as one undo step.</param>
    public ScenarioVfrArrivalGeneratorRowViewModel(ScenarioEditSession session, int index, ScenarioEditApply apply)
        : base(session, index, apply, s => s.VfrArrivalGenerators)
    {
        VfrArrivalGeneratorConfig g = Item;
        _bearingFromText = ScenarioFieldText.Number.Format(g.BearingFrom);
        _bearingToText = ScenarioFieldText.Number.Format(g.BearingTo);
        _initialDistanceText = ScenarioFieldText.Number.Format(g.InitialDistance);
        _maxDistanceText = ScenarioFieldText.Number.Format(g.MaxDistance);
        _altitudeMinText = ScenarioFieldText.Number.Format(g.AltitudeMin);
        _altitudeMaxText = ScenarioFieldText.Number.Format(g.AltitudeMax);
        _directTo = g.DirectTo;
        _initialVsFpmText = ScenarioFieldText.WholeNumber.Format(g.InitialVsFpm);
        _descendToAltitudeText = ScenarioFieldText.OptionalNumber.Format(g.DescendToAltitude);
        _engineType = g.EngineType;
        _weightCategory = g.WeightCategory;
        _autoTrackPositionId = g.AutoTrackConfiguration?.PositionId ?? "";
        EngineTypeOptions = ScenarioFieldText.Picks(ScenarioFieldText.EngineTypes, g.EngineType);
        WeightCategoryOptions = ScenarioFieldText.Picks(ScenarioFieldText.WeightCategories, g.WeightCategory);
    }

    /// <inheritdoc/>
    public override bool IsYaatOnly => true;

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
        set => ScenarioFieldText.Pick(value, EngineType, v => EngineType = v, CommitEngineType);
    }

    /// <summary>Gets or sets the weight picker's selection; only the user's pick is committed (see <see cref="Pick"/>).</summary>
    public string? WeightCategoryPick
    {
        get => WeightCategory;
        set => ScenarioFieldText.Pick(value, WeightCategory, v => WeightCategory = v, CommitWeightCategory);
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
        set => ScenarioFieldText.Pick(value, AutoTrackPositionId, v => AutoTrackPositionId = v, CommitAutoTrackPosition);
    }

    /// <inheritdoc/>
    protected override void SetStartTimeOffset(VfrArrivalGeneratorConfig generator, int seconds) => generator.StartTimeOffset = seconds;

    /// <inheritdoc/>
    protected override void SetMaxTime(VfrArrivalGeneratorConfig generator, int? seconds) => generator.MaxTime = seconds;

    /// <inheritdoc/>
    protected override void SetIntervalTime(VfrArrivalGeneratorConfig generator, int seconds) => generator.IntervalTime = seconds;

    /// <inheritdoc/>
    protected override void SetRandomizeInterval(VfrArrivalGeneratorConfig generator, bool value) => generator.RandomizeInterval = value;

    /// <inheritdoc/>
    protected override void SetEnabled(VfrArrivalGeneratorConfig generator, bool? value) => generator.Enabled = value;

    [RelayCommand]
    private void CommitBearingFrom() =>
        CommitField(ScenarioFieldText.Number, BearingFromText, (t, e) => (BearingFromText, BearingFromError) = (t, e), (g, v) => g.BearingFrom = v);

    [RelayCommand]
    private void CommitBearingTo() =>
        CommitField(ScenarioFieldText.Number, BearingToText, (t, e) => (BearingToText, BearingToError) = (t, e), (g, v) => g.BearingTo = v);

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
    private void CommitAltitudeMin() =>
        CommitField(ScenarioFieldText.Number, AltitudeMinText, (t, e) => (AltitudeMinText, AltitudeMinError) = (t, e), (g, v) => g.AltitudeMin = v);

    [RelayCommand]
    private void CommitAltitudeMax() =>
        CommitField(ScenarioFieldText.Number, AltitudeMaxText, (t, e) => (AltitudeMaxText, AltitudeMaxError) = (t, e), (g, v) => g.AltitudeMax = v);

    [RelayCommand]
    private void CommitDirectTo() => CommitText(DirectTo, t => DirectTo = t, (g, v) => g.DirectTo = v);

    [RelayCommand]
    private void CommitInitialVsFpm() =>
        CommitField(
            ScenarioFieldText.WholeNumber,
            InitialVsFpmText,
            (t, e) => (InitialVsFpmText, InitialVsFpmError) = (t, e),
            (g, v) => g.InitialVsFpm = v
        );

    [RelayCommand]
    private void CommitDescendToAltitude() =>
        CommitField(
            ScenarioFieldText.OptionalNumber,
            DescendToAltitudeText,
            (t, e) => (DescendToAltitudeText, DescendToAltitudeError) = (t, e),
            (g, v) => g.DescendToAltitude = v
        );

    [RelayCommand]
    private void CommitEngineType() => CommitText(EngineType, t => EngineType = t, (g, v) => g.EngineType = v);

    [RelayCommand]
    private void CommitWeightCategory() => CommitText(WeightCategory, t => WeightCategory = t, (g, v) => g.WeightCategory = v);

    [RelayCommand]
    private void CommitAutoTrackPosition() =>
        CommitAutoTrack(AutoTrackPositionId, t => AutoTrackPositionId = t, g => g.AutoTrackConfiguration, (g, v) => g.AutoTrackConfiguration = v);
}
