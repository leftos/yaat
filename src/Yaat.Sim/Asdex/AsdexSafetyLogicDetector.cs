using Yaat.Sim;

namespace Yaat.Sim.Asdex;

/// <summary>
/// The kind of ASDE-X Safety Logic alert, per the CRC ASDE-X manual ("Safety Logic"). Only
/// the conditions that are derivable from the data YAAT actually has are modelled:
/// converging-runway alerts are intentionally absent (they require a runway-pair convergence
/// relationship that is not present in any reachable vNAS data).
/// </summary>
public enum AsdexAlertKind
{
    /// <summary>An aircraft landing or departing on a closed runway.</summary>
    ClosedRunway,

    /// <summary>An aircraft landing/departing on a runway already occupied by another aircraft.</summary>
    OccupiedRunway,

    /// <summary>An aircraft taxiing onto a runway with an active arrival or departure.</summary>
    TaxiOntoActiveRunway,

    /// <summary>An aircraft landing on a taxiway rather than a runway.</summary>
    TaxiwayLanding,
}

/// <summary>One active runway in the ASDE-X safety-logic configuration: its identifier (e.g.
/// "28R"), footprint polygon (lat/lon ring, as supplied by CRC), closed state, and the local
/// magnetic variation (east-positive declination) so the magnetic runway-id heading can be
/// compared against the aircraft's true heading.</summary>
public sealed record AsdexRunwaySurface(string Id, IReadOnlyList<LatLon> Area, bool IsClosed, double MagneticVariationDeg);

/// <summary>A taxiway centerline segment from the airport ground graph, used to approximate
/// taxiway-landing detection (the ground layout carries centerlines, not area polygons).</summary>
public sealed record AsdexTaxiwaySegment(string Name, LatLon Start, LatLon End);

/// <summary>A single emitted Safety Logic alert.</summary>
public sealed record AsdexSafetyAlert(
    string Id,
    AsdexAlertKind Kind,
    IReadOnlyList<string> RunwayIds,
    IReadOnlyList<string> Callsigns,
    IReadOnlyList<string> MessageLines,
    bool PlayAuralAlert
);

/// <summary>
/// Stateless per-tick classifier for ASDE-X Safety Logic alerts. Mirrors CRC's simplified
/// binary alerting (not the real ASDE-X time-to-collision look-ahead) per the controller doc.
///
/// Load-bearing approximations (flagged for aviation review):
/// - An arrival is treated as "still airborne over/short-of the runway" up to
///   <see cref="ArrivalAglCeilingFt"/> AGL; there is no approach-corridor projection, so an
///   occupied/closed-runway alert fires once the arrival is over the runway footprint rather
///   than on short final.
/// - "Using" vs "crossing" a runway is distinguished by alignment of the aircraft's true heading
///   with the runway's true heading — the magnetic heading parsed from the identifier plus the
///   local magnetic variation (±<see cref="AlignmentToleranceDeg"/>).
/// - Taxiway-landing uses centerline proximity (the ground layout has no taxiway polygons) and
///   alerts on any taxiway, since "configured taxiway" data is not available.
/// </summary>
public static class AsdexSafetyLogicDetector
{
    /// <summary>Arrival is treated as airborne over the runway up to this AGL (7110.65 §3-6-4).</summary>
    private const double ArrivalAglCeilingFt = 300;

    /// <summary>Heading tolerance (deg) for treating an aircraft as aligned with a runway.</summary>
    private const double AlignmentToleranceDeg = 35;

    /// <summary>Half-width (nm) within which a landing aircraft counts as over a taxiway (~28 m).</summary>
    private const double TaxiwayLandingHalfWidthNm = 0.015;

