using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Phases;

/// <summary>
/// Entry leg for pattern construction.
/// </summary>
public enum PatternEntryLeg
{
    Upwind,
    Crosswind,
    Downwind,
    Base,
    Final,
}

/// <summary>
/// The inputs of a traffic pattern circuit built from a specific entry leg: the runway and the aircraft it is for,
/// the wind its planning speeds carry, and the controller's and the airport's instructions.
/// </summary>
public sealed record PatternCircuitRequest
{
    /// <summary>The pattern runway.</summary>
    public required RunwayInfo Runway { get; init; }

    /// <summary>The aircraft's category, which sizes the pattern and its climb and descent profile.</summary>
    public required AircraftCategory Category { get; init; }

    /// <summary>ICAO type designator of the aircraft the pattern is built for.</summary>
    public required string AircraftType { get; init; }

    /// <summary>Wind speed (kt) at the aircraft, used to inflate the pattern's planning speeds; 0 when no weather is loaded.</summary>
    public required double WindSpeedKt { get; init; }

    /// <summary>Left or right turns.</summary>
    public required PatternDirection Direction { get; init; }

    /// <summary>The leg the entry starts from.</summary>
    public required PatternEntryLeg EntryLeg { get; init; }

    /// <summary>True ends the approach on a <see cref="TouchAndGoPhase"/> instead of a <see cref="LandingPhase"/>.</summary>
    public required bool TouchAndGo { get; init; }

    /// <summary>How far along final (nm) the entry starts; null for no adjustment.</summary>
    public required double? FinalDistanceNm { get; init; }

    /// <summary>The PSIZE or authored pattern width (nm); null for the category default.</summary>
    public required double? PatternSizeNm { get; init; }

    /// <summary>A controller-assigned pattern altitude (feet MSL), which outranks the airport's and the category's.</summary>
    public required double? AltitudeOverrideFt { get; init; }

    /// <summary>The airport's runways, which the circuit is deconflicted off and compressed clear of.</summary>
    public required IReadOnlyList<RunwayInfo>? AirportRunways { get; init; }

    /// <summary>
    /// The authored ground runway of the pattern runway, for the published threshold displacement; null when no
    /// airport map is loaded, in which case the pattern is built to the pavement threshold.
    /// </summary>
    public required GroundRunway? AuthoredRunway { get; init; }

    /// <summary>The room's active runways; an airport that names none counts every runway.</summary>
    public required ActiveRunways ActiveRunways { get; init; }

    /// <summary>The PSIZE and controller speed that bound compression.</summary>
    public required PatternCommanded Commanded { get; init; }
}

/// <summary>
/// The inputs of the circuit an aircraft flies when it climbs out on one runway and flies another runway's pattern —
/// a cross-runway closed-traffic departure or an in-pattern runway switch on the upwind leg.
/// </summary>
public sealed record RunwayTransitionCircuitRequest
{
    /// <summary>The runway the aircraft is actually flying, whose upwind climbs out into the pattern circuit.</summary>
    public required RunwayInfo FlownRunway { get; init; }

    /// <summary>The pattern (landing) runway, whose downwind, base and final the circuit flies.</summary>
    public required RunwayInfo PatternRunway { get; init; }

    /// <summary>The aircraft's category, which sizes the pattern and its climb and descent profile.</summary>
    public required AircraftCategory Category { get; init; }

    /// <summary>ICAO type designator of the aircraft the pattern is built for.</summary>
    public required string AircraftType { get; init; }

    /// <summary>Wind speed (kt) at the aircraft, used to inflate the pattern's planning speeds; 0 when no weather is loaded.</summary>
    public required double WindSpeedKt { get; init; }

    /// <summary>Left or right turns.</summary>
    public required PatternDirection Direction { get; init; }

    /// <summary>True ends the approach on a <see cref="TouchAndGoPhase"/> instead of a <see cref="LandingPhase"/>.</summary>
    public required bool TouchAndGo { get; init; }

    /// <summary>The PSIZE or authored pattern width (nm); null for the category default.</summary>
    public required double? PatternSizeNm { get; init; }

