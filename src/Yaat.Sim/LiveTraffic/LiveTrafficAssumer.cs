using Microsoft.Extensions.Logging;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Mva;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.LiveTraffic;

/// <summary>
/// <c>ASSUME</c>: converts a live-traffic shadow in place into an ordinary simulated aircraft. Never
/// refused — the RPO always gets control — so the job is to seed a controllable state for whatever
/// the aircraft is doing: clearance fields from the feed win, then the inference rules
/// (level/climbing/descending, established on a final, initial climb, route rejoin, hold, VFR,
/// runway/surface kinds via <see cref="RunwayOccupancy"/>). Nothing is said on frequency: the real
/// crew has been talking to the controller all along.
/// </summary>
public static class LiveTrafficAssumer
{
    private static readonly ILogger Log = SimLog.CreateLogger("LiveTrafficAssumer");

    /// <summary>Above 10 000 ft the speed seed is clamped to this band around the type's default (no Vmo data is available).</summary>
    public const double SpeedClampLowRatio = 0.75;
    public const double SpeedClampHighRatio = 1.25;

    /// <summary>14 CFR §91.117: the IAS seed is capped at 250 kt below 10 000 ft (anything above is a wind-model artefact).</summary>
    public const double SpeedLimitBelow10kKts = 250;

    /// <summary>Below this a VFR cruising altitude (§91.159) is not required; a descending VFR aircraft levels instead of targeting one.</summary>
    public const double VfrCruisingAltitudeFloorFt = 3_500;
    public const double MachSeedFloorFt = 24_000;

    /// <summary>Level when the last samples span ≤ 200 ft (Mode C is 100-ft quantised) and the smoothed rate is ≤ 400 fpm.</summary>
    public const double LevelAltitudeSpanFt = 200;
    public const double LevelMaxVerticalSpeedFpm = 400;
    public const int LevelSampleCount = 3;

    /// <summary>Descent floor when neither the feed nor a procedure gives a target: destination elevation + this, to the next 1 000 ft.</summary>
    public const double DescentFloorAboveFieldFt = 2_000;

    /// <summary>Established on a final: within the approach gate, ≤ 10° off the final course, ≤ 0.3 nm cross-track.</summary>
    public const double EstablishedHeadingDeg = 10;
    public const double EstablishedCrossTrackNm = 0.3;

    /// <summary>Visual-approach install: inside the gate, ≤ 30° off the final, ≤ 1 nm cross-track, within 10 nm, field VMC.</summary>
    public const double VisualHeadingDeg = 30;
    public const double VisualCrossTrackNm = 1.0;
    public const double VisualMaxDistanceNm = 10;
    public const int VmcCeilingFt = 1_000;
    public const double VmcVisibilitySm = 3;

    /// <summary>Initial climb: airborne, climbing, within this of a runway end, aligned within <see cref="InitialClimbHeadingDeg"/>.</summary>
    public const double InitialClimbDistanceNm = 3;
    public const double InitialClimbHeadingDeg = 30;
    public const double ClimbingMinFpm = 300;

    /// <summary>Route rejoin: the candidate fix must be within this cone of the track and at least the larger of 2 nm / 30 s ahead.</summary>
    public const double RejoinConeDeg = 45;
    public const double RejoinMinDistanceNm = 2;
    public const double RejoinMinSeconds = 30;
    public const double ArrivalProfileDistanceNm = 30;

    /// <summary>Hold signature: ≥ 180° of turn over ≤ 90 s within 3 nm (AIM 5-3-8: 3°/s standard rate with straight legs between).</summary>
    public const double HoldTurnSumDeg = 180;
    public const double HoldWindowSeconds = 90;
    public const double HoldMaxDisplacementNm = 3;

    /// <summary>A rollout still faster than this gets <see cref="RunwayExitPhase"/> rather than a bare ground state.</summary>
    public const double RolloutExitMinSpeedKts = 30;

