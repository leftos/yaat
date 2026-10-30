using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Phases.Ground;

/// <summary>
/// The one tail-clear margin past a runway holding position marking: a runway exit, a bare runway crossing and a
/// <c>CLRWY</c> stop all leave every part of the aircraft this far past the marking (AIM 4-3-21.b). A runway exit and a
/// <c>CLRWY</c> stop give the margin up, and if need be the tail's clearance too, before the nose passes another runway's
/// holding position marking (<see cref="StopPastBar"/>).
/// </summary>
public static class RunwayTailClearance
{
    /// <summary>
    /// The margin (ft) past the runway holding position marking every runway exit, bare crossing and <c>CLRWY</c> stop keeps,
    /// so all parts of the aircraft are past the marking (AIM 4-3-21.b) rather than the tail on the line itself.
    /// </summary>
    public const double MarginFt = 10.0;

    /// <summary>
    /// How far (ft) past the hold-short node the aircraft's reference point stops so its tail is <see cref="MarginFt"/> past
    /// the marking: half of <paramref name="aircraftLengthFt"/> plus the margin.
    /// </summary>
    public static double PastBarFt(double aircraftLengthFt) => (aircraftLengthFt / 2.0) + MarginFt;

    /// <summary>
    /// How far (ft) a cut-back stop must still put the reference point past the cleared runway's bar, beyond half the aircraft,
    /// for the tail to count as clear of that runway's marking: the navigator may come to rest up to its final arrival threshold
    /// (<see cref="GroundNavigator.FinalNodeArrivalThresholdNm"/>, about 1.8 ft) short of the stop, plus 1 ft.
    /// </summary>
    public const double TailClearToleranceFt = (GroundNavigator.FinalNodeArrivalThresholdNm * GeoMath.FeetPerNm) + 1.0;

    /// <summary>Where a runway exit or a <c>CLRWY</c> stops past the cleared runway's bar.</summary>
    /// <param name="Target">The virtual node the reference point stops at.</param>
    /// <param name="PastBarFt">How far past the cleared runway's bar the reference point stops, along the graph (ft).</param>
    /// <param name="HeldShortOfBar">The other runway's hold-short node the nose stops at, when the stop was cut back for it.</param>
    /// <param name="TailInsideLine">
    /// True when the cut-back stop leaves the tail inside the cleared runway's holding position marking, or within
    /// <see cref="TailClearToleranceFt"/> of it: the aircraft has not cleared that runway.
    /// </param>
    public sealed record Stop(GroundNode Target, double PastBarFt, GroundNode? HeldShortOfBar, bool TailInsideLine);

    /// <summary>
    /// The stop past <paramref name="bar"/>, a hold-short node of the runway being cleared, reached from
    /// <paramref name="approachFrom"/>: <see cref="PastBarFt(double)"/> along the graph, cut back so the nose stays at or short of
    /// any other runway's holding position marking on the way (<see cref="VirtualNode.OffsetPastShortOfOtherRunways"/>).
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="bar">The cleared runway's hold-short node.</param>
    /// <param name="approachFrom">The node behind it, on the runway side.</param>
    /// <param name="aircraftLengthFt">The aircraft's length (ft).</param>
    /// <returns>The stop.</returns>
    public static Stop StopPastBar(AirportGroundLayout layout, GroundNode bar, GroundNode approachFrom, double aircraftLengthFt)
    {
        double pastBarFt = PastBarFt(aircraftLengthFt);
        double pastBarNm = pastBarFt / GeoMath.FeetPerNm;
        if (bar.RunwayId is not { } clearedRunway)
        {
            return new Stop(VirtualNode.OffsetPast(layout, bar, approachFrom, pastBarNm), pastBarFt, null, false);
        }

        VirtualNode.ClampedOffset offset = VirtualNode.OffsetPastShortOfOtherRunways(
            layout,
            bar,
            approachFrom,
            pastBarNm,
            new VirtualNode.NoseClamp(clearedRunway, aircraftLengthFt)
        );
        double offsetFt = offset.OffsetNm * GeoMath.FeetPerNm;
        bool tailInside = (offset.HeldShortOfBar is not null) && (offsetFt < ((aircraftLengthFt / 2.0) + TailClearToleranceFt));
        return new Stop(offset.Target, offsetFt, offset.HeldShortOfBar, tailInside);
    }

