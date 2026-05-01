namespace Yaat.Client.Services;

/// <summary>
/// Narrow transport contract that the strip view-model depends on. Both the
/// desktop <c>ServerConnection</c> and the browser-side strip transport
/// implement this interface so <c>VStripsViewModel</c> stays free of
/// host-specific code.
///
/// Deliberately narrower than the full server-connection surface:
/// non-strip RPCs (room create, scenario load, weather, CRC, etc.) stay on
/// the concrete <c>ServerConnection</c> and don't leak into Strips.
/// Send-command lives outside the interface too — it flows through a
/// <c>Func&lt;string, string, string, Task&gt;</c> delegate the host wires
/// at construction time, keeping the interface surface focused on what the
/// strip view actually invokes.
///
/// <see cref="StripsConfigChanged"/> replaces ServerConnection's
/// <c>ScenarioLoaded</c> + <c>ScenarioUnloaded</c> for strip purposes:
/// fires the new <see cref="FlightStripsConfigDto"/> on load, fires
/// <c>null</c> on unload. Avoids leaking the full <c>ScenarioLoadedDto</c>
/// (which embeds <c>AircraftDto</c>) into the WASM-clean Strips assembly.
/// </summary>
public interface IStripsTransport
{
    bool IsConnected { get; }

    event Action? Connected;
    event Action<Exception?>? Closed;
    event Action<Exception?>? Reconnecting;
    event Action<string?>? Reconnected;

    event Action<FlightStripsConfigDto?>? StripsConfigChanged;
    event Action<FlightStripsStateDto>? FlightStripsStateChanged;
    event Action<List<StripItemDto>>? StripItemsChanged;

    Task<List<AccessibleFacilityDto>> GetAccessibleFacilitiesAsync();
    Task<FlightStripsConfigDto?> GetFlightStripsConfigForFacilityAsync(string facilityId);
    Task<CommandResultDto> RequestFlightStripForAircraftAsync(string callsign);
}
