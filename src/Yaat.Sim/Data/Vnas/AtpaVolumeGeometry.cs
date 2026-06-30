using Yaat.Sim;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;

namespace Yaat.Sim.Data.Vnas;

/// <summary>Tests whether an aircraft is inside an ATPA volume.</summary>
public static class AtpaVolumeGeometry
{
    /// <summary>Max distance (nm) a runway end may sit from the configured volume threshold to be its match.</summary>
    private const double RunwayThresholdMatchNm = 0.5;

    /// <summary>The runway end whose threshold the volume is anchored on — its true approach course and designator.</summary>
    private readonly record struct VolumeRunwayMatch(double TrueHeadingDeg, string? Designator);

    /// <summary>
    /// Resolves the runway end the volume's configured threshold sits on (within
    /// <see cref="RunwayThresholdMatchNm"/>): its true approach course and zero-padded designator. vNAS
    /// stores the volume as a MAGNETIC <c>magneticHeading</c> rounded to the runway's whole-degree
    /// designator (e.g. 88 for runway 8R); converting that rounded value by the airport declination
    /// compounds the rounding and, on closely-spaced parallels, rotates the volume enough to pull the
    /// neighboring runway's traffic in. Resolving against the actual runway threshold avoids that. Falls
    /// back to the configured heading (as true) with no designator when no runway matches.
    /// </summary>
    private static VolumeRunwayMatch ResolveVolumeRunway(AtpaVolumeConfig volume)
    {
        var navDb = NavigationDatabase.InstanceOrNull;
        if (navDb is not null)
        {
            var threshold = new LatLon(volume.RunwayThreshold.Lat, volume.RunwayThreshold.Lon);
            var bestDistNm = RunwayThresholdMatchNm;
            double? bestHeading = null;
            string? bestDesignator = null;
            foreach (var runway in navDb.GetRunways(volume.AirportId))
            {
                var d1 = GeoMath.DistanceNm(threshold, new LatLon(runway.Lat1, runway.Lon1));
                if (d1 < bestDistNm)
                {
                    bestDistNm = d1;
                    bestHeading = runway.TrueHeading1.Degrees;
                    bestDesignator = runway.Id.End1;
                }

                var d2 = GeoMath.DistanceNm(threshold, new LatLon(runway.Lat2, runway.Lon2));
                if (d2 < bestDistNm)
                {
                    bestDistNm = d2;
                    bestHeading = runway.TrueHeading2.Degrees;
                    bestDesignator = runway.Id.End2;
                }
            }

            if (bestHeading is not null)
            {
                return new VolumeRunwayMatch(bestHeading.Value, bestDesignator);
            }
        }

        return new VolumeRunwayMatch(volume.MagneticHeading, null);
    }

    /// <summary>
    /// The volume centerline heading in TRUE degrees. See <see cref="ResolveVolumeRunway"/> for why the
    /// configured magnetic heading is not declination-converted.
    /// </summary>
    public static double VolumeTrueHeadingDeg(AtpaVolumeConfig volume) => ResolveVolumeRunway(volume).TrueHeadingDeg;

    /// <summary>
    /// The active-approach-end runway designator the volume's threshold matches (zero-padded, e.g. "28R",
    /// "30"); null when no runway matched within <see cref="RunwayThresholdMatchNm"/>. Used to tie-break
    /// best-fit volume association on overlapping parallels via the track's scratchpad runway.
    /// </summary>
    public static string? VolumeRunwayDesignator(AtpaVolumeConfig volume) => ResolveVolumeRunway(volume).Designator;

    /// <summary>
    /// Whether the volume is active. vNAS disables a volume by repointing its <c>airportId</c> at an unrelated
    /// airport (e.g. the SFO side-by volumes set to OVE) while leaving the threshold at the real runway, so a
    /// volume with a non-empty <c>airportId</c> that resolves no runway end within
    /// <see cref="RunwayThresholdMatchNm"/> of its threshold is treated as disabled. A volume with no
    /// <c>airportId</c> (legacy/synthetic, heading taken from the configured magnetic value) is not the
    /// disable pattern and stays active, as does any volume when the nav DB is unavailable.
    /// </summary>
    public static bool IsActiveVolume(AtpaVolumeConfig volume)
    {
        if (string.IsNullOrEmpty(volume.AirportId))
        {
            return true;
        }

        if (NavigationDatabase.InstanceOrNull is null)
        {
            return true;
        }

        return ResolveVolumeRunway(volume).Designator is not null;
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
    /// Signed cross-track offset (nm) of the aircraft from the volume centerline — negative is left of the
    /// outbound (up-the-final) direction, positive is right. Same projection <see cref="IsInside"/> uses
    /// for its width gate; exposed so best-fit association can score nearest-centerline.
    /// </summary>
    public static double CrossTrackNm(AtpaVolumeConfig volume, AircraftState ac)
    {
        var threshold = new LatLon(volume.RunwayThreshold.Lat, volume.RunwayThreshold.Lon);
        var bearingToAc = GeoMath.BearingTo(threshold, ac.Position);
        var distNm = GeoMath.DistanceNm(threshold, ac.Position);
        var angleDiff = (bearingToAc - OutboundTrueHeadingDeg(VolumeTrueHeadingDeg(volume))) * Math.PI / 180.0;
        return distNm * Math.Sin(angleDiff);
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
