namespace Yaat.Sim.Data.Airport;

/// <summary>
/// One answer of a non-blocking read of an airport's ground data.
/// </summary>
/// <param name="IsLoaded">
/// True when the airport's ground data has been fetched; false when it is not loaded yet (its fetch is running, or a
/// failed fetch waits for its retry), when both other members are null.
/// </param>
/// <param name="Layout">The airport's ground layout, or null when it is loading or has no map.</param>
/// <param name="SourceGeoJson">The GeoJSON <paramref name="Layout"/> was parsed from, or null when there is none.</param>
public readonly record struct GroundDataRead(bool IsLoaded, AirportGroundLayout? Layout, string? SourceGeoJson);
