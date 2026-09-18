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
    public LatLon Position { get; init; }
    public TrueHeading TrueHeading { get; init; }
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
    public static PhaseInitResult InitializeOnRunway(RunwayInfo runway, AircraftCategory category = AircraftCategory.Jet)
    {
        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new LinedUpAndWaitingPhase());
        bool isHeli = category == AircraftCategory.Helicopter;
        phases.Add(isHeli ? new HelicopterTakeoffPhase() : new TakeoffPhase());
        phases.Add(new InitialClimbPhase());

        return new PhaseInitResult
        {
            Phases = phases,
            Position = new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude),
            TrueHeading = runway.TrueHeading,
            Altitude = runway.ElevationFt,
            Speed = 0,
            IsOnGround = true,
        };
    }

    /// <summary>
    /// Creates the phase list and starting state for an aircraft
    /// at a parking spot on the ground.
    /// </summary>
    public static PhaseInitResult InitializeAtParking(GroundNode parkingNode, double fieldElevation)
    {
        var phases = new PhaseList();
        phases.Add(new AtParkingPhase());

        return new PhaseInitResult
        {
            Phases = phases,
            Position = parkingNode.Position,
            TrueHeading = parkingNode.TrueHeading ?? new TrueHeading(0),
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
        RunwayInfo runway,
        AircraftCategory category,
        string callsign,
        double? requestedAltitude = null,
        double? requestedSpeed = null,
        double? requestedDistanceNm = null,
        string? aircraftType = null
    )
    {
        double gsAngle = GlideSlopeGeometry.AngleForCategory(category);
        double distNm;
        if (requestedDistanceNm is > 0)
        {
            distNm = requestedDistanceNm.Value;
        }
        else if (requestedAltitude is > 0)
        {
            double agl = requestedAltitude.Value - runway.ElevationFt;
            distNm = agl > 0 ? agl / GlideSlopeGeometry.FeetPerNm(gsAngle) : 5.0;
        }
        else
        {
            distNm = 5.0;
        }

        (LatLon position, double alt) = FinalApproachPoint(runway, category, distNm);
        double speed;
        if (requestedSpeed.HasValue)
        {
            speed = requestedSpeed.Value;
        }
        else
        {
            double fas = AircraftPerformance.ApproachSpeed(aircraftType ?? string.Empty, category);
            speed = FinalApproachSpeedSchedule.SpeedAtDistanceKts(aircraftType ?? string.Empty, category, fas, callsign, distNm);
        }

        var phases = new PhaseList { AssignedRunway = runway };
        phases.Add(new FinalApproachPhase { SkipInterceptCheck = true });
        bool isHeli = category == AircraftCategory.Helicopter;
        phases.Add(isHeli ? new HelicopterLandingPhase() : new LandingPhase());

        return new PhaseInitResult
        {
            Phases = phases,
            Position = position,
            TrueHeading = runway.TrueHeading,
            Altitude = alt,
            Speed = speed,
            IsOnGround = false,
        };
    }

    /// <summary>
    /// Where <see cref="InitializeOnFinal"/> places an arrival <paramref name="distanceNm"/> out on the final of
    /// <paramref name="runway"/>: on the extended centreline, at the glidepath altitude (MSL) an aircraft of
    /// <paramref name="category"/> flies there.
    /// </summary>
    public static (LatLon Position, double AltitudeFt) FinalApproachPoint(RunwayInfo runway, AircraftCategory category, double distanceNm)
    {
        double altitudeFt = GlideSlopeGeometry.AltitudeAtDistance(distanceNm, runway.ElevationFt, category);

        TrueHeading reciprocal = runway.TrueHeading.ToReciprocal();
        double reciprocalRad = reciprocal.ToRadians();
        double latRad = runway.ThresholdLatitude * Math.PI / 180.0;
        double nmPerDegLat = 60.0;

        double lat = runway.ThresholdLatitude + (distanceNm * Math.Cos(reciprocalRad) / nmPerDegLat);
        double lon = runway.ThresholdLongitude + (distanceNm * Math.Sin(reciprocalRad) / (nmPerDegLat * Math.Cos(latRad)));
        return (new LatLon(lat, lon), altitudeFt);
    }
}
