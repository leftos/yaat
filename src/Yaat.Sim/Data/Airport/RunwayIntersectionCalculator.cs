using Yaat.Sim.Phases;

namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Computes the intersection point of two runway centerlines.
/// Used for LAHSO (Land and Hold Short Operations) to determine
/// where the landing runway crosses the hold-short runway.
/// </summary>
public static class RunwayIntersectionCalculator
{
    /// <summary>
    /// Finds the centerline intersection between two directional <see cref="RunwayInfo"/> instances.
    /// Distances are measured from each runway's <em>pavement</em> threshold, deliberately: the only
    /// consumer is same-runway/intersecting-runway separation, where the aircraft being measured is a
    /// departure rolling from the pavement (7110.65 §3-9-6). <c>SoloTrainingEvaluator</c> compares these
    /// against its pavement-datum along-track, so both sides stay on one datum.
    /// </summary>
    public static (double Lat, double Lon, double FirstDistFromThresholdNm, double SecondDistFromThresholdNm)? FindIntersection(
        RunwayInfo firstRunway,
        RunwayInfo secondRunway
    )
    {
        var result = GeoMath.SegmentsIntersect(
            firstRunway.Lat1,
            firstRunway.Lon1,
            firstRunway.Lat2,
            firstRunway.Lon2,
            secondRunway.Lat1,
            secondRunway.Lon1,
            secondRunway.Lat2,
            secondRunway.Lon2
        );
        if (result is null)
        {
            return null;
        }

        var intersection = new LatLon(result.Value.Lat, result.Value.Lon);
        double firstDistanceNm = GeoMath.DistanceNm(new LatLon(firstRunway.ThresholdLatitude, firstRunway.ThresholdLongitude), intersection);
        double secondDistanceNm = GeoMath.DistanceNm(new LatLon(secondRunway.ThresholdLatitude, secondRunway.ThresholdLongitude), intersection);
        return (result.Value.Lat, result.Value.Lon, firstDistanceNm, secondDistanceNm);
    }

    /// <summary>
    /// Finds where two active runway centerlines would cross when projected up to
    /// <paramref name="maxBeyondDepartureEndNm"/> beyond each departure end.
    /// </summary>
    public static (double Lat, double Lon, double FirstDistFromThresholdNm, double SecondDistFromThresholdNm)? FindProjectedFlightPathIntersection(
        RunwayInfo firstRunway,
        RunwayInfo secondRunway,
        double maxBeyondDepartureEndNm
    )
    {
        double firstLengthNm = (firstRunway.PavementLengthFt / GeoMath.FeetPerNm) + maxBeyondDepartureEndNm;
        double secondLengthNm = (secondRunway.PavementLengthFt / GeoMath.FeetPerNm) + maxBeyondDepartureEndNm;
        var firstThreshold = new LatLon(firstRunway.ThresholdLatitude, firstRunway.ThresholdLongitude);
        var secondThreshold = new LatLon(secondRunway.ThresholdLatitude, secondRunway.ThresholdLongitude);
        var firstProjectedEnd = GeoMath.ProjectPoint(firstThreshold, firstRunway.TrueHeading, firstLengthNm);
        var secondProjectedEnd = GeoMath.ProjectPoint(secondThreshold, secondRunway.TrueHeading, secondLengthNm);

        var result = GeoMath.SegmentsIntersect(firstThreshold, firstProjectedEnd, secondThreshold, secondProjectedEnd);
        if (result is null)
        {
            return null;
        }

        double firstDistanceNm = GeoMath.DistanceNm(firstThreshold, result.Value.Point);
        double secondDistanceNm = GeoMath.DistanceNm(secondThreshold, result.Value.Point);
        return (result.Value.Point.Lat, result.Value.Point.Lon, firstDistanceNm, secondDistanceNm);
    }

