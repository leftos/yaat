using Yaat.Sim.Data.Airport;

namespace Yaat.Sim;

/// <summary>Where a swept outline first falls through the clearance floor.</summary>
/// <param name="SampleIndex">The index of the first fouled sample in the swept path.</param>
/// <param name="AlongFt">How far along the path that sample sits, feet.</param>
/// <param name="ClearanceFt">The clearance at that sample, feet.</param>
/// <param name="CrossingAlongFt">How far along the path the clearance crosses the floor, interpolated between the last clear sample and it.</param>
internal readonly record struct GroundOutlineFoul(int SampleIndex, double AlongFt, double ClearanceFt, double CrossingAlongFt);

/// <summary>What sweeping a mover's outline along a path against one neighbour found.</summary>
/// <param name="StartClearanceFt">The clearance at the pose the floor is anchored to, feet.</param>
/// <param name="FloorFt">The floor the sweep was judged against, feet.</param>
/// <param name="SampleCount">How many samples the path carried.</param>
/// <param name="ClosestFt">The closest the outlines came over the samples that were measured, feet; null when none was in reach.</param>
/// <param name="Foul">Where the clearance first fell through the floor, or null when the whole path clears it.</param>
internal readonly record struct GroundOutlineSweepResult(
    double StartClearanceFt,
    double FloorFt,
    int SampleCount,
    double? ClosestFt,
    GroundOutlineFoul? Foul
);

/// <summary>
/// Sweeps a tug-moved aircraft's <see cref="GroundOutline"/> along a path against a parked or held neighbour's, and
/// says where it first comes closer than the move is allowed to. One body, two callers: <see cref="TugMovePlanner"/>
/// judges a candidate with it before the move is planned, and <see cref="GroundConflictDetector"/> judges the move
/// under way with it. A planner that measured this any other way would accept a swing the detector then dead-stops
/// mid-manoeuvre.
/// </summary>
internal static class GroundOutlineSweep
{
    /// <summary>
    /// The clearance a sweep may not fall through, feet: <see cref="GroundConflictDetector.WingtipBufferFt"/>, or — for
    /// a neighbour the move already starts closer to than that, as the aircraft on the next stand usually is — no
    /// closer than it started, less <see cref="GroundConflictDetector.OutlineClearanceSlackFt"/>. Never below that
    /// slack, so contact is never passable.
    ///
    /// <para>The floor this works out to — 24.5 ft for the pair of E75Ls on adjacent SFO gates, off
    /// <see cref="GroundConflictDetector.WingtipBufferFt"/>'s 25 ft — sits between AC 150/5300-13B's taxilane-to-object
    /// wingtip allowance (0.1 W + 10 ft, about 20.2 ft for an ADG-III E75L) and its taxilane-to-taxilane allowance
    /// (0.1 W + 20 ft, about 30.2 ft). The planner therefore refuses swings the object standard would permit, and that
    /// is deliberate: a tow past a parked aircraft is walked, not flown down a design taxilane, and AC 00-65A §11.9 puts
    /// the swing in the wing walkers' judgement before the move rather than on a design clearance. The AC 150/5300-13B
    /// figures are quoted from memory; that AC is not on disk here.</para>
    /// </summary>
    /// <param name="startClearanceFt">The clearance at the pose the floor is anchored to, feet.</param>
    /// <returns>The floor, feet.</returns>
    internal static double FloorFt(double startClearanceFt) =>
        Math.Max(
            GroundConflictDetector.OutlineClearanceSlackFt,
            Math.Min(GroundConflictDetector.WingtipBufferFt, startClearanceFt) - GroundConflictDetector.OutlineClearanceSlackFt
        );

