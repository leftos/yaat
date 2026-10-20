using Yaat.Sim.Data;

namespace Yaat.Sim.Commands;

/// <summary>
/// The segment of an airway a <c>JAWY</c> intercepts: the fix behind and the fix ahead of an aircraft, read the same way for the
/// sim's dispatch (<c>NavigationCommandHandler</c>) and for a client showing where a join would proceed.
/// </summary>
public static class AirwayBracketing
{
    /// <summary>
    /// The fix of <paramref name="airwayId"/> a <c>JAWY</c> from <paramref name="position"/> on <paramref name="heading"/>
    /// proceeds to once it intercepts: the fix ahead of the bracketing segment (<see cref="Find"/>); null for an unknown airway
    /// or where the sim finds no navigable segment.
    /// </summary>
    public static string? AheadFix(NavigationDatabase navDb, string airwayId, LatLon position, TrueHeading heading)
    {
        if (
            (navDb.GetAirwayFixes(airwayId) is not { Count: > 0 } airwayFixes)
            || (Find(navDb, position, heading, airwayFixes).AheadIdx is not (>= 0 and var ahead))
        )
        {
            return null;
        }

        return airwayFixes[ahead];
    }

    /// <summary>
    /// Finds the bracketing segment on an airway — the fix behind and fix ahead of an aircraft at <paramref name="position"/>
    /// flying <paramref name="heading"/>. Returns (behindIdx, aheadIdx). behindIdx may be -1 if the aircraft is before the
    /// first fix. aheadIdx will be -1 if no valid segment is found. The method considers both forward and reverse traversal
    /// of the airway to determine the direction of travel that best matches the heading.
    /// </summary>
    public static (int BehindIdx, int AheadIdx) Find(
        NavigationDatabase navDb,
        LatLon position,
        TrueHeading heading,
        IReadOnlyList<string> airwayFixes
    )
    {
        // Resolve positions for all fixes
        var positions = new (double Lat, double Lon)?[airwayFixes.Count];
        for (int i = 0; i < airwayFixes.Count; i++)
        {
            positions[i] = navDb.GetFixPosition(airwayFixes[i]);
        }

        // Find the closest fix ahead (within ±90° of heading) and closest fix behind
        int bestAheadIdx = -1;
        double bestAheadDist = double.MaxValue;
        int bestBehindIdx = -1;
        double bestBehindDist = double.MaxValue;

        for (int i = 0; i < airwayFixes.Count; i++)
        {
            if (positions[i] is null)
            {
                continue;
            }

            double bearing = GeoMath.BearingTo(position, new LatLon(positions[i]!.Value.Lat, positions[i]!.Value.Lon));
            double angleDiff = ((bearing - heading.Degrees) % 360 + 360) % 360;
            if (angleDiff > 180)
            {
                angleDiff = 360 - angleDiff;
            }

            double dist = GeoMath.DistanceNm(position, new LatLon(positions[i]!.Value.Lat, positions[i]!.Value.Lon));

            if (angleDiff <= 90)
            {
                if (dist < bestAheadDist)
                {
                    bestAheadDist = dist;
                    bestAheadIdx = i;
                }
            }
            else
            {
                if (dist < bestBehindDist)
                {
                    bestBehindDist = dist;
                    bestBehindIdx = i;
                }
            }
        }

        if (bestAheadIdx < 0)
        {
            return (-1, -1);
        }

        // Verify the behind and ahead fixes form an adjacent segment on the airway
        // (they should be consecutive in forward or reverse order)
        if (bestBehindIdx >= 0)
        {
            int diff = bestAheadIdx - bestBehindIdx;
            if (diff != 1 && diff != -1)
            {
                // Not adjacent — use the fix adjacent to the ahead fix in the correct direction
                int candidateBefore = bestAheadIdx - 1;
                int candidateAfter = bestAheadIdx + 1;

                // Pick the adjacent fix that is behind the aircraft
                bestBehindIdx = -1;
                if (candidateBefore >= 0 && positions[candidateBefore] is not null)
                {
                    double bearing = GeoMath.BearingTo(
                        position,
                        new LatLon(positions[candidateBefore]!.Value.Lat, positions[candidateBefore]!.Value.Lon)
                    );
                    double angleDiff = ((bearing - heading.Degrees) % 360 + 360) % 360;
                    if (angleDiff > 180)
                    {
                        angleDiff = 360 - angleDiff;
                    }

                    if (angleDiff > 90)
                    {
                        bestBehindIdx = candidateBefore;
                    }
                }

                if (bestBehindIdx < 0 && candidateAfter < airwayFixes.Count && positions[candidateAfter] is not null)
                {
                    double bearing = GeoMath.BearingTo(
                        position,
                        new LatLon(positions[candidateAfter]!.Value.Lat, positions[candidateAfter]!.Value.Lon)
                    );
                    double angleDiff = ((bearing - heading.Degrees) % 360 + 360) % 360;
                    if (angleDiff > 180)
                    {
                        angleDiff = 360 - angleDiff;
                    }

                    if (angleDiff > 90)
                    {
                        bestBehindIdx = candidateAfter;
                    }
                }
            }
        }

        return (bestBehindIdx, bestAheadIdx);
    }
}