    /// <summary>
    /// Finds the intersection point of two runway centerlines.
    /// Returns null if the runways are parallel (no intersection) or
    /// if the intersection falls outside both runway extents.
    /// </summary>
    /// <param name="landingRunway">The runway the aircraft is landing on.</param>
    /// <param name="crossingRunway">The runway to hold short of.</param>
    /// <returns>
    /// The intersection lat/lon and the distance from the landing runway's first coordinate
    /// to the intersection point in nautical miles, or null if no intersection.
    /// </returns>
    public static (double Lat, double Lon, double DistFromStartNm)? FindIntersection(GroundRunway landingRunway, GroundRunway crossingRunway)
    {
        // Try every pair of segments between the two centerlines
        var landCoords = landingRunway.Coordinates;
        var crossCoords = crossingRunway.Coordinates;

        if (landCoords.Count < 2 || crossCoords.Count < 2)
        {
            return null;
        }

        // Accumulate distance along the landing runway centerline
        double cumulativeDistNm = 0;

        for (int i = 0; i < landCoords.Count - 1; i++)
        {
            double segStartDistNm = cumulativeDistNm;
            double segLenNm = GeoMath.DistanceNm(landCoords[i].Lat, landCoords[i].Lon, landCoords[i + 1].Lat, landCoords[i + 1].Lon);

            for (int j = 0; j < crossCoords.Count - 1; j++)
            {
                var result = GeoMath.SegmentsIntersect(
                    landCoords[i].Lat,
                    landCoords[i].Lon,
                    landCoords[i + 1].Lat,
                    landCoords[i + 1].Lon,
                    crossCoords[j].Lat,
                    crossCoords[j].Lon,
                    crossCoords[j + 1].Lat,
                    crossCoords[j + 1].Lon
                );

                if (result is not null)
                {
                    double distAlongSegment = segLenNm * result.Value.T;
                    return (result.Value.Lat, result.Value.Lon, segStartDistNm + distAlongSegment);
                }
            }

            cumulativeDistNm += segLenNm;
        }

        return null;
    }

    /// <summary>
    /// Computes the hold-short distance from the landing runway's landing threshold to
    /// the LAHSO hold-short point. The hold-short point is set back from the
    /// intersection by half the crossing runway width + 200ft RSA buffer.
    ///
    /// Measured from the landing threshold, not the pavement end, so it is the available landing
    /// distance a LAHSO clearance actually offers (7110.65 §3-10-4) — and so it shares a datum with
    /// the along-track distance <c>LandingPhase</c> compares it against.
    /// </summary>
    /// <param name="intersectionDistNm">Distance from landing runway start to intersection.</param>
    /// <param name="landingDesignator">Which end of the runway we're landing on.</param>
    /// <param name="landingRunway">The landing runway (for total length).</param>
    /// <param name="crossingRunwayWidthFt">Width of the crossing runway in feet.</param>
    /// <returns>Distance from the landing threshold to the hold-short point in nautical miles.</returns>
    public static double ComputeHoldShortDistanceNm(
        double intersectionDistNm,
        string landingDesignator,
        GroundRunway landingRunway,
        double crossingRunwayWidthFt
    )
    {
        // Total centerline length
        double totalLenNm = 0;
        for (int i = 0; i < landingRunway.Coordinates.Count - 1; i++)
        {
            totalLenNm += GeoMath.DistanceNm(
                landingRunway.Coordinates[i].Lat,
                landingRunway.Coordinates[i].Lon,
                landingRunway.Coordinates[i + 1].Lat,
                landingRunway.Coordinates[i + 1].Lon
            );
        }

        // Determine distance from the approach threshold
        // GroundRunway.Coordinates go from one end to the other.
        // The designator tells us which end we're approaching from.
        // If the designator matches the "first" end, distance from threshold = intersectionDistNm.
        // If it matches the "second" end, distance from threshold = totalLen - intersectionDistNm.
        double distFromThreshold;
        if (IsFirstEndDesignator(landingDesignator, landingRunway.Name))
        {
            distFromThreshold = intersectionDistNm;
        }
        else
        {
            distFromThreshold = totalLenNm - intersectionDistNm;
        }

        // Set back from intersection: half crossing runway width + 200ft RSA buffer
        double setbackFt = (crossingRunwayWidthFt / 2.0) + 200.0;
        double setbackNm = setbackFt / 6076.12;

        // Coordinates are pavement ends; a displaced landing threshold starts the usable distance
        // that much further down the runway.
        double displacementNm = landingRunway.ThresholdDisplacementForEnd(landingDesignator) / GeoMath.FeetPerNm;

        return distFromThreshold - setbackNm - displacementNm;
    }

    /// <summary>
    /// Determines if a runway designator corresponds to the first end of a GroundRunway.
    /// GroundRunway.Name is like "10R/28L" — the first designator is "10R", second is "28L".
    /// </summary>
    private static bool IsFirstEndDesignator(string designator, string runwayName)
    {
        int slashIdx = runwayName.IndexOf('/');
        if (slashIdx < 0)
        {
            return true;
        }

        string firstEnd = runwayName[..slashIdx];
        return firstEnd.Equals(designator, StringComparison.OrdinalIgnoreCase);
    }
}
