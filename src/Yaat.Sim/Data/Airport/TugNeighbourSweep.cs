namespace Yaat.Sim.Data.Airport;

/// <summary>
/// Sweeps a tug move's flown path against one parked neighbour by the outline rule <see cref="GroundConflictDetector"/>
/// holds the move under way to (<see cref="GroundOutlineSweep"/>). One body, two callers: <see cref="TugMovePlanner"/>
/// judges and ranks every candidate with it, and the push-target live check judges a stored push plan against the
/// neighbours parked when the menu opens. A second copy of this rule would let the two disagree about the same path.
/// </summary>
internal static class TugNeighbourSweep
{
    /// <summary>
    /// Where the flown moves first fall through the sweep floor a plain tow is held to against one parked neighbour: each
    /// run of them (<see cref="RunPathFrom"/>) is swept with its floor anchored where the run starts, and the earliest foul
    /// over every run is measured along the whole path from the first move's start. Null when every run keeps the floor.
    /// </summary>
    /// <param name="traces">The moves as flown, in order.</param>
    /// <param name="neighbour">The parked neighbour.</param>
    /// <param name="frameOrigin">Where the tow starts; both outlines are built in a flat frame centred on it.</param>
    /// <param name="mover">The towed aircraft's dimensions; they set its outline.</param>
    /// <param name="rowClearanceFt">
    /// The tow's row clearance from the neighbour (<see cref="RowClearanceFt"/>), feet, or null when it does not count.
    /// </param>
    /// <returns>How far along the path the first foul sits, feet, or null.</returns>
    internal static double? FirstFoulAlongFt(
        IReadOnlyList<TugMoveTrace> traces,
        TugParkedNeighbour neighbour,
        LatLon frameOrigin,
        AircraftFootprint mover,
        double? rowClearanceFt
    )
    {
        var frame = new GroundOutlineFrame(frameOrigin);
        var neighbourSize = GroundOutlineSize.Of(neighbour.Footprint, towedNoseFirst: false);
        double? firstFt = null;
        double runStartFt = 0.0;
        for (int i = 0; (i < traces.Count) && (runStartFt < (firstFt ?? double.MaxValue)); i++)
        {
            List<(TugPose Pose, double AlongFt)> path = RunPathFrom(traces, i);
            if (path.Count > 0)
            {
                GroundOutlineSweepResult swept = GroundOutlineSweep.Sweep(
                    path,
                    path[0].Pose,
                    rowClearanceFt,
                    frame,
                    GroundOutlineSize.Of(mover, towedNoseFirst: traces[i].Move.Kind == PushbackLegKind.Pull),
                    neighbour.Position,
                    neighbour.TrueHeadingDeg,
                    neighbourSize
                );
                if (swept.Foul is { } foul)
                {
                    firstFt = Math.Min(firstFt ?? double.MaxValue, runStartFt + foul.AlongFt);
                }
            }

            runStartFt += SampledLengthFt(traces[i]);
        }

        return firstFt;
    }

    /// <summary>The closest the mover's outline comes to one parked neighbour's over every sample of the flown moves, feet.</summary>
    /// <param name="traces">The moves as flown, in order.</param>
    /// <param name="neighbour">The parked neighbour.</param>
    /// <param name="frameOrigin">Where the tow starts; both outlines are built in a flat frame centred on it.</param>
    /// <param name="mover">The towed aircraft's dimensions; they set its outline.</param>
    /// <returns>The closest approach, feet; <see cref="double.MaxValue"/> when the moves carry no sample.</returns>
    internal static double ClosestFt(IReadOnlyList<TugMoveTrace> traces, TugParkedNeighbour neighbour, LatLon frameOrigin, AircraftFootprint mover)
    {
        var frame = new GroundOutlineFrame(frameOrigin);
        var outline = GroundOutline.At(
            frame.ToLocal(neighbour.Position),
            neighbour.TrueHeadingDeg,
            GroundOutlineSize.Of(neighbour.Footprint, towedNoseFirst: false)
        );
        double closestFt = double.MaxValue;
        foreach (TugMoveTrace trace in traces)
        {
            var moverSize = GroundOutlineSize.Of(mover, towedNoseFirst: trace.Move.Kind == PushbackLegKind.Pull);
            foreach (TugPose pose in trace.Samples)
            {
                closestFt = Math.Min(
                    closestFt,
                    GroundOutline.Clearance(GroundOutline.At(frame.ToLocal(pose.Position), pose.NoseTrueDeg, moverSize), outline)
                );
            }
        }

        return closestFt;
    }

    /// <summary>
    /// A tow's row clearance from one parked neighbour (<see cref="GroundOutlineSweep.RowClearanceFt"/>), from its row
    /// anchor, measured once per anchor and neighbour in <paramref name="rowClearances"/>. Null when there is no anchor or
    /// the row does not count.
    /// </summary>
    /// <param name="rowClearances">The measurements so far, reused for as long as their inputs stay the same.</param>
    /// <param name="anchor">Where the tow began and its first move's kind, or null when there is no move yet.</param>
    /// <param name="mover">The towed aircraft's dimensions.</param>
    /// <param name="neighbour">The parked neighbour.</param>
    /// <returns>The row clearance, feet, or null.</returns>
    internal static double? RowClearanceFt(
        TugRowClearances rowClearances,
        TugRowAnchor? anchor,
        AircraftFootprint mover,
        TugParkedNeighbour neighbour
    ) =>
        anchor is { } rowAnchor
            ? rowClearances.RowClearanceFt(
                rowAnchor,
                mover,
                neighbour.Callsign,
                new TugPose(neighbour.Position, neighbour.TrueHeadingDeg),
                neighbour.Footprint
            )
            : null;

    /// <summary>
    /// The poses of move <paramref name="index"/> and of every move flown through from it before the first reversal,
    /// each with how far along that run it sits, feet.
    /// </summary>
    /// <param name="traces">The moves as flown, in order.</param>
    /// <param name="index">The move the run starts at.</param>
    /// <returns>The run's poses with their along-distances.</returns>
    internal static List<(TugPose Pose, double AlongFt)> RunPathFrom(IReadOnlyList<TugMoveTrace> traces, int index)
    {
        var path = new List<(TugPose Pose, double AlongFt)>();
        double alongFt = 0.0;
        LatLon? previous = null;
        for (int i = index; (i < traces.Count) && (traces[i].Move.Kind == traces[index].Move.Kind); i++)
        {
            foreach (TugPose pose in traces[i].Samples)
            {
                alongFt += previous is { } from ? FeetBetween(from, pose.Position) : 0.0;
                path.Add((pose, alongFt));
                previous = pose.Position;
            }
        }

        return path;
    }

    /// <summary>
    /// A move's length as its samples measure it, feet: the same chord sum <see cref="RunPathFrom"/> counts along, so the
    /// two add up to one distance along the whole path.
    /// </summary>
    private static double SampledLengthFt(TugMoveTrace trace)
    {
        double lengthFt = 0.0;
        for (int i = 1; i < trace.Samples.Count; i++)
        {
            lengthFt += FeetBetween(trace.Samples[i - 1].Position, trace.Samples[i].Position);
        }

        return lengthFt;
    }

    private static double FeetBetween(LatLon from, LatLon to) => GeoMath.DistanceNm(from, to) * GeoMath.FeetPerNm;
}