    /// <summary>A controller-assigned pattern altitude (feet MSL), which outranks the airport's and the category's.</summary>
    public required double? AltitudeOverrideFt { get; init; }

    /// <summary>The airport's runways, which the circuit is deconflicted off and compressed clear of.</summary>
    public required IReadOnlyList<RunwayInfo>? AirportRunways { get; init; }

    /// <summary>
    /// The authored ground runway of the flown runway, for the published threshold displacement; null when no
    /// airport map is loaded.
    /// </summary>
    public required GroundRunway? FlownAuthoredRunway { get; init; }

    /// <summary>
    /// The authored ground runway of the pattern runway, for the published threshold displacement; null when no
    /// airport map is loaded.
    /// </summary>
    public required GroundRunway? PatternAuthoredRunway { get; init; }

    /// <summary>The room's active runways; an airport that names none counts every runway.</summary>
    public required ActiveRunways ActiveRunways { get; init; }

    /// <summary>The PSIZE and controller speed that bound compression.</summary>
    public required PatternCommanded Commanded { get; init; }
}

/// <summary>
/// Builds phase sequences for traffic pattern circuits.
/// </summary>
public static class PatternBuilder
{
    /// <summary>
    /// Build a pattern circuit starting from a specific entry leg.
    /// When <see cref="PatternCircuitRequest.TouchAndGo"/> is true, the approach ends
    /// with a TouchAndGoPhase instead of a LandingPhase.
    /// </summary>
    public static List<Phase> BuildCircuit(PatternCircuitRequest request)
    {
        PatternWaypoints waypoints = PatternGeometry.Compute(
            new PatternGeometryRequest(request.Runway, request.Category, request.AircraftType, request.WindSpeedKt, request.Direction)
            {
                SizeOverrideNm = request.PatternSizeNm,
                AltitudeOverrideFt = request.AltitudeOverrideFt,
                AirportRunways = request.AirportRunways,
                AuthoredRunway = request.AuthoredRunway,
                ActiveRunways = request.ActiveRunways,
                Commanded = request.Commanded,
            }
        );
        var phases = new List<Phase>();

        switch (request.EntryLeg)
        {
            case PatternEntryLeg.Upwind:
                phases.Add(new UpwindPhase { Waypoints = waypoints });
                phases.Add(new CrosswindPhase { Waypoints = waypoints });
                phases.Add(new DownwindPhase { Waypoints = waypoints });
                phases.Add(new BasePhase { Waypoints = waypoints });
                break;

            case PatternEntryLeg.Crosswind:
                phases.Add(new CrosswindPhase { Waypoints = waypoints });
                phases.Add(new DownwindPhase { Waypoints = waypoints });
                phases.Add(new BasePhase { Waypoints = waypoints });
                break;

            case PatternEntryLeg.Downwind:
                phases.Add(new DownwindPhase { Waypoints = waypoints });
                phases.Add(new BasePhase { Waypoints = waypoints });
                break;

            case PatternEntryLeg.Base:
                phases.Add(new BasePhase { Waypoints = waypoints, FinalDistanceNm = request.FinalDistanceNm });
                break;

            case PatternEntryLeg.Final:
                break;
        }

        phases.Add(new FinalApproachPhase());
        Phase landingPhase = request.Category == AircraftCategory.Helicopter ? new HelicopterLandingPhase() : new LandingPhase();
        phases.Add(request.TouchAndGo ? new TouchAndGoPhase() : landingPhase);

        return phases;
    }

