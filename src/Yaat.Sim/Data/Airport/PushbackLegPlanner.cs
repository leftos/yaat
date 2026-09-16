using Microsoft.Extensions.Logging;

namespace Yaat.Sim.Data.Airport;

/// <summary>One point a tug move is asked to reach.</summary>
/// <param name="Node">The ground node the leg ends on.</param>
/// <param name="IsSpot">True when the node is a painted ramp spot, whose facing comes from its sub-lane rather than a stored heading.</param>
public readonly record struct PushbackTarget(GroundNode Node, bool IsSpot);

/// <summary>One planned leg of a tug move.</summary>
/// <param name="Kind">Whether the tug reverses the aircraft or tows it forward over this leg.</param>
/// <param name="Target">Where the leg ends.</param>
/// <param name="EndTrueHeadingDeg">The nose heading in degrees true (1-360) the aircraft holds once the leg is done.</param>
/// <param name="LengthFt">Straight-line length of the leg, in feet.</param>
public readonly record struct PushbackLeg(PushbackLegKind Kind, LatLon Target, int EndTrueHeadingDeg, double LengthFt);

/// <summary>
/// Plans a multi-leg tug move across a ramp: a chain of pushes (tug reversing the aircraft tail-first) and
/// pulls (tug towing it nose-first) from one pose through a list of ramp targets. Pure geometry over the
/// ground layout — it moves nothing and reads no aircraft state, so the simulation and the client's map
/// preview can share one body and cannot disagree about what a move will look like.
///
/// <para><b>Leg kind.</b> Off a stand the first leg is always a push: an aircraft parked nose-in cannot be
/// towed forward off the stand, and a first target that is ahead of the nose is refused rather than reversed
/// away from. Every later leg follows the geometry alone — a target more than 90° off the nose is a push,
/// anything else a pull. There is no dead band: the binding limit on a tug reposition is nose-gear steering
/// angle, not heading delta, so a roughly 90° swing is routine.</para>
///
/// <para><b>End heading.</b> A leg naturally finishes with the nose pointed back the way it came for a push
/// and along the leg for a pull. On a non-final leg the planner will turn away from that when doing so makes
/// the next leg a pull, because AC 00-65A §11.10 says "Backing of aircraft should be avoided as much as
/// possible." How much turn a leg is worth follows from the tug's own numbers: at
/// <see cref="CategoryPerformance.PushbackTurnRate"/> degrees per second and
/// <see cref="CategoryPerformance.PushbackSpeed"/> knots of ground speed, a leg buys
/// <c>turnRate / (speedKt x 6076.12 / 3600)</c> degrees per foot — about 0.59 °/ft at the modelled 5°/s and
/// 5 kt, so a 200 ft leg is worth roughly 119°. A turn beyond that is not planned; the next leg falls out a
/// push instead. The final leg takes an explicit facing when one is given, else a spot's nose-out heading,
/// else a parking or helipad node's own heading, else the natural one.</para>
///
/// <para><b>Refusals.</b> Fewer than two targets is a plain <c>PUSH</c>, not a tug move. A leg is refused
/// when it is longer than <see cref="MaxLegFt"/>, crosses a runway, reaches a runway holding position, or
/// transits movement-area pavement rather than merely starting on it or arriving at it.</para>
/// </summary>
public static class PushbackLegPlanner
{
    private static readonly ILogger Log = SimLog.CreateLogger("PushbackLegPlanner");

    /// <summary>
    /// The longest leg the planner will plan. This is a UI sanity bound against a mis-click on the ground
    /// view, not an aviation rule: nothing in the AIM or AC 00-65A caps how far a tug may move an aircraft.
    /// </summary>
    public const double MaxLegFt = 2000.0;

    /// <summary>A target this far off the nose or less is ahead of the aircraft; beyond it, behind.</summary>
    private const double AheadOfNoseDeg = 90.0;

