namespace Yaat.Sim.Simulation.Snapshots;

public sealed class AircraftGroundOpsDto
{
    public string? LayoutAirportId { get; init; }
    public TaxiRouteDto? AssignedTaxiRoute { get; init; }
    public string? ParkingSpot { get; init; }
    public string? CurrentTaxiway { get; init; }
    public required bool IsHeld { get; init; }
    public string? GiveWayTarget { get; init; }
    public required bool AutoDeleteExempt { get; init; }
    public bool PendingAutoDelete { get; init; }
    public required double ConflictBreakRemainingSeconds { get; init; }
    public double? SpeedLimit { get; init; }

    /// <summary>Callsign this aircraft is auto-yielding to (drives the "→{target} (auto)" badge); null when not auto-yielding.</summary>
    public string? AutoYieldTarget { get; init; }

    /// <summary>True when the auto-yield is a same-edge in-trail follow ("Following") rather than a converging give-way ("Yielding to").</summary>
    public bool AutoYieldIsFollowing { get; init; }
    public double? PushbackTrueHeadingDeg { get; init; }
    public required bool HasAnnouncedReady { get; init; }
    public bool InitialCallupDecisionProcessed { get; init; }
    public bool IsScriptedDeparture { get; init; }
    public bool IsExpeditingTaxi { get; init; }
    public bool IsExpeditingExit { get; init; }

    /// <summary>Seconds the current GIVEWAY hold has been active (drives the safety-timeout auto-release).</summary>
    public double HoldElapsedSeconds { get; init; }

    /// <summary>Seconds this aircraft has been stopped on the ground (drives the GIVEWAY target-stationary fallback).</summary>
    public double StationarySeconds { get; init; }

    /// <summary>True when this IFR departure is held for release (held short of the runway until released).</summary>
    public bool HeldForRelease { get; init; }

    /// <summary>True when a held ground departure has been released and is awaiting its auto-issued takeoff clearance.</summary>
    public bool ReleasedForDeparture { get; init; }

    /// <summary>Scenario-elapsed seconds at which the departure was released (drives the auto-CTO jitter).</summary>
    public double ReleasedAtSeconds { get; init; }
}
