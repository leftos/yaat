using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Pattern;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Phases;

/// <summary>
/// What the controller told the aircraft that decides whether a circuit may be compressed.
/// </summary>
/// <param name="SizeIsExplicit">True when the size came from a PSIZE command, which skips the compression search.</param>
/// <param name="SpeedKts">The controller's assigned speed, or null when none stands (7110.65 5-7-4).</param>
public readonly record struct PatternCommanded(bool SizeIsExplicit, double? SpeedKts)
{
    /// <summary>
    /// True for a circuit that never compresses on its own: a departure leaving the pattern (CTO MRD/MLD), since only closed
    /// traffic compresses, and the flown runway's upwind of a crossing transition, which takes the compression of the circuit it
    /// joins.
    /// </summary>
    public required bool NeverCompresses { get; init; }

    /// <summary>No PSIZE and no controller speed.</summary>
    public static PatternCommanded None => default;

    /// <summary>The instructions standing on <paramref name="aircraft"/>.</summary>
    public static PatternCommanded Of(AircraftState aircraft) =>
        new(
            aircraft.Pattern.SizeOverrideNm is not null,
            aircraft.Targets.HasExplicitSpeedCommand ? (aircraft.Targets.TargetSpeed ?? aircraft.IndicatedAirspeed) : null
        )
        {
            NeverCompresses = false,
        };
}

/// <summary>The slower speeds and the 25-degree turn rate of a compressed pattern, for one aircraft type.</summary>
/// <param name="DownwindKts">Downwind speed: the normal one, capped at Vref + 20.</param>
/// <param name="BaseKts">Base speed: the normal one, capped at Vref + 10.</param>
/// <param name="TurnRateDegPerSec">The flown rate, 25 degrees of bank at the downwind speed, so no leg banks further.</param>
/// <param name="FloorNm">The narrowest flyable width at these speeds.</param>
public sealed record CompressedLegs(double DownwindKts, double BaseKts, double TurnRateDegPerSec, double FloorNm);

/// <summary>A pattern that crosses another runway and cannot be compressed clear of it.</summary>
/// <param name="PatternRunway">The pattern's runway, in its stored form.</param>
/// <param name="CrossedRunway">The end of the crossed runway, in its stored form.</param>
/// <param name="TightestFlyableNm">The aircraft's compressed floor.</param>
public sealed record PatternRunwayCrossing(string PatternRunway, string CrossedRunway, double TightestFlyableNm);

/// <summary>The pattern a compression decision leaves.</summary>
/// <param name="SizeNm">The pattern width to fly.</param>
/// <param name="Legs">The compressed speeds, or null for a normal pattern.</param>
/// <param name="Crossing">The runway the normal pattern crosses, when it could not be compressed clear.</param>
internal sealed record PatternCompressionResult(double SizeNm, CompressedLegs? Legs, PatternRunwayCrossing? Crossing);

/// <summary>The inputs of the compression search: the circuit's runway, its two anchors and its side.</summary>
internal sealed record PatternCompressionRequest(RunwayInfo Runway, LatLon DepartureEnd, LatLon LandingThreshold, PatternDirection Direction)
{
    public required AircraftCategory Category { get; init; }

    public required string AircraftType { get; init; }

    /// <summary>The wind (kt) the normal circuit's planning speeds carry.</summary>
    public required double WindSpeedKt { get; init; }

    public required CompressedLegs Legs { get; init; }

    public required PatternCommanded Commanded { get; init; }

    public required IReadOnlyList<RunwayInfo>? AirportRunways { get; init; }

    public required ActiveRunways ActiveRunways { get; init; }
}

/// <summary>
/// Shrinks a traffic pattern that would otherwise cross another active runway, at slower speeds and a steeper turn. A type
/// that cannot clear keeps its normal pattern. AIM 4-3-3.b lets a pilot vary pattern size by performance.
/// </summary>
public static class PatternCompression
{
    private const double BankDegrees = 25.0;

    /// <summary>Knots per (deg/s) of the rate-one radius formula.</summary>
    private const double RadiusDivisor = 62.832;

