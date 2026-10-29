using Yaat.Sim;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Asdex;

/// <summary>
/// The kind of ASDE-X Safety Logic alert, per the CRC ASDE-X manual ("Safety Logic"). Only
/// the conditions that are derivable from the data YAAT actually has are modelled:
/// converging-runway alerts are intentionally absent (they require a runway-pair convergence
/// relationship that is not present in any reachable vNAS data). Snapshots store the kind by number, so each member
/// keeps its value; 2 is unused (<c>SnapshotSchemaMigrator</c> rewrites a stored 2 to <see cref="OccupiedRunway"/>).
/// </summary>
public enum AsdexAlertKind
{
    /// <summary>An aircraft landing or departing on a closed runway.</summary>
    ClosedRunway = 0,

    /// <summary>
    /// An aircraft landing/departing on a runway another aircraft is on, whether that aircraft is also using the runway
    /// or crossing it: vNAS raises no separate alert for an aircraft taxiing onto an active runway.
    /// </summary>
    OccupiedRunway = 1,

    /// <summary>An aircraft landing on a taxiway rather than a runway.</summary>
    TaxiwayLanding = 3,
}

/// <summary>One active runway in the ASDE-X safety-logic configuration: its identifier (e.g.
/// "28R"), footprint polygon (lat/lon ring, as supplied by CRC), closed state, the local
/// magnetic variation (east-positive declination) so the magnetic runway-id heading can be
/// compared against the aircraft's true heading, and the pavement elevation (ft MSL) that an
/// arrival's height over the runway is measured from.</summary>
public sealed record AsdexRunwaySurface(string Id, IReadOnlyList<LatLon> Area, bool IsClosed, double MagneticVariationDeg, double ElevationFt);

/// <summary>A taxiway centerline segment from the airport ground graph, used to approximate
/// taxiway-landing detection (the ground layout carries centerlines, not area polygons).</summary>
public sealed record AsdexTaxiwaySegment(string Name, LatLon Start, LatLon End);

/// <summary>
/// A single emitted Safety Logic alert. <paramref name="GoAround"/> is true only on a closed-runway alert raised for an
/// arrival: its text reads "RWY CLOSED, GO AROUND" and its aural sequence ends with the go-around token.
/// </summary>
public sealed record AsdexSafetyAlert(
    string Id,
    AsdexAlertKind Kind,
    IReadOnlyList<string> RunwayIds,
    IReadOnlyList<string> Callsigns,
    IReadOnlyList<string> MessageLines,
    bool PlayAuralAlert,
    bool GoAround
);

/// <summary>What an aircraft on a runway is doing there, from its current phase; the order is the alert ranking.</summary>
public enum AsdexRunwayUserRole
{
    /// <summary>
    /// Approaching, landing or rolling out (final approach, landing, runway exit, touch-and-go, stop-and-go, low
    /// approach), or going around: a go-around stays a runway user until it is past the threshold.
    /// </summary>
    Arrival = 0,

    /// <summary>Lining up, lined up and waiting, on the takeoff roll (or rejecting it) or in the initial climb.</summary>
    Departure = 1,

    /// <summary>
    /// Any other aircraft: taxiing, crossing, back-taxiing, holding short on the runway after landing (an occupant), or
    /// with no phase.
    /// </summary>
    Other = 2,
}

/// <summary>
/// The one ranking of the aircraft in an ASDE-X alert. The detector uses it to order an alert's callsigns (and so its
/// second line) at detect time; the server reads that stored order, never re-ranking, to find the runway end for the
/// first line and aural name, so line 1's end and line 2's first aircraft belong to the same aircraft.
/// </summary>
public static class AsdexRunwayUsers
{
    /// <summary>The aircraft's role on the runway, from its current phase.</summary>
    public static AsdexRunwayUserRole Role(AircraftState aircraft) =>
        aircraft.Phases?.CurrentPhase switch
        {
            FinalApproachPhase
            or LandingPhase
            or RunwayExitPhase
            or TouchAndGoPhase
            or StopAndGoPhase
            or LowApproachPhase
            or GoAroundPhase
            or HelicopterApproachPhase
            or HelicopterLandingPhase => AsdexRunwayUserRole.Arrival,
            LineUpPhase or LinedUpAndWaitingPhase or TakeoffPhase or RejectedTakeoffPhase or InitialClimbPhase or HelicopterTakeoffPhase =>
                AsdexRunwayUserRole.Departure,
            _ => AsdexRunwayUserRole.Other,
        };

