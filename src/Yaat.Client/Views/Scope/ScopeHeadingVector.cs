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
/// <param name="ArcEnd">Where the arc ends and the straight leg starts: the aircraft itself when there is no arc.</param>
/// <param name="LegEnd">Where the straight leg ends, along the heading the vector sends.</param>
public readonly record struct ScopeHeadingVectorGeometry(bool HasArc, LatLon ArcEnd, LatLon LegEnd);

/// <summary>
/// The elastic heading vector's state, as the Scope's pointer drives it: the aircraft and the cursor it runs toward, the press
/// point a drag is measured from, and whether the button is still held. A press arms it; a move past
/// <see cref="DragThresholdPxSq"/> makes the release the confirm, and a release short of it leaves the vector armed for the
/// next left press.
/// </summary>
/// <param name="Callsign">The aircraft the vector is drawn for.</param>
/// <param name="Cursor">Where the pointer is, in lat/lon.</param>
/// <param name="DragOrigin">The press point, in canvas pixels, that a drag is measured from.</param>
/// <param name="ButtonHeld">True while the button that started the vector is still held.</param>
/// <param name="DraggedPastThreshold">True once the held pointer has moved past <see cref="DragThresholdPxSq"/>.</param>
public readonly record struct ScopeHeadingVectorState(string Callsign, LatLon Cursor, Point DragOrigin, bool ButtonHeld, bool DraggedPastThreshold)
{
    /// <summary>Pixel² movement before a button-held interaction becomes a drag.</summary>
    public const double DragThresholdPxSq = 16;

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
    /// The vector as a left release at <paramref name="cursor"/> leaves it: past the threshold the release confirms, short of it
    /// the release arms the click-confirm.
    /// </summary>
    public ScopeHeadingVectorTransition Released(LatLon cursor)
    {
        ScopeHeadingVectorState moved = this with { Cursor = cursor };
        if (!ButtonHeld)
        {
            return new(moved, ScopeHeadingVectorOutcome.None);
        }

        return DraggedPastThreshold
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
    public static double TurnRadiusNm(double groundSpeedKts, double turnRateDegPerSec)
    {
        if (groundSpeedKts <= 0)
        {
            return 0;
        }

        double turnRateRadPerSec = turnRateDegPerSec * Math.PI / 180.0;
        double gsNmPerSec = groundSpeedKts / 3600.0;
        return gsNmPerSec / turnRateRadPerSec;
    }

    /// <summary>
    /// The point <paramref name="t"/> of the way along the turn arc from the present heading into
    /// <paramref name="snappedHeadingTrue"/>, around the centre on the turn's side; t = 1 is where the arc ends.
    /// </summary>
    public static LatLon ArcPoint(ScopeHeadingVectorOverlay overlay, double snappedHeadingTrue, double t)
    {
        double change = GeoMath.SignedBearingDifference(overlay.HeadingTrue, snappedHeadingTrue);
        double side = change > 0 ? 1 : -1;
        double radiusNm = TurnRadiusNm(overlay.GroundSpeed, overlay.TurnRateDegPerSec);
        LatLon center = GeoMath.ProjectPoint(overlay.Aircraft, new TrueHeading(overlay.HeadingTrue + (90 * side)), radiusNm);
        double startBearing = overlay.HeadingTrue - (90 * side);
        double endBearing = snappedHeadingTrue - (90 * side);
        double bearing = GeoMath.BlendBearings(startBearing, endBearing, t);
        return GeoMath.ProjectPoint(center, new TrueHeading(bearing), radiusNm);
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
        LatLon arcEnd = hasArc ? ArcPoint(overlay, snappedHeadingTrue, 1.0) : overlay.Aircraft;
        double bearingFromArcEnd = GeoMath.BearingTo(arcEnd, overlay.Cursor);
        double cosine = Math.Cos((bearingFromArcEnd - snappedHeadingTrue) * Math.PI / 180.0);
        double alongTrackNm = GeoMath.DistanceNm(arcEnd, overlay.Cursor) * cosine;
        LatLon legEnd = GeoMath.ProjectPoint(arcEnd, new TrueHeading(snappedHeadingTrue), Math.Max(0, alongTrackNm));
        return new(hasArc, arcEnd, legEnd);
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