    /// <summary>The crosswind turn point the clearing test assumes, past the departure end (AIM 4-3-2.c.1).</summary>
    public const double TurnPointPastDepartureEndNm = 0.5;

    /// <summary>How far past each end the other runway's centerline is protected (7110.65 3-9-9.b).</summary>
    private const double ProtectedCenterlineNm = 1.0;

    /// <summary>
    /// The narrowest circuit the by-design test tries: about the C152's compressed floor (0.307 nm), the narrowest of any
    /// fixed-wing type.
    /// </summary>
    private const double ByDesignNarrowestNm = 0.30;

    /// <summary>The widest circuit the by-design test tries: about the B738's normal circuit as flown (2.517 nm).</summary>
    private const double ByDesignWidestNm = 2.5;

    private const double SearchStepNm = 0.005;

    private static readonly double BankRateKnots = 1091.0 * Math.Tan(BankDegrees * Math.PI / 180.0);

    /// <summary>Turn rate (deg/s) at 25 degrees of bank, never below the category's pattern rate.</summary>
    public static double TurnRateDegPerSec(double speedKt, AircraftCategory category) =>
        Math.Max(CategoryPerformance.PatternTurnRate(category), BankRateKnots / speedKt);

    /// <summary>Turn radius (nm) at 25 degrees of bank.</summary>
    public static double TurnRadiusNm(double speedKt, AircraftCategory category) => speedKt / (TurnRateDegPerSec(speedKt, category) * RadiusDivisor);

    /// <summary>The compressed speeds, rate and floor of a type; the floor's planning speeds carry the wind.</summary>
    public static CompressedLegs Legs(string aircraftType, AircraftCategory category, double windSpeedKt)
    {
        double vref = AircraftPerformance.ApproachSpeed(aircraftType, category);
        double downwindKt = Math.Min(AircraftPerformance.DownwindSpeed(aircraftType, category), vref + 20.0);
        double baseKt = Math.Min(AircraftPerformance.BaseSpeed(aircraftType, category), vref + 10.0);
        double arrivalNm = TurnRadiusNm(downwindKt + windSpeedKt, category) + TurnRadiusNm(baseKt + windSpeedKt, category);
        double departureNm = 2.0 * TurnRadiusNm(downwindKt + windSpeedKt, category);
        return new CompressedLegs(downwindKt, baseKt, TurnRateDegPerSec(downwindKt, category), Math.Max(arrivalNm, departureNm));
    }

    /// <summary>The narrowest size a PSIZE gets: the compressed floor, unless the controller's speed rules compression out.</summary>
    public static double CommandedFloorNm(string aircraftType, AircraftCategory category, double windSpeedKt, double? commandedSpeedKts)
    {
        double normalNm = PatternGeometry.MinFlyablePatternSizeNm(aircraftType, category, windSpeedKt);
        CompressedLegs legs = Legs(aircraftType, category, windSpeedKt);
        return SpeedAllows(commandedSpeedKts, legs) ? Math.Min(normalNm, legs.FloorNm) : normalNm;
    }

    /// <summary>The speed of <paramref name="waypoints"/>' upwind, crosswind and downwind, and of the crosswind planning radius.</summary>
    public static double DownwindSpeed(PatternWaypoints? waypoints, string aircraftType, AircraftCategory category) =>
        waypoints?.CompressedDownwindSpeedKts ?? AircraftPerformance.DownwindSpeed(aircraftType, category);

    /// <summary>The base speed of <paramref name="waypoints"/>' circuit.</summary>
    public static double BaseSpeed(PatternWaypoints? waypoints, string aircraftType, AircraftCategory category) =>
        waypoints?.CompressedBaseSpeedKts ?? AircraftPerformance.BaseSpeed(aircraftType, category);

