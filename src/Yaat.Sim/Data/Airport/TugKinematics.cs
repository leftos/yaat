using Microsoft.Extensions.Logging;
using Yaat.Sim.Data.Faa;

namespace Yaat.Sim.Data.Airport;

/// <summary>Which way the tug moves the aircraft over one leg of a ramp reposition.</summary>
public enum PushbackLegKind
{
    /// <summary>Tail-first: the tug reverses the aircraft.</summary>
    Push,

    /// <summary>Nose-first: the tug tows the aircraft forward.</summary>
    Pull,
}

/// <summary>
/// Where a tug-moved aircraft is: the reference point that travels along the aircraft's own axis, and the way the
/// nose points. The heading is normalised to [0, 360) on construction and on every <c>with</c>.
/// </summary>
/// <param name="Position">The aircraft reference point.</param>
/// <param name="NoseTrueDeg">The nose heading, degrees true.</param>
public readonly record struct TugPose(LatLon Position, double NoseTrueDeg)
{
    private readonly double _noseTrueDeg = new TrueHeading(NoseTrueDeg).Degrees;

    /// <summary>The nose heading, degrees true, in [0, 360).</summary>
    public double NoseTrueDeg
    {
        get => _noseTrueDeg;
        init => _noseTrueDeg = new TrueHeading(value).Degrees;
    }

    /// <summary>
    /// The direction the reference point travels on a move of <paramref name="kind"/>.
    /// </summary>
    /// <param name="kind">Push travels tail-first, pull travels nose-first.</param>
    /// <returns>Degrees true: the nose's reciprocal on a push, the nose on a pull.</returns>
    public double TravelTrueDeg(PushbackLegKind kind) => TugKinematics.FlipForKind(NoseTrueDeg, kind);
}

/// <summary>The path a <see cref="TugMove"/> steers along.</summary>
public enum TugMoveShape
{
    /// <summary>Along the direction of travel the move started with, for <see cref="TugMove.StraightDistanceFt"/>.</summary>
    Straight,

    /// <summary>Pursues <see cref="TugMove.Point"/> and ends within 1 ft of it.</summary>
    ToPoint,

    /// <summary>
    /// Captures and follows the line through <see cref="TugMove.Point"/> along <see cref="TugMove.LineTravelTrueDeg"/>,
    /// ending at <see cref="TugMove.StopAt"/> or, when that is null, at capture.
    /// </summary>
    ViaLine,

    /// <summary>A constant-radius arc until the nose is on <see cref="TugMove.FacingTrueDeg"/>.</summary>
    TurnTo,
}

/// <summary>
/// One continuous push or pull. Built only through the shape factories; the flags (<see cref="Tight"/>,
/// <see cref="Creep"/>, <see cref="DwellBefore"/>) are set with <c>with</c>.
/// </summary>
public sealed record TugMove
{
    private TugMove() { }

    /// <summary>Push (tail-first) or pull (nose-first).</summary>
    public required PushbackLegKind Kind { get; init; }

    /// <summary>The path the move steers along.</summary>
    public required TugMoveShape Shape { get; init; }

    /// <summary><see cref="TugMoveShape.Straight"/>: how far to travel, feet.</summary>
    public double StraightDistanceFt { get; init; }

    /// <summary><see cref="TugMoveShape.ToPoint"/>: the target. <see cref="TugMoveShape.ViaLine"/>: a point on the line.</summary>
    public LatLon Point { get; init; }

    /// <summary><see cref="TugMoveShape.ViaLine"/>: the direction of travel along the line, degrees true.</summary>
    public double LineTravelTrueDeg { get; init; }

    /// <summary>
    /// <see cref="TugMoveShape.ViaLine"/>: where along the line to stop; null stops as soon as the line is
    /// captured (a "floating" stop).
    /// </summary>
    public LatLon? StopAt { get; init; }

    /// <summary><see cref="TugMoveShape.TurnTo"/>: the nose heading to end on, degrees true.</summary>
    public double FacingTrueDeg { get; init; }

    /// <summary>Steer on the type's tight radius instead of its routine one.</summary>
    public bool Tight { get; init; }

    /// <summary>The tug slows to walking-alignment speed for this move.</summary>
    public bool Creep { get; init; }

    /// <summary>The move reverses the previous one, so the aircraft dwells stopped before it starts.</summary>
    public bool DwellBefore { get; init; }

