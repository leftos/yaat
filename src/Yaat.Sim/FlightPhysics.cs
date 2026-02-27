namespace Yaat.Sim;

public static class FlightPhysics
{
    public static void UpdatePosition(
        AircraftState aircraft,
        double deltaSeconds)
    {
        double speedNmPerSec = aircraft.GroundSpeed / 3600.0;
        double headingRad = aircraft.Heading * Math.PI / 180.0;
        double latRad = aircraft.Latitude * Math.PI / 180.0;

        // 1 degree of latitude = 60 nautical miles
        aircraft.Latitude +=
            speedNmPerSec * deltaSeconds
            * Math.Cos(headingRad) / 60.0;

        // 1 degree of longitude = 60 * cos(lat) nautical miles
        aircraft.Longitude +=
            speedNmPerSec * deltaSeconds
            * Math.Sin(headingRad)
            / (60.0 * Math.Cos(latRad));

        // Normalize heading to [0, 360)
        aircraft.Heading =
            ((aircraft.Heading % 360.0) + 360.0) % 360.0;
    }
}