    /// <summary>
    /// Ranks aircraft for an alert: arrivals, then departures, then the rest; within a role the faster over the ground,
    /// then the ordinal-first callsign, so the order never depends on the aircraft list's.
    /// </summary>
    public static readonly Comparer<AircraftState> First = Comparer<AircraftState>.Create(
        (a, b) =>
        {
            int role = Role(a).CompareTo(Role(b));
            if (role != 0)
            {
                return role;
            }

            int speed = b.GroundSpeed.CompareTo(a.GroundSpeed);
            return (speed != 0) ? speed : StringComparer.Ordinal.Compare(a.Callsign, b.Callsign);
        }
    );
}

/// <summary>
/// Stateless per-tick classifier for ASDE-X Safety Logic alerts. Mirrors CRC's simplified
/// binary alerting (not the real ASDE-X time-to-collision look-ahead) per the controller doc. Closed-runway-only
/// alerting under the LIMITED runway configuration follows the CRC manual ("only generates alerts for arrivals or
/// departures on closed runways", <c>docs/crc/asdex.md</c>), while every alert kind on every runway under a named
/// configuration was measured on vNAS Test (YAAT-1194).
///
/// Load-bearing approximations (flagged for aviation review):
/// - An arrival is treated as "still airborne over/short-of the runway" up to
///   <see cref="ArrivalAglCeilingFt"/> AGL; there is no approach-corridor projection, so an
///   occupied/closed-runway alert fires once the arrival is over the runway footprint rather
///   than on short final.
/// - "Using" vs "crossing" a runway is distinguished by alignment of the aircraft's true heading
///   with the runway's axis — the magnetic heading parsed from the identifier plus the local
///   magnetic variation, in either direction (±<see cref="AlignmentToleranceDeg"/>), so a
///   back-taxiing aircraft is a runway user rather than a crosser.
/// - Taxiway-landing uses centerline proximity (the ground layout has no taxiway polygons) and
///   alerts on any taxiway, since "configured taxiway" data is not available.
/// </summary>
public static class AsdexSafetyLogicDetector
{
    /// <summary>Arrival is treated as airborne over the runway up to this AGL (7110.65 §3-6-4).</summary>
    public const double ArrivalAglCeilingFt = 300;

    /// <summary>Heading tolerance (deg, either direction along the axis) for treating an aircraft as aligned with a runway.</summary>
    private const double AlignmentToleranceDeg = 35;

    /// <summary>Half-width (nm) within which a landing aircraft counts as over a taxiway (~28 m).</summary>
    private const double TaxiwayLandingHalfWidthNm = 0.015;

    /// <summary>The third line of an occupied-runway alert, as vNAS writes it.</summary>
    public const string OccupiedRunwayText = "RWY OCCUPIED";

    /// <summary>The third line of a closed-runway alert for any aircraft but an arrival, as vNAS writes it.</summary>
    public const string ClosedRunwayText = "RWY CLOSED";

    /// <summary>The third line of a closed-runway alert for an arrival (by role), as vNAS writes it; its aural ends with GO AROUND.</summary>
    public const string ClosedRunwayGoAroundText = "RWY CLOSED, GO AROUND";

    /// <summary>The prefix vNAS writes before the runway on an alert's first line ("RWY 28L").</summary>
    public const string RunwayLinePrefix = "RWY ";

    /// <summary>The separator between the aircraft on an alert's second line ("UAL4065, DAL345").</summary>
    public const string CallsignSeparator = ", ";