    /// <summary>A move along the direction of travel it starts with.</summary>
    /// <param name="kind">Push or pull.</param>
    /// <param name="distanceFt">How far to travel, feet.</param>
    /// <returns>The move.</returns>
    public static TugMove Straight(PushbackLegKind kind, double distanceFt) =>
        new()
        {
            Kind = kind,
            Shape = TugMoveShape.Straight,
            StraightDistanceFt = distanceFt,
        };

    /// <summary>A move that pursues a point.</summary>
    /// <param name="kind">Push or pull.</param>
    /// <param name="target">The point to reach.</param>
    /// <returns>The move.</returns>
    public static TugMove ToPoint(PushbackLegKind kind, LatLon target) =>
        new()
        {
            Kind = kind,
            Shape = TugMoveShape.ToPoint,
            Point = target,
        };

    /// <summary>A move that captures and follows a line.</summary>
    /// <param name="kind">Push or pull.</param>
    /// <param name="linePoint">A point on the line.</param>
    /// <param name="lineTravelTrueDeg">The direction of travel along the line, degrees true.</param>
    /// <param name="stopAt">Where along the line to stop, or null to stop once the line is captured.</param>
    /// <returns>The move.</returns>
    public static TugMove ViaLine(PushbackLegKind kind, LatLon linePoint, double lineTravelTrueDeg, LatLon? stopAt) =>
        new()
        {
            Kind = kind,
            Shape = TugMoveShape.ViaLine,
            Point = linePoint,
            LineTravelTrueDeg = new TrueHeading(lineTravelTrueDeg).Degrees,
            StopAt = stopAt,
        };

    /// <summary>A constant-radius turn onto a nose heading.</summary>
    /// <param name="kind">Push or pull.</param>
    /// <param name="facingTrueDeg">The nose heading to end on, degrees true.</param>
    /// <returns>The move.</returns>
    public static TugMove TurnTo(PushbackLegKind kind, double facingTrueDeg) =>
        new()
        {
            Kind = kind,
            Shape = TugMoveShape.TurnTo,
            FacingTrueDeg = new TrueHeading(facingTrueDeg).Degrees,
        };
}

/// <summary>What a move has done so far.</summary>
/// <param name="Start">Where the move started.</param>
/// <param name="StartTravelTrueDeg">The direction of travel the move started with, degrees true.</param>
/// <param name="DistanceFt">Distance travelled, feet.</param>
/// <param name="Captured">A <see cref="TugMoveShape.ViaLine"/> move has captured its line (latched).</param>
/// <param name="MaxTravelDeviationDeg">The largest angle the direction of travel has been from <paramref name="StartTravelTrueDeg"/>.</param>
public readonly record struct TugMoveProgress(LatLon Start, double StartTravelTrueDeg, double DistanceFt, bool Captured, double MaxTravelDeviationDeg)
{
    /// <summary>The progress of a move that has not yet moved.</summary>
    /// <param name="pose">The pose the move starts from.</param>
    /// <param name="move">The move.</param>
    /// <returns>Zero distance, not captured, no deviation.</returns>
    public static TugMoveProgress Begin(TugPose pose, TugMove move) => new(pose.Position, pose.TravelTrueDeg(move.Kind), 0.0, false, 0.0);
}

/// <summary>One move as <see cref="TugKinematics.Simulate"/> flew it.</summary>
public sealed record TugMoveTrace
{
    /// <summary>The move.</summary>
    public required TugMove Move { get; init; }

    /// <summary>
    /// The start pose, one pose about every 5 ft of travel, then the end pose (never repeated). Empty for a move
    /// skipped because an earlier one was unflyable.
    /// </summary>
    public required IReadOnlyList<TugPose> Samples { get; init; }

    /// <summary>The move finished inside its travel budget.</summary>
    public required bool Completed { get; init; }

    /// <summary>Distance travelled, feet.</summary>
    public required double PathLengthFt { get; init; }

    /// <summary>The largest angle the direction of travel wandered from where the move started, degrees.</summary>
    public required double MaxTravelDeviationDeg { get; init; }

    /// <summary>Where the move ended, or where it gave up. For a skipped move, the pose it would have started from.</summary>
    public required TugPose End { get; init; }

    /// <summary><see cref="TugMoveShape.ViaLine"/> only: the end's signed cross-track from the line, feet (positive = right).</summary>
    public required double? EndCrossTrackFt { get; init; }

