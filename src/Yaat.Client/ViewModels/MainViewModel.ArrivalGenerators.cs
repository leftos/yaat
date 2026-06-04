using Microsoft.Extensions.Logging;
using Yaat.Client.Services;
using Yaat.Sim.Data.Airport;
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

    public IReadOnlyList<string> LatestRunwayIds => UnionRunwayIds(Ground.DomainLayout, LatestArrivalGenerators);

    /// <summary>
    /// The runway-end IDs offered by the generator editor: the union of the active ground layout's
    /// runway ends and the runways already configured on the loaded generators. Unioning the loaded
    /// generators in means an existing generator's runway always appears even before the ground layout
    /// has finished loading (the editor can open first) — or for a runway the layout doesn't list.
    /// </summary>
    internal static List<string> UnionRunwayIds(AirportGroundLayout? layout, IReadOnlyList<ScenarioGeneratorConfig> generators)
    {
        var ends = new List<string>();
        if (layout is not null)
        {
            foreach (var rwy in layout.Runways)
            {
                foreach (var part in rwy.EndDesignators)
                {
                    if (!ends.Contains(part))
                    {
                        ends.Add(part);
                    }
                }
            }
        }
        foreach (var gen in generators)
        {
            if (!string.IsNullOrEmpty(gen.Runway) && !ends.Contains(gen.Runway))
            {
                ends.Add(gen.Runway);
            }
        }
        return ends;
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
