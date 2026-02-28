using Yaat.Sim.Phases;

namespace Yaat.Sim.Data;

public interface IRunwayLookup
{
    RunwayInfo? GetRunway(string airportCode, string runwayId);
    IReadOnlyList<RunwayInfo> GetRunways(string airportCode);
}
