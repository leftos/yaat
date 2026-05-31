namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Selects which <see cref="IFilletArcGenerator"/> implementation runs after
/// <see cref="GeoJsonParser"/> builds the unfilleted ground graph.
/// </summary>
public enum FilletMode
{
    /// <summary>No fillet pass — raw intersection graph from GeoJSON.</summary>
    None,

    /// <summary>Plan-then-execute fillet generator (the production generator).</summary>
    V2,
}
