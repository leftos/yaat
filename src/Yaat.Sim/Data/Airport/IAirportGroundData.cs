namespace Yaat.Sim.Data.Airport;

public interface IAirportGroundData
{
    AirportGroundLayout? GetLayout(string airportId);

    string? GetSourceGeoJson(string airportId);
}
