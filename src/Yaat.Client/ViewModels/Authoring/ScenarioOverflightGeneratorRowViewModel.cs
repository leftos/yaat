using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim.Authoring;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels.Authoring;

/// <summary>
/// One entry of the General tab's overflight generators (<see cref="Scenario.OverflightGenerators"/>, a YAAT-only kind):
/// the entry and exit bearing arcs, distances, altitude band, hemispheric snap, exit distance (blank is none), and engine
/// and weight pickers, on top of the window and cadence fields of <see cref="ScenarioGeneratorRowViewModel{T}"/>.
/// </summary>
public sealed partial class ScenarioOverflightGeneratorRowViewModel : ScenarioGeneratorRowViewModel<OverflightGeneratorConfig>
{
    [ObservableProperty]
    private string _fromBearingFromText;

    [ObservableProperty]
    private string? _fromBearingFromError;

    [ObservableProperty]
    private string _fromBearingToText;

    [ObservableProperty]
    private string? _fromBearingToError;

    [ObservableProperty]
    private string _toBearingFromText;

    [ObservableProperty]
    private string? _toBearingFromError;

    [ObservableProperty]
    private string _toBearingToText;

    [ObservableProperty]
    private string? _toBearingToError;

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
    private bool _snapHemisphericAltitude;

    [ObservableProperty]
    private string _exitDistanceText;

    [ObservableProperty]
    private string? _exitDistanceError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineTypePick))]
    private string _engineType;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WeightCategoryPick))]
    private string _weightCategory;

    /// <summary>Initializes a new instance of the <see cref="ScenarioOverflightGeneratorRowViewModel"/> class from the item at the index.</summary>
    /// <param name="session">The open working copy's session.</param>
    /// <param name="index">The item's index into <see cref="Scenario.OverflightGenerators"/>.</param>
    /// <param name="apply">Writes one edit as one undo step.</param>
    public ScenarioOverflightGeneratorRowViewModel(ScenarioEditSession session, int index, ScenarioEditApply apply)
        : base(session, index, apply, s => s.OverflightGenerators)
    {
        OverflightGeneratorConfig g = Item;
        _fromBearingFromText = ScenarioFieldText.Number.Format(g.FromBearingFrom);
        _fromBearingToText = ScenarioFieldText.Number.Format(g.FromBearingTo);
        _toBearingFromText = ScenarioFieldText.Number.Format(g.ToBearingFrom);
        _toBearingToText = ScenarioFieldText.Number.Format(g.ToBearingTo);
        _initialDistanceText = ScenarioFieldText.Number.Format(g.InitialDistance);
        _maxDistanceText = ScenarioFieldText.Number.Format(g.MaxDistance);
        _altitudeMinText = ScenarioFieldText.Number.Format(g.AltitudeMin);
        _altitudeMaxText = ScenarioFieldText.Number.Format(g.AltitudeMax);
        _snapHemisphericAltitude = g.SnapHemisphericAltitude;
        _exitDistanceText = ScenarioFieldText.OptionalNumber.Format(g.ExitDistance);
        _engineType = g.EngineType;
        _weightCategory = g.WeightCategory;
        EngineTypeOptions = ScenarioFieldText.Picks(ScenarioFieldText.EngineTypes, g.EngineType);
        WeightCategoryOptions = ScenarioFieldText.Picks(ScenarioFieldText.WeightCategories, g.WeightCategory);
    }

    /// <inheritdoc/>
    public override bool IsYaatOnly => true;

    /// <summary>Gets the engine types offered, plus the document's when unlisted.</summary>
    public IReadOnlyList<string> EngineTypeOptions { get; }

    /// <summary>Gets the weight categories offered, plus the document's when unlisted.</summary>
    public IReadOnlyList<string> WeightCategoryOptions { get; }

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

    /// <inheritdoc/>
    protected override void SetStartTimeOffset(OverflightGeneratorConfig generator, int seconds) => generator.StartTimeOffset = seconds;

    /// <inheritdoc/>
    protected override void SetMaxTime(OverflightGeneratorConfig generator, int? seconds) => generator.MaxTime = seconds;

    /// <inheritdoc/>
    protected override void SetIntervalTime(OverflightGeneratorConfig generator, int seconds) => generator.IntervalTime = seconds;

    /// <inheritdoc/>
    protected override void SetRandomizeInterval(OverflightGeneratorConfig generator, bool value) => generator.RandomizeInterval = value;

    /// <inheritdoc/>
    protected override void SetEnabled(OverflightGeneratorConfig generator, bool? value) => generator.Enabled = value;

    [RelayCommand]
    private void CommitFromBearingFrom() =>
        CommitField(
            ScenarioFieldText.Number,
            FromBearingFromText,
            (t, e) => (FromBearingFromText, FromBearingFromError) = (t, e),
            (g, v) => g.FromBearingFrom = v
        );

    [RelayCommand]
    private void CommitFromBearingTo() =>
        CommitField(
            ScenarioFieldText.Number,
            FromBearingToText,
            (t, e) => (FromBearingToText, FromBearingToError) = (t, e),
            (g, v) => g.FromBearingTo = v
        );

    [RelayCommand]
    private void CommitToBearingFrom() =>
        CommitField(
            ScenarioFieldText.Number,
            ToBearingFromText,
            (t, e) => (ToBearingFromText, ToBearingFromError) = (t, e),
            (g, v) => g.ToBearingFrom = v
        );

    [RelayCommand]
    private void CommitToBearingTo() =>
        CommitField(ScenarioFieldText.Number, ToBearingToText, (t, e) => (ToBearingToText, ToBearingToError) = (t, e), (g, v) => g.ToBearingTo = v);

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
    private void CommitExitDistance() =>
        CommitField(
            ScenarioFieldText.OptionalNumber,
            ExitDistanceText,
            (t, e) => (ExitDistanceText, ExitDistanceError) = (t, e),
            (g, v) => g.ExitDistance = v
        );

    [RelayCommand]
    private void CommitEngineType() => CommitText(EngineType, t => EngineType = t, (g, v) => g.EngineType = v);

    [RelayCommand]
    private void CommitWeightCategory() => CommitText(WeightCategory, t => WeightCategory = t, (g, v) => g.WeightCategory = v);

    partial void OnSnapHemisphericAltitudeChanged(bool value) => WriteIfCurrent(g => g.SnapHemisphericAltitude = value);
}