    /// <summary>The alerts the runways and taxiways raise this tick from the aircraft not individually inhibited.</summary>
    /// <param name="runways">The facility's runway footprints.</param>
    /// <param name="taxiways">The taxiway centerline segments checked for taxiway landings.</param>
    /// <param name="aircraft">Every aircraft this tick.</param>
    /// <param name="fieldElevationFt">The facility's field elevation, the reference for arrival AGL.</param>
    /// <param name="closedRunwayAlertsOnly">
    /// True when the facility is on the LIMITED runway configuration ("limited", none, or an id that names none of its
    /// configurations): only closed-runway alerts are raised, and occupied-runway and taxiway-landing alerts are
    /// suppressed. False under any named configuration: every alert kind is raised on every runway, the
    /// configuration's arrival and departure runway lists playing no part.
    /// </param>
    public static IReadOnlyList<AsdexSafetyAlert> Detect(
        IReadOnlyList<AsdexRunwaySurface> runways,
        IReadOnlyList<AsdexTaxiwaySegment> taxiways,
        IReadOnlyList<AircraftState> aircraft,
        double fieldElevationFt,
        bool closedRunwayAlertsOnly
    )
    {
        // Individually-inhibited targets (square drawn around them in CRC) take part in no alert.
        var candidates = aircraft.Where(ac => !ac.Stars.AsdexAlertsInhibited).ToList();
        var alerts = new List<AsdexSafetyAlert>();

        foreach (AsdexRunwaySurface runway in runways)
        {
            alerts.AddRange(RunwayAlerts(runway, candidates, closedRunwayAlertsOnly));
        }

        if (!closedRunwayAlertsOnly)
        {
            AppendTaxiwayLandings(runways, taxiways, candidates, fieldElevationFt, alerts);
        }

        return alerts;
    }

    /// <summary>
    /// The alerts a runway raises this tick: on a closed runway, one closed-runway alert per aircraft using it; on an
    /// open one (unless only closed-runway alerts are raised), one occupied-runway alert per conflicting pair, a pair
    /// being an aircraft that claims the runway and any other aircraft on it, aligned or crossing.
    /// </summary>
    private static IEnumerable<AsdexSafetyAlert> RunwayAlerts(
        AsdexRunwaySurface runway,
        IReadOnlyList<AircraftState> candidates,
        bool closedRunwayAlertsOnly
    )
    {
        if (!runway.IsClosed && closedRunwayAlertsOnly)
        {
            return [];
        }

        var occupants = candidates.Where(ac => PointInPolygon(ac.Position, runway.Area)).ToList();

        // A "claimant" is an aircraft aligned with the runway that is actively using it: on
        // the surface at any speed (rolling, or lined up and waiting) or arriving at low AGL.
        // Ground speed does NOT gate this — a stationary lined-up departure still claims the
        // runway, which is exactly the LUAW-incursion case Safety Logic must catch. Ranked
        // by role (AsdexRunwayUsers.First), so a pair's first aircraft is the one using the runway.
        var claimants = occupants.Where(ac => ClaimsRunway(ac, runway)).OrderBy(ac => ac, AsdexRunwayUsers.First).ToList();

        if (runway.IsClosed)
        {
            return claimants.Select(ac => ClosedRunwayAlert(runway, ac));
        }

        return OccupiedRunwayAlerts(runway, claimants, occupants);
    }

    /// <summary>
    /// One occupied-runway alert per pair of a claimant and another aircraft on the runway, each pair once: a pair of two
    /// claimants is raised from the higher-ranked one, which comes first.
    /// </summary>
    private static List<AsdexSafetyAlert> OccupiedRunwayAlerts(
        AsdexRunwaySurface runway,
        IReadOnlyList<AircraftState> rankedClaimants,
        IReadOnlyList<AircraftState> occupants
    )
    {
        var alerts = new List<AsdexSafetyAlert>();
        for (int i = 0; i < rankedClaimants.Count; i++)
        {
            AircraftState user = rankedClaimants[i];
            // The user itself and every higher-ranked claimant (whose pairs were raised already) are skipped.
            IEnumerable<AircraftState> others = occupants
                .Where(ac => !rankedClaimants.Take(i + 1).Contains(ac))
                .OrderBy(ac => ac.Callsign, StringComparer.Ordinal);
            foreach (AircraftState other in others)
            {
                alerts.Add(BuildAlert(AsdexAlertKind.OccupiedRunway, runway, [user, other], OccupiedRunwayText, goAround: false));
            }
        }

        return alerts;
    }

    /// <summary>The closed-runway alert for one aircraft using a closed runway: an arrival (by role) is told to go around.</summary>
    private static AsdexSafetyAlert ClosedRunwayAlert(AsdexRunwaySurface runway, AircraftState user)
    {
        bool goAround = AsdexRunwayUsers.Role(user) == AsdexRunwayUserRole.Arrival;
        return BuildAlert(AsdexAlertKind.ClosedRunway, runway, [user], goAround ? ClosedRunwayGoAroundText : ClosedRunwayText, goAround);
    }