    /// <summary>
    /// Plans the legs of a tug move.
    /// </summary>
    /// <param name="layout">The airport ground layout the move happens on.</param>
    /// <param name="start">Where the aircraft is now.</param>
    /// <param name="startTrueHeadingDeg">The nose heading now, degrees true.</param>
    /// <param name="startsAtStand">True when the aircraft is parked on a stand, so its first leg must be a push.</param>
    /// <param name="targets">The points to reach, in order. Two or more.</param>
    /// <param name="explicitFinalFacingTrueDeg">A facing the controller named for the last leg, degrees true, or null to derive one.</param>
    /// <param name="category">The aircraft category, which sets the tug's speed and turn rate.</param>
    /// <param name="refusal">Why the move cannot be planned, or empty on success.</param>
    /// <returns>One leg per target, or null when <paramref name="refusal"/> says why not.</returns>
    public static IReadOnlyList<PushbackLeg>? Plan(
        AirportGroundLayout layout,
        LatLon start,
        double startTrueHeadingDeg,
        bool startsAtStand,
        IReadOnlyList<PushbackTarget> targets,
        int? explicitFinalFacingTrueDeg,
        AircraftCategory category,
        out string refusal
    )
    {
        if (targets.Count < 2)
        {
            refusal = "Unable, a tug move needs at least two points — use PUSH to reach a single one";
            Log.LogDebug("Tug move refused: {Refusal}", refusal);
            return null;
        }

        var pavement = new TugPavementClassifier(layout);
        double turnPerFootDeg = TurnPerFootDeg(category);
        var legs = new List<PushbackLeg>(targets.Count);
        var from = start;
        double noseTrueDeg = startTrueHeadingDeg;

        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            var to = target.Node.Position;
            double bearing = GeoMath.BearingTo(from, to);
            double lengthFt = GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;
            var probe = new LegProbe(from, target, lengthFt, Label(target, i));

            if (IsLegObstructed(pavement, probe, out refusal))
            {
                Log.LogDebug("Tug move refused: {Refusal}", refusal);
                return null;
            }

            bool firstLegOffAStand = (i == 0) && startsAtStand;
            double offNoseDeg = GeoMath.AbsBearingDifference(bearing, noseTrueDeg);
            if (!TryResolveKind(firstLegOffAStand, offNoseDeg, probe.Label, out var kind, out refusal))
            {
                Log.LogDebug("Tug move refused: {Refusal}", refusal);
                return null;
            }

            double naturalTrueDeg = kind == PushbackLegKind.Push ? bearing + 180.0 : bearing;
            double endTrueDeg =
                i == (targets.Count - 1)
                    ? FinalFacing(layout, target, explicitFinalFacingTrueDeg, naturalTrueDeg)
                    : FacingForNextLeg(to, targets[i + 1].Node.Position, naturalTrueDeg, lengthFt * turnPerFootDeg);

            legs.Add(new PushbackLeg(kind, to, new TrueHeading(endTrueDeg).ToDisplayInt(), lengthFt));
            from = to;
            noseTrueDeg = endTrueDeg;
        }