    /// <summary>
    /// Build a VFR pattern-exit departure (CTO MRC/MRD/MLC/MLD): the legs up to and including the
    /// exit leg, with no base/final/landing tail. A crosswind exit flies upwind then turns crosswind;
    /// a downwind exit flies upwind, crosswind, then downwind. The legs keep a continuous takeoff-rate
    /// climb toward <paramref name="assignedAltitude"/> ?? <paramref name="cruiseAltitude"/> (no level-off
    /// at pattern altitude), and a terminal <see cref="PatternExitPhase"/> rolls the aircraft out on the
    /// exit-leg heading and departs the area.
    /// </summary>
    public static List<Phase> BuildPatternExitCircuit(
        RunwayInfo runway,
        AircraftCategory category,
        string aircraftType,
        double windSpeedKt,
        PatternDirection direction,
        PatternEntryLeg exitLeg,
        int? assignedAltitude,
        int cruiseAltitude,
        double? patternSizeNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways,
        GroundRunway? authoredRunway,
        ActiveRunways activeRunways,
        PatternCommanded commanded
    )
    {
        PatternWaypoints waypoints = PatternGeometry.Compute(
            new PatternGeometryRequest(runway, category, aircraftType, windSpeedKt, direction)
            {
                SizeOverrideNm = patternSizeNm,
                AltitudeOverrideFt = altitudeOverrideFt,
                AirportRunways = airportRunways,
                AuthoredRunway = authoredRunway,
                ActiveRunways = activeRunways,
                Commanded = commanded,
            }
        );

        // Altitude resolution (COMMANDS.md, CTO Departure Modifiers): an assigned altitude wins; else
        // the filed cruise altitude; else pattern altitude. A VFR departure without a filed cruise still
        // climbs toward pattern altitude — it must never target 0 ft MSL and fly the climb rate into
        // the ground. (CruiseAltitude defaults to 0 for a VFR aircraft with no filed cruise.)
        int climbTo = assignedAltitude ?? (cruiseAltitude > 0 ? cruiseAltitude : (int)Math.Round(waypoints.PatternAltitude));

        var phases = new List<Phase>
        {
            new UpwindPhase { Waypoints = waypoints, DepartureClimbTargetFt = climbTo },
        };

        TrueHeading exitHeading;
        if (exitLeg == PatternEntryLeg.Downwind)
        {
            phases.Add(new CrosswindPhase { Waypoints = waypoints, DepartureClimbTargetFt = climbTo });
            exitHeading = waypoints.DownwindHeading;
        }
        else
        {
            // Crosswind exit: depart on the crosswind heading straight off the upwind turn.
            exitHeading = waypoints.CrosswindHeading;
        }

        phases.Add(
            new PatternExitPhase
            {
                ExitHeading = exitHeading,
                Direction = direction,
                ClimbTargetFt = climbTo,
            }
        );

        return phases;
    }

    /// <summary>
    /// Build the next full pattern circuit (from upwind) for an aircraft cycling in the pattern.
    /// Auto-cycle callers choose <paramref name="touchAndGo"/> based on the previous circuit's
    /// intent: true after a touch-and-go completion (TG cycling), false after a go-around from
    /// a landing-intent approach (the aircraft keeps trying to land full-stop).
    /// </summary>
    public static List<Phase> BuildNextCircuit(
        RunwayInfo runway,
        AircraftCategory category,
        string aircraftType,
        double windSpeedKt,
        PatternDirection direction,
        double? patternSizeNm,
        double? altitudeOverrideFt,
        IReadOnlyList<RunwayInfo>? airportRunways,
        GroundRunway? authoredRunway,
        bool touchAndGo,
        ActiveRunways activeRunways,
        PatternCommanded commanded
    )
    {
        return BuildCircuit(
            new PatternCircuitRequest
            {
                Runway = runway,
                Category = category,
                AircraftType = aircraftType,
                WindSpeedKt = windSpeedKt,
                Direction = direction,
                EntryLeg = PatternEntryLeg.Upwind,
                TouchAndGo = touchAndGo,
                FinalDistanceNm = null,
                PatternSizeNm = patternSizeNm,
                AltitudeOverrideFt = altitudeOverrideFt,
                AirportRunways = airportRunways,
                AuthoredRunway = authoredRunway,
                ActiveRunways = activeRunways,
                Commanded = commanded,
            }
        );
    }