    /// <summary><see cref="TugMoveShape.ViaLine"/> only: the end's |direction of travel − line direction|, degrees.</summary>
    public required double? EndLineTravelErrorDeg { get; init; }

    /// <summary>
    /// <see cref="TugMoveShape.ViaLine"/> with a <see cref="TugMove.StopAt"/> only: how far along the line the end is
    /// past the stop, feet (positive = past it, negative = short of it). Null for every other move.
    /// </summary>
    public required double? EndOvershootFt { get; init; }
}

/// <summary>A sequence of moves as <see cref="TugKinematics.Simulate"/> flew it.</summary>
public sealed record TugSimulation
{
    /// <summary>One trace per move, in order.</summary>
    public required IReadOnlyList<TugMoveTrace> Moves { get; init; }

    /// <summary>The last move's end pose (the start when there are no moves).</summary>
    public required TugPose End { get; init; }

    /// <summary>Every move finished inside its travel budget.</summary>
    public bool Completed => Moves.All(m => m.Completed);
}

/// <summary>
/// The motion body of a tug-moved aircraft: pure and deterministic, with no aircraft state. The reference point
/// moves along the aircraft's own axis (the main gear cannot slide sideways): tail-first on a push, nose-first on a
/// pull. Steering is by curvature, never by turn rate — over a step of <c>d</c> feet the direction of travel turns
/// by at most <c>d / R</c> radians, so a stopped aircraft never rotates and the path does not depend on speed.
/// </summary>
public static class TugKinematics
{
    private static readonly ILogger Log = SimLog.CreateLogger("TugKinematics");

    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>
    /// Nose-gear steering angle for a tight tow turn; a judgement call. The routine turn uses 45°, whose radius is
    /// the wheelbase itself.
    /// </summary>
    private const double TightSteeringAngleDeg = 67.5;

    private const double MinRadiusFt = 1.0;

    /// <summary>
    /// The roll-out radius of a line capture as a multiple of the turn radius. A capture turns in at up to 90°,
    /// flies straight, and rolls out onto its line on an arc this many turn radii wide. The margin over 1 is a
    /// judgement call, sized so the discrete, curvature-limited roll-out never overshoots the line.
    /// </summary>
    public const double RolloutMarginRadii = 1.15;

    /// <summary>The largest angle a line capture intercepts its line at.</summary>
    private const double MaxInterceptDeg = 90.0;

    private const double StopToleranceFt = 1.0;
    private const double CaptureCrossTrackFt = 1.0;
    private const double CaptureTravelErrorDeg = 1.0;
    private const double FacingToleranceDeg = 0.5;
    private const double SampleSpacingFt = 5.0;
    private const double SampleSpacingSlackFt = 1e-9;
    private const double StraightBudgetSlackFt = 1.0;
    private const double TurnBudgetSlackFt = 50.0;
    private const double ReachBudgetFactor = 3.0;
    private const double MinReachBudgetFt = 600.0;

    /// <summary>
    /// The radius a tug turns the aircraft on, feet. Every number here is a judgement call: the routine radius is
    /// the wheelbase (45° of nose-gear steering), the tight radius is the routine one divided by tan 67.5°
    /// (≈0.41 × wheelbase). A type with no FAA record or no wheelbase falls back by category — Jet 50 ft,
    /// Turboprop 30 ft, Piston 7 ft, Helicopter 10 ft — before the tight factor. Never below 1 ft.
    /// </summary>
    /// <param name="aircraftType">ICAO type designator, prefixes and suffixes allowed.</param>
    /// <param name="tight">Use the tight steering angle.</param>
    /// <returns>The turn radius, feet.</returns>
    public static double TurnRadiusFt(string aircraftType, bool tight)
    {
        double? wheelbaseFt = FaaAircraftDatabase.Get(aircraftType)?.WheelbaseFt;
        double routineFt =
            (wheelbaseFt is { } wheelbase && (wheelbase > 0.0)) ? wheelbase : FallbackRadiusFt(AircraftCategorization.Categorize(aircraftType));
        double radiusFt = tight ? routineFt / Math.Tan(TightSteeringAngleDeg * DegToRad) : routineFt;
        return Math.Max(MinRadiusFt, radiusFt);
    }

