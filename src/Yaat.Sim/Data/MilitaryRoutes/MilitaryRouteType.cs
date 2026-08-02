namespace Yaat.Sim.Data.MilitaryRoutes;

/// <summary>
/// The three route systems published in DoD AP/1B, plus aerial refueling tracks.
///
/// IR and VR are the Military Training Route (MTR) system proper. SR routes are slow-speed
/// low-altitude routes which AP/1B chapter 4 states explicitly are <em>not</em> part of the MTR
/// system and carry no AIM or FAA Order JO 7610.4 guidance — they are published alongside IR and
/// VR only because pilots must deconflict against them.
/// </summary>
public enum MilitaryRouteType
{
    /// <summary>IFR military training route. Requires an ATC clearance to enter and to exit.</summary>
    Ir,

    /// <summary>VFR military training route. Flown only in VMC (5 SM visibility, 3000 ft AGL ceiling).</summary>
    Vr,

    /// <summary>Slow speed low altitude training route: 250 KIAS or less, at or below 1500 ft AGL.</summary>
    Sr,

    /// <summary>Aerial refueling track or anchor from AP/1B chapter 5.</summary>
    Ar,
}

/// <summary>Whether a published altitude is measured above ground level or above mean sea level.</summary>
public enum AltitudeReference
{
    Msl,
    Agl,
}

/// <summary>What a route point's published Altitude Data cell expresses.</summary>
public enum MilitaryRouteAltitudeKind
{
    /// <summary>No altitude published on this point's row.</summary>
    None,

    /// <summary>A single altitude, e.g. "60 MSL".</summary>
    Single,

    /// <summary>A floor-to-ceiling block, e.g. "05 AGL B 60 MSL". The dominant form in AP/1B.</summary>
    Block,

    /// <summary>A ceiling only, e.g. "at or below 70 MSL".</summary>
    AtOrBelow,

    /// <summary>Altitude is whatever ATC assigns; common on entry points.</summary>
    AsAssigned,

    /// <summary>Published text the build tool could not interpret; the verbatim text is preserved.</summary>
    Unparsed,
}

/// <summary>
/// A route point's part in the route. AP/1B prints alternate entry and exit points inline among the
/// mainline points, distinguished by a digit-suffixed label (D1, P2, AE3) and an annotation in the
/// Altitude Data column.
/// </summary>
public enum MilitaryRoutePointRole
{
    Point,
    Entry,
    Exit,
    AlternateEntry,
    AlternateExit,

    /// <summary>Air Refueling Initial Point: where the receiver joins the tanker's track.</summary>
    Arip,

    /// <summary>Air Refueling Control Point: where the refueling itself begins.</summary>
    Arcp,

    /// <summary>A published navigation check point between the ARCP and the exit.</summary>
    CheckPoint,

    /// <summary>An anchor's holding fix, about which the refueling orbit is flown.</summary>
    AnchorPoint,

    /// <summary>One corner of an anchor's published orbit pattern.</summary>
    PatternCorner,
}

/// <summary>
/// Which AP/1B chapter 5 table an aerial refueling entry came from. Tracks are flown as a line from
/// the ARIP to the exit; anchors are orbited about an anchor point until the refueling is complete.
/// </summary>
public enum MilitaryRouteArKind
{
    /// <summary>Not an aerial refueling route (an IR, VR, or SR).</summary>
    None,

    /// <summary>A refueling track (AP/1B chapter 5, "REFUELING TRACKS").</summary>
    Track,

    /// <summary>A refueling anchor (AP/1B chapter 5, designators from AR600).</summary>
    Anchor,
}
