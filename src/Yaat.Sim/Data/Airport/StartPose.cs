namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Where an aircraft stands when a route starts at it: its main gear, its heading and its body. This pose decides whether a
/// runway bar at the route's start node holds the aircraft
/// (<see cref="HoldShortAnnotator.StartBarHolds(AirportGroundLayout, GroundNode, GroundNode, StartPose)"/>): a route leading
/// toward the runway holds there while no part of the body has passed the bar; one leading away from it never does.
/// </summary>
/// <param name="MainGear">The main gear position, the aircraft's reference point.</param>
/// <param name="HeadingTrueDeg">The heading (deg true).</param>
/// <param name="Body">The body, from the aircraft's type.</param>
public readonly record struct StartPose(LatLon MainGear, double HeadingTrueDeg, AircraftBody Body)
{
    /// <summary>The pose of <paramref name="aircraft"/> as it stands now.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <returns>Its main gear, heading and body.</returns>
    public static StartPose Of(AircraftState aircraft) => Of(aircraft.Position, aircraft.TrueHeading.Degrees, aircraft.AircraftType);

    /// <summary>The pose of an aircraft of <paramref name="aircraftType"/> standing at <paramref name="position"/>.</summary>
    /// <param name="position">The aircraft's position, its main gear.</param>
    /// <param name="headingTrueDeg">The heading (deg true).</param>
    /// <param name="aircraftType">The aircraft type, which gives the body.</param>
    /// <returns>Its main gear, heading and body.</returns>
    public static StartPose Of(LatLon position, double headingTrueDeg, string aircraftType) =>
        new(position, headingTrueDeg, AircraftBody.Of(aircraftType));

    /// <summary>Where each part of the body lies, nose first (<see cref="AircraftBody.AllParts"/>).</summary>
    /// <returns>The parts' positions.</returns>
    public IEnumerable<LatLon> PartPositions()
    {
        AircraftBody body = Body;
        LatLon mainGear = MainGear;
        double headingDeg = HeadingTrueDeg;
        return AircraftBody.AllParts.Select(part => body.PointOf(part, mainGear, headingDeg));
    }
}