    /// <summary>
    /// The direction of travel after a step: the current travel turned toward the move's commanded travel by at
    /// most <c>stepFt / radiusFt</c> radians, the shorter way. Commanded travel: the start travel (straight), the
    /// bearing to the point (to-point), the roll-out law <c>χL − sign(e)·θ(e)</c> (line; <c>e</c> is the cross-track,
    /// positive right; <c>θ</c> is 90° when <c>|e| ≥ R_c</c>, else <c>acos(1 − |e|/R_c)</c>, with
    /// <c>R_c = <see cref="RolloutMarginRadii"/> × R</c>), or the travel that puts the nose on the facing (turn). A line
    /// capture therefore turns in at up to 90°, flies straight, and rolls out on an arc.
    /// </summary>
    /// <param name="pose">The current pose.</param>
    /// <param name="move">The move being flown.</param>
    /// <param name="progress">The move's progress so far.</param>
    /// <param name="radiusFt">The turn radius, feet; must be positive.</param>
    /// <param name="stepFt">The step length, feet. Zero or less returns the current travel unchanged.</param>
    /// <returns>The new direction of travel, degrees true in [0, 360).</returns>
    public static double SteerTravel(TugPose pose, TugMove move, TugMoveProgress progress, double radiusFt, double stepFt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radiusFt);
        double currentDeg = pose.TravelTrueDeg(move.Kind);
        if (stepFt <= 0.0)
        {
            return currentDeg;
        }

