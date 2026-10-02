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

    /// <summary>True while a hold directive is keeping the aircraft stopped.</summary>
    bool IsHeld { get; }

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

    /// <summary>The fix the aircraft is navigating to, empty when it is not navigating to one.</summary>
    string NavigatingTo { get; }

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

    /// <summary>The approach the aircraft is cleared for or flying, or null when none.</summary>
    string? ActiveApproachId { get; }

    /// <summary>The approach the aircraft has been told to expect, or null when none.</summary>
    string? ExpectedApproach { get; }

    /// <summary>The server-computed situation flags (the stored AircraftSituationState.Flags), sent beside Situation.</summary>
    SituationFlags SituationFlags { get; }

    /// <summary>The fixes along the aircraft's route that a fix picker offers first, computed on each call.</summary>
    IReadOnlyList<string> RouteFixNames();
}