    /// <summary>
    /// The climb speed off the runway into <paramref name="waypoints"/>' circuit: held down to the compressed speed, or Vr if
    /// higher.
    /// </summary>
    public static double TakeoffClimbSpeed(PatternWaypoints? waypoints, string aircraftType, AircraftCategory category)
    {
        double climbKt = AircraftPerformance.InitialClimbSpeed(aircraftType, category);
        return waypoints?.CompressedDownwindSpeedKts is { } downwindKt
            ? Math.Min(climbKt, Math.Max(downwindKt, AircraftPerformance.RotationSpeed(aircraftType, category)))
            : climbKt;
    }

    /// <summary>The circuit an aircraft is about to fly: the first pattern leg after its current phase.</summary>
    public static PatternWaypoints? UpcomingCircuit(PhaseList? phases) =>
        phases?.Phases.Skip(phases.CurrentIndex + 1).Select(AirborneFollowHelper.PatternWaypointsOf).FirstOrDefault(w => w is not null);

    /// <summary>The circuit an aircraft has just flown: the last pattern leg before its current phase.</summary>
    public static PatternWaypoints? FlownCircuit(PhaseList? phases) =>
        phases?.Phases.Take(phases.CurrentIndex).Select(AirborneFollowHelper.PatternWaypointsOf).LastOrDefault(w => w is not null);

    /// <summary>
    /// Holds a climb off the runway into or out of a compressed <paramref name="circuit"/> to its capped speed. Called on every
    /// airborne tick, since physics drops a reached target and the auto speed schedule would then accelerate past the cap; the
    /// cap is computed from the circuit each time, never stored. A controller speed wins (7110.65 5-7-4).
    /// </summary>
    public static void HoldClimbCap(PhaseContext ctx, PatternWaypoints? circuit)
    {
        if ((circuit?.CompressedDownwindSpeedKts is null) || ctx.Targets.HasExplicitSpeedCommand)
        {
            return;
        }

        ctx.Targets.TargetSpeed = TakeoffClimbSpeed(circuit, ctx.AircraftType, ctx.Category);
    }

    /// <summary>The turn rate of <paramref name="waypoints"/>' circuit.</summary>
    public static double TurnRate(PatternWaypoints? waypoints, AircraftCategory category) =>
        waypoints?.CompressedTurnRateDegPerSec ?? CategoryPerformance.PatternTurnRate(category);

    /// <summary>The size and speeds of a PSIZE: flown as told down to the compressed floor, slower once under the normal floor.</summary>
    internal static PatternCompressionResult ResolveExplicit(
        double requestedNm,
        double normalFloorNm,
        CompressedLegs legs,
        PatternCommanded commanded
    )
    {
        if (requestedNm >= normalFloorNm)
        {
            return new PatternCompressionResult(requestedNm, null, null);
        }

        return SpeedAllows(commanded.SpeedKts, legs) && (legs.FloorNm < normalFloorNm)
            ? new PatternCompressionResult(Math.Max(requestedNm, legs.FloorNm), legs, null)
            : new PatternCompressionResult(normalFloorNm, null, null);
    }

    /// <summary>
    /// Searches for the widest size that clears every protected runway, then pulls in by the buffer without passing the floor.
    /// A pattern that already clears, a controller speed above the compressed speed, or no clearing size keep the normal pattern.
    /// </summary>
    internal static PatternCompressionResult Resolve(PatternCompressionRequest request, double normalSizeNm)
    {
        var frame = TrackFrame.Of(request);
        List<ProtectedRunway> others = ProtectedRunways(request, frame);
        // A normal crosswind has no turn lead and a normal downwind no rejoin, so the circuit settles one turn radius outside its
        // plan: the crosswind leg that far past the turn point, the downwind that far outside its size.
        AircraftCategory category = request.Category;
        double overshootNm = BasePhase.TurnRadiusNm(
            AircraftPerformance.DownwindSpeed(request.AircraftType, category) + request.WindSpeedKt,
            category
        );
        double normalBaseExtensionNm = CategoryPerformance.BaseExtensionNm(category) * (normalSizeNm / CategoryPerformance.PatternSizeNm(category));
        double flownNormalNm = normalSizeNm + overshootNm;
        var flownNormal = PlannedTrack.At(frame.TurnAlongNm + overshootNm, flownNormalNm, normalBaseExtensionNm);
        if (!others.Any(o => o.Touches(flownNormal)))
        {
            return new PatternCompressionResult(normalSizeNm, null, null);
        }

        CompressedLegs legs = request.Legs;
        if (!SpeedAllows(request.Commanded.SpeedKts, legs) || (ClearSizeNm(request, frame, others, flownNormalNm) is not { } clearNm))
        {
            return Uncompressed(request, normalSizeNm, others, flownNormal);
        }

        // A compressed circuit is never wider than the normal one; a size that does not clear leaves the normal pattern.
        double sizeNm = Math.Min(normalSizeNm, Math.Max(legs.FloorNm, clearNm - PatternGeometry.RunwayBufferNm));
        return Clears(request, frame, others, sizeNm)
            ? new PatternCompressionResult(sizeNm, legs, null)
            : Uncompressed(request, normalSizeNm, others, flownNormal);
    }

