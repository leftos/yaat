using Avalonia;
using Yaat.Sim;

namespace Yaat.Client.Views.Scope;

/// <summary>What a transition of the Scope's heading vector asks the canvas to do.</summary>
public enum ScopeHeadingVectorOutcome
{
    /// <summary>Nothing is to be sent or left: the vector stays up where the transition's state leaves it.</summary>
    None,

    /// <summary>The transition names the heading: send it and take the vector down.</summary>
    Confirm,

    /// <summary>The transition ends the vector without sending anything.</summary>
    Cancel,
}

/// <summary>The state a transition leaves the vector in, and what it asks for.</summary>
/// <param name="State">The vector as the transition leaves it; null once it is over.</param>
/// <param name="Outcome">What the canvas does with it.</param>
public readonly record struct ScopeHeadingVectorTransition(ScopeHeadingVectorState? State, ScopeHeadingVectorOutcome Outcome);

/// <summary>The heading vector's geometry in lat/lon, so it can be checked without a canvas.</summary>
/// <param name="HasArc">True when the change is three degrees or more at over thirty knots, so a turn arc is drawn.</param>
/// <param name="LeadEnd">
/// Where the straight roll-in lead ends and the arc starts, ahead of the aircraft on its present heading: the aircraft itself
/// when there is no arc.
/// </param>
/// <param name="ArcEnd">Where the arc ends and the straight leg starts: the aircraft itself when there is no arc.</param>
/// <param name="LegEnd">Where the straight leg ends, along the heading the vector sends.</param>
public readonly record struct ScopeHeadingVectorGeometry(bool HasArc, LatLon LeadEnd, LatLon ArcEnd, LatLon LegEnd);

/// <summary>The turn the heading vector previews.</summary>
/// <param name="TurnRateDegPerSec">The type's turn rate in degrees per second.</param>
/// <param name="RadiusNm">The arc's radius at the aircraft's ground speed, in nautical miles.</param>
/// <param name="LeadNm">The straight roll-in lead flown before the arc, in nautical miles.</param>
public readonly record struct ScopeHeadingVectorTurn(double TurnRateDegPerSec, double RadiusNm, double LeadNm);