    private static void AppendTaxiwayLandings(
        IReadOnlyList<AsdexRunwaySurface> runways,
        IReadOnlyList<AsdexTaxiwaySegment> taxiways,
        IReadOnlyList<AircraftState> candidates,
        double fieldElevationFt,
        List<AsdexSafetyAlert> alerts
    )
    {
        foreach (AircraftState ac in candidates)
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

            AsdexTaxiwaySegment? taxiway = NearestTaxiwayWithin(ac.Position, taxiways, TaxiwayLandingHalfWidthNm);
            if (taxiway is not null)
            {
                alerts.Add(
                    new AsdexSafetyAlert(
                        Id: $"TWYLAND|{taxiway.Name}|{ac.Callsign}",
                        Kind: AsdexAlertKind.TaxiwayLanding,
                        RunwayIds: [],
                        Callsigns: [ac.Callsign],
                        MessageLines: [taxiway.Name, ac.Callsign, "TAXIWAY LANDING"],
                        PlayAuralAlert: true,
                        GoAround: false
                    )
                );
            }
        }
    }

    private static AsdexSafetyAlert BuildAlert(
        AsdexAlertKind kind,
        AsdexRunwaySurface runway,
        IReadOnlyList<AircraftState> involved,
        string description,
        bool goAround
    )
    {
        // The id names the aircraft in ordinal order, so a pair keeps one id however its ranking moves from tick to tick
        // (a speed crossover, a touchdown): the broadcaster diffs by id, and vNAS sends an alert once. The callsigns and
        // the second line keep the ranked order the alert was raised in; a standing alert is never rewritten.
        var callsigns = involved.Select(ac => ac.Callsign).ToList();
        string id = $"{kind}|{runway.Id}|{string.Join(",", callsigns.Order(StringComparer.Ordinal))}";
        return new AsdexSafetyAlert(
            Id: id,
            Kind: kind,
            RunwayIds: [runway.Id],
            Callsigns: callsigns,
            MessageLines: [RunwayLinePrefix + runway.Id, string.Join(CallsignSeparator, callsigns), description],
            PlayAuralAlert: true,
            GoAround: goAround
        );
    }

    /// <summary>True if the aircraft heading is within tolerance of the runway's axis in either
    /// direction — a back-taxi (P/CG BACK-TAXI) uses the runway just as a departure does. The
    /// runway-id heading is magnetic, so the local magnetic variation is applied before comparing
    /// against the aircraft's true heading. When the id has no parseable heading, every occupant
    /// is treated as aligned so a conflict is not silently dropped.</summary>
    private static bool IsAligned(AircraftState ac, AsdexRunwaySurface runway)
    {
        if (!TryRunwayHeading(runway.Id, out double magneticHeading))
        {
            return true;
        }

        double trueHeading = magneticHeading + runway.MagneticVariationDeg;
        return AxisDifference(ac.TrueHeading.Degrees, trueHeading) <= AlignmentToleranceDeg;
    }

    /// <summary>True if the aircraft is aligned with the runway and actively using it: on the
    /// surface at any speed (rolling or holding in position) or arriving airborne at/below the
    /// AGL ceiling over the runway's own elevation. A high overflight is excluded by the AGL ceiling.</summary>
    private static bool ClaimsRunway(AircraftState ac, AsdexRunwaySurface runway)
    {
        if (!IsAligned(ac, runway))
        {
            return false;
        }

        return ac.IsOnGround || (ac.Altitude - runway.ElevationFt) <= ArrivalAglCeilingFt;
    }

    private static bool TryRunwayHeading(string runwayId, out double headingDegrees)
    {
        headingDegrees = 0;
        int digits = 0;
        ReadOnlySpan<char> span = runwayId.AsSpan();
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

    /// <summary>Smallest angle (0–90°) between heading <paramref name="a"/> and the line through heading <paramref name="b"/>.</summary>
    private static double AxisDifference(double a, double b)
    {
        double diff = Math.Abs(a - b) % 180.0;
        return diff > 90.0 ? 180.0 - diff : diff;
    }

    private static AsdexTaxiwaySegment? NearestTaxiwayWithin(LatLon point, IReadOnlyList<AsdexTaxiwaySegment> taxiways, double maxDistanceNm)
    {
        AsdexTaxiwaySegment? best = null;
        double bestDistance = maxDistanceNm;
        foreach (AsdexTaxiwaySegment taxiway in taxiways)
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
