using Yaat.Client.Services;

namespace Yaat.Client.ViewModels;

/// <summary>
/// Projection of the fields each sub-VM needs when a scenario becomes active.
/// The three paths that trigger scenario activation — the RPC return of
/// LoadScenario (loader), the ScenarioLoaded broadcast (other clients), and
/// the RoomState snapshot during JoinRoom — each carry a superset of these
/// fields under different names. Projecting into a common record lets
/// <see cref="MainViewModel.ApplyScenarioBootstrap"/> fan out to the sub-VMs
/// without caring which DTO the data originated from.
///
/// Fields that don't appear on all three DTOs (e.g. StudentPositionType is
/// missing from RoomStateDto; ApplySimState has different signatures per
/// path) stay at the call site as per-path extras.
/// </summary>
public sealed record ScenarioBootstrap
{
    /// <summary>Non-null for all three paths (JoinRoom only
    /// reaches this code when <c>RoomStateDto.ScenarioId</c> is non-null).</summary>
    public required string ScenarioId { get; init; }

    /// <summary>Nullable because <c>RoomStateDto</c> allows it
    /// to be null when the room has a scenario loaded without a display name.</summary>
    public required string? ScenarioName { get; init; }

    public required string? PrimaryAirportId { get; init; }

    public required PositionDisplayConfigDto? PositionDisplayConfig { get; init; }

    public required FlightStripsConfigDto? FlightStripsConfig { get; init; }

    public required IReadOnlyList<AircraftDto> Aircraft { get; init; }

    /// <summary>How long the scenario has already been running
    /// when this client picks it up. Zero on the loader and broadcast paths, which
    /// both fire as the scenario starts; the join path carries the room's clock, so
    /// a joiner's elapsed-time displays match the room rather than restarting.</summary>
    public required double ElapsedSeconds { get; init; }
}
