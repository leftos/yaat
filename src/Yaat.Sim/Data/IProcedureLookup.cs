using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data;

public interface IProcedureLookup
{
    CifpSidProcedure? GetSid(string airportCode, string sidId);
    IReadOnlyList<CifpSidProcedure> GetSids(string airportCode);
    CifpStarProcedure? GetStar(string airportCode, string starId);
    IReadOnlyList<CifpStarProcedure> GetStars(string airportCode);
}
