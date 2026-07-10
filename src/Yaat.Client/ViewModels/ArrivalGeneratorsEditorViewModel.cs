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

    public ObservableCollection<VfrArrivalGeneratorRowViewModel> VfrArrivalGenerators { get; } = [];

    public ObservableCollection<OverflightGeneratorRowViewModel> OverflightGenerators { get; } = [];

    public IReadOnlyList<string> RunwayOptions { get; }

    public IReadOnlyList<PositionOption> PositionOptions { get; }

    public IReadOnlyList<string> EngineOptions { get; } = ["Jet", "Turboprop", "Piston"];

    public IReadOnlyList<string> WeightOptions { get; } = ["Small", "SmallPlus", "Large", "Heavy"];

    [ObservableProperty]
    private GeneratorRowViewModel? _selectedGenerator;

    [ObservableProperty]
    private VfrArrivalGeneratorRowViewModel? _selectedVfrArrivalGenerator;

    [ObservableProperty]
    private OverflightGeneratorRowViewModel? _selectedOverflightGenerator;

    [ObservableProperty]
    private string? _statusMessage;

    private readonly GeneratorsPayload _initialSnapshot;

    public ArrivalGeneratorsEditorViewModel(
        IReadOnlyList<ScenarioGeneratorConfig> initial,
        IReadOnlyList<VfrArrivalGeneratorConfig> initialVfrArrivals,
        IReadOnlyList<OverflightGeneratorConfig> initialOverflights,
        IReadOnlyList<ScenarioPositionDto> positions,
        IReadOnlyList<string> runways
    )
    {
        _initialSnapshot = Clone(
            new GeneratorsPayload
            {
                AircraftGenerators = [.. initial],
                VfrArrivalGenerators = [.. initialVfrArrivals],
                OverflightGenerators = [.. initialOverflights],
            }
        );

        RunwayOptions = runways;
        PositionOptions = positions.Select(p => new PositionOption(p.Id, p.Callsign, p.Name)).ToList();

        LoadFrom(_initialSnapshot);
    }

    private void LoadFrom(GeneratorsPayload payload)
    {
        var fresh = Clone(payload);

        Generators.Clear();
        foreach (var cfg in fresh.AircraftGenerators)
        {
            Generators.Add(AttachOptions(GeneratorRowViewModel.FromConfig(cfg)));
        }

        VfrArrivalGenerators.Clear();
        foreach (var cfg in fresh.VfrArrivalGenerators)
        {
            VfrArrivalGenerators.Add(AttachOptions(VfrArrivalGeneratorRowViewModel.FromConfig(cfg)));
        }

        OverflightGenerators.Clear();
        foreach (var cfg in fresh.OverflightGenerators)
        {
            OverflightGenerators.Add(AttachOptions(OverflightGeneratorRowViewModel.FromConfig(cfg)));
        }

        SelectedGenerator = Generators.FirstOrDefault();
        SelectedVfrArrivalGenerator = VfrArrivalGenerators.FirstOrDefault();
        SelectedOverflightGenerator = OverflightGenerators.FirstOrDefault();
    }

    private GeneratorRowViewModel AttachOptions(GeneratorRowViewModel row)
    {
        row.RunwayOptions = RunwayOptions;
        row.PositionOptions = PositionOptions;
        row.EngineOptions = EngineOptions;
        row.WeightOptions = WeightOptions;
        return row;
    }

    private VfrArrivalGeneratorRowViewModel AttachOptions(VfrArrivalGeneratorRowViewModel row)
    {
        row.PositionOptions = PositionOptions;
        row.EngineOptions = EngineOptions;
        row.WeightOptions = WeightOptions;
        return row;
    }

    private OverflightGeneratorRowViewModel AttachOptions(OverflightGeneratorRowViewModel row)
    {
        row.EngineOptions = EngineOptions;
        row.WeightOptions = WeightOptions;
        return row;
    }

    [RelayCommand]
    private void AddGenerator()
    {
        var defaultRunway = RunwayOptions.Count > 0 ? RunwayOptions[0] : "";
        var row = AttachOptions(new GeneratorRowViewModel { Id = NewId(), Runway = defaultRunway });
        Generators.Add(row);
        SelectedGenerator = row;
    }

    [RelayCommand]
    private void RemoveGenerator()
    {
        SelectedGenerator = RemoveSelected(Generators, SelectedGenerator);
    }

    [RelayCommand]
    private void AddVfrArrivalGenerator()
    {
        var row = AttachOptions(new VfrArrivalGeneratorRowViewModel { Id = NewId() });
        VfrArrivalGenerators.Add(row);
        SelectedVfrArrivalGenerator = row;
    }

    [RelayCommand]
    private void RemoveVfrArrivalGenerator()
    {
        SelectedVfrArrivalGenerator = RemoveSelected(VfrArrivalGenerators, SelectedVfrArrivalGenerator);
    }

    [RelayCommand]
    private void AddOverflightGenerator()
    {
        var row = AttachOptions(new OverflightGeneratorRowViewModel { Id = NewId() });
        OverflightGenerators.Add(row);
        SelectedOverflightGenerator = row;
    }

    [RelayCommand]
    private void RemoveOverflightGenerator()
    {
        SelectedOverflightGenerator = RemoveSelected(OverflightGenerators, SelectedOverflightGenerator);
    }

    [RelayCommand]
    private void Revert()
    {
        LoadFrom(_initialSnapshot);
        StatusMessage = "Reverted to initial state";
    }

    public string BuildJson() => JsonSerializer.Serialize(BuildPayload());

    public GeneratorsPayload BuildPayload() =>
        new()
        {
            AircraftGenerators = Generators.Select(r => r.ToConfig()).ToList(),
            VfrArrivalGenerators = VfrArrivalGenerators.Select(r => r.ToConfig()).ToList(),
            OverflightGenerators = OverflightGenerators.Select(r => r.ToConfig()).ToList(),
        };

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Removes the selected row and returns the row that should take its place, or null.</summary>
    private static T? RemoveSelected<T>(ObservableCollection<T> rows, T? selected)
        where T : class
    {
        if (selected is null)
        {
            return null;
        }

        var index = rows.IndexOf(selected);
        rows.Remove(selected);
        return rows.Count == 0 ? null : rows[Math.Min(index, rows.Count - 1)];
    }

    private static GeneratorsPayload Clone(GeneratorsPayload src) => JsonSerializer.Deserialize<GeneratorsPayload>(JsonSerializer.Serialize(src))!;
}