/// <summary>
/// The elastic heading vector's state, as the Scope's pointer drives it: the aircraft and the cursor it runs toward, the press
/// point a drag is measured from, and whether the button is still held. A press arms it; after a move past
/// <see cref="DragThresholdPxSq"/> a release also more than that from the press is the confirm, and any other release (a
/// slip, or a drag back to the press) leaves the vector armed for the next left press.
/// </summary>
/// <param name="Callsign">The aircraft the vector is drawn for.</param>
/// <param name="Cursor">Where the pointer is, in lat/lon.</param>
/// <param name="DragOrigin">The press point, in canvas pixels, that a drag is measured from.</param>
/// <param name="ButtonHeld">True while the button that started the vector is still held.</param>
/// <param name="DraggedPastThreshold">True once the held pointer has moved past <see cref="DragThresholdPxSq"/>.</param>
public readonly record struct ScopeHeadingVectorState(string Callsign, LatLon Cursor, Point DragOrigin, bool ButtonHeld, bool DraggedPastThreshold)
{
    /// <summary>
    /// Pixel² movement (10 px) that separates a deliberate drag from a slipped click: the held pointer must move past it to
    /// drag, and the release must also be past it from the press to confirm.
    /// </summary>
    public const double DragThresholdPxSq = 100;

    /// <summary>Course change under which the vector draws no turn arc.</summary>
    public const double MinArcChangeDeg = 3.0;

    /// <summary>Ground speed at or under which the vector draws no turn arc.</summary>
    public const double MinArcGroundSpeedKts = 30;

    /// <summary>Starts a vector for <paramref name="callsign"/> at its press point, with the button still held.</summary>
    public static ScopeHeadingVectorState Begin(string callsign, LatLon cursor, Point dragOrigin) =>
        new(callsign, cursor, dragOrigin, ButtonHeld: true, DraggedPastThreshold: false);

    /// <summary>The vector as a move to <paramref name="cursor"/> at the canvas point (x, y) leaves it.</summary>
    public ScopeHeadingVectorState MovedTo(LatLon cursor, double x, double y)
    {
        double dx = x - DragOrigin.X;
        double dy = y - DragOrigin.Y;
        bool dragged = DraggedPastThreshold || (ButtonHeld && ((dx * dx) + (dy * dy) > DragThresholdPxSq));
        return this with { Cursor = cursor, DraggedPastThreshold = dragged };
    }

    /// <summary>
    /// The vector as a left release at <paramref name="cursor"/>, at the canvas point (x, y), leaves it: after a drag past the
    /// threshold a release more than the threshold from the press confirms; any other release (a slip, or a drag that came back
    /// to the press) arms the click-confirm.
    /// </summary>
    public ScopeHeadingVectorTransition Released(LatLon cursor, double x, double y)
    {
        ScopeHeadingVectorState moved = this with { Cursor = cursor };
        if (!ButtonHeld)
        {
            return new(moved, ScopeHeadingVectorOutcome.None);
        }

        double dx = x - DragOrigin.X;
        double dy = y - DragOrigin.Y;
        bool releasedAwayFromPress = ((dx * dx) + (dy * dy)) > DragThresholdPxSq;
        return (DraggedPastThreshold && releasedAwayFromPress)
            ? new(moved, ScopeHeadingVectorOutcome.Confirm)
            : new(moved with { ButtonHeld = false }, ScopeHeadingVectorOutcome.None);
    }

    /// <summary>
    /// The vector as an armed press at <paramref name="cursor"/> leaves it: a left press confirms toward the press point, any
    /// other cancels.
    /// </summary>
    public ScopeHeadingVectorTransition Pressed(bool left, LatLon cursor) =>
        left ? new(this with { Cursor = cursor }, ScopeHeadingVectorOutcome.Confirm) : new(null, ScopeHeadingVectorOutcome.Cancel);

    /// <summary>The magnetic heading, snapped to five degrees, from <paramref name="from"/> toward <paramref name="to"/>.</summary>
    public static int MagneticHeading(LatLon from, LatLon to) =>
        SnapHeadingTo5(MagneticDeclination.TrueToMagnetic(GeoMath.BearingTo(from, to), from));

    /// <summary>Snaps a magnetic heading to the nearest five degrees, in [5, 360]: 0 and 360 both read as 360.</summary>
    public static int SnapHeadingTo5(double magneticDeg)
    {
        int snapped = (int)(Math.Round(magneticDeg / 5.0) * 5);
        snapped = ((snapped % 360) + 360) % 360;
        return snapped == 0 ? 360 : snapped;
    }

    /// <summary>The radius of a turn at <paramref name="turnRateDegPerSec"/> at <paramref name="groundSpeedKts"/>, in nautical miles.</summary>
    public static double TurnRadiusNm(double groundSpeedKts, double turnRateDegPerSec) =>
        groundSpeedKts <= 0 ? 0 : TurnShape.RadiusNm(groundSpeedKts, turnRateDegPerSec);

    /// <summary>
    /// The turn the vector previews for a course change of <paramref name="courseChangeDeg"/> (signed, positive right): the
    /// type's turn rate, the radius at the aircraft's ground speed, and the roll-in lead from wings level under
    /// <see cref="TurnModel.Ramp"/>. The client has no session turn model on the wire, so the preview always draws Ramp.
    /// </summary>
    public static ScopeHeadingVectorTurn TurnFor(ScopeHeadingVectorOverlay overlay, double courseChangeDeg)
    {
        double rate = overlay.TurnRateDegPerSec;
        double radiusNm = TurnRadiusNm(overlay.GroundSpeed, rate);
        double leadNm = TurnShape.LeadNm(new TurnLeadInput(overlay.GroundSpeed, courseChangeDeg, rate, overlay.Category, TurnModel.Ramp));
        return new(rate, radiusNm, leadNm);
    }

    /// <summary>
    /// Where <paramref name="turn"/> for a course change of <paramref name="courseChangeDeg"/> rolls in and turns around: the
    /// lead end, the roll-in lead straight ahead of the aircraft along its present heading, and the arc's centre, the radius
    /// abeam the lead end on the turn's side.
    /// </summary>
    public static (LatLon LeadEnd, LatLon Center) TurnGeometry(ScopeHeadingVectorOverlay overlay, double courseChangeDeg, ScopeHeadingVectorTurn turn)
    {
        double side = courseChangeDeg > 0 ? 1 : -1;
        LatLon leadEnd = GeoMath.ProjectPoint(overlay.Aircraft, new TrueHeading(overlay.HeadingTrue), turn.LeadNm);
        LatLon center = GeoMath.ProjectPoint(leadEnd, new TrueHeading(overlay.HeadingTrue + (90 * side)), turn.RadiusNm);
        return (leadEnd, center);
    }

    /// <summary>
    /// The point <paramref name="t"/> of the way along the turn arc from the present heading into
    /// <paramref name="snappedHeadingTrue"/>, around the centre on the turn's side; t = 0 is the lead end, t = 1 where the arc
    /// ends.
    /// </summary>
    public static LatLon ArcPoint(ScopeHeadingVectorOverlay overlay, double snappedHeadingTrue, double t)
    {
        double change = GeoMath.SignedBearingDifference(overlay.HeadingTrue, snappedHeadingTrue);
        double side = change > 0 ? 1 : -1;
        ScopeHeadingVectorTurn turn = TurnFor(overlay, change);
        LatLon center = TurnGeometry(overlay, change, turn).Center;
        double startBearing = overlay.HeadingTrue - (90 * side);
        double endBearing = snappedHeadingTrue - (90 * side);
        double bearing = GeoMath.BlendBearings(startBearing, endBearing, t);
        return GeoMath.ProjectPoint(center, new TrueHeading(bearing), turn.RadiusNm);
    }

    /// <summary>
    /// The vector's geometry: where the turn arc ends, and where the straight leg ends. The leg starts at the arc's end and runs
    /// along <paramref name="snappedHeadingTrue"/> for the cursor's along-track distance from that start, so it follows the
    /// heading that will be sent rather than converging on the aircraft's ray; a cursor behind the arc's end leaves no leg.
    /// </summary>
    /// <param name="overlay">The vector's aircraft, present heading, ground speed, turn rate and cursor.</param>
    /// <param name="snappedHeadingTrue">The snapped true heading the vector sends.</param>
    public static ScopeHeadingVectorGeometry Geometry(ScopeHeadingVectorOverlay overlay, double snappedHeadingTrue)
    {
        double change = GeoMath.SignedBearingDifference(overlay.HeadingTrue, snappedHeadingTrue);
        bool hasArc = (Math.Abs(change) >= MinArcChangeDeg) && (overlay.GroundSpeed > MinArcGroundSpeedKts);
        LatLon leadEnd = hasArc ? TurnGeometry(overlay, change, TurnFor(overlay, change)).LeadEnd : overlay.Aircraft;
        LatLon arcEnd = hasArc ? ArcPoint(overlay, snappedHeadingTrue, 1.0) : overlay.Aircraft;
        double bearingFromArcEnd = GeoMath.BearingTo(arcEnd, overlay.Cursor);
        double cosine = Math.Cos((bearingFromArcEnd - snappedHeadingTrue) * Math.PI / 180.0);
        double alongTrackNm = GeoMath.DistanceNm(arcEnd, overlay.Cursor) * cosine;
        LatLon legEnd = GeoMath.ProjectPoint(arcEnd, new TrueHeading(snappedHeadingTrue), Math.Max(0, alongTrackNm));
        return new(hasArc, leadEnd, arcEnd, legEnd);
    }

    /// <summary>The time to fly <paramref name="distNm"/> at <paramref name="groundSpeedKts"/> as m:ss, or dashes below a knot.</summary>
    public static string FormatEta(double distNm, double groundSpeedKts)
    {
        if (groundSpeedKts < 1)
        {
            return "--:--";
        }

        double seconds = distNm / groundSpeedKts * 3600;
        int totalSec = (int)Math.Round(seconds);
        return $"{totalSec / 60:D1}:{totalSec % 60:D2}";
    }
}