    /// <summary>
    /// The normal pattern, with a crossing to warn of when its flown track touches another runway's protected corridor: the
    /// pavement or the extended centerline compression clears against.
    /// </summary>
    private static PatternCompressionResult Uncompressed(
        PatternCompressionRequest request,
        double normalSizeNm,
        List<ProtectedRunway> others,
        PlannedTrack flownNormal
    )
    {
        ProtectedRunway? crossed = others.FirstOrDefault(o => o.Touches(flownNormal));
        PatternRunwayCrossing? crossing = crossed is null
            ? null
            : new PatternRunwayCrossing(request.Runway.Designator, crossed.Designator, request.Legs.FloorNm);
        return new PatternCompressionResult(normalSizeNm, null, crossing);
    }

    private static bool SpeedAllows(double? commandedSpeedKts, CompressedLegs legs) =>
        (commandedSpeedKts is not { } speedKts) || (speedKts <= legs.DownwindKts);

    /// <summary>The widest compressed size that clears, searched down from <paramref name="startNm"/> to the floor.</summary>
    private static double? ClearSizeNm(PatternCompressionRequest request, TrackFrame frame, List<ProtectedRunway> others, double startNm)
    {
        double floorNm = request.Legs.FloorNm;
        for (double sizeNm = startNm; sizeNm > floorNm; sizeNm -= SearchStepNm)
        {
            if (Clears(request, frame, others, sizeNm))
            {
                return sizeNm;
            }
        }

        return (floorNm <= startNm) && Clears(request, frame, others, floorNm) ? floorNm : null;
    }

    /// <summary>A compressed circuit turns crosswind at the turn point and rejoins its downwind, so it flies its planned size.</summary>
    private static bool Clears(PatternCompressionRequest request, TrackFrame frame, List<ProtectedRunway> others, double sizeNm)
    {
        var track = PlannedTrack.At(frame.TurnAlongNm, sizeNm, PatternGeometry.CompressedBaseExtensionNm(request.Category, sizeNm));
        return !others.Any(o => o.Touches(track));
    }

    /// <summary>
    /// The runways a circuit stays clear of: every other active runway that does not cross the pattern runway, less those that
    /// are by design, which no circuit on this side from <see cref="ByDesignNarrowestNm"/> to <see cref="ByDesignWidestNm"/>
    /// wide clears. A by-design runway neither compresses the pattern nor draws the crossing warning: the field's layout makes
    /// overflying it unavoidable, so it is treated as the field's normal flow. This is a design choice, not a rule from the
    /// references.
    /// </summary>
    private static List<ProtectedRunway> ProtectedRunways(PatternCompressionRequest request, TrackFrame frame)
    {
        var result = new List<ProtectedRunway>();
        if (request.AirportRunways is null)
        {
            return result;
        }

        IReadOnlyList<ActiveRunway> active = request.ActiveRunways.For(request.Runway.AirportId);
        foreach (RunwayInfo other in request.AirportRunways)
        {
            if ((other.Id == request.Runway.Id) || PatternGeometry.RunwaysCross(request.Runway, other))
            {
                continue;
            }

            string? end = NamedEnd(other, request.Runway, active);
            if (end is null)
            {
                continue;
            }

            var runway = new ProtectedRunway(end, frame.Project(other.Lat1, other.Lon1), frame.Project(other.Lat2, other.Lon2), other.WidthFt);
            if (SomeCircuitClears(request, frame, runway))
            {
                result.Add(runway);
            }
        }

        return result;
    }

