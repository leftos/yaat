using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels;

public partial class VfrArrivalGeneratorRowViewModel : ObservableObject
{
    public IReadOnlyList<string> EngineOptions { get; set; } = [];
    public IReadOnlyList<string> WeightOptions { get; set; } = [];
    public IReadOnlyList<PositionOption> PositionOptions { get; set; } = [];

    [ObservableProperty]
    private string _id = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private double _bearingFrom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private double _bearingTo = 360;

    [ObservableProperty]
    private double _initialDistance = 10;

    [ObservableProperty]
    private double _maxDistance = 20;

    [ObservableProperty]
    private double _altitudeMin = 2500;

    [ObservableProperty]
    private double _altitudeMax = 4500;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _directTo = "";

    [ObservableProperty]
    private int _initialVsFpm;

    [ObservableProperty]
    private int? _descendToAltitude;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private string _engineType = "Piston";

    [ObservableProperty]
    private string _weightCategory = "Small";

    [ObservableProperty]
    private int _startTimeOffset;

    [ObservableProperty]
    private int? _maxTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private int _intervalTime = 300;

    [ObservableProperty]
    private bool _randomizeInterval;

    /// <summary>Three-state Active toggle: null follows the start/max-time window, true and false override it.</summary>
    [ObservableProperty]
    private bool? _enabled;

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
            var target = string.IsNullOrWhiteSpace(DirectTo) ? "the field" : DirectTo;
            return $"{BearingFrom:F0}°-{BearingTo:F0}° · direct {target} · {EngineType} · {IntervalTime}s";
        }
    }

    public static VfrArrivalGeneratorRowViewModel FromConfig(VfrArrivalGeneratorConfig cfg)
    {
        var row = new VfrArrivalGeneratorRowViewModel
        {
            Id = cfg.Id,
            BearingFrom = cfg.BearingFrom,
            BearingTo = cfg.BearingTo,
            InitialDistance = cfg.InitialDistance,
            MaxDistance = cfg.MaxDistance,
            AltitudeMin = cfg.AltitudeMin,
            AltitudeMax = cfg.AltitudeMax,
            DirectTo = cfg.DirectTo,
            InitialVsFpm = cfg.InitialVsFpm,
            DescendToAltitude = cfg.DescendToAltitude is { } alt ? (int)alt : null,
            EngineType = cfg.EngineType,
            WeightCategory = cfg.WeightCategory,
            StartTimeOffset = cfg.StartTimeOffset,
            MaxTime = cfg.MaxTime,
            IntervalTime = cfg.IntervalTime,
            RandomizeInterval = cfg.RandomizeInterval,
            Enabled = cfg.Enabled,
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

    public VfrArrivalGeneratorConfig ToConfig()
    {
        var cfg = new VfrArrivalGeneratorConfig
        {
            Id = Id,
            BearingFrom = BearingFrom,
            BearingTo = BearingTo,
            InitialDistance = InitialDistance,
            MaxDistance = MaxDistance,
            AltitudeMin = AltitudeMin,
            AltitudeMax = AltitudeMax,
            DirectTo = DirectTo,
            InitialVsFpm = InitialVsFpm,
            DescendToAltitude = DescendToAltitude,
            EngineType = EngineType,
            WeightCategory = WeightCategory,
            StartTimeOffset = StartTimeOffset,
            MaxTime = MaxTime,
            IntervalTime = IntervalTime,
            RandomizeInterval = RandomizeInterval,
            Enabled = Enabled,
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
