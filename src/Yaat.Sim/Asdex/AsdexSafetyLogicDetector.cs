using System.Collections.Immutable;
using Yaat.Sim;
using Yaat.Sim.Data.Airport;

namespace Yaat.Sim.Asdex;

/// <summary>
/// The kind of ASDE-X Safety Logic alert, per the CRC ASDE-X manual ("Safety Logic"). The kinds modelled are closed
/// runway, occupied runway, taxiway landing, the three opposite-direction kinds (taxi, departure, lander), which replace
/// an occupied-runway alert for a pair heading opposite ways along the runway, and converging runways (a pair on two
/// runways whose centrelines cross ahead of both). Snapshots store the kind by number, so each
/// member keeps its value; 2 is unused (<c>SnapshotSchemaMigrator</c> rewrites a stored 2 to <see cref="OccupiedRunway"/>).
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

    /// <summary>
    /// A runway user and an occupant moving toward it along the runway at
    /// <see cref="AsdexRunwayUsers.OppDirTaxiMinGroundSpeedKts"/> or more, the user first.
    /// </summary>
    OppDirTaxi = 4,

    /// <summary>A departure and an arrival using the runway head on, the departure first.</summary>
    OppDirDeparture = 5,

    /// <summary>
    /// Two arrivals using the runway head on, the one further into its landing first (<see cref="AsdexRunwayUsers.LandedFurther"/>).
    /// </summary>
    OppDirLander = 6,

    /// <summary>
    /// Two runway users on converging runways (intersecting, or whose centrelines meet within 1 NM beyond a departure end),
    /// each with the crossing still ahead (<see cref="AsdexRunwayUsers.ConvergesAhead"/>): an arrival and a departure, or two
    /// arrivals (7110.65 §3-6-4.c.3); the departure first.
    /// </summary>
    Converging = 7,
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
/// <paramref name="RunwayEnds"/> is set on a converging alert only: the runway end each aircraft in
/// <paramref name="Callsigns"/> is using, in the same order ("1R", "28L"); null on every other kind.
/// </summary>
public sealed record AsdexSafetyAlert(
    string Id,
    AsdexAlertKind Kind,
    IReadOnlyList<string> RunwayIds,
    IReadOnlyList<string> Callsigns,
    IReadOnlyList<string> MessageLines,
    bool PlayAuralAlert,
    bool GoAround,
    IReadOnlyList<string>? RunwayEnds
);

/// <summary>
/// One facility's detection for a tick: the alerts its runways and taxiways raise, and the opposite-direction pair memory
/// to pass to the next tick's <see cref="AsdexSafetyLogicDetector.Detect"/> (ordinal-sorted pair keys, each a runway id and
/// the pair's ordinal callsigns, "28L|DAL2988,DAL530").
/// </summary>
public sealed record AsdexDetection(IReadOnlyList<AsdexSafetyAlert> Alerts, ImmutableSortedSet<string> OppDirPairs)
{
    /// <summary>No pair remembered: what a facility with no opposite-direction history reads as.</summary>
    public static ImmutableSortedSet<string> NoOppDirPairs { get; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);

    /// <summary>Nothing raised and nothing remembered: a facility with no configuration or no runways.</summary>
    public static AsdexDetection None { get; } = new([], NoOppDirPairs);
}

/// <summary>
/// What <see cref="AsdexSafetyLogicDetector.Detect"/> reads besides the runways and aircraft: whether only closed-runway
/// alerts are raised (the LIMITED configuration), and the pair memory the previous tick's detection returned.
/// </summary>
public sealed record AsdexDetectionInputs(bool ClosedRunwayAlertsOnly, ImmutableSortedSet<string> OppDirPairs);

