using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels;

public partial class GeneratorRowViewModel : ObservableObject
{
    public IReadOnlyList<string> RunwayOptions { get; set; } = [];
    public IReadOnlyList<PositionOption> PositionOptions { get; set; } = [];
    public IReadOnlyList<string> EngineOptions { get; set; } = [];
    public IReadOnlyList<string> WeightOptions { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _id = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _runway = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _engineType = "Jet";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _weightCategory = "Large";

    [ObservableProperty]
    private double _initialDistance = 10;

    [ObservableProperty]
    private double _maxDistance = 50;

    [ObservableProperty]
    private double _intervalDistance = 5;

    [ObservableProperty]
    private int _startTimeOffset;

    [ObservableProperty]
    private int _maxTime = 3600;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private int _intervalTime = 300;

    [ObservableProperty]
    private bool _randomizeInterval;

    [ObservableProperty]
    private bool _randomizeWeightCategory;

    [ObservableProperty]
    private bool _autoTrackEnabled;

    [ObservableProperty]
    private string _autoTrackPositionId = "";

    [ObservableProperty]
    private int? _autoTrackHandoffDelay;

    [ObservableProperty]
    private string _autoTrackScratchPad = "";

    [ObservableProperty]
    private string _autoTrackClearedAltitude = "";

    public string DisplayLabel
    {
        get
        {
            var rwy = string.IsNullOrEmpty(Runway) ? "?" : Runway;
            return $"{rwy} · {EngineType} {WeightCategory} · {IntervalTime}s";
        }
    }

    public static GeneratorRowViewModel FromConfig(ScenarioGeneratorConfig cfg)
    {
        var row = new GeneratorRowViewModel
        {
            Id = cfg.Id,
            Runway = cfg.Runway,
            EngineType = cfg.EngineType,
            WeightCategory = cfg.WeightCategory,
            InitialDistance = cfg.InitialDistance,
            MaxDistance = cfg.MaxDistance,
            IntervalDistance = cfg.IntervalDistance,
            StartTimeOffset = cfg.StartTimeOffset,
            MaxTime = cfg.MaxTime,
            IntervalTime = cfg.IntervalTime,
            RandomizeInterval = cfg.RandomizeInterval,
            RandomizeWeightCategory = cfg.RandomizeWeightCategory,
        };

        if (cfg.AutoTrackConfiguration is { } at)
        {
            row.AutoTrackEnabled = true;
            row.AutoTrackPositionId = at.PositionId;
            row.AutoTrackHandoffDelay = at.HandoffDelay;
            row.AutoTrackScratchPad = at.ScratchPad ?? "";
            row.AutoTrackClearedAltitude = at.ClearedAltitude ?? "";
        }

        return row;
    }

    public ScenarioGeneratorConfig ToConfig()
    {
        var cfg = new ScenarioGeneratorConfig
        {
            Id = Id,
            Runway = Runway,
            EngineType = EngineType,
            WeightCategory = WeightCategory,
            InitialDistance = InitialDistance,
            MaxDistance = MaxDistance,
            IntervalDistance = IntervalDistance,
            StartTimeOffset = StartTimeOffset,
            MaxTime = MaxTime,
            IntervalTime = IntervalTime,
            RandomizeInterval = RandomizeInterval,
            RandomizeWeightCategory = RandomizeWeightCategory,
        };

        if (AutoTrackEnabled && !string.IsNullOrWhiteSpace(AutoTrackPositionId))
        {
            cfg.AutoTrackConfiguration = new AutoTrackConditions
            {
                PositionId = AutoTrackPositionId,
                HandoffDelay = AutoTrackHandoffDelay,
                ScratchPad = string.IsNullOrEmpty(AutoTrackScratchPad) ? null : AutoTrackScratchPad,
                ClearedAltitude = string.IsNullOrEmpty(AutoTrackClearedAltitude) ? null : AutoTrackClearedAltitude,
            };
        }

        return cfg;
    }
}
