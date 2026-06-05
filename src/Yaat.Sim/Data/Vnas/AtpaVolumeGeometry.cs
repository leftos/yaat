using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data.Vnas;

/// <summary>Tests whether an aircraft is inside an ATPA volume.</summary>
public static class AtpaVolumeGeometry
{
    /// <summary>Max distance (nm) a runway end may sit from the configured volume threshold to be its match.</summary>
    private const double RunwayThresholdMatchNm = 0.5;

    /// <summary>
    /// The volume centerline heading in TRUE degrees. Aircraft tracks and great-circle bearings are
    /// true, but vNAS stores the volume as a MAGNETIC <c>magneticHeading</c> rounded to the runway's
    /// whole-degree designator (e.g. 88 for runway 8R). Converting that rounded value by the airport
    /// declination compounds the rounding and, on closely-spaced parallels, rotates the volume enough to
    /// pull the neighboring runway's traffic in. Resolve the actual runway true heading from the
    /// configured threshold instead; fall back to the configured heading (as true) when no runway matches.
    /// </summary>
    public static double VolumeTrueHeadingDeg(AtpaVolumeConfig volume)
    {
        var navDb = NavigationDatabase.InstanceOrNull;
        if (navDb is not null)
        {
            var threshold = new LatLon(volume.RunwayThreshold.Lat, volume.RunwayThreshold.Lon);
            var bestDistNm = RunwayThresholdMatchNm;
            double? bestHeading = null;
            foreach (var runway in navDb.GetRunways(volume.AirportId))
            {
                var d1 = GeoMath.DistanceNm(threshold, new LatLon(runway.Lat1, runway.Lon1));
                if (d1 < bestDistNm)
                {
                    bestDistNm = d1;
                    bestHeading = runway.TrueHeading1.Degrees;
                }

                var d2 = GeoMath.DistanceNm(threshold, new LatLon(runway.Lat2, runway.Lon2));
                if (d2 < bestDistNm)
                {
                    bestDistNm = d2;
                    bestHeading = runway.TrueHeading2.Degrees;
                }
            }

            if (bestHeading is not null)
            {
                return bestHeading.Value;
            }
        }

        return volume.MagneticHeading;
    }

    /// <summary>The reciprocal of the volume's approach heading — the direction the volume extends back up the final.</summary>
    private static double OutboundTrueHeadingDeg(double volumeTrueHeadingDeg) => (volumeTrueHeadingDeg + 180.0) % 360.0;

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

        var volumeTrueHeading = VolumeTrueHeadingDeg(volume);

        // Heading deviation check: aircraft fly the approach course inbound, so compare the aircraft's
        // true track against the volume's true approach heading.
        var hdgDiff = Math.Abs(HeadingDelta(ac.TrueTrack.Degrees, volumeTrueHeading));
        if (hdgDiff > volume.MaximumHeadingDeviation)
        {
            return false;
        }

        var threshold = new LatLon(volume.RunwayThreshold.Lat, volume.RunwayThreshold.Lon);
        var distNm = GeoMath.DistanceNm(threshold, ac.Position);
        var bearingToAc = GeoMath.BearingTo(threshold, ac.Position);

        // The volume extends OUTBOUND from the threshold — back up the final, opposite the landing
        // direction — because aircraft established on the approach sit behind the threshold relative to
        // the approach course. Project along that reciprocal so on-final arrivals read positive along-track.
        var angleDiff = (bearingToAc - OutboundTrueHeadingDeg(volumeTrueHeading)) * Math.PI / 180.0;
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
        var threshold = new LatLon(volume.RunwayThreshold.Lat, volume.RunwayThreshold.Lon);
        var bearingToAc = GeoMath.BearingTo(threshold, ac.Position);
        var distNm = GeoMath.DistanceNm(threshold, ac.Position);
        var angleDiff = (bearingToAc - OutboundTrueHeadingDeg(VolumeTrueHeadingDeg(volume))) * Math.PI / 180.0;
        return distNm * Math.Cos(angleDiff);
    }

    /// <summary>
    /// Max heading deviation from the volume approach course for the approach-established filter (degrees).
    /// Deliberately tighter than the volume's own <c>MaximumHeadingDeviation</c> membership gate: the
    /// configured gate decides whether a track is geometrically in the box at all, while this tolerance
    /// is the stricter "actually flying the final" test that keeps crossing/turning traffic out of the
    /// in-trail sequence.
    /// </summary>
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

        var hdgDiff = Math.Abs(HeadingDelta(ac.TrueTrack.Degrees, VolumeTrueHeadingDeg(volume)));
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
