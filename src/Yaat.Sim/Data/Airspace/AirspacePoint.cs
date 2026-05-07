namespace Yaat.Sim.Data.Airspace;

public readonly record struct AirspacePoint(double Lat, double Lon)
{
    public LatLon ToLatLon() => new(Lat, Lon);

    public static AirspacePoint FromLatLon(LatLon point) => new(point.Lat, point.Lon);
}
