using System.Text.Json.Serialization;

namespace Yaat.Client.Services;

/// <summary>
/// vTDLS-side companion to <c>YaatHubJsonContext</c>. Registers every DTO that
/// crosses the SignalR boundary on behalf of the vTDLS view: the broadcast
/// payloads (<see cref="TdlsItemDto"/>, <see cref="TdlsStateDto"/>,
/// <see cref="TdlsItemRemovedDto"/>), the per-facility config returned by
/// <c>GetTdlsConfigForFacility</c>, the facility-list return type for
/// <c>GetAccessibleTdlsFacilities</c>, and the <see cref="CommandResultDto"/>
/// every TDLS command resolves to.
///
/// Lives in Yaat.Client.Tdls so the WASM client can ship without referencing
/// Yaat.Client.Core. Both the desktop <c>ServerConnection</c> and the
/// browser-side transport insert this context into their <c>JsonHubProtocol</c>
/// resolver chain alongside the broader Core context.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TdlsItemDto))]
[JsonSerializable(typeof(TdlsStateDto))]
[JsonSerializable(typeof(TdlsItemRemovedDto))]
[JsonSerializable(typeof(TdlsDumpedEntryDto))]
[JsonSerializable(typeof(TdlsConfigDto))]
[JsonSerializable(typeof(TdlsSidDto))]
[JsonSerializable(typeof(TdlsSidTransitionDto))]
[JsonSerializable(typeof(TdlsClearanceValueDto))]
[JsonSerializable(typeof(ClearanceDto))]
[JsonSerializable(typeof(AccessibleFacilityDto))]
[JsonSerializable(typeof(List<AccessibleFacilityDto>))]
[JsonSerializable(typeof(CommandResultDto))]
// Browser-side payload subsets — same pattern as Strips. Server still sends
// the broader DTOs (RoomStateDto, TrainingRoomInfoDto, ScenarioLoadedDto); we
// deserialize into these narrower records so the WASM bundle doesn't have to
// ship the full DTO graph.
[JsonSerializable(typeof(BrowserTdlsRoomInfoDto))]
[JsonSerializable(typeof(BrowserTdlsJoinRoomResultDto))]
internal partial class YaatTdlsHubJsonContext : JsonSerializerContext;
