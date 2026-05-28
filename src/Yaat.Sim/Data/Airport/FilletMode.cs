namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Selects which <see cref="IFilletArcGenerator"/> implementation runs after
/// <see cref="GeoJsonParser"/> builds the unfilleted ground graph.
/// </summary>
public enum FilletMode
{
    /// <summary>No fillet pass — raw intersection graph from GeoJSON.</summary>
    None,

    /// <summary>Legacy pair-based fillet generator (<see cref="FilletArcGenerator"/>).</summary>
    Legacy,

    /// <summary>Clean-room plan-then-execute fillet generator (V2).</summary>
    V2,
}
