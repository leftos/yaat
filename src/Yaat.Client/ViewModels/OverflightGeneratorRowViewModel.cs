using CommunityToolkit.Mvvm.ComponentModel;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels;

public partial class OverflightGeneratorRowViewModel : ObservableObject
{
    public IReadOnlyList<string> EngineOptions { get; set; } = [];
    public IReadOnlyList<string> WeightOptions { get; set; } = [];

    [ObservableProperty]
    private string _id = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private double _fromBearingFrom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private double _fromBearingTo = 360;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private double _toBearingFrom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayLabel))]
    private double _toBearingTo = 360;

    [ObservableProperty]
    private double _initialDistance = 15;

    [ObservableProperty]
    private double _maxDistance = 25;

    [ObservableProperty]
    private double _altitudeMin = 4500;

    [ObservableProperty]
    private double _altitudeMax = 7500;

    /// <summary>14 CFR 91.159(a): a level transit above 3000 ft AGL flies an odd/even thousand + 500 by course.</summary>
    [ObservableProperty]
    private bool _snapHemisphericAltitude = true;

    [ObservableProperty]
    private double? _exitDistance;

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

    public string DisplayLabel =>
        $"{FromBearingFrom:F0}°-{FromBearingTo:F0}° → {ToBearingFrom:F0}°-{ToBearingTo:F0}° · {EngineType} · {IntervalTime}s";

    public static OverflightGeneratorRowViewModel FromConfig(OverflightGeneratorConfig cfg) =>
        new()
        {
            Id = cfg.Id,
            FromBearingFrom = cfg.FromBearingFrom,
            FromBearingTo = cfg.FromBearingTo,
            ToBearingFrom = cfg.ToBearingFrom,
            ToBearingTo = cfg.ToBearingTo,
            InitialDistance = cfg.InitialDistance,
            MaxDistance = cfg.MaxDistance,
            AltitudeMin = cfg.AltitudeMin,
            AltitudeMax = cfg.AltitudeMax,
            SnapHemisphericAltitude = cfg.SnapHemisphericAltitude,
            ExitDistance = cfg.ExitDistance,
            EngineType = cfg.EngineType,
            WeightCategory = cfg.WeightCategory,
            StartTimeOffset = cfg.StartTimeOffset,
            MaxTime = cfg.MaxTime,
            IntervalTime = cfg.IntervalTime,
            RandomizeInterval = cfg.RandomizeInterval,
            Enabled = cfg.Enabled,
        };

    public OverflightGeneratorConfig ToConfig() =>
        new()
        {
            Id = Id,
            FromBearingFrom = FromBearingFrom,
            FromBearingTo = FromBearingTo,
            ToBearingFrom = ToBearingFrom,
            ToBearingTo = ToBearingTo,
            InitialDistance = InitialDistance,
            MaxDistance = MaxDistance,
            AltitudeMin = AltitudeMin,
            AltitudeMax = AltitudeMax,
            SnapHemisphericAltitude = SnapHemisphericAltitude,
            ExitDistance = ExitDistance,
            EngineType = EngineType,
            WeightCategory = WeightCategory,
            StartTimeOffset = StartTimeOffset,
            MaxTime = MaxTime,
            IntervalTime = IntervalTime,
            RandomizeInterval = RandomizeInterval,
            Enabled = Enabled,
        };
}