    public static CommandResult Assume(AircraftState aircraft, DispatchContext ctx)
    {
        if (aircraft.LiveTraffic is not { } lt)
        {
            return new CommandResult(false, $"{aircraft.Callsign} is not live traffic");
        }

        LiveTrafficKinematics.Advance(aircraft, 0, ctx.Weather, ctx.ScenarioElapsedSeconds);
        aircraft.LiveTraffic = null;
        aircraft.Queue.Blocks.Clear();
        aircraft.DeferredDispatches.Clear();

        var notes = new List<string>();
        NoteEmergencySquawk(aircraft, notes);
        if (lt.IsCoasting)
        {
            notes.Add("assumed from a coasting track — position is dead-reckoned; verbal coordination required (§5-4-5)");
        }

        string summary = SeedState(aircraft, lt, ctx, notes);
        foreach (var note in notes)
        {
            aircraft.PendingWarnings.Add(note);
        }

        Log.LogInformation("{Callsign} assumed: {Summary}", aircraft.Callsign, summary);
        return new CommandResult(true, $"{aircraft.Callsign} assumed — {summary}");
    }

    private static string SeedState(AircraftState ac, AircraftLiveTraffic lt, DispatchContext ctx, List<string> notes)
    {
        var (runway, kind) = ClassifyRunwayUse(ac, ctx);
        if (ac.IsOnGround)
        {
            return SeedSurface(ac, ctx, runway, kind);
        }

        if (kind == RunwayUseKind.Landing && runway is not null)
        {
            SeedSpeed(ac, lt);
            ac.Phases = new PhaseList { AssignedRunway = runway, LandingClearance = ClearanceType.ClearedToLand };
            // A rotorcraft settles onto the spot (§3-11-6); the fixed-wing flare/rollout/exit chain would snap it to the centreline.
            ac.Phases.Add(RunwayOccupancy.IsRotorcraft(ac) ? new HelicopterLandingPhase() : new LandingPhase());
            return $"landing runway {RunwayIdentifier.ToDisplayDesignator(runway.Designator)}";
        }

        if (IsVfr(ac))
        {
            SeedSpeed(ac, lt);
            ac.Targets.TargetTrueHeading = ClearedOrCurrentHeading(ac, lt);
            return $"VFR, maintain VFR, {SeedVfrVertical(ac, lt)}";
        }

        if (DetectHold(ac, lt))
        {
            SeedSpeed(ac, lt);
            ac.Targets.TargetTrueHeading = ac.TrueHeading;
            ac.Targets.TargetAltitude = RoundTo100(ac.Altitude);
            ac.Targets.AssignedAltitude = ac.Targets.TargetAltitude;
            notes.Add(
                lt.HoldFix is { } fix
                    ? $"in a hold at {fix} per the feed — reissue holding or a rejoin (§4-6-1)"
                    : "in a hold — reissue holding or a rejoin (§4-6-1)"
            );
            return "holding, heading and altitude held";
        }

        // Lateral first: a STAR profile installed by the rejoin writes a speed restriction into the targets,
        // and the feed's cleared speed / the observed speed must win over it (seeded afterwards).
        string lateral = SeedLateral(ac, lt, ctx, runway, kind, notes);
        SeedSpeed(ac, lt);
        if (ac.Phases?.ActiveApproach is not null)
        {
            return lateral;
        }

        string vertical = SeedVertical(ac, lt, notes);
        return $"{lateral}, {vertical}";
    }

    // --- runway / surface ---

    private static (RunwayInfo? Runway, RunwayUseKind? Kind) ClassifyRunwayUse(AircraftState ac, DispatchContext ctx)
    {
        RunwayInfo? best = null;
        RunwayUseKind? bestKind = null;
        foreach (var runway in CandidateRunways(ac, ctx))
        {
            var kind = RunwayOccupancy.ClassifyByGeometry(ac, runway, ctx.GroundLayout);
            if (kind is null || (bestKind is not null && kind.Value >= bestKind.Value))
            {
                continue;
            }

            best = runway;
            bestKind = kind;
        }

        return (best, bestKind);
    }

    private static IReadOnlyList<RunwayInfo> RunwaysFor(string airport) => RunwayOccupancy.AirportRunways(airport);

