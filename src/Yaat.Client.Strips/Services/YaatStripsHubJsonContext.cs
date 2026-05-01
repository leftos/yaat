using System.Text.Json.Serialization;

namespace Yaat.Client.Services;

/// <summary>
/// Strips-side companion to <c>YaatHubJsonContext</c>. Registers every DTO that
/// crosses the SignalR boundary on behalf of the flight-strip view: the
/// broadcast payloads (<see cref="FlightStripsStateDto"/>,
/// <see cref="StripItemDto"/>), the facility config returned by
/// <c>GetFlightStripsConfigForFacility</c>, the facility-list return type for
/// <c>GetAccessibleFacilities</c>, and the <see cref="CommandResultDto"/> every
/// strip command resolves to.
///
/// Lives in Yaat.Client.Strips so the WASM client can ship without referencing
/// Yaat.Client.Core. Both the desktop <c>ServerConnection</c> and the
/// browser-side transport insert this context into their
/// <c>JsonHubProtocol</c> resolver chain alongside the broader Core context;
/// <see cref="System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver"/>
/// chains are consulted per type, so each context only needs to know its own
/// payloads.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(FlightStripsStateDto))]
[JsonSerializable(typeof(List<StripItemDto>))]
[JsonSerializable(typeof(StripItemDto))]
[JsonSerializable(typeof(StripBayConfigDto))]
[JsonSerializable(typeof(StripBayContentsDto))]
[JsonSerializable(typeof(FlightStripsConfigDto))]
[JsonSerializable(typeof(AccessibleFacilityDto))]
[JsonSerializable(typeof(List<AccessibleFacilityDto>))]
[JsonSerializable(typeof(CommandResultDto))]
// Browser-side method/event payload subsets. Server still sends the
// broader DTOs (RoomStateDto, TrainingRoomInfoDto, ScenarioLoadedDto);
// we deserialize into these narrower records so the WASM bundle
// doesn't have to ship the full DTO graph.
[JsonSerializable(typeof(BrowserRoomInfoDto))]
[JsonSerializable(typeof(BrowserJoinRoomResultDto))]
[JsonSerializable(typeof(BrowserScenarioLoadedDto))]
internal partial class YaatStripsHubJsonContext : JsonSerializerContext;
