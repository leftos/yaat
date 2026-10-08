using Yaat.Sim.Data.Airport;
using Yaat.Sim.Simulation.Snapshots;

namespace Yaat.Sim;

/// <summary>
/// Where a tow began and which way its first move went: the pose the row anchor of
/// <see cref="GroundOutlineSweep.FloorFt"/> slides the mover's outline from, and the direction it slides in. Set when a
/// tow is installed — off a stand or from wherever a new tug instruction finds the aircraft — and carried unchanged
/// through every later move of the same tow (continuations, a mid-push re-plan), so every move of it is judged against
/// the same row.
/// </summary>
/// <param name="TowStartPose">The aircraft's pose where the tow began.</param>
/// <param name="FirstKind">The tow's first move: a push slides the outline aft, a pull forward.</param>
public readonly record struct TugRowAnchor(TugPose TowStartPose, PushbackLegKind FirstKind)
{
    /// <summary>The anchor as its snapshot DTO.</summary>
    /// <returns>The DTO.</returns>
    public TugRowAnchorDto ToSnapshot() =>
        new()
        {
            Latitude = TowStartPose.Position.Lat,
            Longitude = TowStartPose.Position.Lon,
            NoseTrueDeg = TowStartPose.NoseTrueDeg,
            FirstKind = FirstKind,
        };

    /// <summary>The anchor a snapshot DTO carries.</summary>
    /// <param name="dto">The DTO.</param>
    /// <returns>The anchor.</returns>
    public static TugRowAnchor FromSnapshot(TugRowAnchorDto dto) =>
        new(new TugPose(new LatLon(dto.Latitude, dto.Longitude), dto.NoseTrueDeg), dto.FirstKind);
}

/// <summary>
/// The row clearances (<see cref="GroundOutlineSweep.RowClearanceFt"/>) measured so far, by row anchor and neighbour:
/// each is a pure function of the anchor, the two footprints and the neighbour's pose, so it is measured once and reused
/// for as long as those stay the same, and measured again when any of them changes. Never snapshotted — a restored
/// tow measures it again and gets the same figure.
/// </summary>
internal sealed class TugRowClearances
{
    private readonly record struct Measured(AircraftFootprint Mover, TugPose NeighbourPose, AircraftFootprint Neighbour, double? RowClearanceFt);

    private readonly Dictionary<(TugRowAnchor Anchor, string Neighbour), Measured> _measured = [];

    /// <summary>The row clearance against one neighbour, measured once for these inputs (<see cref="GroundOutlineSweep.RowClearanceFt"/>).</summary>
    /// <param name="anchor">Where the tow began and its first move's kind.</param>
    /// <param name="mover">The towed aircraft's dimensions.</param>
    /// <param name="neighbourCallsign">The neighbour's callsign, which keys it.</param>
    /// <param name="neighbourPose">Where the neighbour stands, with its nose.</param>
    /// <param name="neighbour">The neighbour's dimensions.</param>
    /// <returns>The row clearance, feet, or null when it does not count.</returns>
    internal double? RowClearanceFt(
        TugRowAnchor anchor,
        AircraftFootprint mover,
        string neighbourCallsign,
        TugPose neighbourPose,
        AircraftFootprint neighbour
    )
    {
        if (
            _measured.TryGetValue((anchor, neighbourCallsign), out Measured measured)
            && (measured.Mover == mover)
            && (measured.NeighbourPose == neighbourPose)
            && (measured.Neighbour == neighbour)
        )
        {
            return measured.RowClearanceFt;
        }

        double? rowFt = GroundOutlineSweep.RowClearanceFt(anchor, mover, neighbourPose, neighbour);
        _measured[(anchor, neighbourCallsign)] = new Measured(mover, neighbourPose, neighbour, rowFt);
        return rowFt;
    }

    /// <summary>Forgets every measurement.</summary>
    internal void Clear() => _measured.Clear();
}

/// <summary>Where a swept outline first falls through the clearance floor.</summary>
/// <param name="SampleIndex">The index of the first fouled sample in the swept path.</param>
/// <param name="AlongFt">How far along the path that sample sits, feet.</param>
/// <param name="ClearanceFt">The clearance at that sample, feet.</param>
/// <param name="CrossingAlongFt">How far along the path the clearance crosses the floor, interpolated between the last clear sample and it.</param>
internal readonly record struct GroundOutlineFoul(int SampleIndex, double AlongFt, double ClearanceFt, double CrossingAlongFt);

