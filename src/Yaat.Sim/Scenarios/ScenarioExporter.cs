using System.Globalization;
using System.Text.Json;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;

namespace Yaat.Sim.Scenarios;

/// <summary>An exported aircraft the scenario author still has to give preset commands or delete.</summary>
public sealed record ScenarioExportFlag(string Callsign, string Reason);

/// <summary>The scenario an export built and the aircraft in it that need the author's attention.</summary>
public sealed record ScenarioExportResult(Scenario Scenario, IReadOnlyList<ScenarioExportFlag> Flags);

/// <summary>
/// Room-level facts an export stamps on the scenario: the identity the room is running under, the wall-clock time the
/// name carries, and the magnetic-model day the loader will convert the exported magnetic headings back to true with.
/// </summary>
public sealed record ScenarioExportContext(
    string ArtccId,
    string? PrimaryAirportId,
    string? StudentPositionId,
    DateTime ExportedAtUtc,
    DateTime MagneticModelDateUtc
);

/// <summary>
/// Builds a scenario from the aircraft in a room — the mirror of <see cref="ScenarioLoader"/>. Each aircraft becomes a
/// <c>Parking</c>, <c>OnRunway</c>, <c>OnFinal</c> or <c>Coordinates</c> start; one the loader cannot restart in the same situation is
/// flagged with a short reason so the author knows to add presets or delete it. Live-traffic shadows are exported from
/// their feed samples, with the flight plan the caller correlated for them.
/// </summary>
public static class ScenarioExporter
{
    /// <summary>A shadow is on final only within this distance of a destination runway threshold.</summary>
    public const double ShadowFinalMaxDistanceNm = 15.0;

    /// <summary>A shadow is on final only when its true track is within this many degrees of the runway course.</summary>
    public const double ShadowFinalMaxTrackDeviationDeg = 15.0;

    /// <summary>
    /// The widest a shadow may sit either side of the extended runway centreline and still be on final. Nearer in the
    /// limit narrows: <see cref="ShadowFinalCrossTrackAtThresholdNm"/> at the threshold, splaying out at
    /// <see cref="ShadowFinalCrossTrackSplayDeg"/> until it reaches this.
    /// </summary>
    public const double ShadowFinalMaxCrossTrackNm = 1.0;

    /// <summary>The cross-track limit at the threshold itself.</summary>
    public const double ShadowFinalCrossTrackAtThresholdNm = 0.2;

    /// <summary>The half-angle the cross-track limit splays out at with distance from the threshold.</summary>
    public const double ShadowFinalCrossTrackSplayDeg = 5.0;

    /// <summary>A shadow is on final only within this many feet above or below the loader's glidepath at its distance.</summary>
    public const double ShadowFinalGlidepathToleranceFt = 1000.0;

    /// <summary>A shadow climbing faster than this is going around or departing, not on final.</summary>
    public const double ShadowFinalMaxClimbFpm = 300.0;

    /// <summary>
    /// Closer to the threshold than this an aircraft is landing, not on final: an <c>OnFinal</c> start that near
    /// would restart it over the numbers with no approach left to fly.
    /// </summary>
    public const double FinalMinDistanceNm = 0.5;

    /// <summary>A ground aircraft moving faster than this is taxiing, not parked or holding in position.</summary>
    public const double ParkedMaxGroundSpeedKts = 1.0;

    /// <summary>A shadow counts as on a stand within this distance of it: surface surveillance scatters by tens of feet.</summary>
    public const double ShadowStandToleranceFt = 75.0;

    /// <summary>A shadow's filed route is cut to the fixes ahead only when one of its fixes lies within this distance of it.</summary>
    public const double ShadowRouteMaxOffsetNm = 30.0;

