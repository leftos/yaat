namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Classifies the pavement a tug move would cross. The movement-area verdict per taxiway name is the layout's
/// <see cref="MovementAreaClassification"/>, the one the ramp-lane cut reads, so the push planner and the taxi router
/// agree; it is computed once per layout because a move tests every edge on the field.
/// </summary>
internal sealed class TugPavementClassifier
{
    /// <summary>
    /// How close to either end of a leg movement-area pavement may be touched. A leg is allowed to reach a
    /// taxiway — that is what <c>PUSH &lt;taxiway&gt;</c> already does — and equally to leave one it is already
    /// standing on: an aircraft that has made a <c>PUSH A</c> is sitting on taxiway A, whose own edges cut its
    /// next leg at ~0 ft from that leg's start. What stays refused is an intersection in between, which is the
    /// leg crossing pavement it was not cleared onto and carrying on.
    /// </summary>
    internal const double MovementAreaEndWindowFt = 25.0;

    private readonly AirportGroundLayout _layout;
    private readonly MovementAreaClassification _movementArea;

    internal TugPavementClassifier(AirportGroundLayout layout)
    {
        _layout = layout;
        _movementArea = MovementAreaClassification.For(layout);
    }

    /// <summary>The name of the runway the leg crosses, or null when it crosses none.</summary>
    internal string? CrossedRunwayName(LatLon from, LatLon to)
    {
        if (!_layout.RunwayCenterlineBetween(from, to))
        {
            return null;
        }

        foreach (GroundRunway runway in _layout.Runways)
        {
            for (int i = 1; i < runway.Coordinates.Count; i++)
            {
                var a = new LatLon(runway.Coordinates[i - 1].Lat, runway.Coordinates[i - 1].Lon);
                var b = new LatLon(runway.Coordinates[i].Lat, runway.Coordinates[i].Lon);
                if (GeoMath.SegmentsIntersect(from, to, a, b) is not null)
                {
                    return runway.Name;
                }
            }
        }

        return null;
    }

    /// <summary>True when the leg touches any edge hanging off a runway holding-position node.</summary>
    internal bool ReachesHoldShort(LatLon from, LatLon to)
    {
        foreach (IGroundEdge edge in _layout.AllEdges)
        {
            bool touchesHoldShort = (edge.Nodes[0].Type == GroundNodeType.RunwayHoldShort) || (edge.Nodes[1].Type == GroundNodeType.RunwayHoldShort);
            if (touchesHoldShort && (GeoMath.SegmentsIntersect(from, to, edge.Nodes[0].Position, edge.Nodes[1].Position) is not null))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The name of the movement-area pavement the leg transits, or null. An intersection within
    /// <see cref="MovementAreaEndWindowFt"/> of <em>either</em> end point is the leg arriving at that
    /// pavement or leaving pavement it already stands on, both of which are allowed; an intersection
    /// anywhere between the two is the leg cutting across it, which is not.
    /// </summary>
    internal string? TransitedMovementAreaName(LatLon from, LatLon to)
    {
        foreach (IGroundEdge edge in _layout.AllEdges)
        {
            if (MovementAreaName(edge) is not { } name)
            {
                continue;
            }

            if (GeoMath.SegmentsIntersect(from, to, edge.Nodes[0].Position, edge.Nodes[1].Position) is not { } hit)
            {
                continue;
            }

            double toEndFt = Math.Min(GeoMath.DistanceNm(hit.Point, from), GeoMath.DistanceNm(hit.Point, to)) * GeoMath.FeetPerNm;
            if (toEndFt > MovementAreaEndWindowFt)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// The movement-area name the edge carries, or null when the edge is ramp. An edge counts as movement
    /// area only when <em>none</em> of its names is apron or a ramp taxilane: shared pavement at a ramp
    /// entrance is the boundary, and a leg is allowed to reach movement-area pavement. SFO's six-alley
    /// entrance arc carries both taxiway <c>A</c> and ramp lane <c>T6A</c>, and reading it as taxiway A cut
    /// a legitimate tug move from A into the alley 75 ft in.
    ///
    /// <para>This is a decision, not an oversight: it also lets a leg clip taxiway <c>Y</c> where SFO's M1
    /// arc shares pavement with it. Shared pavement is where the ramp ends, so a leg that touches it is a
    /// leg reaching the movement area rather than cutting across it, and the refusals that carry safety
    /// weight — runway pavement, a runway holding position — are separate rules that still fire.</para>
    /// </summary>
    internal string? MovementAreaName(IGroundEdge edge)
    {
        string? movementArea = null;
        foreach (string name in RampLaneReposition.EdgeNames(edge))
        {
            if (!IsMovementArea(name))
            {
                return null;
            }

            movementArea ??= name;
        }

        return movementArea;
    }

    private bool IsMovementArea(string name) => _movementArea.IsMovementArea(name);
}