    /// <summary>
    /// Build the circuit for an aircraft that climbs out on <see cref="RunwayTransitionCircuitRequest.FlownRunway"/> and flies
    /// the pattern of <see cref="RunwayTransitionCircuitRequest.PatternRunway"/> — a cross-runway closed-traffic departure
    /// (takeoff runway 33, make right traffic runway 28R) or an in-pattern runway switch on the
    /// upwind leg. Downwind/base/final always belong to the pattern (landing) runway; only the
    /// upwind belongs to the runway the aircraft is actually flying (AIM 4-3-2). Subsequent circuits
    /// are built entirely from the pattern runway by the auto-cycle.
    ///
    /// <para><b>Close parallels</b> (<see cref="RunwayGeometry.AreCloseParallels"/>): the two
    /// centerlines are a few hundred feet apart on the same heading, so the aircraft simply continues
    /// its upwind and turns crosswind beyond both departure ends
    /// (<see cref="PatternGeometry.ComputeTransition"/>), then flies the pattern runway's crosswind
    /// onto its downwind. Crossing the field is neither needed nor desirable there.</para>
    ///
    /// <para><b>Crossing runways</b>: the upwind is flown on the flown runway's extended centerline
    /// and a <see cref="MidfieldCrossingPhase"/> — with the initial turn biased toward the pattern
    /// side — connects to the pattern runway's downwind.</para>
    /// </summary>
    public static List<Phase> BuildRunwayTransitionCircuit(RunwayTransitionCircuitRequest request)
    {
        PatternWaypoints patternWaypoints = PatternGeometry.Compute(
            new PatternGeometryRequest(request.PatternRunway, request.Category, request.AircraftType, request.WindSpeedKt, request.Direction)
            {
                SizeOverrideNm = request.PatternSizeNm,
                AltitudeOverrideFt = request.AltitudeOverrideFt,
                AirportRunways = request.AirportRunways,
                AuthoredRunway = request.PatternAuthoredRunway,
                ActiveRunways = request.ActiveRunways,
                Commanded = request.Commanded,
            }
        );

        List<Phase> phases = RunwayGeometry.AreCloseParallels(request.FlownRunway, request.PatternRunway)
            ? BuildParallelTransitionLegs(request.FlownRunway, request.PatternRunway, patternWaypoints)
            : BuildCrossingTransitionLegs(
                new CrossingTransitionLegsRequest
                {
                    FlownRunway = request.FlownRunway,
                    Category = request.Category,
                    AircraftType = request.AircraftType,
                    WindSpeedKt = request.WindSpeedKt,
                    Direction = request.Direction,
                    PatternSizeNm = request.PatternSizeNm,
                    AltitudeOverrideFt = request.AltitudeOverrideFt,
                    AirportRunways = request.AirportRunways,
                    FlownAuthoredRunway = request.FlownAuthoredRunway,
                    PatternWaypoints = patternWaypoints,
                    ActiveRunways = request.ActiveRunways,
                    Commanded = request.Commanded,
                }
            );

        phases.Add(new BasePhase { Waypoints = patternWaypoints });
        phases.Add(new FinalApproachPhase());
        Phase landingPhase = request.Category == AircraftCategory.Helicopter ? new HelicopterLandingPhase() : new LandingPhase();
        phases.Add(request.TouchAndGo ? new TouchAndGoPhase() : landingPhase);

        return phases;
    }

    private static List<Phase> BuildParallelTransitionLegs(RunwayInfo flownRunway, RunwayInfo patternRunway, PatternWaypoints patternWaypoints)
    {
        PatternWaypoints transitionWaypoints = PatternGeometry.ComputeTransition(flownRunway, patternRunway, patternWaypoints);

        return
        [
            new UpwindPhase { Waypoints = transitionWaypoints },
            new CrosswindPhase { Waypoints = transitionWaypoints },
            // The transition crosswind ends abeam its own (farther) turn point, off the pattern
            // runway's computed downwind track; re-intercept it so base/final roll out on centerline.
            new DownwindPhase { Waypoints = patternWaypoints, RejoinTrack = true },
        ];
    }

