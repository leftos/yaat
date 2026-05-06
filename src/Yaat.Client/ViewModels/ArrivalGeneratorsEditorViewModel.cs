using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yaat.Client.Services;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels;

public sealed record PositionOption(string Id, string Callsign, string Name)
{
    public string DisplayLabel => string.IsNullOrEmpty(Callsign) ? Name : $"{Callsign} — {Name}";
}

public partial class ArrivalGeneratorsEditorViewModel : ObservableObject
{
    public ObservableCollection<GeneratorRowViewModel> Generators { get; } = [];

    public IReadOnlyList<string> RunwayOptions { get; }

    public IReadOnlyList<PositionOption> PositionOptions { get; }

    public IReadOnlyList<string> EngineOptions { get; } = ["Jet", "Turboprop", "Piston"];

    public IReadOnlyList<string> WeightOptions { get; } = ["Small", "Large", "Heavy"];

    [ObservableProperty]
    private GeneratorRowViewModel? _selectedGenerator;

    [ObservableProperty]
    private string? _statusMessage;

    private readonly List<ScenarioGeneratorConfig> _initialSnapshot;

    public ArrivalGeneratorsEditorViewModel(
        IReadOnlyList<ScenarioGeneratorConfig> initial,
        IReadOnlyList<ScenarioPositionDto> positions,
        IReadOnlyList<string> runways
    )
    {
        _initialSnapshot = initial.Select(CloneConfig).ToList();
        RunwayOptions = runways;
        PositionOptions = positions.Select(p => new PositionOption(p.Id, p.Callsign, p.Name)).ToList();

        foreach (var cfg in initial)
        {
            Generators.Add(AttachOptions(GeneratorRowViewModel.FromConfig(cfg)));
        }

        SelectedGenerator = Generators.Count > 0 ? Generators[0] : null;
    }

    private GeneratorRowViewModel AttachOptions(GeneratorRowViewModel row)
    {
        row.RunwayOptions = RunwayOptions;
        row.PositionOptions = PositionOptions;
        row.EngineOptions = EngineOptions;
        row.WeightOptions = WeightOptions;
        return row;
    }

    [RelayCommand]
    private void AddGenerator()
    {
        var defaultRunway = RunwayOptions.Count > 0 ? RunwayOptions[0] : "";
        var row = AttachOptions(new GeneratorRowViewModel { Id = Guid.NewGuid().ToString("N"), Runway = defaultRunway });
        Generators.Add(row);
        SelectedGenerator = row;
    }

    [RelayCommand]
    private void RemoveGenerator()
    {
        if (SelectedGenerator is null)
        {
            return;
        }

        var idx = Generators.IndexOf(SelectedGenerator);
        Generators.Remove(SelectedGenerator);
        if (Generators.Count == 0)
        {
            SelectedGenerator = null;
        }
        else
        {
            SelectedGenerator = Generators[Math.Min(idx, Generators.Count - 1)];
        }
    }

    [RelayCommand]
    private void Revert()
    {
        Generators.Clear();
        foreach (var cfg in _initialSnapshot)
        {
            Generators.Add(AttachOptions(GeneratorRowViewModel.FromConfig(CloneConfig(cfg))));
        }
        SelectedGenerator = Generators.Count > 0 ? Generators[0] : null;
        StatusMessage = "Reverted to initial state";
    }

    public string BuildJson()
    {
        var configs = Generators.Select(r => r.ToConfig()).ToList();
        return JsonSerializer.Serialize(configs);
    }

    private static ScenarioGeneratorConfig CloneConfig(ScenarioGeneratorConfig src)
    {
        var json = JsonSerializer.Serialize(src);
        return JsonSerializer.Deserialize<ScenarioGeneratorConfig>(json)!;
    }
}
