namespace Yaat.Sim.Data.Airport;

/// <summary>
/// One aircraft as <see cref="TugParkedNeighbours"/> reads it: the plain facts that decide whether it is a parked or held
/// neighbour of a tug move and, if so, how the planner sweeps against it. The simulation fills it from an
/// <see cref="AircraftState"/>; the client fills it from the aircraft as they arrive on the wire, so both plan against
/// the same neighbours.
/// </summary>
public sealed record TugNeighbourCandidate
{
    /// <summary>Its callsign; tells the moving aircraft apart from itself and names it in a refusal.</summary>
    public required string Callsign { get; init; }

    /// <summary>Where it stands.</summary>
    public required LatLon Position { get; init; }

    /// <summary>Its nose heading, degrees true.</summary>
    public required double TrueHeadingDeg { get; init; }

    /// <summary>ICAO type designator; sets its outline.</summary>
    public required string AircraftType { get; init; }

    /// <summary>The stand it is parked on, or null when it is not on a named stand.</summary>
    public required string? StandName { get; init; }

    /// <summary>It is under a controller hold.</summary>
    public required bool IsImmobile { get; init; }

    /// <summary>Its current phase's name, or null when it has none.</summary>
    public required string? PhaseName { get; init; }

    /// <summary>Its ground speed, knots.</summary>
    public required double GroundSpeedKts { get; init; }

    /// <summary>The speed it is commanding, knots, or null when it commands none.</summary>
    public required double? TargetSpeedKts { get; init; }

    /// <summary>The candidate a simulated aircraft is, where it stands now.</summary>
    /// <param name="aircraft">The aircraft.</param>
    /// <returns>Its candidate.</returns>
    public static TugNeighbourCandidate From(AircraftState aircraft) =>
        new()
        {
            Callsign = aircraft.Callsign,
            Position = aircraft.Position,
            TrueHeadingDeg = aircraft.TrueHeading.Degrees,
            AircraftType = aircraft.AircraftType,
            StandName = aircraft.Ground.ParkingSpot,
            IsImmobile = aircraft.Ground.IsImmobile,
            PhaseName = aircraft.Phases?.CurrentPhase?.Name,
            GroundSpeedKts = aircraft.GroundSpeed,
            TargetSpeedKts = aircraft.Targets.TargetSpeed,
        };
}

/// <summary>
/// A parked or held neighbour the aircraft already touches or overlaps where it stands, found when a tug move is
/// planned: a placement error the tow must not paper over, so the move is refused naming both aircraft.
/// </summary>
/// <param name="AircraftCallsign">The aircraft the move was planned for.</param>
/// <param name="NeighbourCallsign">The neighbour it overlaps.</param>
/// <param name="ClearanceFt">Their outline clearance, feet; under <see cref="GroundOutlineSweep.OutlineClearanceSlackFt"/>.</param>
/// <param name="TowedNoseFirst">The planned move opens with a pull, so a tug and towbar lead the aircraft's nose.</param>
public sealed record TugStartOverlap(string AircraftCallsign, string NeighbourCallsign, double ClearanceFt, bool TowedNoseFirst)
{
    /// <summary>The refusal the simulation answers the tug command with, and the push-route preview shows.</summary>
    public string Refusal =>
        $"Unable, {AircraftCallsign} is up against {NeighbourCallsign} — their outlines overlap; reposition one of them before towing";
}

/// <summary>
/// The parked or held aircraft a tug move is planned around, as <see cref="TugMovePlanner"/> sees them: a candidate
/// that would swing into one is dropped at planning time rather than accepted and then held at a standstill by
/// <see cref="GroundConflictDetector"/> halfway through the manoeuvre. One body for the simulation, which plans the
/// move that flies, and the client, which previews it.
/// </summary>
public static class TugParkedNeighbours
{
    /// <summary>
    /// How far from the aircraft a parked or held neighbour is fed to <see cref="TugMovePlanner"/>, feet: a swing radius
    /// about the start pose, wide enough to hold every aircraft a candidate could swing into as it comes off the stand,
    /// and short enough that a ramp's worth of parked aircraft is not swept against every candidate.
    ///
    /// <para>The range is anchored on the start pose alone, so a longer relocation runs out of it — an SFO stand-to-spot
    /// move can cover more than 500 ft (D15 to spot 6A is about 521 ft) — and a neighbour near the far end is never fed
    /// to the planner. That is deliberate: the planner chooses the template that gets the aircraft off its stand, and
    /// <see cref="GroundConflictDetector"/> owns the drive from there. A judgement call.</para>
    /// </summary>
    public const double RangeFt = 400.0;