    private static IEnumerable<RunwayInfo> CandidateRunways(AircraftState ac, DispatchContext ctx)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var airport in new[] { ctx.GroundLayout?.AirportId, ac.FlightPlan.Destination, ac.FlightPlan.Departure })
        {
            if (string.IsNullOrWhiteSpace(airport) || !seen.Add(airport))
            {
                continue;
            }

            // The nav DB lists pavements; orient each to the end the aircraft is tracking toward.
            foreach (var runway in RunwaysFor(airport))
            {
                yield return RunwayOccupancy.AlignedEnd(ac.TrueTrack.Degrees, runway);
            }
        }
    }

    private static string SeedSurface(AircraftState ac, DispatchContext ctx, RunwayInfo? runway, RunwayUseKind? kind)
    {
        ac.Targets.TargetSpeed = ac.IndicatedAirspeed;
        ac.Targets.TargetTrueHeading = ac.TrueHeading;
        if (ctx.GroundLayout is { } layout)
        {
            ac.Ground.Layout = layout;
            ac.Ground.LayoutAirportId = layout.AirportId;
        }

        if (runway is not null && kind == RunwayUseKind.Departing)
        {
            ac.Phases = new PhaseList { AssignedRunway = runway, DepartureRunway = runway };
            ac.Phases.Add(new TakeoffPhase());
            ac.Procedure.DepartureRunway = runway.Designator;
            return $"rolling on runway {RunwayIdentifier.ToDisplayDesignator(runway.Designator)}";
        }

        if (runway is not null && kind == RunwayUseKind.OnSurface && ac.IndicatedAirspeed > RolloutExitMinSpeedKts)
        {
            ac.Phases = new PhaseList { AssignedRunway = runway };
            ac.Phases.Add(new RunwayExitPhase());
            return $"rolling out on runway {RunwayIdentifier.ToDisplayDesignator(runway.Designator)}";
        }

        if (kind is null or RunwayUseKind.Crossing && ctx.GroundLayout is { } snapLayout)
        {
            GroundSpawnSnap.Apply(ac, snapLayout);
        }

        return kind is null ? "on the surface" : $"on runway {RunwayIdentifier.ToDisplayDesignator(runway!.Designator)}";
    }

    // --- speed ---

    private static void SeedSpeed(AircraftState ac, AircraftLiveTraffic lt)
    {
        var cat = AircraftCategorization.Categorize(ac.AircraftType);
        double def = AircraftPerformance.DefaultSpeed(ac.AircraftType, cat, ac.Altitude, null);
        double ias;
        if (ac.Altitude < 10_000)
        {
            // Terminal: an arrival slowed for the approach must not be sped back up (§5-7-1.b.4), so the
            // floor is the type's approach speed and the cap is the §91.117 limit.
            double floor = AircraftPerformance.ApproachSpeed(ac.AircraftType, cat);
            ias = Math.Clamp(ac.IndicatedAirspeed, floor, Math.Max(floor, SpeedLimitBelow10kKts));
        }
        else
        {
            ias = Math.Clamp(ac.IndicatedAirspeed, def * SpeedClampLowRatio, def * SpeedClampHighRatio);
        }

        ac.Targets.TargetSpeed = lt.ClearedSpeedKts ?? ias;
        if (ac.Altitude >= MachSeedFloorFt)
        {
            ac.Targets.TargetMach = WindInterpolator.IasToMach(ac.IndicatedAirspeed, ac.Altitude);
        }
    }

    // --- vertical ---

    private static string SeedVertical(AircraftState ac, AircraftLiveTraffic lt, List<string> notes)
    {
        double vs = ac.VerticalSpeed;
        if (IsLevel(ac, lt))
        {
            ac.Targets.TargetAltitude = RoundTo100(ac.Altitude);
            ac.Targets.AssignedAltitude = ac.Targets.TargetAltitude;
            return $"level {ac.Targets.TargetAltitude:F0}";
        }

        double? cleared = lt.InterimAltitudeFt ?? lt.AssignedAltitudeFt;
        ac.Targets.DesiredVerticalRate = Math.Abs(vs);
        if (vs > 0)
        {
            double target = cleared ?? ac.FlightPlan.Altitude.CruiseFeet ?? RoundUpTo1000(ac.Altitude + 1);
            target = Math.Max(target, RoundTo100(ac.Altitude));
            ac.Targets.TargetAltitude = target;
            ac.Targets.AssignedAltitude = target;
            return $"climbing to {target:F0}";
        }

        double? floor = cleared ?? NextRestrictionBelow(ac) ?? DescentFloor(ac);
        if (floor is null)
        {
            // No clearance, no procedure, no MVA coverage, no destination: an unbounded descent is never
            // seeded (§5-6-1.a.3) — level off and let the controller assign an altitude.
            ac.Targets.DesiredVerticalRate = null;
            ac.Targets.TargetAltitude = RoundDownTo100(ac.Altitude);
            ac.Targets.AssignedAltitude = ac.Targets.TargetAltitude;
            notes.Add("descending with no altitude to descend to — levelled off; assign an altitude");
            return $"level {ac.Targets.TargetAltitude:F0}";
        }

        double descentTarget = Math.Min(floor.Value, RoundDownTo100(ac.Altitude));
        ac.Targets.TargetAltitude = descentTarget;
        ac.Targets.AssignedAltitude = descentTarget;
        return $"descending to {descentTarget:F0}";
    }

    /// <summary>The first published at/at-or-below crossing altitude on the installed route that lies below the aircraft.</summary>
    private static double? NextRestrictionBelow(AircraftState ac)
    {
        foreach (var target in ac.Targets.NavigationRoute)
        {
            if (target.AltitudeRestriction is { } r && r.Type != CifpAltitudeRestrictionType.AtOrAbove && r.Altitude1Ft < ac.Altitude)
            {
                return r.Altitude1Ft;
            }
        }

        return null;
    }

    private static string SeedVfrVertical(AircraftState ac, AircraftLiveTraffic lt)
    {
        double vs = ac.VerticalSpeed;
        if (IsLevel(ac, lt))
        {
            ac.Targets.TargetAltitude = RoundTo100(ac.Altitude);
            return $"level {ac.Targets.TargetAltitude:F0}";
        }

        ac.Targets.DesiredVerticalRate = Math.Abs(vs);
        double course = ac.MagneticTrack.Degrees;
        if (vs > 0)
        {
            ac.Targets.TargetAltitude = NextVfrCruisingAltitude(ac.Altitude, course, up: true);
            return $"climbing to {ac.Targets.TargetAltitude:F0}";
        }

        double below = NextVfrCruisingAltitude(ac.Altitude, course, up: false);
        if (below < VfrCruisingAltitudeFloorFt)
        {
            ac.Targets.DesiredVerticalRate = null;
            ac.Targets.TargetAltitude = RoundDownTo100(ac.Altitude);
            return $"level {ac.Targets.TargetAltitude:F0}";
        }

        ac.Targets.TargetAltitude = below;
        return $"descending to {below:F0}";
    }

    /// <summary>§91.159: magnetic course 0–179° → odd thousands + 500; 180–359° → even thousands + 500.</summary>
    public static double NextVfrCruisingAltitude(double altitudeFt, double magneticCourseDeg, bool up)
    {
        bool odd = magneticCourseDeg < 180;
        double thousands = Math.Floor(altitudeFt / 1000.0);
        for (int i = 0; i < 60; i++)
        {
            double k = up ? thousands + i : thousands - i;
            double candidate = (k * 1000) + 500;
            bool parityOk = (((int)k) % 2 == 1) == odd;
            if (parityOk && ((up && candidate > altitudeFt + 1) || (!up && candidate < altitudeFt - 1)))
            {
                return candidate;
            }
        }

        return RoundTo100(altitudeFt);
    }

    private static bool IsLevel(AircraftState ac, AircraftLiveTraffic lt)
    {
        if (lt.IsCoasting)
        {
            return true;
        }

        var recent = lt.History.Skip(Math.Max(0, lt.History.Count - LevelSampleCount)).ToList();
        double span = recent.Count == 0 ? 0 : recent.Max(h => h.AltitudeFt) - recent.Min(h => h.AltitudeFt);
        return span <= LevelAltitudeSpanFt && Math.Abs(ac.VerticalSpeed) <= LevelMaxVerticalSpeedFpm;
    }

    private static double? DescentFloor(AircraftState ac)
    {
        double? mva = MvaDatabase.Default.GetFloorFtMsl(ac.Position);
        double? fieldFloor = null;
        if (!string.IsNullOrWhiteSpace(ac.FlightPlan.Destination) && RunwaysFor(ac.FlightPlan.Destination) is { Count: > 0 } runways)
        {
            fieldFloor = RoundUpTo1000(runways[0].AirportElevationFt + DescentFloorAboveFieldFt);
        }

        if (mva is null && fieldFloor is null)
        {
            return null;
        }

        return Math.Max(mva ?? 0, fieldFloor ?? 0);
    }

    // --- lateral ---

    private static string SeedLateral(
        AircraftState ac,
        AircraftLiveTraffic lt,
        DispatchContext ctx,
        RunwayInfo? runway,
        RunwayUseKind? kind,
        List<string> notes
    )
    {
        if (TrySeedFinal(ac, ctx, notes) is { } finalSummary)
        {
            return finalSummary;
        }

        if (TrySeedInitialClimb(ac, lt) is { } climbSummary)
        {
            return climbSummary;
        }

        var (rejoinSummary, rejoined) = TrySeedRouteRejoin(ac, lt, notes);
        if (rejoined)
        {
            return rejoinSummary!;
        }

        ac.Targets.TargetTrueHeading = ClearedOrCurrentHeading(ac, lt);
        notes.Add(rejoinSummary ?? "assumed on vectors — no route rejoin");
        return $"heading {ac.MagneticHeading.Degrees:000}";
    }

    private static TrueHeading ClearedOrCurrentHeading(AircraftState ac, AircraftLiveTraffic lt) =>
        lt.ClearedHeadingDeg is { } cleared ? new MagneticHeading(cleared).ToTrue(ac.Declination) : ac.TrueHeading;

    private static string? TrySeedFinal(AircraftState ac, DispatchContext ctx, List<string> notes)
    {
        if (ac.VerticalSpeed > ClimbingMinFpm)
        {
            return null;
        }

        foreach (var runway in CandidateRunways(ac, ctx))
        {
            double distNm = RunwayOccupancy.DistanceToLandingThresholdNm(ac, runway, ctx.GroundLayout);
            var threshold = LandingThreshold.Resolve(runway, ctx.GroundLayout);
            double displacementNm = GeoMath.DistanceNm(new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), threshold);
            double gateNm =
                ApproachGateDatabase.GetMinInterceptDistanceNm(runway.AirportId, runway.Designator, displacementNm)
                - ApproachGateDatabase.InterceptPaddingNm;
            double offDeg = Math.Abs(AngleDiff(ac.TrueTrack.Degrees, runway.TrueHeading.Degrees));
            double xtkNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(ac.Position, threshold, runway.TrueHeading));
            bool approachSide = GeoMath.AlongTrackDistanceNm(ac.Position, threshold, runway.TrueHeading) < 0;
            if (!approachSide || distNm > Math.Max(gateNm, VisualMaxDistanceNm) || offDeg > VisualHeadingDeg || xtkNm > VisualCrossTrackNm)
            {
                continue;
            }

            string display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
            if (distNm <= gateNm && offDeg <= EstablishedHeadingDeg && xtkNm <= EstablishedCrossTrackNm)
            {
                ac.Procedure.DestinationRunway = runway.Designator;
                var result = ApproachCommandHandler.TryClearedApproach(
                    new ClearedApproachCommand(null, runway.AirportId, true, null, null, null, null, null, null, null, null),
                    ac
                );
                if (result.Success)
                {
                    notes.Add($"on the approach to runway {display} — no landing clearance is implied");
                    return $"established on final runway {display}";
                }

                Log.LogDebug("{Callsign}: no coded approach for runway {Runway}: {Reason}", ac.Callsign, display, result.Message);
            }

            if (distNm <= gateNm && IsFieldVmc(ctx, runway.AirportId))
            {
                ac.Approach.HasReportedFieldInSight = true;
                var visual = ApproachCommandHandler.TryClearedVisualApproach(
                    new ClearedVisualApproachCommand(runway.Designator, runway.AirportId, null, null, false),
                    ac,
                    ctx
                );
                if (visual.Success)
                {
                    notes.Add($"on a visual approach to runway {display} — no landing clearance is implied");
                    return $"visual approach runway {display}";
                }

                Log.LogDebug("{Callsign}: visual approach to {Runway} not installed: {Reason}", ac.Callsign, display, visual.Message);
            }

            ac.Targets.TargetTrueHeading = ac.TrueHeading;
            notes.Add($"on vectors to the runway {display} final — issue the approach clearance");
            return $"heading {ac.MagneticHeading.Degrees:000} toward runway {display}";
        }

        return null;
    }

    private static bool IsFieldVmc(DispatchContext ctx, string airportId)
    {
        var metar = ctx.Weather?.GetWeatherForAirport(airportId);
        if (metar is null)
        {
            return true;
        }

        bool ceilingOk = metar.CeilingFeetAgl is not { } ceiling || ceiling >= VmcCeilingFt;
        bool visOk = metar.VisibilityStatuteMiles is not { } vis || vis >= VmcVisibilitySm;
        return ceilingOk && visOk;
    }

    /// <summary>
    /// Just off the departure end and climbing: the aircraft is on its cleared heading, the runway heading,
    /// or its SID — never turned direct to an enroute fix (§5-6-3 NOTE), which is why this runs before the
    /// route rejoin. The filed SID's restrictions are activated when the route names one.
    /// </summary>
    private static string? TrySeedInitialClimb(AircraftState ac, AircraftLiveTraffic lt)
    {
        if (ac.VerticalSpeed < ClimbingMinFpm || string.IsNullOrWhiteSpace(ac.FlightPlan.Departure))
        {
            return null;
        }

        foreach (var pavement in RunwaysFor(ac.FlightPlan.Departure))
        {
            var runway = RunwayOccupancy.AlignedEnd(ac.TrueTrack.Degrees, pavement);
            var end = new LatLon(runway.EndLatitude, runway.EndLongitude);
            if (
                GeoMath.DistanceNm(ac.Position, end) > InitialClimbDistanceNm
                || Math.Abs(AngleDiff(ac.TrueTrack.Degrees, runway.TrueHeading.Degrees)) > InitialClimbHeadingDeg
            )
            {
                continue;
            }

            ac.Procedure.DepartureRunway = runway.Designator;
            bool sid = NavigationCommandHandler.TryActivateFiledSid(ac);
            ac.Targets.TargetTrueHeading = lt.ClearedHeadingDeg is { } cleared
                ? new MagneticHeading(cleared).ToTrue(ac.Declination)
                : runway.TrueHeading;
            string display = RunwayIdentifier.ToDisplayDesignator(runway.Designator);
            string heading = $"{ac.Targets.TargetTrueHeading.Value.ToMagnetic(ac.Declination).Degrees:000}";
            return sid
                ? $"initial climb runway {display} heading {heading}, {ac.Procedure.ActiveSidId} restrictions active"
                : $"initial climb runway {display} heading {heading}";
        }

        return null;
    }

    /// <summary>
    /// Installs the filed route from the next fix ahead. Returns the summary and whether a route was
    /// installed; when not, the summary carries the reason for the terminal note.
    /// </summary>
    private static (string? Summary, bool Rejoined) TrySeedRouteRejoin(AircraftState ac, AircraftLiveTraffic lt, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(ac.FlightPlan.Route))
        {
            return (null, false);
        }

        var navDb = NavigationDatabase.Instance;
        var resolved = new List<ResolvedFix>();
        foreach (var name in RouteExpander.Expand(ac.FlightPlan.Route, navDb, includeAllTransitionsOnMismatch: false))
        {
            if (navDb.ResolveFixOrFrd(name) is { } pos)
            {
                resolved.Add(new ResolvedFix(name, pos.Lat, pos.Lon));
            }
        }

        if (resolved.Count == 0)
        {
            return ("filed route has no resolvable fixes", false);
        }

        int candidate = NextFixAhead(ac, resolved);
        if (candidate < 0)
        {
            return ("assumed on vectors — no route fix ahead within the rejoin cone", false);
        }

        ac.Targets.NavigationRoute.Clear();
        foreach (var fix in resolved.Skip(candidate))
        {
            ac.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix.Name, Position = new LatLon(fix.Lat, fix.Lon) });
        }

        var warnings = new List<string>();
        if (IsWithinOfAirport(ac, ac.FlightPlan.Destination, ArrivalProfileDistanceNm))
        {
            ArrivalRouteResolver.ApplyAltitudeProfile(ac, ac.FlightPlan.Route, warnings);
        }
        else if (IsWithinOfAirport(ac, ac.FlightPlan.Departure, ArrivalProfileDistanceNm))
        {
            NavigationCommandHandler.TryActivateFiledSid(ac);
        }

        notes.AddRange(warnings);
        return ($"direct {resolved[candidate].Name} then the filed route", true);
    }

    /// <summary>
    /// Index of the fix to rejoin at: the fix after the closest route leg, skipping at most one fix
    /// the aircraft is already past or abeam (bearing outside ±90° of track). Requires the candidate
    /// within <see cref="RejoinConeDeg"/> of the track and at least the rejoin distance ahead. -1 when none.
    /// </summary>
    public static int NextFixAhead(AircraftState ac, IReadOnlyList<ResolvedFix> route)
    {
        int candidate;
        if (route.Count == 1)
        {
            candidate = 0;
        }
        else
        {
            int closestLeg = 0;
            double closestFt = double.MaxValue;
            for (int i = 0; i < route.Count - 1; i++)
            {
                double d = GeoMath.DistanceToSegmentFt(ac.Position, Point(route[i]), Point(route[i + 1]));
                if (d < closestFt)
                {
                    closestFt = d;
                    closestLeg = i;
                }
            }

            candidate = closestLeg + 1;
        }

        if (IsBehind(ac, route[candidate]))
        {
            candidate++;
            if (candidate >= route.Count)
            {
                return -1;
            }
        }

        var fix = Point(route[candidate]);
        double bearingOff = Math.Abs(AngleDiff(GeoMath.BearingTo(ac.Position, fix), ac.TrueTrack.Degrees));
        double minNm = Math.Max(RejoinMinDistanceNm, ac.GroundSpeed * RejoinMinSeconds / 3600.0);
        return bearingOff <= RejoinConeDeg && GeoMath.DistanceNm(ac.Position, fix) >= minNm ? candidate : -1;
    }

    /// <summary>Abeam counts as behind: a fix at 90° off the track cannot be flown to without a turn back.</summary>
    private static bool IsBehind(AircraftState ac, ResolvedFix fix) =>
        Math.Abs(AngleDiff(GeoMath.BearingTo(ac.Position, Point(fix)), ac.TrueTrack.Degrees)) >= 90 - AbeamToleranceDeg;

    private const double AbeamToleranceDeg = 0.5;

    private static LatLon Point(ResolvedFix fix) => new(fix.Lat, fix.Lon);

    private static bool IsWithinOfAirport(AircraftState ac, string airport, double nm)
    {
        if (string.IsNullOrWhiteSpace(airport))
        {
            return false;
        }

        var runways = RunwaysFor(airport);
        return runways.Count > 0 && GeoMath.DistanceNm(ac.Position, new LatLon(runways[0].Lat1, runways[0].Lon1)) <= nm;
    }

    // --- hold / VFR / squawk ---

    /// <summary>
    /// The feed's own hold flag (ERAM <c>airborneHold</c>) is authoritative when set; otherwise a <c>HOLD</c> token in the
    /// clearance or route, or a racetrack signature in the history, stands in for it.
    /// </summary>
    private static bool DetectHold(AircraftState ac, AircraftLiveTraffic lt)
    {
        if ((lt.AirborneHold == true) || MentionsHold(lt.ClearanceText) || MentionsHold(ac.FlightPlan.Route))
        {
            return true;
        }

        var h = lt.History;
        if (h.Count < 3)
        {
            return false;
        }

        double turnSum = 0;
        for (int i = h.Count - 1; i > 0; i--)
        {
            if (h[^1].ObservedAtSimSeconds - h[i - 1].ObservedAtSimSeconds > HoldWindowSeconds)
            {
                break;
            }

            turnSum += Math.Abs(AngleDiff(h[i].TrueTrackDeg, h[i - 1].TrueTrackDeg));
            if (turnSum >= HoldTurnSumDeg)
            {
                var first = new LatLon(h[i - 1].Lat, h[i - 1].Lon);
                return GeoMath.DistanceNm(first, ac.Position) < HoldMaxDisplacementNm;
            }
        }

        return false;
    }

    private static bool MentionsHold(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(t => t.Equals("HOLD", StringComparison.OrdinalIgnoreCase));

    private static bool IsVfr(AircraftState ac) => ac.FlightPlan.IsVfr || (!ac.FlightPlan.HasFlightPlan && ac.Transponder.Code == 1200);

    private static void NoteEmergencySquawk(AircraftState ac, List<string> notes)
    {
        switch (ac.Transponder.Code)
        {
            case 7700:
                notes.Add("squawking 7700 — treat as an emergency (§10-1-1)");
                break;
            case 7600:
                notes.Add("squawking 7600 — lost comms, will not comply with vectors (§10-4-4)");
                break;
        }
    }

    // --- helpers ---

    private static double RoundTo100(double ft) => Math.Round(ft / 100.0) * 100.0;

    private static double RoundUpTo1000(double ft) => Math.Ceiling(ft / 1000.0) * 1000.0;

    private static double RoundDownTo100(double ft) => Math.Floor(ft / 100.0) * 100.0;

    /// <summary>Signed smallest difference a − b in degrees, in (−180, 180].</summary>
    private static double AngleDiff(double a, double b)
    {
        double d = (a - b) % 360.0;
        if (d > 180)
        {
            d -= 360;
        }
        else if (d <= -180)
        {
            d += 360;
        }

        return d;
    }
}