/// <summary>What sweeping a mover's outline along a path against one neighbour found.</summary>
/// <param name="StartClearanceFt">The clearance at the pose the floor is anchored to, feet.</param>
/// <param name="RowClearanceFt">
/// The row anchor's clearance (<see cref="GroundOutlineSweep.RowClearanceFt"/>), feet; null when it does not count.
/// </param>
/// <param name="FloorFt">The floor the sweep was judged against, feet.</param>
/// <param name="SampleCount">How many samples the path carried.</param>
/// <param name="ClosestFt">The closest the outlines came over the samples that were measured, feet; null when none was in reach.</param>
/// <param name="Foul">Where the clearance first fell through the floor, or null when the whole path clears it.</param>
internal readonly record struct GroundOutlineSweepResult(
    double StartClearanceFt,
    double? RowClearanceFt,
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
    /// Wingtip room left between two aircraft passing abeam, on top of the pair's half-wingspans
    /// (<see cref="GroundConflictDetector.RequiredLateralClearanceFt"/>). A detector-frame figure, deliberately below the
    /// AC 150/5300-13B design wingtip allowance (0.2 W + 20 ft on a taxiway, 0.1 W + 20 ft on a taxilane): the design value
    /// buys centreline-tracking error the sim does not have, and importing it would ask 160.9 ft of SFO's 160 ft A/B spacing
    /// and hold every parallel-lane pass. 7110.65 has no taxiway-separation paragraph; its §3-1-1 NOTE, AIM 4-3-18.b and
    /// AIM 2-3-4.b.1 ("being centered on the taxiway centerline does not guarantee wingtip clearance") put wingtip avoidance
    /// on the pilot.
    /// </summary>
    public const double WingtipBufferFt = 25.0;

    /// <summary>
    /// Slack under the "no closer than the move started" floor of <see cref="GroundConflictDetector.TugMoveFoulsParkedAt"/>,
    /// feet: sampling and rounding noise between the live outline and the path's samples, not room. It is also the floor's
    /// own lower bound, so the floor never goes to zero or below and a mover whose outline already touches a neighbour's is
    /// held rather than released. The command-time refusal of a tug move that starts inside a neighbour reads contact by the
    /// same slack.
    /// </summary>
    public const double OutlineClearanceSlackFt = 0.5;

    /// <summary>
    /// The least row clearance the row anchor of <see cref="FloorFt"/> counts, feet [J]: about ICAO Annex 14's smallest
    /// stand clearance. Under it the floor stays where the move started (OAK 26 → 27, a 6.2 ft row, still holds).
    /// </summary>
    public const double RowAnchorMinFt = 10.0;

    /// <summary>
    /// How far a neighbour's nose may point from the mover's nose where the tow began for the row anchor of <see cref="FloorFt"/> to
    /// count it, degrees [J]: the same direction, not modulo 180, so a nose-to-tail neighbour is not in the row.
    /// </summary>
    public const double RowAnchorNoseToleranceDeg = 10.0;

    /// <summary>
    /// The step the row anchor slides the mover's outline in, feet [J]: the slide's least clearance is read within about
    /// this much travel of where it really is, which over a wingtip passing abeam moves it by well under the
    /// <see cref="OutlineClearanceSlackFt"/>.
    /// </summary>
    public const double RowSlideStepFt = 1.0;

    /// <summary>
    /// The clearance a sweep may not fall through, feet: <see cref="WingtipBufferFt"/>, or — for a neighbour the move
    /// already starts closer to than that, as the aircraft on the next stand usually is, or one in the mover's own row
    /// that its wingtips pass closer than that — the least of where it started and the row clearance, less
    /// <see cref="OutlineClearanceSlackFt"/>. Never below that slack, so contact is never passable. In full,
    /// <c>max(0.5, min(25, start, row) − 0.5)</c>, for every tug move, turning ones included: a swing closer than the row
    /// lets the wings pass still falls through it.
    ///
    /// <para>The row anchor (<see cref="RowClearanceFt"/>) is the least clearance the mover's outline, at the pose the
    /// tow started from (<see cref="TugRowAnchor"/>), keeps from the neighbour as it slides along its own nose axis in the
    /// tow's first move's direction only — aft for a push, forward for a pull — from 0 past the neighbour's abeam point
    /// by the two outlines' reaches, and is carried unchanged through every later move of the same tow. A stand staggered
    /// further back in a row of parallel stands starts further off than the row's wingtip gap, and a straight push
    /// past it passes it abeam at that gap and never closer; anchored to the start alone, that push was held at the
    /// stand forever. The anchor counts only a neighbour whose nose is within <see cref="RowAnchorNoseToleranceDeg"/> of
    /// the mover's nose where the tow began, in the same direction [J], and only a row clearance of at least
    /// <see cref="RowAnchorMinFt"/> [J]; otherwise the floor is anchored to the start alone. A neighbour on the push line
    /// overlaps the slide and keeps that floor. [J] marks a judgement call.</para>
    ///
    /// <para>The floor this works out to — 24.5 ft for the pair of E75Ls on adjacent SFO gates, off
    /// <see cref="WingtipBufferFt"/>'s 25 ft — sits between AC 150/5300-13B's taxilane-to-object
    /// wingtip allowance (0.1 W + 10 ft, about 20.2 ft for an ADG-III E75L) and its taxilane-to-taxilane allowance
    /// (0.1 W + 20 ft, about 30.2 ft). The planner therefore refuses swings the object standard would permit, and that
    /// is deliberate: a tow past a parked aircraft is walked, not flown down a design taxilane, and AC 00-65A §11.9 puts
    /// the swing in the wing walkers' judgement before the move rather than on a design clearance. The AC 150/5300-13B
    /// figures are quoted from memory; that AC is not on disk here.</para>
    /// </summary>
    /// <param name="anchorClearanceFt">
    /// The clearance the floor is anchored to, feet: the clearance at the move's start, or the row clearance when the row
    /// anchor counts and is the smaller.
    /// </param>
    /// <returns>The floor, feet.</returns>
    internal static double FloorFt(double anchorClearanceFt) =>
        Math.Max(OutlineClearanceSlackFt, Math.Min(WingtipBufferFt, anchorClearanceFt) - OutlineClearanceSlackFt);

    /// <summary>
    /// <see cref="FloorFt"/> anchored to the clearance where the move started and to the row clearance when it counts:
    /// the one floor <see cref="Sweep"/> and <see cref="TowStartFloorFt"/> both judge by.
    /// </summary>
    /// <param name="startClearanceFt">The clearance where the move started, feet.</param>
    /// <param name="rowClearanceFt">The row clearance (<see cref="RowClearanceFt"/>), feet, or null when it does not count.</param>
    /// <returns>The floor, feet.</returns>
    internal static double AnchoredFloorFt(double startClearanceFt, double? rowClearanceFt) =>
        FloorFt(Math.Min(startClearanceFt, rowClearanceFt ?? double.MaxValue));

    /// <summary>
    /// Sweeps <paramref name="path"/> against one neighbour. The floor is anchored to <paramref name="startPose"/> —
    /// where the move began, not where it has got to — because a floor read off the live pose follows the mover down
    /// and ratchets it into contact a foot at a time; and, through <paramref name="rowClearanceFt"/>, to the row the tow
    /// began in (<see cref="AnchoredFloorFt"/>). A sample whose reference point is far enough away that no part of
    /// either outline can be under the floor is not measured.
    /// </summary>
    /// <param name="path">The poses to sweep, each with how far along the path it sits, feet.</param>
    /// <param name="startPose">The pose the move began at; the floor is anchored to its clearance.</param>
    /// <param name="rowClearanceFt">
    /// The tow's row clearance from this neighbour (<see cref="RowClearanceFt"/>), feet, or null for a floor anchored to
    /// the start alone.
    /// </param>
    /// <param name="frame">The flat frame both outlines are built in.</param>
    /// <param name="moverSize">The mover's outline size, with the tug's lead when it is being pulled.</param>
    /// <param name="obstaclePosition">Where the neighbour stands.</param>
    /// <param name="obstacleNoseTrueDeg">The neighbour's nose heading, degrees true.</param>
    /// <param name="obstacleSize">The neighbour's outline size.</param>
    /// <returns>The floor, the closest approach, and where the path first falls through the floor.</returns>
    internal static GroundOutlineSweepResult Sweep(
        IReadOnlyList<(TugPose Pose, double AlongFt)> path,
        TugPose startPose,
        double? rowClearanceFt,
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
        double floorFt = AnchoredFloorFt(startFt, rowClearanceFt);
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
            var foul = new GroundOutlineFoul(i, alongFt, clearanceFt, crossingFt);
            return new GroundOutlineSweepResult(startFt, rowClearanceFt, floorFt, path.Count, closestFt, foul);
        }

        double? measuredClosestFt = closestFt < double.MaxValue ? closestFt : null;
        return new GroundOutlineSweepResult(startFt, rowClearanceFt, floorFt, path.Count, measuredClosestFt, null);
    }

    /// <summary>
    /// The floor a tow's first move is held to against one neighbour, feet: <see cref="FloorFt"/> anchored to the
    /// clearance where the tow began and to the row (<see cref="RowClearanceFt"/>), the figures <see cref="Sweep"/> works
    /// out for that move from the same anchor.
    /// </summary>
    /// <param name="rowAnchor">Where the tow began and its first move's kind.</param>
    /// <param name="mover">The towed aircraft's dimensions.</param>
    /// <param name="neighbourPose">Where the neighbour stands, with its nose.</param>
    /// <param name="neighbour">The neighbour's dimensions.</param>
    /// <returns>The floor, feet.</returns>
    internal static double TowStartFloorFt(TugRowAnchor rowAnchor, AircraftFootprint mover, TugPose neighbourPose, AircraftFootprint neighbour)
    {
        var frame = new GroundOutlineFrame(rowAnchor.TowStartPose.Position);
        var moverSize = GroundOutlineSize.Of(mover, towedNoseFirst: rowAnchor.FirstKind == PushbackLegKind.Pull);
        var neighbourSize = GroundOutlineSize.Of(neighbour, towedNoseFirst: false);
        var neighbourOutline = GroundOutline.At(frame.ToLocal(neighbourPose.Position), neighbourPose.NoseTrueDeg, neighbourSize);
        double startFt = ClearanceAt(rowAnchor.TowStartPose, frame, moverSize, neighbourOutline);
        return AnchoredFloorFt(startFt, RowClearanceFt(rowAnchor, mover, neighbourPose, neighbour));
    }

    /// <summary>
    /// The row anchor's clearance, feet (<see cref="FloorFt"/>): the least clearance the mover's outline — no tug lead —
    /// keeps from the neighbour's as it slides from <see cref="TugRowAnchor.TowStartPose"/> along its own nose axis, aft
    /// for a tow whose first move is a push and forward for a pull, in steps of <see cref="RowSlideStepFt"/>, as far as
    /// the neighbour's abeam point along that axis plus the two outlines' reaches, so a neighbour staggered however far
    /// back is measured where the wings pass. A step whose reference point is too far off to come closer than the least
    /// clearance found so far is not measured. Null when it does not count: the neighbour's nose is more than
    /// <see cref="RowAnchorNoseToleranceDeg"/> off the mover's, or the slide comes closer than
    /// <see cref="RowAnchorMinFt"/> — as it does for a neighbour on the push line, which it overlaps. Measured in a frame
    /// on the tow's start, so the figure depends on its inputs alone and the same inputs always give the same figure
    /// (<see cref="TugRowClearances"/>).
    /// </summary>
    /// <param name="anchor">Where the tow began and its first move's kind.</param>
    /// <param name="mover">The towed aircraft's dimensions.</param>
    /// <param name="neighbourPose">Where the neighbour stands, with its nose.</param>
    /// <param name="neighbour">The neighbour's dimensions.</param>
    /// <returns>The row clearance, feet, or null when it does not count.</returns>
    internal static double? RowClearanceFt(TugRowAnchor anchor, AircraftFootprint mover, TugPose neighbourPose, AircraftFootprint neighbour)
    {
        TugPose start = anchor.TowStartPose;
        if (new TrueHeading(start.NoseTrueDeg).AbsAngleTo(new TrueHeading(neighbourPose.NoseTrueDeg)) > RowAnchorNoseToleranceDeg)
        {
            return null;
        }

        var frame = new GroundOutlineFrame(start.Position);
        var moverSize = GroundOutlineSize.Of(mover, towedNoseFirst: false);
        var neighbourSize = GroundOutlineSize.Of(neighbour, towedNoseFirst: false);
        OutlinePoint neighbourCentre = frame.ToLocal(neighbourPose.Position);
        var neighbourOutline = GroundOutline.At(neighbourCentre, neighbourPose.NoseTrueDeg, neighbourSize);
        double slideRad = (anchor.FirstKind == PushbackLegKind.Push ? start.NoseTrueDeg + 180.0 : start.NoseTrueDeg) * Math.PI / 180.0;
        var slideStep = new OutlinePoint(Math.Sin(slideRad), Math.Cos(slideRad));
        OutlinePoint origin = frame.ToLocal(start.Position);
        OutlinePoint toNeighbour = neighbourCentre - origin;
        double abeamFt = Math.Max(0.0, (toNeighbour.EastFt * slideStep.EastFt) + (toNeighbour.NorthFt * slideStep.NorthFt));
        double reachFt = moverSize.ReachFt + neighbourSize.ReachFt;
        double slideFt = abeamFt + reachFt;
        int steps = (int)Math.Ceiling(slideFt / RowSlideStepFt);
        double leastFt = double.MaxValue;
        for (int i = 0; i <= steps; i++)
        {
            OutlinePoint centre = origin + (Math.Min(i * RowSlideStepFt, slideFt) * slideStep);
            if ((OutlinePoint.Distance(centre, neighbourCentre) - reachFt) >= leastFt)
            {
                continue;
            }

            leastFt = Math.Min(leastFt, GroundOutline.Clearance(GroundOutline.At(centre, start.NoseTrueDeg, moverSize), neighbourOutline));
            if (leastFt < RowAnchorMinFt)
            {
                return null;
            }
        }

        return leastFt;
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
