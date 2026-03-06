using System.Collections.ObjectModel;
using System.Text.Json;
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

public partial class WeatherEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _artccId = "";

    [ObservableProperty]
    private string? _precipitation;

    public ObservableCollection<WindLayerRow> WindLayers { get; } = [];

    public ObservableCollection<MetarRow> Metars { get; } = [];

    public static List<string?> PrecipitationOptions { get; } = [null, "Rain", "Snow"];

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

    public string BuildJson()
    {
        var profile = new WeatherProfile
        {
            Id = Guid.NewGuid().ToString(),
            ArtccId = ArtccId,
            Name = string.IsNullOrWhiteSpace(Name) ? "Custom Weather" : Name,
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
        return JsonSerializer.Serialize(profile);
    }

    public static WeatherEditorViewModel FromJson(string json)
    {
        var profile = JsonSerializer.Deserialize<WeatherProfile>(json) ?? new WeatherProfile();
        var vm = new WeatherEditorViewModel
        {
            Name = profile.Name,
            ArtccId = profile.ArtccId,
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

    public static WeatherEditorViewModel CreateEmpty(string artccId)
    {
        return new WeatherEditorViewModel { ArtccId = artccId };
    }
}
