using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Sim;

namespace Yaat.Client.ViewModels;

public partial class WeatherTimelineEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _artccId = "";

    [ObservableProperty]
    private WeatherPeriodViewModel? _selectedPeriod;

    public ObservableCollection<WeatherPeriodViewModel> Periods { get; } = [];

    [RelayCommand]
    private void AddPeriod()
    {
        double startMinutes = Periods.Count > 0 ? Periods[^1].StartMinutes + 10 : 0;
        var period = new WeatherPeriodViewModel { StartMinutes = startMinutes };
        Periods.Add(period);
        SelectedPeriod = period;
    }

    [RelayCommand]
    private void RemovePeriod()
    {
        if (SelectedPeriod is null || Periods.Count <= 1)
        {
            return;
        }

        int idx = Periods.IndexOf(SelectedPeriod);
        Periods.Remove(SelectedPeriod);
        SelectedPeriod = Periods[Math.Min(idx, Periods.Count - 1)];
    }

    /// <summary>
    /// Builds JSON output. Single period → v1 WeatherProfile; multiple periods → v2 WeatherTimeline.
    /// </summary>
    public string BuildJson()
    {
        if (Periods.Count == 1)
        {
            return BuildV1Json();
        }

        return BuildV2Json();
    }

    private string BuildV1Json()
    {
        var period = Periods[0];
        var profile = new WeatherProfile
        {
            Id = Guid.NewGuid().ToString(),
            ArtccId = ArtccId,
            Name = string.IsNullOrWhiteSpace(Name) ? "Custom Weather" : Name,
            Precipitation = period.Precipitation,
            WindLayers = period.BuildPeriod().WindLayers,
            Metars = period.BuildPeriod().Metars,
        };
        return JsonSerializer.Serialize(profile);
    }

    private string BuildV2Json()
    {
        var timeline = new WeatherTimeline
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "Custom Weather" : Name,
            ArtccId = ArtccId,
            Periods = Periods.Select(p => p.BuildPeriod()).ToList(),
        };
        return JsonSerializer.Serialize(timeline);
    }

    public static WeatherTimelineEditorViewModel FromJson(string json)
    {
        var parseResult = WeatherTimelineParser.Parse(json);

        if (parseResult.IsTimeline)
        {
            var timeline = parseResult.Timeline!;
            var vm = new WeatherTimelineEditorViewModel { Name = timeline.Name, ArtccId = timeline.ArtccId };

            foreach (var period in timeline.Periods)
            {
                vm.Periods.Add(WeatherPeriodViewModel.FromPeriod(period));
            }

            if (vm.Periods.Count > 0)
            {
                vm.SelectedPeriod = vm.Periods[0];
            }

            return vm;
        }

        if (parseResult.IsProfile)
        {
            var profile = parseResult.Profile!;
            var vm = new WeatherTimelineEditorViewModel { Name = profile.Name, ArtccId = profile.ArtccId };

            vm.Periods.Add(WeatherPeriodViewModel.FromProfile(profile));
            vm.SelectedPeriod = vm.Periods[0];
            return vm;
        }

        // Fallback: empty
        return CreateEmpty("");
    }

    public static WeatherTimelineEditorViewModel CreateEmpty(string artccId)
    {
        var vm = new WeatherTimelineEditorViewModel { ArtccId = artccId };
        var period = new WeatherPeriodViewModel { StartMinutes = 0 };
        vm.Periods.Add(period);
        vm.SelectedPeriod = period;
        return vm;
    }
}
