namespace Yaat.Sim.Data.Airport;

public interface IAirportGroundData
{
    AirportGroundLayout? GetLayout(string airportId);
}
