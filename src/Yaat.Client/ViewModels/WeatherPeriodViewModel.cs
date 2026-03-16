using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim;

namespace Yaat.Client.ViewModels;

public partial class WindLayerRow : ObservableObject
{
    [ObservableProperty]
    private double _altitude;

    [ObservableProperty]
    private double _direction;

    [ObservableProperty]
    private double _speed;

    [ObservableProperty]
    private double? _gusts;
}

public partial class MetarRow : ObservableObject
{
    [ObservableProperty]
    private string _text = "";
}

public partial class WeatherPeriodViewModel : ObservableObject
{
    [ObservableProperty]
    private double _startMinutes;

    [ObservableProperty]
    private double _transitionMinutes;

    [ObservableProperty]
    private string? _precipitation;

    public ObservableCollection<WindLayerRow> WindLayers { get; } = [];

    public ObservableCollection<MetarRow> Metars { get; } = [];

    public static List<string?> PrecipitationOptions { get; } = [null, "Rain", "Snow"];

    public string DisplayLabel => $"Period ({StartMinutes} min)";

    partial void OnStartMinutesChanged(double value)
    {
        OnPropertyChanged(nameof(DisplayLabel));
    }

    [RelayCommand]
    private void AddLayer()
    {
        WindLayers.Add(new WindLayerRow());
    }

    [RelayCommand]
    private void RemoveLayer(WindLayerRow layer)
    {
        WindLayers.Remove(layer);
    }

    [RelayCommand]
    private void AddMetar()
    {
        Metars.Add(new MetarRow());
    }

    [RelayCommand]
    private void RemoveMetar(MetarRow metar)
    {
        Metars.Remove(metar);
    }

    public WeatherPeriod BuildPeriod()
    {
        return new WeatherPeriod
        {
            StartMinutes = StartMinutes,
            TransitionMinutes = TransitionMinutes,
            Precipitation = Precipitation,
            WindLayers = WindLayers
                .Select(w => new WindLayer
                {
                    Id = Guid.NewGuid().ToString(),
                    Altitude = w.Altitude,
                    Direction = w.Direction,
                    Speed = w.Speed,
                    Gusts = w.Gusts,
                })
                .ToList(),
            Metars = Metars.Where(m => !string.IsNullOrWhiteSpace(m.Text)).Select(m => m.Text.Trim()).ToList(),
        };
    }

    public static WeatherPeriodViewModel FromPeriod(WeatherPeriod period)
    {
        var vm = new WeatherPeriodViewModel
        {
            StartMinutes = period.StartMinutes,
            TransitionMinutes = period.TransitionMinutes,
            Precipitation = period.Precipitation,
        };

        foreach (var layer in period.WindLayers)
        {
            vm.WindLayers.Add(
                new WindLayerRow
                {
                    Altitude = layer.Altitude,
                    Direction = layer.Direction,
                    Speed = layer.Speed,
                    Gusts = layer.Gusts,
                }
            );
        }

        foreach (var metar in period.Metars)
        {
            vm.Metars.Add(new MetarRow { Text = metar });
        }

        return vm;
    }

    public static WeatherPeriodViewModel FromProfile(WeatherProfile profile)
    {
        var vm = new WeatherPeriodViewModel
        {
            StartMinutes = 0,
            TransitionMinutes = 0,
            Precipitation = profile.Precipitation,
        };

        foreach (var layer in profile.WindLayers)
        {
            vm.WindLayers.Add(
                new WindLayerRow
                {
                    Altitude = layer.Altitude,
                    Direction = layer.Direction,
                    Speed = layer.Speed,
                    Gusts = layer.Gusts,
                }
            );
        }

        foreach (var metar in profile.Metars)
        {
            vm.Metars.Add(new MetarRow { Text = metar });
        }

        return vm;
    }
}