    /// <summary>The inputs of the crossing-runway transition legs: both runways' geometry and the circuit they join.</summary>
    private sealed record CrossingTransitionLegsRequest
    {
        public required RunwayInfo FlownRunway { get; init; }

        public required AircraftCategory Category { get; init; }

        public required string AircraftType { get; init; }

        public required double WindSpeedKt { get; init; }

        public required PatternDirection Direction { get; init; }

        public required double? PatternSizeNm { get; init; }

        public required double? AltitudeOverrideFt { get; init; }

        public required IReadOnlyList<RunwayInfo>? AirportRunways { get; init; }

        public required GroundRunway? FlownAuthoredRunway { get; init; }

        public required PatternWaypoints PatternWaypoints { get; init; }

        public required ActiveRunways ActiveRunways { get; init; }

        public required PatternCommanded Commanded { get; init; }
    }

    private static List<Phase> BuildCrossingTransitionLegs(CrossingTransitionLegsRequest request)
    {
        // The upwind on the flown runway climbs out into the pattern runway's circuit, so it flies that circuit's compression
        // rather than a second decision of its own.
        PatternWaypoints flownWaypoints = PatternGeometry
            .Compute(
                new PatternGeometryRequest(request.FlownRunway, request.Category, request.AircraftType, request.WindSpeedKt, request.Direction)
                {
                    SizeOverrideNm = request.PatternSizeNm,
                    AltitudeOverrideFt = request.AltitudeOverrideFt,
                    AirportRunways = request.AirportRunways,
                    AuthoredRunway = request.FlownAuthoredRunway,
                    ActiveRunways = request.ActiveRunways,
                    Commanded = request.Commanded with { NeverCompresses = true },
                }
            )
            .WithCompressionOf(request.PatternWaypoints);

        var crossing = new MidfieldCrossingPhase
        {
            Waypoints = request.PatternWaypoints,
            InitialTurn = request.Direction == PatternDirection.Left ? TurnDirection.Left : TurnDirection.Right,
            // A departure into another runway's pattern never left the pattern: it climbs out on the
            // runway it used and crosses to the other downwind at traffic-pattern altitude. AIM
            // 4-3-3.a.2's 1,500 ft AGL crossing is an *entry* rule for arrivals joining from outside.
            CrossAtPatternAltitude = true,
        };
        var downwind = new DownwindPhase { Waypoints = request.PatternWaypoints };

        List<Phase> phases = [new UpwindPhase { Waypoints = flownWaypoints }];
        phases.AddRange(
            BuildFieldCrossingPrefix(
                crossing,
                request.PatternWaypoints,
                request.Category,
                request.AltitudeOverrideFt,
                request.FlownRunway.AirportElevationFt,
                [downwind]
            )
        );
        phases.Add(downwind);
        return phases;
    }

