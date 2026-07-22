namespace Yaat.Client.Services;

/// <summary>
/// Narrow transport contract that the vTDLS view-model depends on. Both the
/// desktop <c>ServerConnection</c> and the browser-side TDLS transport
/// implement this interface so <c>VTdlsViewModel</c> stays free of host-specific
/// code.
///
/// Deliberately narrower than the full server-connection surface: non-TDLS RPCs
/// (room create, scenario load, weather, CRC, etc.) stay on the concrete
/// <c>ServerConnection</c> and don't leak into vTDLS. SendCommand lives outside
/// the interface too — it flows through a <c>Func&lt;string, string, string,
/// Task&gt;</c> delegate the host wires at construction time.
/// </summary>
public interface ITdlsTransport
{
    bool IsConnected { get; }

    event Action? Connected;
    event Action<Exception?>? Closed;
    event Action<Exception?>? Reconnecting;
    event Action<string?>? Reconnected;

    /// <summary>One TDLS item changed (created / sent / wilco'd).</summary>
    event Action<TdlsItemDto>? TdlsItemChanged;

    /// <summary>An item was dumped or TTL-expired and removed from the list.</summary>
    event Action<TdlsItemRemovedDto>? TdlsItemRemoved;

    /// <summary>Full-state replacement — fired on JoinRoom and after RequestFullTdlsState.</summary>
    event Action<TdlsStateDto>? TdlsStateChanged;

    /// <summary>Lists every TDLS-configured facility the room's student position can see.</summary>
    Task<List<AccessibleFacilityDto>> GetAccessibleTdlsFacilitiesAsync();

    /// <summary>Returns the bootstrap config (SIDs / transitions / dropdowns / mandatory flags) for one facility.</summary>
    Task<TdlsConfigDto?> GetTdlsConfigForFacilityAsync(string facilityId);

    /// <summary>Asks the server to push the current full TDLS state via <see cref="TdlsStateChanged"/>. Idempotent.</summary>
    Task RequestFullTdlsStateAsync();
}
