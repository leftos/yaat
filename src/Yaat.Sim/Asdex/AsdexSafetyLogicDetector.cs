using Yaat.Sim;

namespace Yaat.Sim.Asdex;

/// <summary>
/// The kind of ASDE-X Safety Logic alert, per the CRC ASDE-X manual ("Safety Logic"). The kinds modelled are closed
/// runway, occupied runway and taxiway landing. Snapshots store the kind by number, so each member keeps its value; 2 is
/// unused (<c>SnapshotSchemaMigrator</c> rewrites a stored 2 to <see cref="OccupiedRunway"/>).
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

/// <summary>
/// One active runway in the ASDE-X safety-logic configuration: the configured runway (its id, the navdata runway the id
/// names and the footprint ring CRC supplies), that runway's axis (built from <see cref="AsdexHoldBarRunway.Runway"/>, once
/// per facility), and whether the configuration marks it closed.
/// </summary>
public sealed record AsdexRunwaySurface
{
    /// <exception cref="ArgumentException"><paramref name="axis"/> is not laid along <paramref name="runway"/>'s navdata runway.</exception>
    public AsdexRunwaySurface(AsdexHoldBarRunway runway, AsdexRunwayAxis axis, bool isClosed)
    {
        if (!ReferenceEquals(axis.Runway, runway.Runway))
        {
            throw new ArgumentException(
                $"The axis for safety-logic runway {runway.Id} is laid along another navdata runway; build it from that runway's own RunwayInfo",
                nameof(axis)
            );
        }

        Runway = runway;
        Axis = axis;
        IsClosed = isClosed;
    }

    /// <summary>The configured runway: its id, navdata runway and footprint.</summary>
    public AsdexHoldBarRunway Runway { get; }

    /// <summary>The axis of <see cref="AsdexHoldBarRunway.Runway"/>.</summary>
    public AsdexRunwayAxis Axis { get; }

    /// <summary>Whether the configuration marks the runway closed.</summary>
    public bool IsClosed { get; }

    /// <summary>The configuration's runway id, e.g. "28R-10L".</summary>
    public string Id => Runway.Id;
}

/// <summary>
/// A facility's safety-logic runways as the detector reads them: the configured runways navdata resolves, in configuration
/// order, and the footprint of every configured runway, resolved or not (empty for one whose configuration carries none). A
/// runway navdata lacks has no axis and so no runway user, but its footprint is still runway, never taxiway.
/// </summary>
public sealed record AsdexSafetyLogicRunways(IReadOnlyList<AsdexRunwaySurface> Surfaces, IReadOnlyList<IReadOnlyList<LatLon>> Footprints);

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

/// <summary>
/// Stateless per-tick classifier for ASDE-X Safety Logic alerts, raised on the triggers measured on vNAS Test (yaat-server
/// <c>docs/plans/asdex-probe-sfo</c>). Every alert kind reads one definition of a runway user, the one the hold bars read
/// (<see cref="AsdexRunwayUsers"/>): an arrival inside about one nautical mile of the threshold, or an aircraft over the
/// footprint at 50 kt or more below 200 ft. So a stopped or lined-up aircraft raises nothing, and an alert clears the tick
/// its user slows below 50 kt or climbs out. A closed runway alerts for each of its users, an arrival from one mile out; an
/// occupied runway alerts once its user is over the footprint, which for an airborne arrival is the threshold crossing,
/// against every other aircraft occupying the footprint (on the ground, or airborne below 200 ft above the runway).
/// Closed-runway-only alerting under the LIMITED runway configuration follows the CRC manual ("only generates alerts for
/// arrivals or departures on closed runways", <c>docs/crc/asdex.md</c>), while every alert kind on every runway under a
/// named configuration was measured on vNAS Test (YAAT-1194).
///
/// Taxiway-landing uses centerline proximity (the ground layout has no taxiway polygons) and alerts on any taxiway, since
/// "configured taxiway" data is not available.
/// </summary>
public static class AsdexSafetyLogicDetector
{
    /// <summary>A taxiway landing is an airborne aircraft up to this height above the field (7110.65 §3-6-4).</summary>
    public const double ArrivalAglCeilingFt = 300;

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
    /// <param name="runways">
    /// The facility's safety-logic runways. Where the configuration names one navdata runway in two entries, the entries are
    /// matched to each other by <see cref="RunwayInfo"/> reference, so their surfaces come from one resolve (one
    /// <see cref="RunwayInfo"/> instance per navdata runway), as the engine's runway cache builds them.
    /// </param>
    /// <param name="taxiways">The taxiway centerline segments checked for taxiway landings.</param>
    /// <param name="aircraft">Every aircraft this tick.</param>
    /// <param name="fieldElevationFt">The facility's field elevation, the reference for an arrival's height.</param>
    /// <param name="closedRunwayAlertsOnly">
    /// True when the facility is on the LIMITED runway configuration ("limited", none, or an id that names none of its
    /// configurations): only closed-runway alerts are raised, and occupied-runway and taxiway-landing alerts are
    /// suppressed. False under any named configuration: every alert kind is raised on every runway, the
    /// configuration's arrival and departure runway lists playing no part.
    /// </param>
    public static IReadOnlyList<AsdexSafetyAlert> Detect(
        AsdexSafetyLogicRunways runways,
        IReadOnlyList<AsdexTaxiwaySegment> taxiways,
        IReadOnlyList<AircraftState> aircraft,
        double fieldElevationFt,
        bool closedRunwayAlertsOnly
    )
    {
        // Individually-inhibited targets (square drawn around them in CRC) take part in no alert.
        var candidates = aircraft.Where(ac => !ac.Stars.AsdexAlertsInhibited).ToList();
        IReadOnlyList<AsdexRunwaySurface> surfaces = runways.Surfaces;
        List<AsdexHoldBarRunway> configured = [.. surfaces.Select(runway => runway.Runway)];
        List<AsdexRunwayAxis> axes = [.. surfaces.Select(runway => runway.Axis)];
        var uses = candidates.Select(ac => AsdexRunwayUsers.UsesOf(ac, configured, axes, fieldElevationFt)).ToList();
        var alerts = new List<AsdexSafetyAlert>();
        for (int i = 0; i < surfaces.Count; i++)
        {
            if (!surfaces[i].IsClosed && closedRunwayAlertsOnly)
            {
                continue;
            }

            // Each aircraft's role on this runway: an arrival box on another runway does not make it an arrival here.
            List<AircraftState> users = [];
            var roles = new Dictionary<AircraftState, AsdexRunwayUserRole>(ReferenceEqualityComparer.Instance);
            for (int k = 0; k < candidates.Count; k++)
            {
                roles[candidates[k]] = AsdexRunwayUsers.Role(candidates[k], AsdexRunwayUsers.ArrivesOn(uses[k], configured, i));
                if (AsdexRunwayUsers.IsUserOf(candidates[k], uses[k], configured, i))
                {
                    users.Add(candidates[k]);
                }
            }

            alerts.AddRange(RunwayAlerts(surfaces[i], users, candidates, roles));
        }

        if (!closedRunwayAlertsOnly)
        {
            AppendTaxiwayLandings(runways.Footprints, taxiways, candidates, fieldElevationFt, alerts);
        }

        return alerts;
    }