    /// <summary>
    /// The phases that carry an aircraft across the field and onto the downwind of
    /// <paramref name="circuit"/>: the crossing itself, and — when it is flown above pattern altitude —
    /// a <see cref="TeardropReentryPhase"/> to shed that height on an outbound leg and a 45° intercept to
    /// abeam before joining. The gate is the altitude the crossing will actually be flown at
    /// (<see cref="MidfieldCrossingPhase.ResolveCrossingAltitude(bool, AircraftCategory, double?, double, double?)"/>),
    /// not the category alone: a piston or helicopter entry, an in-pattern crossover, a departure crossing
    /// into another runway's pattern, a jet flying a controller-assigned pattern altitude, and a turbine
    /// entering at a field whose pattern altitude already is the AIM 4-3-3.a.2 entry height all cross at
    /// pattern altitude and have nothing to descend. Those drop straight into the downwind, and that
    /// downwind re-intercepts its computed track — the crossing can leave the aircraft inside it, and
    /// base/final geometry built for the computed width would otherwise turn early.
    ///
    /// <para>Every join that crosses the field flies this: the arrival entry and the wrong-side MLT/MRT
    /// rebuild (<c>PatternCommandHandler</c>) and the crossing-runway transition circuit
    /// (<see cref="BuildRunwayTransitionCircuit"/>, e.g. <c>CTO 33 MRT 28R</c>).</para>
    /// </summary>
    /// <param name="crossing">The crossing the prefix leads with.</param>
    /// <param name="waypoints">The circuit's waypoints, shared with the crossing and the re-entry.</param>
    /// <param name="category">The aircraft's category, used to resolve the crossing altitude.</param>
    /// <param name="altitudeOverrideFt">A controller-assigned pattern altitude, which outranks the entry height (AIM 4-4-7.b).</param>
    /// <param name="airportElevationFt">
    /// Field elevation (ft MSL) the AIM 4-3-3.a.2 entry height is measured from. Null when the caller has
    /// no runway: the crossing then resolves to pattern altitude, so no teardrop is inserted.
    /// </param>
    /// <param name="circuit">The rest of the circuit, whose downwind this prefix joins.</param>
    public static List<Phase> BuildFieldCrossingPrefix(
        MidfieldCrossingPhase crossing,
        PatternWaypoints waypoints,
        AircraftCategory category,
        double? altitudeOverrideFt,
        double? airportElevationFt,
        IReadOnlyList<Phase> circuit
    )
    {
        var prefix = new List<Phase> { crossing };
        if (CrossingNeedsTeardrop(crossing, waypoints, category, altitudeOverrideFt, airportElevationFt))
        {
            prefix.Add(new TeardropReentryPhase { Waypoints = waypoints });
        }
        else if (circuit.OfType<DownwindPhase>().FirstOrDefault() is { } joinDownwind)
        {
            joinDownwind.RejoinTrack = true;
        }

        return prefix;
    }

