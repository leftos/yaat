using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Data.Airport;

/// <summary>A body part found inside a runway's hold line (<see cref="HoldLineFootprint.FirstCrossing"/>).</summary>
/// <param name="Runway">The runway whose hold line it passed.</param>
/// <param name="Part">The part.</param>
/// <param name="Point">Where the part was.</param>
public readonly record struct HoldLineCrossing(RunwayInfo Runway, AircraftBodyPart Part, LatLon Point);

/// <summary>
/// Whether an aircraft's body at a pose passes any runway's hold line, judged against the pose it starts a manoeuvre from. A
/// runway whose clearance the aircraft holds is not checked. An aircraft whose main gear or tail starts past a runway's hold
/// line (a tail not yet clear after an exit) is still on that runway: every part of it is checked against that runway only for
/// its pavement (<see cref="RunwayOccupancy.IsWithinPavement"/>), and may not enter the pavement unless it started on it. A
/// nose or wingtip alone past the line at the start (a nose stopped a little over the marking, a wingtip leading the nose where
/// a taxiway meets the runway at an angle) is checked that way for that part only; the other parts are checked against the
/// runway's hold line as usual.
///
/// <para>
/// A part is past a runway's hold line on the runway's pavement, or nearer its centreline than the runway's bars on its side
/// (<see cref="RunwayHoldBars.IsInsideBarLine"/>) within <see cref="MarkedBarReachFt"/>, measured along the runway, of one of
/// those bars: a hold line is marked where a taxiway meets the runway, so between bars, over the grass, a part nearer the
/// centreline than the bars' line is not refused.
/// </para>
/// </summary>
public sealed class HoldLineFootprint
{
    /// <summary>
    /// How far (ft), measured along the runway, from one of its marked bars on the same side of the centreline a part off the
    /// pavement can count as past that runway's hold line.
    /// </summary>
    public const double MarkedBarReachFt = 200.0;

    private readonly AirportGroundLayout _layout;
    private readonly AircraftBody _body;
    private readonly RunwayGuard[] _guards;

    /// <summary>
    /// One runway's check: its first end and the bearing from it to the second, how far along the runway from that end each of
    /// its bars lies (right of the centreline, then left), the parts judged only against its pavement, and which of them
    /// started on it.
    /// </summary>
    private sealed record RunwayGuard(RunwayInfo Runway, LatLon End1, TrueHeading Along, double[] RightBarsFt, double[] LeftBarsFt)
    {
        public HashSet<AircraftBodyPart> PavementOnly { get; init; } = [];

        public HashSet<AircraftBodyPart> StartedOnPavement { get; init; } = [];

        /// <summary>How far (ft) along the runway from its first end <paramref name="point"/> lies (negative before it).</summary>
        public double AlongFt(LatLon point) => GeoMath.AlongTrackDistanceNm(point, End1, Along) * GeoMath.FeetPerNm;
    }

    /// <summary>
    /// The footprint check of <paramref name="body"/> on <paramref name="layout"/> for a manoeuvre begun at
    /// <paramref name="start"/>.
    /// </summary>
    /// <param name="layout">The airport's ground layout: its runways and their hold-short bars.</param>
    /// <param name="body">The aircraft's body.</param>
    /// <param name="start">The pose the manoeuvre starts from: the main-gear position and heading (deg true).</param>
    /// <param name="isCleared">Whether the aircraft holds a clearance onto or across a runway, which is then not checked.</param>
    public HoldLineFootprint(
        AirportGroundLayout layout,
        AircraftBody body,
        (LatLon Position, double HeadingDeg) start,
        Func<RunwayInfo, bool> isCleared
    )
    {
        _layout = layout;
        _body = body;
        _guards = [.. RunwayOccupancy.AirportRunways(layout.AirportId).Where(runway => !isCleared(runway)).Select(runway => GuardFor(runway, start))];
    }

    /// <summary>
    /// The first of <paramref name="parts"/>, in their order, inside a checked runway's hold line for the main gear at
    /// <paramref name="position"/> facing <paramref name="headingDeg"/>, runways in the airport's order; null when none is.
    /// </summary>
    /// <param name="position">The main-gear reference position.</param>
    /// <param name="headingDeg">The heading (deg true).</param>
    /// <param name="parts">The parts to test.</param>
    /// <returns>The crossing, or null.</returns>
    public HoldLineCrossing? FirstCrossing(LatLon position, double headingDeg, IReadOnlyList<AircraftBodyPart> parts)
    {
        foreach (AircraftBodyPart part in parts)
        {
            LatLon point = _body.PointOf(part, position, headingDeg);
            foreach (RunwayGuard guard in _guards)
            {
                if (Crosses(guard, part, point))
                {
                    return new HoldLineCrossing(guard.Runway, part, point);
                }
            }
        }

        return null;
    }

    private bool Crosses(RunwayGuard guard, AircraftBodyPart part, LatLon point) =>
        guard.PavementOnly.Contains(part)
            ? (!guard.StartedOnPavement.Contains(part) && RunwayOccupancy.IsWithinPavement(point, guard.Runway))
            : IsPastHoldLine(guard, point);

    /// <summary>
    /// Whether <paramref name="point"/> is past <paramref name="guard"/>'s runway's hold line: on its pavement, or inside its bars'
    /// line within <see cref="MarkedBarReachFt"/> along the runway of one of its bars on the point's side.
    /// </summary>
    private bool IsPastHoldLine(RunwayGuard guard, LatLon point)
    {
        if (RunwayOccupancy.IsWithinPavement(point, guard.Runway))
        {
            return true;
        }

        double[] sideBarsFt = RunwayHoldBars.IsRightOfCentreline(guard.Runway, point) ? guard.RightBarsFt : guard.LeftBarsFt;
        double alongFt = guard.AlongFt(point);
        return sideBarsFt.Any(barFt => Math.Abs(barFt - alongFt) <= MarkedBarReachFt) && RunwayHoldBars.IsInsideBarLine(_layout, guard.Runway, point);
    }

    private RunwayGuard GuardFor(RunwayInfo runway, (LatLon Position, double HeadingDeg) start)
    {
        var end1 = new LatLon(runway.Lat1, runway.Lon1);
        var along = new TrueHeading(GeoMath.BearingTo(end1, new LatLon(runway.Lat2, runway.Lon2)));
        GroundNode[] bars = RunwayHoldBars.RunwayBars(_layout, runway);
        var guard = new RunwayGuard(runway, end1, along, [], []);
        guard = guard with
        {
            RightBarsFt = [.. bars.Where(b => RunwayHoldBars.IsRightOfCentreline(runway, b.Position)).Select(b => guard.AlongFt(b.Position))],
            LeftBarsFt = [.. bars.Where(b => !RunwayHoldBars.IsRightOfCentreline(runway, b.Position)).Select(b => guard.AlongFt(b.Position))],
        };

        LatLon PointOf(AircraftBodyPart part) => _body.PointOf(part, start.Position, start.HeadingDeg);
        AircraftBodyPart[] pastLine = [.. AircraftBody.AllParts.Where(part => IsPastHoldLine(guard, PointOf(part)))];
        bool stillOnRunway = IsPastHoldLine(guard, start.Position) || pastLine.Contains(AircraftBodyPart.Tail);
        return guard with
        {
            PavementOnly = [.. stillOnRunway ? AircraftBody.AllParts : pastLine],
            StartedOnPavement = [.. AircraftBody.AllParts.Where(part => RunwayOccupancy.IsWithinPavement(PointOf(part), runway))],
        };
    }
}