    /// <summary>
    /// True when some circuit from <see cref="ByDesignNarrowestNm"/> to <see cref="ByDesignWidestNm"/> wide, in
    /// <see cref="SearchStepNm"/> steps, clears <paramref name="runway"/>.
    /// </summary>
    private static bool SomeCircuitClears(PatternCompressionRequest request, TrackFrame frame, ProtectedRunway runway)
    {
        List<ProtectedRunway> alone = [runway];
        int steps = (int)Math.Round((ByDesignWidestNm - ByDesignNarrowestNm) / SearchStepNm);
        for (int i = 0; i <= steps; i++)
        {
            double sizeNm = ByDesignNarrowestNm + (i * SearchStepNm);
            if (Clears(request, frame, alone, sizeNm))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The end to name <paramref name="other"/> by, or null when an active list leaves it out.</summary>
    private static string? NamedEnd(RunwayInfo other, RunwayInfo runway, IReadOnlyList<ActiveRunway> active)
    {
        string end1 = RunwayIdentifier.NormalizeDesignator(other.Id.End1);
        string end2 = RunwayIdentifier.NormalizeDesignator(other.Id.End2);
        if (active.Count > 0)
        {
            return active.FirstOrDefault(a => (a.Designator == end1) || (a.Designator == end2))?.Designator;
        }

        double offEnd1 = Math.Abs(GeoMath.SignedBearingDifference(other.TrueHeading1.Degrees, runway.TrueHeading.Degrees));
        double offEnd2 = Math.Abs(GeoMath.SignedBearingDifference(other.TrueHeading2.Degrees, runway.TrueHeading.Degrees));
        return offEnd1 <= offEnd2 ? end1 : end2;
    }

    /// <summary>
    /// True when a flown <paramref name="track"/> touches <paramref name="other"/>'s protected corridor, the one compression
    /// clears against: its pavement, or its centerline extended <see cref="ProtectedCenterlineNm"/> past each end.
    /// </summary>
    public static bool TouchesProtectedRunway(RunwayInfo patternRunway, LatLon landingThreshold, RunwayInfo other, IReadOnlyList<LatLon> track)
    {
        var frame = new TrackFrame(landingThreshold, patternRunway.TrueHeading, 1.0, 0.0);
        var runway = new ProtectedRunway(
            other.Designator,
            frame.Project(other.Lat1, other.Lon1),
            frame.Project(other.Lat2, other.Lon2),
            other.WidthFt
        );
        for (int i = 1; i < track.Count; i++)
        {
            if (runway.TouchesLeg(frame.Project(track[i - 1].Lat, track[i - 1].Lon), frame.Project(track[i].Lat, track[i].Lon)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A position along the pattern runway from the landing threshold, and off it toward the pattern side (nm).</summary>
    private readonly record struct Point(double AlongNm, double OffsetNm);

    /// <summary>The crosswind, downwind and base legs of a circuit, corner to corner.</summary>
    private readonly record struct PlannedTrack(Point Turn, Point DownwindStart, Point BaseTurn, Point BaseEnd)
    {
        /// <summary>
        /// A circuit whose crosswind leg lies <paramref name="crosswindAlongNm"/> along the runway, <paramref name="sizeNm"/> wide.
        /// </summary>
        public static PlannedTrack At(double crosswindAlongNm, double sizeNm, double baseExtensionNm) =>
            new(new(crosswindAlongNm, 0), new(crosswindAlongNm, sizeNm), new(-baseExtensionNm, sizeNm), new(-baseExtensionNm, 0));
    }

    private sealed record TrackFrame(LatLon Origin, TrueHeading Heading, double Side, double TurnAlongNm)
    {
        public static TrackFrame Of(PatternCompressionRequest request)
        {
            TrueHeading heading = request.Runway.TrueHeading;
            double departureEndAlongNm = GeoMath.AlongTrackDistanceNm(request.DepartureEnd, request.LandingThreshold, heading);
            double turnAlongNm = departureEndAlongNm + TurnPointPastDepartureEndNm;
            return new TrackFrame(request.LandingThreshold, heading, request.Direction == PatternDirection.Left ? -1.0 : 1.0, turnAlongNm);
        }

        public Point Project(double lat, double lon)
        {
            var position = new LatLon(lat, lon);
            return new Point(
                GeoMath.AlongTrackDistanceNm(position, Origin, Heading),
                Side * GeoMath.SignedCrossTrackDistanceNm(position, Origin, Heading)
            );
        }
    }

    private sealed record ProtectedRunway(string Designator, Point End1, Point End2, double WidthFt)
    {
        private double LengthNm => Math.Sqrt(Square(End2.AlongNm - End1.AlongNm) + Square(End2.OffsetNm - End1.OffsetNm));

        /// <summary>True when any leg of <paramref name="track"/> touches the pavement or the protected extended centerline.</summary>
        public bool Touches(PlannedTrack track) => AnyLeg(track, OnPavement) || AnyLeg(track, OnExtendedCenterline);

        /// <summary>True when the segment <paramref name="from"/>-<paramref name="to"/> touches the pavement or the protected extended centerline.</summary>
        public bool TouchesLeg(Point from, Point to) => OnPavement(from, to) || OnExtendedCenterline(from, to);

        private static bool AnyLeg(PlannedTrack track, Func<Point, Point, bool> touches) =>
            touches(track.Turn, track.DownwindStart) || touches(track.DownwindStart, track.BaseTurn) || touches(track.BaseTurn, track.BaseEnd);

        private bool OnPavement(Point from, Point to)
        {
            double halfWidthNm = WidthFt / 2.0 / GeoMath.FeetPerNm;
            bool crosses = Intersect(from, to, End1, End2);
            return crosses
                || (DistanceNm(from, End1, End2) <= halfWidthNm)
                || (DistanceNm(to, End1, End2) <= halfWidthNm)
                || (DistanceNm(End1, from, to) <= halfWidthNm)
                || (DistanceNm(End2, from, to) <= halfWidthNm);
        }

        private bool OnExtendedCenterline(Point from, Point to)
        {
            double unitAlong = (End2.AlongNm - End1.AlongNm) / LengthNm;
            double unitOffset = (End2.OffsetNm - End1.OffsetNm) / LengthNm;
            Point before = new(End1.AlongNm - (unitAlong * ProtectedCenterlineNm), End1.OffsetNm - (unitOffset * ProtectedCenterlineNm));
            Point after = new(End2.AlongNm + (unitAlong * ProtectedCenterlineNm), End2.OffsetNm + (unitOffset * ProtectedCenterlineNm));
            return Intersect(from, to, before, after);
        }

        private static bool Intersect(Point a, Point b, Point c, Point d) =>
            PatternGeometry.SegmentsIntersect(a.AlongNm, a.OffsetNm, b.AlongNm, b.OffsetNm, c.AlongNm, c.OffsetNm, d.AlongNm, d.OffsetNm);

        private static double DistanceNm(Point point, Point from, Point to)
        {
            double dx = to.AlongNm - from.AlongNm;
            double dy = to.OffsetNm - from.OffsetNm;
            double lengthSquared = Square(dx) + Square(dy);
            double t = ((((point.AlongNm - from.AlongNm) * dx) + ((point.OffsetNm - from.OffsetNm) * dy)) / lengthSquared);
            double clamped = Math.Clamp(t, 0.0, 1.0);
            return Math.Sqrt(Square(point.AlongNm - from.AlongNm - (clamped * dx)) + Square(point.OffsetNm - from.OffsetNm - (clamped * dy)));
        }

        private static double Square(double value) => value * value;
    }
}
