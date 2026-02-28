namespace Yaat.Sim.Data;

public interface IFixLookup
{
    (double Lat, double Lon)? GetFixPosition(string name);
    double? GetAirportElevation(string code);
}
