using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Tests.Helpers;

/// <summary>An <see cref="IAirportGroundData"/> that has no layouts — the layout-less airport case.</summary>
internal sealed class NullGroundData : IAirportGroundData
{
    public AirportGroundLayout? GetLayout(string airportId) => null;

    public string? GetSourceGeoJson(string airportId) => null;
}
