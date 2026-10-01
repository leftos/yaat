using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Client.ContextMenus;

/// <summary>The runway designators the context menus' runway pickers list for an airport.</summary>
internal static class RunwayDesignators
{
    /// <summary>
    /// Every runway end at <paramref name="airport"/> once, in display form ("8R", not "08R"), sorted by number then
    /// suffix; none when the navigation data has no runways for it.
    /// </summary>
    public static IReadOnlyList<string> ForAirport(string airport)
    {
        IReadOnlyList<RunwayInfo> runways = NavigationDatabase.Instance.GetRunways(airport);
        if (runways.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (RunwayInfo rwy in runways)
        {
            // De-pad for the picker labels (FAA form). The selected value is re-normalized
            // server-side, so the command still resolves the runway. Dedup on the canonical
            // end so both representations of a runway collapse to one entry.
            if (!string.IsNullOrEmpty(rwy.Id.End1) && seen.Add(rwy.Id.End1))
            {
                result.Add(RunwayIdentifier.ToDisplayDesignator(rwy.Id.End1));
            }

            if (!string.IsNullOrEmpty(rwy.Id.End2) && seen.Add(rwy.Id.End2))
            {
                result.Add(RunwayIdentifier.ToDisplayDesignator(rwy.Id.End2));
            }
        }

        result.Sort(RunwayDesignatorComparer.Instance);
        return result;
    }
}
