using System.Text.Json.Serialization;

namespace Yaat.Sim.Data.Airport.Precompute;

/// <summary>
/// One move of a precomputed push plan, as plain fields: the move fields of <c>PushbackPhaseDto</c> and the end the plan
/// simulated for it, so a stored plan can be flown as the live push would fly it.
/// </summary>
public sealed record PushMoveEntry
{
    /// <summary>Push (tail-first) or pull (nose-first).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PushbackLegKind>))]
    public required PushbackLegKind Kind { get; init; }

    /// <summary>The path the move steers along.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TugMoveShape>))]
    public required TugMoveShape Shape { get; init; }

    /// <summary>The move steers on the type's tight radius.</summary>
    public required bool Tight { get; init; }

    /// <summary>The tug slows to walking-alignment speed for the move.</summary>
    public required bool Creep { get; init; }

    /// <summary>The move reverses the previous one, so the aircraft dwells stopped before it starts.</summary>
    public required bool DwellBefore { get; init; }

    /// <summary>The move's point latitude: a to-point move's target, a via-line move's point on the line.</summary>
    public required double PointLatitude { get; init; }

    /// <summary>The move's point longitude.</summary>
    public required double PointLongitude { get; init; }

    /// <summary>A via-line move's direction of travel along the line, degrees true.</summary>
    public required double LineTravelTrueDeg { get; init; }

    /// <summary>Where along the line a via-line move stops, latitude; null for a floating stop and every other shape.</summary>
    public required double? StopAtLatitude { get; init; }

    /// <summary>Where along the line a via-line move stops, longitude; null with <see cref="StopAtLatitude"/>.</summary>
    public required double? StopAtLongitude { get; init; }

    /// <summary>A straight move's distance, feet.</summary>
    public required double StraightDistanceFt { get; init; }

    /// <summary>A turn-to move's nose heading to end on, degrees true.</summary>
    public required double FacingTrueDeg { get; init; }

    /// <summary>Where the plan simulated the move ending, latitude.</summary>
    public required double PlannedEndLatitude { get; init; }

    /// <summary>Where the plan simulated the move ending, longitude.</summary>
    public required double PlannedEndLongitude { get; init; }

    /// <summary>
    /// The entry for one flown move of a plan, rounded so the stored bytes do not carry floating-point noise: latitudes
    /// and longitudes to 1e-7°, feet and degrees to 0.01.
    /// </summary>
    /// <param name="trace">The move as the planner flew it.</param>
    /// <returns>The entry.</returns>
    public static PushMoveEntry From(TugMoveTrace trace)
    {
        TugMove move = trace.Move;
        (double? stopLatitude, double? stopLongitude) = move.StopAt is { } stop
            ? ((double?)Coordinate(stop.Lat), (double?)Coordinate(stop.Lon))
            : (null, null);
        return new PushMoveEntry
        {
            Kind = move.Kind,
            Shape = move.Shape,
            Tight = move.Tight,
            Creep = move.Creep,
            DwellBefore = move.DwellBefore,
            PointLatitude = Coordinate(move.Point.Lat),
            PointLongitude = Coordinate(move.Point.Lon),
            LineTravelTrueDeg = Bearing(move.LineTravelTrueDeg),
            StopAtLatitude = stopLatitude,
            StopAtLongitude = stopLongitude,
            StraightDistanceFt = Hundredths(move.StraightDistanceFt),
            FacingTrueDeg = Bearing(move.FacingTrueDeg),
            PlannedEndLatitude = Coordinate(trace.End.Position.Lat),
            PlannedEndLongitude = Coordinate(trace.End.Position.Lon),
        };
    }

    /// <summary>
    /// The move this entry stores, built through the shape factory the planner built it with: the inverse of
    /// <see cref="From"/> for every field but the planned end, which a re-flown move works out again.
    /// </summary>
    /// <returns>The move.</returns>
    /// <exception cref="InvalidOperationException">
    /// The entry carries one coordinate of its stop without the other, or a shape no move factory builds.
    /// </exception>
    public TugMove ToMove()
    {
        TugMove move = Shape switch
        {
            TugMoveShape.Straight => TugMove.Straight(Kind, StraightDistanceFt),
            TugMoveShape.ToPoint => TugMove.ToPoint(Kind, Point()),
            TugMoveShape.ViaLine => TugMove.ViaLine(Kind, Point(), LineTravelTrueDeg, StopAt()),
            TugMoveShape.TurnTo => TugMove.TurnTo(Kind, FacingTrueDeg),
            _ => throw new InvalidOperationException($"A stored push move has shape {Shape}, which no move factory builds"),
        };
        return move with { Tight = Tight, Creep = Creep, DwellBefore = DwellBefore };
    }

    /// <summary>The move's point: a to-point move's target, a via-line move's point on the line.</summary>
    private LatLon Point() => new(PointLatitude, PointLongitude);

    /// <summary>The via-line stop the entry stores, or null for a floating stop.</summary>
    private LatLon? StopAt() =>
        (StopAtLatitude, StopAtLongitude) switch
        {
            ({ } latitude, { } longitude) => new LatLon(latitude, longitude),
            (null, null) => null,
            _ => throw new InvalidOperationException(
                $"A stored push move carries one coordinate of its stop without the other: {StopAtLatitude}, {StopAtLongitude}"
            ),
        };

    private static double Coordinate(double degrees) => Math.Round(degrees, 7);

    private static double Hundredths(double value) => Math.Round(value, 2);

    /// <summary>A bearing rounded to 0.01° in [0, 360): a bearing that rounds up to 360.00 is stored as 0.</summary>
    private static double Bearing(double degrees)
    {
        double rounded = Hundredths(new TrueHeading(degrees).Degrees);
        return rounded >= 360.0 ? 0.0 : rounded;
    }
}
