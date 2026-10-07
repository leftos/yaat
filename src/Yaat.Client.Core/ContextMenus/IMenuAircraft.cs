using Yaat.Sim;
using Yaat.Sim.Situation;

namespace Yaat.Client.ContextMenus;

/// <summary>
/// The read-only view of an aircraft that the context-menu predicates need, so the catalog and its applicability
/// rules can live outside the desktop client assembly. The client's <c>AircraftModel</c> implements it; nothing
/// here writes to the aircraft or raises change notifications.
/// </summary>
public interface IMenuAircraft
{
    /// <summary>The aircraft's callsign, which a relative item sends as when this is the previous selection.</summary>
    string Callsign { get; }

    /// <summary>True while the aircraft has a taxi route assigned that it has not finished.</summary>
    bool HasActiveTaxiRoute { get; }

    /// <summary>True when the aircraft has a call-for-release window, which a release-window check reports on.</summary>
    bool HasCfrWindow { get; }

    /// <summary>
    /// True when a hold-for-release keeps the departure from departing. Drives the menu header's Release (HFR) item on
    /// every view and the held datablock badge.
    /// </summary>
    bool IsHeldForRelease { get; }

    /// <summary>True for a track mirrored from an external live feed rather than flown by the simulation.</summary>
    bool IsLiveTraffic { get; }

    /// <summary>True when a simulated aircraft was seeded from a live-feed track and can be handed back with <c>UNASSUME</c>.</summary>
    bool AssumedFromLiveTraffic { get; }

    /// <summary>True while the aircraft is on the surface rather than airborne.</summary>
    bool IsOnGround { get; }

    /// <summary>The taxiway the aircraft is on, empty when none.</summary>
    string CurrentTaxiway { get; }

    /// <summary>The parking spot the aircraft is at, empty when none.</summary>
    string ParkingSpot { get; }

    /// <summary>The airport of the ground layout the aircraft is on, null when none.</summary>
    string? GroundAirportId { get; }

    /// <summary>The aircraft's position, which a point menu's Fly heading measures the bearing to the point from.</summary>
    LatLon Position { get; }

    /// <summary>
    /// The callsign of the traffic the aircraft most recently reported in sight (RTIS), or null when it has reported
    /// none. Gates the airborne "follow" relative item to traffic the aircraft can actually see.
    /// </summary>
    string? LastReportedTrafficCallsign { get; }

    /// <summary>True while a hold directive is keeping the aircraft stopped.</summary>
    bool IsHeld { get; }

    /// <summary>The hold in force, <c>HoldPosition</c> or <c>GiveWay</c>, or null when the aircraft is free to move.</summary>
    string? HoldKind { get; }

    /// <summary>The callsign the aircraft yields to under a give-way hold, null for another hold or none.</summary>
    string? HoldYieldTarget { get; }

    /// <summary>The callsign the ground conflict detector has the aircraft yielding to, null when it yields to none.</summary>
    string? AutoYieldTarget { get; }

    /// <summary>True when <see cref="AutoYieldTarget"/> is a same-edge in-trail follow rather than a converging give-way.</summary>
    bool AutoYieldIsFollowing { get; }

    /// <summary>
    /// True for an aircraft the scenario holds back until its spawn delay expires, which every view's menu shows a
    /// spawn-delay menu for.
    /// </summary>
    bool IsDelayed { get; }

    /// <summary>True when a VFR pattern entry is queued but has not become the current phase yet.</summary>
    bool HasQueuedPatternEntry { get; }

    /// <summary>The filed flight rules, <c>IFR</c> or <c>VFR</c>.</summary>
    string FlightRules { get; }

    /// <summary>The name of the phase the aircraft is executing now, empty when it has none.</summary>
    string CurrentPhase { get; }

    /// <summary>The departure or arrival runway the aircraft is assigned, empty when none.</summary>
    string AssignedRunway { get; }

    /// <summary>The remaining phase pipeline, phase names joined with <c> &gt; </c>, empty when none.</summary>
    string PhaseSequence { get; }

    /// <summary>The landing clearance in force, empty when none.</summary>
    string LandingClearance { get; }

    /// <summary>A landing clearance pre-issued against a queued pattern entry, empty when none.</summary>
    string PendingLandingClearance { get; }

    /// <summary>The current true heading in degrees, which the warp popup seeds its heading field with.</summary>
    double HeadingDegrees { get; }

    /// <summary>The current altitude in feet, which the warp popup seeds its altitude field with.</summary>
    double AltitudeFeet { get; }

    /// <summary>The current indicated airspeed in knots, which the warp popup seeds its speed field with.</summary>
    double IndicatedAirspeedKnots { get; }

    /// <summary>The current ground speed in knots, which tells a rejected takeoff still braking from one slowed to taxi speed.</summary>
    double GroundSpeedKnots { get; }

    /// <summary>The fix the aircraft is navigating to, empty when it is not navigating to one.</summary>
    string NavigatingTo { get; }

    /// <summary>The fix names along the aircraft's route, in order, which the header's route summary row starts from.</summary>
    IReadOnlyList<string> NavigationRoute { get; }

    /// <summary>The assigned magnetic heading, or null when none is assigned.</summary>
    MagneticHeading? AssignedHeading { get; }

    /// <summary>The assigned altitude in feet, or null when none is assigned.</summary>
    double? AssignedAltitude { get; }

    /// <summary>The assigned speed in knots, or null when none is assigned.</summary>
    double? AssignedSpeed { get; }

    /// <summary>The aircraft type filed in the flight plan, empty when none was filed.</summary>
    string FiledAircraftType { get; }

    /// <summary>The type a menu header names: the filed type, else the simulated aircraft's own type.</summary>
    string DisplayAircraftType { get; }

    /// <summary>The instructor note on the aircraft, empty when none; the note popup opens prefilled with it.</summary>
    string Note { get; }

    /// <summary>The flight plan's destination airport, empty when none.</summary>
    string Destination { get; }

    /// <summary>The flight plan's departure airport, empty when none.</summary>
    string Departure { get; }

    /// <summary>The flight plan's route text, empty when none.</summary>
    string Route { get; }

    /// <summary>The SID the aircraft is flying, empty when none is active.</summary>
    string ActiveSidId { get; }

    /// <summary>The STAR the aircraft is flying, empty when none is active.</summary>
    string ActiveStarId { get; }

    /// <summary>The approach the aircraft is cleared for or flying, or null when none.</summary>
    string? ActiveApproachId { get; }

    /// <summary>The approach the aircraft has been told to expect, or null when none.</summary>
    string? ExpectedApproach { get; }

    /// <summary>
    /// The server-classified situation (the stored AircraftSituationState.Current), which picks the aircraft's quick-command
    /// list; <see cref="AircraftSituation.Unknown"/> when none could be determined.
    /// </summary>
    AircraftSituation Situation { get; }

    /// <summary>The server-computed situation flags (the stored AircraftSituationState.Flags), sent beside Situation.</summary>
    SituationFlags SituationFlags { get; }

    /// <summary>
    /// The server-computed runway to cross next (the stored AircraftSituationState.NextCrossingRunway), as the end to name
    /// in <c>CROSS</c>; null when the next bar on the taxi route is no runway crossing.
    /// </summary>
    string? NextCrossingRunway { get; }

    /// <summary>The fixes along the aircraft's route that a fix picker offers first, computed on each call.</summary>
    IReadOnlyList<string> RouteFixNames();
}