    public const string ReasonHoldingShort = "holding short";
    public const string ReasonNotAtStand = "taxiing / not at a stand";
    public const string ReasonAirborneVfr = "airborne VFR";
    public const string ReasonOffRoute = "vectored / off route";
    public const string ReasonNoFlightPlan = "no flight plan";
    public const string ReasonNoFiledRoute = "no filed route";
    public const string ReasonFinalNotDestination = "on final for a runway that is not its destination";
    public const string ReasonArrived = "arrived";
    public const string ReasonInProcedure = "in a procedure / holding";
    public const string ReasonRouteNotTrimmed = "filed route, not trimmed";
    public const string ReasonOffGlidepath = "aligned with final but off the glidepath";
    public const string ReasonOverThreshold = "over the threshold / landing";

    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

    /// <summary>
    /// Exports <paramref name="aircraft"/> as a scenario. <paramref name="shadowFlightPlan"/> supplies the correlated
    /// flight plan of a live-traffic shadow (null when it has none); simulated aircraft export their own plan.
    /// </summary>
    public static ScenarioExportResult Export(
        IEnumerable<AircraftState> aircraft,
        ScenarioExportContext context,
        IAirportGroundData? groundData,
        Func<AircraftState, ScenarioFlightPlan?> shadowFlightPlan
    )
    {
        var flags = new List<ScenarioExportFlag>();
        var exported = new List<ScenarioAircraft>();
        foreach (AircraftState state in aircraft)
        {
            ScenarioFlightPlan? plan = state.IsShadow ? shadowFlightPlan(state) : ToScenarioFlightPlan(state.FlightPlan);
            (ScenarioAircraft ac, string? reason) = ExportAircraft(state, plan, context, groundData);
            exported.Add(ac);
            if (reason is not null)
            {
                flags.Add(new ScenarioExportFlag(state.Callsign, reason));
            }
        }

        string airport = context.PrimaryAirportId ?? "";
        var scenario = new Scenario
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"{airport} snapshot {context.ExportedAtUtc:yyyy-MM-dd HHmm}".Trim(),
            ArtccId = context.ArtccId,
            PrimaryAirportId = context.PrimaryAirportId,
            StudentPositionId = context.StudentPositionId,
            Aircraft = exported,
        };
        return new ScenarioExportResult(scenario, flags);
    }

    /// <summary>Serializes an exported scenario the way <see cref="ScenarioLoader"/> reads it (the model's own property names).</summary>
    public static string Serialize(Scenario scenario) => JsonSerializer.Serialize(scenario, SerializeOptions);

    public static ScenarioFlightPlan? ToScenarioFlightPlan(AircraftFlightPlan fp)
    {
        if (!fp.HasFlightPlan)
        {
            return null;
        }

        return new ScenarioFlightPlan
        {
            Rules = fp.FlightRules,
            Departure = fp.Departure,
            Destination = fp.Destination,
            CruiseAltitude = fp.Altitude.CruiseFeet ?? 0,
            CruiseSpeed = fp.CruiseSpeed,
            Route = fp.Route,
            Remarks = fp.Remarks,
            AircraftType = fp.AircraftType,
        };
    }

    private readonly record struct Pose
    {
        public required LatLon Position { get; init; }
        public required double AltitudeFt { get; init; }

        /// <summary>Indicated airspeed, the speed a scenario start carries.</summary>
        public required double SpeedKts { get; init; }
        public required double GroundSpeedKts { get; init; }
        public required double TrueHeadingDeg { get; init; }

        /// <summary>The direction over the ground, which the final-approach alignment test reads.</summary>
        public required double TrueTrackDeg { get; init; }
        public required double VerticalSpeedFpm { get; init; }
    }

    private sealed record FinalFix(RunwayInfo Runway, double DistanceNm, bool OnGlidepath);

    private static (ScenarioAircraft Aircraft, string? Reason) ExportAircraft(
        AircraftState state,
        ScenarioFlightPlan? plan,
        ScenarioExportContext context,
        IAirportGroundData? groundData
    )
    {
        Pose pose = PoseOf(state);
        var ac = new ScenarioAircraft
        {
            Id = Guid.NewGuid().ToString("N"),
            AircraftId = state.Callsign,
            AircraftType = state.AircraftType,
            TransponderMode = state.Transponder.Mode,
            FlightPlan = plan,
            AirportId = string.IsNullOrEmpty(state.AirportId) ? null : state.AirportId,
        };

        if (!state.IsOnGround)
        {
            string? airborneReason = ExportAirborne(ac, state, pose, plan, context);
            if (!state.IsShadow && (ac.StartingConditions.Type == "Coordinates"))
            {
                ac.PresetCommands = PresetsOf(state);
            }

            return (ac, airborneReason);
        }

        AirportGroundLayout? layout = state.Ground.Layout ?? FindLayout(state, plan, context, groundData);
        return (ac, ExportGround(ac, state, pose, layout, context));
    }

    /// <summary>
    /// Where the aircraft is and how it is moving. A shadow's position, altitude, track and vertical speed come from its
    /// feed sample; its heading and IAS are the air vector the live kinematics derived from that sample and the wind —
    /// the heading and speed a <c>Coordinates</c> start takes, since the loader sets IAS and heading, not ground vector.
    /// </summary>
    private static Pose PoseOf(AircraftState state)
    {
        if (state.LiveTraffic is { } live)
        {
            return new Pose
            {
                Position = live.SamplePosition,
                AltitudeFt = live.SampleAltitude,
                SpeedKts = state.IndicatedAirspeed,
                GroundSpeedKts = live.SampleGroundSpeed,
                TrueHeadingDeg = state.TrueHeading.Degrees,
                TrueTrackDeg = live.SampleTrueTrack,
                VerticalSpeedFpm = live.SampleVerticalSpeed,
            };
        }

        return new Pose
        {
            Position = state.Position,
            AltitudeFt = state.Altitude,
            SpeedKts = state.IndicatedAirspeed,
            GroundSpeedKts = state.GroundSpeed,
            TrueHeadingDeg = state.TrueHeading.Degrees,
            TrueTrackDeg = state.TrueTrack.Degrees,
            VerticalSpeedFpm = state.VerticalSpeed,
        };
    }

    private static string? ExportGround(
        ScenarioAircraft ac,
        AircraftState state,
        Pose pose,
        AirportGroundLayout? layout,
        ScenarioExportContext context
    )
    {
        if (layout is not null)
        {
            ac.AirportId = layout.AirportId;
        }

        if (LinedUpRunway(state, pose) is { } runway)
        {
            ac.AirportId = runway.AirportId;
            ac.StartingConditions = new StartingConditions { Type = "OnRunway", Runway = runway.Designator };
            return null;
        }

        GroundNode? stand = (layout is not null) && (pose.GroundSpeedKts < ParkedMaxGroundSpeedKts) ? FindStand(layout, pose.Position, state) : null;
        if (stand?.Name is { } standName)
        {
            ac.StartingConditions = new StartingConditions { Type = "Parking", Parking = standName };
            bool atDestination = (ac.FlightPlan is { } plan) && SameAirport(layout!.AirportId, plan.Destination);
            return atDestination ? ReasonArrived : null;
        }

        // Altitude and speed stay unset: that is how the loader recognises a ground Coordinates start and snaps it
        // onto the nearest taxi edge, using the heading as the edge-direction tiebreaker.
        ac.StartingConditions = new StartingConditions
        {
            Type = "Coordinates",
            Coordinates = ToCoordinates(pose.Position),
            Heading = MagneticHeading(pose, context),
        };
        return state.Phases?.CurrentPhase is HoldingShortPhase ? ReasonHoldingShort : ReasonNotAtStand;
    }

    private static string? ExportAirborne(
        ScenarioAircraft ac,
        AircraftState state,
        Pose pose,
        ScenarioFlightPlan? plan,
        ScenarioExportContext context
    )
    {
        ac.StartingConditions = AirborneCoordinates(pose, context);
        FinalFix? final = state.IsShadow ? FindGeometricFinal(state, pose, plan?.Destination) : FindSimulatedFinal(state);
        if (final is not null)
        {
            return ExportFinal(ac, pose, plan, final);
        }

        if (plan is null)
        {
            return ReasonNoFlightPlan;
        }

        if (string.Equals(plan.Rules, "VFR", StringComparison.OrdinalIgnoreCase))
        {
            return ReasonAirborneVfr;
        }

        return ExportRouteOrFlag(ac, state, pose, plan);
    }

    /// <summary>
    /// The route an airborne IFR aircraft restarts on, or the reason it cannot: a simulated aircraft in a phase that is
    /// not its final (a hold, a procedure turn, an approach it is still flying onto) or steered by vectors rather than a
    /// route; a shadow with no filed route, or one its position could not be placed on.
    /// </summary>
    private static string? ExportRouteOrFlag(ScenarioAircraft ac, AircraftState state, Pose pose, ScenarioFlightPlan plan)
    {
        if (state.Phases?.CurrentPhase is not null and not (FinalApproachPhase or LandingPhase or HelicopterLandingPhase))
        {
            return ReasonInProcedure;
        }

        if (!state.IsShadow)
        {
            string? remaining = RemainingRoute(state);
            if (remaining is null)
            {
                return ReasonOffRoute;
            }

            ac.StartingConditions.NavigationPath = remaining;
            return null;
        }

        if (string.IsNullOrWhiteSpace(plan.Route))
        {
            return ReasonNoFiledRoute;
        }

        string? trimmed = RouteAhead(plan.Route, pose);
        ac.StartingConditions.NavigationPath = trimmed ?? plan.Route.Trim();
        return trimmed is null ? ReasonRouteNotTrimmed : null;
    }

    /// <summary>
    /// A shadow's filed route cut to the fixes still ahead of it: the filed fix nearest the aircraft marks where it is
    /// along the route, and that fix is kept when it lies ahead of the aircraft's track (within 90°) and dropped with
    /// everything before it when it is behind. A non-fix token the cut leaves leading the route (the airway or
    /// <c>DCT</c> the aircraft is already on) goes with it. Null when the position cannot be placed on the route: no
    /// filed token resolves to a point, the nearest is more than <see cref="ShadowRouteMaxOffsetNm"/> away, or every
    /// fix is already behind.
    /// </summary>
    private static string? RouteAhead(string route, Pose pose)
    {
        string[] tokens = route.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        NavigationDatabase navDb = NavigationDatabase.Instance;
        LatLon?[] points = [.. tokens.Select(token => navDb.ResolveFixOrFrd(token) is { } p ? new LatLon(p.Lat, p.Lon) : (LatLon?)null)];

        int nearest = -1;
        double nearestNm = double.MaxValue;
        for (int i = 0; i < points.Length; i++)
        {
            if ((points[i] is { } point) && (GeoMath.DistanceNm(pose.Position, point) < nearestNm))
            {
                nearest = i;
                nearestNm = GeoMath.DistanceNm(pose.Position, point);
            }
        }

        if ((nearest < 0) || (nearestNm > ShadowRouteMaxOffsetNm))
        {
            return null;
        }

        double bearingDeg = GeoMath.BearingTo(pose.Position, points[nearest]!.Value);
        bool ahead = new TrueHeading(bearingDeg).AbsAngleTo(new TrueHeading(pose.TrueTrackDeg)) <= 90.0;
        int first = ahead ? nearest : nearest + 1;
        while ((first < tokens.Length) && (points[first] is null))
        {
            first++;
        }

        return first < tokens.Length ? string.Join(' ', tokens[first..]) : null;
    }

    /// <summary>
    /// The presets that give a reloaded aircraft back the targets it was flying to: a descend-via or climb-via with its
    /// floor or ceiling, otherwise a climb or descent to an assigned altitude it has not reached, then an assigned speed
    /// with its floor or ceiling suffix. Altitudes are written in hundreds of feet, the form the altitude parser reads.
    /// </summary>
    private static List<PresetCommand> PresetsOf(AircraftState state)
    {
        var commands = new List<string>();
        AircraftProcedure procedure = state.Procedure;
        ControlTargets targets = state.Targets;
        if (procedure.StarViaMode)
        {
            commands.Add(procedure.StarViaFloor is { } floor ? $"DVIA {Hundreds(floor)}" : "DVIA");
        }
        else if (procedure.SidViaMode)
        {
            commands.Add(procedure.SidViaCeiling is { } ceiling ? $"CVIA {Hundreds(ceiling)}" : "CVIA");
        }
        else if ((targets.AssignedAltitude is { } altitude) && (Math.Abs(altitude - state.Altitude) > FlightPhysics.AltitudeSnapFt))
        {
            commands.Add($"{(altitude > state.Altitude ? "CM" : "DM")} {Hundreds(altitude)}");
        }

        if (targets.AssignedSpeed is { } speed)
        {
            string suffix =
                (targets.SpeedFloor == speed) ? "+"
                : (targets.SpeedCeiling == speed) ? "-"
                : "";
            commands.Add(FormattableString.Invariant($"SPD {Math.Round(speed):0}{suffix}"));
        }

        return [.. commands.Select(command => new PresetCommand { Id = Guid.NewGuid().ToString("N"), Command = command })];
    }

    private static string Hundreds(double altitudeFt) => ((int)Math.Round(altitudeFt / 100.0)).ToString("D3", CultureInfo.InvariantCulture);

    /// <summary>
    /// An <c>OnFinal</c> start for an aircraft on final, or the reason it stays a <c>Coordinates</c> start: a final to
    /// somewhere other than its filed destination (an aircraft with no plan — a pattern final — has none to compare),
    /// too close in to restart as a final, or aligned with the runway but off the glidepath.
    /// </summary>
    private static string? ExportFinal(ScenarioAircraft ac, Pose pose, ScenarioFlightPlan? plan, FinalFix final)
    {
        if ((plan is not null) && !SameAirport(final.Runway.AirportId, plan.Destination))
        {
            return ReasonFinalNotDestination;
        }

        if (final.DistanceNm < FinalMinDistanceNm)
        {
            return ReasonOverThreshold;
        }

        if (!final.OnGlidepath)
        {
            return ReasonOffGlidepath;
        }

        // The loader places an OnFinal start at the runway of ac.AirportId, falling back to the filed departure.
        ac.AirportId = final.Runway.AirportId;
        ac.StartingConditions = new StartingConditions
        {
            Type = "OnFinal",
            Runway = final.Runway.Designator,
            DistanceFromRunway = Math.Round(final.DistanceNm, 3),
            Speed = Math.Round(pose.SpeedKts),
        };
        return null;
    }

    private static StartingConditions AirborneCoordinates(Pose pose, ScenarioExportContext context) =>
        new()
        {
            Type = "Coordinates",
            Coordinates = ToCoordinates(pose.Position),
            Altitude = Math.Round(pose.AltitudeFt),
            Speed = Math.Round(pose.SpeedKts),
            Heading = MagneticHeading(pose, context),
        };

    private static ScenarioCoordinates ToCoordinates(LatLon position) =>
        new() { Lat = Math.Round(position.Lat, 7), Lon = Math.Round(position.Lon, 7) };

    private static double MagneticHeading(Pose pose, ScenarioExportContext context)
    {
        double magnetic = MagneticDeclination.TrueToMagnetic(pose.TrueHeadingDeg, pose.Position, context.MagneticModelDateUtc);
        return Math.Round(((magnetic % 360.0) + 360.0) % 360.0, 1);
    }

    /// <summary>
    /// The fixes still ahead on a simulated aircraft's route, or null when it is not following one: no route left, or an
    /// assigned heading (vectors) is steering it instead.
    /// </summary>
    private static string? RemainingRoute(AircraftState state)
    {
        if ((state.Targets.NavigationRoute.Count == 0) || (state.Targets.AssignedMagneticHeading is not null))
        {
            return null;
        }

        return string.Join(' ', state.Targets.NavigationRoute.Select(target => target.Name));
    }

    private static FinalFix? FindSimulatedFinal(AircraftState state)
    {
        PhaseList? phases = state.Phases;
        if ((phases?.AssignedRunway is not { } runway) || (phases.CurrentPhase is not (FinalApproachPhase or LandingPhase or HelicopterLandingPhase)))
        {
            return null;
        }

        // A simulated aircraft's phases own its glidepath, so only the distance can rule the final out.
        return new FinalFix(runway, LoaderFinalDistanceNm(state.Position, runway), OnGlidepath: true);
    }

    /// <summary>
    /// A shadow has no phases, so final is read off its geometry against every runway end at its destination: close
    /// to the threshold, on the approach side of it, inside the cross-track limit that narrows toward the threshold,
    /// and tracking the runway course. The closest-to-centreline match wins; it is on the glidepath when it is within
    /// <see cref="ShadowFinalGlidepathToleranceFt"/> of the loader's glidepath and not climbing.
    /// </summary>
    private static FinalFix? FindGeometricFinal(AircraftState state, Pose pose, string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return null;
        }

        NavigationDatabase navDb = NavigationDatabase.Instance;
        IReadOnlyList<RunwayInfo> pavements = navDb.GetRunways(NavigationDatabase.NormalizeAirport(destination));
        if (pavements.Count == 0)
        {
            pavements = navDb.GetRunways(destination);
        }

        RunwayInfo? best = null;
        double bestCrossNm = double.MaxValue;
        foreach (RunwayInfo pavement in pavements)
        {
            foreach (string end in new[] { pavement.Id.End1, pavement.Id.End2 })
            {
                RunwayInfo runway = pavement.ForApproach(end);
                double? crossNm = FinalCrossTrackNm(pose, runway);
                if ((crossNm is { } cross) && (cross < bestCrossNm))
                {
                    bestCrossNm = cross;
                    best = runway;
                }
            }
        }

        if (best is null)
        {
            return null;
        }

        double distanceNm = LoaderFinalDistanceNm(pose.Position, best);
        AircraftCategory category = AircraftCategorization.Categorize(state.AircraftType);
        double glidepathFt = GlideSlopeGeometry.AltitudeAtDistance(distanceNm, best.ElevationFt, category);
        bool onGlidepath =
            (Math.Abs(pose.AltitudeFt - glidepathFt) <= ShadowFinalGlidepathToleranceFt) && (pose.VerticalSpeedFpm <= ShadowFinalMaxClimbFpm);
        return new FinalFix(best, distanceNm, onGlidepath);
    }

    /// <summary>
    /// The shadow's distance off <paramref name="runway"/>'s extended centreline when it is aligned with that final —
    /// laterally and in track, whatever its height — else null.
    /// </summary>
    private static double? FinalCrossTrackNm(Pose pose, RunwayInfo runway)
    {
        LatLon threshold = ThresholdOf(runway);
        if (GeoMath.DistanceNm(pose.Position, threshold) > ShadowFinalMaxDistanceNm)
        {
            return null;
        }

        TrueHeading outbound = runway.TrueHeading.ToReciprocal();
        double alongNm = GeoMath.AlongTrackDistanceNm(pose.Position, threshold, outbound);
        double crossNm = Math.Abs(GeoMath.SignedCrossTrackDistanceNm(pose.Position, threshold, outbound));
        double trackDeviationDeg = new TrueHeading(pose.TrueTrackDeg).AbsAngleTo(runway.TrueHeading);
        double crossLimitNm = Math.Min(
            ShadowFinalMaxCrossTrackNm,
            ShadowFinalCrossTrackAtThresholdNm + (alongNm * Math.Tan(ShadowFinalCrossTrackSplayDeg * Math.PI / 180.0))
        );

        bool aligned = (alongNm > 0.0) && (crossNm <= crossLimitNm) && (trackDeviationDeg <= ShadowFinalMaxTrackDeviationDeg);
        return aligned ? crossNm : null;
    }

    private static LatLon ThresholdOf(RunwayInfo runway) => new(runway.ThresholdLatitude, runway.ThresholdLongitude);

    /// <summary>
    /// Distance out from the threshold along the extended centreline, in the flat-earth metric
    /// <see cref="AircraftInitializer.FinalApproachPoint"/> places an <c>OnFinal</c> start with — its exact inverse, so a
    /// reloaded aircraft lands back where it was rather than a few dozen feet along the final.
    /// </summary>
    private static double LoaderFinalDistanceNm(LatLon position, RunwayInfo runway)
    {
        const double nmPerDegLat = 60.0;
        double reciprocalRad = runway.TrueHeading.ToReciprocal().ToRadians();
        double latRad = runway.ThresholdLatitude * Math.PI / 180.0;
        double northNm = (position.Lat - runway.ThresholdLatitude) * nmPerDegLat;
        double eastNm = (position.Lon - runway.ThresholdLongitude) * nmPerDegLat * Math.Cos(latRad);
        return (northNm * Math.Cos(reciprocalRad)) + (eastNm * Math.Sin(reciprocalRad));
    }

    private static bool SameAirport(string a, string b) =>
        !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && NavigationDatabase.NormalizeAirport(a).Equals(NavigationDatabase.NormalizeAirport(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The layout under a ground aircraft that carries none of its own (a shadow): the airport the loader would place a
    /// ground start at — the aircraft's airport, then its filed departure and destination, then the room's primary airport.
    /// </summary>
    private static AirportGroundLayout? FindLayout(
        AircraftState state,
        ScenarioFlightPlan? plan,
        ScenarioExportContext context,
        IAirportGroundData? groundData
    )
    {
        if (groundData is null)
        {
            return null;
        }

        string?[] candidates = [state.AirportId, plan?.Departure, plan?.Destination, context.PrimaryAirportId];
        foreach (string? airportId in candidates)
        {
            if (!string.IsNullOrWhiteSpace(airportId) && (groundData.GetLayout(airportId) is { } layout))
            {
                return layout;
            }
        }

        return null;
    }

    /// <summary>
    /// The runway a simulated aircraft is lined up and waiting on, stationary — the situation an <c>OnRunway</c> start
    /// restarts — or null. A shadow has no phases to say it holds a line-up clearance.
    /// </summary>
    private static RunwayInfo? LinedUpRunway(AircraftState state, Pose pose)
    {
        PhaseList? phases = state.Phases;
        bool linedUp = (phases?.CurrentPhase is LinedUpAndWaitingPhase) && (pose.GroundSpeedKts < ParkedMaxGroundSpeedKts);
        return linedUp ? phases!.AssignedRunway : null;
    }

    /// <summary>
    /// The named stand or helipad the aircraft sits on, or null. A simulated aircraft must be within
    /// <see cref="AirportGroundLayout.AtNodeToleranceFt"/>, the tolerance the taxi planner uses to treat an aircraft as at
    /// its parking node; a shadow within <see cref="ShadowStandToleranceFt"/>, since its feed position scatters.
    /// </summary>
    private static GroundNode? FindStand(AirportGroundLayout layout, LatLon position, AircraftState state)
    {
        double toleranceFt = state.IsShadow ? ShadowStandToleranceFt : AirportGroundLayout.AtNodeToleranceFt;
        double toleranceNm = toleranceFt / GeoMath.FeetPerNm;
        GroundNode? best = null;
        double bestNm = double.MaxValue;
        foreach (GroundNode node in layout.Nodes.Values)
        {
            if ((node.Type is not (GroundNodeType.Parking or GroundNodeType.Helipad)) || string.IsNullOrEmpty(node.Name))
            {
                continue;
            }

            double distNm = GeoMath.DistanceNm(position, node.Position);
            if ((distNm <= toleranceNm) && (distNm < bestNm))
            {
                best = node;
                bestNm = distNm;
            }
        }

        return best;
    }
}