        refusal = string.Empty;
        return legs;
    }

    /// <summary>
    /// Whether the leg may be flown at all, and why not. Length is tested first so a mis-click on the far side
    /// of the field reads as a mis-click even when the line it draws also crosses something.
    /// </summary>
    private static bool IsLegObstructed(TugPavementClassifier pavement, LegProbe leg, out string refusal)
    {
        // The 2,000 ft bound is a UI sanity guard against a mis-click on the ground view, not an aviation
        // rule — no published limit caps how far a tug may move an aircraft.
        if (leg.LengthFt > MaxLegFt)
        {
            refusal = $"Unable, {leg.Label} is {leg.LengthFt:0} ft — past the {MaxLegFt:0} ft sanity guard on a tug move";
            return true;
        }

        var to = leg.Target.Node.Position;
        if (pavement.CrossedRunwayName(leg.From, to) is { } runway)
        {
            refusal = $"Unable, {leg.Label} crosses runway {runway}";
            return true;
        }

        // A free-space tug move bypasses the hold-short machinery entirely (docs/ground/pushback.md), so the
        // leg itself has to keep clear: no part of an aircraft may pass a runway holding position without a
        // crossing clearance (AIM 4-3-18.a.5; AC 00-65A §11.13).
        if ((leg.Target.Node.Type == GroundNodeType.RunwayHoldShort) || pavement.ReachesHoldShort(leg.From, to))
        {
            refusal = $"Unable, {leg.Label} reaches a runway holding position";
            return true;
        }

        if (pavement.TransitedMovementAreaName(leg.From, to) is { } taxiway)
        {
            refusal = $"Unable, {leg.Label} crosses taxiway {taxiway}";
            return true;
        }

        refusal = string.Empty;
        return false;
    }

    /// <summary>
    /// Whether the tug pushes or pulls over this leg. Off a stand it can only push, and a target ahead of the
    /// nose there is a contradiction — the aircraft has to come off the stand backwards first.
    /// </summary>
    private static bool TryResolveKind(bool firstLegOffAStand, double offNoseDeg, string label, out PushbackLegKind kind, out string refusal)
    {
        refusal = string.Empty;
        if (!firstLegOffAStand)
        {
            kind = offNoseDeg > AheadOfNoseDeg ? PushbackLegKind.Push : PushbackLegKind.Pull;
            return true;
        }

        kind = PushbackLegKind.Push;
        if (offNoseDeg <= AheadOfNoseDeg)
        {
            refusal = $"Unable, {label} is ahead of the nose — the aircraft has to be pushed back off the stand first";
            return false;
        }

        return true;
    }

    /// <summary>The facing the aircraft is left in at the end of the move.</summary>
    private static double FinalFacing(AirportGroundLayout layout, PushbackTarget target, int? explicitFacingTrueDeg, double naturalTrueDeg)
    {
        if (explicitFacingTrueDeg is { } explicitFacing)
        {
            return explicitFacing;
        }

        if (target.IsSpot && layout.TryGetSpotOutboundHeading(target.Node, out double outbound))
        {
            return outbound;
        }

        return target.Node.TrueHeading?.Degrees ?? naturalTrueDeg;
    }

    /// <summary>
    /// The facing to leave a non-final leg in: the bearing of the next leg when the tug can turn that far
    /// within this leg, so the next one is a pull (AC 00-65A §11.10, "Backing of aircraft should be avoided
    /// as much as possible"); otherwise the leg's natural end heading, and the next leg is a push.
    /// </summary>
    private static double FacingForNextLeg(LatLon target, LatLon next, double naturalTrueDeg, double availableTurnDeg)
    {
        double desired = GeoMath.BearingTo(target, next);
        return GeoMath.AbsBearingDifference(desired, naturalTrueDeg) <= availableTurnDeg ? desired : naturalTrueDeg;
    }

    /// <summary>Degrees of heading change one foot of leg is worth at the tug's own speed and turn rate.</summary>
    private static double TurnPerFootDeg(AircraftCategory category)
    {
        double speedFtPerSec = CategoryPerformance.PushbackSpeed(category) * GeoMath.FeetPerNm / 3600.0;
        return CategoryPerformance.PushbackTurnRate(category) / speedFtPerSec;
    }

    /// <summary>How a leg is named in a refusal: its number and the target it was headed for.</summary>
    private static string Label(PushbackTarget target, int index)
    {
        string name = target.Node.Name ?? $"node {target.Node.Id}";
        return $"leg {index + 1} to {(target.IsSpot ? $"spot {name}" : name)}";
    }

    /// <summary>One leg's inputs, bundled so the obstruction tests read as one question.</summary>
    private readonly record struct LegProbe(LatLon From, PushbackTarget Target, double LengthFt, string Label);
}
