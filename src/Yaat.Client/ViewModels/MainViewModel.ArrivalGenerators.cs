using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Scenarios;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Live state mirror for arrival-generator editing: the generator list and ARTCC position
/// list received with the current ScenarioLoaded broadcast, the airport runway-end IDs
/// derived from the active ground layout, and the originally-loaded scenario JSON used by
/// the editor's Save-As path. Refreshed on ScenarioLoaded and on ArrivalGeneratorsChanged.
/// </summary>
public partial class MainViewModel
{
    public IReadOnlyList<ScenarioGeneratorConfig> LatestArrivalGenerators { get; private set; } = [];

    public IReadOnlyList<ScenarioPositionDto> LatestPositions { get; private set; } = [];

    public string? LoadedScenarioJson { get; private set; }

    public IReadOnlyList<string> LatestRunwayIds
    {
        get
        {
            var layout = Ground.DomainLayout;
            if (layout is null)
            {
                return [];
            }
            var ends = new List<string>();
            foreach (var rwy in layout.Runways)
            {
                foreach (var part in rwy.Name.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!ends.Contains(part))
                    {
                        ends.Add(part);
                    }
                }
            }
            return ends;
        }
    }

    internal void StashLoadedScenarioJson(string scenarioJson)
    {
        LoadedScenarioJson = scenarioJson;
    }

    internal void StashScenarioGeneratorsAndPositions(
        IReadOnlyList<ScenarioGeneratorConfig>? generators,
        IReadOnlyList<ScenarioPositionDto>? positions
    )
    {
        LatestArrivalGenerators = generators ?? [];
        LatestPositions = positions ?? [];
    }

    private void OnArrivalGeneratorsChanged(ArrivalGeneratorsChangedDto dto)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LatestArrivalGenerators = dto.Generators;
            _log.LogInformation("Arrival generators updated: {Count} entries", dto.Generators.Count);
        });
    }
}