    /// <summary>
    /// Sweeps <paramref name="path"/> against one neighbour. The floor is anchored to <paramref name="startPose"/> —
    /// where the move began, not where it has got to — because a floor read off the live pose follows the mover down
    /// and ratchets it into contact a foot at a time. A sample whose reference point is far enough away that no part of
    /// either outline can be under the floor is not measured.
    /// </summary>
    /// <param name="path">The poses to sweep, each with how far along the path it sits, feet.</param>
    /// <param name="startPose">The pose the move began at; the floor is anchored to its clearance.</param>
    /// <param name="frame">The flat frame both outlines are built in.</param>
    /// <param name="moverSize">The mover's outline size, with the tug's lead when it is being pulled.</param>
    /// <param name="obstaclePosition">Where the neighbour stands.</param>
    /// <param name="obstacleNoseTrueDeg">The neighbour's nose heading, degrees true.</param>
    /// <param name="obstacleSize">The neighbour's outline size.</param>
    /// <returns>The floor, the closest approach, and where the path first falls through the floor.</returns>
    internal static GroundOutlineSweepResult Sweep(
        IReadOnlyList<(TugPose Pose, double AlongFt)> path,
        TugPose startPose,
        GroundOutlineFrame frame,
        GroundOutlineSize moverSize,
        LatLon obstaclePosition,
        double obstacleNoseTrueDeg,
        GroundOutlineSize obstacleSize
    )
    {
        OutlinePoint obstacleCentre = frame.ToLocal(obstaclePosition);
        var obstacleOutline = GroundOutline.At(obstacleCentre, obstacleNoseTrueDeg, obstacleSize);
        double startFt = ClearanceAt(startPose, frame, moverSize, obstacleOutline);
        double floorFt = FloorFt(startFt);
        double reachFt = moverSize.ReachFt + obstacleSize.ReachFt;
        double closestFt = double.MaxValue;
        for (int i = 0; i < path.Count; i++)
        {
            (TugPose pose, double alongFt) = path[i];
            if ((OutlinePoint.Distance(frame.ToLocal(pose.Position), obstacleCentre) - reachFt) >= floorFt)
            {
                continue;
            }

            double clearanceFt = ClearanceAt(pose, frame, moverSize, obstacleOutline);
            closestFt = Math.Min(closestFt, clearanceFt);
            if (clearanceFt >= floorFt)
            {
                continue;
            }

            (TugPose Pose, double AlongFt) previous = path[Math.Max(0, i - 1)];
            double previousClearanceFt = ClearanceAt(previous.Pose, frame, moverSize, obstacleOutline);
            double crossingFt = FloorCrossingAlongFt(floorFt, (previous.AlongFt, previousClearanceFt), (alongFt, clearanceFt));
            return new GroundOutlineSweepResult(startFt, floorFt, path.Count, closestFt, new GroundOutlineFoul(i, alongFt, clearanceFt, crossingFt));
        }

        return new GroundOutlineSweepResult(startFt, floorFt, path.Count, closestFt < double.MaxValue ? closestFt : null, null);
    }

    /// <summary>The mover's outline clearance from the neighbour at one sample of its path, feet.</summary>
    /// <param name="pose">The sample's pose.</param>
    /// <param name="frame">The flat frame the outlines are built in.</param>
    /// <param name="moverSize">The mover's outline size.</param>
    /// <param name="obstacleOutline">The neighbour's outline, in the same frame.</param>
    /// <returns>The clearance, feet.</returns>
    private static double ClearanceAt(TugPose pose, GroundOutlineFrame frame, GroundOutlineSize moverSize, GroundOutline obstacleOutline) =>
        GroundOutline.Clearance(GroundOutline.At(frame.ToLocal(pose.Position), pose.NoseTrueDeg, moverSize), obstacleOutline);

    /// <summary>
    /// How far along the path the clearance falls through <paramref name="floorFt"/> between the last sample that was
    /// clear of it (<paramref name="from"/>) and the first that is under it (<paramref name="to"/>), feet: the two
    /// samples' along-distances interpolated at the crossing.
    ///
    /// <para>The samples are a few feet apart and the simulation is redone every couple of feet of travel, so the along
    /// distance of the first fouled <em>sample</em> steps down in jumps of the sample spacing and back up whenever the
    /// grid moves. The braking limit is read off that distance, so those jumps would land in the tug's speed. The
    /// crossing point is a property of the path, not of the grid it was sampled on, and moves smoothly as the mover
    /// closes.</para>
    ///
    /// <para><paramref name="to"/>'s own along-distance is the answer when the clearance does not fall between the two
    /// — the live pose itself already fouling the neighbour, which is <c>from</c> and <c>to</c> at once.</para>
    /// </summary>
    /// <param name="floorFt">The floor the clearance falls through, feet.</param>
    /// <param name="from">The last clear sample's along-distance and clearance, feet.</param>
    /// <param name="to">The first fouled sample's along-distance and clearance, feet.</param>
    /// <returns>How far along the path the crossing sits, feet.</returns>
    private static double FloorCrossingAlongFt(double floorFt, (double AlongFt, double ClearanceFt) from, (double AlongFt, double ClearanceFt) to)
    {
        if ((from.ClearanceFt <= to.ClearanceFt) || (from.ClearanceFt < floorFt))
        {
            return to.AlongFt;
        }

        double fraction = (from.ClearanceFt - floorFt) / (from.ClearanceFt - to.ClearanceFt);
        return from.AlongFt + (fraction * (to.AlongFt - from.AlongFt));
    }
}