    /// <summary>
    /// The parked or held aircraft within <see cref="RangeFt"/> of the aircraft, excluding the aircraft itself (by
    /// callsign) and any neighbour it already touches where it stands — <see cref="FindStartOverlap"/> refuses the
    /// move for that one instead.
    /// </summary>
    /// <param name="aircraft">The aircraft the move is planned for.</param>
    /// <param name="others">Every aircraft the caller can see; may include the aircraft itself.</param>
    /// <returns>The neighbours to plan around.</returns>
    public static IReadOnlyList<TugParkedNeighbour> Build(TugNeighbourCandidate aircraft, IEnumerable<TugNeighbourCandidate> others)
    {
        var near = new List<TugParkedNeighbour>();
        foreach (TugNeighbourCandidate other in others)
        {
            if (!IsParkedOrHeldOther(aircraft, other))
            {
                continue;
            }

            if ((GeoMath.DistanceNm(aircraft.Position, other.Position) * GeoMath.FeetPerNm) > RangeFt)
            {
                continue;
            }

            // A neighbour the aircraft already touches where it stands is no candidate's to avoid — every candidate
            // fouls it at its first sample — and FindStartOverlap refuses the move naming both aircraft. Planning
            // around it would answer that placement error with the wrong message.
            if (ClearanceFt(aircraft, aTowedNoseFirst: false, other) < GroundOutlineSweep.OutlineClearanceSlackFt)
            {
                continue;
            }

            near.Add(
                new TugParkedNeighbour
                {
                    Callsign = other.Callsign,
                    Position = other.Position,
                    TrueHeadingDeg = other.TrueHeadingDeg,
                    AircraftType = other.AircraftType,
                    StandName = other.StandName,
                }
            );
        }

        return near;
    }

    /// <summary>
    /// The first parked or held aircraft whose outline the aircraft already touches or overlaps where it stands, with
    /// a tug and towbar ahead of its nose when the planned move opens with a pull; null when every one is clear. No
    /// range applies: an overlap is a contact wherever the pair stands.
    /// </summary>
    /// <param name="aircraft">The aircraft the move was planned for, where it stands now.</param>
    /// <param name="plan">The planned move; its first leg says whether a tug leads the nose.</param>
    /// <param name="others">Every aircraft the caller can see; may include the aircraft itself.</param>
    /// <returns>The overlap, or null.</returns>
    public static TugStartOverlap? FindStartOverlap(TugNeighbourCandidate aircraft, TugPlan plan, IEnumerable<TugNeighbourCandidate> others)
    {
        bool towedNoseFirst = (plan.Moves.Count > 0) && (plan.Moves[0].Move.Kind == PushbackLegKind.Pull);
        foreach (TugNeighbourCandidate other in others)
        {
            if (!IsParkedOrHeldOther(aircraft, other))
            {
                continue;
            }

            double clearanceFt = ClearanceFt(aircraft, towedNoseFirst, other);
            if (clearanceFt < GroundOutlineSweep.OutlineClearanceSlackFt)
            {
                return new TugStartOverlap(aircraft.Callsign, other.Callsign, clearanceFt, towedNoseFirst);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="other"/> is a parked or held aircraft other than <paramref name="aircraft"/>. A dry-run
    /// dispatch plans against a clone standing exactly where the original does, so the callsign — not the reference —
    /// is what tells the aircraft apart from itself.
    /// </summary>
    private static bool IsParkedOrHeldOther(TugNeighbourCandidate aircraft, TugNeighbourCandidate other) =>
        !string.Equals(other.Callsign, aircraft.Callsign, StringComparison.OrdinalIgnoreCase)
        && GroundConflictDetector.IsParkedOrHeld(other.IsImmobile, other.PhaseName, other.GroundSpeedKts, other.TargetSpeedKts);

    private static double ClearanceFt(TugNeighbourCandidate a, bool aTowedNoseFirst, TugNeighbourCandidate b) =>
        GroundOutline.ClearanceBetween(
            new TugPose(a.Position, a.TrueHeadingDeg),
            a.AircraftType,
            aTowedNoseFirst,
            new TugPose(b.Position, b.TrueHeadingDeg),
            b.AircraftType
        );
}