/// <summary>
/// Per-tick classifier for ASDE-X Safety Logic alerts, a pure function of the world and the previous tick's
/// opposite-direction pair memory, raised on the triggers measured on vNAS Test (yaat-server
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
/// A pair heading toward each other along the runway raises an opposite-direction alert in place of RWY OCCUPIED
/// (<see cref="PairKind"/>; two departures head on, or a user whose role is Other, fall back to RWY OCCUPIED, which was
/// not measured). The pair is then remembered: while both stay on the runway it raises no RWY OCCUPIED (vNAS raised
/// nothing after OPP DIR TAXI cleared at the turn-off), and it raises an opposite-direction alert again if it heads
/// opposite again. A remembered pair is forgotten the first tick either aircraft no longer occupies that runway's
/// footprint, or the runway is not evaluated (an inhibited aircraft, a closed or unconfigured runway, LIMITED).
///
/// Two runway users on different open runways raise CONVERGING, as measured on vNAS Test (probe README:305-313): an
/// arrival against a departure, or two arrivals (7110.65 §3-6-4.c.3), when the centrelines cross ahead of both
/// (<see cref="AsdexRunwayUsers.ConvergesAhead"/>). It gives way to the same-runway alerts once a lander occupies the
/// other aircraft's runway, is raised once per aircraft pair, and carries the runway end each aircraft uses.
///
/// Taxiway-landing uses centerline proximity (the ground layout has no taxiway polygons) and alerts on any taxiway unless the
/// airport's sidecar lists <c>asdexLandingTaxiways</c>, when it alerts only on the taxiways that list names.
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

    /// <summary>The third line of an opposite-direction taxi alert, as vNAS writes it.</summary>
    public const string OppDirTaxiText = "OPP DIR TAXI";

    /// <summary>The third line of an opposite-direction departure alert, as vNAS writes it.</summary>
    public const string OppDirDepartureText = "OPP DIR DEPARTURE";

    /// <summary>The third line of an opposite-direction lander alert, as vNAS writes it.</summary>
    public const string OppDirLanderText = "OPP DIR LANDER";

    /// <summary>The third line of a converging-runway alert, as vNAS writes it.</summary>
    public const string ConvergingText = "CONVERGING";

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
    /// <param name="inputs">
    /// The facility's per-tick switches: its pair memory from the previous tick's detection, and
    /// <see cref="AsdexDetectionInputs.ClosedRunwayAlertsOnly"/>, true when the facility is on the LIMITED runway
    /// configuration ("limited", none, or an id that names none of its
    /// configurations): only closed-runway alerts are raised, and occupied-runway, opposite-direction, converging and
    /// taxiway-landing alerts are suppressed. False under any named configuration: every alert kind is raised on every runway, the
    /// configuration's arrival and departure runway lists playing no part.
    /// </param>
    public static AsdexDetection Detect(
        AsdexSafetyLogicRunways runways,
        IReadOnlyList<AsdexTaxiwaySegment> taxiways,
        IReadOnlyList<AircraftState> aircraft,
        double fieldElevationFt,
        AsdexDetectionInputs inputs
    )
    {
        bool closedRunwayAlertsOnly = inputs.ClosedRunwayAlertsOnly;
        ImmutableSortedSet<string> oppDirPairs = inputs.OppDirPairs;
        var memory = new PairMemory(oppDirPairs, ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal));
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

            alerts.AddRange(RunwayAlerts(surfaces[i], users, candidates, roles, memory));
        }

        if (!closedRunwayAlertsOnly)
        {
            alerts.AddRange(ConvergingAlerts(surfaces, configured, axes, candidates, uses));
            AppendTaxiwayLandings(runways.Footprints, taxiways, candidates, fieldElevationFt, alerts);
        }

        return new AsdexDetection(alerts, memory.Next.ToImmutable());
    }

    /// <summary>The pair memory the previous tick returned, and the one this tick builds from the pairs it sees.</summary>
    private readonly record struct PairMemory(ImmutableSortedSet<string> Prior, ImmutableSortedSet<string>.Builder Next);

    /// <summary>
    /// The alerts a runway raises this tick from its <paramref name="users"/>: on a closed runway, one closed-runway alert
    /// per user; on an open one, one occupied-runway or opposite-direction alert per conflicting pair, a pair being a user
    /// over the footprint and any other aircraft on it, using it or occupying it.
    /// </summary>
    private static IEnumerable<AsdexSafetyAlert> RunwayAlerts(
        AsdexRunwaySurface runway,
        List<AircraftState> users,
        IReadOnlyList<AircraftState> candidates,
        IReadOnlyDictionary<AircraftState, AsdexRunwayUserRole> roles,
        PairMemory memory
    )
    {
        Comparer<AircraftState> first = AsdexRunwayUsers.First(roles);
        if (runway.IsClosed)
        {
            return users.OrderBy(ac => ac, first).Select(ac => ClosedRunwayAlert(runway, ac, roles[ac]));
        }

        RetainOccupyingPairs(runway, candidates, memory);

        // An occupied runway needs its user over the footprint: an airborne arrival raises it at the threshold crossing.
        // Ranked by role (AsdexRunwayUsers.First), so a pair's first aircraft is the one using the runway.
        var claimants = users.Where(ac => AsdexRunwayUsers.IsOverFootprint(ac, runway.Runway)).OrderBy(ac => ac, first).ToList();
        if (claimants.Count == 0)
        {
            return [];
        }

        var onRunway = candidates.Where(ac => claimants.Contains(ac) || AsdexRunwayUsers.Occupies(ac, runway.Runway)).ToList();
        return OccupiedRunwayAlerts(runway, claimants, onRunway, roles, memory);
    }

    /// <summary>
    /// One alert per pair of a claimant and another aircraft on the runway, each pair once: a pair of two claimants is raised
    /// from the higher-ranked one, which comes first. A pair heading toward each other along the runway raises an
    /// opposite-direction alert in place of RWY OCCUPIED (<see cref="PairKind"/>) and is remembered; a remembered pair that
    /// would read RWY OCCUPIED raises nothing and stays remembered. Every pair key this tick sees is the next memory's
    /// candidate, so a pair the detector no longer sees on this runway is forgotten.
    /// </summary>
    private static List<AsdexSafetyAlert> OccupiedRunwayAlerts(
        AsdexRunwaySurface runway,
        IReadOnlyList<AircraftState> rankedClaimants,
        IReadOnlyList<AircraftState> onRunway,
        IReadOnlyDictionary<AircraftState, AsdexRunwayUserRole> roles,
        PairMemory memory
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
                string pairKey = AlertKeyBody(runway, [user.Callsign, other.Callsign]);
                AsdexAlertKind kind = PairKind(runway.Axis, user, other, rankedClaimants.Contains(other), roles);
                if (kind != AsdexAlertKind.OccupiedRunway)
                {
                    memory.Next.Add(pairKey);
                    alerts.Add(OppDirAlert(runway, kind, user, other, roles));
                }
                else if (memory.Prior.Contains(pairKey))
                {
                    memory.Next.Add(pairKey);
                }
                else
                {
                    alerts.Add(BuildAlert(AsdexAlertKind.OccupiedRunway, runway, [user, other], OccupiedRunwayText, goAround: false));
                }
            }
        }

        return alerts;
    }

    /// <summary>
    /// The opposite-direction alert for one pair of a claimant (<paramref name="user"/>) and another aircraft on the runway:
    /// a departure names the departure first, a lander the one further into its landing
    /// (<see cref="AsdexRunwayUsers.LandedFurther"/>), and a taxi the claimant.
    /// </summary>
    private static AsdexSafetyAlert OppDirAlert(
        AsdexRunwaySurface runway,
        AsdexAlertKind kind,
        AircraftState user,
        AircraftState other,
        IReadOnlyDictionary<AircraftState, AsdexRunwayUserRole> roles
    ) =>
        kind switch
        {
            AsdexAlertKind.OppDirDeparture => BuildAlert(
                kind,
                runway,
                // First ranks an arrival before a departure, so the claimant is normally the arrival; the check guards a change to that ranking.
                (roles[user] == AsdexRunwayUserRole.Departure)
                    ? [user, other]
                    : [other, user],
                OppDirDepartureText,
                goAround: false
            ),
            AsdexAlertKind.OppDirLander => BuildAlert(
                kind,
                runway,
                AsdexRunwayUsers.LandedFurther(user, other, runway.Axis) ? [user, other] : [other, user],
                OppDirLanderText,
                goAround: false
            ),
            AsdexAlertKind.OppDirTaxi => BuildAlert(kind, runway, [user, other], OppDirTaxiText, goAround: false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an opposite-direction alert kind"),
        };

    /// <summary>
    /// The kind of a pair's alert: a pair heading toward each other along the runway
    /// (<see cref="AsdexRunwayUsers.HeadsOpposite"/>) is an opposite-direction departure when both use it, one as a departure
    /// and one as an arrival; an opposite-direction lander when both use it as arrivals; and an opposite-direction taxi when
    /// the other only occupies it, moving at <see cref="AsdexRunwayUsers.OppDirTaxiMinGroundSpeedKts"/> or more (all three
    /// measured on vNAS Test). Two departures head on, or a user whose role is Other, fall back to RWY OCCUPIED (not
    /// measured), as does any other pair, a stopped occupant included.
    /// </summary>
    private static AsdexAlertKind PairKind(
        AsdexRunwayAxis axis,
        AircraftState user,
        AircraftState other,
        bool otherIsClaimant,
        IReadOnlyDictionary<AircraftState, AsdexRunwayUserRole> roles
    )
    {
        if (!AsdexRunwayUsers.HeadsOpposite(user, other, axis))
        {
            return AsdexAlertKind.OccupiedRunway;
        }

        if (!otherIsClaimant)
        {
            return (other.GroundSpeed >= AsdexRunwayUsers.OppDirTaxiMinGroundSpeedKts) ? AsdexAlertKind.OppDirTaxi : AsdexAlertKind.OccupiedRunway;
        }

        return (roles[user], roles[other]) switch
        {
            (AsdexRunwayUserRole.Arrival, AsdexRunwayUserRole.Arrival) => AsdexAlertKind.OppDirLander,
            // (Departure, Arrival) guards a change to First, which ranks the arrival first and so makes it the claimant.
            (AsdexRunwayUserRole.Arrival, AsdexRunwayUserRole.Departure) or (AsdexRunwayUserRole.Departure, AsdexRunwayUserRole.Arrival) =>
                AsdexAlertKind.OppDirDeparture,
            _ => AsdexAlertKind.OccupiedRunway,
        };
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
                        GoAround: false,
                        RunwayEnds: null
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
        // the second line keep the stored order (the First ranking, except OPP DIR DEPARTURE, departure first, and OPP DIR
        // LANDER, the rolled-out lander first); a standing alert is never rewritten.
        var callsigns = involved.Select(ac => ac.Callsign).ToList();
        string id = $"{kind}|{AlertKeyBody(runway, callsigns)}";
        return new AsdexSafetyAlert(
            Id: id,
            Kind: kind,
            RunwayIds: [runway.Id],
            Callsigns: callsigns,
            MessageLines: [RunwayLinePrefix + runway.Id, string.Join(CallsignSeparator, callsigns), description],
            PlayAuralAlert: true,
            GoAround: goAround,
            RunwayEnds: null
        );
    }

    /// <summary>A runway user as the converging pass reads it: its use of one runway and its role there.</summary>
    private readonly record struct ConvergingUser(AircraftState Aircraft, AsdexRunwayUse Use, AsdexRunwayUserRole Role);

    /// <summary>
    /// One converging alert per pair of aircraft using two different open runways that converge for them, one an arrival and
    /// the other a departure or both arrivals (<see cref="AsdexRunwayUsers.ConvergesAhead"/>: the centrelines cross ahead of
    /// each, past its entry threshold, within 1 NM beyond the departure end of either runway). A pair in which a lander
    /// occupies the other aircraft's runway is left to that runway's own alerts. A pair that converges on more than one
    /// runway combination (a runway configured twice) raises one alert, from the first combination in configuration order.
    /// </summary>
    private static List<AsdexSafetyAlert> ConvergingAlerts(
        IReadOnlyList<AsdexRunwaySurface> surfaces,
        IReadOnlyList<AsdexHoldBarRunway> configured,
        IReadOnlyList<AsdexRunwayAxis> axes,
        IReadOnlyList<AircraftState> candidates,
        IReadOnlyList<AsdexRunwayUses> uses
    )
    {
        var alerts = new List<AsdexSafetyAlert>();
        var raisedPairs = new HashSet<string>(StringComparer.Ordinal);
        // Each open runway's arrivals and departures, built once; null for a closed runway, which raises no converging alert.
        List<ConvergingUser>?[] users =
        [
            .. surfaces.Select((surface, i) => surface.IsClosed ? null : ConvergingUsers(configured, candidates, uses, i)),
        ];
        for (int i = 0; i < surfaces.Count; i++)
        {
            for (int j = i + 1; j < surfaces.Count; j++)
            {
                if ((users[i] is not { } usersI) || (users[j] is not { } usersJ) || !RunwaysCross(configured, axes, i, j))
                {
                    continue;
                }

                foreach (AsdexSafetyAlert alert in RunwayPairConvergingAlerts(surfaces[i], usersI, surfaces[j], usersJ, axes))
                {
                    if (raisedPairs.Add(string.Join(",", alert.Callsigns.Order(StringComparer.Ordinal))))
                    {
                        alerts.Add(alert);
                    }
                }
            }
        }

        return alerts;
    }

    /// <summary>True when two configured entries are different navdata runways whose centrelines cross.</summary>
    private static bool RunwaysCross(IReadOnlyList<AsdexHoldBarRunway> configured, IReadOnlyList<AsdexRunwayAxis> axes, int i, int j) =>
        !ReferenceEquals(configured[i].Runway, configured[j].Runway) && axes[i].TryCrossing(axes[j], out _);

    /// <summary>True when <paramref name="user"/> is an arrival occupying <paramref name="runway"/> (on it, or below 200 ft over it).</summary>
    private static bool LanderOccupies(ConvergingUser user, AsdexRunwaySurface runway) =>
        (user.Role == AsdexRunwayUserRole.Arrival) && AsdexRunwayUsers.Occupies(user.Aircraft, runway.Runway);

    /// <summary>The arrivals and departures using <c>configured[runwayIndex]</c>, each with its use of it and its role there.</summary>
    private static List<ConvergingUser> ConvergingUsers(
        IReadOnlyList<AsdexHoldBarRunway> configured,
        IReadOnlyList<AircraftState> candidates,
        IReadOnlyList<AsdexRunwayUses> uses,
        int runwayIndex
    )
    {
        var users = new List<ConvergingUser>();
        for (int k = 0; k < candidates.Count; k++)
        {
            if (!AsdexRunwayUsers.IsUserOf(candidates[k], uses[k], configured, runwayIndex))
            {
                continue;
            }

            AsdexRunwayUserRole role = AsdexRunwayUsers.Role(candidates[k], AsdexRunwayUsers.ArrivesOn(uses[k], configured, runwayIndex));
            if ((role != AsdexRunwayUserRole.Other) && (AsdexRunwayUsers.UseOn(uses[k], configured, runwayIndex) is { } use))
            {
                users.Add(new ConvergingUser(candidates[k], use, role));
            }
        }

        return users;
    }

    /// <summary>The converging alerts between the users of runway <paramref name="runwayA"/> and those of <paramref name="runwayB"/>.</summary>
    private static IEnumerable<AsdexSafetyAlert> RunwayPairConvergingAlerts(
        AsdexRunwaySurface runwayA,
        List<ConvergingUser> usersA,
        AsdexRunwaySurface runwayB,
        List<ConvergingUser> usersB,
        IReadOnlyList<AsdexRunwayAxis> axes
    )
    {
        foreach (ConvergingUser a in usersA)
        {
            foreach (ConvergingUser b in usersB)
            {
                // An arrival against a departure or another arrival; two departures raise nothing.
                bool hasArrival = (a.Role == AsdexRunwayUserRole.Arrival) || (b.Role == AsdexRunwayUserRole.Arrival);
                // A lander occupying the other aircraft's runway is that runway's own case (RWY OCCUPIED and the like).
                bool handedOver = LanderOccupies(a, runwayB) || LanderOccupies(b, runwayA);
                if (hasArrival && !handedOver && !ReferenceEquals(a.Aircraft, b.Aircraft) && AsdexRunwayUsers.ConvergesAhead(a.Use, b.Use, axes))
                {
                    yield return (b.Role == AsdexRunwayUserRole.Departure)
                        ? ConvergingAlert(runwayB, b, runwayA, a)
                        : ConvergingAlert(runwayA, a, runwayB, b);
                }
            }
        }
    }

    /// <summary>
    /// The converging alert naming <paramref name="first"/> (the departure, or the arrival on the runway configured first)
    /// then <paramref name="second"/>, as vNAS sends it: line 1 "RWY 1R, RWY 28L", one runway end per aircraft. The id names
    /// both runway ids and both callsigns in ordinal order, so it holds however the pair is ranked.
    /// </summary>
    private static AsdexSafetyAlert ConvergingAlert(
        AsdexRunwaySurface firstRunway,
        ConvergingUser first,
        AsdexRunwaySurface secondRunway,
        ConvergingUser second
    )
    {
        List<string> callsigns = [first.Aircraft.Callsign, second.Aircraft.Callsign];
        List<string> ends = [EndInUse(firstRunway, first.Use.Direction), EndInUse(secondRunway, second.Use.Direction)];
        List<string> runwayIds = [firstRunway.Id, secondRunway.Id];
        string orderedRunwayIds = string.Join(",", runwayIds.Order(StringComparer.Ordinal));
        string orderedCallsigns = string.Join(",", callsigns.Order(StringComparer.Ordinal));
        string id = $"{AsdexAlertKind.Converging}|{orderedRunwayIds}|{orderedCallsigns}";
        return new AsdexSafetyAlert(
            Id: id,
            Kind: AsdexAlertKind.Converging,
            RunwayIds: runwayIds,
            Callsigns: callsigns,
            MessageLines:
            [
                string.Join(CallsignSeparator, ends.Select(end => RunwayLinePrefix + end)),
                string.Join(CallsignSeparator, callsigns),
                ConvergingText,
            ],
            PlayAuralAlert: true,
            GoAround: false,
            RunwayEnds: ends
        );
    }

    /// <summary>
    /// The end of the runway an aircraft moving in <paramref name="direction"/> along it is using: end 1 (the end the axis
    /// starts at) when it moves toward end 2, else end 2, spelled as the configuration's runway id spells it ("1R" in
    /// "1R-19L", where navdata writes "01R"), or as navdata does when the id does not name it.
    /// </summary>
    private static string EndInUse(AsdexRunwaySurface runway, int direction)
    {
        RunwayIdentifier navdataId = runway.Runway.Runway.Id;
        string navdataEnd = direction > 0 ? navdataId.End1 : navdataId.End2;
        return runway
                .Id.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(end => RunwayIdentifier.NormalizeDesignator(end).Equals(navdataEnd, StringComparison.OrdinalIgnoreCase))
            ?? navdataEnd;
    }

    /// <summary>
    /// Keeps every remembered pair on this open runway whose two aircraft both still occupy its footprint, user or not (two
    /// aircraft below 50 kt included): a pair is forgotten only once one of the two leaves the runway.
    /// </summary>
    private static void RetainOccupyingPairs(AsdexRunwaySurface runway, IReadOnlyList<AircraftState> candidates, PairMemory memory)
    {
        var occupants = candidates.Where(ac => AsdexRunwayUsers.Occupies(ac, runway.Runway)).ToList();
        for (int i = 0; i < occupants.Count; i++)
        {
            for (int j = i + 1; j < occupants.Count; j++)
            {
                string pairKey = AlertKeyBody(runway, [occupants[i].Callsign, occupants[j].Callsign]);
                if (memory.Prior.Contains(pairKey))
                {
                    memory.Next.Add(pairKey);
                }
            }
        }
    }

    /// <summary>
    /// An alert id after its kind, and a remembered pair's key: the runway id and the aircraft's callsigns in ordinal order
    /// ("28L|DAL2988,DAL530").
    /// </summary>
    private static string AlertKeyBody(AsdexRunwaySurface runway, IEnumerable<string> callsigns) =>
        $"{runway.Id}|{string.Join(",", callsigns.Order(StringComparer.Ordinal))}";

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
