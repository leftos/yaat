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
/// <param name="ScenarioId">Non-null for all three paths (JoinRoom only
/// reaches this code when <c>RoomStateDto.ScenarioId</c> is non-null).</param>
/// <param name="ScenarioName">Nullable because <c>RoomStateDto</c> allows it
/// to be null when the room has a scenario loaded without a display name.</param>
public sealed record ScenarioBootstrap(
    string ScenarioId,
    string? ScenarioName,
    string? PrimaryAirportId,
    PositionDisplayConfigDto? PositionDisplayConfig,
    FlightStripsConfigDto? FlightStripsConfig,
    IReadOnlyList<AircraftDto> Aircraft
);