    /// <summary>
    /// The alerts a runway raises this tick from its <paramref name="users"/>: on a closed runway, one closed-runway alert
    /// per user; on an open one, one occupied-runway alert per conflicting pair, a pair being a user over the footprint and
    /// any other aircraft on it, using it or occupying it.
    /// </summary>
    private static IEnumerable<AsdexSafetyAlert> RunwayAlerts(
        AsdexRunwaySurface runway,
        List<AircraftState> users,
        IReadOnlyList<AircraftState> candidates,
        IReadOnlyDictionary<AircraftState, AsdexRunwayUserRole> roles
    )
    {
        Comparer<AircraftState> first = AsdexRunwayUsers.First(roles);
        if (runway.IsClosed)
        {
            return users.OrderBy(ac => ac, first).Select(ac => ClosedRunwayAlert(runway, ac, roles[ac]));
        }

        // An occupied runway needs its user over the footprint: an airborne arrival raises it at the threshold crossing.
        // Ranked by role (AsdexRunwayUsers.First), so a pair's first aircraft is the one using the runway.
        var claimants = users.Where(ac => AsdexRunwayUsers.IsOverFootprint(ac, runway.Runway)).OrderBy(ac => ac, first).ToList();
        if (claimants.Count == 0)
        {
            return [];
        }

        var onRunway = candidates.Where(ac => claimants.Contains(ac) || AsdexRunwayUsers.Occupies(ac, runway.Runway)).ToList();
        return OccupiedRunwayAlerts(runway, claimants, onRunway);
    }

    /// <summary>
    /// One occupied-runway alert per pair of a claimant and another aircraft on the runway, each pair once: a pair of two
    /// claimants is raised from the higher-ranked one, which comes first.
    /// </summary>
    private static List<AsdexSafetyAlert> OccupiedRunwayAlerts(
        AsdexRunwaySurface runway,
        IReadOnlyList<AircraftState> rankedClaimants,
        IReadOnlyList<AircraftState> onRunway
    )
    {
        var alerts = new List<AsdexSafetyAlert>();
        for (int i = 0; i < rankedClaimants.Count; i++)
        {
            AircraftState user = rankedClaimants[i];
            // The user itself and every higher-ranked claimant (whose pairs were raised already) are skipped.
            IEnumerable<AircraftState> others = onRunway
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
    private static AsdexSafetyAlert ClosedRunwayAlert(AsdexRunwaySurface runway, AircraftState user, AsdexRunwayUserRole role)
    {
        bool goAround = role == AsdexRunwayUserRole.Arrival;
        return BuildAlert(AsdexAlertKind.ClosedRunway, runway, [user], goAround ? ClosedRunwayGoAroundText : ClosedRunwayText, goAround);
    }

    private static void AppendTaxiwayLandings(
        IReadOnlyList<IReadOnlyList<LatLon>> runwayFootprints,
        IReadOnlyList<AsdexTaxiwaySegment> taxiways,
        IReadOnlyList<AircraftState> candidates,
        double fieldElevationFt,
        List<AsdexSafetyAlert> alerts
    )
    {
        foreach (AircraftState ac in candidates)
        {
            // A taxiway landing is an arrival at low AGL whose position is over a taxiway and
            // not over any configured runway's footprint, whether navdata resolves that runway or not.
            double agl = ac.Altitude - fieldElevationFt;
            if (ac.IsOnGround || agl > ArrivalAglCeilingFt || agl < -50)
            {
                continue;
            }

            if (runwayFootprints.Any(footprint => (footprint.Count >= 3) && GeoMath.PointInRing(ac.Position, footprint)))
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
