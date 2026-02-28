using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Scenarios;

/// <summary>
/// Result of a phase-based aircraft position initialization.
/// Contains the phase list and computed position/speed.
/// </summary>
public sealed class PhaseInitResult
{
    public required PhaseList Phases { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double Heading { get; init; }
    public double Altitude { get; init; }
    public double Speed { get; init; }
    public bool IsOnGround { get; init; }
}

public static class AircraftInitializer
{
    /// <summary>
    /// Creates the phase list and starting state for an aircraft
    /// lined up and waiting on a runway.
    /// </summary>
    public static PhaseInitResult InitializeOnRunway(RunwayInfo runway)
    {
        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new LinedUpAndWaitingPhase());
        phases.Add(new TakeoffPhase());
        phases.Add(new InitialClimbPhase());

        return new PhaseInitResult
        {
            Phases = phases,
            Latitude = runway.ThresholdLatitude,
            Longitude = runway.ThresholdLongitude,
            Heading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            Speed = 0,
            IsOnGround = true,
        };
    }

    /// <summary>
    /// Creates the phase list and starting state for an aircraft
    /// on final approach at a distance derived from altitude or defaulting to 5nm.
    /// </summary>
    public static PhaseInitResult InitializeOnFinal(
        RunwayInfo runway, AircraftCategory category,
        double? requestedAltitude = null, double? requestedSpeed = null)
    {
        // Distance from threshold (default 5nm if not specified via altitude hint)
        double distNm = 5.0;
        if (requestedAltitude is > 0)
        {
            double agl = requestedAltitude.Value - runway.ElevationFt;
            if (agl > 0)
            {
                distNm = agl / 300.0;
            }
        }

        double alt = GlideSlopeGeometry.AltitudeAtDistance(distNm, runway.ElevationFt);
        double speed = requestedSpeed ?? CategoryPerformance.ApproachSpeed(category);

        // Position aircraft on extended centerline
        double reciprocalHeading = (runway.TrueHeading + 180.0) % 360.0;
        double reciprocalRad = reciprocalHeading * Math.PI / 180.0;
        double latRad = runway.ThresholdLatitude * Math.PI / 180.0;
        double nmPerDegLat = 60.0;

        double lat = runway.ThresholdLatitude
            + (distNm * Math.Cos(reciprocalRad) / nmPerDegLat);
        double lon = runway.ThresholdLongitude
            + (distNm * Math.Sin(reciprocalRad) / (nmPerDegLat * Math.Cos(latRad)));

        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new FinalApproachPhase());
        phases.Add(new LandingPhase());

        return new PhaseInitResult
        {
            Phases = phases,
            Latitude = lat,
            Longitude = lon,
            Heading = runway.TrueHeading,
            Altitude = alt,
            Speed = speed,
            IsOnGround = false,
        };
    }
}
