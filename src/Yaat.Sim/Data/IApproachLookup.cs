using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data;

public interface IApproachLookup
{
    CifpApproachProcedure? GetApproach(string airportCode, string approachId);

    IReadOnlyList<CifpApproachProcedure> GetApproaches(string airportCode);

    /// <summary>
    /// Maps user shorthand (e.g., "ILS28R", "I28R", "28R", "RNAV17LZ") to the
    /// full CIFP approach ID for the given airport.
    /// </summary>
    string? ResolveApproachId(string airportCode, string shorthand);
}