    public static IReadOnlyList<AsdexSafetyAlert> Detect(
        IReadOnlyList<AsdexRunwaySurface> runways,
        IReadOnlyList<AsdexTaxiwaySegment> taxiways,
        IReadOnlyList<AircraftState> aircraft,
        double fieldElevationFt
    )
    {
        // Individually-inhibited targets (square drawn around them in CRC) take part in no alert.
        var candidates = aircraft.Where(ac => !ac.Stars.AsdexAlertsInhibited).ToList();
        var alerts = new List<AsdexSafetyAlert>();

        foreach (var runway in runways)
        {
            var occupants = candidates.Where(ac => PointInPolygon(ac.Position, runway.Area)).ToList();
            if (occupants.Count == 0)
            {
                continue;
            }

            // A "claimant" is an aircraft aligned with the runway that is actively using it: on
            // the surface at any speed (rolling, or lined up and waiting) or arriving at low AGL.
            // Ground speed does NOT gate this — a stationary lined-up departure still claims the
            // runway, which is exactly the LUAW-incursion case Safety Logic must catch.
            var claimants = occupants.Where(ac => ClaimsRunway(ac, runway, fieldElevationFt)).ToList();

            // Condition 1 — closed runway. Any aircraft using a closed runway is an incursion;
            // this is also the only alert the "LIMITED" configuration generates.
            if (runway.IsClosed)
            {
                if (claimants.Count > 0)
                {
                    alerts.Add(BuildAlert(AsdexAlertKind.ClosedRunway, runway, claimants, "CLOSED RWY"));
                }

                continue;
            }

            // No alert without an active arrival/departure claiming the runway and a distinct
            // second aircraft also on it.
            if (claimants.Count == 0 || occupants.Count < 2)
            {
                continue;
            }

            // The other aircraft sharing the runway: a non-aligned aircraft on the surface is a
            // taxi-onto incursion; otherwise the conflict is between arrivals/departures
            // occupying the same runway.
            var crossers = occupants.Where(ac => ac.IsOnGround && !IsAligned(ac, runway)).ToList();
            if (crossers.Count > 0)
            {
                alerts.Add(BuildAlert(AsdexAlertKind.TaxiOntoActiveRunway, runway, occupants, "RWY INCURSION"));
            }
            else
            {
                alerts.Add(BuildAlert(AsdexAlertKind.OccupiedRunway, runway, occupants, "OCCUPIED RWY"));
            }
        }

        AppendTaxiwayLandings(runways, taxiways, candidates, fieldElevationFt, alerts);
        return alerts;
    }

    private static void AppendTaxiwayLandings(
        IReadOnlyList<AsdexRunwaySurface> runways,
        IReadOnlyList<AsdexTaxiwaySegment> taxiways,
        IReadOnlyList<AircraftState> candidates,
        double fieldElevationFt,
        List<AsdexSafetyAlert> alerts
    )
    {
        foreach (var ac in candidates)
        {
            // A taxiway landing is an arrival at low AGL whose position is over a taxiway and
            // not over any runway footprint.
            double agl = ac.Altitude - fieldElevationFt;
            if (ac.IsOnGround || agl > ArrivalAglCeilingFt || agl < -50)
            {
                continue;
            }

            if (runways.Any(runway => PointInPolygon(ac.Position, runway.Area)))
            {
                continue;
            }

            var taxiway = NearestTaxiwayWithin(ac.Position, taxiways, TaxiwayLandingHalfWidthNm);
            if (taxiway is not null)
            {
                alerts.Add(
                    new AsdexSafetyAlert(
                        Id: $"TWYLAND|{taxiway.Name}|{ac.Callsign}",
                        Kind: AsdexAlertKind.TaxiwayLanding,
                        RunwayIds: [],
                        Callsigns: [ac.Callsign],
                        MessageLines: [taxiway.Name, ac.Callsign, "TAXIWAY LANDING"],
                        PlayAuralAlert: true
                    )
                );
            }
        }
    }

    private static AsdexSafetyAlert BuildAlert(
        AsdexAlertKind kind,
        AsdexRunwaySurface runway,
        IReadOnlyList<AircraftState> involved,
        string description
    )
    {
        // Stable id is order-independent so a pair (A,B) collapses to the same alert as (B,A)
        // across ticks; the broadcaster diffs by id, so clearing needs no explicit logic.
        var callsigns = involved.Select(ac => ac.Callsign).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var id = $"{kind}|{runway.Id}|{string.Join(",", callsigns)}";
        return new AsdexSafetyAlert(
            Id: id,
            Kind: kind,
            RunwayIds: [runway.Id],
            Callsigns: callsigns,
            MessageLines: [runway.Id, string.Join(" ", callsigns), description],
            PlayAuralAlert: true
        );
    }

