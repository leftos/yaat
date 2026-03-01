using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
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
    /// at a parking spot on the ground.
    /// </summary>
    public static PhaseInitResult InitializeAtParking(
        GroundNode parkingNode, double fieldElevation)
    {
        var phases = new PhaseList();
        phases.Add(new AtParkingPhase());

        return new PhaseInitResult
        {
            Phases = phases,
            Latitude = parkingNode.Latitude,
            Longitude = parkingNode.Longitude,
            Heading = parkingNode.Heading ?? 0,
            Altitude = fieldElevation,
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
        double? requestedAltitude = null, double? requestedSpeed = null,
        double? requestedDistanceNm = null)
    {
        double distNm;
        if (requestedDistanceNm is > 0)
        {
            distNm = requestedDistanceNm.Value;
        }
        else if (requestedAltitude is > 0)
        {
            double agl = requestedAltitude.Value - runway.ElevationFt;
            distNm = agl > 0 ? agl / 300.0 : 5.0;
        }
        else
        {
            distNm = 5.0;
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
        phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
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
