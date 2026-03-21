using Yaat.Sim;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data.Vnas;

/// <summary>Tests whether an aircraft is inside an ATPA volume.</summary>
public static class AtpaVolumeGeometry
{
    /// <summary>
    /// Returns true if the aircraft position is inside the ATPA volume.
    /// Volume is defined as a rectangle from the runway threshold extending 'length' nm
    /// along the magnetic heading, with widthLeft/widthRight in feet from centerline,
    /// altitude between floor and ceiling (ft * 100), and ground track within heading deviation.
    /// </summary>
    public static bool IsInside(AtpaVolumeConfig volume, AircraftState ac)
    {
        // Altitude check: floor/ceiling are in hundreds of feet
        var altHundreds = (int)(ac.Altitude / 100);
        if (altHundreds < volume.Floor || altHundreds > volume.Ceiling)
        {
            return false;
        }

        // Heading deviation check
        var hdgDiff = Math.Abs(HeadingDelta(ac.TrueTrack.Degrees, volume.MagneticHeading));
        if (hdgDiff > volume.MaximumHeadingDeviation)
        {
            return false;
        }

        var thresholdLat = volume.RunwayThreshold.Lat;
        var thresholdLon = volume.RunwayThreshold.Lon;

        var distNm = GeoMath.DistanceNm(thresholdLat, thresholdLon, ac.Latitude, ac.Longitude);
        var bearingToAc = GeoMath.BearingTo(thresholdLat, thresholdLon, ac.Latitude, ac.Longitude);

        // Along-track and cross-track relative to volume centerline
        var angleDiff = (bearingToAc - volume.MagneticHeading) * Math.PI / 180.0;
        var alongTrack = distNm * Math.Cos(angleDiff);
        var crossTrack = distNm * Math.Sin(angleDiff);

        // Along-track: must be between 0 (threshold) and volume length
        if (alongTrack < 0 || alongTrack > volume.Length)
        {
            return false;
        }

        // Cross-track: widthLeft/widthRight are in feet
        var crossTrackFeet = crossTrack * GeoMath.FeetPerNm;
        if (crossTrackFeet < -volume.WidthLeft || crossTrackFeet > volume.WidthRight)
        {
            return false;
        }

        return true;
    }

    /// <summary>Along-track distance from runway threshold along the volume centerline (nm).</summary>
    public static double DistanceFromThreshold(AtpaVolumeConfig volume, AircraftState ac)
    {
        var bearingToAc = GeoMath.BearingTo(volume.RunwayThreshold.Lat, volume.RunwayThreshold.Lon, ac.Latitude, ac.Longitude);
        var distNm = GeoMath.DistanceNm(volume.RunwayThreshold.Lat, volume.RunwayThreshold.Lon, ac.Latitude, ac.Longitude);
        var angleDiff = (bearingToAc - volume.MagneticHeading) * Math.PI / 180.0;
        return distNm * Math.Cos(angleDiff);
    }

    /// <summary>Max heading deviation from volume heading for approach-established filter (degrees).</summary>
    public const double ApproachHeadingTolerance = 30.0;

    /// <summary>Max vertical speed (fpm) — aircraft climbing faster than this are excluded from ATPA.</summary>
    public const double MaxVerticalSpeedFpm = 100.0;

    /// <summary>
    /// Returns true if the aircraft exhibits approach-like behavior: airborne, not climbing,
    /// and heading roughly aligned with the volume approach course. This filters out departures,
    /// overflights, and vectored traffic that happen to be inside the ATPA volume.
    /// </summary>
    public static bool IsEstablishedOnApproach(AtpaVolumeConfig volume, AircraftState ac)
    {
        if (ac.IsOnGround)
        {
            return false;
        }

        if (ac.VerticalSpeed > MaxVerticalSpeedFpm)
        {
            return false;
        }

        var hdgDiff = Math.Abs(HeadingDelta(ac.TrueTrack.Degrees, volume.MagneticHeading));
        if (hdgDiff > ApproachHeadingTolerance)
        {
            return false;
        }

        return true;
    }

    public static double HeadingDelta(double a, double b)
    {
        return ((a - b) % 360 + 540) % 360 - 180;
    }
}
