namespace Yaat.Sim.Data.Airport.Pathfinding;

/// <summary>
/// A bare taxi's end past a crossed runway's far bar, as the route reports it: the crossing it clears and the advisory the
/// readback carries.
/// </summary>
/// <param name="Runway">The runway crossed, as the controller reads it ("17R/35L").</param>
/// <param name="NearTaxiway">The taxiway the crossing leaves from, on which its near bar lies.</param>
/// <param name="CarriedInto">The taxiway the end protrudes into, or null when it stays on the final cleared taxiway.</param>
/// <param name="Advisory">
/// The pathfinder's advisory ("route ends on C2 clear of 17R/35L, no destination given"); the TAXI handler prefixes how the
/// near bar stands once auto-cross and the CROSS keywords have applied.
/// </param>
public sealed record ClearOfRunwayEnd(string Runway, string NearTaxiway, string? CarriedInto, string Advisory);

/// <summary>
/// The nodes a search ended a bare taxi on past a crossed runway's far bar, each with its <see cref="ClearOfRunwayEnd"/>. One
/// per search, shared across <c>with</c> copies: every probe records here, and only the route that ends on a recorded node
/// reports it.
/// </summary>
public sealed class ClearOfRunwayEnds
{
    private readonly Dictionary<int, ClearOfRunwayEnd> _ends = [];

    /// <summary>Record that the resolver ended a bare crossing at <paramref name="endNodeId"/>.</summary>
    public void Record(int endNodeId, ClearOfRunwayEnd end) => _ends[endNodeId] = end;

    /// <summary>The bare-crossing end the resolver recorded at <paramref name="nodeId"/>, or null.</summary>
    public ClearOfRunwayEnd? At(int nodeId) => _ends.GetValueOrDefault(nodeId);
}
