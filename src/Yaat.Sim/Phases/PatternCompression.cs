using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Phases;

/// <summary>
/// What the controller told the aircraft that decides whether a circuit may be compressed.
/// </summary>
/// <param name="SizeIsExplicit">True when the size came from a PSIZE command, which skips the compression search.</param>
/// <param name="SpeedKts">The controller's assigned speed, or null when none stands (7110.65 5-7-4).</param>
public readonly record struct PatternCommanded(bool SizeIsExplicit, double? SpeedKts)
{
    /// <summary>No PSIZE and no controller speed.</summary>
    public static PatternCommanded None => default;

    /// <summary>The instructions standing on <paramref name="aircraft"/>.</summary>
    public static PatternCommanded Of(AircraftState aircraft) =>
        new(
            aircraft.Pattern.SizeOverrideNm is not null,
            aircraft.Targets.HasExplicitSpeedCommand ? aircraft.Targets.TargetSpeed ?? aircraft.IndicatedAirspeed : null
        );
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

/// <summary>The inputs of the compression search.</summary>
internal sealed record PatternCompressionRequest(
    RunwayInfo Runway,
    LatLon DepartureEnd,
    LatLon LandingThreshold,
    PatternDirection Direction,
    double NormalSizeNm,
    double NormalBaseExtensionNm,
    double NormalCrosswindOvershootNm,
    AircraftCategory Category,
    CompressedLegs Legs,
    PatternCommanded Commanded,
    IReadOnlyList<RunwayInfo>? AirportRunways,
    ActiveRunways ActiveRunways
);

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
    private const double TurnPointPastDepartureEndNm = 0.5;

    /// <summary>How far past each end the other runway's centerline is protected (7110.65 3-9-9.b).</summary>
    private const double ProtectedCenterlineNm = 1.0;

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

    /// <summary>The climb speed off the runway into <paramref name="waypoints"/>' circuit: held down to the compressed speed, or Vr if higher.</summary>
    public static double TakeoffClimbSpeed(PatternWaypoints? waypoints, string aircraftType, AircraftCategory category)
    {
        double climbKt = AircraftPerformance.InitialClimbSpeed(aircraftType, category);
        return waypoints?.CompressedDownwindSpeedKts is { } downwindKt
            ? Math.Min(climbKt, Math.Max(downwindKt, AircraftPerformance.RotationSpeed(aircraftType, category)))
            : climbKt;
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
    internal static PatternCompressionResult Resolve(PatternCompressionRequest request)
    {
        var normal = new PatternCompressionResult(request.NormalSizeNm, null, null);
        var frame = TrackFrame.Of(request);
        List<ProtectedRunway> others = ProtectedRunways(request, frame);
        // A normal crosswind has no turn lead and a normal downwind no rejoin, so it settles one turn radius outside its size.
        double flownNormalNm = request.NormalSizeNm + request.NormalCrosswindOvershootNm;
        ProtectedRunway? crossed = FirstContact(frame, flownNormalNm, request.NormalBaseExtensionNm, others);
        if (crossed is null)
        {
            return normal;
        }

        CompressedLegs legs = request.Legs;
        var crossing = new PatternRunwayCrossing(request.Runway.Designator, crossed.Designator, legs.FloorNm);
        if (!SpeedAllows(request.Commanded.SpeedKts, legs) || (ClearSizeNm(request, frame, others) is not { } clearNm))
        {
            return normal with { Crossing = crossing };
        }

        double sizeNm = Math.Max(legs.FloorNm, clearNm - PatternGeometry.RunwayBufferNm);
        return new PatternCompressionResult(Clears(request, frame, others, sizeNm) ? sizeNm : clearNm, legs, null);
    }

    private static bool SpeedAllows(double? commandedSpeedKts, CompressedLegs legs) =>
        (commandedSpeedKts is not { } speedKts) || (speedKts <= legs.DownwindKts);

    private static double? ClearSizeNm(PatternCompressionRequest request, TrackFrame frame, List<ProtectedRunway> others)
    {
        double floorNm = request.Legs.FloorNm;
        for (double sizeNm = request.NormalSizeNm; sizeNm > floorNm; sizeNm -= SearchStepNm)
        {
            if (Clears(request, frame, others, sizeNm))
            {
                return sizeNm;
            }
        }

        return (floorNm <= request.NormalSizeNm) && Clears(request, frame, others, floorNm) ? floorNm : null;
    }

    private static bool Clears(PatternCompressionRequest request, TrackFrame frame, List<ProtectedRunway> others, double sizeNm)
    {
        double baseExtensionNm = PatternGeometry.CompressedBaseExtensionNm(request.Category, sizeNm);
        return FirstContact(frame, sizeNm, baseExtensionNm, others) is null;
    }

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
            if (end is not null)
            {
                result.Add(new ProtectedRunway(end, frame.Project(other.Lat1, other.Lon1), frame.Project(other.Lat2, other.Lon2), other.WidthFt));
            }
        }

        return result;
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

    private static ProtectedRunway? FirstContact(TrackFrame frame, double sizeNm, double baseExtensionNm, List<ProtectedRunway> others)
    {
        Point turn = new(frame.TurnAlongNm, 0);
        Point downwindStart = new(frame.TurnAlongNm, sizeNm);
        Point baseTurn = new(-baseExtensionNm, sizeNm);
        Point baseEnd = new(-baseExtensionNm, 0);
        return others.FirstOrDefault(o => o.Touches(turn, downwindStart) || o.Touches(downwindStart, baseTurn) || o.Touches(baseTurn, baseEnd));
    }

    /// <summary>A position along the pattern runway from the landing threshold, and off it toward the pattern side (nm).</summary>
    private readonly record struct Point(double AlongNm, double OffsetNm);

    private sealed record TrackFrame(LatLon Origin, TrueHeading Heading, double Side, double TurnAlongNm)
    {
        public static TrackFrame Of(PatternCompressionRequest request)
        {
            TrueHeading heading = request.Runway.TrueHeading;
            double turnAlongNm = GeoMath.AlongTrackDistanceNm(request.DepartureEnd, request.LandingThreshold, heading) + TurnPointPastDepartureEndNm;
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

        public bool Touches(Point from, Point to) => OnPavement(from, to) || OnExtendedCenterline(from, to);

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