    /// <summary>True if the aircraft heading is within tolerance of the runway's true heading.
    /// The runway-id heading is magnetic, so the local magnetic variation is applied before
    /// comparing against the aircraft's true heading. When the id has no parseable heading,
    /// every occupant is treated as aligned so a conflict is not silently dropped.</summary>
    private static bool IsAligned(AircraftState ac, AsdexRunwaySurface runway)
    {
        if (!TryRunwayHeading(runway.Id, out double magneticHeading))
        {
            return true;
        }

        double trueHeading = magneticHeading + runway.MagneticVariationDeg;
        return AngularDifference(ac.TrueHeading.Degrees, trueHeading) <= AlignmentToleranceDeg;
    }

    /// <summary>True if the aircraft is aligned with the runway and actively using it: on the
    /// surface at any speed (rolling or holding in position) or arriving airborne at/below the
    /// AGL ceiling. A high overflight is excluded by the AGL ceiling.</summary>
    private static bool ClaimsRunway(AircraftState ac, AsdexRunwaySurface runway, double fieldElevationFt)
    {
        if (!IsAligned(ac, runway))
        {
            return false;
        }

        return ac.IsOnGround || (ac.Altitude - fieldElevationFt) <= ArrivalAglCeilingFt;
    }

    private static bool TryRunwayHeading(string runwayId, out double headingDegrees)
    {
        headingDegrees = 0;
        int digits = 0;
        var span = runwayId.AsSpan();
        int i = 0;
        while (i < span.Length && char.IsDigit(span[i]) && i < 2)
        {
            digits = (digits * 10) + (span[i] - '0');
            i++;
        }

        if (i == 0)
        {
            return false;
        }

        headingDegrees = digits * 10.0;
        return headingDegrees is > 0 and <= 360;
    }

    private static double AngularDifference(double a, double b)
    {
        double diff = Math.Abs(a - b) % 360.0;
        return diff > 180.0 ? 360.0 - diff : diff;
    }

    private static AsdexTaxiwaySegment? NearestTaxiwayWithin(LatLon point, IReadOnlyList<AsdexTaxiwaySegment> taxiways, double maxDistanceNm)
    {
        AsdexTaxiwaySegment? best = null;
        double bestDistance = maxDistanceNm;
        foreach (var taxiway in taxiways)
        {
            double distance = PointToSegmentNm(point, taxiway.Start, taxiway.End);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                best = taxiway;
            }
        }

        return best;
    }

    // ── Geometry (equirectangular planar approximation, accurate at airport scale) ──

    private static bool PointInPolygon(LatLon point, IReadOnlyList<LatLon> polygon)
    {
        if (polygon.Count < 3)
        {
            return false;
        }

        bool inside = false;
        double x = point.Lon;
        double y = point.Lat;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            double xi = polygon[i].Lon;
            double yi = polygon[i].Lat;
            double xj = polygon[j].Lon;
            double yj = polygon[j].Lat;
            bool intersects = ((yi > y) != (yj > y)) && (x < ((xj - xi) * (y - yi) / (yj - yi)) + xi);
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double PointToSegmentNm(LatLon point, LatLon start, LatLon end)
    {
        // Project to local nm coordinates around the segment start (x = east, y = north).
        double latRad = start.Lat * Math.PI / 180.0;
        double lonScale = Math.Cos(latRad);
        const double NmPerDegree = 60.0;

        double px = (point.Lon - start.Lon) * lonScale * NmPerDegree;
        double py = (point.Lat - start.Lat) * NmPerDegree;
        double ex = (end.Lon - start.Lon) * lonScale * NmPerDegree;
        double ey = (end.Lat - start.Lat) * NmPerDegree;

        double segLengthSq = (ex * ex) + (ey * ey);
        if (segLengthSq < 1e-12)
        {
            return Math.Sqrt((px * px) + (py * py));
        }

        double t = Math.Clamp(((px * ex) + (py * ey)) / segLengthSq, 0.0, 1.0);
        double cx = t * ex;
        double cy = t * ey;
        double dx = px - cx;
        double dy = py - cy;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
