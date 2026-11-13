using Yaat.Client.Services;
using Yaat.Client.Tdls.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// A connected TDLS transport the vTDLS view-model tests drive by hand: they push the server's broadcasts and choose the
/// accessible facilities. Facility pages fetched through it come back empty.
/// </summary>
internal sealed class FakeTdlsTransport : ITdlsTransport
{
    public bool IsConnected => true;

#pragma warning disable CS0067 // required by the interface but never raised by this fake
    public event Action? Connected;
    public event Action<Exception?>? Closed;
    public event Action<Exception?>? Reconnecting;
    public event Action<string?>? Reconnected;
#pragma warning restore CS0067
    public event Action<TdlsItemDto>? TdlsItemChanged;
    public event Action<TdlsItemRemovedDto>? TdlsItemRemoved;
    public event Action<TdlsStateDto>? TdlsStateChanged;

    /// <summary>The facilities <see cref="GetAccessibleTdlsFacilitiesAsync"/> answers with; none by default.</summary>
    public List<AccessibleFacilityDto> AccessibleFacilities { get; set; } = [];

    /// <summary>The full-state broadcast.</summary>
    public void Push(TdlsStateDto state) => TdlsStateChanged?.Invoke(state);

    /// <summary>One item changed.</summary>
    public void PushItem(TdlsItemDto item) => TdlsItemChanged?.Invoke(item);

    /// <summary>One item removed.</summary>
    public void PushRemoved(TdlsItemRemovedDto removed) => TdlsItemRemoved?.Invoke(removed);

    public Task<List<AccessibleFacilityDto>> GetAccessibleTdlsFacilitiesAsync() =>
        Task.FromResult(new List<AccessibleFacilityDto>(AccessibleFacilities));

    public Task<TdlsFacilityViewDto?> GetTdlsFacilityViewAsync(string facilityId) => Task.FromResult<TdlsFacilityViewDto?>(null);

    public Task RequestFullTdlsStateAsync() => Task.CompletedTask;
}