        double commandedDeg = CommandedTravelDeg(pose, move, progress, radiusFt);
        double maxTurnDeg = (stepFt / radiusFt) * RadToDeg;
        return GeoMath.TurnHeadingToward(new TrueHeading(currentDeg), commandedDeg, maxTurnDeg).Degrees;
    }

    /// <summary>Moves the reference point one step along the direction of travel and lines the body up with it.</summary>
    /// <param name="pose">The current pose.</param>
    /// <param name="move">The move being flown (its kind sets which end of the body leads).</param>
    /// <param name="travelTrueDeg">The direction of travel for this step, degrees true.</param>
    /// <param name="stepFt">The step length, feet.</param>
    /// <returns>The new pose: nose on the travel for a pull, on its reciprocal for a push.</returns>
    public static TugPose Advance(TugPose pose, TugMove move, double travelTrueDeg, double stepFt)
    {
        var position = GeoMath.ProjectPoint(pose.Position, new TrueHeading(travelTrueDeg), stepFt / GeoMath.FeetPerNm);
        return new TugPose(position, FlipForKind(travelTrueDeg, move.Kind));
    }

    /// <summary>
    /// Adds a step to a move's progress: the distance, the largest travel deviation from the start, and — for a line
    /// move — the capture latch (within 1 ft of the line and 1° of its direction).
    /// </summary>
    /// <param name="progress">The progress before the step.</param>
    /// <param name="pose">The pose after the step.</param>
    /// <param name="move">The move being flown.</param>
    /// <param name="stepFt">The step length, feet.</param>
    /// <returns>The progress after the step.</returns>
    public static TugMoveProgress Record(TugMoveProgress progress, TugPose pose, TugMove move, double stepFt)
    {
        double deviationDeg = AbsDiffDeg(pose.TravelTrueDeg(move.Kind), progress.StartTravelTrueDeg);
        bool captured = progress.Captured || ((move.Shape == TugMoveShape.ViaLine) && IsOnLine(pose, move));
        return progress with
        {
            DistanceFt = progress.DistanceFt + stepFt,
            Captured = captured,
            MaxTravelDeviationDeg = Math.Max(progress.MaxTravelDeviationDeg, deviationDeg),
        };
    }

    /// <summary>
    /// Whether a move has finished: a straight has covered its distance; a to-point is within 1 ft of its point; a
    /// floating line move has captured; a line move with a stop has captured <em>and</em> reached the stop's
    /// along-line position less 1 ft — one that reaches the stop first carries on until it captures, one that
    /// captures first carries on along the line to the stop; a turn has the nose within 0.5° of its facing.
    /// </summary>
    /// <param name="pose">The current pose.</param>
    /// <param name="move">The move being flown.</param>
    /// <param name="progress">The move's progress.</param>
    /// <returns>True when the move is done.</returns>
    public static bool IsComplete(TugPose pose, TugMove move, TugMoveProgress progress) =>
        move.Shape switch
        {
            TugMoveShape.Straight => progress.DistanceFt >= move.StraightDistanceFt,
            TugMoveShape.ToPoint => FeetBetween(pose.Position, move.Point) <= StopToleranceFt,
            TugMoveShape.ViaLine => progress.Captured
                && ((move.StopAt is not { } stop) || (AlongLineFt(pose.Position, stop, move) >= -StopToleranceFt)),
            TugMoveShape.TurnTo => AbsDiffDeg(pose.NoseTrueDeg, move.FacingTrueDeg) <= FacingToleranceDeg,
            _ => throw new ArgumentOutOfRangeException(nameof(move), move.Shape, "Unknown tug move shape"),
        };

    /// <summary>
    /// Flies the moves in order with fixed-length steps, each from the previous one's end pose. A move that travels
    /// past its budget — a straight's distance + 1 ft; a turn's 2πR + 50 ft; otherwise max(3 × the direct distance to
    /// the point, the stop or the line, 600 ft) — is reported not completed, and every later move is skipped.
    /// </summary>
    /// <param name="start">The pose before the first move.</param>
    /// <param name="moves">The moves, in order.</param>
    /// <param name="aircraftType">ICAO type designator; sets the turn radius.</param>
    /// <param name="stepFt">The step length, feet; must be positive.</param>
    /// <returns>One trace per move, and the end pose.</returns>
    public static TugSimulation Simulate(TugPose start, IReadOnlyList<TugMove> moves, string aircraftType, double stepFt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepFt);
        var traces = new List<TugMoveTrace>(moves.Count);
        var pose = start;
        bool flyable = true;
        foreach (var move in moves)
        {
            var trace = flyable ? SimulateMove(pose, move, aircraftType, stepFt) : SkippedTrace(pose, move);
            traces.Add(trace);
            pose = trace.End;
            flyable = trace.Completed;
        }

        return new TugSimulation { Moves = traces, End = pose };
    }

    /// <summary>The nose for a direction of travel, or the travel for a nose: the reciprocal on a push, itself on a pull.</summary>
    internal static double FlipForKind(double headingTrueDeg, PushbackLegKind kind) =>
        new TrueHeading(kind == PushbackLegKind.Push ? headingTrueDeg + 180.0 : headingTrueDeg).Degrees;

    private static TugMoveTrace SimulateMove(TugPose start, TugMove move, string aircraftType, double stepFt)
    {
        double radiusFt = TurnRadiusFt(aircraftType, move.Tight);
        double budgetFt = TravelBudgetFt(start, move, radiusFt);
        var progress = TugMoveProgress.Begin(start, move);
        var pose = start;
        var samples = new List<TugPose> { start };
        double sinceSampleFt = 0.0;
        bool completed = IsComplete(pose, move, progress);
        while (!completed && (progress.DistanceFt <= budgetFt))
        {
            double travelDeg = SteerTravel(pose, move, progress, radiusFt, stepFt);
            pose = Advance(pose, move, travelDeg, stepFt);
            progress = Record(progress, pose, move, stepFt);
            completed = IsComplete(pose, move, progress);
            sinceSampleFt += stepFt;
            if (sinceSampleFt >= (SampleSpacingFt - SampleSpacingSlackFt))
            {
                samples.Add(pose);
                sinceSampleFt = 0.0;
            }
        }

        if (sinceSampleFt > 0.0)
        {
            samples.Add(pose);
        }

        if (!completed)
        {
            Log.LogDebug(
                "Tug {Kind} {Shape} move unflyable: travelled {DistanceFt:F0} ft past its {BudgetFt:F0} ft budget (R={RadiusFt:F1} ft)",
                move.Kind,
                move.Shape,
                progress.DistanceFt,
                budgetFt,
                radiusFt
            );
        }

        bool isLine = move.Shape == TugMoveShape.ViaLine;
        return new TugMoveTrace
        {
            Move = move,
            Samples = samples,
            Completed = completed,
            PathLengthFt = progress.DistanceFt,
            MaxTravelDeviationDeg = progress.MaxTravelDeviationDeg,
            End = pose,
            EndCrossTrackFt = isLine ? CrossTrackFt(pose.Position, move) : null,
            EndLineTravelErrorDeg = isLine ? AbsDiffDeg(pose.TravelTrueDeg(move.Kind), move.LineTravelTrueDeg) : null,
            EndOvershootFt = (isLine && (move.StopAt is { } stop)) ? AlongLineFt(pose.Position, stop, move) : null,
        };
    }

    private static TugMoveTrace SkippedTrace(TugPose pose, TugMove move) =>
        new()
        {
            Move = move,
            Samples = [],
            Completed = false,
            PathLengthFt = 0.0,
            MaxTravelDeviationDeg = 0.0,
            End = pose,
            EndCrossTrackFt = null,
            EndLineTravelErrorDeg = null,
            EndOvershootFt = null,
        };

    private static double TravelBudgetFt(TugPose start, TugMove move, double radiusFt) =>
        move.Shape switch
        {
            TugMoveShape.Straight => move.StraightDistanceFt + StraightBudgetSlackFt,
            TugMoveShape.TurnTo => (2.0 * Math.PI * radiusFt) + TurnBudgetSlackFt,
            TugMoveShape.ToPoint => ReachBudgetFt(FeetBetween(start.Position, move.Point)),
            TugMoveShape.ViaLine => ReachBudgetFt(
                move.StopAt is { } stop ? FeetBetween(start.Position, stop) : Math.Abs(CrossTrackFt(start.Position, move))
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(move), move.Shape, "Unknown tug move shape"),
        };

    private static double ReachBudgetFt(double directFt) => Math.Max(ReachBudgetFactor * directFt, MinReachBudgetFt);

    private static double CommandedTravelDeg(TugPose pose, TugMove move, TugMoveProgress progress, double radiusFt) =>
        move.Shape switch
        {
            TugMoveShape.Straight => progress.StartTravelTrueDeg,
            TugMoveShape.ToPoint => GeoMath.BearingTo(pose.Position, move.Point),
            TugMoveShape.ViaLine => LineGuidanceDeg(pose, move, radiusFt),
            TugMoveShape.TurnTo => FlipForKind(move.FacingTrueDeg, move.Kind),
            _ => throw new ArgumentOutOfRangeException(nameof(move), move.Shape, "Unknown tug move shape"),
        };

    /// <summary>
    /// The roll-out law: the line direction turned toward the line by the intercept angle for the cross-track.
    /// </summary>
    private static double LineGuidanceDeg(TugPose pose, TugMove move, double radiusFt)
    {
        double crossFt = CrossTrackFt(pose.Position, move);
        double interceptDeg = InterceptDeg(Math.Abs(crossFt), RolloutMarginRadii * radiusFt);
        return new TrueHeading(move.LineTravelTrueDeg - (Math.Sign(crossFt) * interceptDeg)).Degrees;
    }

    /// <summary>
    /// The largest intercept angle from which one arc of radius <paramref name="rolloutFt"/> still rolls out onto the
    /// line: 90° at or beyond one roll-out radius off it, else <c>acos(1 − offLine / rolloutFt)</c>.
    /// </summary>
    private static double InterceptDeg(double offLineFt, double rolloutFt) =>
        offLineFt >= rolloutFt ? MaxInterceptDeg : Math.Acos(1.0 - (offLineFt / rolloutFt)) * RadToDeg;

    private static bool IsOnLine(TugPose pose, TugMove move) =>
        (Math.Abs(CrossTrackFt(pose.Position, move)) <= CaptureCrossTrackFt)
        && (AbsDiffDeg(pose.TravelTrueDeg(move.Kind), move.LineTravelTrueDeg) <= CaptureTravelErrorDeg);

    private static double FallbackRadiusFt(AircraftCategory category) =>
        category switch
        {
            AircraftCategory.Jet => 50.0,
            AircraftCategory.Turboprop => 30.0,
            AircraftCategory.Piston => 7.0,
            AircraftCategory.Helicopter => 10.0,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown aircraft category"),
        };

    private static double CrossTrackFt(LatLon point, TugMove move) =>
        GeoMath.SignedCrossTrackDistanceNm(point, move.Point, new TrueHeading(move.LineTravelTrueDeg)) * GeoMath.FeetPerNm;

    private static double AlongLineFt(LatLon point, LatLon reference, TugMove move) =>
        GeoMath.AlongTrackDistanceNm(point, reference, new TrueHeading(move.LineTravelTrueDeg)) * GeoMath.FeetPerNm;

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    private static double AbsDiffDeg(double a, double b) => new TrueHeading(a).AbsAngleTo(new TrueHeading(b));
}