    /// <summary>
    /// Whether an exit along <paramref name="path"/> (branch point to the exit's bar, the bar last) stops an aircraft of
    /// <paramref name="aircraftType"/> with its tail inside the runway's holding position marking: the stop
    /// <see cref="RunwayExitPhase"/> builds past that bar (<see cref="StopPastBar"/>) is <see cref="Stop.TailInsideLine"/>.
    /// False for a path with no edge, which has no direction to stop along.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="path">The exit path, ending at the exit's bar.</param>
    /// <param name="aircraftType">The aircraft type, whose length (<see cref="AircraftLength.ResolveFt"/>) sets the stop.</param>
    /// <returns>True when the tail stays inside the runway's marking.</returns>
    public static bool ExitStopsTailInside(AirportGroundLayout layout, IReadOnlyList<GroundNode> path, string aircraftType) =>
        (path.Count >= 2) && StopPastBar(layout, path[^1], path[^2], AircraftLength.ResolveFt(aircraftType)).TailInsideLine;

    /// <summary>
    /// True when an automatic exit choice should pass <paramref name="candidate"/> by: the controller did not name its taxiway,
    /// and on the walk the stop itself takes from the exit's bar on the runway being vacated
    /// (<see cref="VirtualNode.FirstOtherRunwayBarAhead"/>) another runway's bar lies closer than the aircraft plus
    /// <see cref="MarginFt"/>, so it could not stop there clear of both runways. A walk that meets no other runway's bar fits.
    /// </summary>
    /// <param name="candidate">The exit the search found.</param>
    /// <param name="aircraft">The vacating aircraft.</param>
    /// <returns>True to skip the candidate.</returns>
    public static bool IsTooShortForAutomaticExit(AirportGroundLayout.CenterlineExitResult candidate, AircraftState aircraft)
    {
        if (
            string.Equals(aircraft.Phases?.RequestedExit?.Taxiway, candidate.Taxiway, StringComparison.OrdinalIgnoreCase)
            || (candidate.Path.Count < 2)
            || (candidate.HoldShort.RunwayId is not { } vacatedRunway)
        )
        {
            return false;
        }

        double neededNm = (AircraftLength.ResolveFt(aircraft.AircraftType) + MarginFt) / GeoMath.FeetPerNm;
        return (VirtualNode.FirstOtherRunwayBarAhead(candidate.HoldShort, candidate.Path[^2].Position, neededNm, vacatedRunway) is { } bar)
            && (bar.DistanceNm < neededNm);
    }

    /// <summary>
    /// The end of the runway <paramref name="bar"/> holds short of, as the pilot names it: an active runway by its active end
    /// (<see cref="RunwayCrossingEnd.Spoken"/>); otherwise the end whose heading is nearest <paramref name="reference"/> (the
    /// runway being cleared) when the two are within 45° (a parallel), else the end nearest the aircraft.
    /// </summary>
    /// <param name="layout">The airport's ground layout.</param>
    /// <param name="bar">The other runway's hold-short node.</param>
    /// <param name="reference">The runway end being cleared, when known.</param>
    /// <param name="aircraft">The aircraft holding short.</param>
    /// <param name="activeRunways">The room's active runways.</param>
    /// <returns>The display designator.</returns>
    public static string HeldShortRunwayEnd(
        AirportGroundLayout layout,
        GroundNode bar,
        RunwayInfo? reference,
        AircraftState aircraft,
        ActiveRunways activeRunways
    )
    {
        RunwayIdentifier other = bar.RunwayId ?? throw new ArgumentException($"node {bar.Id} is not a runway hold-short", nameof(bar));
        NavigationDatabase? db = NavigationDatabase.InstanceOrNull;
        RunwayInfo? end1 = db?.GetRunway(layout.AirportId, other.End1);
        RunwayInfo? end2 = db?.GetRunway(layout.AirportId, other.End2);
        if ((reference is null) || (end1 is null) || (end2 is null) || RunwayCrossingEnd.HasActiveEnd(other, layout.AirportId, activeRunways))
        {
            return RunwayCrossingEnd.Spoken(aircraft, other.ToString(), layout, activeRunways);
        }

        TrueHeading heading = reference.TrueHeading;
        RunwayInfo aligned = (heading.AbsAngleTo(end1.TrueHeading) <= heading.AbsAngleTo(end2.TrueHeading)) ? end1 : end2;
        return (heading.AbsAngleTo(aligned.TrueHeading) <= 45.0)
            ? RunwayIdentifier.ToDisplayDesignator(aligned.Designator)
            : RunwayCrossingEnd.Spoken(aircraft, other.ToString(), layout, activeRunways);
    }
}