    /// <summary>
    /// Brings a queued field crossing in <paramref name="phases"/> into line with a pattern altitude that changed after
    /// <see cref="BuildFieldCrossingPrefix"/> built it. The crossing and its re-entry read the altitude live when they start, so
    /// a teardrop decided on the old altitude would otherwise be flown on the new one: an assigned altitude leaves a teardrop
    /// with nothing to shed (a level 3 nm outbound leg), and a cleared one leaves an entry-height crossing with no teardrop to
    /// shed it. The first crossing that has not started is decided again exactly as the prefix decides it: the re-entry after
    /// it is inserted or removed (a queued re-entry is always rebuilt on the crossing's current waypoints), and the downwind it
    /// joins re-intercepts its track only when no re-entry leads into it. A crossing already flying has read the altitude and
    /// is left alone.
    /// </summary>
    /// <param name="phases">The phase list that may hold a queued crossing.</param>
    /// <param name="category">The aircraft's category, used to resolve the crossing altitude.</param>
    /// <param name="altitudeOverrideFt">The pattern altitude now standing (<see cref="AircraftPattern.AltitudeOverrideFt"/>).</param>
    public static void ReconcileFieldCrossingPrefix(PhaseList phases, AircraftCategory category, double? altitudeOverrideFt)
    {
        List<Phase> list = phases.Phases;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is MidfieldCrossingPhase { Status: PhaseStatus.Pending, Waypoints: { } waypoints } crossing)
            {
                bool needsTeardrop = CrossingNeedsTeardrop(
                    crossing,
                    waypoints,
                    category,
                    altitudeOverrideFt,
                    phases.AssignedRunway?.AirportElevationFt
                );
                RebuildReentry(list, i + 1, waypoints, needsTeardrop);
                return;
            }
        }
    }

    /// <summary>
    /// Replaces a queued re-entry at <paramref name="reentryIndex"/> with a fresh one on <paramref name="waypoints"/> when
    /// <paramref name="needsTeardrop"/>, or removes it when not, and sets the joined downwind's track re-intercept to match.
    /// </summary>
    private static void RebuildReentry(List<Phase> list, int reentryIndex, PatternWaypoints waypoints, bool needsTeardrop)
    {
        if ((reentryIndex < list.Count) && (list[reentryIndex] is TeardropReentryPhase { Status: PhaseStatus.Pending }))
        {
            list.RemoveAt(reentryIndex);
        }

        if (needsTeardrop)
        {
            list.Insert(reentryIndex, new TeardropReentryPhase { Waypoints = waypoints });
        }

        if (list.Skip(reentryIndex).OfType<DownwindPhase>().FirstOrDefault() is { } joinDownwind)
        {
            joinDownwind.RejoinTrack = !needsTeardrop;
        }
    }

    /// <summary>
    /// Whether <paramref name="crossing"/> is flown above pattern altitude, so a <see cref="TeardropReentryPhase"/> must shed
    /// the height before the downwind: the altitude it will actually be flown at
    /// (<see cref="MidfieldCrossingPhase.ResolveCrossingAltitude(bool, AircraftCategory, double?, double, double?)"/>) against
    /// <paramref name="waypoints"/>' pattern altitude.
    /// </summary>
    private static bool CrossingNeedsTeardrop(
        MidfieldCrossingPhase crossing,
        PatternWaypoints waypoints,
        AircraftCategory category,
        double? altitudeOverrideFt,
        double? airportElevationFt
    )
    {
        double crossingAlt = MidfieldCrossingPhase.ResolveCrossingAltitude(
            crossing.CrossAtPatternAltitude,
            category,
            altitudeOverrideFt,
            waypoints.PatternAltitude,
            airportElevationFt
        );
        return crossingAlt > waypoints.PatternAltitude + FlightPhysics.AltitudeSnapFt;
    }

    /// <summary>
    /// Update waypoints on all active/pending pattern phases in the list.
    /// Returns true if any pattern phases were found.
    /// </summary>
    public static bool UpdateWaypoints(PhaseList phaseList, PatternWaypoints waypoints)
    {
        bool found = false;
        foreach (Phase phase in phaseList.Phases)
        {
            if (phase.Status is PhaseStatus.Pending or PhaseStatus.Active)
            {
                found |= TrySetWaypoints(phase, waypoints);
            }
        }

        return found;
    }

    /// <summary>
    /// Moves every pending pattern phase in the list onto <paramref name="patternAltitudeFt"/>, each keeping its own
    /// geometry: a runway-transition circuit holds two runways' waypoints in one list, so one recomputed set cannot stand in
    /// for all of them. Phases that shared a waypoints instance still share one. A leg already flying caches its targets at
    /// its start and is left on the waypoints it started with.
    /// </summary>
    /// <param name="phaseList">The phase list whose queued pattern phases take the new altitude.</param>
    /// <param name="patternAltitudeFt">The pattern altitude now standing (feet MSL).</param>
    public static void SetPendingPatternAltitude(PhaseList phaseList, double patternAltitudeFt)
    {
        var copies = new Dictionary<PatternWaypoints, PatternWaypoints>(ReferenceEqualityComparer.Instance);
        foreach (Phase phase in phaseList.Phases)
        {
            if ((phase.Status is not PhaseStatus.Pending) || (AirborneFollowHelper.PatternWaypointsOf(phase) is not { } own))
            {
                continue;
            }

            if (!copies.TryGetValue(own, out PatternWaypoints? copy))
            {
                copy = own.WithPatternAltitude(patternAltitudeFt);
                copies[own] = copy;
            }

            TrySetWaypoints(phase, copy);
        }
    }

    /// <summary>Sets <paramref name="waypoints"/> on <paramref name="phase"/> and returns whether it carries pattern waypoints.</summary>
    private static bool TrySetWaypoints(Phase phase, PatternWaypoints waypoints)
    {
        switch (phase)
        {
            case UpwindPhase up:
                up.Waypoints = waypoints;
                return true;
            case CrosswindPhase cw:
                cw.Waypoints = waypoints;
                return true;
            case DownwindPhase dw:
                dw.Waypoints = waypoints;
                return true;
            case BasePhase bp:
                bp.Waypoints = waypoints;
                return true;
            case MidfieldCrossingPhase mc:
                mc.Waypoints = waypoints;
                return true;
            case TeardropReentryPhase td:
                td.Waypoints = waypoints;
                return true;
            default:
                return false;
        }
    }
}
