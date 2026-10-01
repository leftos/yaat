namespace Yaat.Client.ContextMenus;

/// <summary>
/// The read-only view of an aircraft that the context-menu predicates need, so the catalog and its applicability
/// rules can live outside the desktop client assembly. The client's <c>AircraftModel</c> implements it; nothing
/// here writes to the aircraft or raises change notifications.
/// </summary>
public interface IMenuAircraft
{
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
}
